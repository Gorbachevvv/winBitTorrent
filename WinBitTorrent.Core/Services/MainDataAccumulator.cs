using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Core.Services;

public sealed class MainDataAccumulator
{
    private readonly Dictionary<string, TorrentInfo> _torrents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TorrentCategory> _categories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tags = new(StringComparer.OrdinalIgnoreCase);

    public int ResponseId { get; private set; }
    public IReadOnlyDictionary<string, TorrentInfo> Torrents => _torrents;
    public IReadOnlyDictionary<string, TorrentCategory> Categories => _categories;
    public IReadOnlyCollection<string> Tags => _tags;
    public ServerState ServerState { get; private set; } = new();

    public MainDataChangeSet Apply(MainDataResponse response)
    {
        if (response.FullUpdate)
        {
            _torrents.Clear();
            _categories.Clear();
            _tags.Clear();
        }

        var changed = new List<TorrentInfo>();
        var removed = new List<string>();

        if (response.Torrents is not null)
        {
            foreach (var (hash, torrent) in response.Torrents)
            {
                torrent.Hash = hash;
                if (!response.FullUpdate && torrent.PresentFields.Count > 0 && _torrents.TryGetValue(hash, out var existing))
                {
                    ApplyTorrentPatch(existing, torrent);
                    changed.Add(existing);
                }
                else
                {
                    _torrents[hash] = torrent;
                    changed.Add(torrent);
                }
            }
        }

        if (response.TorrentsRemoved is not null)
        {
            foreach (var hash in response.TorrentsRemoved)
            {
                if (_torrents.Remove(hash))
                    removed.Add(hash);
            }
        }

        if (response.Categories is not null)
        {
            foreach (var (name, category) in response.Categories)
                _categories[name] = category;
        }

        if (response.CategoriesRemoved is not null)
        {
            foreach (var name in response.CategoriesRemoved)
                _categories.Remove(name);
        }

        if (response.Tags is not null)
        {
            foreach (var tag in response.Tags)
                _tags.Add(tag);
        }

        if (response.TagsRemoved is not null)
        {
            foreach (var tag in response.TagsRemoved)
                _tags.Remove(tag);
        }

        if (response.ServerState is not null)
            ServerState = response.ServerState;

        ResponseId = response.ResponseId;
        return new MainDataChangeSet(response.FullUpdate, changed, removed, ServerState);
    }

    public void Reset()
    {
        ResponseId = 0;
        _torrents.Clear();
        _categories.Clear();
        _tags.Clear();
        ServerState = new ServerState();
    }

    private static void ApplyTorrentPatch(TorrentInfo target, TorrentInfo patch)
    {
        target.Hash = patch.Hash;

        foreach (var field in patch.PresentFields)
        {
            switch (field)
            {
                case "name": target.Name = patch.Name; break;
                case "size": target.Size = patch.Size; break;
                case "total_size": target.TotalSize = patch.TotalSize; break;
                case "progress": target.Progress = patch.Progress; break;
                case "state": target.State = patch.State; break;
                case "num_seeds": target.Seeds = patch.Seeds; break;
                case "num_complete": target.TotalSeeds = patch.TotalSeeds; break;
                case "num_leechs": target.Peers = patch.Peers; break;
                case "num_incomplete": target.TotalPeers = patch.TotalPeers; break;
                case "dlspeed": target.DownloadSpeed = patch.DownloadSpeed; break;
                case "upspeed": target.UploadSpeed = patch.UploadSpeed; break;
                case "eta": target.Eta = patch.Eta; break;
                case "ratio": target.Ratio = patch.Ratio; break;
                case "popularity": target.Popularity = patch.Popularity; break;
                case "category": target.Category = patch.Category; break;
                case "tags": target.Tags = patch.Tags; break;
                case "added_on": target.AddedOn = patch.AddedOn; break;
                case "completion_on": target.CompletedOn = patch.CompletedOn; break;
                case "created_on": target.CreatedOn = patch.CreatedOn; break;
                case "tracker": target.Tracker = patch.Tracker; break;
                case "dl_limit": target.DownloadLimit = patch.DownloadLimit; break;
                case "up_limit": target.UploadLimit = patch.UploadLimit; break;
                case "downloaded": target.Downloaded = patch.Downloaded; break;
                case "uploaded": target.Uploaded = patch.Uploaded; break;
                case "downloaded_session": target.DownloadedSession = patch.DownloadedSession; break;
                case "uploaded_session": target.UploadedSession = patch.UploadedSession; break;
                case "amount_left": target.AmountLeft = patch.AmountLeft; break;
                case "time_active": target.TimeActive = patch.TimeActive; break;
                case "save_path": target.SavePath = patch.SavePath; break;
                case "content_path": target.ContentPath = patch.ContentPath; break;
                case "completed": target.Completed = patch.Completed; break;
                case "ratio_limit": target.RatioLimit = patch.RatioLimit; break;
                case "seen_complete": target.SeenComplete = patch.SeenComplete; break;
                case "last_activity": target.LastActivity = patch.LastActivity; break;
                case "availability": target.Availability = patch.Availability; break;
                case "download_path": target.DownloadPath = patch.DownloadPath; break;
                case "infohash_v1": target.InfoHashV1 = patch.InfoHashV1; break;
                case "infohash_v2": target.InfoHashV2 = patch.InfoHashV2; break;
                case "reannounce": target.Reannounce = patch.Reannounce; break;
                case "private": target.IsPrivate = patch.IsPrivate; break;
                case "priority": target.QueuePosition = patch.QueuePosition; break;
                case "force_start": target.ForceStart = patch.ForceStart; break;
                case "seq_dl": target.SequentialDownload = patch.SequentialDownload; break;
                case "f_l_piece_prio": target.FirstLastPiecePriority = patch.FirstLastPiecePriority; break;
            }
        }

        if (patch.AdditionalData is not null)
        {
            target.AdditionalData ??= new(StringComparer.Ordinal);
            foreach (var (key, value) in patch.AdditionalData)
                target.AdditionalData[key] = value;
        }
    }
}

public sealed record MainDataChangeSet(
    bool FullUpdate,
    IReadOnlyList<TorrentInfo> ChangedTorrents,
    IReadOnlyList<string> RemovedHashes,
    ServerState ServerState);
