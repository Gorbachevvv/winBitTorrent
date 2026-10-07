namespace WinBitTorrent.Core.Services;

/// <summary>Speed samples for the current selection, independent of the visible details tab.</summary>
public sealed class TorrentSpeedHistory
{
    public const double MaximumHistorySeconds = 900;

    private readonly Queue<SpeedSample> _samples = new();

    public string SourceId { get; private set; } = string.Empty;
    public IReadOnlyCollection<SpeedSample> Samples => _samples;
    public SpeedSample? Latest { get; private set; }
    public event EventHandler? Changed;

    public void SelectSource(string? sourceId, long download, long upload, DateTimeOffset timestamp)
    {
        sourceId ??= string.Empty;
        if (string.Equals(SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
            return;

        SourceId = sourceId;
        _samples.Clear();
        Latest = null;
        if (SourceId.Length > 0)
            RecordSample(download, upload, timestamp);
        else
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordSample(long download, long upload, DateTimeOffset timestamp)
    {
        if (SourceId.Length == 0)
            return;

        Latest = new SpeedSample(timestamp, Math.Max(0, download), Math.Max(0, upload));
        _samples.Enqueue(Latest);
        var oldest = timestamp.AddSeconds(-MaximumHistorySeconds);
        while (_samples.TryPeek(out var sample) && sample.Timestamp < oldest)
            _samples.Dequeue();

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record SpeedSample(DateTimeOffset Timestamp, long Download, long Upload);
