namespace WinBitTorrent.Core.Services;

/// <summary>Validates and normalizes the link formats accepted by qBittorrent's add endpoint.</summary>
public static class TorrentLinkParser
{
    public static bool TryParse(
        string text,
        out IReadOnlyList<string> links,
        out int invalidLineNumber)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        invalidLineNumber = 0;

        var lines = text.Split(['\r', '\n']);
        for (var index = 0; index < lines.Length; index++)
        {
            var value = lines[index].Trim();
            if (value.Length == 0)
                continue;

            var normalized = Normalize(value);
            if (normalized is null)
            {
                links = [];
                invalidLineNumber = index + 1;
                return false;
            }

            if (seen.Add(normalized))
                result.Add(normalized);
        }

        links = result;
        return result.Count > 0;
    }

    private static string? Normalize(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase)
                && uri.Query.Contains("xt=", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        if ((value.Length == 40 && value.All(Uri.IsHexDigit))
            || (value.Length == 32 && value.All(IsBase32Character)))
            return $"magnet:?xt=urn:btih:{value}";

        // A raw v2 info-hash is the 32-byte digest. Multihash code 0x12 (sha2-256) and
        // length 0x20 are required by the btmh magnet representation.
        if (value.Length == 64 && value.All(Uri.IsHexDigit))
            return $"magnet:?xt=urn:btmh:1220{value}";

        return null;
    }

    private static bool IsBase32Character(char character)
        => character is >= 'A' and <= 'Z'
            || character is >= 'a' and <= 'z'
            || character is >= '2' and <= '7';
}
