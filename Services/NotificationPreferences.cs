namespace WinBitTorrent.Services;

public static class NotificationPreferences
{
    public const string EnabledKey = "notifications.enabled";
    public const string TorrentAddedKey = "notifications.torrentAdded";

    public static bool Enabled => ClientSettings.Get(EnabledKey, true);
    public static bool TorrentAddedEnabled => ClientSettings.Get(TorrentAddedKey, false);
}
