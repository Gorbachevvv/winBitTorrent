using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent.Views;

public sealed partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<SearchViewModel>();
        Loaded += async (_, _) => await ViewModel.LoadPluginsAsync();
    }

    private SearchViewModel ViewModel => (SearchViewModel)DataContext;

    private async void Download_Click(object sender, RoutedEventArgs e) => await ViewModel.DownloadSelectedAsync();
    private async void ResultsTable_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => await ViewModel.DownloadSelectedAsync();
    private void Plugins_Click(object sender, RoutedEventArgs e) => new SearchPluginsWindow().Activate();
    private async void Query_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
            await ViewModel.StartAsync();
    }
}
