using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinBitTorrent.EngineHost;

internal static class EngineJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static JsonElement Element<T>(T value)
        => JsonSerializer.SerializeToElement(value, Options);

    public static JsonElement EmptyObject => JsonSerializer.SerializeToElement(new JsonObject(), Options);
}
