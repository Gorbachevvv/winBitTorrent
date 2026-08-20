using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Core.Services;
using WinBitTorrent.Services;

namespace WinBitTorrent.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private readonly IConnectionCoordinator _connection;
    private readonly IManagedBackendHost _backend;
    private readonly IServerProfileStore _profileStore;
    private readonly IAppNotificationService _notifications;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly MainDataAccumulator _mainData = new();
    private readonly TorrentLifecycleMonitor _torrentLifecycle = new();
    private readonly Dictionary<string, TorrentRowViewModel> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PeerRowViewModel> _peers = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _detailsLifetime;
    private ITorrentBackendClient? _api;
    private string? _peerHash;
    private int _peerResponseId;
    private int _visualizationRefreshTick;

    public MainViewModel(
        IConnectionCoordinator connection,
        IManagedBackendHost backend,
        IServerProfileStore profileStore,
        IAppNotificationService notifications,
        ILogger<MainViewModel> logger)
    {
        _connection = connection;
        _backend = backend;
        _profileStore = profileStore;
        _notifications = notifications;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        StatusFilters =
        [
            new(TorrentFilterKind.Status, "all", Localizer.Get("Filter_All", "All"), "\uE8A5"),
            new(TorrentFilterKind.Status, "downloading", Localizer.Get("Filter_Downloading", "Downloading"), "\uE896"),
            new(TorrentFilterKind.Status, "seeding", Localizer.Get("Filter_Seeding", "Seeding"), "\uE898"),
            new(TorrentFilterKind.Status, "completed", Localizer.Get("Filter_Completed", "Completed"), "\uE73E"),
            new(TorrentFilterKind.Status, "stopped", Localizer.Get("Filter_Stopped", "Stopped"), "\uE71A"),
            new(TorrentFilterKind.Status, "active", Localizer.Get("Filter_Active", "Active"), "\uE768"),
            new(TorrentFilterKind.Status, "inactive", Localizer.Get("Filter_Inactive", "Inactive"), "\uE769"),
            new(TorrentFilterKind.Status, "stalled", Localizer.Get("Filter_Stalled", "Stalled"), "\uE7BA"),
            new(TorrentFilterKind.Status, "errored", Localizer.Get("Filter_Errored", "Errored"), "\uEA39")
        ];
        SelectedFilter = StatusFilters[0];
    }

    /// <summary>All categories currently known to the server, keyed by name (not just ones with torrents assigned).</summary>
    public IReadOnlyDictionary<string, TorrentCategory> Categories => _mainData.Categories;

    public ObservableCollection<TorrentRowViewModel> Torrents { get; } = [];
    public ObservableCollection<FilterItemViewModel> StatusFilters { get; }
    public ObservableCollection<FilterItemViewModel> CategoryFilters { get; } = [];
    public ObservableCollection<FilterItemViewModel> TagFilters { get; } = [];
    public ObservableCollection<FilterItemViewModel> TrackerFilters { get; } = [];
    public ObservableCollection<ServerProfile> Profiles { get; } = [];
    public ObservableCollection<TorrentTracker> SelectedTrackers { get; } = [];
    public ObservableCollection<string> SelectedWebSeeds { get; } = [];
    public ObservableCollection<TorrentFile> SelectedFiles { get; } = [];
    public ObservableCollection<PeerRowViewModel> SelectedPeers { get; } = [];

    [ObservableProperty]
    private string _connectionStatus = Localizer.Get("Connection_Starting", "Starting…");

    [ObservableProperty]
    private string _connectionDetails = string.Empty;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private FilterItemViewModel? _selectedFilter;

    [ObservableProperty]
    private TorrentRowViewModel? _selectedTorrent;

    [ObservableProperty]
    private TorrentProperties? _selectedProperties;

    [ObservableProperty]
    private IReadOnlyList<int> _selectedPieceStates = [];

    [ObservableProperty]
    private IReadOnlyList<TorrentAvailabilitySegment> _selectedAvailabilitySegments = [];

    [ObservableProperty]
    private ServerProfile? _selectedProfile;

    [ObservableProperty]
    private string _downloadSpeed = "0 B/s";

    [ObservableProperty]
    private string _uploadSpeed = "0 B/s";

    [ObservableProperty]
    private string _freeSpace = "—";

    [ObservableProperty]
    private string _dhtNodes = "DHT: —";

    [ObservableProperty]
    private bool _useAlternativeSpeedLimits;

    [ObservableProperty]
    private bool _queueingEnabled;

    public IReadOnlyList<TorrentRowViewModel> SelectedTorrents { get; set; } = [];
    public bool HasActiveTorrents => _rows.Values.Any(static torrent => torrent.IsActive);
    public ITorrentBackendClient? Api => _api;
    public string SelectedHashes => GetSelectedHashes();
    public bool CanUseLocalFiles => _api?.Capabilities.HasFlag(BackendCapabilities.LocalFileSystem) == true;
    public bool HasSelectedTorrent => SelectedTorrent is not null;

    public async Task InitializeAsync()
    {
        if (_lifetime is not null)
            return;

        _lifetime = new CancellationTokenSource();
        _connection.StateChanged += OnConnectionStateChanged;

        var profiles = await _profileStore.GetAllAsync(_lifetime.Token);
        Profiles.Clear();
        foreach (var profile in profiles)
            Profiles.Add(profile);

        SelectedProfile = await _profileStore.GetSelectedAsync(_lifetime.Token) ?? profiles.First();
        await ConnectSelectedProfileAsync();
    }

    [RelayCommand]
    public async Task ConnectSelectedProfileAsync()
    {
        if (SelectedProfile is null || _lifetime is null)
            return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            _mainData.Reset();
            _torrentLifecycle.Reset();
            _rows.Clear();
            Torrents.Clear();
            _api = await _connection.ConnectAsync(SelectedProfile, _lifetime.Token);
            // ConnectionState.Connected can be dispatched just before ConnectAsync returns. In
            // that narrow window IsConnected becomes true while Api is still null, so consumers
            // waiting for IsConnected (notably AddTorrentWindow during file activation) miss the
            // moment the usable API actually becomes available. Notify it explicitly.
            OnPropertyChanged(nameof(Api));
            await _profileStore.SelectAsync(SelectedProfile.Id, _lifetime.Token);
            IsConnected = true;
            await ShowPendingMigrationReportAsync(_api, _lifetime.Token);
            _ = RunSyncLoopAsync(_lifetime.Token);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            ConnectionStatus = Localizer.Get("Connection_Failed", "Connection failed");
            IsConnected = false;
            _logger.LogError(exception, "Unable to initialize torrent backend connection.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AddAsync(IReadOnlyList<string> torrentFiles, IReadOnlyList<string> urls, TorrentAddRequest? options = null)
    {
        EnsureApi();
        options ??= new TorrentAddRequest(urls, torrentFiles);
        try
        {
            await _api!.Torrents.AddAsync(options, _lifetime?.Token ?? default);
            await RefreshNowAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notifications.ShowTorrentAddFailed(exception.Message);
            throw;
        }
    }

    public TorrentRowViewModel? FindDuplicateTorrent(IEnumerable<string> hashes)
    {
        var candidates = hashes as IReadOnlyCollection<string> ?? hashes.ToArray();
        return candidates.Count == 0
            ? null
            : _rows.Values.FirstOrDefault(row => TorrentIdentity.Matches(row.Model, candidates));
    }

    [RelayCommand]
    public Task StartSelectedAsync() => ExecuteSelectedAsync(TorrentCommand.Start);

    [RelayCommand]
    public Task StopSelectedAsync() => ExecuteSelectedAsync(TorrentCommand.Stop);

    public async Task ExecuteAllAsync(TorrentCommand command)
    {
        EnsureApi();
        await _api!.Torrents.ExecuteAsync(command, "all", _lifetime?.Token ?? default);
        await RefreshNowAsync();
    }

    [RelayCommand]
    public Task RecheckSelectedAsync() => ExecuteSelectedAsync(TorrentCommand.Recheck);

    [RelayCommand]
    public Task ReannounceSelectedAsync() => ExecuteSelectedAsync(TorrentCommand.Reannounce);

    [RelayCommand]
    public Task MoveUpSelectedAsync() => ExecuteSelectedAsync(TorrentCommand.IncreasePriority);

    [RelayCommand]
    public Task MoveDownSelectedAsync() => ExecuteSelectedAsync(TorrentCommand.DecreasePriority);

    public async Task DeleteSelectedAsync(bool deleteFiles)
    {
        EnsureApi();
        var hashes = GetSelectedHashes();
        if (string.IsNullOrEmpty(hashes))
            return;
        await _api!.Torrents.DeleteAsync(hashes, deleteFiles, _lifetime?.Token ?? default);
        await RefreshNowAsync();
    }

    private async Task ExecuteSelectedMutationAsync(Func<ITorrentsApi, string, CancellationToken, Task> mutation)
    {
        EnsureApi();
        var hashes = GetSelectedHashes();
        if (string.IsNullOrEmpty(hashes))
            return;
        await mutation(_api!.Torrents, hashes, _lifetime?.Token ?? default);
        await RefreshNowAsync();
    }

    public async Task ToggleSequentialSelectedAsync()
    {
        var targets = CaptureFlagTargets(static row => !row.Model.SequentialDownload);
        await ExecuteSelectedAsync(TorrentCommand.ToggleSequentialDownload);
        foreach (var (row, value) in targets)
            row.ApplySequentialDownload(value);
    }

    public async Task ToggleFirstLastSelectedAsync()
    {
        var targets = CaptureFlagTargets(static row => !row.Model.FirstLastPiecePriority);
        await ExecuteSelectedAsync(TorrentCommand.ToggleFirstLastPiecePriority);
        foreach (var (row, value) in targets)
            row.ApplyFirstLastPiecePriority(value);
    }

    public Task MoveTopSelectedAsync() => ExecuteSelectedAsync(TorrentCommand.TopPriority);
    public Task MoveBottomSelectedAsync() => ExecuteSelectedAsync(TorrentCommand.BottomPriority);

    public async Task SetForceStartSelectedAsync(bool enabled)
    {
        var targets = CaptureFlagTargets(_ => enabled);
        await ExecuteSelectedMutationAsync((api, hashes, token) => api.SetForceStartAsync(hashes, enabled, token));
        foreach (var (row, value) in targets)
            row.ApplyForceStart(value);
    }

    // Snapshots the effective selection together with the flag value each torrent should end up
    // with, so the optimistic local update after the server call matches the same rows the action
    // targeted.
    private List<(TorrentRowViewModel Row, bool Value)> CaptureFlagTargets(Func<TorrentRowViewModel, bool> selector)
    {
        var rows = SelectedTorrents.Count > 0
            ? (IReadOnlyList<TorrentRowViewModel>)SelectedTorrents
            : SelectedTorrent is null ? [] : [SelectedTorrent];
        return rows.Select(row => (row, selector(row))).ToList();
    }

    public async Task SetSuperSeedingSelectedAsync(bool enabled)
    {
        var targets = CaptureFlagTargets(_ => enabled);
        await ExecuteSelectedMutationAsync((api, hashes, token) => api.SetSuperSeedingAsync(hashes, enabled, token));
        foreach (var (row, value) in targets)
            row.ApplySuperSeeding(value);
    }

    public Task SetCategorySelectedAsync(string category)
        => ExecuteSelectedMutationAsync((api, hashes, token) => api.SetCategoryAsync(hashes, category, token));

    public Task AddTagsSelectedAsync(string tags)
        => ExecuteSelectedMutationAsync((api, hashes, token) => api.AddTagsAsync(hashes, tags, token));

    public Task RemoveTagsSelectedAsync(string tags)
        => ExecuteSelectedMutationAsync((api, hashes, token) => api.RemoveTagsAsync(hashes, tags, token));

    public Task SetLocationSelectedAsync(string location)
        => ExecuteSelectedMutationAsync((api, hashes, token) => api.SetLocationAsync(hashes, location, token));

    public Task SetDownloadLimitSelectedAsync(long limit)
        => ExecuteSelectedMutationAsync((api, hashes, token) => api.SetDownloadLimitAsync(hashes, limit, token));

    public Task SetUploadLimitSelectedAsync(long limit)
        => ExecuteSelectedMutationAsync((api, hashes, token) => api.SetUploadLimitAsync(hashes, limit, token));

    public Task SetShareLimitsSelectedAsync(double ratioLimit, int seedingTimeLimit, int inactiveSeedingTimeLimit)
        => ExecuteSelectedMutationAsync((api, hashes, token) => api.SetShareLimitsAsync(hashes, ratioLimit, seedingTimeLimit, inactiveSeedingTimeLimit, token));

    public async Task RenameSelectedAsync(string name)
    {
        EnsureApi();
        if (SelectedTorrent is null)
            return;
        await _api!.Torrents.RenameAsync(SelectedTorrent.Hash, name, _lifetime?.Token ?? default);
        await RefreshNowAsync();
    }

    public async Task<byte[]> ExportSelectedAsync()
    {
        EnsureApi();
        if (SelectedTorrent is null)
            throw new InvalidOperationException("Select a torrent to export.");
        return await _api!.Torrents.ExportAsync(SelectedTorrent.Hash, _lifetime?.Token ?? default);
    }

    [RelayCommand]
    public async Task ToggleAlternativeSpeedLimitsAsync()
    {
        EnsureApi();
        await _api!.Transfer.SetAlternativeSpeedLimitsAsync(!UseAlternativeSpeedLimits, _lifetime?.Token ?? default);
        UseAlternativeSpeedLimits = !UseAlternativeSpeedLimits;
    }

    public void SelectFilter(FilterItemViewModel? filter)
    {
        SelectedFilter = filter ?? StatusFilters[0];
        RebuildVisibleRows();
    }

    public void SetSelectedRows(IReadOnlyList<TorrentRowViewModel> rows)
    {
        SelectedTorrents = rows;
        SelectedTorrent = rows.FirstOrDefault();
    }

    partial void OnSelectedTorrentChanged(TorrentRowViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedTorrent));
        _detailsLifetime?.Cancel();
        _detailsLifetime?.Dispose();
        _detailsLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime?.Token ?? default);
        _peerHash = null;
        _peerResponseId = 0;
        _visualizationRefreshTick = 0;
        _peers.Clear();
        SelectedPeers.Clear();
        _ = LoadSelectedDetailsAsync(value, _detailsLifetime.Token);
    }

    partial void OnSearchTextChanged(string value) => RebuildVisibleRows();

    partial void OnSelectedProfileChanged(ServerProfile? value) => OnPropertyChanged(nameof(CanUseLocalFiles));

    private async Task ExecuteSelectedAsync(TorrentCommand command)
    {
        EnsureApi();
        var hashes = GetSelectedHashes();
        if (string.IsNullOrEmpty(hashes))
            return;
        await _api!.Torrents.ExecuteAsync(command, hashes, _lifetime?.Token ?? default);
        await RefreshNowAsync();
    }

    private string GetSelectedHashes()
    {
        var selected = SelectedTorrents.Count > 0
            ? SelectedTorrents
            : SelectedTorrent is null ? [] : [SelectedTorrent];
        return string.Join('|', selected.Select(static torrent => torrent.Hash));
    }

    private async Task RunSyncLoopAsync(CancellationToken cancellationToken)
    {
        var reconnectIndex = 0;
        while (!cancellationToken.IsCancellationRequested && _api is not null)
        {
            try
            {
                await RefreshNowAsync();
                await RefreshSelectedPeersAsync(cancellationToken);
                if (++_visualizationRefreshTick >= 3)
                {
                    _visualizationRefreshTick = 0;
                    await RefreshSelectedVisualizationAsync(cancellationToken);
                }
                reconnectIndex = 0;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException or QbittorrentApiException)
            {
                IsConnected = false;
                ConnectionStatus = Localizer.Get("Connection_Reconnecting", "Reconnecting…");
                ErrorMessage = exception.Message;
                var delay = ReconnectDelays[Math.Min(reconnectIndex++, ReconnectDelays.Length - 1)];
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task RefreshNowAsync()
    {
        if (_api is null)
            return;

        var response = await _api.Sync.GetMainDataAsync(_mainData.ResponseId, _lifetime?.Token ?? default);
        var changeSet = _mainData.Apply(response);
        _notifications.Publish(_torrentLifecycle.Observe(changeSet));
        ApplyChangeSet(changeSet);
        IsConnected = true;
        ConnectionStatus = Localizer.Get("Connection_Connected", "Connected");
        ErrorMessage = string.Empty;
    }

    private void ApplyChangeSet(MainDataChangeSet changeSet)
    {
        if (changeSet.FullUpdate)
        {
            // A full snapshot is not a request to rebuild the UI collection. The local engine
            // currently sends one every second; clearing here destroyed TableView selection,
            // scroll position and row identity and replayed its insertion animation on every
            // poll. Reconcile by infohash so rows that still exist keep the same view-model
            // instance. Connection/profile changes already clear the collection explicitly.
            KeyedSnapshotReconciler.Reconcile(
                _rows,
                changeSet.ChangedTorrents,
                static torrent => torrent.Hash,
                static torrent => new TorrentRowViewModel(torrent),
                static (row, torrent) => row.Update(torrent),
                StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            foreach (var torrent in changeSet.ChangedTorrents)
            {
                if (_rows.TryGetValue(torrent.Hash, out var row))
                    row.Update(torrent);
                else
                    _rows[torrent.Hash] = new TorrentRowViewModel(torrent);
            }
        }

        foreach (var hash in changeSet.RemovedHashes)
            _rows.Remove(hash);

        DownloadSpeed = ValueFormatter.Speed(changeSet.ServerState.DownloadSpeed);
        UploadSpeed = ValueFormatter.Speed(changeSet.ServerState.UploadSpeed);
        FreeSpace = ValueFormatter.Size(changeSet.ServerState.FreeSpaceOnDisk);
        DhtNodes = $"{Localizer.Get("Status_Dht", "DHT")}: {changeSet.ServerState.DhtNodes}";
        UseAlternativeSpeedLimits = changeSet.ServerState.UseAlternativeSpeedLimits;
        QueueingEnabled = changeSet.ServerState.Queueing;

        RebuildFilters();
        RebuildVisibleRows();
    }

    // A NUL key can never collide with a real qBittorrent category name (categories can't
    // contain one), so it's safe as a sentinel for the pinned "All" entry qBittorrent itself
    // shows above the per-category list, counting every torrent regardless of category.
    private const string AllCategoriesKey = "\0";

    private void RebuildFilters()
    {
        foreach (var filter in StatusFilters)
            filter.Count = _rows.Values.Count(row => MatchesStatus(row, filter.Key));

        if (CategoryFilters.Count == 0 || CategoryFilters[0].Key != AllCategoriesKey)
            CategoryFilters.Insert(0, new FilterItemViewModel(TorrentFilterKind.Category, AllCategoriesKey, Localizer.Get("Filter_All", "All"), "\uE71D"));
        CategoryFilters[0].Count = _rows.Count;

        ReplaceFilters(
            CategoryFilters,
            _rows.Values.GroupBy(static row => string.IsNullOrWhiteSpace(row.Model.Category) ? "Uncategorized" : row.Model.Category),
            TorrentFilterKind.Category,
            "\uE8B7",
            offset: 1);

        var tags = _rows.Values
            .SelectMany(static row => row.Model.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase);
        ReplaceFilters(TagFilters, tags, TorrentFilterKind.Tag, "\uE8EC");

        var trackers = _rows.Values
            .Select(static row => TrackerHost(row.Model.Tracker))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase);
        ReplaceFilters(TrackerFilters, trackers, TorrentFilterKind.Tracker, "\uE774");
    }

    // offset lets a caller keep pinned entries at the front of destination (e.g. the "All"
    // category above the per-category list) untouched by this method's own inserts/removals.
    private static void ReplaceFilters<T>(ObservableCollection<FilterItemViewModel> destination, IEnumerable<IGrouping<string, T>> groups, TorrentFilterKind kind, string glyph, int offset = 0)
    {
        var desired = groups
            .OrderBy(static group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new
            {
                group.Key,
                Title = kind == TorrentFilterKind.Category && group.Key == "Uncategorized"
                    ? Localizer.Get("Filter_Uncategorized", "Uncategorized")
                    : group.Key,
                Count = group.Count()
            })
            .ToList();

        for (var index = 0; index < desired.Count; index++)
        {
            var item = desired[index];
            var position = index + offset;
            var existingIndex = IndexOfFilter(destination, item.Key, position);
            if (existingIndex < 0)
            {
                destination.Insert(position, new FilterItemViewModel(kind, item.Key, item.Title, glyph, item.Count));
                continue;
            }

            if (existingIndex != position)
                destination.Move(existingIndex, position);
            destination[position].Count = item.Count;
        }

        while (destination.Count > desired.Count + offset)
            destination.RemoveAt(destination.Count - 1);
    }

    private static int IndexOfFilter(ObservableCollection<FilterItemViewModel> filters, string key, int startIndex)
    {
        for (var index = startIndex; index < filters.Count; index++)
            if (filters[index].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return index;
        return -1;
    }

    private void RebuildVisibleRows()
    {
        var filter = SelectedFilter;
        var desired = _rows.Values
            .Where(row => Matches(row, filter))
            .Where(row => TorrentFilters.MatchesText(row.Model, SearchText))
            .OrderBy(static row => row.QueuePosition < 0 ? int.MaxValue : row.QueuePosition)
            .ThenBy(static row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (Torrents.Count == desired.Count && Torrents.SequenceEqual(desired))
            return;

        for (var index = 0; index < desired.Count; index++)
        {
            var row = desired[index];
            if (index < Torrents.Count && ReferenceEquals(Torrents[index], row))
                continue;
            var existingIndex = Torrents.IndexOf(row);
            if (existingIndex >= 0)
                Torrents.Move(existingIndex, index);
            else
                Torrents.Insert(index, row);
        }
        while (Torrents.Count > desired.Count)
            Torrents.RemoveAt(Torrents.Count - 1);
    }

    private static bool Matches(TorrentRowViewModel row, FilterItemViewModel? filter)
    {
        if (filter is null)
            return true;
        return filter.Kind switch
        {
            TorrentFilterKind.Status => MatchesStatus(row, filter.Key),
            TorrentFilterKind.Category when filter.Key == AllCategoriesKey => true,
            TorrentFilterKind.Category => (string.IsNullOrWhiteSpace(row.Model.Category) ? "Uncategorized" : row.Model.Category)
                .Equals(filter.Key, StringComparison.OrdinalIgnoreCase),
            TorrentFilterKind.Tag => row.Model.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(filter.Key, StringComparer.OrdinalIgnoreCase),
            TorrentFilterKind.Tracker => TrackerHost(row.Model.Tracker).Equals(filter.Key, StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private static bool MatchesStatus(TorrentRowViewModel row, string status)
        => TorrentFilters.MatchesStatus(row.Model, status);

    private static string TrackerHost(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : value;

    private async Task LoadSelectedDetailsAsync(TorrentRowViewModel? selected, CancellationToken cancellationToken)
    {
        SelectedProperties = null;
        SelectedTrackers.Clear();
        SelectedWebSeeds.Clear();
        SelectedFiles.Clear();
        SelectedPieceStates = [];
        SelectedAvailabilitySegments = [];
        if (selected is null || _api is null)
            return;

        try
        {
            var propertiesTask = _api.Torrents.GetPropertiesAsync(selected.Hash, cancellationToken);
            var trackersTask = _api.Torrents.GetTrackersAsync(selected.Hash, cancellationToken);
            var webSeedsTask = _api.Torrents.GetWebSeedsAsync(selected.Hash, cancellationToken);
            var filesTask = _api.Torrents.GetFilesAsync(selected.Hash, cancellationToken);
            var pieceStatesTask = GetPieceStatesOrEmptyAsync(_api, selected.Hash, cancellationToken);
            await Task.WhenAll(propertiesTask, trackersTask, webSeedsTask, filesTask, pieceStatesTask);

            if (!string.Equals(SelectedTorrent?.Hash, selected.Hash, StringComparison.OrdinalIgnoreCase))
                return;

            SelectedProperties = await propertiesTask;
            foreach (var item in await trackersTask)
                SelectedTrackers.Add(item);
            foreach (var item in await webSeedsTask)
                SelectedWebSeeds.Add(item);
            var files = await filesTask;
            foreach (var item in files)
                SelectedFiles.Add(item);
            SelectedPieceStates = (await pieceStatesTask).ToArray();
            SelectedAvailabilitySegments = BuildAvailabilitySegments(files);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Unable to load torrent details for {Hash}.", selected.Hash);
        }
    }

    public async Task RefreshSelectedFilesAsync()
    {
        var selected = SelectedTorrent;
        var api = _api;
        if (selected is null || api is null)
            return;

        var files = await api.Torrents.GetFilesAsync(selected.Hash, _detailsLifetime?.Token ?? default);
        if (!string.Equals(SelectedTorrent?.Hash, selected.Hash, StringComparison.OrdinalIgnoreCase))
            return;

        SelectedAvailabilitySegments = BuildAvailabilitySegments(files);

        var currentByIndex = SelectedFiles.ToDictionary(static file => file.Index);
        if (files.Count == SelectedFiles.Count
            && files.All(file => currentByIndex.TryGetValue(file.Index, out var current)
                && current.Name.Equals(file.Name, StringComparison.Ordinal)))
        {
            foreach (var updated in files)
            {
                var current = currentByIndex[updated.Index];
                current.Size = updated.Size;
                current.Progress = updated.Progress;
                current.Priority = updated.Priority;
                current.IsSeed = updated.IsSeed;
                current.Availability = updated.Availability;
            }
            return;
        }

        SelectedFiles.Clear();
        foreach (var file in files)
            SelectedFiles.Add(file);
    }

    private async Task ShowPendingMigrationReportAsync(ITorrentBackendClient api, CancellationToken cancellationToken)
    {
        if (!api.Capabilities.HasFlag(BackendCapabilities.LocalFileSystem))
            return;
        try
        {
            var report = await api.ClientData.LoadAsync("migration.report", cancellationToken);
            if (report["pending"]?.GetValue<bool>() != true)
                return;
            var needsHashCheck = report["needsHashCheck"] as JsonArray;
            _notifications.ShowMigrationReport(
                report["torrentCount"]?.GetValue<int>() ?? 0,
                needsHashCheck?.Count ?? 0,
                report["backupPath"]?.GetValue<string>() ?? string.Empty);
            report["pending"] = false;
            await api.ClientData.StoreAsync("migration.report", report, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Unable to display the local migration report.");
        }
    }

    private async Task RefreshSelectedVisualizationAsync(CancellationToken cancellationToken)
    {
        var selected = SelectedTorrent;
        var api = _api;
        if (selected is null || api is null)
            return;

        try
        {
            var pieceStatesTask = GetPieceStatesOrEmptyAsync(api, selected.Hash, cancellationToken);
            var filesTask = api.Torrents.GetFilesAsync(selected.Hash, cancellationToken);
            await Task.WhenAll(pieceStatesTask, filesTask);

            if (!string.Equals(SelectedTorrent?.Hash, selected.Hash, StringComparison.OrdinalIgnoreCase))
                return;

            SelectedPieceStates = (await pieceStatesTask).ToArray();
            SelectedAvailabilitySegments = BuildAvailabilitySegments(await filesTask);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or QbittorrentApiException)
        {
            // Piece/file maps are supplementary details. A transient failure must not make the
            // otherwise healthy main synchronization loop reconnect the whole client.
            _logger.LogDebug(exception, "Unable to refresh torrent visualization for {Hash}.", selected.Hash);
        }
    }

    private static IReadOnlyList<TorrentAvailabilitySegment> BuildAvailabilitySegments(
        IEnumerable<TorrentFile> files)
        => files
            .OrderBy(static file => file.Index)
            .Select(static file => new TorrentAvailabilitySegment(file.Size, file.Availability))
            .ToArray();

    private async Task<IReadOnlyList<int>> GetPieceStatesOrEmptyAsync(
        ITorrentBackendClient api,
        string hash,
        CancellationToken cancellationToken)
    {
        try
        {
            return await api.Torrents.GetPieceStatesAsync(hash, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or QbittorrentApiException)
        {
            _logger.LogDebug(exception, "Piece states are unavailable for torrent {Hash}.", hash);
            return [];
        }
    }

    private async Task RefreshSelectedPeersAsync(CancellationToken cancellationToken)
    {
        var selected = SelectedTorrent;
        if (selected is null || _api is null)
            return;
        if (!string.Equals(_peerHash, selected.Hash, StringComparison.OrdinalIgnoreCase))
        {
            _peerHash = selected.Hash;
            _peerResponseId = 0;
            _peers.Clear();
            SelectedPeers.Clear();
        }

        var response = await _api.Sync.GetTorrentPeersAsync(selected.Hash, _peerResponseId, cancellationToken);
        if (!string.Equals(_peerHash, selected.Hash, StringComparison.OrdinalIgnoreCase))
            return;
        var fullUpdate = response["full_update"]?.GetValue<bool>() == true;
        HashSet<string>? fullUpdateIds = fullUpdate ? new(StringComparer.OrdinalIgnoreCase) : null;
        if (response["peers"] is JsonObject peers)
        {
            foreach (var (id, node) in peers)
                if (node is JsonObject peer)
                {
                    fullUpdateIds?.Add(id);
                    if (_peers.TryGetValue(id, out var existingPeer))
                        existingPeer.Update(peer);
                    else
                        _peers[id] = PeerRowViewModel.FromJson(id, peer);
                }
        }
        if (fullUpdateIds is not null)
            foreach (var id in _peers.Keys.Where(id => !fullUpdateIds.Contains(id)).ToList())
                _peers.Remove(id);
        if (response["peers_removed"] is JsonArray removed)
        {
            foreach (var id in removed.Select(static item => item?.GetValue<string>()).Where(static value => value is not null))
                _peers.Remove(id!);
        }
        _peerResponseId = response["rid"]?.GetValue<int>() ?? _peerResponseId;

        var desired = _peers.Values.OrderByDescending(static peer => ParseSpeed(peer.DownloadSpeed)).ThenBy(static peer => peer.Address).ToList();
        SynchronizePeers(desired);
    }

    private void SynchronizePeers(IReadOnlyList<PeerRowViewModel> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var peer = desired[index];
            if (index < SelectedPeers.Count && ReferenceEquals(SelectedPeers[index], peer))
                continue;
            var existingIndex = SelectedPeers.IndexOf(peer);
            if (existingIndex >= 0)
                SelectedPeers.Move(existingIndex, index);
            else
                SelectedPeers.Insert(index, peer);
        }
        while (SelectedPeers.Count > desired.Count)
            SelectedPeers.RemoveAt(SelectedPeers.Count - 1);
    }

    private static double ParseSpeed(string value)
    {
        var number = new string(value.TakeWhile(character => char.IsDigit(character) || character is '.' or ',').ToArray());
        return double.TryParse(number, NumberStyles.Float, CultureInfo.CurrentCulture, out var result) ? result : 0;
    }

    private void OnConnectionStateChanged(object? sender, ConnectionSnapshot snapshot)
    {
        void Update()
        {
            ConnectionStatus = snapshot.State switch
            {
                ConnectionState.StartingBackend => Localizer.Get("Connection_StartingBackend", "Starting backend…"),
                ConnectionState.Connecting => Localizer.Get("Connection_Connecting", "Connecting…"),
                ConnectionState.Authenticating => Localizer.Get("Connection_Authenticating", "Authenticating…"),
                ConnectionState.Connected => Localizer.Get("Connection_Connected", "Connected"),
                ConnectionState.Reconnecting => Localizer.Get("Connection_Reconnecting", "Reconnecting…"),
                ConnectionState.Faulted => Localizer.Get("Connection_Failed", "Connection failed"),
                ConnectionState.Stopping => Localizer.Get("Connection_Stopping", "Stopping…"),
                _ => Localizer.Get("Connection_Disconnected", "Disconnected")
            };
            ConnectionDetails = snapshot.Profile?.BaseAddress.ToString() ?? string.Empty;
            IsConnected = snapshot.State == ConnectionState.Connected;
            if (snapshot.Error is not null)
                ErrorMessage = snapshot.Error;
        }

        if (_dispatcher.HasThreadAccess)
            Update();
        else
            _dispatcher.TryEnqueue(Update);
    }

    private void EnsureApi()
    {
        if (_api is null)
            throw new InvalidOperationException("WinBitTorrent is not connected to a torrent backend.");
    }

    public async Task ShutdownAsync()
    {
        _lifetime?.Cancel();
        _detailsLifetime?.Cancel();
        await _connection.DisconnectAsync();
        await _backend.StopAsync(force: false);
    }

    public async ValueTask DisposeAsync()
    {
        _connection.StateChanged -= OnConnectionStateChanged;
        await ShutdownAsync();
        _detailsLifetime?.Dispose();
        _lifetime?.Dispose();
    }
}
