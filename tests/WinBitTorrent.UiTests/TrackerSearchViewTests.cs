using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace WinBitTorrent.UiTests;

public sealed class TrackerSearchViewTests
{
    // The Pirate Bay needs no account, so picking it has to land straight on the search panel
    // instead of the embedded sign-in browser RuTracker uses.
    [UiFact]
    public void PirateBayIsListedAndOpensSearchWithoutSigningIn()
    {
        using var application = Application.Launch(UiFactAttribute.FindExecutable()!);
        using var automation = new UIA3Automation();
        try
        {
            var window = Retry.WhileNull(() => application.GetMainWindow(automation), TimeSpan.FromSeconds(15)).Result;
            Assert.NotNull(window);
            window!.SetForeground();

            var trackersTab = Retry.WhileNull(
                () => FindByName(window, "Trackers", "Трекеры", "Трэкеры"),
                TimeSpan.FromSeconds(10)).Result;
            Assert.NotNull(trackersTab);
            trackersTab!.Click();

            // The tracker cards render their label in a child TextBlock, so the button carries an
            // explicit AutomationProperties.Name - without it a screen reader announces nothing.
            var pirateBay = Retry.WhileNull(
                () => FindButtonNamed(window, "The Pirate Bay"),
                TimeSpan.FromSeconds(10)).Result;
            Assert.NotNull(pirateBay);
            Assert.NotNull(FindButtonNamed(window, "RuTracker"));

            pirateBay!.AsButton().Invoke();

            // "Open topic" only exists on the tracker search panel, and "Sign out" only on a tracker
            // that keeps an account session.
            var openTopic = Retry.WhileNull(
                () => FindByName(window, "Open topic", "Открыть тему", "Адкрыць тэму"),
                TimeSpan.FromSeconds(10)).Result;
            Assert.NotNull(openTopic);
            Assert.Null(FindByName(window, "Sign out", "Выйти", "Выйсці"));
        }
        finally
        {
            try { application.Close(); } catch (InvalidOperationException) { }
            try { if (!application.HasExited) application.Kill(); } catch (InvalidOperationException) { }
        }
    }

    private static AutomationElement? FindByName(AutomationElement root, params string[] names)
        => root.FindAllDescendants().FirstOrDefault(element => names.Contains(SafeName(element), StringComparer.Ordinal));

    private static AutomationElement? FindButtonNamed(AutomationElement root, string name)
        => root.FindAllDescendants(condition => condition.ByControlType(ControlType.Button))
            .FirstOrDefault(button => string.Equals(SafeName(button), name, StringComparison.Ordinal));

    private static string SafeName(AutomationElement element)
    {
        try { return element.Name; }
        catch (FlaUI.Core.Exceptions.PropertyNotSupportedException) { return string.Empty; }
    }
}
