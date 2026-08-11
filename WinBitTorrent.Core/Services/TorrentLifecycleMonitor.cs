using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Core.Services;

/// <summary>
/// Converts qBittorrent's incremental main-data snapshots into one-shot lifecycle events.
/// A full update is deliberately treated as a baseline: it commonly happens at startup or
/// after reconnecting and must not replay notifications for torrents that completed earlier.
/// </summary>
public sealed class TorrentLifecycleMonitor
{
    private readonly Dictionary<string, TrackedTorrentState> _states = new(StringComparer.OrdinalIgnoreCase);
    private bool _isPrimed;

    public IReadOnlyList<TorrentLifecycleEvent> Observe(MainDataChangeSet changeSet)
    {
        // response id 0 normally produces full_update=true, but this is not guaranteed by every
        // supported qBittorrent/Web API implementation. The first response after connecting is
        // always an inventory snapshot, never a stream of newly-added torrents.
        if (!_isPrimed || changeSet.FullUpdate)
        {
            _states.Clear();
            foreach (var torrent in changeSet.ChangedTorrents)
            {
                var snapshot = Snapshot(torrent);
                _states[torrent.Hash] = new(snapshot, snapshot.IsDownloading);
            }
            _isPrimed = true;
            return [];
        }

        var events = new List<TorrentLifecycleEvent>();
        foreach (var torrent in changeSet.ChangedTorrents)
        {
            var current = Snapshot(torrent);
            if (!_states.TryGetValue(torrent.Hash, out var previous))
            {
                _states[torrent.Hash] = new(current, current.IsDownloading);
                events.Add(new TorrentLifecycleEvent(
                    TorrentLifecycleEventKind.Added,
                    torrent.Hash,
                    torrent.Name));
                continue;
            }

            if (!previous.Snapshot.IsComplete && current.IsComplete && previous.CompletionArmed)
            {
                events.Add(new TorrentLifecycleEvent(
                    TorrentLifecycleEventKind.DownloadCompleted,
                    torrent.Hash,
                    torrent.Name));
            }

            if (!previous.Snapshot.IsErrored && current.IsErrored)
            {
                events.Add(new TorrentLifecycleEvent(
                    TorrentLifecycleEventKind.Error,
                    torrent.Hash,
                    torrent.Name));
            }

            _states[torrent.Hash] = new(
                current,
                previous.CompletionArmed || current.IsDownloading);
        }

        foreach (var hash in changeSet.RemovedHashes)
            _states.Remove(hash);

        return events;
    }

    public void Reset()
    {
        _states.Clear();
        _isPrimed = false;
    }

    private static TorrentStateSnapshot Snapshot(TorrentInfo torrent)
    {
        var isErrored = torrent.State.Equals("error", StringComparison.OrdinalIgnoreCase)
            || torrent.State.Equals("missingFiles", StringComparison.OrdinalIgnoreCase);
        // qBittorrent postpones its own "finished" notification until a completed torrent has
        // been moved out of the temporary download directory. Mirror that by keeping the moving
        // state incomplete until the following seeding/stopped state arrives.
        var isMoving = torrent.State.Equals("moving", StringComparison.OrdinalIgnoreCase);
        // qBittorrent's UP states are authoritative: during initial synchronization it can
        // deliver the state before the progress/amount_left fields. Treating those temporary
        // numeric defaults as real data caused old seeds to look like fresh 0 -> 100% downloads.
        var isUploading = torrent.State.Equals("uploading", StringComparison.OrdinalIgnoreCase)
            || torrent.State.EndsWith("UP", StringComparison.OrdinalIgnoreCase);
        var isDownloading = torrent.State.Equals("downloading", StringComparison.OrdinalIgnoreCase)
            || torrent.State.EndsWith("DL", StringComparison.OrdinalIgnoreCase);
        var isComplete = !isErrored && !isMoving
            && (isUploading || (torrent.Progress >= 0.999999d && torrent.AmountLeft <= 0));
        return new TorrentStateSnapshot(isComplete, isErrored, isDownloading);
    }

    private sealed record TorrentStateSnapshot(bool IsComplete, bool IsErrored, bool IsDownloading);
    private sealed record TrackedTorrentState(TorrentStateSnapshot Snapshot, bool CompletionArmed);
}

public sealed record TorrentLifecycleEvent(
    TorrentLifecycleEventKind Kind,
    string TorrentHash,
    string TorrentName);

public enum TorrentLifecycleEventKind
{
    Added,
    DownloadCompleted,
    Error
}
