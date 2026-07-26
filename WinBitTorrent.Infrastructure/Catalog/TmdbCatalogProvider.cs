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

    public string? Language { get; set; }

    public string? FallbackLanguage { get; set; }

    public string? Region { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    private string RequestLanguage => string.IsNullOrWhiteSpace(Language) ? DefaultLanguage : Language!;

    private static string DefaultLanguage => CultureInfo.CurrentUICulture.Name switch
    {
        "ru-RU" or "ru" => "ru-RU",
        _ => "en-US"
    };

    // Ordered 2-letter language codes to try for a title's text: primary, the configured fallback,
    // then English as a last resort.
    private IEnumerable<string> LanguageChain()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[] { RequestLanguage, FallbackLanguage, "en-US" })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            var code = candidate.Split('-')[0].ToLowerInvariant();
            if (seen.Add(code))
                yield return code;
        }
    }

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
        var (path, kind, parameters) = ResolveSection(section, page);
        return await LocalizeListAsync(path, kind, cancellationToken, parameters).ConfigureAwait(false);
    }

    private (string Path, CatalogKind? Kind, (string, string)[] Parameters) ResolveSection(CatalogSection section, int page)
    {
        var pageParam = ("page", page.ToString(CultureInfo.InvariantCulture));
        // "Popular" from /movie/popular and /tv/popular is global, which floods the TV list with
        // regionally irrelevant soaps/talk shows. When we know the user's region, switch to
        // /discover filtered by titles actually available on streaming services in that region.
        var regional = !string.IsNullOrWhiteSpace(Region);
        (string, string)[] discover =
        [
            pageParam,
            ("sort_by", "popularity.desc"),
            ("watch_region", Region ?? string.Empty),
            ("with_watch_monetization_types", "flatrate|free|ads|rent|buy")
        ];

        return section switch
        {
            CatalogSection.TrendingToday => ("trending/all/day", null, [pageParam]),
            CatalogSection.PopularMovies => regional
                ? ("discover/movie", CatalogKind.Movie, discover)
                : ("movie/popular", CatalogKind.Movie, [pageParam]),
            CatalogSection.PopularTvShows => regional
                ? ("discover/tv", CatalogKind.TvShow, discover)
                : ("tv/popular", CatalogKind.TvShow, [pageParam]),
            CatalogSection.TopRatedMovies => ("movie/top_rated", CatalogKind.Movie, [pageParam]),
            CatalogSection.NowPlayingMovies => ("movie/now_playing", CatalogKind.Movie, [pageParam]),
            CatalogSection.UpcomingMovies => ("movie/upcoming", CatalogKind.Movie, [pageParam]),
            CatalogSection.TopRatedTvShows => ("tv/top_rated", CatalogKind.TvShow, [pageParam]),
            CatalogSection.TvShowsOnTheAir => ("tv/on_the_air", CatalogKind.TvShow, [pageParam]),
            _ => throw new ArgumentOutOfRangeException(nameof(section))
        };
    }

    public async Task<IReadOnlyList<CatalogItem>> GetSimilarAsync(string id, CatalogKind kind, int page = 1, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var path = kind == CatalogKind.Movie ? $"movie/{id}/recommendations" : $"tv/{id}/recommendations";
        return await LocalizeListAsync(path, kind, cancellationToken, ("page", page.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
    }

    // Fetches a TMDB list in the primary language and, when a fallback language is set, in the
    // fallback too, then fills any untranslated title/poster from the fallback (be -> ru -> en).
    private async Task<IReadOnlyList<CatalogItem>> LocalizeListAsync(string path, CatalogKind? kind, CancellationToken cancellationToken, params (string Key, string Value)[] parameters)
    {
        var primaryTask = FetchListAsync(path, kind, RequestLanguage, cancellationToken, parameters);

        // Only Belarusian needs a second list request: TMDB has almost no Belarusian data, so its
        // titles come back as the (English) original and we merge Russian in. Languages with good
        // coverage (ru/en) already fall back sensibly, so a second request would just be wasteful.
        var mergeLists = !string.IsNullOrWhiteSpace(FallbackLanguage)
            && !string.Equals(FallbackLanguage, RequestLanguage, StringComparison.OrdinalIgnoreCase)
            && RequestLanguage.StartsWith("be", StringComparison.OrdinalIgnoreCase);
        if (!mergeLists)
            return await primaryTask.ConfigureAwait(false);

        var fallbackTask = FetchListAsync(path, kind, FallbackLanguage!, cancellationToken, parameters);
        await Task.WhenAll(primaryTask, fallbackTask).ConfigureAwait(false);
        return MergeLists(primaryTask.Result, fallbackTask.Result);
    }

    private async Task<IReadOnlyList<CatalogItem>> FetchListAsync(string path, CatalogKind? kind, string language, CancellationToken cancellationToken, (string Key, string Value)[] parameters)
    {
        var response = await GetAsync<TmdbListResponse>(path, language, cancellationToken, parameters).ConfigureAwait(false);
        return (response.Results ?? [])
            .Where(result => kind is not null || !string.Equals(result.MediaType, "person", StringComparison.OrdinalIgnoreCase))
            .Select(result => ToCatalogItem(result, kind ?? ParseMediaKind(result.MediaType)))
            .ToArray();
    }

    private static IReadOnlyList<CatalogItem> MergeLists(IReadOnlyList<CatalogItem> primary, IReadOnlyList<CatalogItem> fallback)
    {
        var byKey = new Dictionary<(string, CatalogKind), CatalogItem>();
        foreach (var item in fallback)
            byKey[(item.Id, item.Kind)] = item;

        return primary.Select(item =>
        {
            if (!byKey.TryGetValue((item.Id, item.Kind), out var alternate))
                return item;
            var title = IsUntranslated(item.Title, item.OriginalTitle) && !string.IsNullOrWhiteSpace(alternate.Title)
                ? alternate.Title
                : item.Title;
            return item with { Title = title, PosterUrl = item.PosterUrl ?? alternate.PosterUrl };
        }).ToArray();
    }

    private static bool IsUntranslated(string title, string? original)
        => string.IsNullOrWhiteSpace(title) || (original is not null && string.Equals(title, original, StringComparison.Ordinal));

    public async Task<CatalogItemDetails> GetDetailsAsync(string id, CatalogKind kind, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var path = kind == CatalogKind.Movie ? $"movie/{id}" : $"tv/{id}";
        var details = await GetAsync<TmdbDetailsResponse>(path, RequestLanguage, cancellationToken, ("append_to_response", "credits,translations")).ConfigureAwait(false);

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

        // Resolve visible text from the /translations block along the language chain (be -> ru -> en)
        // rather than the primary response, whose fields TMDB already auto-fills with English when the
        // chosen language is missing - which would otherwise skip the preferred Russian fallback.
        var originalTitle = details.OriginalTitle ?? details.OriginalName;
        var title = ResolveTranslated(details, static data => data.Title ?? data.Name)
            ?? details.Title ?? details.Name ?? originalTitle ?? string.Empty;
        var overview = ResolveTranslated(details, static data => data.Overview)
            ?? details.Overview ?? string.Empty;
        var tagline = ResolveTranslated(details, static data => data.Tagline)
            ?? (string.IsNullOrWhiteSpace(details.Tagline) ? null : details.Tagline);

        // Trackers index releases under their English name, so it is resolved regardless of the UI
        // language. Titles that were never translated fall back to the original-language name.
        var englishTitle = ResolveTranslation(details, "en", static data => data.Title ?? data.Name)
            ?? originalTitle;

        return new CatalogItemDetails(
            Id: id,
            Kind: kind,
            Title: title,
            OriginalTitle: originalTitle,
            EnglishTitle: englishTitle,
            Year: ParseYear(details.ReleaseDate ?? details.FirstAirDate),
            PosterUrl: ToImageUrl(PosterBase, details.PosterPath),
            BackdropUrl: ToImageUrl(BackdropBase, details.BackdropPath),
            Rating: details.VoteAverage,
            Overview: overview,
            Genres: details.Genres?.Select(static genre => genre.Name ?? string.Empty).Where(static name => name.Length > 0).ToArray() ?? [],
            Runtime: runtimeMinutes is > 0 ? TimeSpan.FromMinutes(runtimeMinutes.Value) : null,
            SeasonCount: kind == CatalogKind.TvShow ? details.NumberOfSeasons : null,
            Cast: details.Credits?.Cast?.Take(8).Select(static member => member.Name ?? string.Empty).Where(static name => name.Length > 0).ToArray() ?? [],
            Tagline: string.IsNullOrWhiteSpace(tagline) ? null : tagline,
            Directors: directors,
            Countries: details.ProductionCountries?.Select(static country => country.Name ?? string.Empty).Where(static name => name.Length > 0).ToArray() ?? [],
            Seasons: kind == CatalogKind.TvShow
                ? details.Seasons?
                    .Where(static season => season.SeasonNumber is not null && season.EpisodeCount is > 0)
                    .OrderBy(static season => season.SeasonNumber)
                    .Select(static season => new CatalogSeason(season.SeasonNumber!.Value, season.Name ?? string.Empty, season.EpisodeCount!.Value))
                    .ToArray() ?? []
                : []);
    }

    private static CatalogKind ParseMediaKind(string? mediaType)
        => string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ? CatalogKind.TvShow : CatalogKind.Movie;

    // Walks the language chain over the appended /translations block, returning the first non-empty
    // localized value (used to fill a title/overview/tagline the primary language lacks).
    private string? ResolveTranslated(TmdbDetailsResponse details, Func<TmdbTranslationData, string?> selector)
    {
        foreach (var code in LanguageChain())
        {
            if (ResolveTranslation(details, code, selector) is { } value)
                return value;
        }

        return null;
    }

    private static string? ResolveTranslation(TmdbDetailsResponse details, string languageCode, Func<TmdbTranslationData, string?> selector)
    {
        var match = details.Translations?.Translations?
            .FirstOrDefault(translation => string.Equals(translation.Language, languageCode, StringComparison.OrdinalIgnoreCase));
        return match?.Data is { } data && selector(data) is { Length: > 0 } value ? value : null;
    }

    private Task<IReadOnlyList<CatalogItem>> SearchKindAsync(string path, string query, CatalogKind kind, CancellationToken cancellationToken)
        => LocalizeListAsync(path, kind, cancellationToken, ("query", query));

    private async Task<T> GetAsync<T>(string path, string language, CancellationToken cancellationToken, params (string Key, string Value)[] parameters)
    {
        var baseParameters = new List<(string Key, string Value)>
        {
            ("api_key", ApiKey ?? string.Empty),
            ("language", language)
        };
        if (!string.IsNullOrWhiteSpace(Region))
            baseParameters.Add(("region", Region!));

        var query = string.Join('&', baseParameters
            .Concat(parameters)
            .Where(pair => !string.IsNullOrEmpty(pair.Value))
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

        [JsonPropertyName("seasons")]
        public List<TmdbSeason>? Seasons { get; set; }

        [JsonPropertyName("translations")]
        public TmdbTranslations? Translations { get; set; }
    }

    private sealed class TmdbTranslations
    {
        [JsonPropertyName("translations")]
        public List<TmdbTranslation>? Translations { get; set; }
    }

    private sealed class TmdbTranslation
    {
        [JsonPropertyName("iso_639_1")]
        public string? Language { get; set; }

        [JsonPropertyName("data")]
        public TmdbTranslationData? Data { get; set; }
    }

    private sealed class TmdbTranslationData
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("overview")]
        public string? Overview { get; set; }

        [JsonPropertyName("tagline")]
        public string? Tagline { get; set; }
    }

    private sealed class TmdbSeason
    {
        [JsonPropertyName("season_number")]
        public int? SeasonNumber { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("episode_count")]
        public int? EpisodeCount { get; set; }
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
