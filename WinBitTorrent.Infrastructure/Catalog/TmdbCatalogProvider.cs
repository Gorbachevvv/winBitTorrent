using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Infrastructure.Net;

namespace WinBitTorrent.Infrastructure.Catalog;

public sealed class TmdbCatalogProvider : ICatalogProvider, IDisposable
{
    private const string BaseAddress = "https://api.themoviedb.org/3/";
    private const string PosterBase = "https://image.tmdb.org/t/p/w342";
    private const string BackdropBase = "https://image.tmdb.org/t/p/w780";
    private readonly HttpClient _client = CreateClient();

    private static HttpClient CreateClient()
    {
        var baseUri = new Uri(BaseAddress);
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = SystemProxyResolver.Create(baseUri),
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        return new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Id => "tmdb";

    public string? ApiKey { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    private static string Language => CultureInfo.CurrentUICulture.Name switch
    {
        "ru-RU" or "ru" => "ru-RU",
        _ => "en-US"
    };

    public async Task<IReadOnlyList<CatalogItem>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var movies = SearchKindAsync("search/movie", query, CatalogKind.Movie, cancellationToken);
        var shows = SearchKindAsync("search/tv", query, CatalogKind.TvShow, cancellationToken);
        await Task.WhenAll(movies, shows).ConfigureAwait(false);
        return movies.Result.Concat(shows.Result)
            .OrderByDescending(static item => item.Rating ?? 0)
            .ToArray();
    }

    public async Task<IReadOnlyList<CatalogItem>> GetSectionAsync(CatalogSection section, int page = 1, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var (path, kind) = section switch
        {
            CatalogSection.TrendingToday => ("trending/all/day", (CatalogKind?)null),
            CatalogSection.PopularMovies => ("movie/popular", CatalogKind.Movie),
            CatalogSection.TopRatedMovies => ("movie/top_rated", CatalogKind.Movie),
            CatalogSection.NowPlayingMovies => ("movie/now_playing", CatalogKind.Movie),
            CatalogSection.UpcomingMovies => ("movie/upcoming", CatalogKind.Movie),
            CatalogSection.PopularTvShows => ("tv/popular", CatalogKind.TvShow),
            CatalogSection.TopRatedTvShows => ("tv/top_rated", CatalogKind.TvShow),
            CatalogSection.TvShowsOnTheAir => ("tv/on_the_air", CatalogKind.TvShow),
            _ => throw new ArgumentOutOfRangeException(nameof(section))
        };

        var response = await GetAsync<TmdbListResponse>(path, cancellationToken, ("page", page.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
        return (response.Results ?? [])
            .Where(result => kind is not null || !string.Equals(result.MediaType, "person", StringComparison.OrdinalIgnoreCase))
            .Select(result => ToCatalogItem(result, kind ?? ParseMediaKind(result.MediaType)))
            .ToArray();
    }

    public async Task<IReadOnlyList<CatalogItem>> GetSimilarAsync(string id, CatalogKind kind, int page = 1, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var path = kind == CatalogKind.Movie ? $"movie/{id}/recommendations" : $"tv/{id}/recommendations";
        var response = await GetAsync<TmdbListResponse>(path, cancellationToken, ("page", page.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
        return (response.Results ?? []).Select(result => ToCatalogItem(result, kind)).ToArray();
    }

    public async Task<CatalogItemDetails> GetDetailsAsync(string id, CatalogKind kind, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var path = kind == CatalogKind.Movie ? $"movie/{id}" : $"tv/{id}";
        var details = await GetAsync<TmdbDetailsResponse>(path, cancellationToken, ("append_to_response", "credits")).ConfigureAwait(false);

        var runtimeMinutes = kind == CatalogKind.Movie
            ? details.Runtime
            : details.EpisodeRunTime?.FirstOrDefault();

        var directors = kind == CatalogKind.Movie
            ? details.Credits?.Crew?
                .Where(static member => string.Equals(member.Job, "Director", StringComparison.OrdinalIgnoreCase))
                .Select(static member => member.Name ?? string.Empty)
                .Where(static name => name.Length > 0)
                .Distinct()
                .ToArray() ?? []
            : details.CreatedBy?.Select(static creator => creator.Name ?? string.Empty).Where(static name => name.Length > 0).ToArray() ?? [];

        return new CatalogItemDetails(
            Id: id,
            Kind: kind,
            Title: details.Title ?? details.Name ?? string.Empty,
            OriginalTitle: details.OriginalTitle ?? details.OriginalName,
            Year: ParseYear(details.ReleaseDate ?? details.FirstAirDate),
            PosterUrl: ToImageUrl(PosterBase, details.PosterPath),
            BackdropUrl: ToImageUrl(BackdropBase, details.BackdropPath),
            Rating: details.VoteAverage,
            Overview: details.Overview ?? string.Empty,
            Genres: details.Genres?.Select(static genre => genre.Name ?? string.Empty).Where(static name => name.Length > 0).ToArray() ?? [],
            Runtime: runtimeMinutes is > 0 ? TimeSpan.FromMinutes(runtimeMinutes.Value) : null,
            SeasonCount: kind == CatalogKind.TvShow ? details.NumberOfSeasons : null,
            Cast: details.Credits?.Cast?.Take(8).Select(static member => member.Name ?? string.Empty).Where(static name => name.Length > 0).ToArray() ?? [],
            Tagline: string.IsNullOrWhiteSpace(details.Tagline) ? null : details.Tagline,
            Directors: directors,
            Countries: details.ProductionCountries?.Select(static country => country.Name ?? string.Empty).Where(static name => name.Length > 0).ToArray() ?? []);
    }

    private static CatalogKind ParseMediaKind(string? mediaType)
        => string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? CatalogKind.TvShow : CatalogKind.Movie;

    private async Task<IReadOnlyList<CatalogItem>> SearchKindAsync(string path, string query, CatalogKind kind, CancellationToken cancellationToken)
    {
        var response = await GetAsync<TmdbListResponse>(path, cancellationToken, ("query", query)).ConfigureAwait(false);
        return (response.Results ?? []).Select(result => ToCatalogItem(result, kind)).ToArray();
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken, params (string Key, string Value)[] parameters)
    {
        (string Key, string Value)[] baseParameters = [("api_key", ApiKey ?? string.Empty), ("language", Language)];
        var query = string.Join('&', baseParameters
            .Concat(parameters)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        try
        {
            var response = await _client.GetFromJsonAsync<T>($"{path}?{query}", cancellationToken).ConfigureAwait(false);
            return response ?? throw new CatalogException("TMDB returned an empty response.");
        }
        catch (HttpRequestException exception)
        {
            throw new CatalogException($"TMDB request failed: {exception.Message}", exception);
        }
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new CatalogNotConfiguredException("Set a TMDB API key in Settings > Catalog.");
    }

    private static CatalogItem ToCatalogItem(TmdbResult result, CatalogKind kind) => new(
        Id: result.Id.ToString(CultureInfo.InvariantCulture),
        Kind: kind,
        Title: result.Title ?? result.Name ?? string.Empty,
        OriginalTitle: result.OriginalTitle ?? result.OriginalName,
        Year: ParseYear(result.ReleaseDate ?? result.FirstAirDate),
        PosterUrl: ToImageUrl(PosterBase, result.PosterPath),
        Rating: result.VoteAverage);

    private static string? ToImageUrl(string prefix, string? path)
        => string.IsNullOrWhiteSpace(path) ? null : prefix + path;

    private static int? ParseYear(string? date)
        => !string.IsNullOrWhiteSpace(date) && date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), out var year)
            ? year
            : null;

    public void Dispose() => _client.Dispose();

    private sealed class TmdbListResponse
    {
        [JsonPropertyName("results")]
        public List<TmdbResult>? Results { get; set; }
    }

    private sealed class TmdbResult
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("original_title")]
        public string? OriginalTitle { get; set; }

        [JsonPropertyName("original_name")]
        public string? OriginalName { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }

        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }

        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; set; }

        [JsonPropertyName("media_type")]
        public string? MediaType { get; set; }
    }

