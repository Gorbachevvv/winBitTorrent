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
            || torrent.Name.Contains(text.Trim(), StringComparison.CurrentCultureIgnoreCase);

    public static bool Matches(TorrentInfo torrent, string status = "all", string? category = null,
        string? tag = null, string? tracker = null, string? text = null)
        => MatchesStatus(torrent, status)
            && (category is null || (string.IsNullOrWhiteSpace(torrent.Category) ? "" : torrent.Category)
                .Equals(category, StringComparison.Ordinal))
            && (tag is null || torrent.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(tag, StringComparer.Ordinal))
            && (tracker is null || TrackerHost(torrent.Tracker).Equals(tracker, StringComparison.OrdinalIgnoreCase))
            && MatchesText(torrent, text);

    public static string TrackerHost(string value)
        => Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ? uri.Host : value.Trim();
}
