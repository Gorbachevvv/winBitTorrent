#if STORE_BUILD
using Microsoft.UI.Xaml;

namespace WinBitTorrent;

public sealed partial class MainWindow
{
    // Store distribution owns updates. The installer download/launch implementation
    // is excluded from compilation; this keeps the shared startup call harmless.
    public void ScheduleStartupUpdateCheck() { }
    private void CheckForUpdates_Click(object sender, RoutedEventArgs e) { }
}
#endif
