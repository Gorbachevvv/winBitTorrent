using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Core.Services;
using WinBitTorrent.Services;

namespace WinBitTorrent.ViewModels;

public sealed class TorrentRowViewModel : ObservableObject
{
    private TorrentInfo _model;
    private RowSnapshot _snapshot;

    public TorrentRowViewModel(TorrentInfo model)
    {
        _model = model;
        _snapshot = new RowSnapshot(model);
    }

    // Table rows fall back to ToString() for their automation name, so without this a screen reader
    // announces the bare type name instead of the torrent.
    public override string ToString() => Name;

    public TorrentInfo Model => _model;
    public string Hash => _model.Hash;
    public int QueuePosition => _model.QueuePosition;
    public string Name => _model.Name;
    public string Size => ValueFormatter.Size(_model.Size);
    public string TotalSize => ValueFormatter.Size(_model.TotalSize);
    public double ProgressValue => Math.Clamp(EffectiveProgress * 100, 0, 100);
    public string Progress => ValueFormatter.Percentage(EffectiveProgress);
    public string State => _model.State;
    public string Status => TorrentStateText(_model.State);
    public string StatusGlyph => StatusVisual(_model.State).Glyph;
    public Brush StatusBrush => StatusVisual(_model.State).Brush;
    public string Seeds => $"{_model.Seeds} ({_model.TotalSeeds})";
    public string Peers => $"{_model.Peers} ({_model.TotalPeers})";
    public string DownloadSpeed => ValueFormatter.Speed(_model.DownloadSpeed);
    public string UploadSpeed => ValueFormatter.Speed(_model.UploadSpeed);
    public string Eta => ValueFormatter.Duration(_model.Eta);
    public string Ratio => ValueFormatter.Ratio(_model.Ratio);
    public string Popularity => ValueFormatter.Ratio(_model.Popularity);
    public string Category => _model.Category;
    public string Tags => _model.Tags;
    public string AddedOn => FormatDate(_model.AddedOn);
    public string CompletedOn => FormatDate(_model.CompletedOn);
    public string CreatedOn => FormatDate(_model.CreatedOn);
    public string Tracker => _model.Tracker;
    public string DownloadLimit => _model.DownloadLimit <= 0 ? "∞" : ValueFormatter.Speed(_model.DownloadLimit);
    public string UploadLimit => _model.UploadLimit <= 0 ? "∞" : ValueFormatter.Speed(_model.UploadLimit);
    public string Downloaded => ValueFormatter.Size(_model.Downloaded);
    public string Uploaded => ValueFormatter.Size(_model.Uploaded);
    public string DownloadedSession => ValueFormatter.Size(_model.DownloadedSession);
    public string UploadedSession => ValueFormatter.Size(_model.UploadedSession);
    public string AmountLeft => ValueFormatter.Size(_model.AmountLeft);
    public string TimeActive => ValueFormatter.Duration(_model.TimeActive);
    public string SavePath => _model.SavePath;
    public string Completed => ValueFormatter.Size(_model.Completed);
    public string RatioLimit => _model.RatioLimit < 0 ? "∞" : ValueFormatter.Ratio(_model.RatioLimit);
    public string SeenComplete => FormatDate(_model.SeenComplete);
    public string LastActivity => FormatDate(_model.LastActivity);
    public double AvailabilityCopies => _model.Availability;
    public string Availability => _model.Availability < 0
        ? "—"
        : _model.Availability.ToString("0.000", CultureInfo.CurrentCulture);
    public string DownloadPath => _model.DownloadPath;
    public string InfoHashV1 => _model.InfoHashV1;
    public string InfoHashV2 => _model.InfoHashV2;
    public string Reannounce => ValueFormatter.Duration(_model.Reannounce);
    public string Private => _model.IsPrivate ? Localizer.Get("Common_Yes", "Yes") : Localizer.Get("Common_No", "No");
    public bool IsActive => _model.DownloadSpeed > 0 || _model.UploadSpeed > 0 || _model.State.Contains("downloading", StringComparison.OrdinalIgnoreCase);
    public bool IsStopped => _model.State.Contains("paused", StringComparison.OrdinalIgnoreCase)
        || _model.State.Contains("stopped", StringComparison.OrdinalIgnoreCase);
    public bool ShowAvailabilityMap => !IsStopped && EffectiveProgress < 1d;

