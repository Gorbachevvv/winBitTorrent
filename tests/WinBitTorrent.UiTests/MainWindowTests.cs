using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace WinBitTorrent.UiTests;

public sealed class MainWindowTests
{
    [UiFact]
    public void MainWindowExposesOriginalWorkspaceAndKeyboardFocus()
    {
        using var application = Application.Launch(UiFactAttribute.FindExecutable()!);
        using var automation = new UIA3Automation();
        try
        {
            var window = Retry.WhileNull(() => application.GetMainWindow(automation), TimeSpan.FromSeconds(15)).Result;
            Assert.NotNull(window);
            Assert.Contains("WinBitTorrent", window!.Title, StringComparison.OrdinalIgnoreCase);
            var tabs = window.FindAllDescendants(condition => condition.ByControlType(ControlType.TabItem));
            Assert.True(tabs.Length >= 4, $"Expected at least four workspace tabs, found {tabs.Length}.");
            window.Focus();
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.TAB);
            Assert.NotNull(automation.FocusedElement());
        }
        finally
        {
            try { application.Close(); } catch (InvalidOperationException) { }
            try { if (!application.HasExited) application.Kill(); } catch (InvalidOperationException) { }
        }
    }
}

public sealed class UiFactAttribute : FactAttribute
{
    public UiFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("WINBITTORRENT_UI_TESTS"), "1", StringComparison.Ordinal)
            || FindExecutable() is null)
            Skip = "Set WINBITTORRENT_UI_TESTS=1 after building the x64 app to run interactive FlaUI tests.";
    }

    public static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("WINBITTORRENT_EXE");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var bin = Path.Combine(directory.FullName, "bin", "x64", "Debug", "net8.0-windows10.0.19041.0", "win-x64", "WinBitTorrent.exe");
            if (File.Exists(bin)) return bin;
            directory = directory.Parent;
        }
        return null;
    }
}
