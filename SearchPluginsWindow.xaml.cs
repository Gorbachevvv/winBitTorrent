using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent;

public sealed partial class SearchPluginsWindow : Window
{
    private readonly MainViewModel _main;
    private readonly SearchViewModel _search;

    public SearchPluginsWindow()
    {
        InitializeComponent();
        Title = WinBitTorrent.Services.Localizer.Get("WindowTitle_SearchPlugins", "Search plugins");
        this.ConfigureOwned(820, 560);
        _main = App.Services.GetRequiredService<MainViewModel>();
        _search = App.Services.GetRequiredService<SearchViewModel>();
        Activated += SearchPluginsWindow_Activated;
    }

    private async void SearchPluginsWindow_Activated(object sender, WindowActivatedEventArgs args) { Activated -= SearchPluginsWindow_Activated; await RefreshAsync(); }
    private async Task RefreshAsync() { try { await _search.LoadPluginsAsync(); PluginsList.ItemsSource = _search.Plugins; } catch (Exception exception) { Show(exception); } }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { PlaceholderText = "Plugin URL or local .py path" };
        var dialog = new ContentDialog { XamlRoot = ((FrameworkElement)Content).XamlRoot, Title = "Install search plugin", Content = input, PrimaryButtonText = "Install", CloseButtonText = "Cancel" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text) || _main.Api is null) return;
        try { await _main.Api.Search.PostAsync("installPlugin", new Dictionary<string, string?> { ["sources"] = input.Text.Trim() }); await RefreshAsync(); } catch (Exception exception) { Show(exception); }
    }

    private async void Enable_Click(object sender, RoutedEventArgs e) => await SetEnabledAsync(true);
    private async void Disable_Click(object sender, RoutedEventArgs e) => await SetEnabledAsync(false);
    private async Task SetEnabledAsync(bool enabled)
    {
        if (_main.Api is null) return;
        var names = SelectedNames(); if (string.IsNullOrEmpty(names)) return;
        try { await _main.Api.Search.PostAsync("enablePlugin", new Dictionary<string, string?> { ["names"] = names, ["enable"] = enabled.ToString().ToLowerInvariant() }); await RefreshAsync(); } catch (Exception exception) { Show(exception); }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_main.Api is null) return; var names = SelectedNames(); if (string.IsNullOrEmpty(names)) return;
        try { await _main.Api.Search.PostAsync("uninstallPlugin", new Dictionary<string, string?> { ["names"] = names }); await RefreshAsync(); } catch (Exception exception) { Show(exception); }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_main.Api is null) return;
        try { await _main.Api.Search.PostAsync("updatePlugins", new Dictionary<string, string?>()); await RefreshAsync(); } catch (Exception exception) { Show(exception); }
    }

    private string SelectedNames() => string.Join('|', PluginsList.SelectedItems.OfType<SearchPluginViewModel>().Select(static item => item.Name));
    private void Show(Exception exception) { MessageBar.Message = exception.Message; MessageBar.Severity = InfoBarSeverity.Error; MessageBar.IsOpen = true; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
