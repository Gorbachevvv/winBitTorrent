using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinBitTorrent.EngineHost;

internal sealed partial class EngineState
{
    private static void ApplyProcessLimits(JsonElement preferences)
    {
        var mebibytes = preferences.TryGetProperty("memory_working_set_limit", out var value) && value.TryGetInt64(out var parsed)
            ? parsed : 0;
        if (mebibytes < 0) throw new ArgumentOutOfRangeException("memory_working_set_limit");
        var maximum = mebibytes == 0
            ? new IntPtr(-1)
            : new IntPtr(checked(Math.Min(mebibytes * 1024L * 1024L, (long)nint.MaxValue)));
        var minimum = mebibytes == 0 ? new IntPtr(-1) : new IntPtr(Math.Min(16L * 1024 * 1024, maximum.ToInt64()));
        using var process = Process.GetCurrentProcess();
        if (!SetProcessWorkingSetSize(process.Handle, minimum, maximum))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to apply the EngineHost working-set limit.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);
}