    public void Update(TorrentInfo model)
    {
        var previous = _snapshot;
        _model = model;
        _snapshot = new RowSnapshot(model);
        // The accumulator patches TorrentInfo in place for remote profiles, so compare
        // with captured scalar values rather than the previous model reference.
        if (previous.Hash != _snapshot.Hash)
        {
            OnPropertyChanged(nameof(Hash));
        }
        if (previous.QueuePosition != _snapshot.QueuePosition)
        {
            OnPropertyChanged(nameof(QueuePosition));
        }
        if (previous.Name != _snapshot.Name)
        {
            OnPropertyChanged(nameof(Name));
        }
        if (previous.Size != _snapshot.Size)
        {
            OnPropertyChanged(nameof(Size));
        }
        if (previous.TotalSize != _snapshot.TotalSize)
        {
            OnPropertyChanged(nameof(TotalSize));
        }
        if (previous.Progress != _snapshot.Progress)
        {
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(ProgressValue));
            OnPropertyChanged(nameof(ShowAvailabilityMap));
        }
        if (previous.State != _snapshot.State)
        {
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(StatusGlyph));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(ProgressValue));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsStopped));
            OnPropertyChanged(nameof(ShowAvailabilityMap));
        }
        if (previous.Seeds != _snapshot.Seeds)
        {
            OnPropertyChanged(nameof(Seeds));
        }
        if (previous.TotalSeeds != _snapshot.TotalSeeds)
        {
            OnPropertyChanged(nameof(Seeds));
        }
        if (previous.Peers != _snapshot.Peers)
        {
            OnPropertyChanged(nameof(Peers));
        }
        if (previous.TotalPeers != _snapshot.TotalPeers)
        {
            OnPropertyChanged(nameof(Peers));
        }
        if (previous.DownloadSpeed != _snapshot.DownloadSpeed)
        {
            OnPropertyChanged(nameof(DownloadSpeed));
            OnPropertyChanged(nameof(IsActive));
        }
        if (previous.UploadSpeed != _snapshot.UploadSpeed)
        {
            OnPropertyChanged(nameof(UploadSpeed));
            OnPropertyChanged(nameof(IsActive));
        }
        if (previous.Eta != _snapshot.Eta)
        {
            OnPropertyChanged(nameof(Eta));
        }
        if (previous.AddedOn != _snapshot.AddedOn)
        {
            OnPropertyChanged(nameof(AddedOn));
        }
        if (previous.CompletedOn != _snapshot.CompletedOn)
        {
            OnPropertyChanged(nameof(CompletedOn));
        }
        if (previous.CreatedOn != _snapshot.CreatedOn)
        {
            OnPropertyChanged(nameof(CreatedOn));
        }
        if (previous.DownloadLimit != _snapshot.DownloadLimit)
        {
            OnPropertyChanged(nameof(DownloadLimit));
        }
        if (previous.UploadLimit != _snapshot.UploadLimit)
        {
            OnPropertyChanged(nameof(UploadLimit));
        }
        if (previous.Downloaded != _snapshot.Downloaded)
        {
            OnPropertyChanged(nameof(Downloaded));
        }
        if (previous.Uploaded != _snapshot.Uploaded)
        {
            OnPropertyChanged(nameof(Uploaded));
        }
        if (previous.DownloadedSession != _snapshot.DownloadedSession)
        {
            OnPropertyChanged(nameof(DownloadedSession));
        }
        if (previous.UploadedSession != _snapshot.UploadedSession)
        {
            OnPropertyChanged(nameof(UploadedSession));
        }
        if (previous.AmountLeft != _snapshot.AmountLeft)
        {
            OnPropertyChanged(nameof(AmountLeft));
        }
        if (previous.TimeActive != _snapshot.TimeActive)
        {
            OnPropertyChanged(nameof(TimeActive));
        }
        if (previous.Completed != _snapshot.Completed)
        {
            OnPropertyChanged(nameof(Completed));
        }
        if (previous.SeenComplete != _snapshot.SeenComplete)
        {
            OnPropertyChanged(nameof(SeenComplete));
        }
        if (previous.LastActivity != _snapshot.LastActivity)
        {
            OnPropertyChanged(nameof(LastActivity));
        }
        if (previous.Reannounce != _snapshot.Reannounce)
        {
            OnPropertyChanged(nameof(Reannounce));
        }
        if (previous.Ratio != _snapshot.Ratio)
        {
            OnPropertyChanged(nameof(Ratio));
        }
        if (previous.Popularity != _snapshot.Popularity)
        {
            OnPropertyChanged(nameof(Popularity));
        }
        if (previous.RatioLimit != _snapshot.RatioLimit)
        {
            OnPropertyChanged(nameof(RatioLimit));
        }
        if (previous.Availability != _snapshot.Availability)
        {
            OnPropertyChanged(nameof(Availability));
            OnPropertyChanged(nameof(AvailabilityCopies));
        }
        if (previous.Category != _snapshot.Category)
        {
            OnPropertyChanged(nameof(Category));
        }
        if (previous.Tags != _snapshot.Tags)
        {
            OnPropertyChanged(nameof(Tags));
        }
        if (previous.Tracker != _snapshot.Tracker)
        {
            OnPropertyChanged(nameof(Tracker));
        }
        if (previous.SavePath != _snapshot.SavePath)
        {
            OnPropertyChanged(nameof(SavePath));
        }
        if (previous.DownloadPath != _snapshot.DownloadPath)
        {
            OnPropertyChanged(nameof(DownloadPath));
        }
        if (previous.InfoHashV1 != _snapshot.InfoHashV1)
        {
            OnPropertyChanged(nameof(InfoHashV1));
        }
        if (previous.InfoHashV2 != _snapshot.InfoHashV2)
        {
            OnPropertyChanged(nameof(InfoHashV2));
        }
        if (previous.IsPrivate != _snapshot.IsPrivate)
        {
            OnPropertyChanged(nameof(Private));
        }
        OnPropertyChanged(nameof(Model));
    }

    private readonly struct RowSnapshot(TorrentInfo model)
    {
        public readonly string Hash = model.Hash;
        public readonly int QueuePosition = model.QueuePosition;
        public readonly string Name = model.Name;
        public readonly long Size = model.Size;
        public readonly long TotalSize = model.TotalSize;
        public readonly double Progress = model.Progress;
        public readonly string State = model.State;
        public readonly int Seeds = model.Seeds;
        public readonly int TotalSeeds = model.TotalSeeds;
        public readonly int Peers = model.Peers;
        public readonly int TotalPeers = model.TotalPeers;
        public readonly long DownloadSpeed = model.DownloadSpeed;
        public readonly long UploadSpeed = model.UploadSpeed;
        public readonly long Eta = model.Eta;
        public readonly long AddedOn = model.AddedOn;
        public readonly long CompletedOn = model.CompletedOn;
        public readonly long CreatedOn = model.CreatedOn;
        public readonly long DownloadLimit = model.DownloadLimit;
        public readonly long UploadLimit = model.UploadLimit;
        public readonly long Downloaded = model.Downloaded;
        public readonly long Uploaded = model.Uploaded;
        public readonly long DownloadedSession = model.DownloadedSession;
        public readonly long UploadedSession = model.UploadedSession;
        public readonly long AmountLeft = model.AmountLeft;
        public readonly long TimeActive = model.TimeActive;
        public readonly long Completed = model.Completed;
        public readonly long SeenComplete = model.SeenComplete;
        public readonly long LastActivity = model.LastActivity;
        public readonly long Reannounce = model.Reannounce;
        public readonly double Ratio = model.Ratio;
        public readonly double Popularity = model.Popularity;
        public readonly double RatioLimit = model.RatioLimit;
        public readonly double Availability = model.Availability;
        public readonly string Category = model.Category;
        public readonly string Tags = model.Tags;
        public readonly string Tracker = model.Tracker;
        public readonly string SavePath = model.SavePath;
        public readonly string DownloadPath = model.DownloadPath;
        public readonly string InfoHashV1 = model.InfoHashV1;
        public readonly string InfoHashV2 = model.InfoHashV2;
        public readonly bool IsPrivate = model.IsPrivate;
    }

    // Optimistic local updates so the context menu reflects a toggled flag immediately, without
    // waiting for the next maindata delta to re-report this torrent (which may be a long time for
    // an idle torrent). The next real refresh overwrites these with the server's truth.
    public void ApplyForceStart(bool value) => SetFlag(() => _model.ForceStart = value);
    public void ApplySequentialDownload(bool value) => SetFlag(() => _model.SequentialDownload = value);
    public void ApplyFirstLastPiecePriority(bool value) => SetFlag(() => _model.FirstLastPiecePriority = value);
    public void ApplySuperSeeding(bool value) => SetFlag(() => _model.SuperSeeding = value);

    private void SetFlag(Action mutate)
    {
        mutate();
        OnPropertyChanged(string.Empty);
    }

    private static string FormatDate(long seconds)
        => ValueFormatter.UnixDate(seconds)?.LocalDateTime.ToString("g") ?? "—";

    // Compact status icon shown in the leftmost column, in the spirit of qBittorrent's state
    // glyphs: direction arrows for transfers (dimmed while stalled), a pause for stopped
    // downloads, a filled check for finished torrents, a spinner for checking/moving, and a
    // warning for errors.
    // The initial maindata snapshot can contain the authoritative UP state one poll before its
    // progress field. qBittorrent only assigns an UP state after the selected content is
    // complete, so avoid briefly painting an established seed as 0% while that field catches up.
    private double EffectiveProgress
        => _model.State.Equals("uploading", StringComparison.OrdinalIgnoreCase)
            || _model.State.EndsWith("UP", StringComparison.OrdinalIgnoreCase)
                ? 1d
                : _model.Progress;

    private static (string Glyph, SolidColorBrush Brush) StatusVisual(string state) => state switch
    {
        "error" or "missingFiles" => ("", GetBrush(0xDC2626)),
        "uploading" or "forcedUP" => ("", GetBrush(0x16A34A)),
        "stalledUP" => ("", GetBrush(0x94A3B8)),
        "pausedUP" or "stoppedUP" => ("", GetBrush(0x16A34A)),
        "queuedUP" or "queuedDL" => ("", GetBrush(0x94A3B8)),
        "downloading" or "forcedDL" or "metaDL" => ("", GetBrush(0x2563EB)),
        "stalledDL" => ("", GetBrush(0x94A3B8)),
        "pausedDL" or "stoppedDL" => ("", GetBrush(0x64748B)),
        "checkingUP" or "checkingDL" or "checkingResumeData" => ("", GetBrush(0x2563EB)),
        "moving" => ("", GetBrush(0x0D9488)),
        "allocating" => ("", GetBrush(0x94A3B8)),
        _ => ("", GetBrush(0x94A3B8))
    };

    // Icon brushes are shared across rows and created lazily on the UI thread (property getters
    // run during binding), so there is one brush per colour rather than one per torrent.
    private static readonly Dictionary<uint, SolidColorBrush> BrushCache = [];

    private static SolidColorBrush GetBrush(uint rgb)
    {
        if (!BrushCache.TryGetValue(rgb, out var brush))
        {
            brush = new SolidColorBrush(Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            BrushCache[rgb] = brush;
        }
        return brush;
    }

    private static string TorrentStateText(string state) => state switch
    {
        "error" or "missingFiles" => Localizer.Get("TorrentState_Error", "Error"),
        "uploading" or "forcedUP" => Localizer.Get("TorrentState_Seeding", "Seeding"),
        "pausedUP" or "stoppedUP" => Localizer.Get("TorrentState_Completed", "Completed"),
        "queuedUP" => Localizer.Get("TorrentState_QueuedUpload", "Queued for upload"),
        "stalledUP" => Localizer.Get("TorrentState_StalledUpload", "Stalled upload"),
        "checkingUP" or "checkingDL" => Localizer.Get("TorrentState_Checking", "Checking"),
        "downloading" or "forcedDL" or "metaDL" => Localizer.Get("TorrentState_Downloading", "Downloading"),
        "pausedDL" or "stoppedDL" => Localizer.Get("TorrentState_Stopped", "Stopped"),
        "queuedDL" => Localizer.Get("TorrentState_QueuedDownload", "Queued for download"),
        "stalledDL" => Localizer.Get("TorrentState_StalledDownload", "Stalled download"),
        "allocating" => Localizer.Get("TorrentState_Allocating", "Allocating"),
        "moving" => Localizer.Get("TorrentState_Moving", "Moving"),
        "checkingResumeData" => Localizer.Get("TorrentState_CheckingResumeData", "Checking resume data"),
        "unknown" => Localizer.Get("TorrentState_Unknown", "Unknown"),
        _ => state
    };
}
