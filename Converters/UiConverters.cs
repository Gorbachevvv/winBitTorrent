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

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Collapsed;
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string text && !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

// Torrent comments almost always carry a link back to the original release; when the whole
// comment IS one, it is shown as a clickable link instead of plain text. A converter (rather than
// code-behind) is used because HyperlinkButton.NavigateUri needs a real Uri, which XAML bindings
// cannot coerce a string into on their own.
public static class CommentLink
{
    public static bool TryGetHttpUri(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var candidate))
            return false;
        if (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps)
            return false;
        uri = candidate;
        return true;
    }
}

public sealed class CommentLinkUriConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
        => value is string text && CommentLink.TryGetHttpUri(text, out var uri) ? uri : null;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed class CommentLinkVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string text && CommentLink.TryGetHttpUri(text, out _) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

public sealed class CommentPlainTextVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string text && CommentLink.TryGetHttpUri(text, out _) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
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
