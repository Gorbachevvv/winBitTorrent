using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Storage;
using Windows.System;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Services;

namespace WinBitTorrent.ViewModels;

public sealed partial class CatalogViewModel : ObservableObject
{
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".ts"];

    private static readonly (CatalogSection Section, string Key, string Fallback)[] HomeSections =
    [
        (CatalogSection.TrendingToday, "Catalog_Section_Trending", "Trending today"),
        (CatalogSection.PopularMovies, "Catalog_Section_PopularMovies", "Popular movies"),
        (CatalogSection.PopularTvShows, "Catalog_Section_PopularTv", "Popular TV shows"),
        (CatalogSection.TopRatedMovies, "Catalog_Section_TopRatedMovies", "Top rated movies"),
        (CatalogSection.TopRatedTvShows, "Catalog_Section_TopRatedTv", "Top rated TV shows"),
        (CatalogSection.NowPlayingMovies, "Catalog_Section_NowPlaying", "Now in theaters"),
        (CatalogSection.UpcomingMovies, "Catalog_Section_Upcoming", "Coming soon"),
        (CatalogSection.TvShowsOnTheAir, "Catalog_Section_OnTheAir", "TV shows on the air")
    ];

    private readonly MainViewModel _main;
    private readonly ICatalogProvider _catalog;
    private readonly TrackerSearchViewModel _trackerSearch;
    private CancellationTokenSource? _loadLifetime;
    private bool _initialLoadStarted;

    public CatalogViewModel(MainViewModel main, ICatalogProvider catalog, TrackerSearchViewModel trackerSearch)
    {
        _main = main;
        _catalog = catalog;
        _trackerSearch = trackerSearch;
        _trackerSearch.BackToCatalogRequested += OnBackToCatalogRequested;
    }

    public ObservableCollection<CatalogSectionViewModel> Sections { get; } = [];
    public ObservableCollection<CatalogCardViewModel> SearchResults { get; } = [];
    public ObservableCollection<CatalogCardViewModel> SimilarItems { get; } = [];
    public ObservableCollection<CatalogSeasonOption> Seasons { get; } = [];

    [ObservableProperty]
    private bool _isSearchActive;

    [ObservableProperty]
    private bool _isDetailsVisible;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isConfigured;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private CatalogItemDetails? _selectedDetails;

    [ObservableProperty]
    private string _selectedMetaText = string.Empty;

    [ObservableProperty]
    private string _selectedGenresText = string.Empty;

    [ObservableProperty]
    private string _selectedCastText = string.Empty;

    [ObservableProperty]
    private string _selectedTagline = string.Empty;

    [ObservableProperty]
    private string _selectedCrewText = string.Empty;

    [ObservableProperty]
    private bool _hasSeasons;

    [ObservableProperty]
    private CatalogSeasonOption? _selectedSeason;

    [ObservableProperty]
    private bool _canWatch;

    [ObservableProperty]
    private string _watchUnavailableReason = string.Empty;

    [RelayCommand]
    public void BackToSources() => _trackerSearch.BackToTrackers();

    public async Task EnsureLoadedAsync()
    {
        if (_initialLoadStarted)
            return;

        _initialLoadStarted = true;
        await LoadHomeAsync();
    }

    [RelayCommand]
    public async Task LoadHomeAsync()
    {
        if (!SyncApiKey())
            return;

        _loadLifetime?.Cancel();
        _loadLifetime?.Dispose();
        _loadLifetime = new CancellationTokenSource();
        var token = _loadLifetime.Token;
        IsSearchActive = false;
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var tasks = HomeSections.Select(entry => _catalog.GetSectionAsync(entry.Section, 1, token)).ToArray();
            var results = await Task.WhenAll(tasks).ConfigureAwait(true);

            Sections.Clear();
            for (var i = 0; i < HomeSections.Length; i++)
            {
                var section = new CatalogSectionViewModel(Localizer.Get(HomeSections[i].Key, HomeSections[i].Fallback));
                foreach (var item in results[i])
                    section.Items.Add(new CatalogCardViewModel(item));
                if (section.Items.Count > 0)
                    Sections.Add(section);
            }
        }
        catch (OperationCanceledException)
        {
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

    [RelayCommand]
    public async Task SearchAsync()
    {
        if (!SyncApiKey())
            return;

        if (string.IsNullOrWhiteSpace(Query))
        {
            await LoadHomeAsync();
            return;
        }

        _loadLifetime?.Cancel();
        _loadLifetime?.Dispose();
        _loadLifetime = new CancellationTokenSource();
        var token = _loadLifetime.Token;
        IsSearchActive = true;
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var items = await _catalog.SearchAsync(Query.Trim(), token);
            SearchResults.Clear();
            foreach (var item in items)
                SearchResults.Add(new CatalogCardViewModel(item));
        }
        catch (OperationCanceledException)
        {
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

    [RelayCommand]
    public async Task OpenDetailsAsync(CatalogCardViewModel? card)
    {
        if (card is null || IsBusy)
            return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var details = await _catalog.GetDetailsAsync(card.Id, card.Kind);
            SelectedDetails = details;
            SelectedMetaText = BuildMetaText(details);
            SelectedGenresText = string.Join(" · ", details.Genres);
            SelectedCastText = string.Join(", ", details.Cast);
            SelectedTagline = details.Tagline ?? string.Empty;
            SelectedCrewText = BuildCrewText(details);
            IsDetailsVisible = true;

            Seasons.Clear();
            foreach (var season in details.Seasons)
                Seasons.Add(new CatalogSeasonOption(season.SeasonNumber, BuildSeasonLabel(season)));
            HasSeasons = Seasons.Count > 0;
            SelectedSeason = Seasons.FirstOrDefault(static season => season.SeasonNumber == 1) ?? Seasons.FirstOrDefault();

            SimilarItems.Clear();
            try
            {
                var similar = await _catalog.GetSimilarAsync(details.Id, details.Kind);
                foreach (var item in similar)
                    SimilarItems.Add(new CatalogCardViewModel(item));
            }
            catch
            {
                // Non-fatal: keep the details page usable even if recommendations fail to load.
            }

            await RefreshWatchAvailabilityAsync();
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

    [RelayCommand]
    public void BackToBrowse()
    {
        IsDetailsVisible = false;
        SelectedDetails = null;
        Seasons.Clear();
        HasSeasons = false;
        SelectedSeason = null;
    }

    [RelayCommand]
    public async Task DownloadAsync()
    {
        if (SelectedDetails is null)
            return;

        await _trackerSearch.SearchForCatalogTitleAsync(SelectedDetails.Title, SelectedDetails.Year, SelectedSeason?.SeasonNumber);
    }

    [RelayCommand]
    public async Task WatchAsync()
    {
        if (SelectedDetails is null || !CanWatch)
            return;

        var path = await ResolveLocalVideoFileAsync(SelectedDetails);
        if (path is null)
            return;

        var file = await StorageFile.GetFileFromPathAsync(path);
        await Launcher.LaunchFileAsync(file);
    }

    private async void OnBackToCatalogRequested()
    {
        IsDetailsVisible = SelectedDetails is not null;
        if (SelectedDetails is not null)
            await RefreshWatchAvailabilityAsync();
    }

    private async Task RefreshWatchAvailabilityAsync()
    {
        if (SelectedDetails is null)
        {
            CanWatch = false;
            return;
        }

        var path = await ResolveLocalVideoFileAsync(SelectedDetails);
        CanWatch = path is not null;
        WatchUnavailableReason = CanWatch
            ? string.Empty
            : Localizer.Get("Catalog_WatchUnavailable", "Download the title first to watch it.");
    }

    private async Task<string?> ResolveLocalVideoFileAsync(CatalogItemDetails details)
    {
        if (_main.Api is null)
            return null;

        try
        {
            var torrents = await _main.Api.Torrents.GetInfoAsync();
            var match = torrents.FirstOrDefault(torrent => MatchesTitle(torrent.Name, details));
            if (match is null)
                return null;

            var files = await _main.Api.Torrents.GetFilesAsync(match.Hash);
            var video = files
                .Where(file => file.Progress >= 0.999 && VideoExtensions.Contains(Path.GetExtension(file.Name), StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(static file => file.Size)
                .FirstOrDefault();
            if (video is null)
                return null;

            var fullPath = Path.Combine(match.SavePath, video.Name.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool MatchesTitle(string torrentName, CatalogItemDetails details)
    {
        var normalizedTorrent = Normalize(torrentName);
        if (normalizedTorrent.Length == 0)
            return false;

        foreach (var candidate in new[] { details.Title, details.OriginalTitle })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var normalizedCandidate = Normalize(candidate);
            if (normalizedCandidate.Length == 0 || !normalizedTorrent.Contains(normalizedCandidate, StringComparison.Ordinal))
                continue;

            if (details.Year is null || normalizedTorrent.Contains(details.Year.Value.ToString(), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string BuildMetaText(CatalogItemDetails details)
    {
        var parts = new List<string>();
        if (details.Year is { } year)
            parts.Add(year.ToString());
        if (details.Rating is > 0)
            parts.Add($"★ {details.Rating.Value:0.0}");
        if (details.Runtime is { } runtime && runtime.TotalMinutes > 0)
            parts.Add(Localizer.Get("Catalog_RuntimeMinutes", "{0} min").Replace("{0}", ((int)runtime.TotalMinutes).ToString()));
        if (details.SeasonCount is { } seasons && seasons > 0)
            parts.Add(Localizer.Get("Catalog_SeasonCount", "{0} seasons").Replace("{0}", seasons.ToString()));
        if (details.Countries.Count > 0)
            parts.Add(string.Join(", ", details.Countries));
        return string.Join(" · ", parts);
    }

    private static string BuildCrewText(CatalogItemDetails details)
    {
        if (details.Directors.Count == 0)
            return string.Empty;

        var label = details.Kind == CatalogKind.Movie
            ? Localizer.Get("Catalog_DirectorLabel", "Director")
            : Localizer.Get("Catalog_CreatorsLabel", "Creators");
        return $"{label}: {string.Join(", ", details.Directors)}";
    }

    private static string BuildSeasonLabel(CatalogSeason season)
    {
        var name = string.IsNullOrWhiteSpace(season.Name)
            ? string.Format(Localizer.Get("Catalog_SeasonLabel", "Season {0}"), season.SeasonNumber)
            : season.Name;
        return season.EpisodeCount > 0
            ? $"{name} · {string.Format(Localizer.Get("Catalog_EpisodeCountShort", "{0} ep."), season.EpisodeCount)}"
            : name;
    }

    private static string Normalize(string value)
        => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private bool SyncApiKey()
    {
        _catalog.ApiKey = ClientSettings.Get<string>("catalog.tmdb.apiKey");
        IsConfigured = _catalog.IsConfigured;
        if (!IsConfigured)
            ErrorMessage = Localizer.Get("Catalog_NotConfigured", "Set a TMDB API key in Settings > Catalog to load the movie/TV catalog.");
        return IsConfigured;
    }
}

public sealed record CatalogSeasonOption(int SeasonNumber, string DisplayName);

public sealed class CatalogSectionViewModel(string title)
{
    public string Title { get; } = title;
    public ObservableCollection<CatalogCardViewModel> Items { get; } = [];
}

public sealed record CatalogCardViewModel(string Id, CatalogKind Kind, string Title, string? Year, string? PosterUrl, string RatingText)
{
    public CatalogCardViewModel(CatalogItem item) : this(
        item.Id,
        item.Kind,
        item.Title,
        item.Year?.ToString(),
        item.PosterUrl,
        item.Rating is > 0 ? item.Rating.Value.ToString("0.0") : string.Empty)
    {
    }
}
