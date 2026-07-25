using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinBitTorrent.Services;

namespace WinBitTorrent.Controls;

/// <summary>
/// One row of the context menu editor: either a command from <see cref="TorrentMenuLayout.Catalog"/>
/// or a separator. Separators are plain instances rather than a shared singleton because the menu
/// can hold several of them and the reordering list needs to tell them apart.
/// </summary>
public sealed class ContextMenuEditorItem
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = [];

    private ContextMenuEditorItem()
    {
        Id = TorrentMenuLayout.SeparatorId;
        Title = Localizer.Get("MenuEditor_Separator", "Separator");
        Glyph = string.Empty;
        Accent = AccentBrush("#94A3B8");
    }

    public ContextMenuEditorItem(TorrentMenuEntry entry)
    {
        Id = entry.Id;
        Title = entry.Title;
        Glyph = entry.Glyph;
        Accent = AccentBrush(entry.Color);
        HasSubmenu = entry.HasSubmenu;
    }

    public static ContextMenuEditorItem Separator() => new();

    public string Id { get; }
    public string Title { get; }
    public string Glyph { get; }
    public Brush Accent { get; }
    public bool HasSubmenu { get; }
    public bool IsSeparator => Id == TorrentMenuLayout.SeparatorId;

    public Visibility CommandVisibility => IsSeparator ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SeparatorVisibility => IsSeparator ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SubmenuVisibility => HasSubmenu ? Visibility.Visible : Visibility.Collapsed;

    public string DragHint => Localizer.Get("MenuEditor_DragHint", "Drag to move");
    public string RemoveHint => Localizer.Get("MenuEditor_RemoveHint", "Remove from the menu");

    // The menu uses fixed accent colours per command (the same ones the real flyout draws), so the
    // brushes are cached by hex value instead of being rebuilt for every row.
    private static SolidColorBrush AccentBrush(string color)
    {
        if (Brushes.TryGetValue(color, out var cached))
            return cached;
        var value = Convert.ToUInt32(color.TrimStart('#'), 16);
        var brush = new SolidColorBrush(Color.FromArgb(0xFF, (byte)(value >> 16), (byte)(value >> 8), (byte)value));
        Brushes[color] = brush;
        return brush;
    }
}