    private sealed class TmdbDetailsResponse
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("original_title")]
        public string? OriginalTitle { get; set; }

        [JsonPropertyName("original_name")]
        public string? OriginalName { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }

        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }

        [JsonPropertyName("backdrop_path")]
        public string? BackdropPath { get; set; }

        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; set; }

        [JsonPropertyName("overview")]
        public string? Overview { get; set; }

        [JsonPropertyName("genres")]
        public List<TmdbGenre>? Genres { get; set; }

        [JsonPropertyName("runtime")]
        public int? Runtime { get; set; }

        [JsonPropertyName("episode_run_time")]
        public List<int>? EpisodeRunTime { get; set; }

        [JsonPropertyName("number_of_seasons")]
        public int? NumberOfSeasons { get; set; }

        [JsonPropertyName("credits")]
        public TmdbCredits? Credits { get; set; }

        [JsonPropertyName("tagline")]
        public string? Tagline { get; set; }

        [JsonPropertyName("created_by")]
        public List<TmdbCreator>? CreatedBy { get; set; }

        [JsonPropertyName("production_countries")]
        public List<TmdbCountry>? ProductionCountries { get; set; }
    }

    private sealed class TmdbGenre
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class TmdbCountry
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class TmdbCreator
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class TmdbCredits
    {
        [JsonPropertyName("cast")]
        public List<TmdbCastMember>? Cast { get; set; }

        [JsonPropertyName("crew")]
        public List<TmdbCrewMember>? Crew { get; set; }
    }

    private sealed class TmdbCastMember
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class TmdbCrewMember
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("job")]
        public string? Job { get; set; }
    }
}
