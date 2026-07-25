using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent.Views;

public sealed partial class CatalogView : UserControl
{
    public CatalogView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<CatalogViewModel>();
    }

    private CatalogViewModel ViewModel => (CatalogViewModel)DataContext;

    private async void Root_Loaded(object sender, RoutedEventArgs e) => await ViewModel.EnsureLoadedAsync();

    private async void Query_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
            await ViewModel.SearchCommand.ExecuteAsync(null);
    }

    private async void CatalogGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CatalogCardViewModel card)
            await ViewModel.OpenDetailsCommand.ExecuteAsync(card);
    }

    // A horizontal ListView consumes the vertical mouse wheel (it has nowhere vertical to scroll),
    // which stalls the page's vertical scroll whenever the cursor is over a poster row. Redirect a
    // plain wheel to the page's ScrollViewer; Shift+wheel keeps the row's native horizontal scroll.
    private void RowList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView { Tag: null } list)
        {
            list.Tag = "wheel-hooked";
            list.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(Row_PointerWheelChanged), handledEventsToo: true);
        }
    }

    private void Row_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListView list || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
            return;

        var outer = FindParentScrollViewer(list);
        if (outer is null)
            return;

        var delta = e.GetCurrentPoint(list).Properties.MouseWheelDelta;
        outer.ChangeView(null, outer.VerticalOffset - delta, null, disableAnimation: false);
        e.Handled = true;
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject start)
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer scrollViewer)
                return scrollViewer;
        return null;
    }

    private void CatalogNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Selection can resolve before DataContext is assigned (it is set after InitializeComponent),
        // so guard against a null/again-typed DataContext rather than casting unconditionally.
        if (DataContext is CatalogViewModel viewModel
            && args.SelectedItem is NavigationViewItem { Tag: string tag }
            && Enum.TryParse<CatalogNav>(tag, out var nav))
            viewModel.SelectedNav = nav;
    }
}
