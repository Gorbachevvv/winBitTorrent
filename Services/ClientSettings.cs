using System.Text.Json;
using System.Text.Json.Nodes;
using WinBitTorrent.Infrastructure.Storage;

namespace WinBitTorrent.Services;

public static class ClientSettings
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(AppPaths.Root, "client-settings.json");
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
        => SetValues(new Dictionary<string, object?> { [key] = value });

    public static void SetValues(IReadOnlyDictionary<string, object?> values)
    {
        lock (Gate)
        {
            var next = (JsonObject)Values().DeepClone();
            foreach (var (key, value) in values)
                next[key] = value is null ? null : JsonSerializer.SerializeToNode(value);
            if (JsonNode.DeepEquals(Values(), next) && File.Exists(FilePath))
                return;
            Save(next);
            // Publish only after the atomic replacement succeeds, including completion flags.
            _values = next;
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

    private static void Save(JsonObject values)
    {
        var destination = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(destination))
                        File.Replace(temporary, destination, null);
                    else
                        File.Move(temporary, destination);
                    return;
                }
                catch (Exception exception) when (attempt < 5
                    && IsTemporaryReplacementFailure(exception, destination))
                {
                    // Readers/file watchers can briefly hold the destination without delete
                    // sharing. Retry the atomic replacement; never truncate the live file.
                    Thread.Sleep(20 * (attempt + 1));
                }
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsTemporaryReplacementFailure(Exception exception, string destination)
        => exception is IOException or UnauthorizedAccessException
            && (exception.HResult & 0xffff) is 5 or 32 or 33
            && !Directory.Exists(destination)
            && (!File.Exists(destination) || (File.GetAttributes(destination) & FileAttributes.ReadOnly) == 0);
}
