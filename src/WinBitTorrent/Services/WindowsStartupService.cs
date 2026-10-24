using Microsoft.Win32;
using Windows.ApplicationModel;

namespace WinBitTorrent.Services;

internal static class WindowsStartupService
{
    private const string TaskId = "WinBitTorrentStartup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinBitTorrent";
    private static string LegacyShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "WinBitTorrent.lnk");

    public static bool IsPackaged
    {
        get
        {
            try { return Package.Current is not null; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public static async Task<bool> IsEnabledAsync()
    {
        if (IsPackaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string || File.Exists(LegacyShortcut);
    }

    public static async Task SetEnabledAsync(bool enabled)
    {
        if (IsPackaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                var state = await task.RequestEnableAsync();
                if (state is not (StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy))
                    throw new InvalidOperationException(Localizer.Get("SetupText_StartupBlocked", "Windows has disabled startup for this app. Enable it in Settings → Apps → Startup, or turn off this option to continue."));
            }
            else
            {
                task.Disable();
                if (task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
                    throw new InvalidOperationException(Localizer.Get("SetupText_StartupPolicy", "Startup is controlled by your administrator and could not be disabled."));
            }
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            // Preserve an existing installer shortcut without creating a second launch entry.
            if (!File.Exists(LegacyShortcut))
                key.SetValue(ValueName, $"\"{Environment.ProcessPath ?? throw new InvalidOperationException("Application path unavailable.")}\"");
        }
        else
        {
            key.DeleteValue(ValueName, false);
            if (File.Exists(LegacyShortcut)) File.Delete(LegacyShortcut);
        }
    }
}
