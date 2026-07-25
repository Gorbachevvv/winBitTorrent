namespace WinBitTorrent.Services;

/// <summary>
/// One configurable top-level entry of the torrent context menu. <see cref="Id"/> matches the
/// <c>Tag</c> of the corresponding element in the TransfersView flyout, which is what lets the
/// saved layout reorder the real menu without duplicating any of its click handlers.
/// </summary>
public sealed record TorrentMenuEntry(string Id, string Uid, string Fallback, string Glyph, string Color, bool HasSubmenu = false)
{
    public string Title => MenuText(Uid, Fallback);

    // Menu labels live in the .resw as x:Uid entries ("Start.Text"), which the resource compiler
    // publishes under a slash-separated path. The dotted name is tried as well so a mismatch
    // degrades to the English fallback instead of an empty label.
    private static string MenuText(string uid, string fallback)
    {
        var value = Localizer.Get($"{uid}/Text", string.Empty);
        return string.IsNullOrEmpty(value) ? Localizer.Get($"{uid}.Text", fallback) : value;
    }
}

/// <summary>
/// Stores which commands the torrent context menu shows and in what order. The layout is a local
/// UI preference, so it lives in <see cref="ClientSettings"/> rather than in qBittorrent.
/// </summary>
public static class TorrentMenuLayout
{
    public const string SeparatorId = "separator";

    private const string VisibleKey = "ui.torrentMenu.visible";
    private const string HiddenKey = "ui.torrentMenu.hidden";
    private const char Delimiter = ',';

    /// <summary>Every command that can be placed in the menu, with the glyphs the menu draws.</summary>
    public static IReadOnlyList<TorrentMenuEntry> Catalog { get; } =
    [
        new("start", "Start", "Start", "", "#16A34A"),
        new("stop", "Stop", "Stop", "", "#DC2626"),
        new("forceStart", "ForceStart", "Force start", "", "#94A3B8"),
        new("setLocation", "SetLocation", "Set location…", "", "#2563EB"),
        new("rename", "RenameTorrent", "Rename…", "", "#2563EB"),
        new("category", "CategoryMenu", "Category", "", "#D97706", true),
        new("tags", "TagsMenu", "Tags", "", "#9333EA", true),
        new("queue", "QueueMenu", "Queue", "", "#64748B", true),
        new("sequential", "SequentialDownload", "Download in sequential order", "", "#94A3B8"),
        new("firstLast", "FirstLastPriority", "Download first and last pieces first", "", "#94A3B8"),
        new("superSeeding", "SuperSeeding", "Super seeding mode", "", "#94A3B8"),
        new("limitRate", "LimitRate", "Limit rate", "", "#0D9488", true),
        new("trackers", "TrackersWebSeeds", "Trackers and web seeds", "", "#2563EB", true),
        new("forceRecheck", "ForceRecheck", "Force recheck", "", "#2563EB"),
        new("reannounce", "Reannounce", "Reannounce", "", "#D97706"),
        new("preview", "PreviewAvailableFile", "Preview first available file", "", "#0D9488"),
        new("openDestination", "OpenDestination", "Open destination folder", "", "#D97706"),
        new("copy", "CopyMenu", "Copy", "", "#64748B", true),
        new("export", "ExportTorrent", "Export .torrent…", "", "#64748B"),
        new("chooseColumns", "ChooseColumns", "Choose columns…", "", "#64748B"),
        new("delete", "Delete", "Delete…", "", "#DC2626")
    ];

    /// <summary>The menu as it ships: the qBittorrent order, separators included.</summary>
    public static IReadOnlyList<string> DefaultVisible { get; } =
    [
        "start", "stop", "forceStart",
        SeparatorId,
        "setLocation", "rename", "category", "tags",
        SeparatorId,
        "queue", "sequential", "firstLast", "superSeeding", "limitRate", "trackers",
        SeparatorId,
        "forceRecheck", "reannounce",
        SeparatorId,
        "preview", "openDestination", "copy", "export", "chooseColumns",
        SeparatorId,
        "delete"
    ];

    public static TorrentMenuEntry? Find(string id) => Catalog.FirstOrDefault(entry => entry.Id == id);

    /// <summary>
    /// Reads the saved menu order. Unknown ids (left over from an older build) are dropped, and
    /// commands the user has never seen - ones added by a newer build - are appended, so a new
    /// feature never stays unreachable behind a stale layout.
    /// </summary>
    public static IReadOnlyList<string> LoadVisible()
    {
        var stored = Read(VisibleKey);
        if (stored.Count == 0)
            return DefaultVisible;

        var hidden = Read(HiddenKey).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in Catalog)
        {
            if (!hidden.Contains(entry.Id) && !stored.Contains(entry.Id))
                stored.Add(entry.Id);
        }
        return stored;
    }

    /// <summary>The commands the user removed from the menu, in the order they were removed.</summary>
    public static IReadOnlyList<string> LoadHidden()
    {
        if (Read(VisibleKey).Count == 0)
            return [];
        var visible = LoadVisible().ToHashSet(StringComparer.Ordinal);
        return Read(HiddenKey).Where(id => id != SeparatorId && !visible.Contains(id)).ToList();
    }

    public static void Save(IEnumerable<string> visible, IEnumerable<string> hidden)
    {
        ClientSettings.SetValue(VisibleKey, string.Join(Delimiter, visible));
        ClientSettings.SetValue(HiddenKey, string.Join(Delimiter, hidden));
    }

    public static void Reset()
    {
        ClientSettings.SetValue(VisibleKey, null);
        ClientSettings.SetValue(HiddenKey, null);
    }

    // Separators may repeat; every other id is kept at most once so a corrupted file cannot
    // produce the same command twice in the menu.
    private static List<string> Read(string key)
    {
        var stored = ClientSettings.Get<string>(key);
        if (string.IsNullOrWhiteSpace(stored))
            return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return stored
            .Split(Delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => id == SeparatorId || (Find(id) is not null && seen.Add(id)))
            .ToList();
    }
}
