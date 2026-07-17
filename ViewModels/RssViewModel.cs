using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinBitTorrent.Core.Abstractions;

namespace WinBitTorrent.ViewModels;

public sealed partial class RssViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public RssViewModel(MainViewModel main) => _main = main;

    public ObservableCollection<RssFeedViewModel> Feeds { get; } = [];
    public ObservableCollection<RssArticleViewModel> Articles { get; } = [];

    [ObservableProperty]
    private RssFeedViewModel? _selectedFeed;

    [ObservableProperty]
    private RssArticleViewModel? _selectedArticle;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    partial void OnSelectedFeedChanged(RssFeedViewModel? value)
    {
        Articles.Clear();
        if (value is null)
            return;
        foreach (var article in value.Articles)
            Articles.Add(article);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_main.Api is null)
            return;
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var root = await _main.Api.Rss.GetItemsAsync(withData: true);
            Feeds.Clear();
            ParseFeeds(root, string.Empty, Feeds);
            SelectedFeed ??= Feeds.FirstOrDefault();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AddFeedAsync(string url, string? path = null)
    {
        if (_main.Api is null)
            return;
        await _main.Api.Rss.PostAsync("addFeed", new Dictionary<string, string?>
        {
            ["url"] = url,
            ["path"] = path ?? string.Empty
        });
        await RefreshAsync();
    }

    public async Task AddFolderAsync(string path)
    {
        if (_main.Api is null)
            return;
        await _main.Api.Rss.PostAsync("addFolder", new Dictionary<string, string?> { ["path"] = path });
        await RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshSelectedAsync()
    {
        if (_main.Api is null || SelectedFeed is null)
            return;
        await _main.Api.Rss.PostAsync("refreshItem", new Dictionary<string, string?> { ["itemPath"] = SelectedFeed.Path });
        await RefreshAsync();
    }

    [RelayCommand]
    public async Task RemoveSelectedAsync()
    {
        if (_main.Api is null || SelectedFeed is null)
            return;
        await _main.Api.Rss.PostAsync("removeItem", new Dictionary<string, string?> { ["path"] = SelectedFeed.Path });
        SelectedFeed = null;
        await RefreshAsync();
    }

    private static void ParseFeeds(JsonObject container, string parentPath, ObservableCollection<RssFeedViewModel> destination)
    {
        foreach (var (name, node) in container)
        {
            if (node is not JsonObject item)
                continue;
            var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}\\{name}";
            var isFolder = item["isFolder"]?.GetValue<bool>() == true;
            if (isFolder)
            {
                ParseFeeds(item, path, destination);
                continue;
            }

            var articles = new List<RssArticleViewModel>();
            if (item["articles"] is JsonArray articleArray)
            {
                foreach (var articleNode in articleArray.OfType<JsonObject>())
                    articles.Add(RssArticleViewModel.FromJson(articleNode));
            }

            destination.Add(new RssFeedViewModel(
                path,
                item["title"]?.GetValue<string>() ?? name,
                item["url"]?.GetValue<string>() ?? string.Empty,
                articles));
        }
    }
}

public sealed record RssFeedViewModel(string Path, string Title, string Url, IReadOnlyList<RssArticleViewModel> Articles);

public sealed record RssArticleViewModel(string Id, string Title, string Link, string Description, DateTimeOffset? Published)
{
    public string PublishedText => Published?.LocalDateTime.ToString("g") ?? string.Empty;

    public static RssArticleViewModel FromJson(JsonObject value)
    {
        DateTimeOffset? published = null;
        if (value["date"]?.GetValue<long?>() is > 0 and var date)
            published = DateTimeOffset.FromUnixTimeSeconds(date);
        return new RssArticleViewModel(
            value["id"]?.ToString() ?? Guid.NewGuid().ToString("N"),
            value["title"]?.GetValue<string>() ?? "(untitled)",
            value["link"]?.GetValue<string>() ?? string.Empty,
            value["description"]?.GetValue<string>() ?? string.Empty,
            published);
    }
}
