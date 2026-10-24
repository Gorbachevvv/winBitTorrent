using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Infrastructure.Net;

namespace WinBitTorrent.Infrastructure.Trackers;

// The Pirate Bay exposes the same public JSON API its own front-end uses (apibay.org). It needs no
// account and returns info hashes rather than .torrent files, so results carry a magnet link.
public sealed class PirateBayProvider : ITrackerSearchProvider, ITrackerAnonymousAccess, IDisposable
{
    private static readonly Uri[] ApiMirrors = [new("https://apibay.org/")];
    private static readonly Uri Site = new("https://thepiratebay.org/");
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/138.0.0.0 Safari/537.36";

    // apibay answers an empty search with a single placeholder row carrying this all-zero hash.
    private const string EmptyResultHash = "0000000000000000000000000000000000000000";

    // The tracker list thepiratebay.org bakes into the magnet links it hands out.
    private static readonly string[] MagnetTrackers =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://tracker.bittor.pw:1337/announce",
        "udp://public.popcorn-tracker.org:6969/announce",
        "udp://tracker.dler.org:6969/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://open.demonii.com:1337/announce"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client = CreateClient();

    public string Id => "piratebay";
    public string DisplayName => "The Pirate Bay";
    public Uri HomePage => Site;

    public Task SignInAsync(TrackerCredentials credentials, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async Task<IReadOnlyList<TrackerSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        Exception? lastError = null;
        foreach (var candidate in BuildQueryCandidates(query))
        {
            foreach (var mirror in ApiMirrors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var relative = "q.php?q=" + Uri.EscapeDataString(candidate) + "&cat=0";
                    var json = await _client.GetStringAsync(new Uri(mirror, relative), cancellationToken).ConfigureAwait(false);
                    var results = ParseResults(json, Site);
                    if (results.Count > 0)
                        return results;

                    lastError = null;
                    break;
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    lastError = exception;
                }
            }

            if (lastError is not null)
                break;
        }

        if (lastError is null)
            return [];

        throw new HttpRequestException($"The Pirate Bay is unavailable: {lastError.Message}", lastError);
    }

    // apibay matches a query against the *beginning* of a release name, so "Dune 2024" finds
    // nothing while "Dune" finds "Dune Part Two (2024)". Retry with progressively shorter prefixes
    // so catalog queries ("<title> <year>", "<title> season 2") still land on something.
    internal static IReadOnlyList<string> BuildQueryCandidates(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidates = new List<string>();
        for (var length = words.Length; length > 0 && candidates.Count < 3; length--)
            candidates.Add(string.Join(' ', words.Take(length)));
        return candidates;
    }

    // Results are magnet links, so nothing ever asks this provider for a .torrent payload.
    public Task<byte[]> DownloadTorrentAsync(string resultId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The Pirate Bay publishes magnet links instead of torrent files.");

    internal static IReadOnlyList<TrackerSearchResult> ParseResults(string json, Uri site)
    {
        var results = new List<TrackerSearchResult>();
        foreach (var entry in JsonSerializer.Deserialize<PirateBayEntry[]>(json, JsonOptions) ?? [])
        {
            var infoHash = entry.InfoHash?.Trim();
            if (string.IsNullOrEmpty(entry.Id) || string.IsNullOrWhiteSpace(entry.Name) ||
                string.IsNullOrEmpty(infoHash) || infoHash.Equals(EmptyResultHash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // apibay hands out HTML-escaped release names ("Linux Scripting &amp; Tools").
            var title = WebUtility.HtmlDecode(entry.Name).Trim();
            DateTimeOffset? published = null;
            var added = ParseLong(entry.Added);
            if (added > 0)
            {
                try { published = DateTimeOffset.FromUnixTimeSeconds(added); }
                catch (ArgumentOutOfRangeException) { }
            }

            results.Add(new TrackerSearchResult(
                entry.Id,
                title,
                ParseLong(entry.Size),
                (int)ParseLong(entry.Seeders),
                (int)ParseLong(entry.Leechers),
                published,
                new Uri(site, "description.php?id=" + Uri.EscapeDataString(entry.Id)),
                BuildMagnetUri(infoHash, title)));
        }

        return results;
    }

    internal static Uri BuildMagnetUri(string infoHash, string title)
    {
        var builder = new StringBuilder("magnet:?xt=urn:btih:").Append(infoHash);
        builder.Append("&dn=").Append(Uri.EscapeDataString(title));
        foreach (var tracker in MagnetTrackers)
            builder.Append("&tr=").Append(Uri.EscapeDataString(tracker));
        return new Uri(builder.ToString());
    }

    private static long ParseLong(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = SystemProxyResolver.Create(ApiMirrors[0]),
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        return client;
    }

    public void Dispose() => _client.Dispose();

    // apibay returns every numeric field as a string.
    internal sealed record PirateBayEntry
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
        [JsonPropertyName("info_hash")]
        public string? InfoHash { get; init; }
        public string? Leechers { get; init; }
        public string? Seeders { get; init; }
        public string? Size { get; init; }
        public string? Added { get; init; }
    }
}
