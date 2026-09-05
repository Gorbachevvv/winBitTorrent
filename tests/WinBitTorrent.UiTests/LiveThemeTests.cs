using System.Diagnostics;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Application = FlaUI.Core.Application;

namespace WinBitTorrent.UiTests;

public sealed class LiveThemeTests
{
    [UiFact]
    public void ThemePreviewsAcrossWindowsRevertsOnCancelAndPersistsOnApply()
    {
        Assert.Empty(Process.GetProcessesByName("WinBitTorrent"));
        var root = Path.Combine(Path.GetTempPath(), "WinBitTorrent-ThemeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "client-settings.json");
        File.WriteAllText(file, """{"onboarding.completed":true,"ui.language":"en-US","ui.theme":"Dark","window.main.maximized":false,"updates.checkOnStartup":false}""");
        var start = new ProcessStartInfo(UiFactAttribute.FindExecutable()!) { UseShellExecute = false };
        start.Environment["WINBITTORRENT_DATA_ROOT"] = root;
        using var automation = new UIA3Automation();
        using var app = Application.Launch(start);
        try
        {
            var main = Retry.WhileNull(() => app.GetMainWindow(automation), TimeSpan.FromSeconds(20)).Result!;
            var settings = OpenSettings(app, automation, main);
            var mainDark = Brightness(main);
            var settingsDark = Brightness(settings);
            Theme(settings).Select("Light");
            WaitForLight(main, mainDark);
            WaitForLight(settings, settingsDark);
            Assert.Equal("Dark", SavedTheme(file));

            Named(settings, "Cancel").AsButton().Invoke();
            Retry.WhileFalse(() => Brightness(main) < mainDark + .1, TimeSpan.FromSeconds(5));
            Assert.True(Brightness(main) < mainDark + .1);

            settings = OpenSettings(app, automation, main);
            Theme(settings).Select("Light");
            Named(settings, "Apply").AsButton().Invoke();
            Retry.WhileFalse(() => SavedTheme(file) == "Light", TimeSpan.FromSeconds(10));
            Assert.Equal("Light", SavedTheme(file));
            Named(settings, "Settings applied.");
            Named(settings, "Cancel").AsButton().Invoke();
            WaitForLight(main, mainDark);

            // New windows inherit the saved theme without restarting the process.
            settings = OpenSettings(app, automation, main);
            Assert.Equal("Light", Theme(settings).SelectedItem?.Text);
            WaitForLight(settings, settingsDark);
            Theme(settings).Select("Use system setting");
            Named(settings, "Apply").AsButton().Invoke();
            Retry.WhileFalse(() => SavedTheme(file) == "Default", TimeSpan.FromSeconds(10));
            Assert.Equal("Default", SavedTheme(file));
        }
        finally
        {
            if (!app.HasExited) app.Kill();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static Window OpenSettings(Application app, UIA3Automation automation, Window main)
    {
        main.Focus();
        Named(main, "Tools").Click();
        Named(main, "Options…").Patterns.Invoke.Pattern.Invoke();
        var settings = Retry.WhileNull(() => app.GetAllTopLevelWindows(automation)
            .FirstOrDefault(window => !window.IsOffscreen
                && window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsTheme")) is not null), TimeSpan.FromSeconds(10)).Result
            ?? throw new InvalidOperationException("Settings window did not appear.");
        Theme(settings);
        return settings;
    }

    private static ComboBox Theme(Window window) => (Retry.WhileNull(
        () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsTheme")), TimeSpan.FromSeconds(10)).Result
        ?? throw new InvalidOperationException("Theme selector did not appear in " + window.Title)).AsComboBox();
    private static AutomationElement Named(AutomationElement root, string name) => Retry.WhileNull(
        () => root.FindFirstDescendant(cf => cf.ByName(name)), TimeSpan.FromSeconds(10)).Result
        ?? throw new InvalidOperationException($"Missing UI element: {name}");
    private static string SavedTheme(string file) => JsonNode.Parse(File.ReadAllText(file))!["ui.theme"]!.GetValue<string>();
    private static float Brightness(Window window)
    {
        // Blank left edge, outside the centered owned window and away from text/icons.
        using var bitmap = window.Capture();
        return bitmap.GetPixel(12, 80).GetBrightness();
    }
    private static void WaitForLight(Window window, float dark)
    {
        Retry.WhileFalse(() => Brightness(window) > dark + .25, TimeSpan.FromSeconds(5));
        Assert.True(Brightness(window) > dark + .25, "The window did not switch to its light theme.");
    }
}
