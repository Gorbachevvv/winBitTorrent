using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Core.Services;

/// <summary>
/// Decides when the "after downloads complete" power action is due. It only reacts to a fresh
/// completion event, so arming it while nothing downloads never fires right away
/// </summary>
public static class DownloadCompletionWatch
{
    /// <param name="torrentHash"> Hash of the watched torrent, null watches every download </param>
    /// <param name="events"> Events of the latest refresh, see <see cref="TorrentLifecycleMonitor"/> </param>
    /// <param name="torrents"> Every torrent known after that refresh </param>
    public static bool IsReached(string? torrentHash, IReadOnlyList<TorrentLifecycleEvent> events, IEnumerable<TorrentInfo> torrents)
    {
        var completed = events.Where(static item => item.Kind == TorrentLifecycleEventKind.DownloadCompleted);
        if (torrentHash is not null)
            return completed.Any(item => item.TorrentHash.Equals(torrentHash, StringComparison.OrdinalIgnoreCase));

        // Stopped and errored torrents do not hold the action back, the same way qBittorrent does it
        return completed.Any() && !torrents.Any(IsPendingDownload);
    }

    internal static bool IsPendingDownload(TorrentInfo torrent)
    {
        var state = torrent.State;
        if (state.StartsWith("paused", StringComparison.OrdinalIgnoreCase) || state.StartsWith("stopped", StringComparison.OrdinalIgnoreCase))
            return false;

        // "moving" is a finished download still being relocated out of the temporary folder
        return state.EndsWith("DL", StringComparison.Ordinal)
            || state.Equals("downloading", StringComparison.OrdinalIgnoreCase)
            || state.Equals("allocating", StringComparison.OrdinalIgnoreCase)
            || state.Equals("moving", StringComparison.OrdinalIgnoreCase);
    }
}
