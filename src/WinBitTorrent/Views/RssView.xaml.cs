using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent.Views;

public sealed partial class RssView : UserControl
{
    public RssView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<RssViewModel>();
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
    }

    private RssViewModel ViewModel => (RssViewModel)DataContext;

    private void ArticleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ArticleBody.Text = ViewModel.SelectedArticle?.Description ?? string.Empty;

    private async void AddFeed_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { PlaceholderText = "https://example.com/feed.xml" };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "New RSS feed", Content = input, PrimaryButtonText = "Add", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && Uri.TryCreate(input.Text, UriKind.Absolute, out _))
            await ViewModel.AddFeedAsync(input.Text.Trim());
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { PlaceholderText = "Folder name" };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "New RSS folder", Content = input, PrimaryButtonText = "Create", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
            await ViewModel.AddFolderAsync(input.Text.Trim());
    }

    private async void OpenArticle_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(ViewModel.SelectedArticle?.Link, UriKind.Absolute, out var uri))
            await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private void Rules_Click(object sender, RoutedEventArgs e)
        => new RssRulesWindow().Activate();
}
