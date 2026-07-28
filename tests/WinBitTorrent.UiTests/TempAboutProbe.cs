using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace WinBitTorrent.UiTests;

public sealed class TempAboutProbe
{
    private const string Out = @"C:\Users\Kirill\source\repos\WinBitTorrent\build\";

    [UiFact]
    public void ProbeAboutDialogLinks()
    {
        using var application = Application.Launch(UiFactAttribute.FindExecutable()!);
        using var automation = new UIA3Automation();
        try
        {
            var window = Retry.WhileNull(() => application.GetMainWindow(automation), TimeSpan.FromSeconds(20)).Result;
            Thread.Sleep(2000);

            var helpMenu = FindByAnyName(window!, "Справка", "Help");
            Assert.NotNull(helpMenu);
            helpMenu!.Click();
            Thread.Sleep(500);

            var aboutButton = FindByAnyName(window!, "О WinBitTorrent", "About WinBitTorrent");
            Assert.NotNull(aboutButton);
            aboutButton!.Click();
            Thread.Sleep(1500);

            var texts = window!.FindAllDescendants().Select(Safe).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
            File.WriteAllLines(Out + "about-probe-log.txt", texts);
        }
        finally
        {
            try { application.Close(); } catch (InvalidOperationException) { }
            try { if (!application.HasExited) application.Kill(); } catch (InvalidOperationException) { }
        }
    }

    private static AutomationElement? FindByAnyName(AutomationElement root, params string[] names)
        => root.FindAllDescendants().FirstOrDefault(element => names.Contains(Safe(element), StringComparer.Ordinal));

    private static string Safe(AutomationElement element)
    {
        try { return element.Name ?? string.Empty; }
        catch (Exception) { return string.Empty; }
    }
}
