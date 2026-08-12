using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Core.Services;
using WinBitTorrent.Services;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent;

public sealed partial class AddTorrentWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly List<string> _files;
    private readonly ObservableCollection<MetadataFileSelection> _metadataFiles = [];
    private readonly ObservableCollection<MetadataTreeNode> _visibleMetadataTree = [];
    private string? _metadataSource;
    private string? _metadataName;
    private JsonObject? _metadata;
    private bool _initialized;
    private bool _updatingPathControls;
    private string _defaultSavePath;
    private string _defaultDownloadPath = string.Empty;
    private bool _defaultUseDownloadPath;
    private string _manualSavePath;
    private string _manualDownloadPath = string.Empty;
    private bool _manualUseDownloadPath;
    private CancellationTokenSource? _previewLifetime;

    public AddTorrentWindow(IReadOnlyList<string> torrentFiles, IReadOnlyList<string> urls)
    {
        InitializeComponent();
        Title = Localizer.Get("WindowTitle_AddTorrent", "Add torrent");
        this.ConfigureOwned(1120, 720, minimumWidth: 860, minimumHeight: 580);
        _viewModel = App.Services.GetRequiredService<MainViewModel>();
        _files = torrentFiles.ToList();
        MetadataFilesTree.ItemsSource = _visibleMetadataTree;
        _defaultSavePath = DefaultDownloadsPath();
        _manualSavePath = _defaultSavePath;
        SavePathBox.Text = _defaultSavePath;
        SourcesBox.Text = string.Join(Environment.NewLine, torrentFiles.Concat(urls));
        Activated += AddTorrentWindow_Activated;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            // A magnet preview polls qBittorrent for up to two minutes; closing the window has to
            // end that instead of leaving it running against a dead UI.
            _previewLifetime?.Cancel();
        };
    }

    private async void AddTorrentWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= AddTorrentWindow_Activated;
        if (_viewModel.Api is null)
            return;

        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        await InitializeFromApiAsync();
    }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(MainViewModel.IsConnected) and not nameof(MainViewModel.Api)
            || _viewModel.Api is null)
            return;

        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        await InitializeFromApiAsync();
    }

    private async Task InitializeFromApiAsync()
    {
        if (_initialized)
            return;
        _initialized = true;

        try
        {
            var preferences = await _viewModel.Api!.Application.GetPreferencesAsync();
            var savePath = StringPreference(preferences, "save_path");
            if (string.IsNullOrWhiteSpace(savePath))
                savePath = await _viewModel.Api.Application.GetDefaultSavePathAsync();

            _defaultSavePath = string.IsNullOrWhiteSpace(savePath) ? DefaultDownloadsPath() : savePath.Trim();
            _defaultDownloadPath = StringPreference(preferences, "temp_path")?.Trim() ?? string.Empty;
            _defaultUseDownloadPath = BooleanPreference(preferences, "temp_path_enabled");
            _manualSavePath = _defaultSavePath;
            _manualDownloadPath = _defaultDownloadPath;
            _manualUseDownloadPath = _defaultUseDownloadPath;

            _updatingPathControls = true;
            AutoTmmModeBox.SelectedIndex = BooleanPreference(preferences, "auto_tmm_enabled") ? 1 : 0;
            _updatingPathControls = false;
            ApplyPathMode(restoreManualValues: true);
        }
        catch (Exception exception)
        {
            // Metadata preview can still work even if this older/remote backend did not expose
            // preferences. Keep the safe local fallback visible and surface the real API error.
            ShowError(exception);
        }

        if (ParseSources().FirstOrDefault() is { } source)
            await PreviewSourceAsync(source);
    }

    private void AutoTmmModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPathControls || SavePathBox is null)
            return;

        if (AutoTmmModeBox.SelectedIndex == 1)
        {
            _manualSavePath = SavePathBox.Text;
            _manualDownloadPath = TempPathBox.Text;
            _manualUseDownloadPath = UseTempPathBox.IsChecked == true;
            ApplyPathMode(restoreManualValues: false);
        }
        else
        {
            ApplyPathMode(restoreManualValues: true);
        }
    }

    private void CategoryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingPathControls && AutoTmmModeBox.SelectedIndex == 1)
            ShowAutomaticPaths();
    }

    private void ApplyPathMode(bool restoreManualValues)
    {
        var automatic = AutoTmmModeBox.SelectedIndex == 1;
        _updatingPathControls = true;
        SavePathBox.IsEnabled = !automatic;
        SavePathBrowseButton.IsEnabled = !automatic;
        UseTempPathBox.IsEnabled = !automatic;
        if (automatic)
        {
            ShowAutomaticPaths();
        }
        else if (restoreManualValues)
        {
            SavePathBox.Text = string.IsNullOrWhiteSpace(_manualSavePath) ? _defaultSavePath : _manualSavePath;
            TempPathBox.Text = _manualDownloadPath;
            UseTempPathBox.IsChecked = _manualUseDownloadPath;
            UpdateDownloadPathEditors();
        }
        _updatingPathControls = false;
    }

    private void ShowAutomaticPaths()
    {
        var category = CategoryBox.Text.Trim();
        SavePathBox.Text = ResolveAutomaticSavePath(category);
        var hasCategoryDownloadPath = false;
        var categoryDownloadPath = _defaultDownloadPath;
        if (category.Length > 0
            && _viewModel.Categories.TryGetValue(category, out var categoryOptions)
            && !string.IsNullOrWhiteSpace(categoryOptions.DownloadPath))
        {
            hasCategoryDownloadPath = true;
            categoryDownloadPath = categoryOptions.DownloadPath;
        }
        TempPathBox.Text = categoryDownloadPath;
        UseTempPathBox.IsChecked = hasCategoryDownloadPath
            || (_defaultUseDownloadPath && !string.IsNullOrWhiteSpace(categoryDownloadPath));
        UpdateDownloadPathEditors();
    }

    private string ResolveAutomaticSavePath(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return _defaultSavePath;

        if (_viewModel.Categories.TryGetValue(category, out var options)
            && !string.IsNullOrWhiteSpace(options.SavePath))
        {
            return IsAbsoluteServerPath(options.SavePath)
                ? options.SavePath
                : CombineServerPath(_defaultSavePath, options.SavePath);
        }

        var separator = category.LastIndexOf('/');
        var parent = separator < 0 ? string.Empty : category[..separator];
        var leaf = separator < 0 ? category : category[(separator + 1)..];
        return string.IsNullOrWhiteSpace(leaf)
            ? ResolveAutomaticSavePath(parent)
            : CombineServerPath(ResolveAutomaticSavePath(parent), leaf);
    }

    private static bool IsAbsoluteServerPath(string path)
        => path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("\\\\", StringComparison.Ordinal)
            || (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');

    private static string CombineServerPath(string parent, string child)
    {
        if (string.IsNullOrWhiteSpace(parent))
            return child;
        var separator = parent.Contains('\\') && !parent.Contains('/') ? '\\' : '/';
        return $"{parent.TrimEnd('\\', '/')}{separator}{child.TrimStart('\\', '/')}";
    }

    private static string? StringPreference(JsonObject preferences, string key)
        => preferences[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool BooleanPreference(JsonObject preferences, string key)
        => preferences[key] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled;

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".torrent");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var picked = await picker.PickMultipleFilesAsync();
        var added = AddTorrentFiles(picked.OfType<StorageFile>());
        if (added.FirstOrDefault() is { } source)
            await PreviewSourceAsync(source);
    }

    private async void SavePathBrowse_Click(object sender, RoutedEventArgs e)
        => await BrowseFolderAsync(SavePathBox);

    private async void TempPathBrowse_Click(object sender, RoutedEventArgs e)
        => await BrowseFolderAsync(TempPathBox);

    private async Task BrowseFolderAsync(TextBox target)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            target.Text = folder.Path;
    }

    private void UseTempPath_Checked(object sender, RoutedEventArgs e)
        => UpdateDownloadPathEditors();

    private void UpdateDownloadPathEditors()
    {
        var enabled = AutoTmmModeBox.SelectedIndex != 1 && UseTempPathBox.IsChecked == true;
        TempPathBox.IsEnabled = enabled;
        TempPathBrowseButton.IsEnabled = enabled;
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (ParseSources().FirstOrDefault() is { } source)
            await PreviewSourceAsync(source);
    }

    private async Task PreviewSourceAsync(string source)
    {
        if (_viewModel.Api is null || !PreviewButton.IsEnabled)
            return;

        _previewLifetime?.Cancel();
        _previewLifetime?.Dispose();
        _previewLifetime = new CancellationTokenSource();
        var cancellationToken = _previewLifetime.Token;

        PreviewButton.IsEnabled = false;
        SetPreviewBusy(true);
        MetadataSummary.Text = Localizer.Get("AddTorrent_LoadingMetadata", "Loading torrent metadata…");
        ErrorBar.IsOpen = false;
        try
        {
            var metadata = IsRemoteSource(source)
                ? await _viewModel.Api.Torrents.FetchMetadataAsync(source, cancellationToken)
                : await _viewModel.Api.Torrents.ParseMetadataAsync(source, cancellationToken);
            PopulateMetadata(source, metadata);
            MetadataText.Text = metadata.ToJsonString(new() { WriteIndented = true });
        }
        catch (OperationCanceledException)
        {
            // Either the user pressed Stop or the window is closing; a newer preview owns the UI now.
            if (cancellationToken == _previewLifetime?.Token)
                MetadataSummary.Text = Localizer.Get("AddTorrent_MetadataCancelled", "Metadata loading stopped.");
        }
        catch (Exception exception)
        {
            _metadataSource = null;
            _metadataName = null;
            _metadata = null;
            _metadataFiles.Clear();
            ClearMetadataTree();
            ClearTorrentInfo();
            MetadataSummary.Text = Localizer.Get("AddTorrent_MetadataFailed", "Torrent metadata could not be loaded.");
            ShowError(exception);
        }
        finally
        {
            PreviewButton.IsEnabled = true;
            SetPreviewBusy(false);
        }
    }

    private void SetPreviewBusy(bool busy)
    {
        MetadataProgress.IsActive = busy;
        MetadataProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StopPreviewButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StopPreview_Click(object sender, RoutedEventArgs e) => _previewLifetime?.Cancel();

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var sources = ParseSources();
        var files = sources.Where(File.Exists).Concat(_files).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var urls = sources.Where(source => !File.Exists(source)).ToList();
        if (files.Count == 0 && urls.Count == 0)
        {
            ShowError(new InvalidOperationException(Localizer.Get("AddTorrent_SourceRequired", "Add at least one .torrent file, URL, or magnet link.")));
            return;
        }

        var automatic = AutoTmmModeBox.SelectedIndex == 1;
        if (!automatic && string.IsNullOrWhiteSpace(SavePathBox.Text))
        {
            ShowError(new InvalidOperationException(Localizer.Get("AddTorrent_SavePathRequired", "Select a save path.")));
            return;
        }
        if (!automatic && UseTempPathBox.IsChecked == true && string.IsNullOrWhiteSpace(TempPathBox.Text))
        {
            ShowError(new InvalidOperationException(Localizer.Get("AddTorrent_DownloadPathRequired", "Select a path for incomplete torrent data.")));
            return;
        }

        AddButton.IsEnabled = false;
        try
        {
            int? downloadLimit = double.IsNaN(DownloadLimitBox.Value) || DownloadLimitBox.Value <= 0 ? null : checked((int)DownloadLimitBox.Value * 1024);
            int? uploadLimit = double.IsNaN(UploadLimitBox.Value) || UploadLimitBox.Value <= 0 ? null : checked((int)UploadLimitBox.Value * 1024);
            var requestedFilePriorities = GetRequestedFilePriorities(files, urls);
            // File selection is applied through /torrents/filePrio after qBittorrent has
            // created the torrent and assigned stable file indexes.
            var request = CreateAddRequest(files, urls, uploadLimit, downloadLimit);
            var startAfterPriorities = requestedFilePriorities is not null && request.StartTorrent;
            if (startAfterPriorities)
                request = request with { StartTorrent = false };

            var pendingFiles = files.ToList();
            var pendingUrls = urls.Distinct(StringComparer.Ordinal).ToList();
            foreach (var duplicate in FindDuplicateSources(pendingFiles, pendingUrls))
            {
                if (duplicate.IsFile)
                    pendingFiles.RemoveAll(file => file.Equals(duplicate.Source, StringComparison.OrdinalIgnoreCase));
                else
                    pendingUrls.RemoveAll(url => url.Equals(duplicate.Source, StringComparison.Ordinal));

                if (duplicate.Existing.Model.IsPrivate)
                {
                    await TorrentDuplicateChecker.ShowPrivateDuplicateAsync(Root.XamlRoot, duplicate.Existing.Name);
                    continue;
                }

                if (await TorrentDuplicateChecker.AskToMergeDuplicateAsync(Root.XamlRoot, duplicate.Existing.Name))
                    await TorrentDuplicateChecker.MergeDuplicateAsync(_viewModel, duplicate.IsFile, duplicate.Source);
            }

            if (pendingFiles.Count > 0 || pendingUrls.Count > 0)
            {
                var pendingPriorities = pendingFiles.Count + pendingUrls.Count == 1 ? requestedFilePriorities : null;
                var pendingRequest = request with
                {
                    TorrentFiles = pendingFiles,
                    Urls = pendingUrls
                };
                await _viewModel.AddAsync(pendingFiles, pendingUrls, pendingRequest);
                if (pendingPriorities is not null)
                    await ApplyAddedFilePrioritiesAsync(pendingFiles, pendingUrls, pendingPriorities, startAfterPriorities);
                RememberSourceFiles(pendingFiles);
            }
            Close();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            AddButton.IsEnabled = true;
        }
    }

    private IEnumerable<string> ParseSources()
        => SourcesBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Lets the delete dialog later offer to remove the same .torrent file this torrent was added
    // from - qBittorrent itself never records that path once the torrent exists. The hash is
    // computed locally from the file's own bytes rather than trusted from qBittorrent's response,
    // so this works the same way for every file regardless of how the add itself was reported.
    private static void RememberSourceFiles(IReadOnlyList<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                var hashes = TorrentIdentity.FromTorrentFile(file);
                TorrentSourceFileStore.Record(hashes, file);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // A file that cannot be re-read or parsed just does not get tracked; the add
                // itself already succeeded and must not be rolled back over this.
            }
        }
    }

    private TorrentAddRequest CreateAddRequest(
        IReadOnlyList<string> files,
        IReadOnlyList<string> urls,
        int? uploadLimit,
        int? downloadLimit)
    {
        var automatic = AutoTmmModeBox.SelectedIndex == 1;
        var useDownloadPath = !automatic && UseTempPathBox.IsChecked == true;
        return new(
            urls,
            files,
            automatic ? null : NullIfWhiteSpace(SavePathBox.Text),
            DownloadPath: useDownloadPath ? NullIfWhiteSpace(TempPathBox.Text) : null,
            UseDownloadPath: automatic ? null : useDownloadPath,
            Category: NullIfWhiteSpace(CategoryBox.Text),
            Tags: NullIfWhiteSpace(TagsBox.Text),
            StartTorrent: StartBox.IsChecked == true,
            SequentialDownload: SequentialBox.IsChecked == true,
            FirstLastPiecePriority: FirstLastBox.IsChecked == true,
            AutomaticTorrentManagement: automatic,
            SkipChecking: SkipCheckingBox.IsChecked == true,
            UploadLimit: uploadLimit,
            DownloadLimit: downloadLimit);
    }

    private IReadOnlyList<int>? GetRequestedFilePriorities(IReadOnlyList<string> files, IReadOnlyList<string> urls)
    {
        if (files.Count + urls.Count != 1 || _metadataFiles.Count == 0 || !IsMetadataSource(files, urls))
            return null;

        var priorities = _metadataFiles.Select(static file => file.Priority).ToArray();
        return priorities.Any(static priority => priority != 1) ? priorities : null;
    }

    private async Task ApplyAddedFilePrioritiesAsync(
        IReadOnlyList<string> files,
        IReadOnlyList<string> urls,
        IReadOnlyList<int> priorities,
        bool startAfterApplying)
    {
        var api = _viewModel.Api
            ?? throw new InvalidOperationException(Localizer.Get("Connection_NotConnected", "Not connected to qBittorrent."));
        var hashes = ResolveAddedTorrentHashes(files, urls);
        TorrentRowViewModel? torrent = null;
        for (var attempt = 0; attempt < 24 && torrent is null; attempt++)
        {
            torrent = _viewModel.FindDuplicateTorrent(hashes);
            if (torrent is null)
                await Task.Delay(250);
        }
        if (torrent is null)
            throw new InvalidOperationException(Localizer.Get("AddTorrent_PriorityTorrentNotFound", "The torrent was added, but its file list is not available yet."));

        IReadOnlyList<TorrentFile> torrentFiles = [];
        for (var attempt = 0; attempt < 24; attempt++)
        {
            torrentFiles = await api.Torrents.GetFilesAsync(torrent.Hash);
            if (torrentFiles.Count > 0)
                break;
            await Task.Delay(250);
        }
        if (torrentFiles.Count == 0 || torrentFiles.Any(file => file.Index < 0 || file.Index >= priorities.Count))
            throw new InvalidOperationException(Localizer.Get("AddTorrent_PriorityFilesUnavailable", "The torrent was added, but qBittorrent did not return a compatible file list for applying priorities."));

        var requested = torrentFiles
            .Select(file => (File: file, Priority: priorities[file.Index]))
            .Where(static item => item.Priority != 1)
            .ToList();
        foreach (var group in requested.GroupBy(static item => item.Priority))
        {
            await api.Torrents.PostAsync("filePrio", new Dictionary<string, string?>
            {
                ["hash"] = torrent.Hash,
                ["id"] = string.Join('|', group.Select(static item => item.File.Index)),
                ["priority"] = group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        for (var attempt = 0; attempt < 12; attempt++)
        {
            var applied = await api.Torrents.GetFilesAsync(torrent.Hash);
            if (requested.All(item => applied.FirstOrDefault(file => file.Index == item.File.Index)?.Priority == item.Priority))
            {
                if (startAfterApplying)
                    await api.Torrents.ExecuteAsync(TorrentCommand.Start, torrent.Hash);
                return;
            }
            await Task.Delay(200);
        }

        throw new InvalidOperationException(Localizer.Get("AddTorrent_PriorityVerificationFailed", "The torrent was added, but qBittorrent did not apply all selected file priorities."));
    }

    private IReadOnlySet<string> ResolveAddedTorrentHashes(IReadOnlyList<string> files, IReadOnlyList<string> urls)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_metadata is not null && IsMetadataSource(files, urls))
            result.UnionWith(TorrentIdentity.FromMetadata(_metadata));
        if (files.FirstOrDefault() is { } file)
            result.UnionWith(TorrentIdentity.FromTorrentFile(file));
        if (urls.FirstOrDefault() is { } url)
            result.UnionWith(TorrentIdentity.FromMagnet(url));
        return result;
    }

    private IReadOnlyList<DuplicateSource> FindDuplicateSources(
        IReadOnlyList<string> files,
        IReadOnlyList<string> urls)
    {
        var duplicates = new List<DuplicateSource>();
        foreach (var file in files)
        {
            IReadOnlySet<string> hashes;
            try
            {
                hashes = TorrentIdentity.FromTorrentFile(file);
            }
            catch
            {
                hashes = IsMetadataSource([file], []) && _metadata is not null
                    ? TorrentIdentity.FromMetadata(_metadata)
                    : new HashSet<string>();
            }

            if (_viewModel.FindDuplicateTorrent(hashes) is { } existing)
                duplicates.Add(new DuplicateSource(file, true, existing));
        }

        foreach (var url in urls)
        {
            var hashes = TorrentIdentity.FromMagnet(url);
            if (hashes.Count == 0 && IsMetadataSource([], [url]) && _metadata is not null)
                hashes = TorrentIdentity.FromMetadata(_metadata);
            if (_viewModel.FindDuplicateTorrent(hashes) is { } existing)
                duplicates.Add(new DuplicateSource(url, false, existing));
        }

        return duplicates;
    }

    private void PopulateMetadata(string source, JsonObject metadata)
    {
        _metadataSource = source;
        _metadata = metadata;
        _metadataFiles.Clear();

        var info = metadata["info"] as JsonObject ?? metadata;
        if ((info["files"] ?? metadata["files"]) is JsonArray files)
        {
            foreach (var file in files.OfType<JsonObject>())
            {
                var path = MetadataPath(file["path"] ?? file["name"]);
                var length = MetadataLength(file["length"] ?? file["size"]);
                _metadataFiles.Add(new MetadataFileSelection(path, length));
            }
        }

        var name = MetadataPath(info["name"] ?? metadata["name"]);
        if (name == "(unnamed)")
            name = Path.GetFileName(source);
        _metadataName = name;
        var totalLength = MetadataLength(info["length"] ?? info["size"]);
        if (totalLength <= 0)
            totalLength = _metadataFiles.Sum(static file => file.Length);
        if (_metadataFiles.Count == 0 && totalLength > 0)
            _metadataFiles.Add(new MetadataFileSelection(name, totalLength));
        RefreshVisibleMetadataFiles();
        TorrentNameText.Text = name;
        TorrentSizeText.Text = totalLength > 0 ? ValueFormatter.Size(totalLength) : NotAvailable();
        TorrentDateText.Text = MetadataDate(metadata["creation date"] ?? metadata["creation_date"] ?? info["creation date"] ?? info["creation_date"]);
        TorrentHashV1Text.Text = MetadataNodeText(metadata["infohash_v1"] ?? metadata["hash"] ?? metadata["info_hash"] ?? info["infohash_v1"]);
        TorrentHashV2Text.Text = MetadataNodeText(metadata["infohash_v2"] ?? info["infohash_v2"]);
        TorrentCommentText.Text = MetadataNodeText(metadata["comment"] ?? info["comment"]);
        var fileLabel = _metadataFiles.Count == 1
            ? Localizer.Get("AddTorrent_File", "file")
            : Localizer.Get("AddTorrent_Files", "files");
        MetadataSummary.Text = $"{name} · {_metadataFiles.Count} {fileLabel} · {ValueFormatter.Size(totalLength)}";
    }

    private bool IsMetadataSource(IReadOnlyList<string> files, IReadOnlyList<string> urls)
    {
        if (string.IsNullOrWhiteSpace(_metadataSource))
            return false;
        return files.Any(file => string.Equals(file, _metadataSource, StringComparison.OrdinalIgnoreCase))
            || urls.Any(url => string.Equals(url, _metadataSource, StringComparison.Ordinal));
    }

    private void FilesFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshVisibleMetadataFiles();

    private void RefreshVisibleMetadataFiles()
    {
        var filter = FilesFilterBox.Text?.Trim();
        IEnumerable<MetadataFileSelection> visibleFiles = string.IsNullOrWhiteSpace(filter)
            || (_metadataName?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ?? false)
            ? _metadataFiles
            : _metadataFiles
                .Where(file => file.Path.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                .ToList();

        ClearMetadataTree();
        foreach (var root in MetadataTreeBuilder.Build(_metadataName, visibleFiles))
            _visibleMetadataTree.Add(root);
    }

    private void ClearMetadataTree()
    {
        foreach (var node in _visibleMetadataTree)
            node.Dispose();
        _visibleMetadataTree.Clear();
    }

    private static string MetadataPath(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            return text;
        if (node is JsonArray parts)
        {
            var path = string.Join('/', parts
                .Select(static part => part is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
                .Where(static part => !string.IsNullOrWhiteSpace(part)));
            if (!string.IsNullOrWhiteSpace(path))
                return path;
        }
        return "(unnamed)";
    }

    private static long MetadataLength(JsonNode? node)
    {
        if (node is not JsonValue value)
            return 0;
        if (value.TryGetValue<long>(out var longValue))
            return longValue;
        if (value.TryGetValue<int>(out var intValue))
            return intValue;
        return value.TryGetValue<double>(out var doubleValue) ? checked((long)doubleValue) : 0;
    }

    private static string MetadataNodeText(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                return text;
            if (value.TryGetValue<long>(out var longValue))
                return longValue.ToString();
        }

        return NotAvailable();
    }

    private static string MetadataDate(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var unixSeconds) && unixSeconds > 0)
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString("g");
            if (value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                return text;
        }

        return NotAvailable();
    }

    private void ClearTorrentInfo()
    {
        TorrentNameText.Text = string.Empty;
        TorrentSizeText.Text = string.Empty;
        TorrentDateText.Text = string.Empty;
        TorrentHashV1Text.Text = string.Empty;
        TorrentHashV2Text.Text = string.Empty;
        TorrentCommentText.Text = string.Empty;
    }

    private static string NotAvailable() => Localizer.Get("CommonNotAvailable", "N/A");

    private static string DefaultDownloadsPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Downloads")
            : Path.Combine(userProfile, "Downloads");
    }

    private static bool IsRemoteSource(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile;

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add torrent files";
        e.DragUIOverride.IsCaptionVisible = true;
        TorrentDropOverlay.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void Root_DragLeave(object sender, DragEventArgs e)
        => TorrentDropOverlay.Visibility = Visibility.Collapsed;

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        TorrentDropOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var added = AddTorrentFiles(items.OfType<StorageFile>());
            if (added.FirstOrDefault() is { } source)
                await PreviewSourceAsync(source);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private List<string> AddTorrentFiles(IEnumerable<StorageFile> files)
    {
        var added = files
            .Where(static file => file.FileType.Equals(".torrent", StringComparison.OrdinalIgnoreCase))
            .Select(static file => file.Path)
            .Where(path => !_files.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _files.AddRange(added);
        SourcesBox.Text = string.Join(Environment.NewLine, ParseSources().Concat(_files).Distinct(StringComparer.OrdinalIgnoreCase));
        return added;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAllMetadataFiles(true);
    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAllMetadataFiles(false);

    private void MetadataFileCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: MetadataTreeNode node } checkBox)
            return;

        var newValue = node.IsChecked != true;
        checkBox.IsChecked = newValue;
        node.IsChecked = newValue;
    }

    private void SetAllMetadataFiles(bool selected)
    {
        foreach (var file in _metadataFiles)
            file.PriorityIndex = selected ? 1 : 0;
    }

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private void ShowError(Exception exception) { ErrorBar.Message = exception.Message; ErrorBar.IsOpen = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record DuplicateSource(string Source, bool IsFile, TorrentRowViewModel Existing);
}

