using System.Globalization;
using System.Text.Json;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Core.Services;

namespace WinBitTorrent.Core.Tests;

public sealed class ContractTests
{
    [Fact]
    public void TorrentDtoPreservesUnknownApiFields()
    {
        var torrent = JsonSerializer.Deserialize<TorrentInfo>("""{"name":"Ubuntu","future_field":{"enabled":true}}""")!;
        Assert.Equal("Ubuntu", torrent.Name);
        Assert.Contains("name", torrent.PresentFields);
        Assert.True(torrent.AdditionalData!.ContainsKey("future_field"));
    }

    [Fact]
    public void TorrentDtoTreatsNullNumericAndBooleanFieldsAsDefaults()
    {
        // qBittorrent can legitimately send null for these (e.g. eta/ratio_limit/availability on
        // torrents that haven't started or don't apply) - the reader used to call GetInt64()/
        // GetDouble()/GetBoolean() unconditionally and crash with
        // "Cannot get the value of a token type 'Null' as a number." the first time a real
        // response included one.
        var torrent = JsonSerializer.Deserialize<TorrentInfo>(
            """{"name":"Ubuntu","eta":null,"ratio_limit":null,"private":null,"num_seeds":null}""")!;
        Assert.Equal("Ubuntu", torrent.Name);
        Assert.Equal(0, torrent.Eta);
        Assert.Equal(0, torrent.RatioLimit);
        Assert.False(torrent.IsPrivate);
        Assert.Equal(0, torrent.Seeds);
    }

    [Fact]
    public void ProfileJsonNeverContainsASecretProperty()
    {
        var profile = new ServerProfile(Guid.NewGuid(), "Remote", ProfileKind.RemoteQbittorrent, new Uri("https://example.test/"), AuthenticationMode.ApiKey);
        var json = JsonSerializer.Serialize(profile);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1.00 KiB")]
    [InlineData(1048576, "1.00 MiB")]
    public void FormatsBinarySizes(long bytes, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { Assert.Equal(expected, ValueFormatter.Size(bytes)); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("downloading", "downloading", true)]
    [InlineData("forcedUP", "seeding", true)]
    [InlineData("missingFiles", "errored", true)]
    [InlineData("stoppedDL", "active", false)]
    public void MatchesDesktopStatusFilters(string state, string filter, bool expected)
        => Assert.Equal(expected, TorrentFilters.MatchesStatus(new TorrentInfo { State = state }, filter));

    [Fact]
    public void TextSearchMatchesOnlyTheTorrentName()
    {
        var torrent = new TorrentInfo { Name = "Ubuntu ISO", Category = "Linux", Tags = "daily, trusted" };
        Assert.True(TorrentFilters.MatchesText(torrent, "ubuntu"));
        Assert.True(TorrentFilters.MatchesText(torrent, " UBUNTU "));
        Assert.False(TorrentFilters.MatchesText(torrent, "linux"));
        Assert.False(TorrentFilters.MatchesText(torrent, "trusted"));
        Assert.False(TorrentFilters.MatchesText(torrent, "movie"));
    }

    [Fact]
    public void SidebarFiltersCombineAndAllResetsOnlyItsOwnGroup()
    {
        TorrentInfo[] torrents =
        [
            new() { Name = "Ubuntu ISO", State = "downloading", Category = "Linux", Tags = "daily, trusted", Tracker = "https://Tracker.Example.org/announce" },
            new() { Name = "Ubuntu archive", State = "stoppedDL", Category = "Linux", Tags = "trusted", Tracker = "udp://tracker.example.org:80/announce" },
            new() { Name = "Other ISO", State = "downloading", Category = "Other", Tags = "daily", Tracker = "https://other.example.org/announce" }
        ];
        Assert.Single(torrents, t => TorrentFilters.Matches(t, "downloading", "Linux", "trusted", "tracker.example.org", "ubuntu"));
        Assert.DoesNotContain(torrents, t => TorrentFilters.Matches(t, "downloading", "Other", "trusted"));
        // All categories still respects the selected tag; all statuses still respects category.
        Assert.Single(torrents, t => TorrentFilters.Matches(t, "downloading", tag: "trusted"));
        Assert.Equal(2, torrents.Count(t => TorrentFilters.Matches(t, category: "Linux", tracker: "tracker.example.org")));
        Assert.Single(torrents, t => TorrentFilters.Matches(t, "downloading", "Linux"));
        Assert.Equal(3, torrents.Count(t => TorrentFilters.Matches(t)));
    }

    [Fact]
    public void UncategorizedAndTagMatchingDoNotCollideWithRealNames()
    {
        var torrent = new TorrentInfo { Name = "Example", Category = "Uncategorized", Tags = "trusted-extra, daily" };
        Assert.False(TorrentFilters.Matches(torrent, category: ""));
        Assert.True(TorrentFilters.Matches(torrent, category: "Uncategorized", tag: "daily"));
        Assert.False(TorrentFilters.Matches(torrent, category: "uncategorized"));
        Assert.False(TorrentFilters.Matches(torrent, tag: "DAILY"));
        Assert.False(TorrentFilters.Matches(torrent, tag: "trusted"));
        torrent.Category = " ";
        Assert.True(TorrentFilters.Matches(torrent, category: ""));
    }
}
