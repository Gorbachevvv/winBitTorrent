using System.Diagnostics;
using System.Text.Json.Nodes;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Application = FlaUI.Core.Application;

namespace WinBitTorrent.UiTests;

public sealed class FirstRunTests
{
    [UiFact]
    public void RussianSetupFitsSmallWindowAndCanBeDismissedWithEscape()
    {
        Assert.Empty(Process.GetProcessesByName("WinBitTorrent"));
        var root = Path.Combine(Path.GetTempPath(), "WinBitTorrent-OnboardingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "client-settings.json"), """{"ui.language":"ru-RU","window.main.maximized":false,"window.main.widthDip":820,"window.main.heightDip":560,"updates.checkOnStartup":false}""");
        var start = new ProcessStartInfo(UiFactAttribute.FindExecutable()!) { UseShellExecute = false };
        start.Environment["WINBITTORRENT_DATA_ROOT"] = root;
        using var automation = new UIA3Automation();
        using var app = Application.Launch(start);
        try
        {
            var window = MainWindow(app, automation);
            var dialog = WaitId(window, "FirstRunDialog");
            WaitName(window, "Выберите тему");
            WaitId(window, "SetupThemeLight").AsRadioButton().IsChecked = true;
            Capture(window, "06-russian-small");
            var next = Button(window, "Продолжить");
            var back = Button(window, "Назад");
            var later = Button(window, "Настроить позже");
            Assert.True(later.BoundingRectangle.Right <= back.BoundingRectangle.Left);
            Assert.True(back.BoundingRectangle.Right <= next.BoundingRectangle.Left);
            Assert.Null(window.FindFirstDescendant(cf => cf.ByName("●  PEER TO PEER")));
            Assert.Null(window.FindFirstDescendant(cf => cf.ByName("Добро пожаловать! Давайте настроим WinBitTorrent под вас.")));
            Assert.True(window.BoundingRectangle.Contains(next.BoundingRectangle));
            Assert.False(next.IsOffscreen);
            next.Invoke();
            WaitId(window, "SetupDownloadPath");
            Capture(window, "07-russian-downloads-small");
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
            Retry.WhileTrue(() => FindId(window, "FirstRunDialog") is not null, TimeSpan.FromSeconds(5));
            Assert.Null(FindId(window, "FirstRunDialog"));
            Assert.False(ReadSettings(Path.Combine(root, "client-settings.json"))["onboarding.completed"]?.GetValue<bool>() ?? false);
        }
        finally
        {
            if (!app.HasExited) app.Kill();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [UiFact]
    public void FirstRunResumesAfterCloseAndOnlyDisappearsAfterSuccessfulSave()
    {
        Assert.Empty(Process.GetProcessesByName("WinBitTorrent"));
        var root = Path.Combine(Path.GetTempPath(), "WinBitTorrent-OnboardingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "client-settings.json");
        File.WriteAllText(settingsPath, """{"ui.language":"en-US","window.main.maximized":false,"updates.checkOnStartup":false}""");
        var start = new ProcessStartInfo(UiFactAttribute.FindExecutable()!) { UseShellExecute = false };
        start.Environment["WINBITTORRENT_DATA_ROOT"] = root;
        using var automation = new UIA3Automation();
        Application? app = null;
        try
        {
            app = Application.Launch(start);
            var window = MainWindow(app, automation);
            WaitId(window, "FirstRunDialog");
            WaitName(window, "Choose a theme");
            Assert.True(Button(window, "Set up later").BoundingRectangle.Right <= Button(window, "Back").BoundingRectangle.Left);
            Assert.True(Button(window, "Back").BoundingRectangle.Right <= Button(window, "Continue").BoundingRectangle.Left);
            WaitId(window, "SetupThemeDark").AsRadioButton().IsChecked = true;
            Capture(window, "01-appearance-dark");
            WaitId(window, "SetupThemeLight").AsRadioButton().IsChecked = true;
            Capture(window, "02-appearance-light");
            WaitId(window, "SetupThemeDark").AsRadioButton().IsChecked = true;
            Button(window, "Continue").Invoke();
            var folder = WaitId(window, "SetupDownloadPath").AsTextBox();
            // Invalid paths must not mark setup complete or dismiss the dialog.
            folder.Text = "relative-folder";
            Capture(window, "03-downloads");
            Button(window, "Continue").Invoke();
            Capture(window, "04-windows");
            Button(window, "Continue").Invoke();
            Button(window, "Start downloading").Invoke();
            WaitName(window, "Use a full folder path, such as C:\\Downloads.");
            Assert.False(ReadSettings(settingsPath)["onboarding.completed"]?.GetValue<bool>() ?? false);
            Button(window, "Back").Invoke();
            Button(window, "Back").Invoke();
            var downloads = Path.Combine(root, "My downloads");
            WaitId(window, "SetupDownloadPath").AsTextBox().Text = downloads;
            Button(window, "Continue").Invoke();
            // Closing the actual app during setup must exit, preserving the draft.
            window.Close();
            Retry.WhileFalse(() => app.HasExited, TimeSpan.FromSeconds(20));
            Assert.True(app.HasExited);
            app.Dispose();
            app = Application.Launch(start);
            window = MainWindow(app, automation);
            WaitId(window, "FirstRunDialog");
            WaitName(window, "Set up Windows integration");
            var draft = JsonNode.Parse(ReadSettings(settingsPath)["onboarding.draft"]!.GetValue<string>())!;
            Assert.Equal(2, draft["Step"]!.GetValue<int>());
            Assert.Equal("Dark", draft["Theme"]!.GetValue<string>());
            Assert.Equal(downloads, draft["DownloadPath"]!.GetValue<string>());
            // Dismissal is also resumable on the next launch.
            Button(window, "Set up later").Invoke();
            Retry.WhileTrue(() => FindId(window, "FirstRunDialog") is not null, TimeSpan.FromSeconds(5));
            Assert.Null(FindId(window, "FirstRunDialog"));
            app.Kill();
            app.Dispose();
            app = Application.Launch(start);
            window = MainWindow(app, automation);
            WaitName(window, "Set up Windows integration");
            Button(window, "Continue").Invoke();
            Capture(window, "05-ready");
            Button(window, "Start downloading").Invoke();
            Retry.WhileFalse(() => ReadSettings(settingsPath)["onboarding.completed"]?.GetValue<bool>() == true, TimeSpan.FromSeconds(30));
            Assert.True(ReadSettings(settingsPath)["onboarding.completed"]?.GetValue<bool>() == true);
            Assert.Null(ReadSettings(settingsPath)["onboarding.draft"]);
            Assert.Equal("Dark", ReadSettings(settingsPath)["ui.theme"]!.GetValue<string>());
            Assert.True(Directory.Exists(downloads));
            app.Kill();
            app.Dispose();
            app = Application.Launch(start);
            window = MainWindow(app, automation);
            WaitName(window, "Tools");
            Thread.Sleep(1500);
            Assert.Null(FindId(window, "FirstRunDialog"));
        }
        finally
        {
            if (app is not null)
            {
                if (!app.HasExited) app.Kill();
                app.Dispose();
            }
            // Only the unique test data directory is removed; application data is untouched.
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static Window MainWindow(Application app, UIA3Automation automation)
        => Retry.WhileNull(() => app.GetMainWindow(automation), TimeSpan.FromSeconds(20)).Result
            ?? throw new InvalidOperationException("Main window did not appear.");
    private static AutomationElement? FindId(AutomationElement root, string id)
        => root.FindFirstDescendant(cf => cf.ByAutomationId(id));
    private static AutomationElement WaitId(AutomationElement root, string id)
        => Retry.WhileNull(() => FindId(root, id), TimeSpan.FromSeconds(20)).Result
            ?? throw new InvalidOperationException($"Missing UI element: {id}");
    private static AutomationElement WaitName(AutomationElement root, string name)
        => Retry.WhileNull(() => root.FindFirstDescendant(cf => cf.ByName(name)), TimeSpan.FromSeconds(20)).Result
            ?? throw new InvalidOperationException($"Missing UI text: {name}");
    private static Button Button(AutomationElement root, string name) => WaitName(root, name).AsButton();
    private static JsonObject ReadSettings(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WINBITTORRENT_SETUP_CAPTURES") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        Thread.Sleep(300);
        window.CaptureToFile(Path.Combine(output, name + ".png"));
    }
}
