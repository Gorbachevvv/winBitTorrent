namespace WinBitTorrent.Core.Models;

public sealed record TrackerCredentials(string UserName, string Password);

public sealed record TrackerSearchResult(
    string Id,
    string Title,
    long Size,
    int Seeds,
    int Leechers,
    DateTimeOffset? PublishedAt,
    Uri DetailsUri);
