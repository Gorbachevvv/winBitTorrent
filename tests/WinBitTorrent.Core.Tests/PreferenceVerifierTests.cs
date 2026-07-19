using System.Text.Json.Nodes;
using WinBitTorrent.Core.Services;

namespace WinBitTorrent.Core.Tests;

public sealed class PreferenceVerifierTests
{
    [Fact]
    public void AcceptsEquivalentIntegerRepresentationsAndNormalizedIpLists()
    {
        var requested = new JsonObject
        {
            ["listen_port"] = 4242.0,
            ["max_connec"] = 200,
            ["banned_IPs"] = "192.0.2.1\r\n\r\n198.51.100.2\r\n"
        };
        var actual = new JsonObject
        {
            ["listen_port"] = 4242,
            ["max_connec"] = 200.0,
            ["banned_IPs"] = "192.0.2.1\n198.51.100.2"
        };

        Assert.Empty(PreferenceVerifier.FindMismatchedKeys(requested, actual));
    }

    [Fact]
    public void ReportsMissingAndChangedPreferences()
    {
        var requested = new JsonObject
        {
            ["upnp"] = true,
            ["proxy_type"] = "SOCKS5",
            ["max_uploads"] = 10
        };
        var actual = new JsonObject
        {
            ["upnp"] = false,
            ["proxy_type"] = "SOCKS5"
        };

        Assert.Equal(["upnp", "max_uploads"], PreferenceVerifier.FindMismatchedKeys(requested, actual));
    }
}
