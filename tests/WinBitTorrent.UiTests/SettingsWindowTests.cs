using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace WinBitTorrent.UiTests;

public sealed class SettingsWindowTests
{
    // Regression for: visiting settings tabs without changing anything, then saving, used to
    // report "qBittorrent did not apply" some settings the user never touched. Two causes:
    // 1. CaptureSection captured every field shown in a tab, not just edited ones.
    // 2. If the settings window's first Activated fired before MainViewModel finished
    //    connecting, preferences were never loaded, so every field read back as "changed"
    //    relative to an empty baseline.
    [UiFact]
    public void SavingAfterOnlyBrowsingTabsReportsNoFailedSettings()
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

            // Visit two tabs without changing anything in either - this is exactly what the two
            // bugs above turned into "the user edited every field in both tabs".
            var connection = Retry.WhileNull(
                () => FindByAnyName(settingsWindow!, "Connection", "Соединение"),
                TimeSpan.FromSeconds(10)).Result;
            Assert.NotNull(connection);
            connection!.Click();

            Retry.While(
                () => settingsWindow!.FindAllDescendants().Select(SafeName),
                names => !names.Any(name => name.Contains("Proxy server", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Прокси-сервер", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(10));

            var speed = Retry.WhileNull(
                () => FindByAnyName(settingsWindow!, "Speed", "Скорость"),
                TimeSpan.FromSeconds(10)).Result;
            Assert.NotNull(speed);
            speed!.Click();

            Retry.While(
                () => settingsWindow!.FindAllDescendants().Select(SafeName),
                names => !names.Any(name => name.Contains("Global download limit", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Глобальный лимит скачивания", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(10));

            var apply = Retry.WhileNull(
                () => FindByAnyName(settingsWindow!, "Apply", "Применить"),
                TimeSpan.FromSeconds(5)).Result;
            Assert.NotNull(apply);
            apply!.Click();

            // Give the save + verification round-trip a moment, then the failure banner - if
            // present at all - must not be there.
            Thread.Sleep(2000);
            Assert.Null(FindContaining(settingsWindow!, "did not apply"));
            Assert.Null(FindContaining(settingsWindow!, "не применил"));
        }
        finally
        {
            try { application.Close(); } catch (InvalidOperationException) { }
            try { if (!application.HasExited) application.Kill(); } catch (InvalidOperationException) { }
        }
    }

    private static AutomationElement? FindByAnyName(AutomationElement root, params string[] names)
        => root.FindAllDescendants().FirstOrDefault(element => names.Contains(SafeName(element), StringComparer.Ordinal));

    private static AutomationElement? FindContaining(AutomationElement root, string text)
        => root.FindAllDescendants().FirstOrDefault(element => SafeName(element).Contains(text, StringComparison.OrdinalIgnoreCase));

    private static string SafeName(AutomationElement element)
    {
        // The UI keeps mutating (tab switches rebuild the whole settings panel) while these
        // retry loops poll it, so a name lookup can legitimately land on an element that was
        // just torn down - that is not a real failure, just a stale read to retry past.
        try { return element.Name ?? string.Empty; }
        catch (Exception) { return string.Empty; }
    }
}
