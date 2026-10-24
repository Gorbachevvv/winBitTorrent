using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using WinBitTorrent.Infrastructure.Storage;
using WinBitTorrent.Services;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent.Views;

public sealed partial class TrackerSearchView : UserControl
{
    // A tracker blocked by the ISP usually leaves the request hanging instead of refusing it, so the
    // wait is capped and turned into an actionable panel. The navigation itself keeps running: if it
    // does arrive late, NavigationCompleted still takes over.
    private static readonly TimeSpan LoginPageTimeout = TimeSpan.FromSeconds(25);

    // Stored cookies that the tracker keeps refusing send the sign-in page round in circles.
    private const int MaxRejectedSessions = 2;

    private int _rejectedSessions;
    private bool _openingBrowser;
    private bool _checkingSession;
    private bool _navigationCancelled;
    private int _loginAttempt;
    private WebView2? _loginWebView;
    private Uri? _loginWebViewProxy;
    private readonly DispatcherTimer _loginPageTimeout = new();

    public TrackerSearchView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<TrackerSearchViewModel>();
        _loginPageTimeout.Interval = LoginPageTimeout;
        _loginPageTimeout.Tick += LoginPageTimeout_Tick;
        // Subscribed on Loaded, not in the constructor: the tab host unloads and reloads this view,
        // and handlers attached once would be dropped by the first Unloaded and never come back.
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ViewModel.InteractiveLoginRequested -= OnInteractiveLoginRequested;
            ViewModel.InteractiveLoginRequested += OnInteractiveLoginRequested;
        };
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.InteractiveLoginRequested -= OnInteractiveLoginRequested;
            _loginPageTimeout.Tick -= LoginPageTimeout_Tick;
            _loginPageTimeout.Stop();
            DisposeLoginWebView();
        };
    }

    private void OnInteractiveLoginRequested() => _ = OpenInteractiveLoginAsync();

    private void LoginPageTimeout_Tick(object? sender, object e)
    {
        _loginPageTimeout.Stop();
        if (!ViewModel.IsBrowserPageLoading || !ViewModel.IsBrowserLoginVisible)
            return;

        // The attempt may still be stuck inside WebView2 creation, which would make the retry button
        // a no-op. Release the guard here; the attempt counter discards whatever the stuck call does
        // if it ever finishes.
        _openingBrowser = false;
        ViewModel.ReportBrowserPageFailed(Localizer.Get("Tracker_BrowserLoadTimedOut", "The page did not respond in time."));
    }

    private TrackerSearchViewModel ViewModel => (TrackerSearchViewModel)DataContext;

    private async void TrackerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string trackerId })
            await ViewModel.SelectTrackerAsync(trackerId);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Switching the proxy has to rebuild the browser (its route is fixed at creation time) and
        // retry, otherwise a blocked page stays blank no matter how often the switch is flipped.
        if (e.PropertyName == nameof(TrackerSearchViewModel.BrowserProxy) && ViewModel.IsBrowserLoginVisible)
            _ = OpenInteractiveLoginAsync();
    }

    private async void TrackerBrowserRetry_Click(object sender, RoutedEventArgs e)
    {
        _rejectedSessions = 0;
        await OpenInteractiveLoginAsync();
    }

    // The stored cookies survive a restart, so a session the tracker refuses would otherwise come
    // back on every launch. This is the way out of that.
    private async void TrackerBrowserResetSession_Click(object sender, RoutedEventArgs e)
    {
        _rejectedSessions = 0;
        await ClearTrackerBrowserSessionAsync().ConfigureAwait(true);
        await ViewModel.SignOutAsync().ConfigureAwait(true);
        await OpenInteractiveLoginAsync().ConfigureAwait(true);
    }

    private async Task OpenInteractiveLoginAsync()
    {
        if (_openingBrowser || !ViewModel.IsBrowserLoginVisible)
            return;

        if (_rejectedSessions >= MaxRejectedSessions)
        {
            ViewModel.ReportBrowserPageFailed(Localizer.Get(
                "Tracker_BrowserSessionRejected",
                "The tracker did not accept the saved session."));
            return;
        }

        // Guarded before the first await so a second proxy toggle (or a retry click) cannot start a
        // parallel rebuild of the browser control.
        _openingBrowser = true;
        var attempt = ++_loginAttempt;
        try
        {
            var loginPage = ViewModel.StartInteractiveLogin();
            if (loginPage is null)
                return;

            // The watchdog starts before any await: building the WebView2 environment can hang just
            // as easily as the navigation itself, and that used to leave a blank panel with no way
            // out other than restarting the app.
            RestartLoginPageTimeout();

            var webView = await EnsureLoginWebViewAsync(attempt).ConfigureAwait(true);
            if (webView is null || attempt != _loginAttempt)
                return;

            if (await TryCompleteBrowserSessionAsync().ConfigureAwait(true) == BrowserSessionResult.SignedIn)
            {
                // An already-valid session skips the page entirely, so the pending-load state that
                // StartInteractiveLogin optimistically set has to be taken back.
                _loginPageTimeout.Stop();
                ViewModel.IsBrowserPageLoading = false;
                return;
            }

            if (attempt != _loginAttempt)
                return;

            ViewModel.Status = Localizer.Get("Tracker_BrowserLoginStatus", "Sign in and complete the captcha below.");
            RestartLoginPageTimeout();
            // CoreWebView2.Navigate also reloads when the requested address is already
            // assigned to Source. This is important after sign-out: the old document
            // can still be the signed-in page even though its cookies were removed.
            webView.CoreWebView2.Navigate(loginPage.AbsoluteUri);
        }
        catch (Exception exception)
        {
            if (attempt == _loginAttempt)
                ViewModel.ReportBrowserPageFailed(exception.Message);
        }
        finally
        {
            if (attempt == _loginAttempt)
                _openingBrowser = false;
        }
    }

    // The proxy is applied through Chromium's --proxy-server argument, which can only be set while
    // the WebView2 environment is built, so switching it means a brand new control.
    private async Task<WebView2?> EnsureLoginWebViewAsync(int attempt)
    {
        var proxy = ViewModel.BrowserProxy;
        if (_loginWebView is { CoreWebView2: not null } existing && _loginWebViewProxy == proxy)
            return existing;

        DisposeLoginWebView();

        var webView = new WebView2();
        webView.NavigationStarting += TrackerLoginWebView_NavigationStarting;
        webView.NavigationCompleted += TrackerLoginWebView_NavigationCompleted;
        TrackerLoginWebViewHost.Child = webView;

        // Without a proxy the control keeps WebView2's own default environment - the arrangement
        // that works across every packaging layout. A custom environment (and the writable user
        // data folder it needs) is only built when the proxy actually has to be injected.
        var environment = proxy is null ? null : await TryCreateProxyEnvironmentAsync(proxy);
        if (environment is null)
            await webView.EnsureCoreWebView2Async();
        else
            await webView.EnsureCoreWebView2Async(environment);

        if (attempt != _loginAttempt)
        {
            // A newer attempt already replaced this control while it was being built.
            try { webView.Close(); }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException) { }
            return null;
        }

        _loginWebView = webView;
        _loginWebViewProxy = environment is null ? null : proxy;
        return webView;
    }

    private async Task<CoreWebView2Environment?> TryCreateProxyEnvironmentAsync(Uri proxy)
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = $"--proxy-server=\"{proxy.Scheme}://{proxy.Host}:{proxy.Port}\""
            };
            // The proxied browser needs a user data folder of its own: WebView2 refuses a folder
            // that is already in use with different browser arguments.
            var userDataFolder = Path.Combine(AppPaths.Root, "TrackerLoginProxy");
            Directory.CreateDirectory(userDataFolder);
            return await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null,
                userDataFolder,
                options);
        }
        catch (Exception exception)
        {
            // Falling back to the direct route beats a dead sign-in panel; the message explains why
            // the switch appears to have had no effect.
            ViewModel.ErrorMessage = string.Format(
                Localizer.Get("Tracker_BrowserProxyUnavailable", "The tracker proxy could not be applied to the sign-in browser: {0}"),
                exception.Message);
            return null;
        }
    }

    private void RestartLoginPageTimeout()
    {
        _loginPageTimeout.Stop();
        ViewModel.IsBrowserPageLoading = true;
        _loginPageTimeout.Start();
    }

    private void DisposeLoginWebView()
    {
        // Cleared unconditionally: a previous attempt may have parented a control without ever
        // getting far enough to record it.
        TrackerLoginWebViewHost.Child = null;
        if (_loginWebView is null)
            return;

        _loginWebView.NavigationStarting -= TrackerLoginWebView_NavigationStarting;
        _loginWebView.NavigationCompleted -= TrackerLoginWebView_NavigationCompleted;
        try { _loginWebView.Close(); }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException) { }
        _loginWebView = null;
        _loginWebViewProxy = null;
    }

    private async void Query_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
            await ViewModel.SearchAsync();
    }

    private async void Download_Click(object sender, RoutedEventArgs e) => await OpenAddTorrentWindowForSelectedAsync();
    private async void TrackerResultsTable_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => await OpenAddTorrentWindowForSelectedAsync();

    private void TrackerResultsTable_Sorting(object sender, WinUI.TableView.TableViewSortingEventArgs e)
    {
        // Sorting reorders the results in place; a user scrolled halfway down would otherwise be
        // left in the middle of the newly ordered list. Bring them back to the top once the sort
        // has re-laid out the rows (hence the queued, low-priority scroll).
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (TrackerResultsTable.Items.Count > 0)
                TrackerResultsTable.ScrollRowIntoView(0);
        });
    }
    private async void OpenTopic_Click(object sender, RoutedEventArgs e) => await ViewModel.OpenSelectedAsync();

    private async Task OpenAddTorrentWindowForSelectedAsync()
    {
        var request = await ViewModel.PrepareSelectedDownloadAsync().ConfigureAwait(true);
        if (request is null)
            return;

        var window = new AddTorrentWindow(
            request.TorrentFile is null ? [] : [request.TorrentFile],
            request.MagnetUri is null ? [] : [request.MagnetUri]);
        if (request.TorrentFile is { } torrentFile)
        {
            window.Closed += (_, _) =>
            {
                try { File.Delete(torrentFile); }
                catch { }
            };
        }
        window.Activate();
    }

    private void TrackerBrowserBack_Click(object sender, RoutedEventArgs e)
    {
        _loginWebView?.CoreWebView2?.Stop();
        ViewModel.CancelInteractiveLogin();
    }

    private async void TrackerBrowserComplete_Click(object sender, RoutedEventArgs e)
    {
        if (await TryCompleteBrowserSessionAsync().ConfigureAwait(true) != BrowserSessionResult.SignedIn)
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
            var webView = await EnsureLoginWebViewAsync(++_loginAttempt).ConfigureAwait(true);
            webView?.CoreWebView2.CookieManager.DeleteAllCookies();
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
        {
            // Redirects and form posts start their own navigation, so the wait is timed from here
            // too rather than only from the initial Navigate call.
            RestartLoginPageTimeout();
            return;
        }

        args.Cancel = true;
        // Cancelling raises NavigationCompleted with IsSuccess = false; remember that we caused it,
        // so following an off-limits link is not mistaken for the tracker being unreachable.
        _navigationCancelled = true;
        ViewModel.Status = Localizer.Get(
            "Tracker_BrowserNavigationBlocked",
            "Only the RuTracker sign-in page is available here.");
    }

    private async void TrackerLoginWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        var cancelledByUs = _navigationCancelled;
        _navigationCancelled = false;
        _loginPageTimeout.Stop();
        ViewModel.IsBrowserPageLoading = false;
        if (!args.IsSuccess)
        {
            // A failed navigation leaves the browser showing nothing, so the retry panel takes over
            // rather than leaving the user staring at a blank rectangle.
            if (!cancelledByUs)
            {
                ViewModel.ReportBrowserPageFailed(string.Format(
                    Localizer.Get("Tracker_BrowserNavigationFailed", "The sign-in page could not be loaded: {0}"),
                    args.WebErrorStatus));
            }
            return;
        }

        ViewModel.IsBrowserPageFailed = false;
        var session = await TryCompleteBrowserSessionAsync().ConfigureAwait(true);
        if (session == BrowserSessionResult.SignedIn)
            return;

        // A rejected session re-shows the sign-in page, which lands right back here: stop after a
        // couple of rounds and offer to wipe the stored cookies, since they survive a restart and
        // would otherwise reproduce this on every launch.
        if (session == BrowserSessionResult.Rejected && _rejectedSessions >= MaxRejectedSessions)
        {
            ViewModel.ReportBrowserPageFailed(Localizer.Get(
                "Tracker_BrowserSessionRejected",
                "The tracker did not accept the saved session."));
            return;
        }

        if (sender.Source?.AbsolutePath.Equals("/forum/login.php", StringComparison.OrdinalIgnoreCase) == true)
        {
            await FocusLoginFormAsync().ConfigureAwait(true);
            ViewModel.Status = Localizer.Get("Tracker_WaitingForBrowserLogin", "Waiting for RuTracker sign-in…");
            return;
        }

        var loginPage = ViewModel.StartInteractiveLogin();
        if (loginPage is not null)
        {
            RestartLoginPageTimeout();
            sender.CoreWebView2?.Navigate(loginPage.AbsoluteUri);
        }
    }

    private enum BrowserSessionResult
    {
        /// <summary>No tracker session cookie yet - the user still has to sign in.</summary>
        NotSignedIn,
        SignedIn,
        /// <summary>A session cookie exists but the tracker refused to accept it.</summary>
        Rejected
    }

    private async Task<BrowserSessionResult> TryCompleteBrowserSessionAsync()
    {
        if (_checkingSession || _loginWebView?.CoreWebView2 is null)
            return BrowserSessionResult.NotSignedIn;

        _checkingSession = true;
        try
        {
            var manager = _loginWebView.CoreWebView2.CookieManager;
            var cookieUris = new[]
            {
                "https://rutracker.org/forum/",
                "https://rutracker.net/forum/",
                _loginWebView.Source?.AbsoluteUri
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
            {
                _rejectedSessions = 0;
                return BrowserSessionResult.NotSignedIn;
            }

            var userName = await ReadSignedInUserAsync().ConfigureAwait(true);
            if (await ViewModel.CompleteInteractiveLoginAsync(cookies, userName).ConfigureAwait(true))
            {
                _rejectedSessions = 0;
                return BrowserSessionResult.SignedIn;
            }

            _rejectedSessions++;
            return BrowserSessionResult.Rejected;
        }
        catch (Exception exception)
        {
            ViewModel.ErrorMessage = string.Format(
                Localizer.Get("Tracker_BrowserSessionReadFailed", "Could not check the RuTracker session: {0}"),
                exception.Message);
            return BrowserSessionResult.NotSignedIn;
        }
        finally
        {
            _checkingSession = false;
        }
    }

    private async Task<string?> ReadSignedInUserAsync()
    {
        if (_loginWebView?.CoreWebView2 is null || _loginWebView.Source is null)
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
            var result = await _loginWebView.CoreWebView2.ExecuteScriptAsync(script);
            return JsonSerializer.Deserialize<string>(result);
        }
        catch
        {
            return null;
        }
    }

    private async Task FocusLoginFormAsync()
    {
        if (_loginWebView?.CoreWebView2 is null)
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
            await _loginWebView.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch { }
    }
}
