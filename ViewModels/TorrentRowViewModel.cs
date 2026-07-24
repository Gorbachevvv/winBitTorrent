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

    public TorrentRowViewModel(TorrentInfo model) => _model = model;

    public TorrentInfo Model => _model;
    public string Hash => _model.Hash;
    public int QueuePosition => _model.QueuePosition;
    public string Name => _model.Name;
    public string Size => ValueFormatter.Size(_model.Size);
    public string TotalSize => ValueFormatter.Size(_model.TotalSize);
    public double ProgressValue => Math.Clamp(_model.Progress * 100, 0, 100);
    public string Progress => ValueFormatter.Percentage(_model.Progress);
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
    public double AvailabilityValue => Math.Clamp(_model.Availability * 100, 0, 100);
    public string Availability => ValueFormatter.Percentage(_model.Availability);
    public string DownloadPath => _model.DownloadPath;
    public string InfoHashV1 => _model.InfoHashV1;
    public string InfoHashV2 => _model.InfoHashV2;
    public string Reannounce => ValueFormatter.Duration(_model.Reannounce);
    public string Private => _model.IsPrivate ? Localizer.Get("Common_Yes", "Yes") : Localizer.Get("Common_No", "No");
    public bool IsActive => _model.DownloadSpeed > 0 || _model.UploadSpeed > 0 || _model.State.Contains("downloading", StringComparison.OrdinalIgnoreCase);
    public bool IsStopped => _model.State.Contains("paused", StringComparison.OrdinalIgnoreCase)
        || _model.State.Contains("stopped", StringComparison.OrdinalIgnoreCase);

    public void Update(TorrentInfo model)
    {
        _model = model;
        OnPropertyChanged(string.Empty);
    }

    // Optimistic local updates so the context menu reflects a toggled flag immediately, without
    // waiting for the next maindata delta to re-report this torrent (which may be a long time for
    // an idle torrent). The next real refresh overwrites these with the server's truth.
    public void ApplyForceStart(bool value) => SetFlag(() => _model.ForceStart = value);
    public void ApplySequentialDownload(bool value) => SetFlag(() => _model.SequentialDownload = value);
    public void ApplyFirstLastPiecePriority(bool value) => SetFlag(() => _model.FirstLastPiecePriority = value);

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
