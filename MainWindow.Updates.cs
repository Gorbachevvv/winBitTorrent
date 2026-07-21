using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Services;

namespace WinBitTorrent;

public sealed partial class MainWindow
{
    private bool _updateCheckStarted;

    /// <summary>
    /// Silent check on startup: if a newer release exists, prompt the user once.
    /// Any failure (offline, no releases, API error) is swallowed - it never nags.
    /// </summary>
    public void ScheduleStartupUpdateCheck()
    {
        if (_updateCheckStarted)
            return;
        _updateCheckStarted = true;

        _ = Task.Run(async () =>
        {
            // Give the app a moment to finish connecting before touching the network.
            await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            var release = await SafeGetLatestReleaseAsync().ConfigureAwait(false);
            if (release is null || release.Version <= AppVersion.Current)
                return;

            DispatcherQueue.TryEnqueue(async void () =>
            {
                try { await ShowUpdateDialogAsync(release); }
                catch { /* dialog races on shutdown are non-fatal */ }
            });
        });
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        var release = await SafeGetLatestReleaseAsync();
        if (release is not null && release.Version > AppVersion.Current)
        {
            await ShowUpdateDialogAsync(release);
            return;
        }

        await ShowSimpleDialogAsync(
            Localizer.Get("Update_UpToDateTitle", "You're up to date"),
            release is null
                ? Localizer.Get("Update_CheckFailed", "Could not check for updates right now. Please try again later.")
                : string.Format(Localizer.Get("Update_UpToDateMessage", "WinBitTorrent {0} is the latest version."), AppVersion.Display));
    }

    private static async Task<UpdateRelease?> SafeGetLatestReleaseAsync()
    {
        try
        {
            return await App.Services.GetRequiredService<IUpdateService>().GetLatestReleaseAsync();
        }
        catch
        {
            return null;
        }
    }

    private async Task ShowUpdateDialogAsync(UpdateRelease release)
    {
        var body = new StackPanel { Spacing = 12, Width = 420 };
        body.Children.Add(new TextBlock
        {
            Text = string.Format(
                Localizer.Get("Update_AvailableHeadline", "WinBitTorrent {0} is available."),
                release.Version),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text = string.Format(
                Localizer.Get("Update_CurrentVersionLine", "You have version {0}."),
                AppVersion.Display),
            Opacity = 0.7
        });

        if (!string.IsNullOrWhiteSpace(release.ReleaseNotes))
        {
            body.Children.Add(new TextBlock
            {
                Text = Localizer.Get("Update_WhatsNew", "What's new"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 0)
            });
            body.Children.Add(new ScrollViewer
            {
                MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = release.ReleaseNotes,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = Localizer.Get("Update_DialogTitle", "Update available"),
            Content = body,
            PrimaryButtonText = Localizer.Get("Update_UpdateNow", "Update now"),
            CloseButtonText = Localizer.Get("Update_Later", "Later"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await StartUpdateAsync(release);
    }

    private async Task StartUpdateAsync(UpdateRelease release)
    {
        // Portable builds (no Inno uninstaller next to the exe) and releases without an
        // installer asset can't self-update in place - send the user to the download page.
        if (release.InstallerUrl is null || !IsInstalledBuild())
        {
            await Launcher.LaunchUriAsync(release.ReleasePageUrl);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var progressBar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Width = 380 };
        var progressText = new TextBlock { Text = Localizer.Get("Update_Downloading", "Downloading update…"), Opacity = 0.8 };
        var progressDialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = Localizer.Get("Update_DialogTitle", "Update available"),
            Content = new StackPanel { Spacing = 12, Children = { progressText, progressBar } },
            CloseButtonText = Localizer.Get("Common_Cancel", "Cancel")
        };
        progressDialog.CloseButtonClick += (_, _) => cancellation.Cancel();

        var progress = new Progress<double>(value =>
        {
            progressBar.Value = value;
            progressText.Text = string.Format(
                Localizer.Get("Update_DownloadingPercent", "Downloading update… {0:P0}"), value);
        });

        var updateService = App.Services.GetRequiredService<IUpdateService>();
        var downloadTask = updateService.DownloadInstallerAsync(release, progress, cancellation.Token);
        _ = progressDialog.ShowAsync();

        string installerPath;
        try
        {
            installerPath = await downloadTask;
        }
        catch (OperationCanceledException)
        {
            progressDialog.Hide();
            return;
        }
        catch (Exception exception)
        {
            progressDialog.Hide();
            await ShowSimpleDialogAsync(
                Localizer.Get("Update_FailedTitle", "Update failed"),
                $"{Localizer.Get("Update_DownloadFailed", "The update could not be downloaded.")}\n\n{exception.Message}");
            return;
        }

        progressDialog.Hide();

        // Launch the installer silently; it closes/replaces the running app and relaunches it.
        var startInfo = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /FORCECLOSEAPPLICATIONS /RELAUNCH"
        };
        try
        {
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            await ShowSimpleDialogAsync(
                Localizer.Get("Update_FailedTitle", "Update failed"),
                $"{Localizer.Get("Update_LaunchFailed", "The installer could not be started.")}\n\n{exception.Message}");
            return;
        }

        // Exit so the backend stops and the installer can replace the files.
        await ExitApplicationAsync();
    }

    private async Task ShowSimpleDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = Localizer.Get("Common_Close", "Close")
        };
        await dialog.ShowAsync();
    }

    private static bool IsInstalledBuild()
        => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));
}
