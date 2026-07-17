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

    [ObservableProperty]
    private int _count;
}
