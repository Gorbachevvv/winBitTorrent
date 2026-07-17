namespace WinBitTorrent.Services;

internal enum TrayIconCommand
{
    ToggleWindow = 1001,
    AddTorrentFile = 1002,
    AddTorrentLink = 1003,
    ToggleAlternativeSpeedLimits = 1004,
    GlobalSpeedLimits = 1005,
    StartAll = 1006,
    StopAll = 1007,
    Options = 1008,
    Exit = 1009
}
