using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Globalization;
using WinBitTorrent.Infrastructure;
using WinBitTorrent.Services;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent;

public partial class App : Application
{
    private Window? _window;
    private AppInstance? _mainInstance;

    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        UnhandledException += (_, args) => WriteCrash(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrash(args.ExceptionObject as Exception);
        if (HasPackageIdentity())
            ApplicationLanguages.PrimaryLanguageOverride = ClientSettings.GetValue("ui.language") as string ?? string.Empty;
        InitializeComponent();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddWinBitTorrentInfrastructure();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<RssViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<TrackerSearchViewModel>();
        services.AddSingleton<LogViewModel>();
        services.AddSingleton<MainWindow>();
        Services = services.BuildServiceProvider();
    }

    private static void WriteCrash(Exception? exception)
    {
        if (exception is null)
            return;
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "WinBitTorrent-crash.log"), exception.ToString());
        }
        catch
        {
        }
    }

    internal static bool HasPackageIdentity()
    {
        try { return Windows.ApplicationModel.Package.Current.Id is not null; }
        catch (InvalidOperationException) { return false; }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var current = AppInstance.GetCurrent();
        _mainInstance = AppInstance.FindOrRegisterForKey("WinBitTorrent.Main");
        if (!_mainInstance.IsCurrent)
        {
            _ = RedirectActivationAndExitAsync(_mainInstance, current.GetActivatedEventArgs());
            return;
        }

        _mainInstance.Activated += OnActivated;
        _window = Services.GetRequiredService<MainWindow>();
        _window.Activate();
        if (_window is MainWindow mainWindow)
        {
            mainWindow.HandleActivation(current.GetActivatedEventArgs());
            _ = mainWindow.ViewModel.InitializeAsync();
        }
    }

    private void OnActivated(object? sender, AppActivationArguments args)
    {
        var dispatcher = _window?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        dispatcher.TryEnqueue(() =>
        {
            if (_window is MainWindow window)
            {
                window.ShowMainWindow();
                window.HandleActivation(args);
            }
        });
    }

    private static async Task RedirectActivationAndExitAsync(AppInstance target, AppActivationArguments args)
    {
        await target.RedirectActivationToAsync(args);
        Environment.Exit(0);
    }
}
