using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinBitTorrent.Core.Services;
using WinBitTorrent.Services;

namespace WinBitTorrent.ViewModels;

public sealed partial class SearchViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _searchLifetime;

    public SearchViewModel(MainViewModel main) => _main = main;

    public ObservableCollection<SearchResultViewModel> Results { get; } = [];
    public ObservableCollection<SearchPluginViewModel> Plugins { get; } = [];

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private string _category = "all";

    [ObservableProperty]
    private string _selectedPlugin = "all";

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _status = Localizer.Get("Search_Ready", "Ready");

    [ObservableProperty]
    private SearchResultViewModel? _selectedResult;

    [RelayCommand]
    public async Task LoadPluginsAsync()
    {
        if (_main.Api is null)
            return;
        var plugins = await _main.Api.Search.GetPluginsAsync();
        Plugins.Clear();
        foreach (var plugin in plugins.OfType<JsonObject>())
        {
            Plugins.Add(new SearchPluginViewModel(
                plugin["name"]?.GetValue<string>() ?? string.Empty,
                plugin["fullName"]?.GetValue<string>() ?? plugin["name"]?.GetValue<string>() ?? string.Empty,
                plugin["enabled"]?.GetValue<bool>() ?? false,
                plugin["version"]?.GetValue<string>() ?? string.Empty,
                plugin["url"]?.GetValue<string>() ?? string.Empty));
        }
    }

    [RelayCommand]
    public async Task StartAsync()
    {
        if (_main.Api is null || string.IsNullOrWhiteSpace(Query))
            return;

        _searchLifetime?.Cancel();
        _searchLifetime?.Dispose();
        _searchLifetime = new CancellationTokenSource();
        var token = _searchLifetime.Token;

        Results.Clear();
        IsSearching = true;
        Status = Localizer.Get("Search_Starting", "Starting search…");
        try
        {
            var id = await _main.Api.Search.StartAsync(Query.Trim(), Category, SelectedPlugin, token);
            while (!token.IsCancellationRequested)
            {
                var response = await _main.Api.Search.GetResultsAsync(id, 2000, 0, token);
                ApplyResults(response["results"] as JsonArray);
                var statuses = await _main.Api.Search.GetStatusAsync(id, token);
                var state = statuses.OfType<JsonObject>().FirstOrDefault()?["status"]?.GetValue<string>() ?? "Unknown";
                Status = $"{LocalizeSearchState(state)} · {Results.Count} {Localizer.Get("Search_Results", "results")}";
                if (state.Equals("Stopped", StringComparison.OrdinalIgnoreCase))
                    break;
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException)
        {
            Status = Localizer.Get("Search_Stopped", "Stopped");
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    public void Stop()
    {
        _searchLifetime?.Cancel();
        IsSearching = false;
    }

    public async Task DownloadSelectedAsync()
    {
        if (SelectedResult is null)
            return;
        await _main.AddAsync([], [SelectedResult.FileUrl]);
    }

    private static string LocalizeSearchState(string state) => state.ToLowerInvariant() switch
    {
        "running" => Localizer.Get("Search_Running", "Running"),
        "stopped" => Localizer.Get("Search_Stopped", "Stopped"),
        _ => state
    };

    private void ApplyResults(JsonArray? values)
    {
        if (values is null)
            return;
        var existing = Results.ToDictionary(static item => item.FileUrl, StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.OfType<JsonObject>())
        {
            var item = SearchResultViewModel.FromJson(value);
            if (!existing.ContainsKey(item.FileUrl))
                Results.Add(item);
        }
    }
}

public sealed record SearchPluginViewModel(string Name, string FullName, bool Enabled, string Version, string Url);

public sealed record SearchResultViewModel(
    string Name,
    string FileUrl,
    string DescriptionUrl,
    string SiteUrl,
    long SizeValue,
    int Seeds,
    int Leechers,
    string Engine,
    string Category)
{
    public string Size => ValueFormatter.Size(SizeValue);

    public static SearchResultViewModel FromJson(JsonObject value) => new(
        value["fileName"]?.GetValue<string>() ?? string.Empty,
        value["fileUrl"]?.GetValue<string>() ?? string.Empty,
        value["descrLink"]?.GetValue<string>() ?? string.Empty,
        value["siteUrl"]?.GetValue<string>() ?? string.Empty,
        value["fileSize"]?.GetValue<long>() ?? -1,
        value["nbSeeders"]?.GetValue<int>() ?? -1,
        value["nbLeechers"]?.GetValue<int>() ?? -1,
        value["siteUrl"]?.GetValue<string>() ?? string.Empty,
        value["filePubDate"]?.GetValue<string>() ?? string.Empty);
}
