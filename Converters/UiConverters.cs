using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace WinBitTorrent.Converters;

public sealed class NonEmptyStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string text && !string.IsNullOrWhiteSpace(text);

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed class ConnectionBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Connected = new(Colors.LimeGreen);
    private static readonly SolidColorBrush Disconnected = new(Colors.DarkGray);

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Connected : Disconnected;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Visible;
}

public sealed class ProgressGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var progress = value switch
        {
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            int intValue => intValue,
            _ => 0d
        };
        progress = Math.Clamp(progress, 0d, 100d);

        if (parameter is string text && text.Equals("remaining", StringComparison.OrdinalIgnoreCase))
            progress = 100d - progress;

        return new GridLength(progress, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
