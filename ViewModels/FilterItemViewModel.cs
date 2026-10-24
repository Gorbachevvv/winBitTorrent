using CommunityToolkit.Mvvm.ComponentModel;

namespace WinBitTorrent.ViewModels;

public enum TorrentFilterKind
{
    Status,
    Category,
    Tag,
    Tracker
}

public sealed partial class FilterItemViewModel : ObservableObject
{
    public const string AllKey = "\0";
    public FilterItemViewModel(TorrentFilterKind kind, string key, string title, string glyph, int count = 0)
    {
        Kind = kind;
        Key = key;
        Title = title;
        Glyph = glyph;
        Count = count;
    }

    public TorrentFilterKind Kind { get; }
    public string Key { get; }
    public string Title { get; }
    public string Glyph { get; }
    public bool IsAll => Key == AllKey || (Kind == TorrentFilterKind.Status && Key == "all");

    [ObservableProperty]
    private int _count;

    // List rows fall back to ToString() for their automation name, so without this a screen reader
    // announces the bare type name instead of the filter.
    public override string ToString() => Title;
}
