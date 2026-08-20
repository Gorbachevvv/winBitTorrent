using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinBitTorrent.Core.EngineProtocol;

public static class EngineRpcProtocol
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 64 * 1024 * 1024;
}

public sealed record EngineRpcRequest(
    int Version,
    long Id,
    string Method,
    JsonElement Payload,
    string? AuthenticationToken = null);

public sealed record EngineRpcResponse(
    int Version,
    long Id,
    bool Success,
    JsonElement Payload,
    EngineRpcError? Error = null);

public sealed record EngineRpcError(string Code, string Message, string? Details = null);

public sealed record EngineHello(
    string EngineVersion,
    string LibtorrentVersion,
    int ProcessId,
    DateTimeOffset StartedAt);

public static class EngineRpcMethods
{
    public const string Authenticate = "system.authenticate";
    public const string Shutdown = "system.shutdown";
    public const string ApplicationVersion = "application.version";
    public const string ApplicationBuildInfo = "application.buildInfo";
    public const string ApplicationProcessInfo = "application.processInfo";
    public const string ApplicationGetPreferences = "application.getPreferences";
    public const string ApplicationSetPreferences = "application.setPreferences";
    public const string ApplicationDefaultSavePath = "application.defaultSavePath";
    public const string ApplicationDirectoryContent = "application.directoryContent";
    public const string ApplicationGetCookies = "application.getCookies";
    public const string ApplicationSetCookies = "application.setCookies";
    public const string ApplicationRotateApiKey = "application.rotateApiKey";
    public const string ApplicationDeleteApiKey = "application.deleteApiKey";
    public const string ApplicationChangeRemoteApiPassword = "application.changeRemoteApiPassword";
    public const string ApplicationDeleteMigrationBackup = "application.deleteMigrationBackup";
    public const string SyncMainData = "sync.mainData";
    public const string SyncTorrentPeers = "sync.torrentPeers";
    public const string TransferInfo = "transfer.info";
    public const string TransferGetAlternativeLimits = "transfer.getAlternativeLimits";
    public const string TransferSetAlternativeLimits = "transfer.setAlternativeLimits";
    public const string TransferGetDownloadLimit = "transfer.getDownloadLimit";
    public const string TransferGetUploadLimit = "transfer.getUploadLimit";
    public const string TransferSetDownloadLimit = "transfer.setDownloadLimit";
    public const string TransferSetUploadLimit = "transfer.setUploadLimit";
    public const string TransferBanPeers = "transfer.banPeers";
    public const string TorrentsInfo = "torrents.info";
    public const string TorrentsProperties = "torrents.properties";
    public const string TorrentsTrackers = "torrents.trackers";
    public const string TorrentsWebSeeds = "torrents.webSeeds";
    public const string TorrentsFiles = "torrents.files";
    public const string TorrentsPieceStates = "torrents.pieceStates";
    public const string TorrentsAdd = "torrents.add";
    public const string TorrentsDelete = "torrents.delete";
    public const string TorrentsCommand = "torrents.command";
    public const string TorrentsAction = "torrents.action";
    public const string TorrentsExport = "torrents.export";
    public const string TorrentsFetchMetadata = "torrents.fetchMetadata";
    public const string TorrentsParseMetadata = "torrents.parseMetadata";
    public const string TorrentsMetadata = "torrents.metadata";
    public const string LogsMain = "logs.main";
    public const string LogsPeers = "logs.peers";
    public const string RssItems = "rss.items";
    public const string RssRules = "rss.rules";
    public const string RssMatchingArticles = "rss.matchingArticles";
    public const string RssAction = "rss.action";
    public const string SearchStart = "search.start";
    public const string SearchStatus = "search.status";
    public const string SearchResults = "search.results";
    public const string SearchPlugins = "search.plugins";
    public const string SearchAction = "search.action";
    public const string CreatorAdd = "creator.add";
    public const string CreatorStatus = "creator.status";
    public const string CreatorFile = "creator.file";
    public const string CreatorDelete = "creator.delete";
    public const string ClientDataLoad = "clientData.load";
    public const string ClientDataStore = "clientData.store";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EngineRpcRequest))]
[JsonSerializable(typeof(EngineRpcResponse))]
[JsonSerializable(typeof(EngineHello))]
public partial class EngineRpcJsonContext : JsonSerializerContext;
