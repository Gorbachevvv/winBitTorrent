using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinBitTorrent.Core.Services;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent.Services;

/// <summary>
/// Detects torrents that are already in the transfer list before the Add Torrent window ever
/// opens, matching qBittorrent: picking an already-added .torrent file or magnet link asks
/// whether to merge trackers right away, without showing a configuration dialog for a torrent
/// that's already there. Only files and magnet links can be identified this cheaply (their info
/// hash is either in the file or embedded in the URI) - a plain HTTP link to a .torrent still
/// needs its metadata fetched first, so those fall through to the Add Torrent window's own
/// later check once that metadata is available.
/// </summary>
public static class TorrentDuplicateChecker
{
    public static async Task<(IReadOnlyList<string> Files, IReadOnlyList<string> Urls)> RemoveDuplicatesAsync(
        XamlRoot xamlRoot, MainViewModel viewModel, IReadOnlyList<string> files, IReadOnlyList<string> urls)
    {
        var pendingFiles = files.ToList();
        var pendingUrls = urls.ToList();

        foreach (var file in files)
        {
            IReadOnlySet<string> hashes;
            try
            {
                hashes = TorrentIdentity.FromTorrentFile(file);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                continue;
            }

            if (viewModel.FindDuplicateTorrent(hashes) is not { } existing)
                continue;

            pendingFiles.Remove(file);
            await HandleDuplicateAsync(xamlRoot, viewModel, existing.Name, existing.Model.IsPrivate, isFile: true, source: file);
        }

        foreach (var url in urls)
        {
            var hashes = TorrentIdentity.FromMagnet(url);
            if (hashes.Count == 0)
                continue;

            if (viewModel.FindDuplicateTorrent(hashes) is not { } existing)
                continue;

            pendingUrls.Remove(url);
            await HandleDuplicateAsync(xamlRoot, viewModel, existing.Name, existing.Model.IsPrivate, isFile: false, source: url);
        }

        return (pendingFiles, pendingUrls);
    }

    private static async Task HandleDuplicateAsync(XamlRoot xamlRoot, MainViewModel viewModel, string existingName, bool existingIsPrivate, bool isFile, string source)
    {
        if (existingIsPrivate)
        {
            await ShowPrivateDuplicateAsync(xamlRoot, existingName);
            return;
        }

        if (await AskToMergeDuplicateAsync(xamlRoot, existingName))
            await MergeDuplicateAsync(viewModel, isFile, source);
    }

    public static async Task<bool> AskToMergeDuplicateAsync(XamlRoot xamlRoot, string name)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Localizer.Get("DuplicateTorrent_Title", "Torrent is already present"),
            Content = string.Format(
                Localizer.Get("DuplicateTorrent_Message", "Torrent '{0}' is already in the transfer list. Do you want to merge trackers from the new source?"),
                name),
            PrimaryButtonText = Localizer.Get("Common_Yes", "Yes"),
            CloseButtonText = Localizer.Get("Common_No", "No"),
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task ShowPrivateDuplicateAsync(XamlRoot xamlRoot, string name)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Localizer.Get("DuplicateTorrent_Title", "Torrent is already present"),
            Content = string.Format(
                Localizer.Get("DuplicateTorrent_Private", "Torrent '{0}' is private. Its trackers cannot be merged."),
                name),
            CloseButtonText = Localizer.Get("Common_OK", "OK"),
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    // Re-adds the same source with qBittorrent's own merge_trackers preference temporarily
    // forced on, so the server merges in whatever trackers the file/magnet carries instead of
    // rejecting the duplicate outright. Save path/category/etc. are left at their defaults since
    // qBittorrent ignores them for a torrent that already exists - only the tracker merge itself
    // has any effect.
    public static async Task MergeDuplicateAsync(MainViewModel viewModel, bool isFile, string source)
    {
        var api = viewModel.Api
            ?? throw new InvalidOperationException(Localizer.Get("Connection_NotConnected", "Not connected to a torrent backend."));
        var preferences = await api.Application.GetPreferencesAsync();
        var mergeWasEnabled = preferences["merge_trackers"]?.GetValue<bool>() == true;
        if (!mergeWasEnabled)
            await api.Application.SetPreferencesAsync(new JsonObject { ["merge_trackers"] = true });

        try
        {
            IReadOnlyList<string> files = isFile ? [source] : [];
            IReadOnlyList<string> urls = isFile ? [] : [source];
            await viewModel.AddAsync(files, urls, new Core.Models.TorrentAddRequest(urls, files));
        }
        finally
        {
            if (!mergeWasEnabled)
                await api.Application.SetPreferencesAsync(new JsonObject { ["merge_trackers"] = false });
        }
    }
}
