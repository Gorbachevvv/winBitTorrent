using WinBitTorrent.Core.Models;
using WinBitTorrent.Core.Services;

namespace WinBitTorrent.Core.Tests;

public sealed class DownloadCompletionWatchTests
{
    private static readonly TorrentLifecycleEvent CompletedA = new(TorrentLifecycleEventKind.DownloadCompleted, "a", "Alpha");

    [Fact]
    public void AllDownloadsFireWhenTheLastOneCompletes()
        => Assert.True(DownloadCompletionWatch.IsReached(null, [CompletedA], [Torrent("a", "stalledUP"), Torrent("b", "stoppedDL"), Torrent("c", "error")]));

    [Theory]
    [InlineData("downloading")]
    [InlineData("stalledDL")]
    [InlineData("queuedDL")]
    [InlineData("metaDL")]
    [InlineData("checkingDL")]
    [InlineData("checkingResumeData")]
    [InlineData("moving")]
    public void AllDownloadsWaitWhileAnotherOneIsPending(string state)
        => Assert.False(DownloadCompletionWatch.IsReached(null, [CompletedA], [Torrent("a", "uploading"), Torrent("b", state)]));

    [Fact]
    public void NothingFiresWithoutACompletionEvent()
    {
        Assert.False(DownloadCompletionWatch.IsReached(null, [], [Torrent("a", "uploading")]));
        Assert.False(DownloadCompletionWatch.IsReached("a", [new(TorrentLifecycleEventKind.Added, "a", "Alpha")], []));
    }

    [Fact]
    public void SingleTorrentFiresOnlyForItsOwnCompletion()
    {
        Assert.True(DownloadCompletionWatch.IsReached("A", [CompletedA], [Torrent("a", "uploading"), Torrent("b", "downloading")]));
        Assert.False(DownloadCompletionWatch.IsReached("b", [CompletedA], [Torrent("a", "uploading"), Torrent("b", "downloading")]));
    }

    [Fact]
    public void RecheckingCountdownRequiresTheTargetToStillBeComplete()
    {
        Assert.False(DownloadCompletionWatch.CanRun("a", []));
        Assert.False(DownloadCompletionWatch.CanRun("a", [Torrent("a", "moving")]));
        Assert.False(DownloadCompletionWatch.CanRun(null, [Torrent("b", "downloading")]));
        Assert.True(DownloadCompletionWatch.CanRun("a", [Torrent("a", "stalledUP"), Torrent("b", "downloading")]));
    }

    private static TorrentInfo Torrent(string hash, string state) => new()
    {
        Hash = hash, State = state, Progress = state == "uploading" || state.EndsWith("UP", StringComparison.Ordinal) ? 1 : 0
    };
}
