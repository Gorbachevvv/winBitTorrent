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
        => HasCompletion(torrentHash, events) && CanRun(torrentHash, torrents);

    public static bool HasCompletion(string? torrentHash, IReadOnlyList<TorrentLifecycleEvent> events)
        => events.Any(item => item.Kind == TorrentLifecycleEventKind.DownloadCompleted
            && (torrentHash is null || item.TorrentHash.Equals(torrentHash, StringComparison.OrdinalIgnoreCase)));

    public static bool CanRun(string? torrentHash, IEnumerable<TorrentInfo> torrents)
    {
        if (torrentHash is null)
            return !torrents.Any(IsPendingDownload);
        var target = torrents.FirstOrDefault(item => item.Hash.Equals(torrentHash, StringComparison.OrdinalIgnoreCase));
        return target is not null && target.Progress >= 0.999999 && target.AmountLeft <= 0
            && !IsPendingDownload(target) && target.State is not ("error" or "missingFiles" or "unknown");
    }

    internal static bool IsPendingDownload(TorrentInfo torrent)
    {
        var state = torrent.State;
        if (state.StartsWith("paused", StringComparison.OrdinalIgnoreCase) || state.StartsWith("stopped", StringComparison.OrdinalIgnoreCase))
            return false;

        // "moving" is a finished download still being relocated out of the temporary folder
        return state.EndsWith("DL", StringComparison.Ordinal)
            || state.Equals("checkingResumeData", StringComparison.OrdinalIgnoreCase)
            || state.Equals("checkingUP", StringComparison.OrdinalIgnoreCase)
            || state.Equals("downloading", StringComparison.OrdinalIgnoreCase)
            || state.Equals("allocating", StringComparison.OrdinalIgnoreCase)
            || state.Equals("moving", StringComparison.OrdinalIgnoreCase);
    }
}
