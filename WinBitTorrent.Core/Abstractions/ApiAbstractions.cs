using System.Text.Json.Nodes;
using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Core.Abstractions;

public interface IQBittorrentApi : IAsyncDisposable
{
    ServerProfile Profile { get; }
    IAuthApi Auth { get; }
    IApplicationApi Application { get; }
    ISyncApi Sync { get; }
    ITransferApi Transfer { get; }
    ITorrentsApi Torrents { get; }
    ILogApi Logs { get; }
    IRssApi Rss { get; }
    ISearchApi Search { get; }
    ITorrentCreatorApi TorrentCreator { get; }
    IClientDataApi ClientData { get; }
}

public interface IAuthApi
{
    Task LoginAsync(string userName, string password, CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
}

public interface IApplicationApi
{
    Task<string> GetVersionAsync(CancellationToken cancellationToken = default);
    Task<string> GetWebApiVersionAsync(CancellationToken cancellationToken = default);
    Task<JsonObject> GetBuildInfoAsync(CancellationToken cancellationToken = default);
    Task<JsonObject> GetProcessInfoAsync(CancellationToken cancellationToken = default);
    Task<JsonObject> GetPreferencesAsync(CancellationToken cancellationToken = default);
    Task SetPreferencesAsync(JsonObject preferences, CancellationToken cancellationToken = default);
    Task<string> GetDefaultSavePathAsync(CancellationToken cancellationToken = default);
    Task<string> RotateApiKeyAsync(CancellationToken cancellationToken = default);
    Task DeleteApiKeyAsync(CancellationToken cancellationToken = default);
    Task<JsonArray> GetDirectoryContentAsync(string path, CancellationToken cancellationToken = default);
    Task<JsonArray> GetCookiesAsync(CancellationToken cancellationToken = default);
    Task SetCookiesAsync(JsonArray cookies, CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

public interface ISyncApi
{
    Task<MainDataResponse> GetMainDataAsync(int responseId, CancellationToken cancellationToken = default);
    Task<JsonObject> GetTorrentPeersAsync(string hash, int responseId, CancellationToken cancellationToken = default);
}

public interface ITransferApi
{
    Task<JsonObject> GetInfoAsync(CancellationToken cancellationToken = default);
    Task<bool> GetAlternativeSpeedLimitsAsync(CancellationToken cancellationToken = default);
    Task SetAlternativeSpeedLimitsAsync(bool enabled, CancellationToken cancellationToken = default);
    Task<long> GetDownloadLimitAsync(CancellationToken cancellationToken = default);
    Task<long> GetUploadLimitAsync(CancellationToken cancellationToken = default);
    Task SetDownloadLimitAsync(long value, CancellationToken cancellationToken = default);
    Task SetUploadLimitAsync(long value, CancellationToken cancellationToken = default);
    Task BanPeersAsync(IEnumerable<string> peers, CancellationToken cancellationToken = default);
}

public interface ITorrentsApi
{
    Task<IReadOnlyList<TorrentInfo>> GetInfoAsync(string filter = "all", string? category = null, string? tag = null, CancellationToken cancellationToken = default);
    Task<TorrentProperties> GetPropertiesAsync(string hash, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TorrentTracker>> GetTrackersAsync(string hash, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetWebSeedsAsync(string hash, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TorrentFile>> GetFilesAsync(string hash, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<int>> GetPieceStatesAsync(string hash, CancellationToken cancellationToken = default);
    Task AddAsync(TorrentAddRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(string hashes, bool deleteFiles, CancellationToken cancellationToken = default);
    Task ExecuteAsync(TorrentCommand command, string hashes, CancellationToken cancellationToken = default);
    Task PostAsync(string action, IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken = default);
    Task<byte[]> ExportAsync(string hash, CancellationToken cancellationToken = default);
    Task<JsonObject> FetchMetadataAsync(string url, CancellationToken cancellationToken = default);
    Task<JsonObject> ParseMetadataAsync(string torrentFilePath, CancellationToken cancellationToken = default);
}

public interface ILogApi
{
    Task<JsonArray> GetMainAsync(long lastKnownId = -1, bool normal = true, bool info = true, bool warning = true, bool critical = true, CancellationToken cancellationToken = default);
    Task<JsonArray> GetPeersAsync(long lastKnownId = -1, CancellationToken cancellationToken = default);
}

public interface IRssApi
{
    Task<JsonObject> GetItemsAsync(bool withData = true, CancellationToken cancellationToken = default);
    Task<JsonObject> GetRulesAsync(CancellationToken cancellationToken = default);
    Task<JsonArray> GetMatchingArticlesAsync(string ruleName, CancellationToken cancellationToken = default);
    Task PostAsync(string action, IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken = default);
}

public interface ISearchApi
{
    Task<int> StartAsync(string pattern, string category = "all", string plugins = "all", CancellationToken cancellationToken = default);
    Task<JsonArray> GetStatusAsync(int? id = null, CancellationToken cancellationToken = default);
    Task<JsonObject> GetResultsAsync(int id, int limit = 500, int offset = 0, CancellationToken cancellationToken = default);
    Task<JsonArray> GetPluginsAsync(CancellationToken cancellationToken = default);
    Task PostAsync(string action, IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken = default);
}

public interface ITorrentCreatorApi
{
    Task<JsonObject> AddTaskAsync(JsonObject request, CancellationToken cancellationToken = default);
    Task<JsonObject> GetStatusAsync(int taskId, CancellationToken cancellationToken = default);
    Task<byte[]> GetTorrentFileAsync(int taskId, CancellationToken cancellationToken = default);
    Task DeleteTaskAsync(int taskId, CancellationToken cancellationToken = default);
}

public interface IClientDataApi
{
    Task<JsonObject> LoadAsync(string key, CancellationToken cancellationToken = default);
    Task StoreAsync(string key, JsonNode value, CancellationToken cancellationToken = default);
}

public sealed class QbittorrentApiException : Exception
{
    public QbittorrentApiException(string message, int? statusCode = null, string? response = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Response = response;
    }

    public int? StatusCode { get; }
    public string? Response { get; }
}
