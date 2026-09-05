using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Core.Services;
using WinBitTorrent.Services;
using WinBitTorrent.ViewModels;
using WinUI.TableView;

namespace WinBitTorrent.Views;

public sealed partial class TransfersView : UserControl
{
    private static readonly InputCursor HorizontalResizeCursor =
        InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private static readonly InputCursor VerticalResizeCursor =
        InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);

    private const double SidebarDefaultWidth = 220;
    private const double SidebarMinExpandedWidth = 180;
    private const double SidebarMaxWidth = 300;
    private const double SidebarCollapseThreshold = 112;
    private const double SidebarSplitterWidth = 10;
    private const double ColumnMenuMinHeight = 320;
    private const double ColumnMenuMaxHeight = 480;

    private static readonly int[] FilePriorityValues = [0, 1, 6, 7];
    private readonly ObservableCollection<TorrentFileTreeNode> _fileTreeRoots = [];
    private readonly DispatcherTimer _filesRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _fileEventsAttached;
    private bool _fileTreeRefreshQueued;
    private bool _isRefreshingFiles;
    private bool _sidebarCollapsed;
    private double _sidebarDragWidth;
    private double _expandedSidebarWidth = SidebarDefaultWidth;
    private int _detailsSelectedIndex;
    private Storyboard? _detailsTransition;

    // Torrent context menu layout: every command keyed by the Tag it carries in XAML, plus a pool
    // of separator instances, so the flyout can be re-ordered from the saved layout on each open.
    private Dictionary<string, MenuFlyoutItemBase>? _menuCommands;
    private readonly List<MenuFlyoutSeparator> _menuSeparators = [];
    private string _appliedMenuLayout = string.Empty;

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
        UpdateDetailsSelectorBackgrounds(DetailsSelector);
        ConfigureSidebarAccessibility();
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

    private void DetailsSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        UpdateDetailsSelectorBackgrounds(sender);

        if (DetailsTabs is null)
            return;

        var index = sender.Items.IndexOf(sender.SelectedItem);
        if (index < 0 || index >= DetailsTabs.TabItems.Count || index == DetailsTabs.SelectedIndex)
            return;

        var direction = index > _detailsSelectedIndex ? 1 : -1;
        _detailsSelectedIndex = index;
        DetailsTabs.SelectedIndex = index;
        AnimateDetailsTransition(direction);
    }

    private static void UpdateDetailsSelectorBackgrounds(SelectorBar selector)
    {
        if (selector.Resources["SelectorBarItemBackground"] is not Brush normalBrush
            || selector.Resources["SelectorBarItemBackgroundSelected"] is not Brush selectedBrush)
            return;

        foreach (var item in selector.Items)
            item.Background = ReferenceEquals(item, selector.SelectedItem) ? selectedBrush : normalBrush;
    }

    private void AnimateDetailsTransition(int direction)
    {
        _detailsTransition?.Stop();
        DetailsContentTransform.TranslateX = 0;
        DetailsTabs.Opacity = 1;

        if (!new UISettings().AnimationsEnabled)
            return;

        var duration = new Duration(TimeSpan.FromMilliseconds(150));
        _detailsTransition = new Storyboard();
        _detailsTransition.Children.Add(CreateDetailsAnimation(
            DetailsContentTransform, "TranslateX", direction * 7, 0, duration));
        _detailsTransition.Children.Add(CreateDetailsAnimation(
            DetailsTabs, "Opacity", 0.72, 1, duration));
        _detailsTransition.Begin();
    }

    private static DoubleAnimation CreateDetailsAnimation(
        DependencyObject target,
        string property,
        double from,
        double to,
        Duration duration)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

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

    private async void DeleteKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Delete remains an editing key inside text controls. Everywhere else on the active
        // Transfers page it acts on the selected torrents, regardless of whether selection came
        // from row clicks, Ctrl/Shift clicks, or the rubber-band marquee.
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or PasswordBox or RichEditBox or NumberBox
            || ViewModel.SelectedTorrents.Count == 0)
            return;

        args.Handled = true;
        await TorrentActions.ConfirmDeleteSelectedAsync(XamlRoot, ViewModel);
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

        // A row click focuses the table natively, but a drag that starts on its empty surface does
        // not. Give marquee selection the same keyboard semantics so Delete and navigation keys
        // work immediately after the mouse button is released.
        TorrentTable.Focus(FocusState.Pointer);
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

    private void DetailsSplitter_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = VerticalResizeCursor;
        DetailsSplitterGrip.Opacity = 1;
    }

    private void SidebarSplitter_PointerEntered(object sender, PointerRoutedEventArgs e)
        => ProtectedCursor = HorizontalResizeCursor;

    private void Splitter_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = null;
        if (ReferenceEquals(sender, DetailsSplitter))
            DetailsSplitterGrip.Opacity = 0.72;
    }

    private void SidebarSplitter_DragStarted(object sender, DragStartedEventArgs e)
    {
        _sidebarDragWidth = _sidebarCollapsed ? 0 : SidebarColumn.ActualWidth;
        if (_sidebarCollapsed)
            SidebarPanel.Visibility = Visibility.Visible;
        UpdateSidebarPanelClip(_sidebarDragWidth);
    }

    private void SidebarSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _sidebarDragWidth = Math.Clamp(_sidebarDragWidth + e.HorizontalChange, 0, SidebarMaxWidth);
        SidebarColumn.Width = new GridLength(_sidebarDragWidth);
        SidebarPanel.Visibility = _sidebarDragWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSidebarPanelClip(_sidebarDragWidth);
    }

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_sidebarDragWidth <= SidebarCollapseThreshold)
        {
            SetSidebarCollapsed(true);
            return;
        }

        _expandedSidebarWidth = Math.Clamp(_sidebarDragWidth, SidebarMinExpandedWidth, SidebarMaxWidth);
        SetSidebarCollapsed(false);
    }

    private void SidebarPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateSidebarPanelClip(Math.Min(SidebarColumn.ActualWidth, e.NewSize.Width));

    private void UpdateSidebarPanelClip(double visibleWidth)
    {
        var visibleHeight = Math.Max(SidebarPanel.ActualHeight, LayoutRoot.ActualHeight);
        SidebarPanelClip.Rect = new Rect(0, 0, Math.Max(0, visibleWidth), Math.Max(0, visibleHeight));
    }

    private void SidebarSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        => SetSidebarCollapsed(!_sidebarCollapsed);

    private void ConfigureSidebarAccessibility()
    {
        var header = Localizer.Get("Sidebar_Filters", "Filters");

        SidebarHeaderText.Text = header;
        UpdateSidebarSplitterAccessibility();
    }

    private void UpdateSidebarSplitterAccessibility()
    {
        var description = _sidebarCollapsed
            ? Localizer.Get("Sidebar_Expand", "Expand filters")
            : Localizer.Get("Sidebar_Resize", "Drag to resize. Double-click to collapse.");
        ToolTipService.SetToolTip(SidebarSplitter, description);
        AutomationProperties.SetName(SidebarResizeThumb, description);
    }

    private void SetSidebarCollapsed(bool collapsed, bool save = true)
    {
        if (collapsed && !_sidebarCollapsed && SidebarColumn.ActualWidth >= SidebarCollapseThreshold)
            _expandedSidebarWidth = Math.Clamp(SidebarColumn.ActualWidth, SidebarMinExpandedWidth, SidebarMaxWidth);

        _sidebarCollapsed = collapsed;
        SidebarPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = collapsed
            ? new GridLength(0)
            : new GridLength(Math.Clamp(_expandedSidebarWidth, SidebarMinExpandedWidth, SidebarMaxWidth));
        SidebarDividerColumn.Width = new GridLength(SidebarSplitterWidth);
        UpdateSidebarPanelClip(collapsed ? 0 : _expandedSidebarWidth);
        UpdateSidebarSplitterAccessibility();

        if (save)
            SaveLayout();
    }

    private async void OpenDestination_Click(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.SelectedTorrent;
        if (selected is null)
            return;

        if (!ViewModel.CanUseLocalFiles)
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
            $"{Localizer.Get("Dialog_ReportedSavePath", "Backend-reported save path")}: {selected.Model.SavePath}\n{Localizer.Get("Dialog_DownloadPath", "Download path")}: {selected.Model.DownloadPath}\n\n{Localizer.Get("Dialog_PathUnavailableReason", "The path may not exist yet, or the torrent metadata has not finished syncing.")}");
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
            _expandedSidebarWidth = Math.Clamp(sidebarWidth, SidebarMinExpandedWidth, SidebarMaxWidth);
        SetSidebarCollapsed(ClientSettings.GetValue("layout.sidebarCollapsed") is true, save: false);
        if (ClientSettings.GetValue("layout.detailsHeight") is double detailsHeight)
            ContentGrid.RowDefinitions[2].Height = new GridLength(Math.Max(ContentGrid.RowDefinitions[2].MinHeight, detailsHeight));

        // Column order is never tracked through TableViewColumn.Order (see the comment on
        // TorrentTable_ColumnReordering for why); it is instead the physical sequence of columns
        // in the underlying collection, which the saved array reproduces entry by entry.
        if (ClientSettings.GetValue("layout.torrentColumns") is string json)
        {
            try
            {
                var states = JsonSerializer.Deserialize<List<ColumnState>>(json) ?? [];
                var matched = new List<TableViewColumn>();
                foreach (var state in states)
                {
                    // Matched by header text - stable across reorders and across a changed column
                    // count between app versions, unlike a saved positional index. The one column
                    // with no header (the leading status icon) always keeps its declared position,
                    // so it never needs a saved entry to begin with.
                    var column = TorrentTable.Columns.FirstOrDefault(candidate =>
                        !matched.Contains(candidate) &&
                        candidate.Header?.ToString() is { Length: > 0 } header &&
                        string.Equals(header, state.Header, StringComparison.Ordinal));
                    if (column is null)
                        continue;

                    matched.Add(column);
                    if (double.IsFinite(state.Width) && state.Width > 0)
                        column.Width = new GridLength(Math.Max(column.MinWidth ?? 0d, state.Width));
                    column.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
                }

                // Move every matched column to the front, in saved order, one at a time - and
                // unconditionally, even when a column's position does not actually change. The
                // remove+insert is also what makes WinUI.TableView recompute which columns are
                // "frozen" (see RefreshColumnPlacement), which a column that starts hidden in XAML
                // otherwise never gets right. Columns absent from the file (added by a newer build
                // than the one that saved it) are left untouched, keeping their declared position
                // around the ones that were restored.
                for (var target = 0; target < matched.Count; target++)
                {
                    var column = matched[target];
                    var current = TorrentTable.Columns.IndexOf(column);
                    TorrentTable.Columns.RemoveAt(current);
                    TorrentTable.Columns.Insert(target, column);
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
        ClientSettings.SetValue("layout.sidebarWidth", _expandedSidebarWidth);
        ClientSettings.SetValue("layout.sidebarCollapsed", _sidebarCollapsed);
        ClientSettings.SetValue("layout.detailsHeight", ContentGrid.RowDefinitions[2].ActualHeight);
        // The array's own sequence *is* the saved column order - see RestoreLayout.
        var columns = TorrentTable.Columns.Select(column => new ColumnState(
            column.Header?.ToString() ?? string.Empty,
            double.IsFinite(column.ActualWidth) && column.ActualWidth > 0 ? column.ActualWidth : column.Width.Value,
            column.Visibility == Visibility.Visible)).ToList();
        ClientSettings.SetValue("layout.torrentColumns", JsonSerializer.Serialize(columns));
    }

    // WinUI.TableView's own header drag-and-drop measures the drop position among *visible*
    // columns but then moves that index inside the *raw* (all-columns, hidden included) collection -
    // two different index spaces that only agree when nothing is hidden. This table hides about
    // thirty of its ~38 columns by default, so the built-in move reliably repositions (and
    // corrupts the saved position of) some unrelated column instead of the one that was dragged.
    // The drag gesture and drop indicator are unaffected by this, so only the resulting move is
    // replaced: it is cancelled here and redone from the column/target index the event still
    // reports correctly.
    private void TorrentTable_ColumnReordering(object sender, TableViewColumnReorderingEventArgs e)
    {
        e.Cancel = true;
        MoveVisibleColumn(e.Column, e.DropIndex);
        SaveLayout();
    }

    // Repositions a column among the currently visible ones, leaving every hidden column exactly
    // where it physically sits (their relative order among themselves is never touched, so a later
    // "show" places them predictably). The actual index arithmetic is in ColumnReorderPlanner
    // (WinBitTorrent.Core), unit-tested there independently of any live TableView.
    private void MoveVisibleColumn(TableViewColumn column, int visibleDropIndex)
    {
        var others = TorrentTable.Columns.VisibleColumns.Where(candidate => candidate != column).ToList();
        TorrentTable.Columns.Remove(column);
        var insertAt = ColumnReorderPlanner.ComputeInsertIndex(TorrentTable.Columns.ToList(), others, visibleDropIndex);
        TorrentTable.Columns.Insert(insertAt, column);
    }

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
        var enabled = ViewModel.SelectedTorrent?.Model.SuperSeeding != true;
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

    /// <summary>
    /// Re-arranges the context menu to the layout saved by the editor in Settings -> Behavior.
    /// The XAML flyout stays the single definition of every command - the items are moved around
    /// and left out rather than rebuilt, so their click handlers, x:Names, and check glyphs all
    /// keep working. Rebuilding only happens when the saved order actually changed.
    /// </summary>
    private void ApplyMenuLayout(MenuFlyout flyout)
    {
        if (_menuCommands is null)
        {
            _menuCommands = flyout.Items
                .Where(static item => item.Tag is string)
                .ToDictionary(static item => (string)item.Tag!, static item => item);
            _menuSeparators.AddRange(flyout.Items.OfType<MenuFlyoutSeparator>());
        }

        var layout = TorrentMenuLayout.LoadVisible();
        var signature = string.Join(',', layout);
        if (signature == _appliedMenuLayout)
            return;

        flyout.Items.Clear();
        var separators = 0;
        foreach (var id in layout)
        {
            if (id == TorrentMenuLayout.SeparatorId)
                flyout.Items.Add(MenuSeparator(separators++));
            else if (_menuCommands.TryGetValue(id, out var item))
                flyout.Items.Add(item);
        }
        _appliedMenuLayout = signature;
    }

    // Separators are interchangeable, but the same instance cannot sit in the menu twice, so the
    // pool grows on demand and is reused across rebuilds.
    private MenuFlyoutSeparator MenuSeparator(int index)
    {
        while (_menuSeparators.Count <= index)
            _menuSeparators.Add(new MenuFlyoutSeparator());
        return _menuSeparators[index];
    }

    private void TorrentContextMenu_Opening(object sender, object e)
    {
        if (sender is MenuFlyout flyout)
            ApplyMenuLayout(flyout);

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
        // current flag from the model and flips it.
        SetCheckIcon(ForceStartCheckIcon, model?.ForceStart == true);
        SetCheckIcon(SequentialCheckIcon, model?.SequentialDownload == true);
        SetCheckIcon(FirstLastCheckIcon, model?.FirstLastPiecePriority == true);
        SetCheckIcon(SuperSeedingCheckIcon, model?.SuperSeeding == true);

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
        await ViewModel.Api.Torrents.CreateCategoryAsync(categoryName, path.Text.Trim());
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
        await ExecuteMenuActionAsync(() => ViewModel.Api.Torrents.CreateCategoryAsync(name.Text.Trim(), path.Text.Trim()));
    }

    private async void EditCategory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Api is null || CategoriesList.SelectedItem is not FilterItemViewModel category || category.Key == "Uncategorized")
            return;
        var path = await PromptAsync(Localizer.Get("Dialog_EditCategory", "Edit category"), Localizer.Get("Dialog_DefaultSavePath", "Default save path"));
        if (path is null)
            return;
        await ExecuteMenuActionAsync(() => ViewModel.Api.Torrents.EditCategoryAsync(category.Key, path.Trim()));
    }

    private async void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Api is null || CategoriesList.SelectedItem is not FilterItemViewModel category || category.Key == "Uncategorized")
            return;
        if (!await ConfirmAsync(Localizer.Get("Dialog_DeleteCategory", "Delete category?"), string.Format(Localizer.Get("Dialog_DeleteCategoryMessage", "The torrents in “{0}” will become uncategorized."), category.Title)))
            return;
        await ExecuteMenuActionAsync(() => ViewModel.Api.Torrents.RemoveCategoriesAsync([category.Key]));
    }

    private async void CreateTags_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Api is null)
            return;
        var tags = await PromptAsync(Localizer.Get("Dialog_CreateTags", "Create tags"), Localizer.Get("Dialog_CommaSeparatedTags", "Comma-separated tag names"));
        if (string.IsNullOrWhiteSpace(tags))
            return;
        await ExecuteMenuActionAsync(() => ViewModel.Api.Torrents.CreateTagsAsync(tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
    }

    private async void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Api is null || TagsList.SelectedItem is not FilterItemViewModel tag)
            return;
        if (!await ConfirmAsync(Localizer.Get("Dialog_DeleteTag", "Delete tag?"), string.Format(Localizer.Get("Dialog_DeleteTagMessage", "Remove “{0}” from all torrents?"), tag.Title)))
            return;
        await ExecuteMenuActionAsync(() => ViewModel.Api.Torrents.DeleteTagsAsync([tag.Key]));
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
            await ViewModel.SetDownloadLimitSelectedAsync((long)Math.Max(0, download.Value) * 1024);
            await ViewModel.SetUploadLimitSelectedAsync((long)Math.Max(0, upload.Value) * 1024);
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
        await ExecuteMenuActionAsync(() => ViewModel.SetShareLimitsSelectedAsync(ratio.Value, (int)time.Value, (int)inactive.Value));
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
        var input = new TextBox
        {
            Text = ViewModel.SelectedTorrent?.Model.SavePath ?? string.Empty,
            PlaceholderText = Localizer.Get("Dialog_ServerPath", "Path on the torrent backend"),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(input, input.PlaceholderText);
        var panel = new Grid { ColumnSpacing = 8, MinWidth = 320 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.Children.Add(input);
        var pickerError = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(pickerError, 1);
        Grid.SetColumnSpan(pickerError, 2);
        panel.Children.Add(pickerError);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localizer.Get("Dialog_SetTorrentLocation", "Set torrent location"),
            Content = panel,
            PrimaryButtonText = Localizer.Get("Common_Apply", "Apply"),
            CloseButtonText = Localizer.Get("Common_Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text)
        };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        // A Windows picker cannot choose a directory on a remote torrent server.
        if (ViewModel.CanUseLocalFiles)
        {
            var browse = new Button
            {
                Content = Localizer.Get("CommonBrowse.Content", "Browse…"),
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(browse, 1);
            panel.Children.Add(browse);
            browse.Click += async (_, _) =>
            {
                browse.IsEnabled = false;
                pickerError.Visibility = Visibility.Collapsed;
                try
                {
                    var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
                    picker.FileTypeFilter.Add("*");
                    InitializePicker(picker);
                    if (await picker.PickSingleFolderAsync() is { } folder)
                        input.Text = folder.Path;
                }
                catch (Exception exception)
                {
                    // Do not open another ContentDialog while this one is active.
                    pickerError.Text = exception.Message;
                    pickerError.Visibility = Visibility.Visible;
                }
                finally
                {
                    browse.IsEnabled = true;
                }
            };
        }
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text))
            return;
        await ExecuteMenuActionAsync(() => ViewModel.SetLocationSelectedAsync(input.Text.Trim()));
    }

    private async void AddTrackers_Click(object sender, RoutedEventArgs e)
    {
        var urls = await PromptAsync(Localizer.Get("Dialog_AddTrackers", "Add trackers"), Localizer.Get("Dialog_TrackerPerLine", "One tracker URL per line"), multiline: true);
        if (string.IsNullOrWhiteSpace(urls))
            return;
        await ExecuteMenuActionAsync(() => ViewModel.Api!.Torrents.AddTrackersAsync(
            ViewModel.SelectedTorrent!.Hash, urls.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
    }

    private async void RemoveTrackers_Click(object sender, RoutedEventArgs e)
    {
        var urls = string.Join('|', TrackersList.SelectedItems.OfType<TorrentTracker>().Select(static tracker => tracker.Url));
        if (string.IsNullOrEmpty(urls))
            return;
        await ExecuteMenuActionAsync(() => ViewModel.Api!.Torrents.RemoveTrackersAsync(
            ViewModel.SelectedTorrent!.Hash, urls.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
    }

    private async void AddWebSeeds_Click(object sender, RoutedEventArgs e)
    {
        var urls = await PromptAsync(Localizer.Get("Dialog_AddHttpSources", "Add HTTP sources"), Localizer.Get("Dialog_UrlPerLine", "One URL per line"), multiline: true);
        if (string.IsNullOrWhiteSpace(urls))
            return;
        await ExecuteMenuActionAsync(() => ViewModel.Api!.Torrents.AddWebSeedsAsync(
            ViewModel.SelectedTorrent!.Hash, urls.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
    }

    private async void RemoveWebSeeds_Click(object sender, RoutedEventArgs e)
    {
        var urls = string.Join('|', WebSeedsList.SelectedItems.OfType<string>());
        if (string.IsNullOrEmpty(urls))
            return;
        await ExecuteMenuActionAsync(() => ViewModel.Api!.Torrents.RemoveWebSeedsAsync(
            ViewModel.SelectedTorrent!.Hash, urls.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
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
            await ViewModel.Api.Torrents.SetFilePriorityAsync(
                ViewModel.SelectedTorrent.Hash, files.Select(static file => file.Index), priority);
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
            await ViewModel.Api.Torrents.SetFilePriorityAsync(
                ViewModel.SelectedTorrent.Hash, changedFiles.Select(static file => file.Index), priority);
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
        => await TorrentActions.ConfirmDeleteSelectedAsync(XamlRoot, ViewModel);

    // Column visibility is edited by right-clicking the header row, the way qBittorrent does it,
    // instead of from the row context menu. TableView has no header context flyout of its own, so
    // the right-tap is caught as it bubbles up and answered only when it started on a header.
    private void TorrentTable_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!IsColumnHeader(e.OriginalSource as DependencyObject))
            return;

        // The complete column set is intentionally long. Limit the presenter instead of letting
        // it consume the monitor height; MenuFlyoutPresenter keeps its native mouse-wheel and
        // touch scrolling when constrained by MaxHeight.
        var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(
            FrameworkElement.MaxHeightProperty,
            Math.Clamp(ActualHeight * 0.58, ColumnMenuMinHeight, ColumnMenuMaxHeight)));

        var flyout = new MenuFlyout { MenuFlyoutPresenterStyle = presenterStyle };
        foreach (var column in TorrentTable.Columns.OrderBy(static column => column.Order))
        {
            // The leading status-icon column has no header text and no business being hidden.
            if (column.Header?.ToString() is not { Length: > 0 } header)
                continue;

            var item = new ToggleMenuFlyoutItem { Text = header, IsChecked = column.Visibility == Visibility.Visible };
            item.Click += (_, _) => ToggleColumn(column, item);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(TorrentTable, new FlyoutShowOptions { Position = e.GetPosition(TorrentTable) });
        e.Handled = true;
    }

    private void ToggleColumn(TableViewColumn column, ToggleMenuFlyoutItem item)
    {
        // Hiding the last visible column would leave a table nothing could be done with.
        if (!item.IsChecked && TorrentTable.Columns.Count(static candidate =>
                candidate.Visibility == Visibility.Visible &&
                candidate.Header?.ToString() is { Length: > 0 }) <= 1)
        {
            item.IsChecked = true;
            return;
        }

        column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        RefreshColumnPlacement(column);
        SaveLayout();
    }

    // WinUI.TableView only recomputes which columns are "frozen" (pinned to the left, outside the
    // normal header flow) when the Columns collection itself changes - never when a column's
    // Visibility flips. A column that starts Collapsed in XAML is, at that point, absent from
    // VisibleColumns, and the library's frozen-column check (index-in-VisibleColumns < frozen
    // count) treats "absent" the same as "before the first column" - so it is marked frozen and
    // stays that way even once shown, rendering in the pinned area instead of its real slot. A
    // harmless remove-then-reinsert at the same spot is enough to make the library redo that
    // check (and rebuild the header) now that the column's real position is known.
    private void RefreshColumnPlacement(TableViewColumn column)
    {
        var index = TorrentTable.Columns.IndexOf(column);
        if (index < 0)
            return;
        TorrentTable.Columns.RemoveAt(index);
        TorrentTable.Columns.Insert(index, column);
    }

    private static bool IsColumnHeader(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is TableViewColumnHeader)
                return true;
            if (node is TableView)
                return false;
        }
        return false;
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

    private sealed record ColumnState(string Header, double Width, bool Visible);
}
