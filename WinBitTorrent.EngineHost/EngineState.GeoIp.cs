using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MaxMind.Db;
using WinBitTorrent.Core.EngineProtocol;

namespace WinBitTorrent.EngineHost;

internal sealed partial class EngineState
{
    private Reader? _geoIpReader;

    private void InitializeGeoIp()
    {
        var candidates = new[]
        {
            Path.Combine(_dataRoot, "GeoDB"),
            Path.Combine(ResolveBackendRoot(), "GeoDB")
        };
        foreach (var directory in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.mmdb", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    _geoIpReader = new Reader(path);
                    return;
                }
                catch (Exception exception) when (exception is IOException or InvalidDatabaseException)
                {
                    _geoIpReader?.Dispose();
                    _geoIpReader = null;
                }
            }
        }
    }

    private async Task<JsonElement> GetTorrentPeersWithGeoIpAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var result = _native.Invoke(EngineRpcMethods.SyncTorrentPeers, payload);
        if (_geoIpReader is null) return result;
        var preferences = await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
        if (preferences.TryGetProperty("resolve_peer_countries", out var enabled) && !enabled.GetBoolean()) return result;

        var root = JsonNode.Parse(result.GetRawText())?.AsObject();
        if (root?["peers"] is not JsonObject peers) return result;
        foreach (var (_, value) in peers)
        {
            if (value is not JsonObject peer || !IPAddress.TryParse(peer["ip"]?.GetValue<string>(), out var address)) continue;
            try
            {
                var record = _geoIpReader.Find<Dictionary<string, object>>(address);
                if (record is null || !TryMap(record.GetValueOrDefault("country"), out var country)) continue;
                peer["country_code"] = Text(country.GetValueOrDefault("iso_code")).ToUpperInvariant();
                if (TryMap(country.GetValueOrDefault("names"), out var names))
                    peer["country"] = Text(names.GetValueOrDefault("en"));
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidDatabaseException or ArgumentException) { }
        }
        return EngineJson.Element(root);
    }

    private static bool TryMap(object? value, out IReadOnlyDictionary<string, object> map)
    {
        if (value is IReadOnlyDictionary<string, object> readOnly)
        {
            map = readOnly;
            return true;
        }
        if (value is IDictionary<string, object> dictionary)
        {
            map = new Dictionary<string, object>(dictionary, StringComparer.OrdinalIgnoreCase);
            return true;
        }
        map = new Dictionary<string, object>();
        return false;
    }

    private static string Text(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
}
