using System.Text.Json;

namespace WinBitTorrent.Services;

/// <summary>
/// Remembers which local .torrent file a torrent was added from, keyed by info hash. qBittorrent
/// has no notion of this itself - once a torrent exists it only keeps its own internal resume-data
/// copy, not a reference to wherever the user originally picked the file from - so the delete
/// dialog cannot offer "also delete the source file" without this app tracking it separately.
/// </summary>
public static class TorrentSourceFileStore
{
    private const string Key = "torrents.sourceFiles";

    public static IReadOnlyDictionary<string, string> Load()
    {
        var json = ClientSettings.Get<string>(Key);
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // JsonSerializer.Deserialize builds a plain Dictionary with the default (ordinal,
            // case-sensitive) comparer - the OrdinalIgnoreCase comparer Record/Forget use is a
            // runtime-only property that never survives the JSON round-trip. Re-wrapping here is
            // what makes a lookup with a differently-cased hash (e.g. qBittorrent's own lowercase
            // report vs. this app's uppercase Convert.ToHexString) actually succeed.
            var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return deserialized is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(deserialized, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Records <paramref name="path"/> under every hash the torrent is known by (v1 and/or
    /// v2), so a later lookup succeeds regardless of which hash the caller has on hand.</summary>
    public static void Record(IEnumerable<string> hashes, string path)
    {
        var map = new Dictionary<string, string>(Load(), StringComparer.OrdinalIgnoreCase);
        foreach (var hash in hashes)
        {
            if (!string.IsNullOrWhiteSpace(hash))
                map[hash] = path;
        }
        ClientSettings.SetValue(Key, JsonSerializer.Serialize(map));
    }

    public static string? Find(IEnumerable<string> hashes)
    {
        var map = Load();
        foreach (var hash in hashes)
        {
            if (!string.IsNullOrWhiteSpace(hash) && map.TryGetValue(hash, out var path))
                return path;
        }
        return null;
    }

    /// <summary>Drops the mapping for a torrent that no longer exists in qBittorrent, whether or
    /// not its source file was actually deleted - a dead hash can never be looked up again.</summary>
    public static void Forget(IEnumerable<string> hashes)
    {
        var map = new Dictionary<string, string>(Load(), StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var hash in hashes)
            changed |= map.Remove(hash);
        if (changed)
            ClientSettings.SetValue(Key, JsonSerializer.Serialize(map));
    }
}
