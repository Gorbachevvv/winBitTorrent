using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppLifecycle;
using WinBitTorrent.ViewModels;
using WinBitTorrent.Services;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinBitTorrent.Core.Models;

namespace WinBitTorrent;

public sealed partial class MainWindow : Window
{
    private static readonly Uri RepositoryUri = new("https://github.com/Gorbachevvv/winBitTorrent");
    private static readonly Uri RepositoryIssuesUri = new("https://github.com/Gorbachevvv/winBitTorrent/issues");

    private readonly AppWindow _appWindow;
    private readonly IntPtr _windowHandle;
    private readonly TrayIconService _trayIcon;
    private bool _allowClose;
    private bool _closing;
    private bool _isWindowVisible = true;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        RootGrid.DataContext = viewModel;
        RestoreWorkspaceTabs();
        RootGrid.RequestedTheme = (ClientSettings.GetValue("ui.theme") as string) switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "WinBitTorrent.ico"));
        _appWindow.Resize(new Windows.Graphics.SizeInt32(1240, 800));
        _appWindow.Closing += AppWindow_Closing;
        Closed += MainWindow_Closed;
        _trayIcon = new TrayIconService(
            _windowHandle,
            ExecuteTrayCommand,
            () => _isWindowVisible,
            () => ViewModel.IsConnected,
            () => ViewModel.UseAlternativeSpeedLimits);
    }

    public MainViewModel ViewModel { get; }

    public void ShowMainWindow()
    {
        ShowWindow(_windowHandle, ShowWindowCommand.Show);
        _isWindowVisible = true;
        Activate();
        SetForegroundWindow(_windowHandle);
    }

    public void HandleActivation(AppActivationArguments args, bool isInitialLaunch = false)
    {
        switch (args.Kind)
        {
            case ExtendedActivationKind.File when args.Data is IFileActivatedEventArgs fileArgs:
                var files = fileArgs.Files.OfType<StorageFile>().Where(file => file.FileType.Equals(".torrent", StringComparison.OrdinalIgnoreCase)).Select(file => file.Path).ToList();
                if (files.Count > 0)
                    OpenAddTorrent(files, []);
                break;
            case ExtendedActivationKind.Protocol when args.Data is IProtocolActivatedEventArgs protocolArgs:
                OpenAddTorrent([], [protocolArgs.Uri.AbsoluteUri]);
                break;
            case ExtendedActivationKind.Launch:
                HandleLaunchActivation(args, isInitialLaunch);
                break;
        }
    }

    private void HandleLaunchActivation(AppActivationArguments args, bool isInitialLaunch)
    {
        // Prefer the arguments carried by THIS activation. When a second instance is
        // redirected into the running app, args holds the new file/link; the running
        // process's own Environment.GetCommandLineArgs() would still be the one it was
        // originally started with (the "previous torrent" bug). Only fall back to the
        // process command line on the very first launch, where it is the correct value.
        // Both forms include the executable path as the first token, so skip it.
        var launchArguments = (args.Data as ILaunchActivatedEventArgs)?.Arguments;
        IEnumerable<string> tokens;
        if (!string.IsNullOrWhiteSpace(launchArguments))
            tokens = SplitArguments(launchArguments);
        else if (isInitialLaunch)
            tokens = Environment.GetCommandLineArgs();
        else
            return;

        var payload = tokens
            .Skip(1)
            .Select(NormalizeActivationArgument)
            .FirstOrDefault(value => value is not null
                && (value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                    || (value.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(value))));
        if (payload is null)
            return;

        if (payload.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            OpenAddTorrent([], [payload]);
        else
            OpenAddTorrent([payload], []);
    }

    private static string NormalizeActivationArgument(string token)
    {
        var value = token.Trim().Trim('"');
        // Rich activation wraps the payload in a "----ms-protocol:" / "----ms-file:"
        // marker; strip it if a token still carries one.
        foreach (var prefix in (string[])["----ms-protocol:", "----ms-file:"])
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                value = value[prefix.Length..].Trim().Trim('"');
        }

        return value;
    }

    private static List<string> SplitArguments(string commandLine)
    {
        // Minimal quote-aware tokenizer: magnet URIs are a single space-free token,
        // and file paths that contain spaces arrive quoted.
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var character in commandLine)
        {
            if (character == '"')
                inQuotes = !inQuotes;
            else if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else
                current.Append(character);
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    private void OpenAddTorrent(IReadOnlyList<string> files, IReadOnlyList<string> urls)
        => DispatcherQueue.TryEnqueue(() => new AddTorrentWindow(files, urls).Activate());

    private async void AddTorrentFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".torrent");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
            new AddTorrentWindow(files.Select(static file => file.Path).ToList(), []).Activate();
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add torrent files";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
        TorrentDropOverlay.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void RootGrid_DragLeave(object sender, DragEventArgs e)
        => TorrentDropOverlay.Visibility = Visibility.Collapsed;

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        TorrentDropOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var files = items
                .OfType<StorageFile>()
                .Where(static file => file.FileType.Equals(".torrent", StringComparison.OrdinalIgnoreCase))
                .Select(static file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count > 0)
                new AddTorrentWindow(files, []).Activate();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void AddTorrentLink_Click(object sender, RoutedEventArgs e) => new AddTorrentWindow([], []).Activate();
    private void CreateTorrent_Click(object sender, RoutedEventArgs e) => new CreateTorrentWindow().Activate();
    private void Profiles_Click(object sender, RoutedEventArgs e) => new ProfilesWindow().Activate();
    private void Options_Click(object sender, RoutedEventArgs e) => new SettingsWindow().Activate();
    private void Cookies_Click(object sender, RoutedEventArgs e) => new CookiesWindow().Activate();

    private async void Statistics_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Api is null)
            return;
        try
        {
            var transfer = await ViewModel.Api.Transfer.GetInfoAsync();
            var process = await ViewModel.Api.Application.GetProcessInfoAsync();
            var content = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                Height = 430,
                Width = 680,
                Text = $"Transfer statistics\n{transfer.ToJsonString(new() { WriteIndented = true })}\n\nBackend process\n{process.ToJsonString(new() { WriteIndented = true })}"
            };
            await new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "qBittorrent statistics", Content = content, CloseButtonText = "Close" }.ShowAsync();
        }
        catch (Exception exception)
        {
            await new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "Statistics unavailable", Content = exception.Message, CloseButtonText = "Close" }.ShowAsync();
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
        => await TorrentActions.ConfirmDeleteSelectedAsync(RootGrid.XamlRoot, ViewModel);

    private async void AlternativeSpeed_Click(object sender, RoutedEventArgs e) => await ViewModel.ToggleAlternativeSpeedLimitsAsync();

    private void NavigateMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string value })
            return;

        var tab = WorkspaceTabs.TabItems
            .OfType<TabViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), value, StringComparison.Ordinal));
        if (tab is null)
            return;
        tab.Visibility = Visibility.Visible;
        WorkspaceTabs.SelectedItem = tab;
        SaveWorkspaceTabs();
    }

    private void WorkspaceTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab is not TabViewItem tab)
            return;

        var closedIndex = sender.TabItems.IndexOf(tab);
        tab.Visibility = Visibility.Collapsed;
        for (var offset = 1; offset <= sender.TabItems.Count; offset++)
        {
            var candidateIndex = (closedIndex + offset) % sender.TabItems.Count;
            if (sender.TabItems[candidateIndex] is TabViewItem { Visibility: Visibility.Visible } candidate)
            {
                sender.SelectedItem = candidate;
                break;
            }
        }
        SaveWorkspaceTabs();
    }

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => SaveWorkspaceTabs();

    private void RestoreWorkspaceTabs()
    {
        var tabs = WorkspaceTabs.TabItems.OfType<TabViewItem>().ToList();
        if (ClientSettings.GetValue("workspace.hiddenTabs") is string hiddenJson)
        {
            try
            {
                var hiddenTags = JsonSerializer.Deserialize<List<string>>(hiddenJson) ?? [];
                foreach (var tab in tabs)
                    if (tab.Tag?.ToString() is { } tag && hiddenTags.Contains(tag))
                        tab.Visibility = Visibility.Collapsed;
            }
            catch (JsonException)
            {
            }
        }

        var selectedTag = ClientSettings.GetValue("workspace.selectedTab") as string;
        var selected = tabs.FirstOrDefault(tab => tab.Visibility == Visibility.Visible
            && string.Equals(tab.Tag?.ToString(), selectedTag, StringComparison.Ordinal))
            ?? tabs.FirstOrDefault(static tab => tab.Visibility == Visibility.Visible);
        if (selected is not null)
            WorkspaceTabs.SelectedItem = selected;
    }

    private void SaveWorkspaceTabs()
    {
        var hiddenTags = WorkspaceTabs.TabItems
            .OfType<TabViewItem>()
            .Where(static tab => tab.Visibility == Visibility.Collapsed)
            .Select(static tab => tab.Tag?.ToString() ?? string.Empty)
            .ToList();
        ClientSettings.SetValue("workspace.hiddenTabs", JsonSerializer.Serialize(hiddenTags));
        if (WorkspaceTabs.SelectedItem is TabViewItem { Tag: { } selectedTag })
            ClientSettings.SetValue("workspace.selectedTab", selectedTag.ToString());
    }

    private async void Documentation_Click(object sender, RoutedEventArgs e)
        => await Launcher.LaunchUriAsync(new Uri("https://github.com/qbittorrent/qBittorrent/wiki"));

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var backend = string.Empty;
        if (ViewModel.Api is not null)
        {
            try
            {
                backend = $"qBittorrent {await ViewModel.Api.Application.GetVersionAsync()} / Web API {await ViewModel.Api.Application.GetWebApiVersionAsync()}";
            }
            catch
            {
            }
        }

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new Image
        {
            Width = 40,
            Height = 40,
            Source = new BitmapImage(new Uri("ms-appx:///Assets/Square44x44Logo.targetsize-24_altform-unplated.png"))
        });
        var titleColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleColumn.Children.Add(new TextBlock { Text = "WinBitTorrent", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titleColumn.Children.Add(new TextBlock { Text = $"v{AppVersion.Display}", FontSize = 12, Opacity = 0.7 });
        header.Children.Add(titleColumn);

        var description = Localizer.Get("Dialog_AboutDescription", "Native WinUI 3 client for qBittorrent 5.2.3 / Web API 2.15.1");
        if (!string.IsNullOrEmpty(backend))
            description += $"\n{string.Format(Localizer.Get("Dialog_AboutConnectedBackend", "Connected backend: {0}"), backend)}";

        var links = new StackPanel { Spacing = 2 };
        links.Children.Add(new HyperlinkButton
        {
            NavigateUri = RepositoryUri,
            Content = Localizer.Get("Dialog_AboutRepository", "GitHub repository"),
            Padding = new Thickness(0)
        });
        links.Children.Add(new HyperlinkButton
        {
            NavigateUri = RepositoryIssuesUri,
            Content = Localizer.Get("Dialog_AboutReportIssue", "Report an issue"),
            Padding = new Thickness(0)
        });

        var body = new StackPanel { Spacing = 14, Width = 340 };
        body.Children.Add(header);
        body.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(links);
        body.Children.Add(new TextBlock
        {
            Text = Localizer.Get("Dialog_AboutLicense", "qBittorrent is licensed under GPL-2.0-or-later. Third-party notices and the corresponding-source offer are included with the app."),
            FontSize = 11,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "WinBitTorrent",
            Content = body,
            CloseButtonText = Localizer.Get("Common_Close", "Close")
        };
        await dialog.ShowAsync();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => _ = ExitApplicationAsync();

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
            return;

        args.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        ShowWindow(_windowHandle, ShowWindowCommand.Hide);
        _isWindowVisible = false;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
        => _trayIcon.Dispose();

    private void ExecuteTrayCommand(TrayIconCommand command)
    {
        switch (command)
        {
            case TrayIconCommand.ToggleWindow:
                if (_isWindowVisible)
                    HideToTray();
                else
                    ShowMainWindow();
                break;
            case TrayIconCommand.AddTorrentFile:
                ShowMainWindow();
                AddTorrentFile_Click(this, new RoutedEventArgs());
                break;
            case TrayIconCommand.AddTorrentLink:
                ShowMainWindow();
                new AddTorrentWindow([], []).Activate();
                break;
            case TrayIconCommand.ToggleAlternativeSpeedLimits:
                _ = ToggleAlternativeSpeedLimitsFromTrayAsync();
                break;
            case TrayIconCommand.GlobalSpeedLimits:
                _ = ShowGlobalSpeedLimitsDialogAsync();
                break;
            case TrayIconCommand.StartAll:
                _ = ExecuteAllFromTrayAsync(TorrentCommand.Start);
                break;
            case TrayIconCommand.StopAll:
                _ = ExecuteAllFromTrayAsync(TorrentCommand.Stop);
                break;
            case TrayIconCommand.Options:
                ShowMainWindow();
                new SettingsWindow().Activate();
                break;
            case TrayIconCommand.Exit:
                _ = ExitApplicationAsync();
                break;
        }
    }

    private async Task ToggleAlternativeSpeedLimitsFromTrayAsync()
    {
        if (ViewModel.Api is null)
            return;
        await ViewModel.ToggleAlternativeSpeedLimitsAsync();
    }

    private async Task ShowGlobalSpeedLimitsDialogAsync()
    {
        if (ViewModel.Api is null)
            return;

        ShowMainWindow();
        var downloadLimit = await ViewModel.Api.Transfer.GetDownloadLimitAsync();
        var uploadLimit = await ViewModel.Api.Transfer.GetUploadLimitAsync();
        var downloadBox = CreateSpeedLimitBox(
            Localizer.Get("Dialog_DownloadLimit", "Download limit (KiB/s, 0 = unlimited)"),
            downloadLimit);
        var uploadBox = CreateSpeedLimitBox(
            Localizer.Get("Dialog_UploadLimit", "Upload limit (KiB/s, 0 = unlimited)"),
            uploadLimit);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(downloadBox);
        content.Children.Add(uploadBox);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = Localizer.Get("Tray_GlobalSpeedLimits", "Global speed limits..."),
            Content = content,
            PrimaryButtonText = Localizer.Get("CommonSave.Content", "Save"),
            CloseButtonText = Localizer.Get("CommonCancel.Content", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        await ViewModel.Api.Transfer.SetDownloadLimitAsync(ToBytesPerSecond(downloadBox.Value));
        await ViewModel.Api.Transfer.SetUploadLimitAsync(ToBytesPerSecond(uploadBox.Value));
    }

    private async Task ExecuteAllFromTrayAsync(TorrentCommand command)
    {
        if (ViewModel.Api is null)
            return;
        await ViewModel.ExecuteAllAsync(command);
    }

    private async Task ExitApplicationAsync()
    {
        if (_closing)
            return;

        _closing = true;
        try
        {
            _allowClose = true;
            _trayIcon.Dispose();
            await ViewModel.ShutdownAsync();
            Close();
        }
        finally
        {
            _closing = false;
        }
    }

    private static NumberBox CreateSpeedLimitBox(string header, long bytesPerSecond)
        => new()
        {
            Header = header,
            Value = bytesPerSecond <= 0 ? 0 : bytesPerSecond / 1024d,
            Minimum = 0,
            SmallChange = 50,
            LargeChange = 500,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };

    private static long ToBytesPerSecond(double kibPerSecond)
        => double.IsNaN(kibPerSecond) || kibPerSecond <= 0
            ? 0
            : (long)Math.Round(kibPerSecond * 1024d, MidpointRounding.AwayFromZero);

    private enum ShowWindowCommand
    {
        Hide = 0,
        Show = 5
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, ShowWindowCommand nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
