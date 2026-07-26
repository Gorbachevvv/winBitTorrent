namespace WinBitTorrent.Core.Models;

public enum CatalogKind
{
    Movie,
    TvShow
}

public enum CatalogSection
{
    TrendingToday,
    PopularMovies,
    PopularTvShows,
    TopRatedMovies,
    TopRatedTvShows,
    NowPlayingMovies,
    UpcomingMovies,
    TvShowsOnTheAir
}

public sealed record CatalogItem(
    string Id,
    CatalogKind Kind,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? PosterUrl,
    double? Rating);

// EnglishTitle is the title as released in English, independent of the UI language. Tracker release
// names are overwhelmingly English, so searches are built from it rather than from Title.
public sealed record CatalogItemDetails(
    string Id,
    CatalogKind Kind,
    string Title,
    string? OriginalTitle,
    string? EnglishTitle,
    int? Year,
    string? PosterUrl,
    string? BackdropUrl,
    double? Rating,
    string Overview,
    IReadOnlyList<string> Genres,
    TimeSpan? Runtime,
    int? SeasonCount,
    IReadOnlyList<string> Cast,
    string? Tagline,
    IReadOnlyList<string> Directors,
    IReadOnlyList<string> Countries,
    IReadOnlyList<CatalogSeason> Seasons);

public sealed record CatalogSeason(int SeasonNumber, string Name, int EpisodeCount);
