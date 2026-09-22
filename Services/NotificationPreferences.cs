namespace WinBitTorrent.Services;

public static class NotificationPreferences
{
    public const string EnabledKey = "notifications.enabled";
    public const string TorrentAddedKey = "notifications.torrentAdded";

    public static bool Enabled => ClientSettings.Current.Notifications.Enabled;
    public static bool TorrentAddedEnabled => ClientSettings.Current.Notifications.TorrentAdded;
}
