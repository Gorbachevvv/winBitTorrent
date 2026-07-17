using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinBitTorrent.Services;

internal sealed class TrayIconService : IDisposable
{
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_SHOWTIP = 0x00000080;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint WM_APP = 0x8000;
    private const uint WM_NULL = 0x0000;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MBUTTONUP = 0x0208;
    private const int GWLP_WNDPROC = -4;
    private const int IDI_APPLICATION = 32512;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint CallbackMessage = WM_APP + 0x51;
    private const uint IconId = 1;

    private readonly IntPtr _hwnd;
    private readonly Action<TrayIconCommand> _executeCommand;
    private readonly Func<bool> _isWindowVisible;
    private readonly Func<bool> _isConnected;
    private readonly Func<bool> _isAlternativeSpeedLimitsEnabled;
    private readonly uint _taskbarCreatedMessage;
    private readonly WindowProcedure _windowProcedure;
    private readonly IntPtr _previousWindowProcedure;
    private IntPtr _icon;
    private bool _isIconAdded;
    private bool _disposed;

    public TrayIconService(
        IntPtr hwnd,
        Action<TrayIconCommand> executeCommand,
        Func<bool> isWindowVisible,
        Func<bool> isConnected,
        Func<bool> isAlternativeSpeedLimitsEnabled)
    {
        _hwnd = hwnd;
        _executeCommand = executeCommand;
        _isWindowVisible = isWindowVisible;
        _isConnected = isConnected;
        _isAlternativeSpeedLimitsEnabled = isAlternativeSpeedLimitsEnabled;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _windowProcedure = ProcessWindowMessage;
        _previousWindowProcedure = SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_windowProcedure));
        _icon = LoadTrayIcon();
        AddOrUpdateIcon();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        DeleteIcon();
        if (_previousWindowProcedure != IntPtr.Zero)
            SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, _previousWindowProcedure);
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
        _disposed = true;
    }

    private IntPtr ProcessWindowMessage(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            var notification = (uint)lParam.ToInt64() & 0xffff;
            switch (notification)
            {
                case WM_CONTEXTMENU:
                case WM_RBUTTONUP:
                case WM_MBUTTONUP:
                    ShowContextMenu();
                    return IntPtr.Zero;
                case WM_LBUTTONDBLCLK:
                    _executeCommand(TrayIconCommand.ToggleWindow);
                    return IntPtr.Zero;
            }
        }
        else if (message == _taskbarCreatedMessage)
        {
            _isIconAdded = false;
            AddOrUpdateIcon();
        }

        return CallWindowProcW(_previousWindowProcedure, hwnd, message, wParam, lParam);
    }

    private void AddOrUpdateIcon()
    {
        var data = CreateIconData();
        if (!_isIconAdded)
        {
            if (Shell_NotifyIconW(NIM_ADD, ref data))
            {
                data.uVersion = NOTIFYICON_VERSION_4;
                Shell_NotifyIconW(NIM_SETVERSION, ref data);
                _isIconAdded = true;
            }
            return;
        }

        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private void DeleteIcon()
    {
        if (!_isIconAdded)
            return;

        var data = CreateIconData();
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _isIconAdded = false;
    }

    private NOTIFYICONDATA CreateIconData()
        => new()
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = "WinBitTorrent"
        };

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            var connected = _isConnected();
            AddMenuItem(menu, TrayIconCommand.ToggleWindow, _isWindowVisible()
                ? Localizer.Get("Tray_Hide", "Hide WinBitTorrent")
                : Localizer.Get("Tray_Show", "Show WinBitTorrent"));
            AddSeparator(menu);
            AddMenuItem(menu, TrayIconCommand.AddTorrentFile, Localizer.Get("Tray_AddTorrentFile", "Add Torrent File..."), connected);
            AddMenuItem(menu, TrayIconCommand.AddTorrentLink, Localizer.Get("Tray_AddTorrentLink", "Add Torrent Link..."), connected);
            AddSeparator(menu);
            AddMenuItem(menu, TrayIconCommand.ToggleAlternativeSpeedLimits, Localizer.Get("Tray_AlternativeSpeedLimits", "Alternative speed limits"), connected, _isAlternativeSpeedLimitsEnabled());
            AddMenuItem(menu, TrayIconCommand.GlobalSpeedLimits, Localizer.Get("Tray_GlobalSpeedLimits", "Global speed limits..."), connected);
            AddSeparator(menu);
            AddMenuItem(menu, TrayIconCommand.StartAll, Localizer.Get("Tray_StartAll", "Resume all"), connected);
            AddMenuItem(menu, TrayIconCommand.StopAll, Localizer.Get("Tray_StopAll", "Pause all"), connected);
            AddSeparator(menu);
            AddMenuItem(menu, TrayIconCommand.Options, Localizer.Get("Tray_Options", "Options..."));
            AddMenuItem(menu, TrayIconCommand.Exit, Localizer.Get("Tray_Exit", "Exit WinBitTorrent"));

            if (!GetCursorPos(out var point))
                point = new POINT();

            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | TPM_LEFTALIGN, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WM_NULL, UIntPtr.Zero, IntPtr.Zero);
            if (command > 0)
                _executeCommand((TrayIconCommand)command);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void AddMenuItem(IntPtr menu, TrayIconCommand command, string text, bool enabled = true, bool isChecked = false)
    {
        var flags = MF_STRING;
        if (!enabled)
            flags |= MF_GRAYED;
        if (isChecked)
            flags |= MF_CHECKED;
        AppendMenuW(menu, flags, (UIntPtr)(uint)command, text);
    }

    private static void AddSeparator(IntPtr menu)
        => AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);

    private static IntPtr LoadTrayIcon()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                var largeIcons = new IntPtr[1];
                var smallIcons = new IntPtr[1];
                if (ExtractIconExW(exePath, 0, largeIcons, smallIcons, 1) > 0)
                {
                    if (largeIcons[0] != IntPtr.Zero)
                        DestroyIcon(largeIcons[0]);
                    if (smallIcons[0] != IntPtr.Zero)
                        return smallIcons[0];
                }
            }
        }
        catch
        {
        }

        return LoadIconW(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
    }

    private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);
}
