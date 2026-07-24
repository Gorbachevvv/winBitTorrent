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
        if (viewModel.SelectedTorrents.Count == 0 && viewModel.SelectedTorrent is null)
            return;

        try
        {
            if (!ClientSettings.Get("ui.confirmDelete", true))
            {
                await viewModel.DeleteSelectedAsync(deleteFiles: false);
                return;
            }

            var deleteFiles = new CheckBox { Content = Localizer.Get("Dialog_AlsoDeleteFiles", "Also delete files from disk") };
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = Localizer.Get("Dialog_DeleteSelectedTorrents", "Delete selected torrents?"),
                Content = deleteFiles,
                PrimaryButtonText = Localizer.Get("Common_Delete", "Delete"),
                CloseButtonText = Localizer.Get("Common_Cancel", "Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await viewModel.DeleteSelectedAsync(deleteFiles.IsChecked == true);
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
}
