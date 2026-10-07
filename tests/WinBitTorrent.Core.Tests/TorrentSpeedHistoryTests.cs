using WinBitTorrent.Core.Services;

namespace WinBitTorrent.Core.Tests;

public sealed class TorrentSpeedHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SelectionCapturesSpeedAndCollectsHistoryBeforeTheGraphSubscribes()
    {
        var history = new TorrentSpeedHistory();
        history.SelectSource("torrent-a", 40 * 1024 * 1024, 10 * 1024 * 1024, Start);
        history.RecordSample(20 * 1024 * 1024, 5 * 1024 * 1024, Start.AddSeconds(1));
        history.RecordSample(0, 0, Start.AddSeconds(2));

        // Opening a details tab only subscribes to updates; it must retain the earlier peak.
        var updates = 0;
        history.Changed += (_, _) => updates++;
        Assert.Equal(3, history.Samples.Count);
        Assert.Equal(40 * 1024 * 1024, history.Samples.Max(sample => sample.Download));
        Assert.Equal(20 * 1024 * 1024, history.Samples.Average(sample => sample.Download));
        Assert.True(SpeedGraphScale.GetMaximum(history.Samples.Max(sample => sample.Download)) > 40 * 1024 * 1024);

        history.RecordSample(1024, 2048, Start.AddSeconds(3));
        Assert.Equal(1, updates);
        Assert.Equal(4, history.Samples.Count);
        Assert.Equal(2048, history.Latest!.Upload);
    }

    [Fact]
    public void ChangingSelectionStartsANewHistoryWithoutMixingTorrents()
    {
        var history = new TorrentSpeedHistory();
        history.SelectSource("torrent-a", 100, 200, Start);
        history.RecordSample(300, 400, Start.AddSeconds(1));
        history.SelectSource("torrent-b", 10, 20, Start.AddSeconds(2));

        var sample = Assert.Single(history.Samples);
        Assert.Equal(new SpeedSample(Start.AddSeconds(2), 10, 20), sample);
        Assert.Equal("torrent-b", history.SourceId);

        history.SelectSource("torrent-a", 50, 60, Start.AddSeconds(3));
        Assert.Equal(new SpeedSample(Start.AddSeconds(3), 50, 60), Assert.Single(history.Samples));
    }

    [Fact]
    public void SelectingTheSameTorrentAgainKeepsItsHistory()
    {
        var history = new TorrentSpeedHistory();
        history.SelectSource("abcdef", 100, 200, Start);
        history.RecordSample(300, 400, Start.AddSeconds(1));
        history.SelectSource("ABCDEF", 500, 600, Start.AddSeconds(2));

        Assert.Equal(2, history.Samples.Count);
        Assert.Equal(100, history.Samples.First().Download);
        Assert.Equal(300, history.Latest!.Download);
    }

    [Fact]
    public void ClearingSelectionClearsHistoryAndStopsCollection()
    {
        var history = new TorrentSpeedHistory();
        history.SelectSource("torrent-a", 100, 200, Start);
        history.SelectSource(null, 0, 0, Start.AddSeconds(1));
        history.RecordSample(300, 400, Start.AddSeconds(2));

        Assert.Empty(history.Samples);
        Assert.Null(history.Latest);
        Assert.Equal(string.Empty, history.SourceId);
    }

    [Fact]
    public void HistoryRetainsFifteenMinutesIncludingTheBoundary()
    {
        var history = new TorrentSpeedHistory();
        history.SelectSource("torrent-a", 100, 200, Start);
        for (var second = 1; second <= 901; second++)
            history.RecordSample(second, 0, Start.AddSeconds(second));

        Assert.Equal(901, history.Samples.Count);
        Assert.Equal(Start.AddSeconds(1), history.Samples.First().Timestamp);
        Assert.Equal(Start.AddSeconds(901), history.Latest!.Timestamp);
    }

    [Fact]
    public void NegativeSpeedsAreRecordedAsZero()
    {
        var history = new TorrentSpeedHistory();
        history.SelectSource("torrent-a", -1, -2, Start);
        Assert.Equal(new SpeedSample(Start, 0, 0), Assert.Single(history.Samples));
    }
}
