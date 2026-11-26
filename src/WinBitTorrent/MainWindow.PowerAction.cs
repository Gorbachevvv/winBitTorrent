using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinBitTorrent.Services;

namespace WinBitTorrent;

public sealed partial class MainWindow
{
    // Long enough to notice and cancel, short enough not to keep an unattended PC running
    private const int PowerActionCountdownSeconds = 60;

    private void PowerAction_Click(object sender, RoutedEventArgs e)
    {
        var action = PowerActionSleepItem.IsChecked ? PowerAction.Sleep
            : PowerActionShutdownItem.IsChecked ? PowerAction.Shutdown
            : PowerAction.None;
        var hash = PowerTargetSelectedItem.IsChecked ? ViewModel.SelectedTorrent?.Hash : null;
        if (PowerTargetSelectedItem.IsChecked && hash is null)
        {
            // The selection is gone: fall back to every download rather than to a torrent nobody picked
            PowerTargetAllItem.IsChecked = true;
        }
        ViewModel.ArmPowerAction(action, hash);
    }

    private async void OnPowerActionDue(PowerAction action)
    {
        // The action is one-shot, the menu goes back to "Do nothing" whatever the user decides next
        PowerActionNoneItem.IsChecked = true;
        PowerTargetAllItem.IsChecked = true;

        ShowMainWindow();
        if (!await ConfirmPowerActionAsync(action))
            return;

        if (action == PowerAction.Sleep)
        {
            if (!PowerActions.Sleep())
                ViewModel.ErrorMessage = Localizer.Get("PowerAction_SleepFailed", "Windows did not put the computer to sleep.");
            return;
        }

        // The engine saves its resume data first, so the shutdown does not cost any downloaded pieces
        await ExitApplicationAsync(() =>
        {
            try { PowerActions.Shutdown(); }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        });
    }

    private async Task<bool> ConfirmPowerActionAsync(PowerAction action)
    {
        var format = action == PowerAction.Sleep
            ? Localizer.Get("PowerAction_SleepCountdown", "Downloads are complete. The computer will go to sleep in {0} s.")
            : Localizer.Get("PowerAction_ShutdownCountdown", "Downloads are complete. The computer will shut down in {0} s.");
        var remaining = PowerActionCountdownSeconds;
        var text = new TextBlock { Text = string.Format(format, remaining), TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = Localizer.Get("PowerAction_Title", "When downloads complete"),
            Content = text,
            PrimaryButtonText = Localizer.Get("PowerAction_Now", "Now"),
            CloseButtonText = Localizer.Get("CommonCancel.Content", "Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        var timedOut = false;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) =>
        {
            if (--remaining > 0)
            {
                text.Text = string.Format(format, remaining);
                return;
            }
            timer.Stop();
            timedOut = true;
            dialog.Hide();
        };

        try
        {
            timer.Start();
            return await dialog.ShowAsync() == ContentDialogResult.Primary || timedOut;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Another dialog is already open: without a way to cancel, the computer is left as it is
            return false;
        }
        finally
        {
            timer.Stop();
        }
    }
}
