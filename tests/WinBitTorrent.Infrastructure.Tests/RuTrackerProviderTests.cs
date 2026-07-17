using System.Net;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Infrastructure.Trackers;

namespace WinBitTorrent.Infrastructure.Tests;

public sealed class RuTrackerProviderTests
{
    [Fact]
    public void ParseResultsReadsCurrentRuTrackerRowShape()
    {
        const string html = """
            <table>
              <tr id="trs-tr-654321">
                <td><a data-topic_id="654321" href="viewtopic.php?t=654321">Космос &amp; наука</a></td>
                <td data-ts_text="2147483648">2 GB</td>
                <td data-ts_text="42">42</td>
                <td class="leechmed">7</td>
                <td data-ts_text="1720000000">03-Jul-24</td>
              </tr>
            </table>
            """;

        var result = Assert.Single(RuTrackerProvider.ParseResults(html, new Uri("https://rutracker.org")));

        Assert.Equal("654321", result.Id);
        Assert.Equal("Космос & наука", result.Title);
        Assert.Equal(2147483648, result.Size);
        Assert.Equal(42, result.Seeds);
        Assert.Equal(7, result.Leechers);
        Assert.Equal(new Uri("https://rutracker.org/forum/viewtopic.php?t=654321"), result.DetailsUri);
        Assert.NotNull(result.PublishedAt);
    }

    [Fact]
    public void PercentEncodeUsesRuTrackerCp1251Encoding()
        => Assert.Equal("%EA%EE%F1%EC%EE%F1", RuTrackerProvider.PercentEncode("космос"));

    [Fact]
    public async Task BrowserSessionRequiresRuTrackerSessionCookie()
    {
        using var provider = new RuTrackerProvider();

        await Assert.ThrowsAsync<TrackerAuthenticationException>(() =>
            provider.ImportSessionCookiesAsync([new Cookie("other", "value", "/", ".rutracker.org")]));
    }

    [Fact]
    public void GuestPageIsRejectedAsExpiredSession()
    {
        const string guestPage = "<form><input name=\"login_username\"></form>";

        Assert.Throws<TrackerAuthenticationException>(() => RuTrackerProvider.EnsurePageIsAuthenticated(guestPage));
    }
}
