using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinBitTorrent.Services;
using Windows.Graphics;

namespace WinBitTorrent;

internal static class WindowUtilities
{
    private static readonly Dictionary<Window, Action<ElementTheme>> ThemeWindows = [];
    private static readonly List<(Window Owner, ElementTheme Theme)> ThemePreviews = [];

    private static ElementTheme EffectiveTheme => ThemePreviews.Count > 0 ? ThemePreviews[^1].Theme : CurrentTheme();

    internal static void RegisterThemeWindow(Window window, AppWindow appWindow, FrameworkElement root)
    {
        void Apply(ElementTheme theme)
        {
            root.RequestedTheme = theme;
            ApplyTitleBarTheme(appWindow, theme);
        }
        ThemeWindows.Add(window, Apply);
        Apply(EffectiveTheme);
        window.Closed += (_, _) =>
        {
            ThemeWindows.Remove(window);
            EndThemePreview(window);
        };
    }

    internal static void PreviewTheme(Window owner, ElementTheme theme)
    {
        ThemePreviews.RemoveAll(preview => ReferenceEquals(preview.Owner, owner));
        ThemePreviews.Add((owner, theme));
        RefreshThemes();
    }

    internal static void EndThemePreview(Window owner)
    {
        ThemePreviews.RemoveAll(preview => ReferenceEquals(preview.Owner, owner));
        RefreshThemes();
    }

    private static void RefreshThemes()
    {
        var theme = EffectiveTheme;
        foreach (var apply in ThemeWindows.Values) apply(theme);
    }

    private const int GwlpHwndParent = -8;
    private const int CaptionButtonReservedWidth = 138;
    private const int TitleBarHeight = 36;
    private const int MainWindowMinimumWidth = 820;
    private const int MainWindowMinimumHeight = 560;

    public static AppWindow ConfigureOwned(
        this Window window,
        int width,
        int height,
        int minimumWidth,
        int minimumHeight)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        var appWindow = AppWindow.GetFromWindowId(id);
        ApplyAppChrome(window, appWindow);

        var owner = App.Services.GetService(typeof(MainWindow)) as MainWindow;
        AppWindow? ownerWindow = null;
        if (owner is not null && !ReferenceEquals(owner, window))
        {
            var ownerHandle = WinRT.Interop.WindowNative.GetWindowHandle(owner);
            SetWindowLongPtr(handle, GwlpHwndParent, ownerHandle);
            var ownerId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(ownerHandle);
            ownerWindow = AppWindow.GetFromWindowId(ownerId);
        }

        SetMinimumSize(appWindow, handle, minimumWidth, minimumHeight);
        ResizeAndCenter(appWindow, handle, width, height, ownerWindow);

