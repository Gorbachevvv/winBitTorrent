using System.Globalization;

namespace WinBitTorrent.Core.Services;

public static class ValueFormatter
{
    private static readonly string[] SizeUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    public static string Size(long bytes)
    {
        if (bytes < 0)
            return "—";

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return $"{value.ToString(format, CultureInfo.CurrentCulture)} {SizeUnits[unit]}";
    }

    public static string Speed(long bytesPerSecond) => bytesPerSecond < 0 ? "—" : $"{Size(bytesPerSecond)}/s";

    public static string Duration(long seconds)
    {
        if (seconds < 0 || seconds >= 8_640_000)
            return "∞";

        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours:00}h";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        return $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    public static string Percentage(double ratio) => $"{Math.Clamp(ratio, 0, 1) * 100:0.0}%";

    public static string Ratio(double ratio) => double.IsFinite(ratio) ? ratio.ToString("0.00", CultureInfo.CurrentCulture) : "∞";

    public static DateTimeOffset? UnixDate(long seconds) => seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
