using WinBitTorrent.Core.Services;

namespace WinBitTorrent.Services;

public interface IAppNotificationService
{
    event EventHandler? NotificationInvoked;

    void Initialize();
    void Shutdown();
    void SetFallbackPresenter(Func<string, string, bool>? presenter);
    void Publish(IReadOnlyList<TorrentLifecycleEvent> events);
    void ShowTorrentAddFailed(string reason);
    void ShowMigrationReport(int torrentCount, int needsHashCheck, string backupPath);
}
