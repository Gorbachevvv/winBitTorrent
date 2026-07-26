namespace WinBitTorrent.Core.Models;

public sealed record TrackerCredentials(string UserName, string Password);

// A catalog title handed over to a tracker search. IsEnglishTitle reports whether Title is the
// English release name (false when the catalog had no English translation to offer).
public sealed record CatalogTrackerQuery(string Title, int? Year, int? Season, bool IsEnglishTitle);

// MagnetUri is set by trackers that publish magnet links instead of .torrent files (The Pirate Bay);
// when it is null the torrent has to be fetched through ITrackerSearchProvider.DownloadTorrentAsync.
public sealed record TrackerSearchResult(
    string Id,
    string Title,
    long Size,
    int Seeds,
    int Leechers,
    DateTimeOffset? PublishedAt,
    Uri DetailsUri,
    Uri? MagnetUri = null);
