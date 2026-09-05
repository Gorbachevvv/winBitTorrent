using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinBitTorrent.Services;

namespace WinBitTorrent;

public sealed partial class MainWindow
{
    private bool _startupPending;
    private bool _setupOpen;
    private Task _backendInitialization = Task.CompletedTask;
    private readonly Queue<(AppActivationArguments Args, bool Initial)> _pendingActivations = new();

    public void StartApplication(AppActivationArguments activation)
    {
        _backendInitialization = ViewModel.InitializeAsync();
        _startupPending = !OnboardingPreferences.IsComplete;
        HandleActivation(activation, isInitialLaunch: true);
        if (_startupPending)
        {
            if (RootGrid.XamlRoot is not null) _ = ShowFirstRunAsync();
            else RootGrid.Loaded += FirstRunRoot_Loaded;
        }
        else ScheduleStartupUpdateCheck();
    }

    private async void FirstRunRoot_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= FirstRunRoot_Loaded;
        await ShowFirstRunAsync();
    }

    private async void FirstRunSetup_Click(object sender, RoutedEventArgs e) => await ShowFirstRunAsync();

    private async Task ShowFirstRunAsync()
    {
        if (_setupOpen || _closing) return;
        _setupOpen = true;
        try
        {
            var dialog = new FirstRunDialog(ViewModel, _windowHandle, theme =>
                WindowUtilities.PreviewTheme(this, theme), _backendInitialization) { XamlRoot = RootGrid.XamlRoot };
            await dialog.ShowAsync();
        }
        catch (Exception exception) { ViewModel.ErrorMessage = exception.Message; }
        finally
        {
            _setupOpen = _startupPending = false;
            WindowUtilities.EndThemePreview(this);
        }
        if (_closing) return;
        while (_pendingActivations.TryDequeue(out var pending)) HandleActivation(pending.Args, pending.Initial);
        if (OnboardingPreferences.IsComplete) ScheduleStartupUpdateCheck();
    }
}
