using WinBitTorrent.Core.Models;
using WinBitTorrent.Core.Services;

namespace WinBitTorrent.Core.Tests;

public sealed class PowerReviewRegressionTests
{
    [Fact]
    public void AllDownloadsMustWaitForAnIncompleteTorrentCheckingResumeData()
    {
        TorrentLifecycleEvent completed = new(TorrentLifecycleEventKind.DownloadCompleted, "finished", "Finished");
        TorrentInfo checking = new() { Hash = "checking", State = "checkingResumeData", Progress = 0.5, AmountLeft = 100 };
        Assert.False(DownloadCompletionWatch.IsReached(null, [completed], [checking]));
    }
}
