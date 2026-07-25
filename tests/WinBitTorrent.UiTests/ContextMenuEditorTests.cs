using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace WinBitTorrent.UiTests;

public sealed class ContextMenuEditorTests
{
    [UiFact]
    public void BehaviorSectionOpensTheContextMenuEditor()
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

            // The Behavior section is selected when the window opens, so the launcher is already there.
            var launcher = Retry.WhileNull(
                () => FindByAnyName(settingsWindow!, "Edit the context menu…", "Редактировать контекстное меню…", "Рэдагаваць кантэкстнае меню…"),
                TimeSpan.FromSeconds(10)).Result;
            Assert.NotNull(launcher);
            launcher!.Click();

            // Both panes render: the menu preview on the left and the shelf of spare commands.
            var editorNames = WaitForNames(settingsWindow!, "Torrent context menu", "Контекстное меню торрента", "Кантэкстнае меню торэнта");
            AssertAnyName(editorNames, "Available commands", "Доступные команды", "Даступныя каманды");
            // Separator rows and command rows are both drawn inside the preview, and the command
            // labels come from the same resources the real menu uses.
            AssertAnyName(editorNames, "Separator", "Разделитель", "Падзяляльнік");
            AssertAnyName(editorNames, "Force recheck", "Перепроверить принудительно", "Пераправерыць прымусова");
            // Nothing has been removed yet, so the right-hand list is empty.
            AssertAnyName(editorNames, "Every command is already in the menu", "Все команды уже добавлены в меню", "Усе каманды ўжо дададзеныя ў меню");

            // Removing the first command moves it out of the menu and onto the shelf...
            var remove = FindByAnyName(settingsWindow!, "Remove from the menu", "Убрать из меню", "Прыбраць з меню");
            Assert.NotNull(remove);
            remove!.Click();
            var afterRemoval = Retry.While(
                () => VisibleNames(settingsWindow!),
                found => found.Any(name => name.Contains("Every command is already in the menu", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Все команды уже добавлены в меню", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Усе каманды ўжо дададзеныя ў меню", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(5)).Result ?? [];
            AssertAnyName(afterRemoval, "Start", "Запустить", "Запусціць");

            // ...and clicking it there puts it back, emptying the shelf again.
            var available = FindByAnyName(settingsWindow!, "Start", "Запустить", "Запусціць");
            Assert.NotNull(available);
            available!.Click();
            WaitForNames(settingsWindow!, "Every command is already in the menu", "Все команды уже добавлены в меню", "Усе каманды ўжо дададзеныя ў меню");
        }
        finally
        {
            try { application.Close(); } catch (InvalidOperationException) { }
            try { if (!application.HasExited) application.Kill(); } catch (InvalidOperationException) { }
        }
    }

    // Polls until one of the expected labels shows up, then hands back everything that was visible
    // at that moment so the remaining assertions can report the real contents when they fail.
    private static string[] WaitForNames(AutomationElement root, params string[] expected)
    {
        var names = Retry.While(
            () => VisibleNames(root),
            found => !found.Any(name => expected.Any(candidate => name.Contains(candidate, StringComparison.OrdinalIgnoreCase))),
            TimeSpan.FromSeconds(10)).Result ?? VisibleNames(root);
        AssertAnyName(names, expected);
        return names;
    }

    private static void AssertAnyName(string[] names, params string[] expected)
        => Assert.True(
            names.Any(name => expected.Any(candidate => name.Contains(candidate, StringComparison.OrdinalIgnoreCase))),
            $"None of [{string.Join(", ", expected)}] was found. Visible: {string.Join(" | ", names)}");

    private static string[] VisibleNames(AutomationElement root)
        => root.FindAllDescendants()
            .Select(SafeName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

    private static AutomationElement? FindByAnyName(AutomationElement root, params string[] names)
        => root.FindAllDescendants().FirstOrDefault(element => names.Contains(SafeName(element), StringComparer.Ordinal));

    private static string SafeName(AutomationElement element)
    {
        try { return element.Name; }
        catch (FlaUI.Core.Exceptions.PropertyNotSupportedException) { return string.Empty; }
    }
}
