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

    private static readonly (CatalogSection Section, string Key, string Fallback, CatalogNav Nav)[] HomeSections =
    [
        (CatalogSection.TrendingToday, "Catalog_Section_Trending", "Trending today", CatalogNav.Home),
        (CatalogSection.PopularMovies, "Catalog_Section_PopularMovies", "Popular movies", CatalogNav.Movies),
        (CatalogSection.NowPlayingMovies, "Catalog_Section_NowPlaying", "Now in theaters", CatalogNav.Movies),
        (CatalogSection.UpcomingMovies, "Catalog_Section_Upcoming", "Coming soon", CatalogNav.Movies),
        (CatalogSection.TopRatedMovies, "Catalog_Section_TopRatedMovies", "Top rated movies", CatalogNav.Movies),
        (CatalogSection.PopularTvShows, "Catalog_Section_PopularTv", "Popular TV shows", CatalogNav.Tv),
        (CatalogSection.TvShowsOnTheAir, "Catalog_Section_OnTheAir", "TV shows on the air", CatalogNav.Tv),
        (CatalogSection.TopRatedTvShows, "Catalog_Section_TopRatedTv", "Top rated TV shows", CatalogNav.Tv)
    ];

    private readonly MainViewModel _main;
    private readonly ICatalogProvider _catalog;
    private readonly TrackerSearchViewModel _trackerSearch;
    private readonly List<(CatalogNav Nav, CatalogSectionViewModel Section)> _allSections = [];
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
    public ObservableCollection<CatalogCardViewModel> Favorites { get; } = [];
    public ObservableCollection<CatalogSeasonOption> Seasons { get; } = [];

    // Every tracker registered in the app, so the download flyout stays in sync automatically.
    public ObservableCollection<TrackerCardViewModel> Trackers => _trackerSearch.Trackers;

    // Left-rail navigation (Lampa-style): Home shows every row, Movies/TV filter to their sections,
    // Favorites shows the local bookmarks grid. Search overlays all of them while a query is active.
    public bool ShowSections => !IsSearchActive && !IsFavoritesActive;
    public bool ShowFavorites => IsFavoritesActive && !IsSearchActive;

    [ObservableProperty]
    private CatalogNav _selectedNav = CatalogNav.Home;

    [ObservableProperty]
    private bool _isFavoritesActive;

    [ObservableProperty]
    private bool _isFavoritesEmpty;

    [ObservableProperty]
    private bool _isSelectedFavorite;

    public string FavoriteButtonText => IsSelectedFavorite
        ? Localizer.Get("Catalog_RemoveFavorite", "In favorites")
        : Localizer.Get("Catalog_AddFavorite", "Add to favorites");

    public string FavoriteGlyph => IsSelectedFavorite ? "" : "";

    [ObservableProperty]
    private bool _isSearchActive;

    [ObservableProperty]
    private bool _isDetailsVisible;

    [ObservableProperty]
    private bool _isTrackerPickerOpen;

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

    partial void OnIsSearchActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSections));
        OnPropertyChanged(nameof(ShowFavorites));
    }

    partial void OnIsFavoritesActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSections));
        OnPropertyChanged(nameof(ShowFavorites));
    }

    partial void OnIsSelectedFavoriteChanged(bool value)
    {
        OnPropertyChanged(nameof(FavoriteButtonText));
        OnPropertyChanged(nameof(FavoriteGlyph));
    }

    partial void OnSelectedNavChanged(CatalogNav value)
    {
        IsSearchActive = false;
        ApplyNav();
    }

    private void ApplyNav()
    {
        IsFavoritesActive = SelectedNav == CatalogNav.Favorites;
        if (IsFavoritesActive)
        {
            RefreshFavorites();
            Sections.Clear();
            return;
        }

        Sections.Clear();
        foreach (var (nav, section) in _allSections)
            if (SelectedNav == CatalogNav.Home || nav == SelectedNav)
                Sections.Add(section);
    }

    private void RefreshFavorites()
    {
        Favorites.Clear();
        foreach (var favorite in CatalogFavoritesStore.Load())
            Favorites.Add(new CatalogCardViewModel(favorite.Id, favorite.Kind, favorite.Title, favorite.Year, favorite.PosterUrl, favorite.RatingText));
        IsFavoritesEmpty = IsFavoritesActive && Favorites.Count == 0;
    }

    [RelayCommand]
    public void ToggleSelectedFavorite()
    {
        if (SelectedDetails is null)
            return;

        var favorite = new CatalogFavorite(
            SelectedDetails.Id,
            SelectedDetails.Kind,
            SelectedDetails.Title,
            SelectedDetails.Year?.ToString(),
            SelectedDetails.PosterUrl,
            SelectedDetails.Rating is > 0 ? SelectedDetails.Rating.Value.ToString("0.0") : string.Empty);

        IsSelectedFavorite = CatalogFavoritesStore.Toggle(favorite);
        RefreshFavorites();
    }

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
        if (!SyncProviderConfig())
        {
            // Bookmarks are local, so they stay usable even without a TMDB key.
            if (SelectedNav == CatalogNav.Favorites)
                ApplyNav();
            return;
        }

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

            _allSections.Clear();
            for (var i = 0; i < HomeSections.Length; i++)
            {
                var section = new CatalogSectionViewModel(Localizer.Get(HomeSections[i].Key, HomeSections[i].Fallback));
                foreach (var item in results[i])
                    section.Items.Add(new CatalogCardViewModel(item));
                if (section.Items.Count > 0)
                    _allSections.Add((HomeSections[i].Nav, section));
            }

            ApplyNav();
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
        if (!SyncProviderConfig())
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
            IsSelectedFavorite = CatalogFavoritesStore.Contains(details.Id, details.Kind);
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
        IsTrackerPickerOpen = false;
        IsDetailsVisible = false;
        SelectedDetails = null;
        Seasons.Clear();
        HasSeasons = false;
        SelectedSeason = null;
    }

    // "Download" no longer jumps straight to one tracker: it opens the side panel so the user picks
    // which of the registered trackers should run the search.
    [RelayCommand]
    public void Download()
    {
        if (SelectedDetails is null)
            return;

        IsTrackerPickerOpen = true;
    }

    [RelayCommand]
    public void CloseTrackerPicker() => IsTrackerPickerOpen = false;

    [RelayCommand]
    public async Task PickTrackerAsync(TrackerCardViewModel? tracker)
    {
        if (tracker is null || SelectedDetails is null)
            return;

        IsTrackerPickerOpen = false;
        await _trackerSearch.SearchForCatalogTitleAsync(BuildTrackerQuery(), tracker.Id);
    }

    // Trackers name their releases in English, so the search uses the English title even when the
    // catalog is displayed in another language - "Блич" finds nothing on an English-indexed tracker.
    private CatalogTrackerQuery BuildTrackerQuery()
    {
        var english = SelectedDetails!.EnglishTitle;
        var useEnglish = !string.IsNullOrWhiteSpace(english);
        return new CatalogTrackerQuery(
            useEnglish ? english!.Trim() : SelectedDetails.Title,
            SelectedDetails.Year,
            SelectedSeason?.SeasonNumber,
            useEnglish);
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
        IsTrackerPickerOpen = false;
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

    private bool SyncProviderConfig()
    {
        _catalog.ApiKey = ClientSettings.Get<string>("catalog.tmdb.apiKey");
        _catalog.Language = ResolveTmdbLanguage();
        _catalog.FallbackLanguage = ResolveTmdbFallback();
        _catalog.Region = ResolveTmdbRegion();
        IsConfigured = _catalog.IsConfigured;
        if (!IsConfigured)
            ErrorMessage = Localizer.Get("Catalog_NotConfigured", "Set a TMDB API key in Settings > Catalog to load the movie/TV catalog.");
        return IsConfigured;
    }

    // Titles/posters/overviews follow the app's chosen language; the section relevance follows the
    // system region (so "popular TV shows" isn't full of unrelated foreign shows).
    private static string ResolveTmdbLanguage()
    {
        var setting = ClientSettings.Get<string>("ui.language");
        var culture = string.IsNullOrWhiteSpace(setting)
            ? System.Globalization.CultureInfo.CurrentUICulture.Name
            : setting;
        return culture switch
        {
            "be-BY" or "be" => "be",
            _ => culture
        };
    }

    // Belarusian has little TMDB coverage, so it falls back to Russian; Russian (and anything else)
    // falls back to English. English needs no fallback.
    private static string? ResolveTmdbFallback()
        => ResolveTmdbLanguage() switch
        {
            "be" => "ru-RU",
            "en-US" or "en" => null,
            _ => "en-US"
        };

    private static string? ResolveTmdbRegion()
    {
        try
        {
            return System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
        }
        catch
        {
            return null;
        }
    }
}

public enum CatalogNav
{
    Home,
    Movies,
    Tv,
    Favorites
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

    // The poster is an image with no text of its own, so the list/grid container falls back to
    // ToString() for the automation name a screen reader announces.
    public string AutomationName => string.IsNullOrWhiteSpace(Year) ? Title : $"{Title} ({Year})";

    public override string ToString() => AutomationName;
}