public sealed class MetadataFileSelection(string path, long length) : INotifyPropertyChanged
{
    private static readonly int[] PriorityValues = [0, 1, 6, 7];
    private int _priorityIndex = 1;

    public string Path { get; } = path;
    public long Length { get; } = length;
    public string SizeText => ValueFormatter.Size(Length);
    public int Priority => PriorityValues[Math.Clamp(_priorityIndex, 0, PriorityValues.Length - 1)];

    public int PriorityIndex
    {
        get => _priorityIndex;
        set
        {
            var index = Math.Clamp(value, 0, PriorityValues.Length - 1);
            if (_priorityIndex == index)
                return;

            _priorityIndex = index;
            OnPropertyChanged(nameof(PriorityIndex));
            OnPropertyChanged(nameof(Priority));
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    public bool IsSelected
    {
        get => PriorityIndex > 0;
        set
        {
            if (value == IsSelected)
                return;
            PriorityIndex = value ? 1 : 0;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class MetadataTreeNode : INotifyPropertyChanged, IDisposable
{
    private readonly MetadataFileSelection? _file;
    private MetadataTreeNode? _parent;
    private bool _isExpanded;
    private bool _disposed;

    private MetadataTreeNode(string name, string fullPath, MetadataFileSelection? file)
    {
        Name = name;
        FullPath = fullPath;
        _file = file;
        if (_file is not null)
            _file.PropertyChanged += File_PropertyChanged;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsFolder => _file is null;
    public string IconGlyph => IsFolder ? "\uE8B7" : "\uE7C3";
    public Windows.UI.Text.FontWeight FontWeight => IsFolder
        ? Microsoft.UI.Text.FontWeights.SemiBold
        : Microsoft.UI.Text.FontWeights.Normal;
    public string MixedPriorityText => Localizer.Get("AddTorrent_PriorityMixed", "Mixed");
    public ObservableCollection<MetadataTreeNode> Children { get; } = [];
    public long Length => _file?.Length ?? Children.Sum(static child => child.Length);
    public string SizeText => ValueFormatter.Size(Length);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    public int PriorityIndex
    {
        get
        {
            if (_file is not null)
                return _file.PriorityIndex;
            if (Children.Count == 0)
                return 1;

            var priority = Children[0].PriorityIndex;
            return priority >= 0 && Children.Skip(1).All(child => child.PriorityIndex == priority)
                ? priority
                : -1;
        }
        set
        {
            if (value < 0)
                return;
            if (_file is not null)
            {
                _file.PriorityIndex = value;
                return;
            }

            foreach (var child in Children)
                child.PriorityIndex = value;
            RaiseStateChanged();
        }
    }

    public bool? IsChecked
    {
        get
        {
            if (_file is not null)
                return _file.IsSelected;
            if (Children.Count == 0)
                return false;

            var selectedCount = Children.Count(static child => child.IsChecked == true);
            if (selectedCount == Children.Count)
                return true;
            return selectedCount == 0 && Children.All(static child => child.IsChecked == false)
                ? false
                : null;
        }
        set
        {
            if (!value.HasValue)
                return;
            SetSelected(value.Value);
            RaiseStateChanged();
        }
    }

    internal static MetadataTreeNode Folder(string name, string fullPath)
        => new(name, fullPath, null);

    internal static MetadataTreeNode File(string name, string fullPath, MetadataFileSelection file)
        => new(name, fullPath, file);

    internal void AddChild(MetadataTreeNode child)
    {
        child._parent = this;
        Children.Add(child);
    }

    private void SetSelected(bool selected)
    {
        if (_file is not null)
        {
            if (!selected)
                _file.PriorityIndex = 0;
            else if (_file.PriorityIndex == 0)
                _file.PriorityIndex = 1;
            return;
        }

        foreach (var child in Children)
            child.SetSelected(selected);
    }

    private void File_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MetadataFileSelection.PriorityIndex)
            or nameof(MetadataFileSelection.Priority)
            or nameof(MetadataFileSelection.IsSelected))
            RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(PriorityIndex));
        OnPropertyChanged(nameof(IsChecked));
        _parent?.RaiseStateChanged();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_file is not null)
            _file.PropertyChanged -= File_PropertyChanged;
        foreach (var child in Children)
            child.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal static class MetadataTreeBuilder
{
    public static IReadOnlyList<MetadataTreeNode> Build(
        string? torrentName,
        IEnumerable<MetadataFileSelection> files)
    {
        var fileList = files.ToList();
        if (fileList.Count == 0)
            return [];

        var paths = fileList
            .Select(file => (File: file, Parts: SplitPath(file.Path)))
            .ToList();
        var normalizedTorrentName = string.IsNullOrWhiteSpace(torrentName) ? null : torrentName.Trim();
        var shouldAddTorrentRoot = fileList.Count > 1
            && normalizedTorrentName is not null
            && paths.All(path => path.Parts.Length == 0
                || !path.Parts[0].Equals(normalizedTorrentName, StringComparison.CurrentCultureIgnoreCase));

        var roots = new List<MetadataTreeNode>();
        MetadataTreeNode? torrentRoot = null;
        if (shouldAddTorrentRoot)
        {
            torrentRoot = MetadataTreeNode.Folder(normalizedTorrentName!, normalizedTorrentName!);
            torrentRoot.IsExpanded = true;
            roots.Add(torrentRoot);
        }

        foreach (var (file, rawParts) in paths)
        {
            var parts = rawParts.Length == 0 ? [file.Path] : rawParts;
            IList<MetadataTreeNode> siblings = torrentRoot is null ? roots : torrentRoot.Children;
            MetadataTreeNode? parent = torrentRoot;
            var currentPath = torrentRoot?.FullPath ?? string.Empty;

            for (var index = 0; index < parts.Length - 1; index++)
            {
                var part = parts[index];
                currentPath = CombinePath(currentPath, part);
                var folder = siblings.FirstOrDefault(node => node.IsFolder
                    && node.Name.Equals(part, StringComparison.CurrentCultureIgnoreCase));
                if (folder is null)
                {
                    folder = MetadataTreeNode.Folder(part, currentPath);
                    folder.IsExpanded = parent is null || parent == torrentRoot;
                    if (parent is null)
                        roots.Add(folder);
                    else
                        parent.AddChild(folder);
                }

                parent = folder;
                siblings = folder.Children;
            }

            var fileName = parts[^1];
            var filePath = CombinePath(currentPath, fileName);
            var fileNode = MetadataTreeNode.File(fileName, filePath, file);
            if (parent is null)
                roots.Add(fileNode);
            else
                parent.AddChild(fileNode);
        }

        SortNodes(roots);
        return roots;
    }

    private static void SortNodes(IList<MetadataTreeNode> nodes)
    {
        foreach (var node in nodes)
            SortNodes(node.Children);

        var ordered = nodes
            .OrderByDescending(static node => node.IsFolder)
            .ThenBy(static node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        nodes.Clear();
        foreach (var node in ordered)
            nodes.Add(node);
    }

    private static string[] SplitPath(string path)
        => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string CombinePath(string parent, string name)
        => string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";
}
