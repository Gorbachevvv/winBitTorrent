using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Core.Services;

public static class TorrentIdentity
{
    public static IReadOnlySet<string> FromTorrentFile(string path)
    {
        var data = File.ReadAllBytes(path);
        var offset = 0;
        if (data.Length == 0 || data[offset++] != (byte)'d')
            throw new InvalidDataException("The torrent file does not contain a bencoded dictionary.");

        while (offset < data.Length && data[offset] != (byte)'e')
        {
            var key = ReadString(data, ref offset);
            var valueStart = offset;
            SkipValue(data, ref offset);
            if (!key.SequenceEqual("info"u8))
                continue;

            var info = data.AsSpan(valueStart, offset - valueStart);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Convert.ToHexString(SHA1.HashData(info)),
                Convert.ToHexString(SHA256.HashData(info))
            };
        }

        throw new InvalidDataException("The torrent file does not contain an info dictionary.");
    }

    public static IReadOnlySet<string> FromMagnet(string source)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!source.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            return result;

        foreach (var part in source[(source.IndexOf('?') + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator < 0 || !Uri.UnescapeDataString(part[..separator]).Equals("xt", StringComparison.OrdinalIgnoreCase))
                continue;

            AddNormalized(result, Uri.UnescapeDataString(part[(separator + 1)..]));
        }

        return result;
    }

    public static IReadOnlySet<string> FromMetadata(JsonObject metadata)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddNode(result, metadata["infohash_v1"]);
        AddNode(result, metadata["infohash_v2"]);
        AddNode(result, metadata["hash"]);
        AddNode(result, metadata["info_hash"]);
        if (metadata["info"] is JsonObject info)
        {
            AddNode(result, info["infohash_v1"]);
            AddNode(result, info["infohash_v2"]);
            AddNode(result, info["hash"]);
            AddNode(result, info["info_hash"]);
        }
        return result;
    }

    public static bool Matches(TorrentInfo torrent, IEnumerable<string> hashes)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddNormalized(existing, torrent.Hash);
        AddNormalized(existing, torrent.InfoHashV1);
        AddNormalized(existing, torrent.InfoHashV2);
        return hashes.Any(existing.Contains);
    }

    private static void AddNode(HashSet<string> destination, JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            AddNormalized(destination, text);
    }

    private static void AddNormalized(HashSet<string> destination, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var hash = value.Trim();
        var separator = hash.LastIndexOf(':');
        if (separator >= 0)
            hash = hash[(separator + 1)..];
        if (hash.StartsWith("1220", StringComparison.OrdinalIgnoreCase) && hash.Length == 68)
            hash = hash[4..];

        if (hash.Length == 32 && hash.All(IsBase32Character))
            hash = Convert.ToHexString(DecodeBase32(hash));

        if ((hash.Length == 40 || hash.Length == 64) && hash.All(Uri.IsHexDigit))
            destination.Add(hash.ToUpperInvariant());
    }

    private static bool IsBase32Character(char value)
        => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '2' and <= '7';

    private static byte[] DecodeBase32(string value)
    {
        var output = new byte[value.Length * 5 / 8];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var character in value.ToUpperInvariant())
        {
            var digit = character is >= 'A' and <= 'Z' ? character - 'A' : character - '2' + 26;
            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits < 8)
                continue;
            bits -= 8;
            output[index++] = (byte)(buffer >> bits);
            buffer &= (1 << bits) - 1;
        }
        return output;
    }

    private static ReadOnlySpan<byte> ReadString(byte[] data, ref int offset)
    {
        var length = ReadStringLength(data, ref offset);
        if (length < 0 || offset > data.Length - length)
            throw new InvalidDataException("The torrent file contains an invalid byte string.");
        var value = data.AsSpan(offset, length);
        offset += length;
        return value;
    }

    private static int ReadStringLength(byte[] data, ref int offset)
    {
        if (offset >= data.Length || data[offset] is < (byte)'0' or > (byte)'9')
            throw new InvalidDataException("The torrent file contains an invalid byte string length.");

        var length = 0;
        while (offset < data.Length && data[offset] != (byte)':')
        {
            var digit = data[offset++] - (byte)'0';
            if (digit > 9)
                throw new InvalidDataException("The torrent file contains an invalid byte string length.");
            length = checked((length * 10) + digit);
        }
        if (offset >= data.Length || data[offset++] != (byte)':')
            throw new InvalidDataException("The torrent file contains an unterminated byte string length.");
        return length;
    }

    private static void SkipValue(byte[] data, ref int offset)
    {
        if (offset >= data.Length)
            throw new InvalidDataException("The torrent file ended unexpectedly.");

        switch (data[offset])
        {
            case (byte)'i':
                offset++;
                while (offset < data.Length && data[offset] != (byte)'e')
                    offset++;
                if (offset >= data.Length)
                    throw new InvalidDataException("The torrent file contains an unterminated integer.");
                offset++;
                return;
            case (byte)'l':
                offset++;
                while (offset < data.Length && data[offset] != (byte)'e')
                    SkipValue(data, ref offset);
                EndCollection(data, ref offset);
                return;
            case (byte)'d':
                offset++;
                while (offset < data.Length && data[offset] != (byte)'e')
                {
                    _ = ReadString(data, ref offset);
                    SkipValue(data, ref offset);
                }
                EndCollection(data, ref offset);
                return;
            default:
                _ = ReadString(data, ref offset);
                return;
        }
    }

    private static void EndCollection(byte[] data, ref int offset)
    {
        if (offset >= data.Length || data[offset++] != (byte)'e')
            throw new InvalidDataException("The torrent file contains an unterminated collection.");
    }
}
