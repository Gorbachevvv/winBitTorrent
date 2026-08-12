using WinBitTorrent.Core.Services;

namespace WinBitTorrent.Core.Tests;

public sealed class TorrentLinkParserTests
{
    [Fact]
    public void ParsesSupportedLinksAndRemovesDuplicates()
    {
        const string text = "magnet:?xt=urn:btih:ABC\r\nhttps://example.test/file.torrent\nMAGNET:?xt=urn:btih:abc";

        var valid = TorrentLinkParser.TryParse(text, out var links, out var invalidLine);

        Assert.True(valid);
        Assert.Equal(0, invalidLine);
        Assert.Equal(2, links.Count);
    }

    [Fact]
    public void ConvertsRawV1AndV2InfoHashesToMagnets()
    {
        var v1 = new string('a', 40);
        var v2 = new string('b', 64);

        var valid = TorrentLinkParser.TryParse($"{v1}\n{v2}", out var links, out _);

        Assert.True(valid);
        Assert.Equal($"magnet:?xt=urn:btih:{v1}", links[0]);
        Assert.Equal($"magnet:?xt=urn:btmh:1220{v2}", links[1]);
    }

    [Fact]
    public void ReportsTheInvalidNonEmptyLine()
    {
        var valid = TorrentLinkParser.TryParse(
            "https://example.test/a.torrent\n\nnot a torrent link",
            out var links,
            out var invalidLine);

        Assert.False(valid);
        Assert.Empty(links);
        Assert.Equal(3, invalidLine);
    }

    [Fact]
    public void RejectsEmptyInput()
    {
        Assert.False(TorrentLinkParser.TryParse(" \r\n ", out var links, out var invalidLine));
        Assert.Empty(links);
        Assert.Equal(0, invalidLine);
    }
}
