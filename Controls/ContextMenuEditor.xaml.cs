using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using WinBitTorrent.Services;

namespace WinBitTorrent.Controls;

/// <summary>
/// Visual editor for the torrent context menu: the left list draws the menu as it will pop up and
/// accepts reordering, the right list holds the commands that are currently left out. Nothing is
/// persisted here - the hosting settings window reads <see cref="VisibleIds"/> and
/// <see cref="HiddenIds"/> when the user applies the settings.
/// </summary>
public sealed partial class ContextMenuEditor : UserControl
{
    private readonly ObservableCollection<ContextMenuEditorItem> _menu = [];
    private readonly ObservableCollection<ContextMenuEditorItem> _available = [];

    // WinUI hands the drop handler a DataPackage, not the dragged object, so the item and the list
    // it came from are remembered when the drag starts. That source is also what distinguishes a
    // reorder (which the ListView performs itself) from a drop coming from the other list.
    private ListView? _dragSource;
    private ContextMenuEditorItem? _dragged;

    public ContextMenuEditor()
    {
        InitializeComponent();
        HintText.Text = Localizer.Get("MenuEditor_Hint", "Drag entries to reorder them, or drag them between the lists. Click an available command to add it to the menu.");
        PreviewCaption.Text = Localizer.Get("MenuEditor_Preview", "Torrent context menu");
        AvailableCaption.Text = Localizer.Get("MenuEditor_Available", "Available commands");
        AddSeparatorText.Text = Localizer.Get("MenuEditor_AddSeparator", "Add a separator");
        MenuEmptyText.Text = Localizer.Get("MenuEditor_MenuEmpty", "The menu is empty. Add commands from the list on the right — an empty menu falls back to the default one.");
        AvailableEmptyText.Text = Localizer.Get("MenuEditor_AvailableEmpty", "Every command is already in the menu.");

        MenuList.ItemsSource = _menu;
        AvailableList.ItemsSource = _available;
        _menu.CollectionChanged += (_, _) => UpdateEmptyStates();
        _available.CollectionChanged += (_, _) => UpdateEmptyStates();
        Load(TorrentMenuLayout.LoadVisible(), TorrentMenuLayout.LoadHidden());
    }

    /// <summary>The menu contents, top to bottom, including separators.</summary>
    public IReadOnlyList<string> VisibleIds => _menu.Select(static item => item.Id).ToList();

    /// <summary>Commands deliberately left out of the menu.</summary>
    public IReadOnlyList<string> HiddenIds => _available.Select(static item => item.Id).ToList();

    public void Reset() => Load(TorrentMenuLayout.DefaultVisible, []);

    private void Load(IReadOnlyList<string> visible, IReadOnlyList<string> hidden)
    {
        _menu.Clear();
        _available.Clear();

        foreach (var id in visible)
        {
            if (id == TorrentMenuLayout.SeparatorId)
                _menu.Add(ContextMenuEditorItem.Separator());
            else if (TorrentMenuLayout.Find(id) is { } entry)
                _menu.Add(new ContextMenuEditorItem(entry));
        }

        foreach (var id in hidden)
        {
            if (TorrentMenuLayout.Find(id) is { } entry)
                _available.Add(new ContextMenuEditorItem(entry));
        }

        UpdateEmptyStates();
    }

    private void UpdateEmptyStates()
    {
        MenuEmptyText.Visibility = _menu.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AvailableEmptyText.Visibility = _available.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddSeparator_Click(object sender, RoutedEventArgs e)
        => Insert(ContextMenuEditorItem.Separator());

    private void AvailableList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ContextMenuEditorItem item)
            return;
        _available.Remove(item);
        Insert(item);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ContextMenuEditorItem item })
            return;
        _menu.Remove(item);
        // A separator is just a rule in the menu - there is nothing to put back on the shelf.
        if (!item.IsSeparator)
            _available.Add(item);
    }

    // Clicking an available command drops it right below the selected row, which is how the user
    // aims for a spot without dragging; with nothing selected it lands at the end of the menu.
    private void Insert(ContextMenuEditorItem item)
    {
        var index = MenuList.SelectedIndex >= 0 ? MenuList.SelectedIndex + 1 : _menu.Count;
        _menu.Insert(index, item);
        MenuList.SelectedItem = item;
        MenuList.ScrollIntoView(item);
    }

    private void List_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragSource = sender as ListView;
        _dragged = e.Items.FirstOrDefault() as ContextMenuEditorItem;
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText(_dragged?.Id ?? string.Empty);
    }

    private void MenuList_DragOver(object sender, DragEventArgs e)
    {
        if (_dragged is null)
            return;
        e.AcceptedOperation = DataPackageOperation.Move;
        if (_dragSource == AvailableList && e.DragUIOverride is { } overlay)
        {
            overlay.Caption = Localizer.Get("MenuEditor_DropAdd", "Add to the menu");
            overlay.IsCaptionVisible = true;
        }
    }

    private void MenuList_Drop(object sender, DragEventArgs e)
    {
        // Reordering inside the menu is handled by the ListView itself; only a drop coming from
        // the available list has to be moved across by hand.
        if (_dragged is null || _dragSource != AvailableList)
            return;

        var item = _dragged;
        _available.Remove(item);
        _menu.Insert(DropIndex(MenuList, e, _menu.Count), item);
        MenuList.SelectedItem = item;
        e.Handled = true;
        ClearDrag();
    }

    private void AvailableList_DragOver(object sender, DragEventArgs e)
    {
        if (_dragged is null || _dragSource != MenuList)
            return;
        e.AcceptedOperation = DataPackageOperation.Move;
        if (e.DragUIOverride is { } overlay)
        {
            overlay.Caption = Localizer.Get("MenuEditor_DropRemove", "Remove from the menu");
            overlay.IsCaptionVisible = true;
        }
    }

    private void AvailableList_Drop(object sender, DragEventArgs e)
    {
        if (_dragged is null || _dragSource != MenuList)
            return;

        var item = _dragged;
        _menu.Remove(item);
        if (!item.IsSeparator)
            _available.Insert(DropIndex(AvailableList, e, _available.Count), item);
        e.Handled = true;
        ClearDrag();
    }

    private void ClearDrag()
    {
        _dragged = null;
        _dragSource = null;
    }

    // Finds the row the pointer is hovering and returns the index above or below it, so an item
    // lands exactly where the drop indicator suggested.
    private static int DropIndex(ListView list, DragEventArgs e, int fallback)
    {
        var position = e.GetPosition(list).Y;
        for (var index = 0; index < list.Items.Count; index++)
        {
            if (list.ContainerFromIndex(index) is not ListViewItem container)
                continue;
            var bounds = container
                .TransformToVisual(list)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (position < bounds.Top + (bounds.Height / 2))
                return index;
        }
        return fallback;
    }
}
