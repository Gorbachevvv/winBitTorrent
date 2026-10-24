using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.System;
using WinBitTorrent.Core.Services;
using WinBitTorrent.Services;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent;

public sealed partial class FirstRunDialog : ContentDialog
{
    private readonly MainViewModel _main;
    private readonly nint _ownerHandle;
    private readonly Action<ElementTheme> _previewTheme;
    private OnboardingDraft _draft;
    private bool _loading = true;
    private bool _saving;
    private bool _closed;
    private bool? _initialStartup;
    private readonly Task _initialization;

    public FirstRunDialog(MainViewModel main, nint ownerHandle, Action<ElementTheme> previewTheme, Task backendReady)
    {
        _main = main;
        _ownerHandle = ownerHandle;
        _previewTheme = previewTheme;
        _draft = OnboardingPreferences.Load();
        InitializeComponent();
        ActualThemeChanged += (_, _) => UpdateSidebarSelection();
        CloseButtonText = L("Later", "Set up later");
        SecondaryButtonText = L("Back", "Back");
        AutomationProperties.SetName(this, L("AccessibleTitle", "Welcome to WinBitTorrent"));
        AutomationProperties.SetName(NotificationsToggle, L("NotifyTitle", "Keep me in the loop"));
        AutomationProperties.SetName(StartupToggle, L("StartupTitle", "Start with Windows"));
        DownloadPathBox.Text = _draft.DownloadPath;
        NotificationsToggle.IsOn = _draft.Notifications;
        StartupToggle.IsOn = _draft.Startup == true;
        (_draft.Theme switch { "Light" => LightTheme, "Dark" => DarkTheme, _ => SystemTheme }).IsChecked = true;
        _loading = false;
        ApplyTheme();
        RenderStep();
        Opened += (_, _) =>
        {
            ConfigureDialogChrome();
            ResizeToRoot();
            XamlRoot.Changed += Root_Changed;
            Heading.Focus(FocusState.Programmatic);
        };
        Closed += (_, _) => XamlRoot.Changed -= Root_Changed;
        _initialization = InitializeAsync(backendReady);
    }

