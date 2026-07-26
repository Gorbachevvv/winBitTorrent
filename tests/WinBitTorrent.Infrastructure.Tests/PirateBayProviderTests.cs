using WinBitTorrent.Infrastructure.Trackers;

namespace WinBitTorrent.Infrastructure.Tests;

public sealed class PirateBayProviderTests
{
    private static readonly Uri Site = new("https://thepiratebay.org/");

    [Fact]
    public void ParseResultsReadsApibayEntryShape()
    {
        const string json = """
            [{"id":"12345678","name":"Big Buck Bunny 1080p","info_hash":"DD8255ECDC7CA55FB0BBF81323D87062DB1F6D1C",
              "leechers":"7","seeders":"42","num_files":"3","size":"2147483648","username":"vip_user",
              "added":"1720000000","status":"vip","category":"207","imdb":"tt1254207"}]
            """;

        var result = Assert.Single(PirateBayProvider.ParseResults(json, Site));

        Assert.Equal("12345678", result.Id);
        Assert.Equal("Big Buck Bunny 1080p", result.Title);
        Assert.Equal(2147483648, result.Size);
        Assert.Equal(42, result.Seeds);
        Assert.Equal(7, result.Leechers);
        Assert.Equal(new Uri("https://thepiratebay.org/description.php?id=12345678"), result.DetailsUri);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1720000000), result.PublishedAt);
        Assert.NotNull(result.MagnetUri);
    }

    // apibay answers "nothing found" with one placeholder row carrying an all-zero info hash.
    [Fact]
    public void ParseResultsSkipsTheEmptySearchPlaceholder()
    {
        const string json = """
            [{"id":"0","name":"No results returned","info_hash":"0000000000000000000000000000000000000000",
              "leechers":"0","seeders":"0","num_files":"0","size":"0","username":"","added":"0",
              "status":"member","category":"0","imdb":""}]
            """;

        Assert.Empty(PirateBayProvider.ParseResults(json, Site));
    }

    [Fact]
    public void ParseResultsDecodesHtmlEscapedReleaseNames()
    {
        const string json = """
            [{"id":"71222161","name":"Linux Command Line &amp; Scripting","info_hash":"C426B415D030710DF9335DAA38B2B12DBD0D48FB",
              "leechers":"0","seeders":"5","num_files":"1","size":"1024","username":"x","added":"1700000000",
              "status":"vip","category":"601","imdb":""}]
            """;

        Assert.Equal("Linux Command Line & Scripting", Assert.Single(PirateBayProvider.ParseResults(json, Site)).Title);
    }

    // apibay matches from the start of a release name, so shorter prefixes are tried as a fallback.
    [Fact]
    public void QueryCandidatesShortenTheQueryFromTheEnd()
        => Assert.Equal(
            ["Dune Part Two 2024", "Dune Part Two", "Dune Part"],
            PirateBayProvider.BuildQueryCandidates("Dune Part Two 2024"));

    [Fact]
    public void QueryCandidatesKeepASingleWordQueryIntact()
        => Assert.Equal(["Inception"], PirateBayProvider.BuildQueryCandidates("Inception"));

    [Fact]
    public void BuildMagnetUriCarriesHashDisplayNameAndTrackers()
    {
        var magnet = PirateBayProvider.BuildMagnetUri("DD8255ECDC7CA55FB0BBF81323D87062DB1F6D1C", "Big Buck Bunny 1080p");

        Assert.StartsWith("magnet:?xt=urn:btih:DD8255ECDC7CA55FB0BBF81323D87062DB1F6D1C", magnet.OriginalString, StringComparison.Ordinal);
        Assert.Contains("&dn=Big%20Buck%20Bunny%201080p", magnet.OriginalString, StringComparison.Ordinal);
        Assert.Contains("&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337%2Fannounce", magnet.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadTorrentIsNotSupportedForMagnetOnlyResults()
    {
        using var provider = new PirateBayProvider();

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DownloadTorrentAsync("12345678"));
    }
}
