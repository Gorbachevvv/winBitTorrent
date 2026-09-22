using System.Text.Json;
using System.Text.Json.Nodes;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.EngineProtocol;
using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Infrastructure.Engine;

internal sealed class LocalLibtorrentBackendClient : ITorrentBackendClient
{
    private readonly EnginePipeClient _rpc;

    public LocalLibtorrentBackendClient(ServerProfile profile, EnginePipeClient rpc)
    {
        Profile = profile;
        _rpc = rpc;
        Auth = new AuthApi();
        Application = new ApplicationApi(rpc);
        Sync = new SyncApi(rpc);
        Transfer = new TransferApi(rpc);
        Torrents = new TorrentsApi(rpc);
        Logs = new LogApi(rpc);
        Rss = new RssApi(rpc);
        Search = new SearchApi(rpc);
        TorrentCreator = new TorrentCreatorApi(rpc);
        ClientData = new ClientDataApi(rpc);
    }

    public ServerProfile Profile { get; }
    public BackendCapabilities Capabilities => BackendCapabilities.All;
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

    // The host owns the pipe because ConnectionCoordinator disposes a backend client before it
    // asks the local host to shut down. Closing it here would prevent the graceful shutdown RPC.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class AuthApi : IAuthApi
    {
        public Task LoginAsync(string userName, string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ApplicationApi(EnginePipeClient rpc) : IApplicationApi
    {
        public Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.ApplicationVersion, EngineRpcJsonContext.Default.String, cancellationToken: cancellationToken);
        public Task<string> GetProtocolVersionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult($"EngineRPC/{EngineRpcProtocol.Version}");
        public Task<JsonObject> GetBuildInfoAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.ApplicationBuildInfo, EngineRpcJsonContext.Default.JsonObject, cancellationToken: cancellationToken);
        public Task<JsonObject> GetProcessInfoAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.ApplicationProcessInfo, EngineRpcJsonContext.Default.JsonObject, cancellationToken: cancellationToken);
        public Task<JsonObject> GetPreferencesAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.ApplicationGetPreferences, EngineRpcJsonContext.Default.JsonObject, cancellationToken: cancellationToken);
        public Task SetPreferencesAsync(JsonObject preferences, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.ApplicationSetPreferences, preferences, cancellationToken);
        public Task<string> GetDefaultSavePathAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.ApplicationDefaultSavePath, EngineRpcJsonContext.Default.String, cancellationToken: cancellationToken);
        public Task<string> RotateApiKeyAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.ApplicationRotateApiKey, EngineRpcJsonContext.Default.String, cancellationToken: cancellationToken);
        public Task DeleteApiKeyAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.ApplicationDeleteApiKey, cancellationToken: cancellationToken);
        public Task ChangeRemoteApiPasswordAsync(string newPassword, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.ApplicationChangeRemoteApiPassword, new JsonObject { ["newPassword"] = newPassword }, cancellationToken);
        public Task<bool> DeleteMigrationBackupAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.ApplicationDeleteMigrationBackup, EngineRpcJsonContext.Default.Boolean, cancellationToken: cancellationToken);
        public Task<JsonArray> GetDirectoryContentAsync(string path, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.ApplicationDirectoryContent, EngineRpcJsonContext.Default.JsonArray, new JsonObject { ["path"] = path }, cancellationToken);
        public Task<JsonArray> GetCookiesAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.ApplicationGetCookies, EngineRpcJsonContext.Default.JsonArray, cancellationToken: cancellationToken);
        public Task SetCookiesAsync(JsonArray cookies, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.ApplicationSetCookies, cookies, cancellationToken);
        public Task ShutdownAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.Shutdown, cancellationToken: cancellationToken);
    }

    private sealed class SyncApi(EnginePipeClient rpc) : ISyncApi
    {
        public Task<MainDataResponse> GetMainDataAsync(int responseId, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.SyncMainData, InfrastructureJsonContext.Default.MainDataResponse, new JsonObject { ["responseId"] = responseId }, cancellationToken);
        public Task<JsonObject> GetTorrentPeersAsync(string hash, int responseId, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.SyncTorrentPeers, EngineRpcJsonContext.Default.JsonObject, new JsonObject { ["hash"] = hash, ["responseId"] = responseId }, cancellationToken);
    }

    private sealed class TransferApi(EnginePipeClient rpc) : ITransferApi
    {
        public Task<JsonObject> GetInfoAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TransferInfo, EngineRpcJsonContext.Default.JsonObject, cancellationToken: cancellationToken);
        public Task<bool> GetAlternativeSpeedLimitsAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TransferGetAlternativeLimits, EngineRpcJsonContext.Default.Boolean, cancellationToken: cancellationToken);
        public Task SetAlternativeSpeedLimitsAsync(bool enabled, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TransferSetAlternativeLimits, new JsonObject { ["enabled"] = enabled }, cancellationToken);
        public Task<long> GetDownloadLimitAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TransferGetDownloadLimit, EngineRpcJsonContext.Default.Int64, cancellationToken: cancellationToken);
        public Task<long> GetUploadLimitAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TransferGetUploadLimit, EngineRpcJsonContext.Default.Int64, cancellationToken: cancellationToken);
        public Task SetDownloadLimitAsync(long value, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TransferSetDownloadLimit, new JsonObject { ["value"] = value }, cancellationToken);
        public Task SetUploadLimitAsync(long value, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TransferSetUploadLimit, new JsonObject { ["value"] = value }, cancellationToken);
        public Task BanPeersAsync(IEnumerable<string> peers, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TransferBanPeers, new JsonObject { ["peers"] = ToArray(peers) }, cancellationToken);
    }

    private sealed class TorrentsApi(EnginePipeClient rpc) : ITorrentsApi
    {
        public Task<IReadOnlyList<TorrentInfo>> GetInfoAsync(string filter = "all", string? category = null, string? tag = null, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TorrentsInfo, InfrastructureJsonContext.Default.ListTorrentInfo, new JsonObject { ["filter"] = filter, ["category"] = category, ["tag"] = tag }, cancellationToken).ContinueWith(static task => (IReadOnlyList<TorrentInfo>)task.Result, cancellationToken);
        public Task<TorrentProperties> GetPropertiesAsync(string hash, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TorrentsProperties, InfrastructureJsonContext.Default.TorrentProperties, new JsonObject { ["hash"] = hash }, cancellationToken);
        public Task<IReadOnlyList<TorrentTracker>> GetTrackersAsync(string hash, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TorrentsTrackers, InfrastructureJsonContext.Default.ListTorrentTracker, new JsonObject { ["hash"] = hash }, cancellationToken).ContinueWith(static task => (IReadOnlyList<TorrentTracker>)task.Result, cancellationToken);
        public Task<IReadOnlyList<string>> GetWebSeedsAsync(string hash, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TorrentsWebSeeds, InfrastructureJsonContext.Default.ListString, new JsonObject { ["hash"] = hash }, cancellationToken).ContinueWith(static task => (IReadOnlyList<string>)task.Result, cancellationToken);
        public Task<IReadOnlyList<TorrentFile>> GetFilesAsync(string hash, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TorrentsFiles, InfrastructureJsonContext.Default.ListTorrentFile, new JsonObject { ["hash"] = hash }, cancellationToken).ContinueWith(static task => (IReadOnlyList<TorrentFile>)task.Result, cancellationToken);
        public Task<IReadOnlyList<int>> GetPieceStatesAsync(string hash, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TorrentsPieceStates, InfrastructureJsonContext.Default.ListInt32, new JsonObject { ["hash"] = hash }, cancellationToken).ContinueWith(static task => (IReadOnlyList<int>)task.Result, cancellationToken);
        public Task<IReadOnlyList<int>> GetPieceAvailabilityAsync(string hash, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TorrentsPieceAvailability, InfrastructureJsonContext.Default.ListInt32, new JsonObject { ["hash"] = hash }, cancellationToken).ContinueWith(static task => (IReadOnlyList<int>)task.Result, cancellationToken);
        public Task AddAsync(TorrentAddRequest request, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TorrentsAdd, JsonSerializer.SerializeToNode(request, InfrastructureJsonContext.Default.TorrentAddRequest), cancellationToken);
        public Task DeleteAsync(string hashes, bool deleteFiles, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TorrentsDelete, new JsonObject { ["hashes"] = hashes, ["deleteFiles"] = deleteFiles }, cancellationToken);
        public Task ExecuteAsync(TorrentCommand command, string hashes, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TorrentsCommand, new JsonObject { ["command"] = (int)command, ["hashes"] = hashes }, cancellationToken);
        public Task SetForceStartAsync(string hashes, bool enabled, CancellationToken cancellationToken = default)
            => ActionAsync("setForceStart", new() { ["hashes"] = hashes, ["value"] = enabled ? "true" : "false" }, cancellationToken);
        public Task SetSuperSeedingAsync(string hashes, bool enabled, CancellationToken cancellationToken = default)
            => ActionAsync("setSuperSeeding", new() { ["hashes"] = hashes, ["value"] = enabled ? "true" : "false" }, cancellationToken);
        public Task SetCategoryAsync(string hashes, string category, CancellationToken cancellationToken = default)
            => ActionAsync("setCategory", new() { ["hashes"] = hashes, ["category"] = category }, cancellationToken);
        public Task AddTagsAsync(string hashes, string tags, CancellationToken cancellationToken = default)
            => ActionAsync("addTags", new() { ["hashes"] = hashes, ["tags"] = tags }, cancellationToken);
        public Task RemoveTagsAsync(string hashes, string tags, CancellationToken cancellationToken = default)
            => ActionAsync("removeTags", new() { ["hashes"] = hashes, ["tags"] = tags }, cancellationToken);
        public Task SetLocationAsync(string hashes, string location, CancellationToken cancellationToken = default)
            => ActionAsync("setLocation", new() { ["hashes"] = hashes, ["location"] = location }, cancellationToken);
        public Task SetDownloadLimitAsync(string hashes, long limit, CancellationToken cancellationToken = default)
            => ActionAsync("setDownloadLimit", new() { ["hashes"] = hashes, ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken);
        public Task SetUploadLimitAsync(string hashes, long limit, CancellationToken cancellationToken = default)
            => ActionAsync("setUploadLimit", new() { ["hashes"] = hashes, ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken);
        public Task SetShareLimitsAsync(string hashes, double ratioLimit, int seedingTimeLimit, int inactiveSeedingTimeLimit, CancellationToken cancellationToken = default)
            => ActionAsync("setShareLimits", new()
            {
                ["hashes"] = hashes,
                ["ratioLimit"] = ratioLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["seedingTimeLimit"] = seedingTimeLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["inactiveSeedingTimeLimit"] = inactiveSeedingTimeLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }, cancellationToken);
        public Task RenameAsync(string hash, string name, CancellationToken cancellationToken = default)
            => ActionAsync("rename", new() { ["hash"] = hash, ["name"] = name }, cancellationToken);
        public Task SetFilePriorityAsync(string hash, IEnumerable<int> fileIds, int priority, CancellationToken cancellationToken = default)
            => ActionAsync("filePrio", new() { ["hash"] = hash, ["id"] = string.Join('|', fileIds), ["priority"] = priority.ToString(System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken);
        public Task AddTrackersAsync(string hash, IEnumerable<string> urls, CancellationToken cancellationToken = default)
            => ActionAsync("addTrackers", new() { ["hash"] = hash, ["urls"] = string.Join('\n', urls) }, cancellationToken);
        public Task RemoveTrackersAsync(string hash, IEnumerable<string> urls, CancellationToken cancellationToken = default)
            => ActionAsync("removeTrackers", new() { ["hash"] = hash, ["urls"] = string.Join('|', urls) }, cancellationToken);
        public Task AddWebSeedsAsync(string hash, IEnumerable<string> urls, CancellationToken cancellationToken = default)
            => ActionAsync("addWebSeeds", new() { ["hash"] = hash, ["urls"] = string.Join('\n', urls) }, cancellationToken);
        public Task RemoveWebSeedsAsync(string hash, IEnumerable<string> urls, CancellationToken cancellationToken = default)
            => ActionAsync("removeWebSeeds", new() { ["hash"] = hash, ["urls"] = string.Join('|', urls) }, cancellationToken);
        public Task CreateCategoryAsync(string category, string savePath, CancellationToken cancellationToken = default)
            => ActionAsync("createCategory", new() { ["category"] = category, ["savePath"] = savePath }, cancellationToken);
        public Task EditCategoryAsync(string category, string savePath, CancellationToken cancellationToken = default)
            => ActionAsync("editCategory", new() { ["category"] = category, ["savePath"] = savePath }, cancellationToken);
        public Task RemoveCategoriesAsync(IEnumerable<string> categories, CancellationToken cancellationToken = default)
            => ActionAsync("removeCategories", new() { ["categories"] = string.Join('\n', categories) }, cancellationToken);
        public Task CreateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
            => ActionAsync("createTags", new() { ["tags"] = string.Join(',', tags) }, cancellationToken);
        public Task DeleteTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
            => ActionAsync("deleteTags", new() { ["tags"] = string.Join(',', tags) }, cancellationToken);
        private Task ActionAsync(string action, Dictionary<string, string?> parameters, CancellationToken cancellationToken)
            => rpc.InvokeAsync(EngineRpcMethods.TorrentsAction, new JsonObject { ["action"] = action, ["parameters"] = ToObject(parameters) }, cancellationToken);
        public Task<byte[]> ExportAsync(string hash, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TorrentsExport, EngineRpcJsonContext.Default.ByteArray, new JsonObject { ["hash"] = hash }, cancellationToken);
        public Task<JsonObject> FetchMetadataAsync(string url, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TorrentsFetchMetadata, EngineRpcJsonContext.Default.JsonObject, new JsonObject { ["url"] = url }, cancellationToken);
        public Task<JsonObject> ParseMetadataAsync(string torrentFilePath, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.TorrentsParseMetadata, EngineRpcJsonContext.Default.JsonObject, new JsonObject { ["torrentFilePath"] = torrentFilePath }, cancellationToken);
    }

    private sealed class LogApi(EnginePipeClient rpc) : ILogApi
    {
        public Task<JsonArray> GetMainAsync(long lastKnownId = -1, bool normal = true, bool info = true, bool warning = true, bool critical = true, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.LogsMain, EngineRpcJsonContext.Default.JsonArray, new JsonObject { ["lastKnownId"] = lastKnownId, ["normal"] = normal, ["info"] = info, ["warning"] = warning, ["critical"] = critical }, cancellationToken);
        public Task<JsonArray> GetPeersAsync(long lastKnownId = -1, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.LogsPeers, EngineRpcJsonContext.Default.JsonArray, new JsonObject { ["lastKnownId"] = lastKnownId }, cancellationToken);
    }

    private sealed class RssApi(EnginePipeClient rpc) : IRssApi
    {
        public Task<JsonObject> GetItemsAsync(bool withData = true, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.RssItems, EngineRpcJsonContext.Default.JsonObject, new JsonObject { ["withData"] = withData }, cancellationToken);
        public Task<JsonObject> GetRulesAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.RssRules, EngineRpcJsonContext.Default.JsonObject, cancellationToken: cancellationToken);
        public Task<JsonArray> GetMatchingArticlesAsync(string ruleName, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.RssMatchingArticles, EngineRpcJsonContext.Default.JsonArray, new JsonObject { ["ruleName"] = ruleName }, cancellationToken);
        public Task AddFeedAsync(string url, string path, CancellationToken cancellationToken = default)
            => ActionAsync("addFeed", new() { ["url"] = url, ["path"] = path }, cancellationToken);
        public Task AddFolderAsync(string path, CancellationToken cancellationToken = default)
            => ActionAsync("addFolder", new() { ["path"] = path }, cancellationToken);
        public Task RefreshItemAsync(string itemPath, CancellationToken cancellationToken = default)
            => ActionAsync("refreshItem", new() { ["itemPath"] = itemPath }, cancellationToken);
        public Task RemoveItemAsync(string path, CancellationToken cancellationToken = default)
            => ActionAsync("removeItem", new() { ["path"] = path }, cancellationToken);
        public Task SetRuleAsync(string ruleName, JsonObject definition, CancellationToken cancellationToken = default)
            => ActionAsync("setRule", new() { ["ruleName"] = ruleName, ["ruleDef"] = definition.ToJsonString() }, cancellationToken);
        public Task RemoveRuleAsync(string ruleName, CancellationToken cancellationToken = default)
            => ActionAsync("removeRule", new() { ["ruleName"] = ruleName }, cancellationToken);
        private Task ActionAsync(string action, Dictionary<string, string?> parameters, CancellationToken cancellationToken)
            => rpc.InvokeAsync(EngineRpcMethods.RssAction, new JsonObject { ["action"] = action, ["parameters"] = ToObject(parameters) }, cancellationToken);
    }

    private sealed class SearchApi(EnginePipeClient rpc) : ISearchApi
    {
        public Task<int> StartAsync(string pattern, string category = "all", string plugins = "all", CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.SearchStart, EngineRpcJsonContext.Default.Int32, new JsonObject { ["pattern"] = pattern, ["category"] = category, ["plugins"] = plugins }, cancellationToken);
        public Task<JsonArray> GetStatusAsync(int? id = null, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.SearchStatus, EngineRpcJsonContext.Default.JsonArray, new JsonObject { ["id"] = id }, cancellationToken);
        public Task<JsonObject> GetResultsAsync(int id, int limit = 500, int offset = 0, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.SearchResults, EngineRpcJsonContext.Default.JsonObject, new JsonObject { ["id"] = id, ["limit"] = limit, ["offset"] = offset }, cancellationToken);
        public Task<JsonArray> GetPluginsAsync(CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.SearchPlugins, EngineRpcJsonContext.Default.JsonArray, cancellationToken: cancellationToken);
        public Task InstallPluginAsync(string source, CancellationToken cancellationToken = default)
            => ActionAsync("installPlugin", new() { ["sources"] = source }, cancellationToken);
        public Task SetPluginsEnabledAsync(IEnumerable<string> names, bool enabled, CancellationToken cancellationToken = default)
            => ActionAsync("enablePlugin", new() { ["names"] = string.Join('|', names), ["enable"] = enabled ? "true" : "false" }, cancellationToken);
        public Task UninstallPluginsAsync(IEnumerable<string> names, CancellationToken cancellationToken = default)
            => ActionAsync("uninstallPlugin", new() { ["names"] = string.Join('|', names) }, cancellationToken);
        public Task UpdatePluginsAsync(CancellationToken cancellationToken = default)
            => ActionAsync("updatePlugins", new Dictionary<string, string?>(), cancellationToken);
        public Task StopAsync(int id, CancellationToken cancellationToken = default)
            => ActionAsync("stop", new() { ["id"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken);
        private Task ActionAsync(string action, Dictionary<string, string?> parameters, CancellationToken cancellationToken)
            => rpc.InvokeAsync(EngineRpcMethods.SearchAction, new JsonObject { ["action"] = action, ["parameters"] = ToObject(parameters) }, cancellationToken);
    }

    private sealed class TorrentCreatorApi(EnginePipeClient rpc) : ITorrentCreatorApi
    {
        public Task<JsonObject> AddTaskAsync(JsonObject request, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.CreatorAdd, EngineRpcJsonContext.Default.JsonObject, request, cancellationToken);
        public Task<JsonObject> GetStatusAsync(string taskId, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.CreatorStatus, EngineRpcJsonContext.Default.JsonObject, new JsonObject { ["taskId"] = taskId }, cancellationToken);
        public Task<byte[]> GetTorrentFileAsync(string taskId, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.CreatorFile, EngineRpcJsonContext.Default.ByteArray, new JsonObject { ["taskId"] = taskId }, cancellationToken);
        public Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.CreatorDelete, new JsonObject { ["taskId"] = taskId }, cancellationToken);
    }

    private sealed class ClientDataApi(EnginePipeClient rpc) : IClientDataApi
    {
        public Task<JsonObject> LoadAsync(string key, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.ClientDataLoad, EngineRpcJsonContext.Default.JsonObject, new JsonObject { ["key"] = key }, cancellationToken);
        public Task StoreAsync(string key, JsonNode value, CancellationToken cancellationToken = default)
            => rpc.InvokeAsync(EngineRpcMethods.ClientDataStore, new JsonObject { ["key"] = key, ["value"] = value.DeepClone() }, cancellationToken);
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add((JsonNode?)JsonValue.Create(value));
        return array;
    }

    private static JsonObject ToObject(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in values)
            obj[key] = value;
        return obj;
    }
}
