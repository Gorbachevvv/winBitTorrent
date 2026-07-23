using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Services;
using WinBitTorrent.ViewModels;
using WinUI.TableView;

namespace WinBitTorrent.Views;

public sealed partial class TransfersView : UserControl
{
    private static readonly int[] FilePriorityValues = [0, 1, 6, 7];
    private readonly ObservableCollection<TorrentFileTreeNode> _fileTreeRoots = [];
    private readonly DispatcherTimer _filesRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _fileEventsAttached;
    private bool _fileTreeRefreshQueued;
    private bool _isRefreshingFiles;

    // Rubber-band (marquee) selection state.
    private const double RubberBandThreshold = 4;
    private bool _rubberBanding;
    private bool _rubberMoved;
    private bool _rubberAdditive;
    private Point _rubberOrigin;
    private readonly HashSet<object> _rubberBaseSelection = [];

    public TransfersView()
    {
        InitializeComponent();
        RestoreLayout();
        DataContext = App.Services.GetRequiredService<MainViewModel>();
        ContentFilesTree.ItemsSource = _fileTreeRoots;
        _filesRefreshTimer.Tick += FilesRefreshTimer_Tick;
        // TableViewRow marks pointer events as handled, so subscribe with handledEventsToo to
        // still observe presses that start on empty space (where the marquee begins).
        TorrentTable.AddHandler(PointerPressedEvent, new PointerEventHandler(TorrentTable_PointerPressed), true);
        TorrentTable.AddHandler(PointerMovedEvent, new PointerEventHandler(TorrentTable_PointerMoved), true);
        TorrentTable.AddHandler(PointerReleasedEvent, new PointerEventHandler(TorrentTable_PointerReleased), true);
        TorrentTable.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(TorrentTable_PointerCaptureLost), true);
        Loaded += TransfersView_Loaded;
        Unloaded += TransfersView_Unloaded;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void FilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView { SelectedItem: FilterItemViewModel filter })
            ViewModel.SelectFilter(filter);
    }

    private void TorrentTable_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var rows = TorrentTable.SelectedItems.OfType<TorrentRowViewModel>().ToList();
        ViewModel.SetSelectedRows(rows);
    }

    /// <summary>
    /// Matches the desktop convention (and qBittorrent): right-clicking a row that is not part of
    /// the current selection moves the selection to that single row before the menu opens, unless
    /// Shift/Ctrl is held (which keeps the multi-selection so the action applies to all of them).
    /// </summary>
    private void TorrentTable_RowContextFlyoutOpening(object sender, TableViewRowContextFlyoutEventArgs args)
    {
        if (args.Item is not TorrentRowViewModel row)
            return;
        if (IsKeyDown(VirtualKey.Shift) || IsKeyDown(VirtualKey.Control) || TorrentTable.SelectedItems.Contains(row))
            return;
        TorrentTable.SelectedItems.Clear();
        TorrentTable.SelectedItems.Add(row);
    }

    private void TorrentTable_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TorrentTable);
        // Only the left button starts a marquee, and only when the press lands on empty space -
        // a press on a row or a column header keeps the table's native click behaviour.
        if (point.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse
            || !point.Properties.IsLeftButtonPressed
            || IsOverRowOrHeader(e.OriginalSource))
            return;

        _rubberBanding = true;
        _rubberMoved = false;
        _rubberAdditive = IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Shift);
        _rubberOrigin = e.GetCurrentPoint(SelectionOverlay).Position;
        _rubberBaseSelection.Clear();
        if (_rubberAdditive)
            foreach (var item in TorrentTable.SelectedItems)
                _rubberBaseSelection.Add(item);
        TorrentTable.CapturePointer(e.Pointer);
    }

    private void TorrentTable_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_rubberBanding)
            return;
        var current = e.GetCurrentPoint(SelectionOverlay).Position;
        if (!_rubberMoved)
        {
            if (Math.Abs(current.X - _rubberOrigin.X) < RubberBandThreshold
                && Math.Abs(current.Y - _rubberOrigin.Y) < RubberBandThreshold)
                return;
            _rubberMoved = true;
            SelectionRectangle.Visibility = Visibility.Visible;
        }

        var marquee = new Rect(
            new Point(Math.Min(_rubberOrigin.X, current.X), Math.Min(_rubberOrigin.Y, current.Y)),
            new Point(Math.Max(_rubberOrigin.X, current.X), Math.Max(_rubberOrigin.Y, current.Y)));
        Canvas.SetLeft(SelectionRectangle, marquee.X);
        Canvas.SetTop(SelectionRectangle, marquee.Y);
        SelectionRectangle.Width = marquee.Width;
        SelectionRectangle.Height = marquee.Height;
        UpdateRubberSelection(marquee);
    }

    private void TorrentTable_PointerReleased(object sender, PointerRoutedEventArgs e)
        => EndRubberBand(e.Pointer);

    private void TorrentTable_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => EndRubberBand(null);

    private void EndRubberBand(Pointer? pointer)
    {
        if (!_rubberBanding)
            return;
        _rubberBanding = false;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        if (pointer is not null)
            TorrentTable.ReleasePointerCapture(pointer);
        // A press-and-release on empty space with no drag clears the selection, like clicking the
        // empty area of a file list. A modifier press leaves the selection untouched.
        if (!_rubberMoved && !_rubberAdditive)
            TorrentTable.SelectedItems.Clear();
        _rubberBaseSelection.Clear();
    }

    private void UpdateRubberSelection(Rect marquee)
    {
        var target = new HashSet<object>(_rubberBaseSelection);
        foreach (var row in EnumerateRows(TorrentTable))
        {
            if (TorrentTable.ItemFromContainer(row) is not { } item)
                continue;
            var bounds = row.TransformToVisual(SelectionOverlay)
                .TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
            if (IntersectsVertically(marquee, bounds))
                target.Add(item);
        }

        var selected = TorrentTable.SelectedItems;
        for (var index = selected.Count - 1; index >= 0; index--)
            if (!target.Contains(selected[index]))
                selected.RemoveAt(index);
        foreach (var item in target)
            if (!selected.Contains(item))
                selected.Add(item);
    }

    // Rows span the full table width, so a vertical overlap is enough to consider a row selected -
    // this keeps the marquee forgiving horizontally, matching how list marquees usually feel.
    private static bool IntersectsVertically(Rect marquee, Rect row)
        => marquee.Top < row.Bottom && marquee.Bottom > row.Top;

    private static bool IsOverRowOrHeader(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is TableViewRow or TableViewHeaderRow or TableViewColumnHeader)
                return true;
        return false;
    }

    private static IEnumerable<TableViewRow> EnumerateRows(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is TableViewRow row)
            {
                yield return row;
                continue;
            }
            foreach (var descendant in EnumerateRows(child))
                yield return descendant;
        }
    }

    private static bool IsKeyDown(VirtualKey key)
        => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void DetailsSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var details = ContentGrid.RowDefinitions[2];
        details.Height = new GridLength(Math.Max(details.MinHeight, details.ActualHeight - e.VerticalChange));
        SaveLayout();
    }

    private void SidebarSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var sidebar = ((Grid)Content).ColumnDefinitions[0];
        sidebar.Width = new GridLength(Math.Clamp(sidebar.ActualWidth + e.HorizontalChange, 150, 420));
        SaveLayout();
    }

    private async void OpenDestination_Click(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.SelectedTorrent;
        if (selected is null)
            return;

        if (ViewModel.SelectedProfile?.Kind != ProfileKind.LocalManaged)
        {
            await ShowInfoAsync(Localizer.Get("Dialog_LocalFilesUnavailableTitle", "Local files are unavailable"), Localizer.Get("Dialog_LocalFilesUnavailableMessage", "This torrent belongs to a remote profile. WinBitTorrent can only open folders for the managed local backend."));
            return;
        }

        var target = ResolveDestinationTarget(selected);
        if (target is not null)
        {
            SelectInExplorer(target);
            return;
        }

        await ShowInfoAsync(
            Localizer.Get("Dialog_DestinationUnavailableTitle", "Destination folder is not available"),
            $"{Localizer.Get("Dialog_ReportedSavePath", "qBittorrent reported save path")}: {selected.Model.SavePath}\n{Localizer.Get("Dialog_DownloadPath", "Download path")}: {selected.Model.DownloadPath}\n\n{Localizer.Get("Dialog_PathUnavailableReason", "The path may not exist yet, or the torrent metadata has not finished syncing.")}");
    }

    /// <summary>
    /// Resolves the single best path to reveal in Explorer for the whole torrent: its
    /// content path (the exact file for single-file torrents, or the exact folder qBittorrent
    /// is using for multi-file torrents - which may be a subfolder of the save path, or may
    /// not exist as a subfolder at all). Falling back to the bare save/download path only
    /// selects the shared downloads root, not the torrent's own folder, so it is a last resort.
    /// </summary>
    private static string? ResolveDestinationTarget(TorrentRowViewModel torrent)
    {
        var contentPath = torrent.Model.ContentPath;
        if (!string.IsNullOrWhiteSpace(contentPath) && (Directory.Exists(contentPath) || File.Exists(contentPath)))
            return contentPath;

        var folderPath = ResolveLocalDirectoryPath(torrent);
        return folderPath is not null && Directory.Exists(folderPath) ? folderPath : null;
    }

    private static void SelectInExplorer(string path)
    {
        // explorer.exe's "/select," switch is parsed as a single command-line token: the path
        // must be quoted *inside* that token (/select,"C:\..."). Passing it via ArgumentList
        // makes .NET quote the whole "/select,C:\..." argument whenever the path contains a
        // space, which explorer cannot parse - it silently falls back to opening the Documents
        // folder. Building the argument string by hand keeps the switch and the quoted path
        // in the shape explorer expects.
        var target = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
    }

    private void TransfersView_Loaded(object sender, RoutedEventArgs e)
    {
        StatusList.SelectedIndex = 0;
        if (!_fileEventsAttached)
        {
            ViewModel.SelectedFiles.CollectionChanged += SelectedFiles_CollectionChanged;
            _fileEventsAttached = true;
        }
        _filesRefreshTimer.Start();
        QueueFileTreeRefresh();
    }

    private void TransfersView_Unloaded(object sender, RoutedEventArgs e)
    {
        SaveLayout();
        _filesRefreshTimer.Stop();
        if (!_fileEventsAttached)
            return;
        ViewModel.SelectedFiles.CollectionChanged -= SelectedFiles_CollectionChanged;
        _fileEventsAttached = false;
    }

    private void SelectedFiles_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => QueueFileTreeRefresh();

    private void QueueFileTreeRefresh()
    {
        if (_fileTreeRefreshQueued)
            return;
        _fileTreeRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _fileTreeRefreshQueued = false;
            RebuildFileTree();
        }))
            _fileTreeRefreshQueued = false;
    }

    private void RebuildFileTree()
    {
        _fileTreeRoots.Clear();
        var filter = ContentFilesFilterBox.Text?.Trim();
        foreach (var root in TorrentFileTreeBuilder.Build(
            ViewModel.SelectedTorrent?.Name,
            ViewModel.SelectedFiles,
            filter))
            _fileTreeRoots.Add(root);
    }

    private void RestoreLayout()
    {
        if (ClientSettings.GetValue("layout.sidebarWidth") is double sidebarWidth)
            ((Grid)Content).ColumnDefinitions[0].Width = new GridLength(Math.Clamp(sidebarWidth, 150, 420));
        if (ClientSettings.GetValue("layout.detailsHeight") is double detailsHeight)
            ContentGrid.RowDefinitions[2].Height = new GridLength(Math.Max(ContentGrid.RowDefinitions[2].MinHeight, detailsHeight));

        if (ClientSettings.GetValue("layout.torrentColumns") is string json)
        {
            try
            {
                var states = JsonSerializer.Deserialize<List<ColumnState>>(json) ?? [];
                for (var stateIndex = 0; stateIndex < states.Count; stateIndex++)
                {
                    var state = states[stateIndex];
                    TableViewColumn? column = null;
                    if (int.TryParse(state.Id, out var columnIndex) && columnIndex >= 0 && columnIndex < TorrentTable.Columns.Count)
                        column = TorrentTable.Columns[columnIndex];
                    column ??= TorrentTable.Columns.FirstOrDefault(candidate => string.Equals(candidate.Header?.ToString(), state.Header, StringComparison.Ordinal));
                    // Older versions persisted localized header text only. The serialized list
                    // follows the declaration order, so its position is a safe migration key.
                    if (column is null && stateIndex < TorrentTable.Columns.Count)
                        column = TorrentTable.Columns[stateIndex];
                    if (column is null)
                        continue;
                    if (double.IsFinite(state.Width) && state.Width > 0)
                        column.Width = new GridLength(Math.Max(column.MinWidth ?? 0d, state.Width));
                    column.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
                    column.Order = state.Order;
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    private void SaveLayout()
    {
        if (!IsLoaded)
            return;
        ClientSettings.SetValue("layout.sidebarWidth", ((Grid)Content).ColumnDefinitions[0].ActualWidth);
        ClientSettings.SetValue("layout.detailsHeight", ContentGrid.RowDefinitions[2].ActualHeight);
        var columns = TorrentTable.Columns.Select((column, index) => new ColumnState(
            index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            column.Header?.ToString() ?? string.Empty,
            double.IsFinite(column.ActualWidth) && column.ActualWidth > 0 ? column.ActualWidth : column.Width.Value,
            column.Visibility == Visibility.Visible,
            column.Order ?? TorrentTable.Columns.IndexOf(column))).ToList();
        ClientSettings.SetValue("layout.torrentColumns", JsonSerializer.Serialize(columns));
    }

    private void TorrentTable_ColumnReordered(object sender, TableViewColumnReorderedEventArgs e) => SaveLayout();

    private async void ForceStart_Click(object sender, RoutedEventArgs e)
    {
        var enabled = ViewModel.SelectedTorrent?.Model.ForceStart != true;
        await ExecuteMenuActionAsync(() => ViewModel.SetForceStartSelectedAsync(enabled));
    }

    private async void Sequential_Click(object sender, RoutedEventArgs e)
        => await ExecuteMenuActionAsync(ViewModel.ToggleSequentialSelectedAsync);

    private async void FirstLast_Click(object sender, RoutedEventArgs e)
        => await ExecuteMenuActionAsync(ViewModel.ToggleFirstLastSelectedAsync);

    private async void SuperSeeding_Click(object sender, RoutedEventArgs e)
    {
        // The backend does not report super-seeding state, so the icon is always shown unchecked;
        // treat a click as a request to enable it.
        var enabled = SuperSeedingCheckIcon.Glyph == UncheckedCheckGlyph;
        await ExecuteMenuActionAsync(() => ViewModel.SetSuperSeedingSelectedAsync(enabled));
    }

    private const string UncheckedCheckGlyph = "";
    private const string CheckedCheckGlyph = "";

    private static SolidColorBrush? _checkedBrush;
    private static SolidColorBrush? _uncheckedBrush;

    // Renders a menu item's leading icon as an empty or ticked checkbox in the shared icon column.
    // Uses fixed colours (blue when checked, slate when not), like the other menu glyph icons -
    // theme brushes fetched by key from Application.Resources don't resolve per-theme and showed
    // up white in the light theme.
    private static void SetCheckIcon(FontIcon icon, bool isChecked)
    {
        _checkedBrush ??= new SolidColorBrush(Color.FromArgb(0xFF, 0x25, 0x63, 0xEB));
        _uncheckedBrush ??= new SolidColorBrush(Color.FromArgb(0xFF, 0x94, 0xA3, 0xB8));
        icon.Glyph = isChecked ? CheckedCheckGlyph : UncheckedCheckGlyph;
        icon.Foreground = isChecked ? _checkedBrush : _uncheckedBrush;
    }

    // The context menu lives in the TableView's RowContextFlyout, whose DataContext is the row's
    // TorrentRowViewModel rather than the MainViewModel - so Command bindings to the MainViewModel
    // commands silently fail to resolve. These commands are invoked through code-behind handlers
    // (like the rest of the menu) so they always reach the MainViewModel.
    private async void Start_Click(object sender, RoutedEventArgs e)
        => await ExecuteMenuActionAsync(ViewModel.StartSelectedAsync);

    private async void Stop_Click(object sender, RoutedEventArgs e)
        => await ExecuteMenuActionAsync(ViewModel.StopSelectedAsync);

    private async void ForceRecheck_Click(object sender, RoutedEventArgs e)
        => await ExecuteMenuActionAsync(ViewModel.RecheckSelectedAsync);

    private async void Reannounce_Click(object sender, RoutedEventArgs e)
        => await ExecuteMenuActionAsync(ViewModel.ReannounceSelectedAsync);

    private async void MoveUp_Click(object sender, RoutedEventArgs e)
        => await ExecuteMenuActionAsync(ViewModel.MoveUpSelectedAsync);

    private async void MoveDown_Click(object sender, RoutedEventArgs e)
        => await ExecuteMenuActionAsync(ViewModel.MoveDownSelectedAsync);

    private async void QueueTop_Click(object sender, RoutedEventArgs e)
        => await ExecuteMenuActionAsync(ViewModel.MoveTopSelectedAsync);

    private async void QueueBottom_Click(object sender, RoutedEventArgs e)
        => await ExecuteMenuActionAsync(ViewModel.MoveBottomSelectedAsync);

    private void TorrentContextMenu_Opening(object sender, object e)
    {
        var selection = ViewModel.SelectedTorrents;
        var model = ViewModel.SelectedTorrent?.Model;

        // Mirror qBittorrent: only offer the action that actually applies to the selection.
        // Start is hidden once everything is running, Stop once everything is stopped; a mixed
        // selection keeps both.
        var anyStopped = selection.Any(static row => row.IsStopped);
        var anyRunning = selection.Any(static row => !row.IsStopped);
        StartMenuItem.Visibility = anyStopped ? Visibility.Visible : Visibility.Collapsed;
        StopMenuItem.Visibility = anyRunning ? Visibility.Visible : Visibility.Collapsed;

        // Reflect the per-torrent flags on the checkbox items up front. These use a checkbox
        // glyph in the shared icon column (rather than a ToggleMenuFlyoutItem, whose checkmark
        // sits in a separate column and pushes everything right), so the click handler reads the
        // current flag from the model and flips it. Super seeding state is not reported by the
        // backend, so it always shows unchecked.
        SetCheckIcon(ForceStartCheckIcon, model?.ForceStart == true);
        SetCheckIcon(SequentialCheckIcon, model?.SequentialDownload == true);
        SetCheckIcon(FirstLastCheckIcon, model?.FirstLastPiecePriority == true);
        SetCheckIcon(SuperSeedingCheckIcon, false);

        // File-system actions only make sense for the local managed backend; hide (not just
        // disable) them for remote profiles, the way qBittorrent omits them over WebUI.
        var localVisibility = ViewModel.CanUseLocalFiles ? Visibility.Visible : Visibility.Collapsed;
        PreviewMenuItem.Visibility = localVisibility;
        OpenDestinationMenuItem.Visibility = localVisibility;

        CategorySubmenu.Items.Clear();
        var currentCategory = ViewModel.SelectedTorrent?.Category ?? string.Empty;

        var noneItem = new ToggleMenuFlyoutItem
        {
            Text = Localizer.Get("Category_Uncategorized", "Uncategorized"),
            IsChecked = string.IsNullOrEmpty(currentCategory)
        };
        noneItem.Click += async (_, _) => await ExecuteMenuActionAsync(() => ViewModel.SetCategorySelectedAsync(string.Empty));
        CategorySubmenu.Items.Add(noneItem);

        var categories = ViewModel.Categories.Keys
            .OrderBy(static name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (categories.Count > 0)
            CategorySubmenu.Items.Add(new MenuFlyoutSeparator());

        foreach (var category in categories)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = category,
                IsChecked = string.Equals(category, currentCategory, StringComparison.Ordinal)
            };
            item.Click += async (_, _) => await ExecuteMenuActionAsync(() => ViewModel.SetCategorySelectedAsync(category));
            CategorySubmenu.Items.Add(item);
        }

        CategorySubmenu.Items.Add(new MenuFlyoutSeparator());
        var newItem = new MenuFlyoutItem { Text = Localizer.Get("Category_New", "New category…") };
        newItem.Click += async (_, _) => await ExecuteMenuActionAsync(CreateAndAssignCategoryAsync);
        CategorySubmenu.Items.Add(newItem);
    }

    private async Task CreateAndAssignCategoryAsync()
    {
        if (ViewModel.Api is null)
            return;
        var name = new TextBox { Header = Localizer.Get("Dialog_CategoryName", "Category name") };
        var path = new TextBox { Header = Localizer.Get("Dialog_DefaultSavePath", "Default save path") };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(name);
        panel.Children.Add(path);
        if (await ShowFormAsync(Localizer.Get("Dialog_NewCategory", "New category"), panel) != ContentDialogResult.Primary
            || string.IsNullOrWhiteSpace(name.Text))
            return;

        var categoryName = name.Text.Trim();
        await ViewModel.Api.Torrents.PostAsync("createCategory", new Dictionary<string, string?> { ["category"] = categoryName, ["savePath"] = path.Text.Trim() });
        await ViewModel.SetCategorySelectedAsync(categoryName);
    }

    /// <summary>
    /// Runs a menu action and shows a friendly error instead of letting an API rejection
    /// (e.g. an invalid category or tag name) or any other failure crash the app.
    /// </summary>
    private async Task ExecuteMenuActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            await ShowInfoAsync(Localizer.Get("Dialog_ActionFailedTitle", "Action failed"), exception.Message);
        }
    }

    private async void CreateCategory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Api is null)
            return;
        var name = new TextBox { Header = Localizer.Get("Dialog_CategoryName", "Category name") };
        var path = new TextBox { Header = Localizer.Get("Dialog_DefaultSavePath", "Default save path") };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(name); panel.Children.Add(path);
        if (await ShowFormAsync(Localizer.Get("Dialog_NewCategory", "New category"), panel) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text))
            return;
        await ExecuteMenuActionAsync(() => ViewModel.Api.Torrents.PostAsync("createCategory", new Dictionary<string, string?> { ["category"] = name.Text.Trim(), ["savePath"] = path.Text.Trim() }));
    }

    private async void EditCategory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Api is null || CategoriesList.SelectedItem is not FilterItemViewModel category || category.Key == "Uncategorized")
            return;
        var path = await PromptAsync(Localizer.Get("Dialog_EditCategory", "Edit category"), Localizer.Get("Dialog_DefaultSavePath", "Default save path"));
        if (path is null)
            return;
        await ExecuteMenuActionAsync(() => ViewModel.Api.Torrents.PostAsync("editCategory", new Dictionary<string, string?> { ["category"] = category.Key, ["savePath"] = path.Trim() }));
    }

    private async void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Api is null || CategoriesList.SelectedItem is not FilterItemViewModel category || category.Key == "Uncategorized")
            return;
        if (!await ConfirmAsync(Localizer.Get("Dialog_DeleteCategory", "Delete category?"), string.Format(Localizer.Get("Dialog_DeleteCategoryMessage", "The torrents in “{0}” will become uncategorized."), category.Title)))
            return;
        await ExecuteMenuActionAsync(() => ViewModel.Api.Torrents.PostAsync("removeCategories", new Dictionary<string, string?> { ["categories"] = category.Key }));
    }

    private async void CreateTags_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Api is null)
            return;
        var tags = await PromptAsync(Localizer.Get("Dialog_CreateTags", "Create tags"), Localizer.Get("Dialog_CommaSeparatedTags", "Comma-separated tag names"));
        if (string.IsNullOrWhiteSpace(tags))
            return;
        await ExecuteMenuActionAsync(() => ViewModel.Api.Torrents.PostAsync("createTags", new Dictionary<string, string?> { ["tags"] = tags }));
    }

    private async void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Api is null || TagsList.SelectedItem is not FilterItemViewModel tag)
            return;
        if (!await ConfirmAsync(Localizer.Get("Dialog_DeleteTag", "Delete tag?"), string.Format(Localizer.Get("Dialog_DeleteTagMessage", "Remove “{0}” from all torrents?"), tag.Title)))
            return;
        await ExecuteMenuActionAsync(() => ViewModel.Api.Torrents.PostAsync("deleteTags", new Dictionary<string, string?> { ["tags"] = tag.Key }));
    }

    private async void Tags_Click(object sender, RoutedEventArgs e)
    {
        var value = await PromptAsync(Localizer.Get("Dialog_TorrentTags", "Torrent tags"), Localizer.Get("Dialog_CommaSeparatedTags", "Comma-separated tags"));
        if (string.IsNullOrWhiteSpace(value))
            return;
        var isRemove = sender is MenuFlyoutItem { Tag: "remove" };
        await ExecuteMenuActionAsync(() => isRemove ? ViewModel.RemoveTagsSelectedAsync(value) : ViewModel.AddTagsSelectedAsync(value));
    }

    private async void Limits_Click(object sender, RoutedEventArgs e)
    {
        var download = new NumberBox { Header = Localizer.Get("Dialog_DownloadLimit", "Download limit (KiB/s, 0 = unlimited)"), Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var upload = new NumberBox { Header = Localizer.Get("Dialog_UploadLimit", "Upload limit (KiB/s, 0 = unlimited)"), Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(download); panel.Children.Add(upload);
        if (await ShowFormAsync(Localizer.Get("Dialog_TorrentRateLimits", "Torrent rate limits"), panel) != ContentDialogResult.Primary)
            return;
        await ExecuteMenuActionAsync(async () =>
        {
            await ViewModel.PostSelectedAsync("setDownloadLimit", new Dictionary<string, string?> { ["limit"] = ((long)Math.Max(0, download.Value) * 1024).ToString() });
            await ViewModel.PostSelectedAsync("setUploadLimit", new Dictionary<string, string?> { ["limit"] = ((long)Math.Max(0, upload.Value) * 1024).ToString() });
        });
    }

    private async void ShareLimits_Click(object sender, RoutedEventArgs e)
    {
        var ratio = new NumberBox { Header = Localizer.Get("Dialog_RatioLimit", "Ratio limit (-1 = global)"), Value = -1, Minimum = -2, SmallChange = .1 };
        var time = new NumberBox { Header = Localizer.Get("Dialog_SeedingTimeLimit", "Seeding time limit (minutes, -1 = global)"), Value = -1, Minimum = -2 };
        var inactive = new NumberBox { Header = Localizer.Get("Dialog_InactiveSeedingTime", "Inactive seeding time (minutes, -1 = global)"), Value = -1, Minimum = -2 };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(ratio); panel.Children.Add(time); panel.Children.Add(inactive);
        if (await ShowFormAsync(Localizer.Get("Dialog_ShareLimits", "Share limits"), panel) != ContentDialogResult.Primary)
            return;
        await ExecuteMenuActionAsync(() => ViewModel.PostSelectedAsync("setShareLimits", new Dictionary<string, string?>
        {
            ["ratioLimit"] = ratio.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["seedingTimeLimit"] = ((int)time.Value).ToString(),
            ["inactiveSeedingTimeLimit"] = ((int)inactive.Value).ToString()
        }));
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var value = await PromptAsync(Localizer.Get("Dialog_RenameTorrent", "Rename torrent"), Localizer.Get("Dialog_NewName", "New name"), ViewModel.SelectedTorrent?.Name);
        if (string.IsNullOrWhiteSpace(value))
            return;
        await ExecuteMenuActionAsync(() => ViewModel.RenameSelectedAsync(value.Trim()));
    }

    private async void SetLocation_Click(object sender, RoutedEventArgs e)
    {
        var value = await PromptAsync(Localizer.Get("Dialog_SetTorrentLocation", "Set torrent location"), Localizer.Get("Dialog_ServerPath", "Path on the qBittorrent server"), ViewModel.SelectedTorrent?.Model.SavePath);
        if (string.IsNullOrWhiteSpace(value))
            return;
        await ExecuteMenuActionAsync(() => ViewModel.SetLocationSelectedAsync(value.Trim()));
    }

    private async void AddTrackers_Click(object sender, RoutedEventArgs e)
    {
        var urls = await PromptAsync(Localizer.Get("Dialog_AddTrackers", "Add trackers"), Localizer.Get("Dialog_TrackerPerLine", "One tracker URL per line"), multiline: true);
        if (string.IsNullOrWhiteSpace(urls))
            return;
        await ExecuteMenuActionAsync(() => PostForSingleTorrentAsync("addTrackers", "urls", urls));
    }

    private async void RemoveTrackers_Click(object sender, RoutedEventArgs e)
    {
        var urls = string.Join('|', TrackersList.SelectedItems.OfType<TorrentTracker>().Select(static tracker => tracker.Url));
        if (string.IsNullOrEmpty(urls))
            return;
        await ExecuteMenuActionAsync(() => PostForSingleTorrentAsync("removeTrackers", "urls", urls));
    }

    private async void AddWebSeeds_Click(object sender, RoutedEventArgs e)
    {
        var urls = await PromptAsync(Localizer.Get("Dialog_AddHttpSources", "Add HTTP sources"), Localizer.Get("Dialog_UrlPerLine", "One URL per line"), multiline: true);
        if (string.IsNullOrWhiteSpace(urls))
            return;
        await ExecuteMenuActionAsync(() => PostForSingleTorrentAsync("addWebSeeds", "urls", urls));
    }

    private async void RemoveWebSeeds_Click(object sender, RoutedEventArgs e)
    {
        var urls = string.Join('|', WebSeedsList.SelectedItems.OfType<string>());
        if (string.IsNullOrEmpty(urls))
            return;
        await ExecuteMenuActionAsync(() => PostForSingleTorrentAsync("removeWebSeeds", "urls", urls));
    }

    private async Task PostForSingleTorrentAsync(string action, string key, string value)
    {
        if (ViewModel.Api is null || ViewModel.SelectedTorrent is null)
            return;
        await ViewModel.Api.Torrents.PostAsync(action, new Dictionary<string, string?> { ["hash"] = ViewModel.SelectedTorrent.Hash, [key] = value });
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTorrent is null)
            return;
        var picker = new FileSavePicker { SuggestedFileName = SanitizeFileName(ViewModel.SelectedTorrent.Name) };
        picker.FileTypeChoices.Add("Torrent file", [".torrent"]);
        InitializePicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;
        await ExecuteMenuActionAsync(async () => await FileIO.WriteBytesAsync(file, await ViewModel.ExportSelectedAsync()));
    }

    private void CopyInfo_Click(object sender, RoutedEventArgs e)
    {
        var torrent = ViewModel.SelectedTorrent;
        if (torrent is null || sender is not MenuFlyoutItem { Tag: string kind })
            return;

        var text = kind switch
        {
            "name" => torrent.Name,
            "hashv1" => torrent.Model.InfoHashV1,
            "hashv2" => torrent.Model.InfoHashV2,
            "magnet" => BuildMagnetLink(torrent),
            _ => null
        };
        if (string.IsNullOrEmpty(text))
            return;

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private static string BuildMagnetLink(TorrentRowViewModel torrent)
    {
        var hash = !string.IsNullOrEmpty(torrent.Model.InfoHashV1) ? torrent.Model.InfoHashV1 : torrent.Model.InfoHashV2;
        if (string.IsNullOrEmpty(hash))
            return string.Empty;
        var magnet = $"magnet:?xt=urn:btih:{hash}";
        return string.IsNullOrEmpty(torrent.Name) ? magnet : $"{magnet}&dn={Uri.EscapeDataString(torrent.Name)}";
    }

    private async void PreviewFile_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUseLocalFiles || ViewModel.SelectedTorrent is null)
            return;
        var file = SelectedContentFiles().FirstOrDefault(static file => file.Progress > 0)
            ?? ViewModel.SelectedFiles.FirstOrDefault(static file => file.Progress > 0);
        if (file is null)
        {
            await ShowInfoAsync(Localizer.Get("Dialog_PreviewUnavailable", "Preview is not available"), Localizer.Get("Dialog_SelectDownloadedFile", "Select a downloaded file in the Files tab first."));
            return;
        }

        var path = ResolveLocalFilePath(ViewModel.SelectedTorrent, file);
        if (path is not null && File.Exists(path))
        {
            await Windows.System.Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path));
            return;
        }

        await ShowInfoAsync(Localizer.Get("Dialog_PreviewUnavailable", "Preview is not available"), $"{Localizer.Get("Dialog_DownloadedFileNotFound", "The downloaded file was not found locally.")}\n\n{Localizer.Get("Dialog_ExpectedPath", "Expected path")}: {path ?? ViewModel.SelectedTorrent.Model.SavePath}");
    }

    private void ContentFilesFilter_TextChanged(object sender, TextChangedEventArgs e)
        => RebuildFileTree();

    private async void FilesSelectAll_Click(object sender, RoutedEventArgs e)
        => await ApplyFileSelectionAsync(AllFileTreeRoots(), true);

    private async void FilesSelectNone_Click(object sender, RoutedEventArgs e)
        => await ApplyFileSelectionAsync(AllFileTreeRoots(), false);

    private IReadOnlyList<TorrentFileTreeNode> AllFileTreeRoots()
        => TorrentFileTreeBuilder.Build(ViewModel.SelectedTorrent?.Name, ViewModel.SelectedFiles);

    private void FilesExpandAll_Click(object sender, RoutedEventArgs e)
        => SetExpanded(_fileTreeRoots, true);

    private void FilesCollapseAll_Click(object sender, RoutedEventArgs e)
        => SetExpanded(_fileTreeRoots, false);

    private async void FilesRefresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshSelectedFilesAsync();
            RefreshFileTreeValues();
        }
        catch (Exception exception)
        {
            ShowFilesMessage(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void FilesRefreshTimer_Tick(object? sender, object e)
    {
        if (_isRefreshingFiles
            || !ViewModel.IsConnected
            || ViewModel.SelectedTorrent is null
            || !ReferenceEquals(DetailsTabs.SelectedItem, FilesTab))
            return;

        _isRefreshingFiles = true;
        try
        {
            await ViewModel.RefreshSelectedFilesAsync();
            RefreshFileTreeValues();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The main connection loop reports connection failures. A transient details
            // refresh should not cover the file list with repeated error messages.
        }
        finally
        {
            _isRefreshingFiles = false;
        }
    }

    private void RefreshFileTreeValues()
    {
        foreach (var node in _fileTreeRoots)
            node.RefreshDisplayedValues();
    }

    private static void SetExpanded(IEnumerable<TorrentFileTreeNode> nodes, bool expanded)
    {
        foreach (var node in nodes)
        {
            if (node.IsFolder)
                node.IsExpanded = expanded;
            SetExpanded(node.Children, expanded);
        }
    }

    private async void SelectedFilesPriority_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string value } || !int.TryParse(value, out var priority))
            return;
        if (ContentFilesTree.SelectedItem is not TorrentFileTreeNode node)
        {
            ShowFilesMessage(
                Localizer.Get("Files_SelectItemsFirst", "Select a file or folder first."),
                InfoBarSeverity.Informational);
            return;
        }
        await ApplyFilePriorityAsync([node], priority);
    }

    private async void ContentFileCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: TorrentFileTreeNode node } checkBox)
            return;

        var newValue = node.IsChecked != true;
        checkBox.IsChecked = newValue;
        await ApplyFileSelectionAsync([node], newValue);
    }

    private async void ContextFilePriority_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: TorrentFileTreeNode node, CommandParameter: string value }
            || !int.TryParse(value, out var priority))
            return;
        await ApplyFilePriorityAsync([node], priority);
    }

    private async void FilePriorityCombo_DropDownClosed(object sender, object e)
    {
        if (sender is not ComboBox { Tag: TorrentFileTreeNode node } combo
            || combo.SelectedIndex < 0
            || combo.SelectedIndex >= FilePriorityValues.Length)
            return;
        var priority = FilePriorityValues[combo.SelectedIndex];
        if (node.DescendantFiles().All(file => file.Priority == priority))
            return;
        await ApplyFilePriorityAsync([node], priority);
    }

    private async Task ApplyFilePriorityAsync(IEnumerable<TorrentFileTreeNode> nodes, int priority)
    {
        if (ViewModel.Api is null || ViewModel.SelectedTorrent is null)
            return;
        var nodeList = nodes.ToList();
        var files = nodeList
            .SelectMany(static node => node.DescendantFiles())
            .DistinctBy(static file => file.Index)
            .ToList();
        if (files.Count == 0)
            return;

        var previousPriorities = files.ToDictionary(static file => file.Index, static file => file.Priority);
        foreach (var node in nodeList)
            node.SetPriority(priority);
        FilesInfoBar.IsOpen = false;
        try
        {
            await ViewModel.Api.Torrents.PostAsync("filePrio", new Dictionary<string, string?>
            {
                ["hash"] = ViewModel.SelectedTorrent.Hash,
                ["id"] = string.Join('|', files.Select(static file => file.Index)),
                ["priority"] = priority.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            await ViewModel.RefreshSelectedFilesAsync();
            RefreshFileTreeValues();
            ShowFilesMessage(
                string.Format(Localizer.Get("Files_PriorityUpdated", "Priority updated for {0} files."), files.Count),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            foreach (var file in files)
                file.Priority = previousPriorities[file.Index];
            RebuildFileTree();
            ShowFilesMessage(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task ApplyFileSelectionAsync(IEnumerable<TorrentFileTreeNode> nodes, bool selected)
    {
        if (ViewModel.Api is null || ViewModel.SelectedTorrent is null)
            return;
        var nodeList = nodes.ToList();
        var files = nodeList
            .SelectMany(static node => node.DescendantFiles())
            .DistinctBy(static file => file.Index)
            .ToList();
        var changedFiles = files
            .Where(file => selected ? file.Priority == 0 : file.Priority != 0)
            .ToList();
        if (changedFiles.Count == 0)
        {
            RefreshFileTreeValues();
            return;
        }

        var previousPriorities = changedFiles.ToDictionary(static file => file.Index, static file => file.Priority);
        foreach (var node in nodeList)
            node.SetSelection(selected);
        FilesInfoBar.IsOpen = false;
        var priority = selected ? 1 : 0;
        try
        {
            await ViewModel.Api.Torrents.PostAsync("filePrio", new Dictionary<string, string?>
            {
                ["hash"] = ViewModel.SelectedTorrent.Hash,
                ["id"] = string.Join('|', changedFiles.Select(static file => file.Index)),
                ["priority"] = priority.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            await ViewModel.RefreshSelectedFilesAsync();
            RefreshFileTreeValues();
            ShowFilesMessage(
                string.Format(Localizer.Get("Files_SelectionUpdated", "Download selection updated for {0} files."), changedFiles.Count),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            foreach (var file in changedFiles)
                file.Priority = previousPriorities[file.Index];
            RebuildFileTree();
            ShowFilesMessage(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowFilesMessage(string message, InfoBarSeverity severity)
    {
        FilesInfoBar.Message = message;
        FilesInfoBar.Severity = severity;
        FilesInfoBar.IsOpen = true;
    }

    private IReadOnlyList<TorrentFile> SelectedContentFiles()
        => ContentFilesTree.SelectedItem is TorrentFileTreeNode node
            ? node.DescendantFiles()
            : [];

    private async void OpenContentNode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: TorrentFileTreeNode node })
            await OpenContentNodeAsync(node);
    }

    private async void OpenContentNodeDestination_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: TorrentFileTreeNode node })
            await OpenContentNodeDestinationAsync(node);
    }

    private void ContentFileRow_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextFlyout: MenuFlyout flyout } element)
            return;
        e.Handled = true;
        flyout.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
    }

    private async void ContentFileRow_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TorrentFileTreeNode node })
            await OpenContentNodeAsync(node);
    }

    private async Task OpenContentNodeAsync(TorrentFileTreeNode node)
    {
        var torrent = ViewModel.SelectedTorrent;
        if (torrent is null)
            return;
        if (!ViewModel.CanUseLocalFiles)
        {
            await ShowInfoAsync(
                Localizer.Get("Dialog_LocalFilesUnavailableTitle", "Local files are unavailable"),
                Localizer.Get("Dialog_LocalFilesUnavailableMessage", "This torrent belongs to a remote profile. WinBitTorrent can only open folders for the managed local backend."));
            return;
        }

        if (ResolveLocalNodePath(torrent, node, _fileTreeRoots.Contains(node)) is { } path)
        {
            if (node.File is not null)
                await Windows.System.Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(path));
            else
                await Windows.System.Launcher.LaunchFolderPathAsync(path);
            return;
        }

        await ShowInfoAsync(
            Localizer.Get("Dialog_DestinationUnavailableTitle", "Destination folder is not available"),
            Localizer.Get("Files_LocalItemUnavailable", "The selected file or folder is not available locally yet."));
    }

    private async Task OpenContentNodeDestinationAsync(TorrentFileTreeNode node)
    {
        var torrent = ViewModel.SelectedTorrent;
        if (torrent is null)
            return;
        if (!ViewModel.CanUseLocalFiles)
        {
            await ShowInfoAsync(
                Localizer.Get("Dialog_LocalFilesUnavailableTitle", "Local files are unavailable"),
                Localizer.Get("Dialog_LocalFilesUnavailableMessage", "This torrent belongs to a remote profile. WinBitTorrent can only open folders for the managed local backend."));
            return;
        }

        if (ResolveLocalNodePath(torrent, node, _fileTreeRoots.Contains(node)) is { } path)
        {
            SelectInExplorer(path);
            return;
        }

        await ShowInfoAsync(
            Localizer.Get("Dialog_DestinationUnavailableTitle", "Destination folder is not available"),
            Localizer.Get("Files_LocalItemUnavailable", "The selected file or folder is not available locally yet."));
    }

    private static string? ResolveLocalNodePath(TorrentRowViewModel torrent, TorrentFileTreeNode node, bool isRootNode)
    {
        if (node.File is not null)
        {
            var filePath = ResolveLocalFilePath(torrent, node.File);
            return filePath is not null && File.Exists(filePath) ? filePath : null;
        }

        // The root of the tree represents the whole torrent. Its FullPath may be a purely
        // synthetic display name (built to group files that don't already share a common
        // folder) that never existed on disk, so prefer the server-reported content path,
        // which is always correct, over combining names ourselves.
        if (isRootNode && ResolveDestinationTarget(torrent) is { } contentTarget && Directory.Exists(contentTarget))
            return contentTarget;

        if (ResolveLocalDirectoryPath(torrent) is not { } basePath)
            return null;
        var folderPath = Path.Combine(basePath, node.FullPath.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(folderPath) ? folderPath : null;
    }

    private async void BanPeers_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Api is null)
            return;
        var peers = PeersList.SelectedItems.OfType<PeerRowViewModel>().Select(static peer => peer.Address).ToList();
        if (peers.Count == 0)
            return;
        await ExecuteMenuActionAsync(() => ViewModel.Api.Transfer.BanPeersAsync(peers));
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ViewModel.SelectedHashes))
            return;
        if (!ClientSettings.Get("ui.confirmDelete", true))
        {
            await ExecuteMenuActionAsync(() => ViewModel.DeleteSelectedAsync(deleteFiles: false));
            return;
        }
        var files = new CheckBox { Content = Localizer.Get("Dialog_AlsoDeleteFiles", "Also delete files from disk") };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = Localizer.Get("Dialog_DeleteSelectedTorrents", "Delete selected torrents?"), Content = files, PrimaryButtonText = Localizer.Get("Common_Delete", "Delete"), CloseButtonText = Localizer.Get("Common_Cancel", "Cancel"), DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        await ExecuteMenuActionAsync(() => ViewModel.DeleteSelectedAsync(files.IsChecked == true));
    }

    private async void ChooseColumns_Click(object sender, RoutedEventArgs e)
    {
        var list = new StackPanel { Spacing = 4 };
        var choices = new List<(TableViewColumn Column, CheckBox CheckBox)>();
        foreach (var column in TorrentTable.Columns.OrderBy(static column => column.Order))
        {
            var checkBox = new CheckBox { Content = column.Header?.ToString(), IsChecked = column.Visibility == Visibility.Visible };
            choices.Add((column, checkBox)); list.Children.Add(checkBox);
        }
        var scroll = new ScrollViewer { Content = list, MaxHeight = 430 };
        if (await ShowFormAsync(Localizer.Get("Dialog_ChooseTorrentColumns", "Choose torrent columns"), scroll) != ContentDialogResult.Primary)
            return;
        foreach (var (column, checkBox) in choices)
            column.Visibility = checkBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SaveLayout();
    }

    private async Task<string?> PromptAsync(string title, string placeholder, string? value = null, bool multiline = false)
    {
        var input = new TextBox { PlaceholderText = placeholder, Text = value ?? string.Empty, AcceptsReturn = multiline, Height = multiline ? 120 : double.NaN, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        return await ShowFormAsync(title, input) == ContentDialogResult.Primary ? input.Text : null;
    }

    private async Task<ContentDialogResult> ShowFormAsync(string title, object content)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = content, PrimaryButtonText = Localizer.Get("Common_Apply", "Apply"), CloseButtonText = Localizer.Get("Common_Cancel", "Cancel"), DefaultButton = ContentDialogButton.Primary };
        return await dialog.ShowAsync();
    }

    private async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, CloseButtonText = Localizer.Get("Common_OK", "OK"), DefaultButton = ContentDialogButton.Close };
        await dialog.ShowAsync();
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, PrimaryButtonText = Localizer.Get("Common_Confirm", "Confirm"), CloseButtonText = Localizer.Get("Common_Cancel", "Cancel"), DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string SanitizeFileName(string value)
        => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static string? ResolveLocalDirectoryPath(TorrentRowViewModel torrent)
    {
        if (!string.IsNullOrWhiteSpace(torrent.Model.SavePath))
            return torrent.Model.SavePath;
        if (!string.IsNullOrWhiteSpace(torrent.Model.DownloadPath))
            return torrent.Model.DownloadPath;
        return null;
    }

    private static string? ResolveLocalFilePath(TorrentRowViewModel torrent, TorrentFile? file)
    {
        if (file is null)
            return null;

        var basePath = ResolveLocalDirectoryPath(torrent);
        if (string.IsNullOrWhiteSpace(basePath))
            return null;

        var relative = file.Name.Replace('/', Path.DirectorySeparatorChar);
        var combined = Path.Combine(basePath, relative);
        if (File.Exists(combined))
            return combined;

        if (!string.IsNullOrWhiteSpace(torrent.Model.DownloadPath))
        {
            var fromDownloadPath = Path.Combine(torrent.Model.DownloadPath, relative);
            if (File.Exists(fromDownloadPath))
                return fromDownloadPath;
        }

        return combined;
    }

    private static void InitializePicker(object picker)
    {
        var window = App.Services.GetRequiredService<MainWindow>();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
    }

    private sealed record ColumnState(string? Id, string Header, double Width, bool Visible, int Order);
}
