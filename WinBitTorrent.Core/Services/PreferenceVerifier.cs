using System.Globalization;
using System.Text.Json.Nodes;

namespace WinBitTorrent.Core.Services;

public static class PreferenceVerifier
{
    public static IReadOnlyList<string> FindMismatchedKeys(JsonObject requested, JsonObject actual)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(actual);

        return requested
            .Where(pair => !ValuesEqual(pair.Key, pair.Value, actual[pair.Key]))
            .Select(static pair => pair.Key)
            .ToArray();
    }

    private static bool ValuesEqual(string key, JsonNode? requested, JsonNode? actual)
    {
        if (requested is null || actual is null)
            return requested is null && actual is null;

        if (requested is JsonValue requestedValue && actual is JsonValue actualValue)
        {
            if (requestedValue.TryGetValue<bool>(out var requestedBoolean)
                && actualValue.TryGetValue<bool>(out var actualBoolean))
                return requestedBoolean == actualBoolean;

            if (TryNumber(requestedValue, out var requestedNumber)
                && TryNumber(actualValue, out var actualNumber))
                return requestedNumber == actualNumber;

            if (requestedValue.TryGetValue<string>(out var requestedText)
                && actualValue.TryGetValue<string>(out var actualText))
            {
                if (key.Equals("banned_IPs", StringComparison.Ordinal))
                    return NormalizeIpList(requestedText) == NormalizeIpList(actualText);
                if (key is "save_path" or "temp_path")
                    return PathsEqual(requestedText, actualText);
                return string.Equals(requestedText, actualText, StringComparison.Ordinal);
            }
        }

        return requested.ToJsonString() == actual.ToJsonString();
    }

    private static bool TryNumber(JsonValue value, out decimal number)
    {
        if (value.TryGetValue<decimal>(out number))
            return true;
        if (value.TryGetValue<double>(out var floatingPoint)
            && decimal.TryParse(floatingPoint.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return true;
        number = default;
        return false;
    }

    private static string NormalizeIpList(string value)
        => string.Join('\n', value
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool PathsEqual(string left, string right)
    {
        var normalizedLeft = NormalizePath(left);
        var normalizedRight = NormalizePath(right);
        var comparison = LooksLikeWindowsPath(normalizedLeft) || LooksLikeWindowsPath(normalizedRight)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(normalizedLeft, normalizedRight, comparison);
    }

    private static string NormalizePath(string value)
    {
        var path = value.Trim().Replace('\\', '/');
        while (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal)
            && !(path.Length == 3 && path[1] == ':'))
            path = path[..^1];
        return path;
    }

    private static bool LooksLikeWindowsPath(string path)
        => path.StartsWith("//", StringComparison.Ordinal)
            || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');
}
