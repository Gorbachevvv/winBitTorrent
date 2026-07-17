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
    public void ProfileJsonNeverContainsASecretProperty()
    {
        var profile = new ServerProfile(Guid.NewGuid(), "Remote", ProfileKind.Remote, new Uri("https://example.test/"), AuthenticationMode.ApiKey);
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
    public void TextFilterCoversNameCategoryAndTags()
    {
        var torrent = new TorrentInfo { Name = "Ubuntu ISO", Category = "Linux", Tags = "daily, trusted" };
        Assert.True(TorrentFilters.MatchesText(torrent, "ubuntu"));
        Assert.True(TorrentFilters.MatchesText(torrent, "linux"));
        Assert.True(TorrentFilters.MatchesText(torrent, "trusted"));
        Assert.False(TorrentFilters.MatchesText(torrent, "movie"));
    }
}
