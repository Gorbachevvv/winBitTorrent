using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent.Services;

/// <summary>
/// Shared torrent actions that surface UI (dialogs) and are triggered from more than one place
/// (toolbar, context menu). Centralising them keeps a single localized implementation instead of
/// duplicating the dialog in each call site, where copies drift out of sync - e.g. the toolbar
/// delete dialog previously shipped hardcoded English while the context-menu one was localized.
/// </summary>
public static class TorrentActions
{
    public static async Task ConfirmDeleteSelectedAsync(XamlRoot xamlRoot, MainViewModel viewModel)
    {
        var selected = viewModel.SelectedTorrents.Count > 0
            ? viewModel.SelectedTorrents
            : viewModel.SelectedTorrent is null ? [] : [viewModel.SelectedTorrent];
        if (selected.Count == 0)
            return;

        try
        {
            if (!ClientSettings.Get("ui.confirmDelete", true))
            {
                await viewModel.DeleteSelectedAsync(deleteFiles: false);
                return;
            }

            var hashes = selected.SelectMany(TorrentHashes).ToArray();
            // Only offered when a tracked source file is still actually on disk - a torrent added
            // before this feature existed, or whose .torrent file was since moved or deleted, has
            // nothing to remove here.
            var sourceFiles = selected
                .Select(torrent => TorrentSourceFileStore.Find(TorrentHashes(torrent)))
                .OfType<string>()
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var content = new StackPanel { Spacing = 8 };
            var deleteFiles = new CheckBox { Content = Localizer.Get("Dialog_AlsoDeleteFiles", "Also delete files from disk") };
            content.Children.Add(deleteFiles);
            CheckBox? deleteSourceFiles = null;
            if (sourceFiles.Length > 0)
            {
                deleteSourceFiles = new CheckBox { Content = Localizer.Get("Dialog_AlsoDeleteSourceFile", "Also delete the .torrent file it was added from") };
                content.Children.Add(deleteSourceFiles);
            }

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = string.Format(
                    Localizer.Get("Dialog_DeleteSelectedTorrents", "Delete selected torrents ({0})?"),
                    selected.Count),
                Content = content,
                PrimaryButtonText = Localizer.Get("Common_Delete", "Delete"),
                CloseButtonText = Localizer.Get("Common_Cancel", "Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            await viewModel.DeleteSelectedAsync(deleteFiles.IsChecked == true);
            if (deleteSourceFiles?.IsChecked == true)
            {
                foreach (var path in sourceFiles)
                {
                    try { File.Delete(path); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
            // The torrent no longer exists in qBittorrent either way, so the mapping can never be
            // looked up again - drop it regardless of whether the file itself was also deleted.
            TorrentSourceFileStore.Forget(hashes);
        }
        catch (Exception exception)
        {
            await new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = Localizer.Get("Dialog_ActionFailedTitle", "Action failed"),
                Content = exception.Message,
                CloseButtonText = Localizer.Get("Common_OK", "OK")
            }.ShowAsync();
        }
    }

    private static IEnumerable<string> TorrentHashes(TorrentRowViewModel torrent)
    {
        if (!string.IsNullOrWhiteSpace(torrent.Hash)) yield return torrent.Hash;
        if (!string.IsNullOrWhiteSpace(torrent.InfoHashV1)) yield return torrent.InfoHashV1;
        if (!string.IsNullOrWhiteSpace(torrent.InfoHashV2)) yield return torrent.InfoHashV2;
    }
}
