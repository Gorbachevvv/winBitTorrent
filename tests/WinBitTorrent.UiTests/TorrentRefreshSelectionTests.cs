using System.Security.Cryptography;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace WinBitTorrent.UiTests;

public sealed class TorrentRefreshSelectionTests
{
    [UiFact]
    public void FullSnapshotRefreshKeepsTheSelectedTorrent()
    {
        var previousDataRoot = Environment.GetEnvironmentVariable("WINBITTORRENT_DATA_ROOT");
        var dataRoot = Path.Combine(Path.GetTempPath(), $"WinBitTorrent-UiSelection-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", dataRoot);
        SeedLegacyTorrent(dataRoot);

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
            row!.Click();
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
            try { application.Close(); } catch (InvalidOperationException) { }
            try { if (!application.HasExited) application.Kill(); } catch (InvalidOperationException) { }
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", previousDataRoot);
        }
    }

    private const string TorrentName = "selection-regression.bin";

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

    private static void SeedLegacyTorrent(string dataRoot)
    {
        var content = Encoding.ASCII.GetBytes("selection regression fixture");
        var pieceHash = SHA1.HashData(content);
        var infoPrefix = Encoding.ASCII.GetBytes(
            $"d6:lengthi{content.Length}e4:name{TorrentName.Length}:{TorrentName}12:piece lengthi16384e6:pieces20:");
        var info = infoPrefix.Concat(pieceHash).Append((byte)'e').ToArray();
        var infoHash = Convert.ToHexString(SHA1.HashData(info)).ToLowerInvariant();
        var torrent = Encoding.ASCII.GetBytes("d4:info").Concat(info).Append((byte)'e').ToArray();

        var backupRoot = Path.Combine(dataRoot, "Backend", "Profile", "qBittorrent", "data", "BT_backup");
        Directory.CreateDirectory(backupRoot);
        File.WriteAllBytes(Path.Combine(backupRoot, infoHash + ".torrent"), torrent);
    }
}
