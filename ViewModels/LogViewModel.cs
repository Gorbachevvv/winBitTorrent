using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinBitTorrent.Services;

namespace WinBitTorrent.ViewModels;

public sealed partial class LogViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private long _lastId = -1;
    private long _lastPeerId = -1;

    public LogViewModel(MainViewModel main) => _main = main;

    public ObservableCollection<LogEntryViewModel> Entries { get; } = [];
    public ObservableCollection<PeerLogEntryViewModel> PeerEntries { get; } = [];

    [ObservableProperty]
    private bool _showNormal = true;

    [ObservableProperty]
    private bool _showInformation = true;

    [ObservableProperty]
    private bool _showWarning = true;

    [ObservableProperty]
    private bool _showCritical = true;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_main.Api is null)
            return;
        var values = await _main.Api.Logs.GetMainAsync(_lastId, ShowNormal, ShowInformation, ShowWarning, ShowCritical);
        foreach (var value in values.OfType<JsonObject>())
        {
            var item = LogEntryViewModel.FromJson(value);
            Entries.Add(item);
            _lastId = Math.Max(_lastId, item.Id);
        }

        while (Entries.Count > 5000)
            Entries.RemoveAt(0);

        var peers = await _main.Api.Logs.GetPeersAsync(_lastPeerId);
        foreach (var value in peers.OfType<JsonObject>())
        {
            var item = PeerLogEntryViewModel.FromJson(value);
            PeerEntries.Add(item);
            _lastPeerId = Math.Max(_lastPeerId, item.Id);
        }
        while (PeerEntries.Count > 5000)
            PeerEntries.RemoveAt(0);
    }

    [RelayCommand]
    public void Clear()
    {
        Entries.Clear();
        PeerEntries.Clear();
        _lastId = -1;
        _lastPeerId = -1;
    }
}

public sealed record PeerLogEntryViewModel(long Id, DateTimeOffset Timestamp, string Address, bool Blocked, string Reason)
{
    public string Time => Timestamp.LocalDateTime.ToString("G");
    public string Action => Blocked ? Localizer.Get("Log_Blocked", "Blocked") : Localizer.Get("Log_Allowed", "Allowed");

    public static PeerLogEntryViewModel FromJson(JsonObject value)
    {
        var timestamp = value["timestamp"]?.GetValue<long>() ?? 0;
        return new PeerLogEntryViewModel(
            value["id"]?.GetValue<long>() ?? -1,
            timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(timestamp) : DateTimeOffset.Now,
            value["ip"]?.GetValue<string>() ?? string.Empty,
            value["blocked"]?.GetValue<bool>() ?? false,
            value["reason"]?.GetValue<string>() ?? string.Empty);
    }
}

public sealed record LogEntryViewModel(long Id, DateTimeOffset Timestamp, int Type, string Message)
{
    public string Time => Timestamp.LocalDateTime.ToString("G");
    public string Level => Type switch
    {
        1 => Localizer.Get("Log_Normal", "Normal"),
        2 => Localizer.Get("Log_Information", "Information"),
        4 => Localizer.Get("Log_Warning", "Warning"),
        8 => Localizer.Get("Log_Critical", "Critical"),
        _ => Type.ToString()
    };

    public static LogEntryViewModel FromJson(JsonObject value)
    {
        var timestamp = value["timestamp"]?.GetValue<long>() ?? 0;
        return new LogEntryViewModel(
            value["id"]?.GetValue<long>() ?? -1,
            timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(timestamp) : DateTimeOffset.Now,
            value["type"]?.GetValue<int>() ?? 0,
            value["message"]?.GetValue<string>() ?? string.Empty);
    }
}
