using WinBitTorrent.Core.Models;
using WinBitTorrent.Core.Services;

namespace WinBitTorrent.Core.Tests;

public sealed class TorrentLifecycleMonitorTests
{
    [Fact]
    public void FullUpdateEstablishesBaselineWithoutReplayingEvents()
    {
        var monitor = new TorrentLifecycleMonitor();

        var events = monitor.Observe(ChangeSet(
            fullUpdate: true,
            Torrent("a", "Already complete", progress: 1, amountLeft: 0),
            Torrent("b", "Already broken", progress: .5, amountLeft: 50, state: "error")));

        Assert.Empty(events);
    }

    [Fact]
    public void FirstIncrementalResponseAlsoEstablishesBaselineWithoutReplayingEvents()
    {
        var monitor = new TorrentLifecycleMonitor();

        var events = monitor.Observe(ChangeSet(
            fullUpdate: false,
            Torrent("a", "Already complete", progress: 1, amountLeft: 0, state: "stalledUP"),
            Torrent("b", "Already broken", progress: .5, amountLeft: 50, state: "error")));

        Assert.Empty(events);
    }

    [Fact]
    public void ResetMakesTheNextResponseABaselineAgain()
    {
        var monitor = new TorrentLifecycleMonitor();
        monitor.Observe(ChangeSet(true, Torrent("a", "Alpha", .5, 50)));
        monitor.Reset();

        var events = monitor.Observe(ChangeSet(false, Torrent("a", "Alpha", 1, 0, "stalledUP")));

        Assert.Empty(events);
    }

    [Fact]
    public void StartupSeedDoesNotCompleteWhenItsDelayedProgressArrives()
    {
        var monitor = new TorrentLifecycleMonitor();
        monitor.Observe(ChangeSet(
            true,
            Torrent("a", "Old partial seed", 0, 0, "stalledUP")));

        var events = monitor.Observe(ChangeSet(
            false,
            Torrent("a", "Old partial seed", 1, 0, "stalledUP")));

        Assert.Empty(events);
    }

    [Fact]
    public void StartupTorrentWithDelayedStateIsNotMistakenForACompletedDownload()
    {
        var monitor = new TorrentLifecycleMonitor();
        monitor.Observe(ChangeSet(true, Torrent("a", "Old seed", 0, 0, "")));

        var events = monitor.Observe(ChangeSet(
            false,
            Torrent("a", "Old seed", 1, 0, "stalledUP")));

        Assert.Empty(events);
    }

    [Fact]
    public void EmitsCompletionOnlyOnTheIncompleteToCompleteTransition()
    {
        var monitor = new TorrentLifecycleMonitor();
        monitor.Observe(ChangeSet(true, Torrent("a", "Alpha", .75, 25)));

        var completed = monitor.Observe(ChangeSet(false, Torrent("a", "Alpha", 1, 0)));
        var unchanged = monitor.Observe(ChangeSet(false, Torrent("a", "Alpha", 1, 0, "stalledUP")));

        var torrentEvent = Assert.Single(completed);
        Assert.Equal(TorrentLifecycleEventKind.DownloadCompleted, torrentEvent.Kind);
        Assert.Equal("a", torrentEvent.TorrentHash);
        Assert.Equal("Alpha", torrentEvent.TorrentName);
        Assert.Empty(unchanged);
    }

    [Fact]
    public void EmitsErrorOncePerEntryIntoAnErrorState()
    {
        var monitor = new TorrentLifecycleMonitor();
        monitor.Observe(ChangeSet(true, Torrent("a", "Alpha", .5, 50)));

        var firstError = monitor.Observe(ChangeSet(false, Torrent("a", "Alpha", .5, 50, "error")));
        var sameError = monitor.Observe(ChangeSet(false, Torrent("a", "Alpha", .5, 50, "missingFiles")));
        monitor.Observe(ChangeSet(false, Torrent("a", "Alpha", .5, 50, "downloading")));
        var secondError = monitor.Observe(ChangeSet(false, Torrent("a", "Alpha", .5, 50, "error")));

        Assert.Equal(TorrentLifecycleEventKind.Error, Assert.Single(firstError).Kind);
        Assert.Empty(sameError);
        Assert.Equal(TorrentLifecycleEventKind.Error, Assert.Single(secondError).Kind);
    }

    [Fact]
    public void NewTorrentProducesOnlyTheAddedEventEvenWhenItIsAlreadyComplete()
    {
        var monitor = new TorrentLifecycleMonitor();
        monitor.Observe(ChangeSet(true));

        var events = monitor.Observe(ChangeSet(false, Torrent("a", "Seed data", 1, 0, "uploading")));

        Assert.Equal(TorrentLifecycleEventKind.Added, Assert.Single(events).Kind);
    }

    [Fact]
    public void CompletionWaitsUntilMovingHasFinished()
    {
        var monitor = new TorrentLifecycleMonitor();
        monitor.Observe(ChangeSet(true, Torrent("a", "Alpha", .99, 1)));

        var moving = monitor.Observe(ChangeSet(false, Torrent("a", "Alpha", 1, 0, "moving")));
        var moved = monitor.Observe(ChangeSet(false, Torrent("a", "Alpha", 1, 0, "uploading")));

        Assert.Empty(moving);
        Assert.Equal(TorrentLifecycleEventKind.DownloadCompleted, Assert.Single(moved).Kind);
    }

    private static MainDataChangeSet ChangeSet(bool fullUpdate, params TorrentInfo[] torrents)
        => new(fullUpdate, torrents, [], new ServerState());

    private static TorrentInfo Torrent(
        string hash,
        string name,
        double progress,
        long amountLeft,
        string state = "downloading")
        => new()
        {
            Hash = hash,
            Name = name,
            Progress = progress,
            AmountLeft = amountLeft,
            State = state
        };
}
