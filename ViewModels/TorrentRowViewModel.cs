using CommunityToolkit.Mvvm.ComponentModel;
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

    private static string FormatDate(long seconds)
        => ValueFormatter.UnixDate(seconds)?.LocalDateTime.ToString("g") ?? "—";

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
