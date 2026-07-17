using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Core.Services;

public static class TorrentFilters
{
    public static bool IsActive(TorrentInfo torrent)
        => torrent.DownloadSpeed > 0
            || torrent.UploadSpeed > 0
            || torrent.State.Contains("downloading", StringComparison.OrdinalIgnoreCase);

    public static bool MatchesStatus(TorrentInfo torrent, string status) => status switch
    {
        "all" => true,
        "downloading" => torrent.State is "downloading" or "forcedDL" or "metaDL" or "stalledDL",
        "seeding" => torrent.State is "uploading" or "forcedUP" or "stalledUP" or "queuedUP",
        "completed" => torrent.Progress >= 1,
        "stopped" => torrent.State.StartsWith("stopped", StringComparison.OrdinalIgnoreCase)
            || torrent.State.StartsWith("paused", StringComparison.OrdinalIgnoreCase),
        "active" => IsActive(torrent),
        "inactive" => !IsActive(torrent),
        "stalled" => torrent.State.StartsWith("stalled", StringComparison.OrdinalIgnoreCase),
        "errored" => torrent.State is "error" or "missingFiles",
        _ => true
    };

    public static bool MatchesText(TorrentInfo torrent, string? text)
        => string.IsNullOrWhiteSpace(text)
            || torrent.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
            || torrent.Tags.Contains(text, StringComparison.CurrentCultureIgnoreCase)
            || torrent.Category.Contains(text, StringComparison.CurrentCultureIgnoreCase);
}
