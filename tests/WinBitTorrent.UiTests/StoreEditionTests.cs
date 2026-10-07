using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace WinBitTorrent.UiTests;

public sealed class StoreEditionTests
{
    [StoreUiFact]
    public void PackagedEditionStartsAndDoesNotExposeExcludedFeatures()
    {
        using var app = Application.LaunchStoreApp(Environment.GetEnvironmentVariable("WINBITTORRENT_STORE_AUMID")!, "");
        using var automation = new UIA3Automation();
        try
        {
            // Packaged activation initially exposes a transient splash HWND.
            var main = Retry.WhileNull(() => app.GetAllTopLevelWindows(automation)
                .FirstOrDefault(window => Find(window, "Tools", "Инструменты", "Інструменты") is not null),
                TimeSpan.FromSeconds(25), ignoreException: true).Result;
            Assert.NotNull(main);
            var later = Retry.WhileNull(() => Find(main!, "Set up later", "Настроить позже", "Наладзіць пазней"), TimeSpan.FromSeconds(8)).Result;
            later?.AsButton().Invoke();
            var view = Retry.WhileNull(() => Find(main!, "View", "Вид", "Выгляд"), TimeSpan.FromSeconds(10)).Result;
            Assert.NotNull(view);
            view!.Click();
            Assert.NotNull(Retry.WhileNull(() => Find(main!, "Transfers", "Передачи", "Перадачы"), TimeSpan.FromSeconds(5)).Result);
            Assert.Null(Find(main!, "Search", "Поисковая система", "Пошукавая сістэма"));
            Assert.Null(Find(main!, "Tracker Search", "Поиск по трекерам", "Пошук па трэкерах"));
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
            Find(main!, "Tools", "Инструменты", "Інструменты")!.Click();
            Assert.Null(Find(main!, "Cookies…"));
            if (Environment.GetEnvironmentVariable("WINBITTORRENT_STORE_CAPTURES") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                main!.CaptureToFile(Path.Combine(output, "store-main.png"));
            }
        }
        finally { if (!app.HasExited) app.Kill(); }
    }

    private static AutomationElement? Find(AutomationElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.FindFirstDescendant(cf => cf.ByName(name)) is { } element) return element;
        return null;
    }
}

public sealed class StoreUiFactAttribute : FactAttribute
{
    public StoreUiFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINBITTORRENT_STORE_AUMID")))
            Skip = "Set WINBITTORRENT_STORE_AUMID for a registered Store development package.";
    }
}
