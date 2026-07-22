using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
}
