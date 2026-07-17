using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinBitTorrent.Services;

namespace WinBitTorrent;

internal static class WindowUtilities
{
    private const int GwlpHwndParent = -8;
    private const int CaptionButtonReservedWidth = 138;
    private const int TitleBarHeight = 36;

    public static AppWindow ConfigureOwned(this Window window, int width, int height)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        var appWindow = AppWindow.GetFromWindowId(id);
        ApplyAppChrome(window, appWindow);
        appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

        var owner = App.Services.GetService(typeof(MainWindow)) as MainWindow;
        if (owner is not null && !ReferenceEquals(owner, window))
        {
            var ownerHandle = WinRT.Interop.WindowNative.GetWindowHandle(owner);
            SetWindowLongPtr(handle, GwlpHwndParent, ownerHandle);
        }

        return appWindow;
    }

    private static void ApplyAppChrome(Window window, AppWindow appWindow)
    {
        window.ExtendsContentIntoTitleBar = true;
        window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        appWindow.SetIcon(AppIconPath());

        var titleBar = appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        if (window.Content is not FrameworkElement originalContent)
            return;

        var theme = CurrentTheme();
        originalContent.HorizontalAlignment = HorizontalAlignment.Stretch;
        originalContent.VerticalAlignment = VerticalAlignment.Stretch;
        originalContent.RequestedTheme = theme;

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

    private static ElementTheme CurrentTheme()
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
}
