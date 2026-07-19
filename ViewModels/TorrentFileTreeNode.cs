using System.Collections.ObjectModel;
using System.ComponentModel;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Core.Services;
using WinBitTorrent.Services;

namespace WinBitTorrent.ViewModels;

public sealed class TorrentFileTreeNode : INotifyPropertyChanged
{
    private static readonly int[] PriorityValues = [0, 1, 6, 7];
    private TorrentFileTreeNode? _parent;
    private bool _isExpanded;

    private TorrentFileTreeNode(string name, string fullPath, TorrentFile? file)
    {
        Name = name;
        FullPath = fullPath;
        File = file;
    }

    public string Name { get; }
    public string FullPath { get; }
    public TorrentFile? File { get; }
    public bool IsFolder => File is null;
    public string IconGlyph => IsFolder ? "\uE8B7" : "\uE7C3";
    public Windows.UI.Text.FontWeight FontWeight => IsFolder
        ? Microsoft.UI.Text.FontWeights.SemiBold
        : Microsoft.UI.Text.FontWeights.Normal;
    public ObservableCollection<TorrentFileTreeNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    public long Size => File?.Size ?? Children.Sum(static child => child.Size);
    public string SizeText => ValueFormatter.Size(Size);
    public long Remaining => File is not null
        ? checked((long)Math.Round(File.Size * (1d - Math.Clamp(File.Progress, 0d, 1d))))
        : Children.Sum(static child => child.Remaining);
    public string RemainingText => ValueFormatter.Size(Remaining);
    public double ProgressValue => Size <= 0
        ? 0d
        : Math.Clamp((Size - Remaining) * 100d / Size, 0d, 100d);
    public string ProgressText => $"{ProgressValue:0.0}%";
    public double Availability => File?.Availability
        ?? (Size <= 0
            ? 0d
            : Children.Sum(child => Math.Max(0d, child.Availability) * child.Size) / Size);
    public string AvailabilityText => Availability < 0d ? "—" : Availability.ToString("0.00");

    public int PriorityIndex
    {
        get
        {
            if (File is not null)
                return Array.IndexOf(PriorityValues, File.Priority) is var index and >= 0 ? index : 1;
            if (Children.Count == 0)
                return 1;

            var priority = Children[0].PriorityIndex;
            return priority >= 0 && Children.Skip(1).All(child => child.PriorityIndex == priority)
                ? priority
                : -1;
        }
    }

    public bool? IsChecked
    {
        get
        {
            if (File is not null)
                return File.Priority > 0;
            if (Children.Count == 0)
                return false;

            var checkedCount = Children.Count(static child => child.IsChecked == true);
            if (checkedCount == Children.Count)
                return true;
            return checkedCount == 0 && Children.All(static child => child.IsChecked == false)
                ? false
                : null;
        }
    }

    public string MixedPriorityText => Localizer.Get("Files_PriorityMixed", "Mixed");

    public IReadOnlyList<TorrentFile> DescendantFiles()
        => File is not null
            ? [File]
            : Children.SelectMany(static child => child.DescendantFiles()).ToList();

    public void SetPriority(int priority)
    {
        if (File is not null)
            File.Priority = priority;
        else
            foreach (var child in Children)
                child.SetPriority(priority);

        RaiseFileStateChanged();
    }

    public void SetSelection(bool selected)
    {
        if (File is not null)
        {
            if (!selected)
                File.Priority = 0;
            else if (File.Priority == 0)
                File.Priority = 1;
        }
        else
        {
            foreach (var child in Children)
                child.SetSelection(selected);
        }

        RaiseFileStateChanged();
    }

    public void RefreshDisplayedValues()
    {
        foreach (var child in Children)
            child.RefreshDisplayedValues();
        RaiseFileStateChanged(propagateToParent: false);
    }

    internal static TorrentFileTreeNode Folder(string name, string fullPath)
        => new(name, fullPath, null);

    internal static TorrentFileTreeNode FileNode(string name, string fullPath, TorrentFile file)
        => new(name, fullPath, file);

    internal void AddChild(TorrentFileTreeNode child)
    {
        child._parent = this;
        Children.Add(child);
    }

    private void RaiseFileStateChanged(bool propagateToParent = true)
    {
        OnPropertyChanged(nameof(PriorityIndex));
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(Availability));
        OnPropertyChanged(nameof(AvailabilityText));
        if (propagateToParent)
            _parent?.RaiseFileStateChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public static class TorrentFileTreeBuilder
{
    public static IReadOnlyList<TorrentFileTreeNode> Build(
        string? torrentName,
        IEnumerable<TorrentFile> files,
        string? filter = null)
    {
        var normalizedName = string.IsNullOrWhiteSpace(torrentName) ? null : torrentName.Trim();
        var normalizedFilter = filter?.Trim();
        var source = files
            .Where(file => string.IsNullOrWhiteSpace(normalizedFilter)
                || file.Name.Contains(normalizedFilter, StringComparison.CurrentCultureIgnoreCase)
                || (normalizedName?.Contains(normalizedFilter, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .Select(file => (File: file, Parts: SplitPath(file.Name)))
            .ToList();
        if (source.Count == 0)
            return [];

        var addTorrentRoot = source.Count > 1
            && normalizedName is not null
            && source.All(item => item.Parts.Length == 0
                || !item.Parts[0].Equals(normalizedName, StringComparison.CurrentCultureIgnoreCase));
        var roots = new List<TorrentFileTreeNode>();
        TorrentFileTreeNode? torrentRoot = null;
        if (addTorrentRoot)
        {
            torrentRoot = TorrentFileTreeNode.Folder(normalizedName!, normalizedName!);
            torrentRoot.IsExpanded = true;
            roots.Add(torrentRoot);
        }

        foreach (var (file, rawParts) in source)
        {
            var parts = rawParts.Length == 0 ? [file.Name] : rawParts;
            IList<TorrentFileTreeNode> siblings = torrentRoot is null ? roots : torrentRoot.Children;
            TorrentFileTreeNode? parent = torrentRoot;
            var currentPath = torrentRoot?.FullPath ?? string.Empty;

            for (var index = 0; index < parts.Length - 1; index++)
            {
                var part = parts[index];
                currentPath = CombinePath(currentPath, part);
                var folder = siblings.FirstOrDefault(node => node.IsFolder
                    && node.Name.Equals(part, StringComparison.CurrentCultureIgnoreCase));
                if (folder is null)
                {
                    folder = TorrentFileTreeNode.Folder(part, currentPath);
                    folder.IsExpanded = parent is null || parent == torrentRoot;
                    if (parent is null)
                        roots.Add(folder);
                    else
                        parent.AddChild(folder);
                }

                parent = folder;
                siblings = folder.Children;
            }

            var fileName = parts[^1];
            var fileNode = TorrentFileTreeNode.FileNode(fileName, CombinePath(currentPath, fileName), file);
            if (parent is null)
                roots.Add(fileNode);
            else
                parent.AddChild(fileNode);
        }

        Sort(roots);
        return roots;
    }

    private static void Sort(IList<TorrentFileTreeNode> nodes)
    {
        foreach (var node in nodes)
            Sort(node.Children);
        var ordered = nodes
            .OrderByDescending(static node => node.IsFolder)
            .ThenBy(static node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        nodes.Clear();
        foreach (var node in ordered)
            nodes.Add(node);
    }

    private static string[] SplitPath(string path)
        => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string CombinePath(string parent, string name)
        => string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";
}
