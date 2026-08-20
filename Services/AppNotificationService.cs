using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using WinBitTorrent.Core.Services;

namespace WinBitTorrent.Services;

/// <summary>Delivers torrent events through the native Windows notification center.</summary>
public sealed class AppNotificationService(ILogger<AppNotificationService> logger) : IAppNotificationService
{
    private bool _initializationAttempted;
    private bool _registered;
    private bool _handlerAttached;
    private Func<string, string, bool>? _fallbackPresenter;

    public event EventHandler? NotificationInvoked;

    public void Initialize()
    {
        if (_initializationAttempted)
            return;

        _initializationAttempted = true;
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                logger.LogInformation("Native Windows notifications are not supported on this system.");
                return;
            }

            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            _handlerAttached = true;
            // For an unpackaged app this overload creates the required COM registration and
            // lets Windows obtain the display name and icon from the executable.
            manager.Register();
            _registered = true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Native Windows notifications could not be registered.");
        }
    }

    public void SetFallbackPresenter(Func<string, string, bool>? presenter)
        => _fallbackPresenter = presenter;

    public void Shutdown()
    {
        if (!_registered && !_handlerAttached)
            return;

        try
        {
            var manager = AppNotificationManager.Default;
            if (_handlerAttached)
                manager.NotificationInvoked -= OnNotificationInvoked;
            if (_registered)
                manager.Unregister();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Native Windows notifications could not be unregistered cleanly.");
        }
        finally
        {
            _registered = false;
            _handlerAttached = false;
            _fallbackPresenter = null;
        }
    }

    public void Publish(IReadOnlyList<TorrentLifecycleEvent> events)
    {
        if (!CanShow || !NotificationPreferences.Enabled)
            return;

        foreach (var torrentEvent in events)
        {
            if (torrentEvent.Kind == TorrentLifecycleEventKind.Added
                && !NotificationPreferences.TorrentAddedEnabled)
            {
                continue;
            }

            var (title, message) = torrentEvent.Kind switch
            {
                TorrentLifecycleEventKind.DownloadCompleted => (
                    Localizer.Get("Notification_DownloadCompleted_Title", "Download completed"),
                    string.Format(
                        Localizer.Get("Notification_DownloadCompleted_Message", "'{0}' has finished downloading."),
                        torrentEvent.TorrentName)),
                TorrentLifecycleEventKind.Error => (
                    Localizer.Get("Notification_TorrentError_Title", "Torrent error"),
                    string.Format(
                        Localizer.Get("Notification_TorrentError_Message", "An error occurred for torrent '{0}'. Check its status for details."),
                        torrentEvent.TorrentName)),
                _ => (
                    Localizer.Get("Notification_TorrentAdded_Title", "Torrent added"),
                    string.Format(
                        Localizer.Get("Notification_TorrentAdded_Message", "'{0}' was added."),
                        torrentEvent.TorrentName))
            };

            Show(title, message, torrentEvent.TorrentHash);
        }
    }

    public void ShowTorrentAddFailed(string reason)
    {
        if (!CanShow || !NotificationPreferences.Enabled)
            return;

        Show(
            Localizer.Get("Notification_AddFailed_Title", "Add torrent failed"),
            string.Format(
                Localizer.Get("Notification_AddFailed_Message", "Couldn't add the torrent. Reason: {0}"),
                reason),
            null);
    }

    public void ShowMigrationReport(int torrentCount, int needsHashCheck, string backupPath)
    {
        if (!CanShow)
            return;

        var summary = needsHashCheck == 0
            ? $"Imported {torrentCount} torrents. Existing data was not moved."
            : $"Imported {torrentCount} torrents. {needsHashCheck} must be hash-checked before starting.";
        Show(
            Localizer.Get("Notification_Migration_Title", "Torrent profile migration completed"),
            $"{summary} Backup: {backupPath}",
            null);
    }

    private void Show(string title, string message, string? torrentHash)
    {
        try
        {
            if (!TryShowCore(title, message, torrentHash))
                logger.LogWarning("Windows rejected a native notification request.");
        }
        catch (Exception exception)
        {
            // A disabled system-level notification setting or unavailable notification platform
            // must never interrupt the torrent synchronization loop.
            logger.LogWarning(exception, "Unable to display a native Windows notification.");
        }
    }

    private bool TryShowCore(string title, string message, string? torrentHash)
    {
        if (_registered)
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "show")
                .AddText(title)
                .AddText(message);

            if (!string.IsNullOrWhiteSpace(torrentHash))
                builder.AddArgument("torrentHash", torrentHash);

            AppNotificationManager.Default.Show(builder.BuildNotification());
            return true;
        }

        return _fallbackPresenter?.Invoke(title, message) == true;
    }

    private bool CanShow => _registered || _fallbackPresenter is not null;

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
        => NotificationInvoked?.Invoke(this, EventArgs.Empty);
}
