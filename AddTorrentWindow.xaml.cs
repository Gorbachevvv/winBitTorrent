using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinBitTorrent.Core.Abstractions;
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
    private readonly ObservableCollection<MetadataFileSelection> _visibleMetadataFiles = [];
    private string? _metadataSource;
    private JsonObject? _metadata;
    private bool _initialized;

    public AddTorrentWindow(IReadOnlyList<string> torrentFiles, IReadOnlyList<string> urls)
    {
        InitializeComponent();
        Title = Localizer.Get("WindowTitle_AddTorrent", "Add torrent");
        this.ConfigureOwned(1120, 720);
        _viewModel = App.Services.GetRequiredService<MainViewModel>();
        _files = torrentFiles.ToList();
        MetadataFilesList.ItemsSource = _visibleMetadataFiles;
        SavePathBox.Text = DefaultDownloadsPath();
        SourcesBox.Text = string.Join(Environment.NewLine, torrentFiles.Concat(urls));
        Activated += AddTorrentWindow_Activated;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Closed += (_, _) => _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
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
        if (e.PropertyName != nameof(MainViewModel.IsConnected) || !_viewModel.IsConnected || _viewModel.Api is null)
            return;

        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        await InitializeFromApiAsync();
    }

    private async Task InitializeFromApiAsync()
    {
        if (_initialized)
            return;
        _initialized = true;

        if (ParseSources().FirstOrDefault() is { } source)
            await PreviewSourceAsync(source);
    }

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
    {
        var enabled = UseTempPathBox.IsChecked == true;
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

        PreviewButton.IsEnabled = false;
        MetadataSummary.Text = Localizer.Get("AddTorrent_LoadingMetadata", "Loading torrent metadata…");
        ErrorBar.IsOpen = false;
        try
        {
            var metadata = IsRemoteSource(source)
                ? await _viewModel.Api.Torrents.FetchMetadataAsync(source)
                : await _viewModel.Api.Torrents.ParseMetadataAsync(source);
            PopulateMetadata(source, metadata);
            MetadataText.Text = metadata.ToJsonString(new() { WriteIndented = true });
        }
        catch (Exception exception)
        {
            _metadataSource = null;
            _metadata = null;
            _metadataFiles.Clear();
            _visibleMetadataFiles.Clear();
            ClearTorrentInfo();
            MetadataSummary.Text = Localizer.Get("AddTorrent_MetadataFailed", "Torrent metadata could not be loaded.");
            ShowError(exception);
        }
        finally
        {
            PreviewButton.IsEnabled = true;
        }
    }

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

        AddButton.IsEnabled = false;
        try
        {
            int? downloadLimit = double.IsNaN(DownloadLimitBox.Value) || DownloadLimitBox.Value <= 0 ? null : checked((int)DownloadLimitBox.Value * 1024);
            int? uploadLimit = double.IsNaN(UploadLimitBox.Value) || UploadLimitBox.Value <= 0 ? null : checked((int)UploadLimitBox.Value * 1024);
            var filePriorities = GetRequestedFilePriorities(files, urls);
            var request = CreateAddRequest(files, urls, uploadLimit, downloadLimit, filePriorities);

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
                    await ShowPrivateDuplicateAsync(duplicate.Existing.Name);
                    continue;
                }

                if (await AskToMergeDuplicateAsync(duplicate.Existing.Name))
                    await MergeDuplicateAsync(duplicate, request);
            }

            if (pendingFiles.Count > 0 || pendingUrls.Count > 0)
            {
                var pendingPriorities = pendingFiles.Count + pendingUrls.Count == 1 ? filePriorities : null;
                var pendingRequest = request with
                {
                    TorrentFiles = pendingFiles,
                    Urls = pendingUrls,
                    FilePriorities = pendingPriorities
                };
                try
                {
                    await _viewModel.AddAsync(pendingFiles, pendingUrls, pendingRequest);
                }
                catch (QbittorrentApiException exception) when (pendingPriorities is not null && IsFilePrioritiesRejectedForSeeding(exception))
                {
                    await _viewModel.AddAsync(pendingFiles, pendingUrls, pendingRequest with { FilePriorities = null });
                }
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

    private TorrentAddRequest CreateAddRequest(
        IReadOnlyList<string> files,
        IReadOnlyList<string> urls,
        int? uploadLimit,
        int? downloadLimit,
        IReadOnlyList<int>? filePriorities)
        => new(
            urls,
            files,
            NullIfWhiteSpace(SavePathBox.Text),
            DownloadPath: UseTempPathBox.IsChecked == true ? NullIfWhiteSpace(TempPathBox.Text) : null,
            Category: NullIfWhiteSpace(CategoryBox.Text),
            Tags: NullIfWhiteSpace(TagsBox.Text),
            StartTorrent: StartBox.IsChecked == true,
            SequentialDownload: SequentialBox.IsChecked == true,
            FirstLastPiecePriority: FirstLastBox.IsChecked == true,
            AutomaticTorrentManagement: AutoTmmModeBox.SelectedIndex == 1,
            SkipChecking: SkipCheckingBox.IsChecked == true,
            UploadLimit: uploadLimit,
            DownloadLimit: downloadLimit,
            FilePriorities: filePriorities);

    private IReadOnlyList<int>? GetRequestedFilePriorities(IReadOnlyList<string> files, IReadOnlyList<string> urls)
    {
        if (files.Count + urls.Count != 1 || _metadataFiles.Count == 0 || !IsMetadataSource(files, urls))
            return null;

        var priorities = _metadataFiles.Select(static file => file.Priority).ToArray();
        return priorities.Any(static priority => priority != 1) ? priorities : null;
    }

    private static bool IsFilePrioritiesRejectedForSeeding(QbittorrentApiException exception)
        => exception.StatusCode == 400
            && exception.Message.Contains("filePriorities", StringComparison.OrdinalIgnoreCase)
            && (exception.Message.Contains("seeding", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("разда", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("отда", StringComparison.OrdinalIgnoreCase));

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

    private async Task<bool> AskToMergeDuplicateAsync(string name)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
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

    private async Task ShowPrivateDuplicateAsync(string name)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Localizer.Get("DuplicateTorrent_Title", "Torrent is already present"),
            Content = string.Format(
                Localizer.Get("DuplicateTorrent_Private", "Torrent '{0}' is private. Its trackers cannot be merged."),
                name),
            CloseButtonText = Localizer.Get("Common_OK", "OK"),
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private async Task MergeDuplicateAsync(DuplicateSource duplicate, TorrentAddRequest request)
    {
        var api = _viewModel.Api
            ?? throw new InvalidOperationException(Localizer.Get("Connection_NotConnected", "Not connected to qBittorrent."));
        var preferences = await api.Application.GetPreferencesAsync();
        var mergeWasEnabled = preferences["merge_trackers"]?.GetValue<bool>() == true;
        if (!mergeWasEnabled)
            await api.Application.SetPreferencesAsync(new JsonObject { ["merge_trackers"] = true });

        try
        {
            IReadOnlyList<string> files = duplicate.IsFile ? [duplicate.Source] : [];
            IReadOnlyList<string> urls = duplicate.IsFile ? [] : [duplicate.Source];
            var duplicateRequest = request with
            {
                TorrentFiles = files,
                Urls = urls,
                FilePriorities = null
            };
            await _viewModel.AddAsync(files, urls, duplicateRequest);
        }
        finally
        {
            if (!mergeWasEnabled)
                await api.Application.SetPreferencesAsync(new JsonObject { ["merge_trackers"] = false });
        }
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
        _visibleMetadataFiles.Clear();
        foreach (var file in _metadataFiles)
        {
            if (string.IsNullOrWhiteSpace(filter) || file.Path.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                _visibleMetadataFiles.Add(file);
        }
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

    private void SetAllMetadataFiles(bool selected)
    {
        foreach (var file in _metadataFiles)
            file.PriorityIndex = selected ? 1 : 0;
        RefreshVisibleMetadataFiles();
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
