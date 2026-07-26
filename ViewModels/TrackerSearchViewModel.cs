using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.System;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Core.Services;
using WinBitTorrent.Services;

namespace WinBitTorrent.ViewModels;

public sealed partial class TrackerSearchViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ITrackerCredentialStore _credentialStore;
    private readonly IReadOnlyDictionary<string, ITrackerSearchProvider> _providers;
    private CancellationTokenSource? _searchLifetime;
    private ITrackerSearchProvider? _activeProvider;
    private CatalogTrackerQuery? _pendingCatalogQuery;

    public event Action? BackToCatalogRequested;

    public TrackerSearchViewModel(
        MainViewModel main,
        IEnumerable<ITrackerSearchProvider> providers,
        ITrackerCredentialStore credentialStore)
    {
        _main = main;
        _credentialStore = credentialStore;
        _providers = providers.ToDictionary(static provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        Trackers = new ObservableCollection<TrackerCardViewModel>(_providers.Values.Select(provider => new TrackerCardViewModel(
            provider.Id,
            provider.DisplayName,
            provider.HomePage.ToString(),
            $"ms-appx:///Assets/Trackers/{provider.Id}.png")));
    }

    public ObservableCollection<TrackerCardViewModel> Trackers { get; }
    public ObservableCollection<TrackerResultViewModel> Results { get; } = [];

    [ObservableProperty]
    private bool _isCatalogVisible;

    [ObservableProperty]
    private bool _isPickerVisible = true;

    [ObservableProperty]
    private bool _isSearchVisible;

    [ObservableProperty]
    private bool _isBrowserLoginVisible;

    // Set when the embedded browser could not load the tracker at all (regional blocking, DNS, no
    // network). The sign-in page is blank in that state, so the UI offers a retry instead of the
    // "check sign-in" action, which could only ever fail.
    [ObservableProperty]
    private bool _isBrowserPageFailed;

    // A blocked tracker often does not fail outright - the request just hangs. Surfacing the wait
    // keeps the panel from looking like an empty rectangle the user can only escape by restarting.
    [ObservableProperty]
    private bool _isBrowserPageLoading;

    [ObservableProperty]
    private string _browserFailureDetail = string.Empty;

    // Nothing can be checked while the page is blank, so the action that would only ever fail is
    // taken out of the way.
    public bool CanCheckBrowserSignIn => !IsBrowserPageFailed && !IsBrowserPageLoading;

    partial void OnIsBrowserPageFailedChanged(bool value) => OnPropertyChanged(nameof(CanCheckBrowserSignIn));

    partial void OnIsBrowserPageLoadingChanged(bool value) => OnPropertyChanged(nameof(CanCheckBrowserSignIn));

    [ObservableProperty]
    private bool _isSearchOriginatedFromCatalog;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasProxyOption;

    [ObservableProperty]
    private bool _hasInteractiveLogin;

    [ObservableProperty]
    private bool _requiresSignIn = true;

    [ObservableProperty]
    private bool _useTrackerProxy;

    [ObservableProperty]
    private string _trackerProxyDescription = string.Empty;

    [ObservableProperty]
    private string _activeTrackerName = string.Empty;

    [ObservableProperty]
    private string _activeLogoUri = string.Empty;

    [ObservableProperty]
    private string _signedInUser = string.Empty;

    [ObservableProperty]
    private bool _isSignedIn;

    // Anonymous trackers are always "signed in", so they must not offer a sign-out action.
    public bool CanSignOut => IsSignedIn && RequiresSignIn;

    partial void OnIsSignedInChanged(bool value) => OnPropertyChanged(nameof(CanSignOut));

    partial void OnRequiresSignInChanged(bool value) => OnPropertyChanged(nameof(CanSignOut));

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private TrackerResultViewModel? _selectedResult;

    public Task SelectTrackerAsync(string trackerId)
    {
        if (!_providers.TryGetValue(trackerId, out var provider))
            return Task.CompletedTask;

        var resumeSignedInSession = IsSignedIn &&
            _activeProvider?.Id.Equals(provider.Id, StringComparison.OrdinalIgnoreCase) == true;

        IsSearchOriginatedFromCatalog = false;
        _activeProvider = provider;
        HasInteractiveLogin = provider is ITrackerInteractiveAuthentication;
        RequiresSignIn = provider is not ITrackerAnonymousAccess;
        ActiveTrackerName = provider.DisplayName;
        ActiveLogoUri = $"ms-appx:///Assets/Trackers/{provider.Id}.png";
        if (provider is ITrackerProxyOptions proxyOptions)
        {
            HasProxyOption = true;
            TrackerProxyDescription = proxyOptions.BuiltInProxyDescription;
            UseTrackerProxy = ClientSettings.Get($"trackers.{provider.Id}.useBuiltInProxy", false);
            proxyOptions.UseBuiltInProxy = UseTrackerProxy;
        }
        else
        {
            HasProxyOption = false;
            TrackerProxyDescription = string.Empty;
            UseTrackerProxy = false;
        }
        ErrorMessage = string.Empty;
        if (!RequiresSignIn)
        {
            SignedInUser = string.Empty;
            IsSignedIn = true;
            Results.Clear();
            SelectedResult = null;
            Status = Localizer.Get("Tracker_Ready", "Ready");
            ShowSearch();
        }
        else if (resumeSignedInSession)
        {
            Status = Localizer.Get("Tracker_Ready", "Ready");
            ShowSearch();
        }
        else
        {
            IsSignedIn = false;
            Status = string.Empty;
            ShowInteractiveLogin();
        }
        return Task.CompletedTask;
    }

    [RelayCommand]
    public void BackToTrackers()
    {
        _searchLifetime?.Cancel();
        ErrorMessage = string.Empty;
        Status = string.Empty;
        IsSearchOriginatedFromCatalog = false;
        IsCatalogVisible = false;
        IsPickerVisible = true;
        IsSearchVisible = false;
        IsBrowserLoginVisible = false;
    }

    [RelayCommand]
    public void ShowCatalog()
    {
        _searchLifetime?.Cancel();
        ErrorMessage = string.Empty;
        Status = string.Empty;
        IsSearchOriginatedFromCatalog = false;
        IsCatalogVisible = true;
        IsPickerVisible = false;
        IsSearchVisible = false;
        IsBrowserLoginVisible = false;
    }

    [RelayCommand]
    public void BackToCatalog()
    {
        ShowCatalog();
        BackToCatalogRequested?.Invoke();
    }

    public async Task SearchForCatalogTitleAsync(
        CatalogTrackerQuery query,
        string? trackerId = null,
        CancellationToken cancellationToken = default)
    {
        var provider = trackerId is not null && _providers.TryGetValue(trackerId, out var requested)
            ? requested
            : _activeProvider ?? _providers.Values.FirstOrDefault();
        if (provider is null)
            return;

        _pendingCatalogQuery = query;
        if (_activeProvider?.Id != provider.Id || !IsSignedIn)
            await SelectTrackerAsync(provider.Id);

        if (!IsSignedIn)
            return;

        ShowSearch();
        await RunPendingCatalogQueryAsync();
    }

    private async Task RunPendingCatalogQueryAsync()
    {
        if (_pendingCatalogQuery is not { } pending)
            return;

        _pendingCatalogQuery = null;
        Query = TrackerQueryBuilder.Build(pending, Localizer.Get("Tracker_SeasonQuerySuffix", "season {0}"));
        IsSearchOriginatedFromCatalog = true;
        await SearchAsync();
    }

    public Uri? StartInteractiveLogin()
    {
        if (_activeProvider is not ITrackerInteractiveAuthentication interactive)
            return null;

        ErrorMessage = string.Empty;
        IsBrowserPageFailed = false;
        IsBrowserPageLoading = true;
        BrowserFailureDetail = string.Empty;
        Status = Localizer.Get("Tracker_BrowserLoginStatus", "Complete sign-in and any captcha in the browser below.");
        IsCatalogVisible = false;
        IsPickerVisible = false;
        IsSearchVisible = false;
        IsBrowserLoginVisible = true;
        return interactive.LoginPage;
    }

    // The proxy that the embedded sign-in browser must be routed through, or null when the active
    // tracker has no built-in proxy or the user left it switched off.
    public Uri? BrowserProxy => UseTrackerProxy && _activeProvider is ITrackerProxyOptions proxyOptions
        ? proxyOptions.BuiltInProxyAddress
        : null;

    public void ReportBrowserPageFailed(string detail)
    {
        BrowserFailureDetail = detail;
        IsBrowserPageLoading = false;
        IsBrowserPageFailed = true;
        Status = string.Empty;
    }

    public void CancelInteractiveLogin()
    {
        BackToTrackers();
    }

    public async Task<bool> CompleteInteractiveLoginAsync(IReadOnlyCollection<Cookie> cookies, string? signedInUser = null)
    {
        if (_activeProvider is not ITrackerInteractiveAuthentication interactive || IsBusy)
            return false;

        IsBusy = true;
        ErrorMessage = string.Empty;
        Status = Localizer.Get("Tracker_ImportingBrowserSession", "Checking the RuTracker browser session…");
        try
        {
            await interactive.ImportSessionCookiesAsync(cookies);
            SignedInUser = string.IsNullOrWhiteSpace(signedInUser) ? _activeProvider.DisplayName : signedInUser.Trim();
            IsSignedIn = true;
            Results.Clear();
            SelectedResult = null;
            Status = Localizer.Get("Tracker_Ready", "Ready");
            ShowSearch();
            await RunPendingCatalogQueryAsync();
            return true;
        }
        catch (Exception exception)
        {
            HandleProviderException(exception);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        if (_activeProvider is null || IsBusy || string.IsNullOrWhiteSpace(Query))
            return;

        _searchLifetime?.Cancel();
        _searchLifetime?.Dispose();
        _searchLifetime = new CancellationTokenSource();
        var token = _searchLifetime.Token;
        ErrorMessage = string.Empty;
        IsBusy = true;
        Status = string.Format(Localizer.Get("Tracker_SearchingNamed", "Searching {0}…"), ActiveTrackerName);
        Results.Clear();
        SelectedResult = null;
        try
        {
            var results = await _activeProvider.SearchAsync(Query.Trim(), token);
            foreach (var result in results)
                Results.Add(new TrackerResultViewModel(result));
            Status = string.Format(
                Localizer.Get("Tracker_ResultCount", "Results: {0}"),
                Results.Count);
        }
        catch (OperationCanceledException)
        {
            Status = Localizer.Get("Search_Stopped", "Stopped");
        }
        catch (Exception exception)
        {
            HandleProviderException(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void StopSearch()
    {
        _searchLifetime?.Cancel();
        IsBusy = false;
    }

    public async Task SignOutAsync()
    {
        if (_activeProvider is null)
            return;

        _searchLifetime?.Cancel();
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await _credentialStore.DeleteAsync(_activeProvider.Id);
            if (_activeProvider is ITrackerSessionControl sessionControl)
                await sessionControl.SignOutAsync();

            Results.Clear();
            SelectedResult = null;
            Query = string.Empty;
            SignedInUser = string.Empty;
            IsSignedIn = false;
            Status = Localizer.Get("Tracker_SignedOut", "Signed out. Sign in with another account.");
            ShowInteractiveLogin();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            Status = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<TrackerAddRequest?> PrepareSelectedDownloadAsync()
    {
        if (_activeProvider is null || SelectedResult is null || IsBusy)
            return null;

        if (_main.Api is null)
        {
            ErrorMessage = Localizer.Get("Tracker_QBittorrentRequired", "Connect to qBittorrent before downloading.");
            return null;
        }

        // Magnet-only trackers (The Pirate Bay) never expose a .torrent payload to fetch.
        if (SelectedResult.MagnetUri is { } magnet)
        {
            ErrorMessage = string.Empty;
            Status = Localizer.Get("Tracker_MagnetReady", "Magnet link ready. Review add options.");
            return new TrackerAddRequest(null, magnet.AbsoluteUri);
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        Status = Localizer.Get("Tracker_Downloading", "Downloading torrent file…");
        try
        {
            var bytes = await _activeProvider.DownloadTorrentAsync(SelectedResult.Id);
            var tempFile = Path.Combine(Path.GetTempPath(), $"WinBitTorrent-{_activeProvider.Id}-{SelectedResult.Id}-{Guid.NewGuid():N}.torrent");
            await File.WriteAllBytesAsync(tempFile, bytes);
            Status = Localizer.Get("Tracker_AddWindowReady", "Torrent file downloaded. Review add options.");
            return new TrackerAddRequest(tempFile, null);
        }
        catch (Exception exception)
        {
            HandleProviderException(exception);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task OpenSelectedAsync()
    {
        if (SelectedResult is not null)
            await Launcher.LaunchUriAsync(SelectedResult.DetailsUri);
    }

    // Raised every time the app wants the sign-in page on screen. It is an event rather than a
    // property change because the panel is often already visible - an expired session re-shows it
    // while IsBrowserLoginVisible never flips, and relying on the flag left the browser stranded on
    // a stale page with no way to reload it.
    public event Action? InteractiveLoginRequested;

    private void ShowInteractiveLogin()
    {
        IsCatalogVisible = false;
        IsPickerVisible = false;
        IsSearchVisible = false;
        IsBrowserLoginVisible = true;
        InteractiveLoginRequested?.Invoke();
    }

    partial void OnUseTrackerProxyChanged(bool value)
    {
        if (_activeProvider is ITrackerProxyOptions proxyOptions)
        {
            proxyOptions.UseBuiltInProxy = value;
            ClientSettings.SetValue($"trackers.{_activeProvider.Id}.useBuiltInProxy", value);
        }

        OnPropertyChanged(nameof(BrowserProxy));
    }

    private void ShowSearch()
    {
        IsCatalogVisible = false;
        IsPickerVisible = false;
        IsSearchVisible = true;
        IsBrowserLoginVisible = false;
    }

    private void HandleProviderException(Exception exception)
    {
        if (exception is TrackerAuthenticationException && HasInteractiveLogin)
        {
            ErrorMessage = Localizer.Get("Tracker_SessionExpired", "The RuTracker session has expired. Sign in again.");
            Status = string.Empty;
            SignedInUser = string.Empty;
            IsSignedIn = false;
            ShowInteractiveLogin();
            return;
        }

        ErrorMessage = exception.Message;
        Status = string.Empty;
    }
}

public sealed record TrackerCardViewModel(string Id, string Name, string HomePage, string LogoUri)
{
    public override string ToString() => Name;
}

// Exactly one of the two is set: a downloaded .torrent file, or a magnet link.
public sealed record TrackerAddRequest(string? TorrentFile, string? MagnetUri);

public sealed record TrackerResultViewModel
{
    public TrackerResultViewModel(TrackerSearchResult result)
    {
        Id = result.Id;
        Name = result.Title;
        Size = ValueFormatter.Size(result.Size);
        Seeds = result.Seeds;
        Leechers = result.Leechers;
        Published = result.PublishedAt?.ToLocalTime().ToString("g") ?? string.Empty;
        DetailsUri = result.DetailsUri;
        MagnetUri = result.MagnetUri;
    }

    public string Id { get; }
    public string Name { get; }
    public string Size { get; }
    public int Seeds { get; }
    public int Leechers { get; }
    public string Published { get; }
    public Uri DetailsUri { get; }
    public Uri? MagnetUri { get; }

    // The results grid falls back to ToString() for the row's automation name, and the record
    // default dumps every property at a screen reader.
    public override string ToString() => Name;
}
