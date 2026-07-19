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

    [UiFact]
    public void ConnectionSettingsExposeGroupedQbittorrentControls()
    {
        using var application = Application.Launch(UiFactAttribute.FindExecutable()!);
        using var automation = new UIA3Automation();
        try
        {
            var mainWindow = Retry.WhileNull(() => application.GetMainWindow(automation), TimeSpan.FromSeconds(15)).Result;
            Assert.NotNull(mainWindow);

            var tools = Retry.WhileNull(
                () => FindByAnyName(mainWindow!, "Tools", "Инструменты"),
                TimeSpan.FromSeconds(10)).Result;
            Assert.NotNull(tools);
            tools!.Click();

            var options = Retry.WhileNull(
                () => FindByAnyName(mainWindow!, "Options…", "Options...", "Настройки…", "Настройки..."),
                TimeSpan.FromSeconds(5)).Result;
            Assert.NotNull(options);
            options!.Click();

            var mainHandle = mainWindow!.FrameworkAutomationElement.NativeWindowHandle;
            var settingsWindow = Retry.WhileNull(
                () => application.GetAllTopLevelWindows(automation)
                    .FirstOrDefault(window => window.FrameworkAutomationElement.NativeWindowHandle != mainHandle),
                TimeSpan.FromSeconds(10)).Result;
            Assert.NotNull(settingsWindow);

            var connection = Retry.WhileNull(
                () => FindByAnyName(settingsWindow!, "Connection", "Соединение"),
                TimeSpan.FromSeconds(5)).Result;
            Assert.NotNull(connection);
            connection!.Click();

            var visibleNames = Retry.While(
                () => settingsWindow!.FindAllDescendants()
                    .Select(SafeName)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .ToArray(),
                names => !names.Any(name => name.Contains("Proxy server", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Прокси-сервер", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(10)).Result;

            var verifiedNames = visibleNames ?? [];
            Assert.Contains(verifiedNames, name => name.Contains("Protocol and listening port", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Протокол и порт прослушивания", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(verifiedNames, name => name.Contains("Connection limits", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Ограничения соединений", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(verifiedNames, name => name.Contains("Proxy server", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Прокси-сервер", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(verifiedNames, name => name.Contains("I2P", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(verifiedNames, name => name.Contains("IP filtering", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Фильтрация IP", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { application.Close(); } catch (InvalidOperationException) { }
            try { if (!application.HasExited) application.Kill(); } catch (InvalidOperationException) { }
        }
    }

    private static AutomationElement? FindByAnyName(AutomationElement root, params string[] names)
        => root.FindAllDescendants().FirstOrDefault(element => names.Contains(SafeName(element), StringComparer.Ordinal));

    private static string SafeName(AutomationElement element)
    {
        try { return element.Name; }
        catch (FlaUI.Core.Exceptions.PropertyNotSupportedException) { return string.Empty; }
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
