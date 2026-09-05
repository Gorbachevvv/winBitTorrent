using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Threading;
using WinBitTorrent.Infrastructure;
using WinBitTorrent.Services;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent;

public partial class App : Application
{
    private Window? _window;
    private AppInstance? _mainInstance;
    private IAppNotificationService? _notifications;
    // Held for the lifetime of the process purely so the installer can see it: Inno Setup's
    // AppMutex check (see build/installer/WinBitTorrent.iss) is what makes a silent in-app
    // update reliably wait for/close the running instance before overwriting its files.
    // Without it, the installer used to race the app's own async shutdown and sometimes hit a
    // sharing violation on a locked file, which made it roll back and abort the update.
    private static Mutex? _appMutex;

    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        UnhandledException += (_, args) =>
        {
            WriteCrash(args.Exception);
            // Menu actions and dialogs run as "async void" handlers; an unexpected error in
            // one of them (a rejected API call, a missing file, etc.) must not take down the
            // whole app. The offending action simply fails - it's already logged above - and
            // the user can retry instead of losing their whole torrent session.
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrash(args.ExceptionObject as Exception);
        ApplyLanguageOverride(ClientSettings.GetValue("ui.language") as string ?? string.Empty);
        InitializeComponent();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddWinBitTorrentInfrastructure();
        services.AddSingleton<IAppNotificationService, AppNotificationService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<RssViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<TrackerSearchViewModel>();
        services.AddSingleton<CatalogViewModel>();
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

    internal static void ApplyLanguageOverride(string language)
    {
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        }
        catch
        {
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Installer hooks: register/unregister the file & protocol associations without
        // starting the UI, so they exist immediately after install (not only after the
        // first manual launch).
        var commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Any(a => a.Equals("--register-associations", StringComparison.OrdinalIgnoreCase)))
        {
            RegisterActivation();
            Environment.Exit(0);
            return;
        }
        if (commandLine.Any(a => a.Equals("--register-torrent-association", StringComparison.OrdinalIgnoreCase)))
        {
            RegisterTorrentActivation();
            Environment.Exit(0);
            return;
        }
        if (commandLine.Any(a => a.Equals("--register-magnet-association", StringComparison.OrdinalIgnoreCase)))
        {
            RegisterMagnetActivation();
            Environment.Exit(0);
            return;
        }
        if (commandLine.Any(a => a.Equals("--unregister-associations", StringComparison.OrdinalIgnoreCase)))
        {
            UnregisterActivation();
            Environment.Exit(0);
            return;
        }

        var current = AppInstance.GetCurrent();
        _mainInstance = AppInstance.FindOrRegisterForKey("WinBitTorrent.Main");
        if (!_mainInstance.IsCurrent)
        {
            _ = RedirectActivationAndExitAsync(_mainInstance, current.GetActivatedEventArgs());
            return;
        }

        _appMutex = new Mutex(initiallyOwned: false, name: "WinBitTorrentAppMutex");

        _mainInstance.Activated += OnActivated;
        _window = Services.GetRequiredService<MainWindow>();
        _window.Closed += OnMainWindowClosed;
        _notifications = Services.GetRequiredService<IAppNotificationService>();
        _notifications.NotificationInvoked += OnNotificationInvoked;
        _notifications.Initialize();
        _window.Activate();
        if (_window is MainWindow mainWindow)
        {
            mainWindow.StartApplication(current.GetActivatedEventArgs());
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

    private void OnNotificationInvoked(object? sender, EventArgs args)
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_window is MainWindow window)
                window.ShowMainWindow();
        });
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        if (_notifications is not null)
        {
            _notifications.NotificationInvoked -= OnNotificationInvoked;
            _notifications.Shutdown();
        }
    }

    private static async Task RedirectActivationAndExitAsync(AppInstance target, AppActivationArguments args)
    {
        await target.RedirectActivationToAsync(args);
        Environment.Exit(0);
    }

    private static readonly string[] AssociatedFileTypes = [".torrent"];
    private const string MagnetScheme = "magnet";

    internal static void RegisterActivation()
    {
        RegisterTorrentActivation();
        RegisterMagnetActivation();
    }

    private static string ActivationIcon =>
        $"{Path.Combine(AppContext.BaseDirectory, "Assets", "WinBitTorrent.ico")},0";

    internal static void RegisterTorrentActivation()
    {
        try
        {
            ActivationRegistrationManager.RegisterForFileTypeActivation(
                AssociatedFileTypes,
                ActivationIcon,
                "Torrent file",
                [],
                string.Empty);
        }
        catch
        {
        }
    }

    internal static void RegisterMagnetActivation()
    {
        try
        {
            ActivationRegistrationManager.RegisterForProtocolActivation(
                MagnetScheme,
                ActivationIcon,
                "Magnet link",
                string.Empty);
        }
        catch
        {
        }
    }

    internal static void UnregisterActivation()
    {
        try
        {
            ActivationRegistrationManager.UnregisterForFileTypeActivation(AssociatedFileTypes, string.Empty);
            ActivationRegistrationManager.UnregisterForProtocolActivation(MagnetScheme, string.Empty);
        }
        catch
        {
        }
    }
}
