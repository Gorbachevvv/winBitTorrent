using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace WinBitTorrent.UiTests;

public sealed class TorrentRefreshSelectionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [UiFact]
    public void MarqueeCanGrowShrinkAndAddSelectionThenExitCleanly()
    {
        var previousDataRoot = Environment.GetEnvironmentVariable("WINBITTORRENT_DATA_ROOT");
        var dataRoot = Path.Combine(Path.GetTempPath(), $"WinBitTorrent-UiMarquee-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "client-settings.json"), """{"onboarding.completed":true,"ui.language":"en-US","window.main.maximized":false,"window.main.widthDip":1200,"window.main.heightDip":900,"updates.checkOnStartup":false}""");
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", dataRoot);
        string[] names = ["marquee-1.bin", "marquee-2.bin", "marquee-3.bin"];
        foreach (var name in names)
            SeedLegacyTorrent(dataRoot, name);
        using var application = Application.Launch(UiFactAttribute.FindExecutable()!);
        using var process = Process.GetProcessById(application.ProcessId);
        _ = process.Handle;
        using var automation = new UIA3Automation();
        try
        {
            var window = Retry.WhileNull(() => application.GetMainWindow(automation), TimeSpan.FromSeconds(20)).Result!;
            Assert.NotNull(window);
            Retry.WhileNull(() => FindByAnyName(window, "View"), TimeSpan.FromSeconds(10)).Result!.Click();
            Retry.WhileNull(() => FindByAnyName(window, "Transfers"), TimeSpan.FromSeconds(5)).Result!.Click();
            var rows = names.Select(name => Retry.WhileNull(() => FindSelectableTorrent(window, name), TimeSpan.FromSeconds(20)).Result!).OrderBy(row => row.BoundingRectangle.Top).ToArray();
            Assert.All(rows, Assert.NotNull);
            var first = rows[0].BoundingRectangle;
            var last = rows[^1].BoundingRectangle;
            var x = first.Left + Math.Min(first.Width / 2, 150);
            var originY = last.Bottom + 40;
            output.WriteLine($"First row: {first}; last row: {last}; window: {window.BoundingRectangle}");
            window.SetForeground();
            MovePointer(x, originY);
            Mouse.Down(MouseButton.Left);
            Thread.Sleep(100);
            MovePointer(x + 50, first.Top + first.Height / 2);
            var selectedAll = Retry.WhileFalse(() => names.All(name => IsTorrentSelected(window, name)), TimeSpan.FromSeconds(5)).Success;
            if (!selectedAll && Environment.GetEnvironmentVariable("WINBITTORRENT_SETUP_CAPTURES") is { Length: > 0 } captureRoot)
                window.CaptureToFile(Path.Combine(captureRoot, "marquee.png"));
            Assert.True(selectedAll);
            // Moving back must remove rows again, including the final position on release.
            MovePointer(x + 50, last.Top + last.Height / 2);
            Mouse.Up(MouseButton.Left);
            Assert.True(Retry.WhileFalse(() => names.Count(name => IsTorrentSelected(window, name)) == 1, TimeSpan.FromSeconds(5)).Success);
            Assert.True(IsTorrentSelected(window, rows[^1].Name));
            MovePointer(first.Left + 80, first.Top + first.Height / 2);
            Mouse.Click();
            Assert.True(Retry.WhileFalse(() => IsTorrentSelected(window, rows[0].Name)
                && names.Count(name => IsTorrentSelected(window, name)) == 1, TimeSpan.FromSeconds(5)).Success);
            using (Keyboard.Pressing(VirtualKeyShort.CONTROL))
            {
                MovePointer(x, originY);
                Mouse.Down(MouseButton.Left);
                Thread.Sleep(100);
                MovePointer(x + 50, last.Top + last.Height / 2);
                Mouse.Up(MouseButton.Left);
            }
            Assert.True(Retry.WhileFalse(() => IsTorrentSelected(window, rows[0].Name)
                && IsTorrentSelected(window, rows[^1].Name)
                && names.Count(name => IsTorrentSelected(window, name)) == 2, TimeSpan.FromSeconds(5)).Success);
            Thread.Sleep(2200); // Full snapshots must preserve the marquee selection too.
            Assert.True(IsTorrentSelected(window, rows[0].Name));
            Assert.True(IsTorrentSelected(window, rows[^1].Name));
            Assert.Equal(2, names.Count(name => IsTorrentSelected(window, name)));
            MovePointer(x, originY);
            Mouse.Click();
            Assert.True(Retry.WhileFalse(() => names.All(name => !IsTorrentSelected(window, name)), TimeSpan.FromSeconds(5)).Success);
            var handle = window.Properties.NativeWindowHandle.Value;
            // Deliver the same notification Explorer sends when opening this app's tray menu.
            // This avoids changing the user's taskbar settings to expose a hidden tray icon.
            Assert.True(PostMessage(handle, 0x8051, UIntPtr.Zero, new IntPtr(0x007B)));
            var exit = Retry.WhileNull(() => automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("Exit WinBitTorrent")), TimeSpan.FromSeconds(5)).Result!;
            Assert.NotNull(exit);
            var exitBounds = exit.BoundingRectangle;
            MovePointer(exitBounds.Left + exitBounds.Width / 2, exitBounds.Top + exitBounds.Height / 2);
            var timer = Stopwatch.StartNew();
            Mouse.Click();
            Assert.True(Retry.WhileTrue(() => IsWindowVisible(handle), TimeSpan.FromSeconds(1)).Success);
            output.WriteLine($"Exit UI dismissed after {timer.Elapsed.TotalMilliseconds:F0} ms");
            Assert.True(Retry.WhileFalse(() => application.HasExited, TimeSpan.FromSeconds(20)).Success);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            Mouse.Up(MouseButton.Left);
            if (!application.HasExited) application.Kill();
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", previousDataRoot);
            try
            {
                foreach (var file in Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(dataRoot, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [UiFact]
    public void FullSnapshotRefreshKeepsTheSelectedTorrent()
    {
        var previousDataRoot = Environment.GetEnvironmentVariable("WINBITTORRENT_DATA_ROOT");
        var dataRoot = Path.Combine(Path.GetTempPath(), $"WinBitTorrent-UiSelection-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", dataRoot);
        SeedLegacyTorrent(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "client-settings.json"), """{"onboarding.completed":true,"updates.checkOnStartup":false}""");

        using var application = Application.Launch(UiFactAttribute.FindExecutable()!);
        using var automation = new UIA3Automation();
        try
        {
            var window = Retry.WhileNull(() => application.GetMainWindow(automation), TimeSpan.FromSeconds(20)).Result;
            Assert.NotNull(window);

            var viewMenu = Retry.WhileNull(
                () => FindByAnyName(window!, "View", "Вид", "Выгляд"),
                TimeSpan.FromSeconds(5)).Result;
            Assert.NotNull(viewMenu);
            viewMenu!.Click();
            var transfers = Retry.WhileNull(
                () => FindByAnyName(window!, "Transfers", "Передачи", "Перадачы"),
                TimeSpan.FromSeconds(5)).Result;
            Assert.NotNull(transfers);
            transfers!.Click();

            var row = Retry.WhileNull(
                () => FindSelectableTorrent(window!, TorrentName),
                TimeSpan.FromSeconds(20)).Result;
            Assert.True(row is not null, $"Torrent row was not exposed to UI Automation. Visible: {string.Join(" | ", VisibleNames(window!))}");
            var bounds = row!.BoundingRectangle;
            MovePointer(bounds.Left + 80, bounds.Top + bounds.Height / 2);
            Mouse.Click();
            Assert.True(Retry.WhileTrue(
                () => !IsTorrentSelected(window!, TorrentName),
                TimeSpan.FromSeconds(5)).Success);

            // The local backend publishes a full main-data snapshot once per second. Waiting for
            // three cycles catches the old Clear()+reinsert behavior that discarded selection.
            Thread.Sleep(TimeSpan.FromSeconds(3.2));
            Assert.True(IsTorrentSelected(window!, TorrentName));
        }
        finally
        {
            try { if (!application.HasExited) application.Kill(); } catch (InvalidOperationException) { }
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", previousDataRoot);
        }
    }

    [UiFact]
    public void SidebarAllEntriesPreserveOtherGroupsAndNameSearchWorks()
    {
        var previousRoot = Environment.GetEnvironmentVariable("WINBITTORRENT_DATA_ROOT");
        var root = Path.Combine(Path.GetTempPath(), $"WinBitTorrent-UiFilters-{Guid.NewGuid():N}");
        SeedLegacyTorrent(root);
        File.WriteAllText(Path.Combine(root, "client-settings.json"), """{"onboarding.completed":true,"ui.language":"en-US","updates.checkOnStartup":false}""");
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", root);
        using var app = Application.Launch(UiFactAttribute.FindExecutable()!);
        using var automation = new UIA3Automation();
        try
        {
            var window = Retry.WhileNull(() => app.GetMainWindow(automation), TimeSpan.FromSeconds(20)).Result!;
            Assert.NotNull(window);
            Assert.NotNull(Retry.WhileNull(() => FindSelectableTorrent(window, TorrentName), TimeSpan.FromSeconds(20)).Result);
            var statuses = window.FindFirstDescendant(cf => cf.ByAutomationId("StatusList"))!;
            FindSelectableTorrent(statuses, "Completed")!.Patterns.SelectionItem.Pattern.Select();
            Assert.True(Retry.WhileFalse(() => FindSelectableTorrent(window, TorrentName) is null, TimeSpan.FromSeconds(5)).Success);
            foreach (var id in new[] { "TagsList", "SidebarTrackersList", "CategoriesList" })
            {
                var list = window.FindFirstDescendant(cf => cf.ByAutomationId(id));
                Assert.NotNull(list);
                var all = FindSelectableTorrent(list!, "All");
                Assert.NotNull(all);
                all!.Patterns.SelectionItem.Pattern.Select();
                Assert.Null(FindSelectableTorrent(window, TorrentName));
            }
            FindSelectableTorrent(statuses, "All")!.Patterns.SelectionItem.Pattern.Select();
            Assert.NotNull(Retry.WhileNull(() => FindSelectableTorrent(window, TorrentName), TimeSpan.FromSeconds(5)).Result);
            var search = window.FindFirstDescendant(cf => cf.ByAutomationId("TorrentNameSearch"))!.AsTextBox();
            search.Text = "does-not-exist";
            Assert.True(Retry.WhileFalse(() => FindSelectableTorrent(window, TorrentName) is null, TimeSpan.FromSeconds(5)).Success);
            search.Text = "REGRESSION";
            Assert.NotNull(Retry.WhileNull(() => FindSelectableTorrent(window, TorrentName), TimeSpan.FromSeconds(5)).Result);
            Thread.Sleep(1500);
            Assert.NotNull(FindSelectableTorrent(window, TorrentName));
        }
        finally
        {
            if (!app.HasExited) app.Kill();
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", previousRoot);
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private const string TorrentName = "selection-regression.bin";

    [UiFact]
    public void ResizingPanelsSavesOnlyAfterReleaseAndRestoresSizes()
    {
        var previousRoot = Environment.GetEnvironmentVariable("WINBITTORRENT_DATA_ROOT");
        var root = Path.Combine(Path.GetTempPath(), $"WinBitTorrent-UiResize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "client-settings.json");
        File.WriteAllText(settingsPath, """{"onboarding.completed":true,"ui.language":"en-US","window.main.maximized":false,"window.main.widthDip":1200,"window.main.heightDip":900,"updates.checkOnStartup":false}""");
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", root);
        using var app = Application.Launch(UiFactAttribute.FindExecutable()!);
        using var automation = new UIA3Automation();
        try
        {
            var window = Retry.WhileNull(() => app.GetMainWindow(automation), TimeSpan.FromSeconds(20)).Result!;
            Assert.NotNull(window);
            Assert.NotNull(Retry.WhileNull(() => FindByAnyName(window, "Connected"), TimeSpan.FromSeconds(20)).Result);
            window.SetForeground();
            Thread.Sleep(1200); // Let initial window placement settle before watching writes.

            foreach (var (id, dx, dy) in new[] { ("DetailsResizeThumb", 0, -70), ("SidebarResizeThumb", 40, 0) })
            {
                var thumb = window.FindFirstDescendant(cf => cf.ByAutomationId(id));
                Assert.NotNull(thumb);
                var start = thumb!.BoundingRectangle;
                var x = start.Left + start.Width / 2;
                var y = start.Top + start.Height / 2;
                var beforeWrite = File.GetLastWriteTimeUtc(settingsPath);
                MovePointer(x, y);
                Mouse.Down(MouseButton.Left);
                for (var step = 1; step <= 12; step++)
                {
                    MovePointer(x + dx * step / 12, y + dy * step / 12);
                    Thread.Sleep(20);
                }
                Thread.Sleep(150);
                Assert.Equal(beforeWrite, File.GetLastWriteTimeUtc(settingsPath));
                var moved = thumb.BoundingRectangle;
                Assert.True(Math.Abs(dx == 0 ? moved.Top - start.Top : moved.Left - start.Left) > 20);
                Mouse.Up(MouseButton.Left);
                Assert.True(Retry.WhileFalse(() => File.GetLastWriteTimeUtc(settingsPath) != beforeWrite, TimeSpan.FromSeconds(5)).Success);
            }

            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
            Assert.InRange(saved.RootElement.GetProperty("layout.sidebarWidth").GetDouble(), 255, 265);
            Assert.InRange(saved.RootElement.GetProperty("layout.detailsHeight").GetDouble(), 325, 335);
            FindByAnyName(window, "File")!.Click();
            FindByAnyName(window, "Exit")!.Click();
            Assert.True(Retry.WhileFalse(() => app.HasExited, TimeSpan.FromSeconds(20)).Success);
            using var restarted = Application.Launch(UiFactAttribute.FindExecutable()!);
            try
            {
                var restoredWindow = Retry.WhileNull(() => restarted.GetMainWindow(automation), TimeSpan.FromSeconds(20)).Result!;
                var restored = Retry.WhileNull(() => restoredWindow.FindFirstDescendant(cf => cf.ByAutomationId("DetailsResizeThumb")), TimeSpan.FromSeconds(5)).Result!;
                Assert.True(Retry.WhileFalse(() => Math.Abs(restored.BoundingRectangle.Height - 10) <= 1, TimeSpan.FromSeconds(5)).Success);
                using var persisted = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
                Assert.Equal(saved.RootElement.GetProperty("layout.sidebarWidth").GetDouble(), persisted.RootElement.GetProperty("layout.sidebarWidth").GetDouble());
                Assert.Equal(saved.RootElement.GetProperty("layout.detailsHeight").GetDouble(), persisted.RootElement.GetProperty("layout.detailsHeight").GetDouble());
            }
            finally { if (!restarted.HasExited) restarted.Kill(); }
        }
        finally
        {
            Mouse.Up(MouseButton.Left);
            if (!app.HasExited) app.Kill();
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", previousRoot);
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [UiFact]
    public void CategoryAndTagContextMenusOperateOnRightClickedItem()
    {
        var previousRoot = Environment.GetEnvironmentVariable("WINBITTORRENT_DATA_ROOT");
        var root = Path.Combine(Path.GetTempPath(), $"WinBitTorrent-UiTaxonomy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "client-settings.json"), """{"onboarding.completed":true,"ui.language":"en-US","window.main.maximized":false,"window.main.widthDip":1200,"window.main.heightDip":1000,"updates.checkOnStartup":false}""");
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", root);
        using var app = Application.Launch(UiFactAttribute.FindExecutable()!);
        using var automation = new UIA3Automation();
        try
        {
            var window = Retry.WhileNull(() => app.GetMainWindow(automation), TimeSpan.FromSeconds(20)).Result!;
            Assert.NotNull(window);
            AutomationElement List(string id) => window.FindFirstDescendant(cf => cf.ByAutomationId(id))!;
            AutomationElement Row(string id, string name) => Retry.WhileNull(() => FindSelectableTorrent(List(id), name), TimeSpan.FromSeconds(10)).Result!;
            AutomationElement? VisibleDialog(string id) => window.FindAllDescendants(cf => cf.ByAutomationId(id)).FirstOrDefault(element => !element.IsOffscreen);
            AutomationElement Dialog(string id) => Retry.WhileNull(() => VisibleDialog(id), TimeSpan.FromSeconds(5)).Result
                ?? throw new InvalidOperationException($"Dialog did not appear: {id}");
            void Command(string id, string name, string command)
            {
                window.SetForeground();
                var row = Row(id, name);
                Assert.NotNull(row);
                if (row.Patterns.ScrollItem.IsSupported) row.Patterns.ScrollItem.Pattern.ScrollIntoView();
                var bounds = row.BoundingRectangle;
                output.WriteLine($"Context target {id}/{name}: {bounds}");
                MovePointer(bounds.Left + 60, bounds.Top + bounds.Height / 2);
                Mouse.Click(MouseButton.Right);
                // WinUI flyouts can live in a separate UIA popup tree. Use their native
                // keyboard navigation after the real right-click instead of tree searches.
                Thread.Sleep(150);
                void Key(VirtualKeyShort key) { Keyboard.Press(key); Keyboard.Release(key); }
                Key(VirtualKeyShort.HOME);
                var down = command == "Delete category" ? 2 : command is "Edit category…" or "Delete tag" ? 1 : 0;
                for (var index = 0; index < down; index++) Key(VirtualKeyShort.DOWN);
                Key(VirtualKeyShort.RETURN);
            }
            void Apply(AutomationElement dialog, string button = "Apply")
            {
                var id = dialog.AutomationId;
                var action = Retry.WhileNull(() => FindByAnyName(dialog, button), TimeSpan.FromSeconds(5)).Result;
                Assert.NotNull(action);
                action!.Patterns.Invoke.Pattern.Invoke();
                Assert.True(Retry.WhileTrue(() => VisibleDialog(id) is not null, TimeSpan.FromSeconds(5)).Success);
            }
            // Wait for the local connection before enabling category/tag operations.
            Assert.NotNull(Retry.WhileNull(() => FindByAnyName(window, "Connected"), TimeSpan.FromSeconds(20)).Result);
            const string categories = "CategoriesList", tags = "TagsList";
            foreach (var name in new[] { "Keep", "keep", "Films, series | archive" })
            {
                Command(categories, "All", "New category…");
                var dialog = Dialog("TransferActionDialog");
                var edits = dialog.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit));
                edits[0].AsTextBox().Text = name;
                edits[1].AsTextBox().Text = Path.Combine(root, name == "Keep" ? "Keep" : "Original");
                Apply(dialog);
                Assert.NotNull(Row(categories, name));
            }
            Row(categories, "Keep").Patterns.SelectionItem.Pattern.Select();
            Command(categories, "Films, series | archive", "Edit category…");
            var editDialog = Dialog("TransferActionDialog");
            var pathBox = editDialog.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit))!.AsTextBox();
            Assert.Equal(Path.Combine(root, "Original"), pathBox.Text);
            pathBox.Text = Path.Combine(root, "Edited");
            Apply(editDialog);
            Command(categories, "Films, series | archive", "Edit category…");
            editDialog = Dialog("TransferActionDialog");
            Assert.Equal(Path.Combine(root, "Edited"), editDialog.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit))!.AsTextBox().Text);
            Apply(editDialog, "Cancel");
            Row(categories, "Keep").Patterns.SelectionItem.Pattern.Select();
            Command(categories, "Films, series | archive", "Delete category");
            Apply(Dialog("TransferConfirmationDialog"), "Confirm");
            Assert.True(Retry.WhileFalse(() => FindSelectableTorrent(List(categories), "Films, series | archive") is null, TimeSpan.FromSeconds(5)).Success);
            Assert.NotNull(Row(categories, "Keep"));
            Assert.NotNull(Row(categories, "keep"));

            Command(tags, "All", "Create tags…");
            var tagDialog = Dialog("TransferActionDialog");
            tagDialog.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Edit))!.AsTextBox().Text = "keep-tag, Keep-tag, topic|one";
            Apply(tagDialog);
            Assert.NotNull(Row(tags, "topic|one"));
            Row(tags, "keep-tag").Patterns.SelectionItem.Pattern.Select();
            Command(tags, "topic|one", "Delete tag");
            Apply(Dialog("TransferConfirmationDialog"), "Confirm");
            Assert.True(Retry.WhileFalse(() => FindSelectableTorrent(List(tags), "topic|one") is null, TimeSpan.FromSeconds(5)).Success);
            Assert.NotNull(Row(tags, "keep-tag"));
            Assert.NotNull(Row(tags, "Keep-tag"));
        }
        finally
        {
            if (!app.HasExited) app.Kill();
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", previousRoot);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static void MovePointer(int x, int y)
    {
        // SetCursorPos (used by FlaUI's MoveTo) can leave WinUI's captured pointer position
        // stale. Inject an absolute mouse movement so the test exercises real pointer frames.
        var dx = (uint)((x - GetSystemMetrics(76)) * 65536L / GetSystemMetrics(78));
        var dy = (uint)((y - GetSystemMetrics(77)) * 65536L / GetSystemMetrics(79));
        mouse_event(0xC001, dx, dy, 0, UIntPtr.Zero);
        Thread.Sleep(50);
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    private static AutomationElement? FindSelectableTorrent(AutomationElement root, string name)
        => root.FindAllDescendants().FirstOrDefault(element =>
            SafeName(element).Equals(name, StringComparison.Ordinal)
            && element.Patterns.SelectionItem.IsSupported);

    private static AutomationElement? FindByAnyName(AutomationElement root, params string[] names)
        => root.FindAllDescendants().FirstOrDefault(element =>
            names.Contains(SafeName(element), StringComparer.Ordinal));

    private static bool IsTorrentSelected(AutomationElement root, string name)
    {
        var row = FindSelectableTorrent(root, name);
        return row is not null && row.Patterns.SelectionItem.Pattern.IsSelected.Value;
    }

    private static string SafeName(AutomationElement element)
    {
        try { return element.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string[] VisibleNames(AutomationElement root)
        => root.FindAllDescendants()
            .Select(SafeName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

    private static void SeedLegacyTorrent(string dataRoot, string name = TorrentName)
    {
        var content = Encoding.ASCII.GetBytes("selection regression fixture");
        var pieceHash = SHA1.HashData(content);
        var infoPrefix = Encoding.ASCII.GetBytes(
            $"d6:lengthi{content.Length}e4:name{name.Length}:{name}12:piece lengthi16384e6:pieces20:");
        var info = infoPrefix.Concat(pieceHash).Append((byte)'e').ToArray();
        var infoHash = Convert.ToHexString(SHA1.HashData(info)).ToLowerInvariant();
        var torrent = Encoding.ASCII.GetBytes("d4:info").Concat(info).Append((byte)'e').ToArray();

        var backupRoot = Path.Combine(dataRoot, "Backend", "Profile", "qBittorrent", "data", "BT_backup");
        Directory.CreateDirectory(backupRoot);
        File.WriteAllBytes(Path.Combine(backupRoot, infoHash + ".torrent"), torrent);
    }
}
