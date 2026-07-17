using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinBitTorrent.Services;

public static class ClientSettings
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinBitTorrent",
        "client-settings.json");
    private static JsonObject? _values;

    public static object? GetValue(string key)
    {
        lock (Gate)
        {
            var node = Values()[key];
            if (node is not JsonValue value)
                return null;
            if (value.TryGetValue<bool>(out var boolean)) return boolean;
            if (value.TryGetValue<double>(out var number)) return number;
            if (value.TryGetValue<string>(out var text)) return text;
            return node.ToJsonString();
        }
    }

    public static T? Get<T>(string key, T? fallback = default)
    {
        lock (Gate)
        {
            try { return Values()[key] is { } node ? node.GetValue<T>() : fallback; }
            catch (InvalidOperationException) { return fallback; }
        }
    }

    public static void SetValue(string key, object? value)
    {
        lock (Gate)
        {
            Values()[key] = value is null ? null : JsonSerializer.SerializeToNode(value);
            Save();
        }
    }

    private static JsonObject Values()
    {
        if (_values is not null)
            return _values;
        try
        {
            _values = File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject loaded ? loaded : [];
        }
        catch (JsonException)
        {
            _values = [];
        }
        return _values;
    }

    private static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, Values().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}
