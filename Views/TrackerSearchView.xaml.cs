using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using WinBitTorrent.Services;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent.Views;

public sealed partial class TrackerSearchView : UserControl
{
    private bool _openingBrowser;
    private bool _checkingSession;

    public TrackerSearchView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<TrackerSearchViewModel>();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private TrackerSearchViewModel ViewModel => (TrackerSearchViewModel)DataContext;

    private async void TrackerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string trackerId })
            await ViewModel.SelectTrackerAsync(trackerId);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackerSearchViewModel.IsBrowserLoginVisible) && ViewModel.IsBrowserLoginVisible)
            _ = OpenInteractiveLoginAsync();
    }

    private async Task OpenInteractiveLoginAsync()
    {
        if (_openingBrowser || !ViewModel.IsBrowserLoginVisible)
            return;

        var loginPage = ViewModel.StartInteractiveLogin();
        if (loginPage is null)
            return;

        _openingBrowser = true;
        try
        {
            await TrackerLoginWebView.EnsureCoreWebView2Async();
            if (await TryCompleteBrowserSessionAsync().ConfigureAwait(true))
                return;

            ViewModel.ErrorMessage = string.Empty;
            ViewModel.Status = Localizer.Get("Tracker_BrowserLoginStatus", "Sign in and complete the captcha below.");
            // CoreWebView2.Navigate also reloads when the requested address is already
            // assigned to Source. This is important after sign-out: the old document
            // can still be the signed-in page even though its cookies were removed.
            TrackerLoginWebView.CoreWebView2.Navigate(loginPage.AbsoluteUri);
        }
        catch (Exception exception)
        {
            ViewModel.ErrorMessage = string.Format(
                Localizer.Get("Tracker_BrowserOpenFailed", "Could not open the RuTracker sign-in page: {0}"),
                exception.Message);
        }
        finally
        {
            _openingBrowser = false;
        }
    }

    private async void Query_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
            await ViewModel.SearchAsync();
    }

    private async void Download_Click(object sender, RoutedEventArgs e) => await OpenAddTorrentWindowForSelectedAsync();
    private async void TrackerResultsTable_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => await OpenAddTorrentWindowForSelectedAsync();
    private async void OpenTopic_Click(object sender, RoutedEventArgs e) => await ViewModel.OpenSelectedAsync();

    private async Task OpenAddTorrentWindowForSelectedAsync()
    {
        var torrentFile = await ViewModel.DownloadSelectedTorrentFileAsync().ConfigureAwait(true);
        if (torrentFile is null)
            return;

        var window = new AddTorrentWindow([torrentFile], []);
        window.Closed += (_, _) =>
        {
            try { File.Delete(torrentFile); }
            catch { }
        };
        window.Activate();
    }

    private void TrackerBrowserBack_Click(object sender, RoutedEventArgs e)
    {
        TrackerLoginWebView.CoreWebView2?.Stop();
        ViewModel.CancelInteractiveLogin();
    }

    private async void TrackerBrowserComplete_Click(object sender, RoutedEventArgs e)
    {
        if (!await TryCompleteBrowserSessionAsync().ConfigureAwait(true))
        {
            ViewModel.ErrorMessage = Localizer.Get(
                "Tracker_BrowserNotSignedIn",
                "Sign-in has not completed yet. Enter your credentials and complete the captcha.");
        }
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        await ClearTrackerBrowserSessionAsync().ConfigureAwait(true);
        await ViewModel.SignOutAsync().ConfigureAwait(true);
        await OpenInteractiveLoginAsync().ConfigureAwait(true);
    }

    private async Task ClearTrackerBrowserSessionAsync()
    {
        try
        {
            await TrackerLoginWebView.EnsureCoreWebView2Async();
            TrackerLoginWebView.CoreWebView2.CookieManager.DeleteAllCookies();
        }
        catch
        {
        }
    }

    private void TrackerLoginWebView_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Scheme == "about")
            return;

        var allowedHost = uri.Host.Equals("rutracker.org", StringComparison.OrdinalIgnoreCase) ||
                          uri.Host.Equals("rutracker.net", StringComparison.OrdinalIgnoreCase);
        var allowedPath = uri.AbsolutePath.Equals("/forum/login.php", StringComparison.OrdinalIgnoreCase) ||
                          uri.AbsolutePath.Equals("/forum/index.php", StringComparison.OrdinalIgnoreCase) ||
                          uri.AbsolutePath.Equals("/forum/", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && allowedHost && allowedPath)
            return;

        args.Cancel = true;
        ViewModel.Status = Localizer.Get(
            "Tracker_BrowserNavigationBlocked",
            "Only the RuTracker sign-in page is available here.");
    }

    private async void TrackerLoginWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess)
        {
            // Keep WebView visible so its own network/certificate error is not
            // replaced by an unexplained empty panel.
            ViewModel.ErrorMessage = string.Format(
                Localizer.Get("Tracker_BrowserNavigationFailed", "The sign-in page could not be loaded: {0}"),
                args.WebErrorStatus);
            return;
        }

        if (await TryCompleteBrowserSessionAsync().ConfigureAwait(true))
            return;

        if (TrackerLoginWebView.Source?.AbsolutePath.Equals("/forum/login.php", StringComparison.OrdinalIgnoreCase) == true)
        {
            await FocusLoginFormAsync().ConfigureAwait(true);
            ViewModel.Status = Localizer.Get("Tracker_WaitingForBrowserLogin", "Waiting for RuTracker sign-in…");
            return;
        }

        var loginPage = ViewModel.StartInteractiveLogin();
        if (loginPage is not null)
            TrackerLoginWebView.CoreWebView2?.Navigate(loginPage.AbsoluteUri);
    }

    private async Task<bool> TryCompleteBrowserSessionAsync()
    {
        if (_checkingSession || TrackerLoginWebView.CoreWebView2 is null)
            return false;

        _checkingSession = true;
        try
        {
            var manager = TrackerLoginWebView.CoreWebView2.CookieManager;
            var cookieUris = new[]
            {
                "https://rutracker.org/forum/",
                "https://rutracker.net/forum/",
                TrackerLoginWebView.Source?.AbsoluteUri
            }
                .Where(static uri => !string.IsNullOrWhiteSpace(uri))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var webViewCookies = new List<CoreWebView2Cookie>();
            foreach (var uri in cookieUris)
                webViewCookies.AddRange(await manager.GetCookiesAsync(uri!));

            var cookies = webViewCookies
                .Select(static cookie => new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
                {
                    Expires = cookie.IsSession
                        ? DateTime.MinValue
                        : DateTimeOffset.FromUnixTimeSeconds((long)cookie.Expires).UtcDateTime,
                    HttpOnly = cookie.IsHttpOnly,
                    Secure = cookie.IsSecure
                })
                .GroupBy(static cookie => (cookie.Name, cookie.Domain, cookie.Path))
                .Select(static group => group.First())
                .ToArray();
            if (!cookies.Any(static cookie => cookie.Name.Equals("bb_session", StringComparison.OrdinalIgnoreCase)))
                return false;

            var userName = await ReadSignedInUserAsync().ConfigureAwait(true);
            return await ViewModel.CompleteInteractiveLoginAsync(cookies, userName).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ViewModel.ErrorMessage = string.Format(
                Localizer.Get("Tracker_BrowserSessionReadFailed", "Could not check the RuTracker session: {0}"),
                exception.Message);
            return false;
        }
        finally
        {
            _checkingSession = false;
        }
    }

    private async Task<string?> ReadSignedInUserAsync()
    {
        if (TrackerLoginWebView.CoreWebView2 is null || TrackerLoginWebView.Source is null)
            return null;

        const string script = """
            (() => {
                const element = document.querySelector(
                    '#logged-in-username, a[href*="profile.php?mode=viewprofile"]');
                return element?.textContent?.trim() || '';
            })()
            """;
        try
        {
            var result = await TrackerLoginWebView.CoreWebView2.ExecuteScriptAsync(script);
            return JsonSerializer.Deserialize<string>(result);
        }
        catch
        {
            return null;
        }
    }

    private async Task FocusLoginFormAsync()
    {
        if (TrackerLoginWebView.CoreWebView2 is null)
            return;

        const string script = """
            (() => {
                const form = document.querySelector('#login-form-full');
                if (!form) return false;

                let visible = form;
                while (visible && visible !== document.body) {
                    const parent = visible.parentElement;
                    if (!parent) break;
                    for (const sibling of parent.children) {
                        if (sibling !== visible && sibling.tagName !== 'SCRIPT' && sibling.tagName !== 'STYLE') {
                            sibling.style.setProperty('display', 'none', 'important');
                        }
                    }
                    visible = parent;
                }

                document.documentElement.style.cssText =
                    'background:#fff!important;min-width:0!important;overflow:auto!important';
                document.body.style.cssText =
                    'background:#fff!important;min-width:0!important;margin:0!important;padding:28px!important;overflow:auto!important';
                form.querySelector('.nav')?.style.setProperty('display', 'none', 'important');

                let style = document.getElementById('winbittorrent-login-style');
                if (!style) {
                    style = document.createElement('style');
                    style.id = 'winbittorrent-login-style';
                    document.head.appendChild(style);
                }
                style.textContent = `
                    #body_container, #page_container, #page_content, #main_content,
                    #main_content_wrap { min-width:0!important; width:100%!important; margin:0!important;
                        padding:0!important; background:#fff!important; }
                    #login-form-full { width:min(560px, 100%)!important; margin:0 auto!important; }
                    #login-form-full .forumline { width:100%!important; margin:0!important;
                        border:1px solid #d4d7dc!important; border-radius:10px!important;
                        box-shadow:0 10px 32px rgba(0,0,0,.08)!important; overflow:hidden!important; }
                    #login-form-full input[type=text], #login-form-full input[type=password] {
                        min-width:260px!important; padding:8px 10px!important; font-size:15px!important; }
                    #login-form-full input[type=submit] { padding:8px 28px!important; cursor:pointer!important; }
                    #login-form-full a { pointer-events:none!important; }
                `;
                form.querySelector('input[name="login_username"]')?.focus();
                return true;
            })()
            """;
        try
        {
            await TrackerLoginWebView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch { }
    }
}
