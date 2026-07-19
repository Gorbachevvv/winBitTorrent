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
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly MainDataAccumulator _mainData = new();
    private readonly Dictionary<string, TorrentRowViewModel> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PeerRowViewModel> _peers = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _detailsLifetime;
    private IQBittorrentApi? _api;
    private string? _peerHash;
    private int _peerResponseId;

    public MainViewModel(
        IConnectionCoordinator connection,
        IManagedBackendHost backend,
        IServerProfileStore profileStore,
        ILogger<MainViewModel> logger)
    {
        _connection = connection;
        _backend = backend;
        _profileStore = profileStore;
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

    public IReadOnlyList<TorrentRowViewModel> SelectedTorrents { get; set; } = [];
    public bool HasActiveTorrents => _rows.Values.Any(static torrent => torrent.IsActive);
    public IQBittorrentApi? Api => _api;
    public string SelectedHashes => GetSelectedHashes();
    public bool CanUseLocalFiles => SelectedProfile?.Kind == ProfileKind.LocalManaged;

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
            _rows.Clear();
            Torrents.Clear();
            _api = await _connection.ConnectAsync(SelectedProfile, _lifetime.Token);
            await _profileStore.SelectAsync(SelectedProfile.Id, _lifetime.Token);
            IsConnected = true;
            _ = RunSyncLoopAsync(_lifetime.Token);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            ConnectionStatus = Localizer.Get("Connection_Failed", "Connection failed");
            IsConnected = false;
            _logger.LogError(exception, "Unable to initialize qBittorrent connection.");
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
        await _api!.Torrents.AddAsync(options, _lifetime?.Token ?? default);
        await RefreshNowAsync();
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

    public async Task PostSelectedAsync(string action, IReadOnlyDictionary<string, string?> parameters)
    {
        EnsureApi();
        var hashes = GetSelectedHashes();
        if (string.IsNullOrEmpty(hashes))
            return;
        var values = new Dictionary<string, string?>(parameters) { ["hashes"] = hashes };
        await _api!.Torrents.PostAsync(action, values, _lifetime?.Token ?? default);
        await RefreshNowAsync();
    }

    public Task ToggleSequentialSelectedAsync() => ExecuteSelectedAsync(TorrentCommand.ToggleSequentialDownload);
    public Task ToggleFirstLastSelectedAsync() => ExecuteSelectedAsync(TorrentCommand.ToggleFirstLastPiecePriority);
    public Task MoveTopSelectedAsync() => ExecuteSelectedAsync(TorrentCommand.TopPriority);
    public Task MoveBottomSelectedAsync() => ExecuteSelectedAsync(TorrentCommand.BottomPriority);

    public Task SetForceStartSelectedAsync(bool enabled)
        => PostSelectedAsync("setForceStart", new Dictionary<string, string?> { ["value"] = enabled.ToString().ToLowerInvariant() });

    public Task SetSuperSeedingSelectedAsync(bool enabled)
        => PostSelectedAsync("setSuperSeeding", new Dictionary<string, string?> { ["value"] = enabled.ToString().ToLowerInvariant() });

    public Task SetCategorySelectedAsync(string category)
        => PostSelectedAsync("setCategory", new Dictionary<string, string?> { ["category"] = category });

    public Task AddTagsSelectedAsync(string tags)
        => PostSelectedAsync("addTags", new Dictionary<string, string?> { ["tags"] = tags });

    public Task RemoveTagsSelectedAsync(string tags)
        => PostSelectedAsync("removeTags", new Dictionary<string, string?> { ["tags"] = tags });

    public Task SetLocationSelectedAsync(string location)
        => PostSelectedAsync("setLocation", new Dictionary<string, string?> { ["location"] = location });

    public async Task RenameSelectedAsync(string name)
    {
        EnsureApi();
        if (SelectedTorrent is null)
            return;
        await _api!.Torrents.PostAsync("rename", new Dictionary<string, string?> { ["hash"] = SelectedTorrent.Hash, ["name"] = name }, _lifetime?.Token ?? default);
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
        _detailsLifetime?.Cancel();
        _detailsLifetime?.Dispose();
        _detailsLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime?.Token ?? default);
        _peerHash = null;
        _peerResponseId = 0;
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
        ApplyChangeSet(changeSet);
        IsConnected = true;
        ConnectionStatus = Localizer.Get("Connection_Connected", "Connected");
        ErrorMessage = string.Empty;
    }

    private void ApplyChangeSet(MainDataChangeSet changeSet)
    {
        if (changeSet.FullUpdate)
        {
            _rows.Clear();
            Torrents.Clear();
        }

        foreach (var torrent in changeSet.ChangedTorrents)
        {
            if (_rows.TryGetValue(torrent.Hash, out var row))
                row.Update(torrent);
            else
                _rows[torrent.Hash] = new TorrentRowViewModel(torrent);
        }

        foreach (var hash in changeSet.RemovedHashes)
            _rows.Remove(hash);

        DownloadSpeed = ValueFormatter.Speed(changeSet.ServerState.DownloadSpeed);
        UploadSpeed = ValueFormatter.Speed(changeSet.ServerState.UploadSpeed);
        FreeSpace = ValueFormatter.Size(changeSet.ServerState.FreeSpaceOnDisk);
        DhtNodes = $"{Localizer.Get("Status_Dht", "DHT")}: {changeSet.ServerState.DhtNodes}";
        UseAlternativeSpeedLimits = changeSet.ServerState.UseAlternativeSpeedLimits;

        RebuildFilters();
        RebuildVisibleRows();
    }

    private void RebuildFilters()
    {
        foreach (var filter in StatusFilters)
            filter.Count = _rows.Values.Count(row => MatchesStatus(row, filter.Key));

        ReplaceFilters(
            CategoryFilters,
            _rows.Values.GroupBy(static row => string.IsNullOrWhiteSpace(row.Model.Category) ? "Uncategorized" : row.Model.Category),
            TorrentFilterKind.Category,
            "\uE8B7");

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

    private static void ReplaceFilters<T>(ObservableCollection<FilterItemViewModel> destination, IEnumerable<IGrouping<string, T>> groups, TorrentFilterKind kind, string glyph)
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
            var existingIndex = IndexOfFilter(destination, item.Key, index);
            if (existingIndex < 0)
            {
                destination.Insert(index, new FilterItemViewModel(kind, item.Key, item.Title, glyph, item.Count));
                continue;
            }

            if (existingIndex != index)
                destination.Move(existingIndex, index);
            destination[index].Count = item.Count;
        }

        while (destination.Count > desired.Count)
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
        if (selected is null || _api is null)
            return;

        try
        {
            var propertiesTask = _api.Torrents.GetPropertiesAsync(selected.Hash, cancellationToken);
            var trackersTask = _api.Torrents.GetTrackersAsync(selected.Hash, cancellationToken);
            var webSeedsTask = _api.Torrents.GetWebSeedsAsync(selected.Hash, cancellationToken);
            var filesTask = _api.Torrents.GetFilesAsync(selected.Hash, cancellationToken);
            await Task.WhenAll(propertiesTask, trackersTask, webSeedsTask, filesTask);

            SelectedProperties = await propertiesTask;
            foreach (var item in await trackersTask)
                SelectedTrackers.Add(item);
            foreach (var item in await webSeedsTask)
                SelectedWebSeeds.Add(item);
            foreach (var item in await filesTask)
                SelectedFiles.Add(item);
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
            throw new InvalidOperationException("WinBitTorrent is not connected to qBittorrent.");
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
