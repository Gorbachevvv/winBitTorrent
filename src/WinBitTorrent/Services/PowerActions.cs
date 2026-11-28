using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinBitTorrent.Services;

/// <summary> What to do with the computer once the watched downloads complete </summary>
public enum PowerAction
{
    None,
    Sleep,
    Shutdown
}

internal static class PowerActions
{
    // ponytail: SetSuspendState only does classic S3 sleep, on Modern Standby (S0) devices it can return
    // false and leave the PC awake, a WinRT/PowerRequest route would be needed there
    public static bool Sleep() => SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);

    // shutdown.exe takes care of the shutdown privilege, which ExitWindowsEx needs to be granted first
    public static void Shutdown()
        => Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = false, CreateNoWindow = true });

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);
}