        return appWindow;
    }

    /// <summary>
    /// Restores the main window in device-independent units. AppWindow uses physical pixels,
    /// which made every configured size appear 25-50% smaller on a scaled display.
    /// </summary>
    public static void RestoreMainWindow(AppWindow appWindow, nint handle)
    {
        var width = (int)Math.Round(ClientSettings.Get("window.main.widthDip", 1240d));
        var height = (int)Math.Round(ClientSettings.Get("window.main.heightDip", 800d));
        SetMinimumSize(appWindow, handle, MainWindowMinimumWidth, MainWindowMinimumHeight);
        ResizeAndCenter(
            appWindow,
            handle,
            Math.Clamp(width, MainWindowMinimumWidth, 2400),
            Math.Clamp(height, MainWindowMinimumHeight, 1600));

        if (appWindow.Presenter is OverlappedPresenter presenter
            && ClientSettings.Get("window.main.maximized", true))
        {
            presenter.Maximize();
        }
    }

    public static void SaveMainWindowPlacement(nint handle)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(handle, ref placement))
            return;

        const int showMinimized = 2;
        const int showMaximized = 3;
        const int restoreToMaximized = 2;
        var maximized = placement.ShowCommand == showMaximized
            || (placement.ShowCommand == showMinimized && (placement.Flags & restoreToMaximized) != 0);
        ClientSettings.SetValue("window.main.maximized", maximized);

        // rcNormalPosition is maintained by Windows even while the window is maximized. Reading
        // AppWindow.Size here lost the user's restored size whenever the app was closed from a
        // maximized state, leaving only window.main.maximized in the settings file.
        var normalWidth = placement.NormalPosition.Right - placement.NormalPosition.Left;
        var normalHeight = placement.NormalPosition.Bottom - placement.NormalPosition.Top;
        if (normalWidth <= 0 || normalHeight <= 0)
            return;
        var scale = GetScale(handle);
        ClientSettings.SetValue("window.main.widthDip", Math.Round(normalWidth / scale));
        ClientSettings.SetValue("window.main.heightDip", Math.Round(normalHeight / scale));
    }

    private static void ResizeAndCenter(
        AppWindow appWindow,
        nint handle,
        int widthDip,
        int heightDip,
        AppWindow? ownerWindow = null)
    {
        var scale = GetScale(handle);
        var requestedWidth = (int)Math.Round(widthDip * scale);
        var requestedHeight = (int)Math.Round(heightDip * scale);

        var display = ownerWindow is not null
            ? DisplayArea.GetFromWindowId(ownerWindow.Id, DisplayAreaFallback.Primary)
            : DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;
        var edgeInset = Math.Max(16, (int)Math.Round(32 * scale));
        var width = Math.Min(requestedWidth, Math.Max(320, workArea.Width - (edgeInset * 2)));
        var height = Math.Min(requestedHeight, Math.Max(240, workArea.Height - (edgeInset * 2)));

        var centerX = ownerWindow is null
            ? workArea.X + ((workArea.Width - width) / 2)
            : ownerWindow.Position.X + ((ownerWindow.Size.Width - width) / 2);
        var centerY = ownerWindow is null
            ? workArea.Y + ((workArea.Height - height) / 2)
            : ownerWindow.Position.Y + ((ownerWindow.Size.Height - height) / 2);
        var x = Math.Clamp(centerX, workArea.X + edgeInset, workArea.X + workArea.Width - width - edgeInset);
        var y = Math.Clamp(centerY, workArea.Y + edgeInset, workArea.Y + workArea.Height - height - edgeInset);

        appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private static void SetMinimumSize(
        AppWindow appWindow,
        nint handle,
        int minimumWidthDip,
        int minimumHeightDip)
    {
        if (appWindow.Presenter is not OverlappedPresenter presenter)
            return;

        var scale = GetScale(handle);
        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var edgeInset = Math.Max(16, (int)Math.Round(32 * scale));
        presenter.PreferredMinimumWidth = Math.Min(
            (int)Math.Round(minimumWidthDip * scale),
            Math.Max(320, display.WorkArea.Width - (edgeInset * 2)));
        presenter.PreferredMinimumHeight = Math.Min(
            (int)Math.Round(minimumHeightDip * scale),
            Math.Max(240, display.WorkArea.Height - (edgeInset * 2)));
    }

    private static double GetScale(nint handle)
        => Math.Max(1d, GetDpiForWindow(handle) / 96d);

    private static void ApplyAppChrome(Window window, AppWindow appWindow)
    {
        window.ExtendsContentIntoTitleBar = true;
        window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        appWindow.SetIcon(AppIconPath());

        var theme = CurrentTheme();
        ApplyTitleBarTheme(appWindow, theme);

        if (window.Content is not FrameworkElement originalContent)
            return;

        originalContent.HorizontalAlignment = HorizontalAlignment.Stretch;
        originalContent.VerticalAlignment = VerticalAlignment.Stretch;
        // Inherit live changes from the chrome root instead of pinning the old theme.
        originalContent.RequestedTheme = ElementTheme.Default;

        var chromeRoot = new Grid
        {
            RequestedTheme = theme,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        chromeRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleBarHeight) });
        chromeRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBarContent = CreateTitleBar(window.Title);
        Grid.SetRow(titleBarContent, 0);
        chromeRoot.Children.Add(titleBarContent);

        Grid.SetRow(originalContent, 1);
        chromeRoot.Children.Add(originalContent);
        window.Content = chromeRoot;
        window.SetTitleBar(titleBarContent);
        RegisterThemeWindow(window, appWindow, chromeRoot);
    }

    private static Grid CreateTitleBar(string title)
    {
        var titleBar = new Grid
        {
            Height = TitleBarHeight,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CaptionButtonReservedWidth) });

        var icon = new Image
        {
            Width = 18,
            Height = 18,
            Margin = new Thickness(12, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Source = new BitmapImage(new Uri("ms-appx:///Assets/Square44x44Logo.targetsize-24_altform-unplated.png"))
        };
        titleBar.Children.Add(icon);

        var text = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(text, 1);
        titleBar.Children.Add(text);

        return titleBar;
    }

    /// <summary>
    /// Colours the system-drawn caption buttons (minimise, maximise, close). Those are painted by
    /// the shell, not by XAML, so the app theme picked in the settings never reaches them on its
    /// own - with a light theme over a dark app mode their glyphs stayed white and invisible.
    /// </summary>
    internal static void ApplyTitleBarTheme(AppWindow appWindow, ElementTheme theme)
    {
        var titleBar = appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode
        };
    }

    internal static ElementTheme CurrentTheme()
        => (ClientSettings.GetValue("ui.theme") as string) switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

    internal static string AppIconPath()
        => Path.Combine(AppContext.BaseDirectory, "Assets", "WinBitTorrent.ico");

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint windowHandle, ref WindowPlacement placement);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinimumPosition;
        public NativePoint MaximumPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
