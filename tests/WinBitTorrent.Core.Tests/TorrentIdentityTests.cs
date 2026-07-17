using System.Text;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Core.Services;

namespace WinBitTorrent.Core.Tests;

public sealed class TorrentIdentityTests
{
    [Fact]
    public void ReadsExactInfoDictionaryHashesFromTorrentFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(
                "d4:infod4:name4:test6:lengthi1e6:pieces20:aaaaaaaaaaaaaaaaaaaaee"));

            var hashes = TorrentIdentity.FromTorrentFile(path);

            Assert.Contains("35065D23F5068E5F0BA2C5C2CC9CF382474042F3", hashes);
            Assert.Contains("71916E574D396085B9BED4C4E15890E52AF760693C575D64382C38C5477D3FE7", hashes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsHexAndBase32MagnetHashes()
    {
        var hex = "0123456789ABCDEF0123456789ABCDEF01234567";
        var hashes = TorrentIdentity.FromMagnet(
            $"magnet:?xt=urn:btih:{hex}&xt=urn:btih:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&xt=urn:btmh:122071916E574D396085B9BED4C4E15890E52AF760693C575D64382C38C5477D3FE7");

        Assert.Contains(hex, hashes);
        Assert.Contains(new string('0', 40), hashes);
        Assert.Contains("71916E574D396085B9BED4C4E15890E52AF760693C575D64382C38C5477D3FE7", hashes);
    }

    [Fact]
    public void MatchesAnyTorrentHashVersion()
    {
        var torrent = new TorrentInfo
        {
            Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            InfoHashV2 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"
        };

        Assert.True(TorrentIdentity.Matches(torrent, [torrent.InfoHashV2.ToLowerInvariant()]));
        Assert.False(TorrentIdentity.Matches(torrent, ["CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC"]));
    }
}
