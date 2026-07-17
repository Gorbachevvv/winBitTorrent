using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent;

public sealed partial class CookiesWindow : Window
{
    private readonly MainViewModel _main;

    public CookiesWindow()
    {
        InitializeComponent();
        Title = WinBitTorrent.Services.Localizer.Get("WindowTitle_Cookies", "Cookies");
        this.ConfigureOwned(760, 580);
        _main = App.Services.GetRequiredService<MainViewModel>();
        Activated += CookiesWindow_Activated;
    }

    private async void CookiesWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= CookiesWindow_Activated;
        if (_main.Api is null)
            return;
        try
        {
            CookiesEditor.Text = (await _main.Api.Application.GetCookiesAsync()).ToJsonString(new() { WriteIndented = true });
        }
        catch (Exception exception) { Show(exception.Message, InfoBarSeverity.Error); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_main.Api is null)
            return;
        try
        {
            var cookies = JsonNode.Parse(CookiesEditor.Text) as JsonArray ?? throw new InvalidOperationException("Enter a valid JSON array.");
            await _main.Api.Application.SetCookiesAsync(cookies);
            Show("Cookies saved.", InfoBarSeverity.Success);
        }
        catch (Exception exception) { Show(exception.Message, InfoBarSeverity.Error); }
    }

    private void Show(string message, InfoBarSeverity severity) { MessageBar.Message = message; MessageBar.Severity = severity; MessageBar.IsOpen = true; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