    public bool Completed { get; private set; }
    private static string L(string key, string fallback) => Localizer.Get("SetupText_" + key, fallback);

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ConfigureDialogChrome();
    }

    private void ConfigureDialogChrome()
    {
        // Keep WinUI's native commands, default action and dismissal behavior. The body
        // has no outer inset; only the command bar retains padding below the artwork.
        if (GetTemplateChild("CommandSpace") is Grid commands)
        {
            commands.Padding = new Thickness(24, 20, 24, 20);
            commands.Margin = new Thickness(0);
            // The left action uses the remaining space; navigation stays grouped on the right.
            commands.ColumnDefinitions.Clear();
            commands.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            commands.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            commands.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            commands.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            commands.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            commands.ColumnSpacing = 0;
        }
        if (GetTemplateChild("PrimaryButton") is Button next)
        {
            Grid.SetColumn(next, 4);
            next.Margin = new Thickness(5, 0, 0, 0);
            next.MinWidth = 156;
            next.TabIndex = 2;
        }
        if (GetTemplateChild("SecondaryButton") is Button back)
        {
            Grid.SetColumn(back, 2);
            back.Margin = new Thickness(0, 0, 5, 0);
            back.MinWidth = 136;
            back.TabIndex = 1;
        }
        if (GetTemplateChild("CloseButton") is Button later)
        {
            Grid.SetColumn(later, 0);
            later.Margin = new Thickness(0);
            later.MinWidth = 146;
            later.HorizontalAlignment = HorizontalAlignment.Left;
            later.TabIndex = 0;
        }
    }

    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeToRoot();

    private void ResizeToRoot()
    {
        Layout.Width = Math.Min(856, Math.Max(300, XamlRoot.Size.Width - 48));
        // Move 20 DIPs from the page into the gap above the commands, keeping total height.
        Layout.Height = Math.Min(488, Math.Max(200, XamlRoot.Size.Height - 156));
        var compact = Layout.Width < 748;
        Sidebar.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Layout.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 216);
    }

    private async Task InitializeAsync(Task backendReady)
    {
        try
        {
            _initialStartup = await WindowsStartupService.IsEnabledAsync();
            if (_closed) return;
            if (_draft.Startup is null)
            {
                _loading = true;
                StartupToggle.IsOn = _initialStartup.Value;
                _loading = false;
            }
        }
        catch (Exception exception) { ShowError(exception.Message); }
        try
        {
            await backendReady;
            if (_closed) return;
            if (_main.Api is { } api && string.IsNullOrWhiteSpace(DownloadPathBox.Text))
            {
                var path = await api.Application.GetDefaultSavePathAsync();
                if (_closed) return;
                // Do not overwrite a folder the user picked while the engine was connecting.
                if (string.IsNullOrWhiteSpace(DownloadPathBox.Text)) DownloadPathBox.Text = path;
            }
            BrowseButton.IsEnabled = _main.CanUseLocalFiles;
            RenderSummary();
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void ApplyTheme()
    {
        RequestedTheme = _draft.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        _previewTheme(RequestedTheme);
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _draft = _draft with { Theme = (string)((RadioButton)sender).Tag };
        ApplyTheme();
        SaveDraft();
    }

    private void Draft_Changed(object sender, TextChangedEventArgs e) => CaptureDraft();
    private void Draft_Toggled(object sender, RoutedEventArgs e) => CaptureDraft();
    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _draft = _draft with { Startup = StartupToggle.IsOn };
        CaptureDraft();
    }

    private void CaptureDraft()
    {
        if (_loading) return;
        _draft = _draft with { DownloadPath = DownloadPathBox.Text.Trim(), Notifications = NotificationsToggle.IsOn };
        SaveDraft();
    }

    private bool SaveDraft()
    {
        try { OnboardingPreferences.SaveDraft(_draft); return true; }
        catch (Exception exception) { ShowError(exception.Message); return false; }
    }

    private void RenderStep(bool resetScroll = true)
    {
        FrameworkElement[] pages = [AppearancePage, DownloadsPage, WindowsPage, ReadyPage];
        string[] titles = [L("TitleAppearance", "Choose a theme"), L("TitleDownloads", "Set up downloads"), L("TitleWindows", "Set up Windows integration"), L("TitleReady", "Review your settings")];
        for (var index = 0; index < pages.Length; index++)
        {
            pages[index].Visibility = index == _draft.Step ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateSidebarSelection();
        Heading.Text = titles[_draft.Step];
        StepCaption.Text = string.Format(L("StepFormat", "GET STARTED  /  {0} OF 4"), _draft.Step + 1);
        StepProgress.Value = _draft.Step + 1;
        IsSecondaryButtonEnabled = _draft.Step > 0;
        PrimaryButtonText = _draft.Step == 3 ? L("Finish", "Start downloading") : L("Next", "Continue");
        if (resetScroll) PageScroll.ChangeView(null, 0, null);
        RenderSummary();
    }

    private void UpdateSidebarSelection()
    {
        Button[] steps = [Step0, Step1, Step2, Step3];
        var selected = ActualTheme == ElementTheme.Light
            ? ColorHelper.FromArgb(16, 0, 0, 0)
            : ColorHelper.FromArgb(40, 255, 255, 255);
        for (var index = 0; index < steps.Length; index++)
            steps[index].Background = new SolidColorBrush(index == _draft.Step ? selected : Colors.Transparent);
    }

    private string ThemeName => _draft.Theme switch { "Light" => L("LightLabel", "Light"), "Dark" => L("DarkLabel", "Dark"), _ => L("SystemLabel", "System") };
    private string OnOff(bool value) => value ? L("On", "On") : L("Off", "Off");
    private void RenderSummary()
    {
        SummaryTheme.Text = L("SummaryTheme", "Theme: {0}").Replace("{0}", ThemeName);
        SummaryPath.Text = L("SummaryPath", "Downloads: {0}").Replace("{0}", DownloadPathBox.Text);
        SummaryStartup.Text = L("SummaryStartup", "Start with Windows: {0}").Replace("{0}", OnOff(StartupToggle.IsOn));
        SummaryNotifications.Text = L("SummaryNotifications", "Notifications: {0}").Replace("{0}", OnOff(NotificationsToggle.IsOn));
    }

    private void MoveTo(int step)
    {
        if (_saving) return;
        _draft = _draft with { Step = Math.Clamp(step, 0, 3) };
        ErrorBar.IsOpen = false;
        SaveDraft();
        RenderStep();
    }

    private void Step_Click(object sender, RoutedEventArgs e) => MoveTo(int.Parse((string)((Button)sender).Tag));
    private void Back_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        MoveTo(_draft.Step - 1);
    }

    private async void Next_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_draft.Step < 3) { MoveTo(_draft.Step + 1); return; }
        var deferral = args.GetDeferral();
        _saving = true;
        Layout.IsHitTestVisible = false;
        IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = false;
        PrimaryButtonText = L("Saving", "Saving…");
        try
        {
            await _initialization;
            CaptureDraft();
            if (!SaveDraft()) return;
            if (_main.Api is not { } api)
                throw new InvalidOperationException(L("BackendUnavailable", "The download engine isn't connected yet. Wait a moment and try again."));
            if (string.IsNullOrWhiteSpace(_draft.DownloadPath))
                throw new InvalidOperationException(L("FolderRequired", "Choose a download folder on the Downloads step."));
            if (_main.CanUseLocalFiles)
            {
                if (!Path.IsPathFullyQualified(_draft.DownloadPath))
                    throw new InvalidOperationException(L("FolderAbsolute", "Use a full folder path, such as C:\\Downloads."));
                Directory.CreateDirectory(_draft.DownloadPath);
                // Verify write access without leaving a file behind.
                using var probe = new FileStream(Path.Combine(_draft.DownloadPath, $".winbittorrent-{Guid.NewGuid():N}.tmp"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            var requested = new JsonObject { ["save_path"] = _draft.DownloadPath };
            await api.Application.SetPreferencesAsync(requested);
            var actual = await api.Application.GetPreferencesAsync();
            if (PreferenceVerifier.FindMismatchedKeys(requested, actual).Count > 0)
                throw new InvalidOperationException(L("FolderNotApplied", "The download engine could not apply this folder. Choose another folder and try again."));
            if (_draft.Startup is { } startup && startup != _initialStartup)
                await WindowsStartupService.SetEnabledAsync(startup);
            OnboardingPreferences.Complete(_draft);
            Completed = true;
            args.Cancel = false;
        }
        catch (Exception exception) { ShowError(exception.Message); }
        finally
        {
            _saving = false;
            Layout.IsHitTestVisible = true;
            IsPrimaryButtonEnabled = true;
            RenderStep(resetScroll: false);
            deferral.Complete();
        }
    }

    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_saving) { args.Cancel = true; return; }
        _closed = true;
        if (!Completed) SaveDraft();
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) DownloadPathBox.Text = folder.Path;
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async void DefaultApps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!WindowsStartupService.IsPackaged) App.RegisterActivation();
            if (!await Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps")))
                ShowError(L("DefaultAppsFailed", "Open Windows Settings → Apps → Default apps to choose WinBitTorrent."));
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void ShowError(string message)
    {
        if (_closed) return;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed && ErrorBar.IsOpen) ErrorBar.StartBringIntoView();
        });
    }
}
