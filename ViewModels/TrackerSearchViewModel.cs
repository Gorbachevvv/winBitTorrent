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
    private bool _isPickerVisible = true;

    [ObservableProperty]
    private bool _isSearchVisible;

    [ObservableProperty]
    private bool _isBrowserLoginVisible;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasProxyOption;

    [ObservableProperty]
    private bool _hasInteractiveLogin;

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

        _activeProvider = provider;
        HasInteractiveLogin = provider is ITrackerInteractiveAuthentication;
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
        Status = string.Empty;
        ShowInteractiveLogin();
        return Task.CompletedTask;
    }

    [RelayCommand]
    public void BackToTrackers()
    {
        _searchLifetime?.Cancel();
        ErrorMessage = string.Empty;
        Status = string.Empty;
        IsPickerVisible = true;
        IsSearchVisible = false;
        IsBrowserLoginVisible = false;
    }

    public Uri? StartInteractiveLogin()
    {
        if (_activeProvider is not ITrackerInteractiveAuthentication interactive)
            return null;

        ErrorMessage = string.Empty;
        Status = Localizer.Get("Tracker_BrowserLoginStatus", "Complete sign-in and any captcha in the browser below.");
        IsPickerVisible = false;
        IsSearchVisible = false;
        IsBrowserLoginVisible = true;
        return interactive.LoginPage;
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
        Status = Localizer.Get("Tracker_Searching", "Searching RuTracker…");
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

    public async Task<string?> DownloadSelectedTorrentFileAsync()
    {
        if (_activeProvider is null || SelectedResult is null || IsBusy)
            return null;

        if (_main.Api is null)
        {
            ErrorMessage = Localizer.Get("Tracker_QBittorrentRequired", "Connect to qBittorrent before downloading.");
            return null;
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
            return tempFile;
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

    private void ShowInteractiveLogin()
    {
        IsPickerVisible = false;
        IsSearchVisible = false;
        IsBrowserLoginVisible = true;
    }

    partial void OnUseTrackerProxyChanged(bool value)
    {
        if (_activeProvider is not ITrackerProxyOptions proxyOptions)
            return;

        proxyOptions.UseBuiltInProxy = value;
        ClientSettings.SetValue($"trackers.{_activeProvider.Id}.useBuiltInProxy", value);
    }

    private void ShowSearch()
    {
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

public sealed record TrackerCardViewModel(string Id, string Name, string HomePage, string LogoUri);

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
    }

    public string Id { get; }
    public string Name { get; }
    public string Size { get; }
    public int Seeds { get; }
    public int Leechers { get; }
    public string Published { get; }
    public Uri DetailsUri { get; }
}
