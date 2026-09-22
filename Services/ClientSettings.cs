using System.Text.Json;
using System.Text.Json.Serialization;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Infrastructure.Storage;

namespace WinBitTorrent.Services;

public sealed class ClientSettingsDocument
{
    public UiClientSettings Ui { get; set; } = new();
    public NotificationClientSettings Notifications { get; set; } = new();
    public CatalogClientSettings Catalog { get; set; } = new();
    public OnboardingClientSettings Onboarding { get; set; } = new();
    public WorkspaceClientSettings Workspace { get; set; } = new();
    public LayoutClientSettings Layout { get; set; } = new();
    public TorrentClientSettings Torrents { get; set; } = new();
    public Dictionary<string, TrackerClientSettings> Trackers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public WindowClientSettings Window { get; set; } = new();
    public UpdateClientSettings Updates { get; set; } = new();
}

public sealed class UiClientSettings
{
    public string Language { get; set; } = string.Empty;
    public string Theme { get; set; } = "Default";
    public bool ConfirmDelete { get; set; } = true;
}

public sealed class NotificationClientSettings
{
    public bool Enabled { get; set; } = true;
    public bool TorrentAdded { get; set; }
}

public sealed class CatalogClientSettings
{
    public string? TmdbApiKey { get; set; }
    public List<CatalogFavorite> Favorites { get; set; } = [];
}

/// <summary>A single bookmarked catalog title, persisted in client settings.</summary>
public sealed record CatalogFavorite(
    string Id,
    CatalogKind Kind,
    string Title,
    string? Year,
    string? PosterUrl,
    string RatingText);

public sealed class OnboardingClientSettings
{
    public bool Completed { get; set; }
    public OnboardingDraft? Draft { get; set; }
}

public sealed class WorkspaceClientSettings
{
    public List<string> HiddenTabs { get; set; } = [];
    public string? SelectedTab { get; set; }
}

public sealed class LayoutClientSettings
{
    public double? SidebarWidth { get; set; }
    public bool SidebarCollapsed { get; set; }
    public double? DetailsHeight { get; set; }
    public List<TorrentColumnLayout> TorrentColumns { get; set; } = [];
}

public sealed record TorrentColumnLayout(string Header, double Width, bool Visible);

public sealed class TorrentClientSettings
{
    public Dictionary<string, string> SourceFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> VisibleMenuItems { get; set; } = [];
    public List<string> HiddenMenuItems { get; set; } = [];
}

public sealed class TrackerClientSettings
{
    public bool UseBuiltInProxy { get; set; }
}

public sealed class WindowClientSettings
{
    public MainWindowClientSettings Main { get; set; } = new();
}

public sealed class MainWindowClientSettings
{
    public bool Maximized { get; set; }
    public double? WidthDip { get; set; }
    public double? HeightDip { get; set; }
}

public sealed class UpdateClientSettings
{
    public bool CheckOnStartup { get; set; } = true;
}

public static partial class ClientSettings
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(AppPaths.Root, "client-settings.json");
    private static ClientSettingsDocument? _values;
    private static string? _loadedFilePath;
    private static string? _lastSavedJson;

    public static ClientSettingsDocument Current
    {
        get
        {
            lock (Gate)
            {
                var filePath = FilePath;
                if (_values is null || !string.Equals(_loadedFilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    (_values, _lastSavedJson) = Load(filePath);
                    _loadedFilePath = filePath;
                }
                return _values;
            }
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            Save(Current);
        }
    }

    private static (ClientSettingsDocument Settings, string Json) Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                return (JsonSerializer.Deserialize(json, ClientSettingsJsonContext.Default.ClientSettingsDocument) ?? new ClientSettingsDocument(), json);
            }
        }
        catch (JsonException)
        {
        }

        var settings = new ClientSettingsDocument();
        return (settings, JsonSerializer.Serialize(settings, ClientSettingsJsonContext.Default.ClientSettingsDocument));
    }

    private static void Save(ClientSettingsDocument values)
    {
        var filePath = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temporary = filePath + ".tmp";
        var json = JsonSerializer.Serialize(values, ClientSettingsJsonContext.Default.ClientSettingsDocument);
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, filePath, true);
            _loadedFilePath = filePath;
            _lastSavedJson = json;
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
            _values = _lastSavedJson is null
                ? new ClientSettingsDocument()
                : JsonSerializer.Deserialize(_lastSavedJson, ClientSettingsJsonContext.Default.ClientSettingsDocument) ?? new ClientSettingsDocument();
            _loadedFilePath = filePath;
            throw;
        }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
    [JsonSerializable(typeof(ClientSettingsDocument))]
    private sealed partial class ClientSettingsJsonContext : JsonSerializerContext;
}
