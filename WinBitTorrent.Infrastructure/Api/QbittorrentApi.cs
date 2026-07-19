using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Infrastructure.Api;

public sealed class QbittorrentApi : IQBittorrentApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public QbittorrentApi(ServerProfile profile, HttpClient client, bool ownsClient = true)
    {
        Profile = profile;
        _client = client;
        _ownsClient = ownsClient;

        Auth = new AuthApi(this);
        Application = new ApplicationApi(this);
        Sync = new SyncApi(this);
        Transfer = new TransferApi(this);
        Torrents = new TorrentsApi(this);
        Logs = new LogApi(this);
        Rss = new RssApi(this);
        Search = new SearchApi(this);
        TorrentCreator = new TorrentCreatorApi(this);
        ClientData = new ClientDataApi(this);
    }

    public ServerProfile Profile { get; }
    public IAuthApi Auth { get; }
    public IApplicationApi Application { get; }
    public ISyncApi Sync { get; }
    public ITransferApi Transfer { get; }
    public ITorrentsApi Torrents { get; }
    public ILogApi Logs { get; }
    public IRssApi Rss { get; }
    public ISearchApi Search { get; }
    public ITorrentCreatorApi TorrentCreator { get; }
    public IClientDataApi ClientData { get; }

    public static QbittorrentApi Create(ServerProfile profile, string? apiKey = null, HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true
        };

        var client = new HttpClient(handler)
        {
            BaseAddress = EnsureTrailingSlash(profile.BaseAddress),
            Timeout = TimeSpan.FromSeconds(30)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinBitTorrent/1.0");
        client.DefaultRequestHeaders.Referrer = client.BaseAddress;
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return new QbittorrentApi(profile, client);
    }

    internal async Task<string> GetStringAsync(string path, IReadOnlyDictionary<string, string?>? query = null, CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync(BuildPath(path, query), cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<byte[]> GetBytesAsync(string path, IReadOnlyDictionary<string, string?>? query = null, CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync(BuildPath(path, query), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw CreateException(response, body);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<T> GetJsonAsync<T>(string path, IReadOnlyDictionary<string, string?>? query = null, CancellationToken cancellationToken = default)
    {
        var json = await GetStringAsync(path, query, cancellationToken).ConfigureAwait(false);
        return Deserialize<T>(json);
    }

    internal async Task<JsonObject> GetObjectAsync(string path, IReadOnlyDictionary<string, string?>? query = null, CancellationToken cancellationToken = default)
        => ParseObject(await GetStringAsync(path, query, cancellationToken).ConfigureAwait(false));

    internal async Task<JsonArray> GetArrayAsync(string path, IReadOnlyDictionary<string, string?>? query = null, CancellationToken cancellationToken = default)
        => ParseArray(await GetStringAsync(path, query, cancellationToken).ConfigureAwait(false));

    internal Task PostAsync(string path, IReadOnlyDictionary<string, string?>? parameters = null, CancellationToken cancellationToken = default)
        => PostForStringAsync(path, parameters, cancellationToken);

    internal async Task<string> PostForStringAsync(string path, IReadOnlyDictionary<string, string?>? parameters = null, CancellationToken cancellationToken = default)
        => (await PostForResponseAsync(path, parameters, cancellationToken).ConfigureAwait(false)).Body;

    internal async Task<(HttpStatusCode StatusCode, string Body)> PostForResponseAsync(string path, IReadOnlyDictionary<string, string?>? parameters = null, CancellationToken cancellationToken = default)
    {
        var fields = parameters?
            .Where(static item => item.Value is not null)
            .Select(static item => new KeyValuePair<string, string>(item.Key, item.Value!))
            ?? [];

        using var content = new FormUrlEncodedContent(fields);
        using var response = await _client.PostAsync(path, content, cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false));
    }

    internal async Task<string> PostMultipartAsync(string path, MultipartFormDataContent content, CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsync(path, content, cancellationToken).ConfigureAwait(false);
        return await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new QbittorrentApiException($"qBittorrent returned an empty {typeof(T).Name} response.", response: json);

    private static JsonObject ParseObject(string json)
        => JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json)?.AsObject()
            ?? throw new QbittorrentApiException("qBittorrent returned invalid JSON object.", response: json);

    private static JsonArray ParseArray(string json)
        => JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json)?.AsArray()
            ?? throw new QbittorrentApiException("qBittorrent returned invalid JSON array.", response: json);

    private static async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw CreateException(response, body);
        return body;
    }

    private static QbittorrentApiException CreateException(HttpResponseMessage response, string body)
    {
        var message = string.IsNullOrWhiteSpace(body)
            ? $"qBittorrent API returned {(int)response.StatusCode} {response.ReasonPhrase}."
            : $"qBittorrent API returned {(int)response.StatusCode}: {body.Trim()}";
        return new QbittorrentApiException(message, (int)response.StatusCode, body);
    }

    private static string BuildPath(string path, IReadOnlyDictionary<string, string?>? query)
    {
        if (query is null || query.Count == 0)
            return path;

        var values = query
            .Where(static pair => pair.Value is not null)
            .Select(static pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");
        return $"{path}?{string.Join('&', values)}";
    }

    private static Uri EnsureTrailingSlash(Uri uri)
        => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);

    public ValueTask DisposeAsync()
    {
        if (_ownsClient)
            _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class AuthApi(QbittorrentApi api) : IAuthApi
    {
        public async Task LoginAsync(string userName, string password, CancellationToken cancellationToken = default)
        {
            var result = await api.PostForStringAsync("api/v2/auth/login", new Dictionary<string, string?>
            {
                ["username"] = userName,
                ["password"] = password
            }, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(result) && !result.Trim().Equals("Ok.", StringComparison.OrdinalIgnoreCase))
                throw new QbittorrentApiException("qBittorrent authentication failed.", response: result);
        }

        public Task LogoutAsync(CancellationToken cancellationToken = default)
            => api.PostAsync("api/v2/auth/logout", cancellationToken: cancellationToken);
    }

    private sealed class ApplicationApi(QbittorrentApi api) : IApplicationApi
    {
        public Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
            => api.GetStringAsync("api/v2/app/version", cancellationToken: cancellationToken);

        public Task<string> GetWebApiVersionAsync(CancellationToken cancellationToken = default)
            => api.GetStringAsync("api/v2/app/webapiVersion", cancellationToken: cancellationToken);

        public Task<JsonObject> GetBuildInfoAsync(CancellationToken cancellationToken = default)
            => api.GetObjectAsync("api/v2/app/buildInfo", cancellationToken: cancellationToken);

        public Task<JsonObject> GetProcessInfoAsync(CancellationToken cancellationToken = default)
            => api.GetObjectAsync("api/v2/app/processInfo", cancellationToken: cancellationToken);

        public Task<JsonObject> GetPreferencesAsync(CancellationToken cancellationToken = default)
            => api.GetObjectAsync("api/v2/app/preferences", cancellationToken: cancellationToken);

        public Task SetPreferencesAsync(JsonObject preferences, CancellationToken cancellationToken = default)
            => api.PostAsync("api/v2/app/setPreferences", new Dictionary<string, string?>
            {
                ["json"] = preferences.ToJsonString()
            }, cancellationToken);

        public Task<string> GetDefaultSavePathAsync(CancellationToken cancellationToken = default)
            => api.GetStringAsync("api/v2/app/defaultSavePath", cancellationToken: cancellationToken);

        public async Task<string> RotateApiKeyAsync(CancellationToken cancellationToken = default)
        {
            var json = ParseObject(await api.PostForStringAsync("api/v2/app/rotateAPIKey", cancellationToken: cancellationToken).ConfigureAwait(false));
            return json["apiKey"]?.GetValue<string>()
                ?? throw new QbittorrentApiException("qBittorrent did not return the generated API key.");
        }

        public Task DeleteApiKeyAsync(CancellationToken cancellationToken = default)
            => api.PostAsync("api/v2/app/deleteAPIKey", cancellationToken: cancellationToken);

        public Task<JsonArray> GetDirectoryContentAsync(string path, CancellationToken cancellationToken = default)
            => api.GetArrayAsync("api/v2/app/getDirectoryContent", new Dictionary<string, string?> { ["dirPath"] = path }, cancellationToken);

        public Task<JsonArray> GetCookiesAsync(CancellationToken cancellationToken = default)
            => api.GetArrayAsync("api/v2/app/cookies", cancellationToken: cancellationToken);

        public Task SetCookiesAsync(JsonArray cookies, CancellationToken cancellationToken = default)
            => api.PostAsync("api/v2/app/setCookies", new Dictionary<string, string?> { ["cookies"] = cookies.ToJsonString() }, cancellationToken);

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
            => api.PostAsync("api/v2/app/shutdown", cancellationToken: cancellationToken);
    }

    private sealed class SyncApi(QbittorrentApi api) : ISyncApi
    {
        public Task<MainDataResponse> GetMainDataAsync(int responseId, CancellationToken cancellationToken = default)
            => api.GetJsonAsync<MainDataResponse>("api/v2/sync/maindata", new Dictionary<string, string?>
            {
                ["rid"] = responseId.ToString(CultureInfo.InvariantCulture)
            }, cancellationToken);

        public Task<JsonObject> GetTorrentPeersAsync(string hash, int responseId, CancellationToken cancellationToken = default)
            => api.GetObjectAsync("api/v2/sync/torrentPeers", new Dictionary<string, string?>
            {
                ["hash"] = hash,
                ["rid"] = responseId.ToString(CultureInfo.InvariantCulture)
            }, cancellationToken);
    }

    private sealed class TransferApi(QbittorrentApi api) : ITransferApi
    {
        public Task<JsonObject> GetInfoAsync(CancellationToken cancellationToken = default)
            => api.GetObjectAsync("api/v2/transfer/info", cancellationToken: cancellationToken);

        public async Task<bool> GetAlternativeSpeedLimitsAsync(CancellationToken cancellationToken = default)
        {
            var value = await api.GetStringAsync("api/v2/transfer/speedLimitsMode", cancellationToken: cancellationToken).ConfigureAwait(false);
            return value.Trim() is "1" or "true";
        }

        public Task SetAlternativeSpeedLimitsAsync(bool enabled, CancellationToken cancellationToken = default)
            => api.PostAsync("api/v2/transfer/setSpeedLimitsMode", new Dictionary<string, string?> { ["mode"] = enabled ? "1" : "0" }, cancellationToken);

        public async Task<long> GetDownloadLimitAsync(CancellationToken cancellationToken = default)
            => long.Parse(await api.GetStringAsync("api/v2/transfer/downloadLimit", cancellationToken: cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        public async Task<long> GetUploadLimitAsync(CancellationToken cancellationToken = default)
            => long.Parse(await api.GetStringAsync("api/v2/transfer/uploadLimit", cancellationToken: cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        public Task SetDownloadLimitAsync(long value, CancellationToken cancellationToken = default)
            => api.PostAsync("api/v2/transfer/setDownloadLimit", new Dictionary<string, string?> { ["limit"] = value.ToString(CultureInfo.InvariantCulture) }, cancellationToken);

        public Task SetUploadLimitAsync(long value, CancellationToken cancellationToken = default)
            => api.PostAsync("api/v2/transfer/setUploadLimit", new Dictionary<string, string?> { ["limit"] = value.ToString(CultureInfo.InvariantCulture) }, cancellationToken);

        public Task BanPeersAsync(IEnumerable<string> peers, CancellationToken cancellationToken = default)
            => api.PostAsync("api/v2/transfer/banPeers", new Dictionary<string, string?> { ["peers"] = string.Join('|', peers) }, cancellationToken);
    }

    private sealed class TorrentsApi(QbittorrentApi api) : ITorrentsApi
    {
        public async Task<IReadOnlyList<TorrentInfo>> GetInfoAsync(string filter = "all", string? category = null, string? tag = null, CancellationToken cancellationToken = default)
        {
            var torrents = await api.GetJsonAsync<List<TorrentInfo>>("api/v2/torrents/info", new Dictionary<string, string?>
            {
                ["filter"] = filter,
                ["category"] = category,
                ["tag"] = tag
            }, cancellationToken).ConfigureAwait(false);
            return torrents;
        }

        public Task<TorrentProperties> GetPropertiesAsync(string hash, CancellationToken cancellationToken = default)
            => api.GetJsonAsync<TorrentProperties>("api/v2/torrents/properties", new Dictionary<string, string?> { ["hash"] = hash }, cancellationToken);

        public async Task<IReadOnlyList<TorrentTracker>> GetTrackersAsync(string hash, CancellationToken cancellationToken = default)
            => await api.GetJsonAsync<List<TorrentTracker>>("api/v2/torrents/trackers", new Dictionary<string, string?> { ["hash"] = hash }, cancellationToken).ConfigureAwait(false);

        public async Task<IReadOnlyList<string>> GetWebSeedsAsync(string hash, CancellationToken cancellationToken = default)
        {
            var array = await api.GetArrayAsync("api/v2/torrents/webseeds", new Dictionary<string, string?> { ["hash"] = hash }, cancellationToken).ConfigureAwait(false);
            return array.Select(static node => node is JsonObject obj ? obj["url"]?.GetValue<string>() ?? string.Empty : node?.GetValue<string>() ?? string.Empty)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToList();
        }

        public async Task<IReadOnlyList<TorrentFile>> GetFilesAsync(string hash, CancellationToken cancellationToken = default)
            => await api.GetJsonAsync<List<TorrentFile>>("api/v2/torrents/files", new Dictionary<string, string?> { ["hash"] = hash }, cancellationToken).ConfigureAwait(false);

        public async Task<IReadOnlyList<int>> GetPieceStatesAsync(string hash, CancellationToken cancellationToken = default)
            => await api.GetJsonAsync<List<int>>("api/v2/torrents/pieceStates", new Dictionary<string, string?> { ["hash"] = hash }, cancellationToken).ConfigureAwait(false);

        public async Task AddAsync(TorrentAddRequest request, CancellationToken cancellationToken = default)
        {
            using var content = new MultipartFormDataContent();
            if (request.Urls.Count > 0)
                content.Add(new StringContent(string.Join('\n', request.Urls)), "urls");

            foreach (var file in request.TorrentFiles)
            {
                var stream = File.OpenRead(file);
                var fileContent = new StreamContent(stream);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
                content.Add(fileContent, "torrents", Path.GetFileName(file));
            }

            AddString(content, "savepath", request.SavePath);
            AddString(content, "downloadPath", request.DownloadPath);
            AddString(content, "category", request.Category);
            AddString(content, "tags", request.Tags);
            AddString(content, "stopped", request.StartTorrent ? "false" : "true");
            AddString(content, "sequentialDownload", request.SequentialDownload ? "true" : "false");
            AddString(content, "firstLastPiecePrio", request.FirstLastPiecePriority ? "true" : "false");
            AddString(content, "autoTMM", request.AutomaticTorrentManagement ? "true" : "false");
            AddString(content, "skip_checking", request.SkipChecking ? "true" : "false");
            AddString(content, "upLimit", request.UploadLimit?.ToString(CultureInfo.InvariantCulture));
            AddString(content, "dlLimit", request.DownloadLimit?.ToString(CultureInfo.InvariantCulture));
            await api.PostMultipartAsync("api/v2/torrents/add", content, cancellationToken).ConfigureAwait(false);
        }

        public Task DeleteAsync(string hashes, bool deleteFiles, CancellationToken cancellationToken = default)
            => api.PostAsync("api/v2/torrents/delete", new Dictionary<string, string?>
            {
                ["hashes"] = hashes,
                ["deleteFiles"] = deleteFiles ? "true" : "false"
            }, cancellationToken);

        public Task ExecuteAsync(TorrentCommand command, string hashes, CancellationToken cancellationToken = default)
        {
            var action = command switch
            {
                TorrentCommand.Start => "start",
                TorrentCommand.Stop => "stop",
                TorrentCommand.Recheck => "recheck",
                TorrentCommand.Reannounce => "reannounce",
                TorrentCommand.IncreasePriority => "increasePrio",
                TorrentCommand.DecreasePriority => "decreasePrio",
                TorrentCommand.TopPriority => "topPrio",
                TorrentCommand.BottomPriority => "bottomPrio",
                TorrentCommand.ToggleSequentialDownload => "toggleSequentialDownload",
                TorrentCommand.ToggleFirstLastPiecePriority => "toggleFirstLastPiecePrio",
                _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
            };
            return api.PostAsync($"api/v2/torrents/{action}", new Dictionary<string, string?> { ["hashes"] = hashes }, cancellationToken);
        }

        public Task PostAsync(string action, IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken = default)
            => api.PostAsync($"api/v2/torrents/{action}", parameters, cancellationToken);

        public Task<byte[]> ExportAsync(string hash, CancellationToken cancellationToken = default)
            => api.GetBytesAsync("api/v2/torrents/export", new Dictionary<string, string?> { ["hash"] = hash }, cancellationToken);

        public async Task<JsonObject> FetchMetadataAsync(string url, CancellationToken cancellationToken = default)
        {
            for (var attempt = 0; attempt < 120; attempt++)
            {
                var response = await api.PostForResponseAsync(
                    "api/v2/torrents/fetchMetadata",
                    new Dictionary<string, string?> { ["source"] = url },
                    cancellationToken).ConfigureAwait(false);
                var metadata = ParseObject(response.Body);
                if (response.StatusCode != HttpStatusCode.Accepted)
                    return metadata;

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out while waiting for qBittorrent torrent metadata.");
        }

        public async Task<JsonObject> ParseMetadataAsync(string torrentFilePath, CancellationToken cancellationToken = default)
        {
            using var content = new MultipartFormDataContent();
            var stream = File.OpenRead(torrentFilePath);
            content.Add(new StreamContent(stream), "torrent", Path.GetFileName(torrentFilePath));
            var json = await api.PostMultipartAsync("api/v2/torrents/parseMetadata", content, cancellationToken).ConfigureAwait(false);
            var node = JsonNode.Parse(json);
            return node switch
            {
                JsonObject result => result,
                JsonArray { Count: > 0 } result when result[0] is JsonObject first => first,
                _ => throw new QbittorrentApiException("qBittorrent returned invalid torrent metadata.", response: json)
            };
        }

        private static void AddString(MultipartFormDataContent content, string name, string? value)
        {
            if (value is not null)
                content.Add(new StringContent(value), name);
        }
    }

    private sealed class LogApi(QbittorrentApi api) : ILogApi
    {
        public Task<JsonArray> GetMainAsync(long lastKnownId = -1, bool normal = true, bool info = true, bool warning = true, bool critical = true, CancellationToken cancellationToken = default)
            => api.GetArrayAsync("api/v2/log/main", new Dictionary<string, string?>
            {
                ["last_known_id"] = lastKnownId.ToString(CultureInfo.InvariantCulture),
                ["normal"] = normal.ToString().ToLowerInvariant(),
                ["info"] = info.ToString().ToLowerInvariant(),
                ["warning"] = warning.ToString().ToLowerInvariant(),
                ["critical"] = critical.ToString().ToLowerInvariant()
            }, cancellationToken);

        public Task<JsonArray> GetPeersAsync(long lastKnownId = -1, CancellationToken cancellationToken = default)
            => api.GetArrayAsync("api/v2/log/peers", new Dictionary<string, string?>
            {
                ["last_known_id"] = lastKnownId.ToString(CultureInfo.InvariantCulture)
            }, cancellationToken);
    }

    private sealed class RssApi(QbittorrentApi api) : IRssApi
    {
        public Task<JsonObject> GetItemsAsync(bool withData = true, CancellationToken cancellationToken = default)
            => api.GetObjectAsync("api/v2/rss/items", new Dictionary<string, string?> { ["withData"] = withData ? "true" : "false" }, cancellationToken);

        public Task<JsonObject> GetRulesAsync(CancellationToken cancellationToken = default)
            => api.GetObjectAsync("api/v2/rss/rules", cancellationToken: cancellationToken);

        public Task<JsonArray> GetMatchingArticlesAsync(string ruleName, CancellationToken cancellationToken = default)
            => api.GetArrayAsync("api/v2/rss/matchingArticles", new Dictionary<string, string?> { ["ruleName"] = ruleName }, cancellationToken);

        public Task PostAsync(string action, IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken = default)
            => api.PostAsync($"api/v2/rss/{action}", parameters, cancellationToken);
    }

    private sealed class SearchApi(QbittorrentApi api) : ISearchApi
    {
        public async Task<int> StartAsync(string pattern, string category = "all", string plugins = "all", CancellationToken cancellationToken = default)
        {
            var json = ParseObject(await api.PostForStringAsync("api/v2/search/start", new Dictionary<string, string?>
            {
                ["pattern"] = pattern,
                ["category"] = category,
                ["plugins"] = plugins
            }, cancellationToken).ConfigureAwait(false));
            return json["id"]?.GetValue<int>() ?? throw new QbittorrentApiException("Search did not return a job id.");
        }

        public Task<JsonArray> GetStatusAsync(int? id = null, CancellationToken cancellationToken = default)
            => api.GetArrayAsync("api/v2/search/status", id is null ? null : new Dictionary<string, string?> { ["id"] = id.Value.ToString(CultureInfo.InvariantCulture) }, cancellationToken);

        public Task<JsonObject> GetResultsAsync(int id, int limit = 500, int offset = 0, CancellationToken cancellationToken = default)
            => api.GetObjectAsync("api/v2/search/results", new Dictionary<string, string?>
            {
                ["id"] = id.ToString(CultureInfo.InvariantCulture),
                ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
                ["offset"] = offset.ToString(CultureInfo.InvariantCulture)
            }, cancellationToken);

        public Task<JsonArray> GetPluginsAsync(CancellationToken cancellationToken = default)
            => api.GetArrayAsync("api/v2/search/plugins", cancellationToken: cancellationToken);

        public Task PostAsync(string action, IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken = default)
            => api.PostAsync($"api/v2/search/{action}", parameters, cancellationToken);
    }

    private sealed class TorrentCreatorApi(QbittorrentApi api) : ITorrentCreatorApi
    {
        public async Task<JsonObject> AddTaskAsync(JsonObject request, CancellationToken cancellationToken = default)
        {
            var parameters = request.ToDictionary(static item => item.Key, static item => item.Value?.ToJsonString().Trim('"'));
            return ParseObject(await api.PostForStringAsync("api/v2/torrentcreator/addTask", parameters, cancellationToken).ConfigureAwait(false));
        }

        public Task<JsonObject> GetStatusAsync(int taskId, CancellationToken cancellationToken = default)
            => api.GetObjectAsync("api/v2/torrentcreator/status", new Dictionary<string, string?> { ["taskID"] = taskId.ToString(CultureInfo.InvariantCulture) }, cancellationToken);

        public Task<byte[]> GetTorrentFileAsync(int taskId, CancellationToken cancellationToken = default)
            => api.GetBytesAsync("api/v2/torrentcreator/torrentFile", new Dictionary<string, string?> { ["taskID"] = taskId.ToString(CultureInfo.InvariantCulture) }, cancellationToken);

        public Task DeleteTaskAsync(int taskId, CancellationToken cancellationToken = default)
            => api.PostAsync("api/v2/torrentcreator/deleteTask", new Dictionary<string, string?> { ["taskID"] = taskId.ToString(CultureInfo.InvariantCulture) }, cancellationToken);
    }

    private sealed class ClientDataApi(QbittorrentApi api) : IClientDataApi
    {
        public Task<JsonObject> LoadAsync(string key, CancellationToken cancellationToken = default)
            => api.GetObjectAsync("api/v2/clientdata/load", new Dictionary<string, string?> { ["key"] = key }, cancellationToken);

        public Task StoreAsync(string key, JsonNode value, CancellationToken cancellationToken = default)
            => api.PostAsync("api/v2/clientdata/store", new Dictionary<string, string?>
            {
                ["key"] = key,
                ["value"] = value.ToJsonString()
            }, cancellationToken);
    }
}
