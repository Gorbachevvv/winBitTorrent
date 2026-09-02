using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using WinBitTorrent.Core.EngineProtocol;

namespace WinBitTorrent.EngineHost;

internal sealed partial class EngineState : IAsyncDisposable
{
    private const int SchemaVersion = 1;
    private const string RestoreAppStateMethod = "engine.restoreAppState";
    private const string SaveResumeMethod = "engine.saveResume";
    private const string ApplySettingsMethod = "engine.applySettings";
    private readonly SqliteConnection _database;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private readonly SemaphoreSlim _nativeGate = new(1, 1);
    private readonly NativeEngine _native;
    private readonly string _dataRoot;

    private EngineState(SqliteConnection database, NativeEngine native, string dataRoot)
    {
        _database = database;
        _native = native;
        _dataRoot = dataRoot;
        Hello = new EngineHello("1.0", NativeEngine.Version, Environment.ProcessId, DateTimeOffset.Now);
    }

    public EngineHello Hello { get; }

    public static async Task<EngineState> OpenAsync(string dataRoot)
    {
        await LegacyQbittorrentMigrator.MigrateIfNeededAsync(dataRoot, CancellationToken.None).ConfigureAwait(false);
        var databasePath = Path.Combine(dataRoot, "engine.db");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);
                INSERT INTO schema_info(version)
                    SELECT $version WHERE NOT EXISTS (SELECT 1 FROM schema_info);
                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY NOT NULL,
                    value_json TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS client_data (
                    key TEXT PRIMARY KEY NOT NULL,
                    value_json TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS categories (
                    name TEXT PRIMARY KEY NOT NULL,
                    save_path TEXT NOT NULL DEFAULT '',
                    download_path TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE IF NOT EXISTS tags (name TEXT PRIMARY KEY NOT NULL);
                CREATE TABLE IF NOT EXISTS torrents (
                    hash TEXT PRIMARY KEY NOT NULL,
                    metadata BLOB,
                    resume_data BLOB,
                    app_state_json TEXT NOT NULL DEFAULT '{}'
                );
                CREATE TABLE IF NOT EXISTS rss_items (
                    path TEXT PRIMARY KEY NOT NULL,
                    kind INTEGER NOT NULL,
                    url TEXT NOT NULL DEFAULT '',
                    title TEXT NOT NULL DEFAULT '',
                    last_refresh INTEGER NOT NULL DEFAULT 0,
                    error TEXT NOT NULL DEFAULT ''
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ix_rss_items_url
                    ON rss_items(url) WHERE kind = 1;
                CREATE TABLE IF NOT EXISTS rss_articles (
                    feed_path TEXT NOT NULL,
                    article_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    link TEXT NOT NULL DEFAULT '',
                    download_url TEXT NOT NULL DEFAULT '',
                    description TEXT NOT NULL DEFAULT '',
                    published INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY(feed_path, article_id),
                    FOREIGN KEY(feed_path) REFERENCES rss_items(path) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS rss_rules (
                    name TEXT PRIMARY KEY NOT NULL,
                    definition_json TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS rss_downloads (
                    rule_name TEXT NOT NULL,
                    feed_path TEXT NOT NULL,
                    article_id TEXT NOT NULL,
                    created_at INTEGER NOT NULL,
                    PRIMARY KEY(rule_name, feed_path, article_id)
                );
                CREATE TABLE IF NOT EXISTS cookies (
                    domain TEXT NOT NULL,
                    path TEXT NOT NULL DEFAULT '/',
                    name TEXT NOT NULL,
                    value TEXT NOT NULL,
                    expires INTEGER NOT NULL DEFAULT 0,
                    secure INTEGER NOT NULL DEFAULT 0,
                    http_only INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY(domain, path, name)
                );
                CREATE TABLE IF NOT EXISTS engine_logs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp INTEGER NOT NULL,
                    type INTEGER NOT NULL,
                    message TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS peer_logs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp INTEGER NOT NULL,
                    ip TEXT NOT NULL,
                    blocked INTEGER NOT NULL,
                    reason TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE IF NOT EXISTS api_credentials (
                    kind TEXT PRIMARY KEY NOT NULL,
                    salt BLOB NOT NULL,
                    hash BLOB NOT NULL,
                    iterations INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS api_keys (
                    id TEXT PRIMARY KEY NOT NULL,
                    hash BLOB NOT NULL,
                    created_at INTEGER NOT NULL
                );
                """;
            command.Parameters.AddWithValue("$version", SchemaVersion);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        try
        {
            await HydrateResumeFilesFromDatabaseAsync(connection, dataRoot, force: false, CancellationToken.None).ConfigureAwait(false);
            var state = new EngineState(connection, NativeEngine.Open(dataRoot), dataRoot);
            await state.ImportLegacyStateAsync(CancellationToken.None).ConfigureAwait(false);
            await state.ImportLegacyRssAsync(CancellationToken.None).ConfigureAwait(false);
            state.InitializeSearchRuntime();
            state.InitializeGeoIp();
            var preferences = await state.LoadPreferencesAsync(CancellationToken.None).ConfigureAwait(false);
            state.InvokeNative(ApplySettingsMethod, preferences);
            ApplyProcessLimits(preferences);
            await state.RestoreNativeStateAsync(CancellationToken.None).ConfigureAwait(false);
            // Preserve the loaded resume snapshot until libtorrent has validated it. Saving here
            // can replace a valid pre-start snapshot with transient checking-resume state.
            await state.CaptureResumeStorageAsync(CancellationToken.None).ConfigureAwait(false);
            state.StartBackgroundServices();
            await state.AppendLogAsync(2, $"WinBitTorrent Engine {state.Hello.EngineVersion} started with libtorrent {state.Hello.LibtorrentVersion}.", CancellationToken.None).ConfigureAwait(false);
            return state;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonElement> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken)
    {
        try
        {
            return method switch
        {
            EngineRpcMethods.ApplicationVersion => EngineJson.Element("WinBitTorrent Engine 1.0"),
            EngineRpcMethods.ApplicationBuildInfo => EngineJson.Element(new
            {
                engine = Hello.EngineVersion,
                libtorrent = Hello.LibtorrentVersion,
                protocol = EngineRpcProtocol.Version
            }),
            EngineRpcMethods.ApplicationProcessInfo => EngineJson.Element(GetProcessInfo()),
            EngineRpcMethods.ApplicationGetPreferences => await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.ApplicationSetPreferences => await SetPreferencesAsync(payload, cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.ApplicationDefaultSavePath => EngineJson.Element(await GetSettingStringAsync("save_path", DefaultSavePath(), cancellationToken).ConfigureAwait(false)),
            EngineRpcMethods.ApplicationDirectoryContent => EngineJson.Element(GetDirectoryContent(payload)),
            EngineRpcMethods.ApplicationGetCookies => await GetCookiesAsync(cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.ApplicationSetCookies => await SetCookiesAsync(payload, cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.ApplicationRotateApiKey => await RotateRemoteApiKeyAsync(cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.ApplicationDeleteApiKey => await DeleteRemoteApiKeysAsync(cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.ApplicationChangeRemoteApiPassword => await ChangeRemoteApiPasswordAsync(payload, cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.ApplicationDeleteMigrationBackup => await DeleteMigrationBackupAsync(cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.SyncTorrentPeers => await GetTorrentPeersWithGeoIpAsync(payload, cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.TorrentsAdd => await AddTorrentsAsync(payload, cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.TorrentsDelete or EngineRpcMethods.TorrentsCommand or EngineRpcMethods.TorrentsAction or EngineRpcMethods.TransferBanPeers
                => await InvokeNativeMutationAsync(method, payload, cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.TorrentsExport => ExportTorrent(payload),
            EngineRpcMethods.TorrentsFetchMetadata => await FetchMetadataAsync(payload, cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.LogsMain => await GetLogsAsync(payload, cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.LogsPeers => await GetPeerLogsAsync(payload, cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.RssItems => await GetRssItemsAsync(payload, cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.RssRules => await GetRssRulesAsync(cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.RssMatchingArticles => await GetRssMatchingArticlesAsync(payload, cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.RssAction => await HandleRssActionAsync(payload, cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.SearchStart => await StartSearchAsync(payload, cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.SearchStatus => GetSearchStatus(payload),
            EngineRpcMethods.SearchResults => GetSearchResults(payload),
            EngineRpcMethods.SearchPlugins => await GetSearchPluginsAsync(cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.SearchAction => await HandleSearchActionAsync(payload, cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.CreatorAdd => StartCreatorTask(payload),
            EngineRpcMethods.CreatorStatus => GetCreatorStatus(payload),
            EngineRpcMethods.CreatorFile => GetCreatorFile(payload),
            EngineRpcMethods.CreatorDelete => DeleteCreatorTask(payload),
            EngineRpcMethods.ClientDataLoad => await LoadClientDataAsync(payload, cancellationToken).ConfigureAwait(false),
            EngineRpcMethods.ClientDataStore => await StoreClientDataAsync(payload, cancellationToken).ConfigureAwait(false),
            _ when IsNativeMethod(method) => InvokeNative(method, payload),
            _ => throw new NotSupportedException($"Engine method '{method}' is not implemented.")
        };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AppendLogAsync(8, $"{method}: {exception.Message}", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsNativeMethod(string method)
        => method.StartsWith("sync.", StringComparison.Ordinal)
            || method.StartsWith("transfer.", StringComparison.Ordinal)
            || method.StartsWith("torrents.", StringComparison.Ordinal);

    private JsonElement ExportTorrent(JsonElement payload)
    {
        var native = InvokeNative(EngineRpcMethods.TorrentsExport, payload);
        return EngineJson.Element(native.EnumerateArray().Select(static value => value.GetByte()).ToArray());
    }

    private async Task<JsonElement> InvokeNativeMutationAsync(string method, JsonElement payload, CancellationToken cancellationToken)
    {
        var result = InvokeNative(method, payload);
        await PersistNativeStateAsync(cancellationToken).ConfigureAwait(false);
        if (method == EngineRpcMethods.TransferBanPeers && payload.TryGetProperty("peers", out var peers))
        {
            var preferences = await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
            var banned = (preferences.TryGetProperty("banned_IPs", out var stored) ? stored.GetString() : string.Empty)
                ?.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var peer in peers.EnumerateArray())
            {
                var value = peer.GetString() ?? string.Empty;
                await AppendPeerLogAsync(value, blocked: true, "Banned by user", cancellationToken).ConfigureAwait(false);
                banned.Add(StripPeerPort(value));
            }
            await StoreMapAsync("settings", EngineJson.Element(new { banned_IPs = string.Join(Environment.NewLine, banned) }), cancellationToken).ConfigureAwait(false);
        }
        await AppendLogAsync(1, $"Executed {method}.", cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static string StripPeerPort(string value)
    {
        value = value.Trim();
        if (value.StartsWith('[') && value.IndexOf(']') is var close and > 0) return value[1..close];
        var colon = value.LastIndexOf(':');
        return colon > 0 && value.IndexOf(':') == colon && int.TryParse(value[(colon + 1)..], out _) ? value[..colon] : value;
    }

    private async Task<JsonElement> AddTorrentsAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var node = JsonNode.Parse(payload.GetRawText())?.AsObject() ?? throw new ArgumentException("Torrent add payload must be an object.");
        await ApplyTorrentAddPathsAsync(node, cancellationToken).ConfigureAwait(false);
        payload = EngineJson.Element(node);
        if (!payload.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
            return await InvokeNativeMutationAsync(EngineRpcMethods.TorrentsAdd, payload, cancellationToken).ConfigureAwait(false);

        var remoteUrls = urls.EnumerateArray()
            .Select(static value => value.GetString())
            .Where(static value => value is not null && !value.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .ToArray();
        if (remoteUrls.Length == 0)
            return await InvokeNativeMutationAsync(EngineRpcMethods.TorrentsAdd, payload, cancellationToken).ConfigureAwait(false);

        var stagingRoot = Path.Combine(_dataRoot, "staging");
        Directory.CreateDirectory(stagingRoot);
        var staged = new List<string>();
        try
        {
            using var client = await CreateHttpClientAsync("proxy_bittorrent", cancellationToken).ConfigureAwait(false);
            foreach (var url in remoteUrls)
            {
                var bytes = await client.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                var path = Path.Combine(stagingRoot, $"{Guid.NewGuid():N}.torrent");
                await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                staged.Add(path);
            }

            var torrentFiles = node["torrentFiles"] as JsonArray ?? [];
            foreach (var path in staged)
                torrentFiles.Add(path);
            node["torrentFiles"] = torrentFiles;
            node["urls"] = new JsonArray(urls.EnumerateArray()
                .Select(static value => value.GetString())
                .Where(static value => value?.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) == true)
                .Select(static value => (JsonNode?)value)
                .ToArray());
            return await InvokeNativeMutationAsync(
                EngineRpcMethods.TorrentsAdd, EngineJson.Element(node), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var path in staged)
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }
    }

    private async Task ApplyTorrentAddPathsAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var preferences = await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
        var automatic = request["automaticTorrentManagement"]?.GetValue<bool>() == true
            || preferences.TryGetProperty("auto_tmm_enabled", out var autoTmm) && autoTmm.GetBoolean();
        var category = request["category"]?.GetValue<string>() ?? string.Empty;
        string? categorySavePath = null;
        string? categoryDownloadPath = null;
        if (automatic && category.Length != 0)
        {
            await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var command = _database.CreateCommand();
                command.CommandText = "SELECT save_path, download_path FROM categories WHERE name=$name";
                command.Parameters.AddWithValue("$name", category);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    categorySavePath = reader.GetString(0);
                    categoryDownloadPath = reader.GetString(1);
                }
            }
            finally
            {
                _databaseGate.Release();
            }
        }
        if (string.IsNullOrWhiteSpace(request["savePath"]?.GetValue<string>()))
            request["savePath"] = !string.IsNullOrWhiteSpace(categorySavePath) ? categorySavePath : preferences.GetProperty("save_path").GetString();
        if (string.IsNullOrWhiteSpace(request["downloadPath"]?.GetValue<string>()))
        {
            if (!string.IsNullOrWhiteSpace(categoryDownloadPath)) request["downloadPath"] = categoryDownloadPath;
            else if (preferences.TryGetProperty("temp_path_enabled", out var tempEnabled) && tempEnabled.GetBoolean())
                request["downloadPath"] = preferences.GetProperty("temp_path").GetString();
        }
        request["useDownloadPath"] = !string.IsNullOrWhiteSpace(request["downloadPath"]?.GetValue<string>());
    }

    private async Task<JsonElement> FetchMetadataAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var url = payload.GetProperty("url").GetString() ?? throw new ArgumentException("url is required.");
        if (url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            return await FetchMagnetMetadataAsync(url, cancellationToken).ConfigureAwait(false);

        var stagingRoot = Path.Combine(_dataRoot, "staging");
        Directory.CreateDirectory(stagingRoot);
        var path = Path.Combine(stagingRoot, $"{Guid.NewGuid():N}.torrent");
        try
        {
            using var client = await CreateHttpClientAsync("proxy_bittorrent", cancellationToken).ConfigureAwait(false);
            var bytes = await client.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return InvokeNative(EngineRpcMethods.TorrentsParseMetadata, EngineJson.Element(new { torrentFilePath = path }));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private async Task<JsonElement> FetchMagnetMetadataAsync(string magnet, CancellationToken cancellationToken)
    {
        var before = InvokeNative(EngineRpcMethods.TorrentsInfo, EngineJson.Element(new { filter = "all" }))
            .EnumerateArray().Select(static value => value.GetProperty("hash").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stagingRoot = Path.Combine(_dataRoot, "staging", "metadata", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        string? hash = null;
        try
        {
            InvokeNative(EngineRpcMethods.TorrentsAdd, EngineJson.Element(new
            {
                urls = new[] { magnet }, torrentFiles = Array.Empty<string>(), savePath = stagingRoot,
                startTorrent = true, automaticTorrentManagement = false
            }));
            var after = InvokeNative(EngineRpcMethods.TorrentsInfo, EngineJson.Element(new { filter = "all" }));
            hash = after.EnumerateArray().Select(static value => value.GetProperty("hash").GetString()!).FirstOrDefault(value => !before.Contains(value));
            if (hash is null) throw new InvalidOperationException("The magnet is already present in the torrent list.");
            for (var attempt = 0; attempt < 120; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { return InvokeNative(EngineRpcMethods.TorrentsMetadata, EngineJson.Element(new { hash })); }
                catch (InvalidOperationException exception) when (exception.Message.Contains("not available", StringComparison.OrdinalIgnoreCase)) { }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            throw new TimeoutException("Timed out while waiting for magnet metadata.");
        }
        finally
        {
            if (hash is not null)
            {
                try { InvokeNative(EngineRpcMethods.TorrentsDelete, EngineJson.Element(new { hashes = hash, deleteFiles = true })); } catch { }
                await PersistNativeStateAsync(CancellationToken.None).ConfigureAwait(false);
            }
            try { Directory.Delete(stagingRoot, recursive: true); } catch (IOException) { }
        }
    }

    private async Task RestoreNativeStateAsync(CancellationToken cancellationToken)
    {
        var torrents = new JsonArray();
        var categories = new JsonArray();
        var tags = new JsonArray();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var command = _database.CreateCommand())
            {
                command.CommandText = "SELECT app_state_json FROM torrents ORDER BY hash";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    torrents.Add(JsonNode.Parse(reader.GetString(0)));
            }
            await using (var command = _database.CreateCommand())
            {
                command.CommandText = "SELECT name, save_path, download_path FROM categories ORDER BY name";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    categories.Add(new JsonObject
                    {
                        ["name"] = reader.GetString(0),
                        ["savePath"] = reader.GetString(1),
                        ["downloadPath"] = reader.GetString(2)
                    });
            }
            await using (var command = _database.CreateCommand())
            {
                command.CommandText = "SELECT name FROM tags ORDER BY name";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    tags.Add(reader.GetString(0));
            }
        }
        finally
        {
            _databaseGate.Release();
        }
        InvokeNative(RestoreAppStateMethod, EngineJson.Element(new { torrents, categories, tags }));
    }

    private async Task ImportLegacyStateAsync(CancellationToken cancellationToken)
    {
        var importPath = Path.Combine(_dataRoot, "legacy-import.json");
        if (!File.Exists(importPath)) return;
        var import = JsonNode.Parse(await File.ReadAllTextAsync(importPath, cancellationToken).ConfigureAwait(false))?.AsObject()
            ?? throw new InvalidDataException("The legacy migration import document is invalid.");

        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var alreadyImported = _database.CreateCommand())
            {
                alreadyImported.CommandText = "SELECT 1 FROM settings WHERE key='migration_import_version'";
                if (await alreadyImported.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    return;
            }

            await using var transaction = await _database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (import["settings"] is JsonObject settings)
            {
                foreach (var (key, value) in settings)
                {
                    await using var command = _database.CreateCommand();
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = "INSERT INTO settings(key, value_json) VALUES($key, $value) ON CONFLICT(key) DO NOTHING";
                    command.Parameters.AddWithValue("$key", key);
                    command.Parameters.AddWithValue("$value", value?.ToJsonString(EngineJson.Options) ?? "null");
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            if (import["categories"] is JsonObject categories)
            {
                foreach (var (name, value) in categories)
                {
                    var category = value as JsonObject;
                    await using var command = _database.CreateCommand();
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = "INSERT INTO categories(name, save_path, download_path) VALUES($name, $save, $download) ON CONFLICT(name) DO NOTHING";
                    command.Parameters.AddWithValue("$name", name);
                    command.Parameters.AddWithValue("$save", category?["savePath"]?.GetValue<string>() ?? string.Empty);
                    command.Parameters.AddWithValue("$download", category?["downloadPath"]?.GetValue<string>() ?? string.Empty);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            if (import["tags"] is JsonArray tags)
            {
                foreach (var tag in tags)
                {
                    await using var command = _database.CreateCommand();
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = "INSERT INTO tags(name) VALUES($name) ON CONFLICT(name) DO NOTHING";
                    command.Parameters.AddWithValue("$name", tag!.GetValue<string>());
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            if (import["torrents"] is JsonArray torrents)
            {
                foreach (var torrent in torrents.OfType<JsonObject>())
                {
                    await using var command = _database.CreateCommand();
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = "INSERT INTO torrents(hash, app_state_json) VALUES($hash, $state) ON CONFLICT(hash) DO UPDATE SET app_state_json=excluded.app_state_json";
                    command.Parameters.AddWithValue("$hash", torrent["hash"]!.GetValue<string>());
                    command.Parameters.AddWithValue("$state", torrent.ToJsonString(EngineJson.Options));
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            var migrationReport = new JsonObject
            {
                ["pending"] = true,
                ["sourceKind"] = import["sourceKind"]?.DeepClone(),
                ["backupPath"] = import["backupPath"]?.DeepClone(),
                ["torrentCount"] = import["expectedHashes"] is JsonArray expectedHashes ? expectedHashes.Count : 0,
                ["needsHashCheck"] = import["needsRecheck"]?.DeepClone() ?? new JsonArray()
            };
            await using (var command = _database.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "INSERT INTO client_data(key, value_json) VALUES('migration.report', $value) ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json";
                command.Parameters.AddWithValue("$value", migrationReport.ToJsonString(EngineJson.Options));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var command = _database.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "INSERT INTO settings(key, value_json) VALUES('migration_import_version', '1')";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task PersistNativeStateAsync(CancellationToken cancellationToken)
    {
        var torrents = InvokeNative(EngineRpcMethods.TorrentsInfo, EngineJson.Element(new { filter = "all" }));
        var mainData = InvokeNative(EngineRpcMethods.SyncMainData, EngineJson.Element(new { responseId = 0 }));

        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resumeBlobs = new Dictionary<string, (byte[]? Metadata, byte[]? Resume)>(StringComparer.OrdinalIgnoreCase);
            await using (var existing = _database.CreateCommand())
            {
                existing.CommandText = "SELECT hash, metadata, resume_data FROM torrents";
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    resumeBlobs[reader.GetString(0)] = (reader.IsDBNull(1) ? null : (byte[])reader[1], reader.IsDBNull(2) ? null : (byte[])reader[2]);
            }
            await using var transaction = await _database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync("DELETE FROM torrents", transaction, cancellationToken).ConfigureAwait(false);
            foreach (var torrent in torrents.EnumerateArray())
            {
                var appState = new JsonObject
                {
                    ["hash"] = torrent.GetProperty("hash").GetString(),
                    ["category"] = torrent.GetProperty("category").GetString(),
                    ["tags"] = torrent.GetProperty("tags").GetString(),
                    ["displayName"] = torrent.GetProperty("display_name").GetString(),
                    ["savePath"] = torrent.GetProperty("save_path").GetString(),
                    ["downloadPath"] = torrent.GetProperty("download_path").GetString(),
                    ["firstLast"] = torrent.GetProperty("f_l_piece_prio").GetBoolean(),
                    ["forceStart"] = torrent.GetProperty("force_start").GetBoolean(),
                    ["automaticTmm"] = torrent.GetProperty("auto_tmm").GetBoolean(),
                    ["ratioLimit"] = torrent.GetProperty("ratio_limit").GetDouble(),
                    ["seedingTimeLimit"] = torrent.GetProperty("seeding_time_limit").GetInt32(),
                    ["inactiveSeedingTimeLimit"] = torrent.GetProperty("inactive_seeding_time_limit").GetInt32(),
                    ["queuePosition"] = torrent.GetProperty("priority").GetInt32()
                };
                await using var command = _database.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "INSERT INTO torrents(hash, metadata, resume_data, app_state_json) VALUES($hash, $metadata, $resume, $state)";
                var hash = torrent.GetProperty("hash").GetString()!;
                command.Parameters.AddWithValue("$hash", hash);
                var blobs = resumeBlobs.GetValueOrDefault(hash);
                command.Parameters.AddWithValue("$metadata", (object?)blobs.Metadata ?? DBNull.Value);
                command.Parameters.AddWithValue("$resume", (object?)blobs.Resume ?? DBNull.Value);
                command.Parameters.AddWithValue("$state", appState.ToJsonString(EngineJson.Options));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync("DELETE FROM categories", transaction, cancellationToken).ConfigureAwait(false);
            if (mainData.TryGetProperty("categories", out var categoryValues))
            {
                foreach (var category in categoryValues.EnumerateObject())
                {
                    await using var command = _database.CreateCommand();
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = "INSERT INTO categories(name, save_path, download_path) VALUES($name, $save, $download)";
                    command.Parameters.AddWithValue("$name", category.Name);
                    command.Parameters.AddWithValue("$save", category.Value.GetProperty("savePath").GetString() ?? string.Empty);
                    command.Parameters.AddWithValue("$download", category.Value.GetProperty("downloadPath").GetString() ?? string.Empty);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await ExecuteAsync("DELETE FROM tags", transaction, cancellationToken).ConfigureAwait(false);
            if (mainData.TryGetProperty("tags", out var tagValues))
            {
                foreach (var tag in tagValues.EnumerateArray())
                {
                    await using var command = _database.CreateCommand();
                    command.Transaction = (SqliteTransaction)transaction;
                    command.CommandText = "INSERT INTO tags(name) VALUES($name)";
                    command.Parameters.AddWithValue("$name", tag.GetString()!);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task ExecuteAsync(string sql, System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = _database.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static object GetProcessInfo()
    {
        using var process = Process.GetCurrentProcess();
        return new
        {
            pid = process.Id,
            memory_working_set = process.WorkingSet64,
            memory_private = process.PrivateMemorySize64,
            started_on = new DateTimeOffset(process.StartTime).ToUnixTimeSeconds()
        };
    }

    private async Task<JsonElement> LoadPreferencesAsync(CancellationToken cancellationToken)
    {
        var result = DefaultPreferences();
        var stored = await LoadMapAsync("settings", cancellationToken).ConfigureAwait(false);
        foreach (var property in stored.EnumerateObject())
            result[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        return EngineJson.Element(result);
    }

    private async Task<JsonElement> SetPreferencesAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new ArgumentException("A JSON object is required.");
        var previous = await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
        var prospectiveNode = JsonNode.Parse(previous.GetRawText())!.AsObject();
        foreach (var property in payload.EnumerateObject())
            prospectiveNode[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        var prospective = EngineJson.Element(prospectiveNode);
        var previousStorage = previous.GetProperty("resume_data_storage_type").GetString() ?? "SQLite";
        var currentStorage = prospective.GetProperty("resume_data_storage_type").GetString() ?? "SQLite";
        var storageChanged = !string.Equals(previousStorage, currentStorage, StringComparison.OrdinalIgnoreCase);
        try
        {
            InvokeNative(ApplySettingsMethod, prospective);
            ApplyProcessLimits(prospective);
            if (storageChanged)
            {
                InvokeNative(SaveResumeMethod, EngineJson.EmptyObject);
                if (currentStorage.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
                    await CaptureResumeStorageAsync(cancellationToken, force: true).ConfigureAwait(false);
            }
            return await StoreMapAsync("settings", payload, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                InvokeNative(ApplySettingsMethod, previous);
                ApplyProcessLimits(previous);
                if (storageChanged)
                {
                    if (previousStorage.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
                    {
                        // The failed transition to Legacy already produced fresh files. Capture
                        // them back into SQLite before cleaning up instead of discarding the
                        // newest resume snapshot during rollback.
                        await CaptureResumeStorageAsync(CancellationToken.None, force: true).ConfigureAwait(false);
                    }
                    else
                    {
                        await HydrateResumeFilesFromDatabaseAsync(_database, _dataRoot, force: true, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            catch { }
            throw;
        }
    }

    private static JsonObject DefaultPreferences() => new()
    {
        ["save_path"] = DefaultSavePath(),
        ["temp_path_enabled"] = false,
        ["temp_path"] = Path.Combine(DefaultSavePath(), "Incomplete"),
        ["create_subfolder_enabled"] = true,
        ["auto_tmm_enabled"] = false,
        ["preallocate_all"] = false,
        ["bittorrent_protocol"] = 0,
        ["listen_port"] = 0,
        ["random_port"] = true,
        ["upnp"] = true,
        ["max_connec"] = -1,
        ["max_connec_per_torrent"] = -1,
        ["max_uploads"] = -1,
        ["max_uploads_per_torrent"] = -1,
        ["proxy_type"] = "None",
        ["proxy_ip"] = string.Empty,
        ["proxy_port"] = 0,
        ["proxy_hostname_lookup"] = false,
        ["proxy_auth_enabled"] = false,
        ["proxy_username"] = string.Empty,
        ["proxy_password"] = string.Empty,
        ["proxy_bittorrent"] = true,
        ["proxy_peer_connections"] = false,
        ["proxy_rss"] = true,
        ["proxy_misc"] = true,
        ["i2p_enabled"] = false,
        ["i2p_address"] = "127.0.0.1",
        ["i2p_port"] = 7656,
        ["i2p_mixed_mode"] = false,
        ["ip_filter_enabled"] = false,
        ["ip_filter_path"] = string.Empty,
        ["ip_filter_trackers"] = false,
        ["banned_IPs"] = string.Empty,
        ["dl_limit"] = 0,
        ["up_limit"] = 0,
        ["alt_dl_limit"] = 0,
        ["alt_up_limit"] = 0,
        ["use_alt_speed_limits"] = false,
        ["scheduler_enabled"] = false,
        ["schedule_from_hour"] = 8,
        ["schedule_from_min"] = 0,
        ["schedule_to_hour"] = 20,
        ["schedule_to_min"] = 0,
        ["scheduler_days"] = 0,
        ["dht"] = true,
        ["pex"] = true,
        ["lsd"] = true,
        ["encryption"] = 0,
        ["queueing_enabled"] = false,
        ["max_active_downloads"] = 3,
        ["max_active_uploads"] = 3,
        ["search_enabled"] = true,
        ["python_executable_path"] = string.Empty,
        ["rss_processing_enabled"] = true,
        ["rss_refresh_interval"] = 30,
        ["rss_max_articles_per_feed"] = 50,
        ["rss_auto_downloading_enabled"] = true,
        ["web_ui_address"] = "127.0.0.1",
        ["web_ui_port"] = 0,
        ["web_ui_username"] = "admin",
        ["web_ui_upnp"] = false,
        ["web_ui_csrf_protection_enabled"] = true,
        ["web_ui_external_enabled"] = false,
        ["web_ui_https_enabled"] = false,
        ["web_ui_https_certificate_path"] = string.Empty,
        ["resume_data_storage_type"] = "SQLite",
        ["memory_working_set_limit"] = 0,
        ["disk_cache"] = -1,
        ["async_io_threads"] = 4,
        ["recheck_completed_torrents"] = false,
        ["anonymous_mode"] = false
    };

    private static JsonArray GetDirectoryContent(JsonElement payload)
    {
        var path = payload.TryGetProperty("path", out var value) ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return [];
        var result = new JsonArray();
        foreach (var entry in Directory.EnumerateFileSystemEntries(path).Take(10_000))
        {
            var isDirectory = Directory.Exists(entry);
            result.Add(new JsonObject
            {
                ["name"] = Path.GetFileName(entry),
                ["path"] = entry,
                ["type"] = isDirectory ? "directory" : "file"
            });
        }
        return result;
    }

    private async Task<JsonElement> LoadMapAsync(string table, CancellationToken cancellationToken)
    {
        var result = new JsonObject();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = $"SELECT key, value_json FROM {table}";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                result[reader.GetString(0)] = JsonNode.Parse(reader.GetString(1));
        }
        finally
        {
            _databaseGate.Release();
        }
        return EngineJson.Element(result);
    }

    private async Task<JsonElement> StoreMapAsync(string table, JsonElement payload, CancellationToken cancellationToken)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A JSON object is required.");

        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = await _database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var property in payload.EnumerateObject())
            {
                await using var command = _database.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = $"INSERT INTO {table}(key, value_json) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json";
                command.Parameters.AddWithValue("$key", property.Name);
                command.Parameters.AddWithValue("$value", property.Value.GetRawText());
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
        return EngineJson.EmptyObject;
    }

    private async Task<string> GetSettingStringAsync(string key, string fallback, CancellationToken cancellationToken)
    {
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "SELECT value_json FROM settings WHERE key=$key";
            command.Parameters.AddWithValue("$key", key);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            return value is null ? fallback : JsonSerializer.Deserialize<string>(value) ?? fallback;
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task<JsonElement> LoadClientDataAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var key = payload.GetProperty("key").GetString() ?? throw new ArgumentException("key is required.");
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "SELECT value_json FROM client_data WHERE key=$key";
            command.Parameters.AddWithValue("$key", key);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            return value is null ? EngineJson.EmptyObject : JsonDocument.Parse(value).RootElement.Clone();
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private Task<JsonElement> StoreClientDataAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var key = payload.GetProperty("key").GetString() ?? throw new ArgumentException("key is required.");
        var value = payload.GetProperty("value");
        return StoreMapAsync("client_data", EngineJson.Element(new Dictionary<string, JsonElement> { [key] = value }), cancellationToken);
    }

    private static string DefaultSavePath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private JsonElement InvokeNative(string method, JsonElement payload)
    {
        _nativeGate.Wait();
        try { return _native.Invoke(method, payload); }
        finally { _nativeGate.Release(); }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        InvokeNative(SaveResumeMethod, EngineJson.EmptyObject);
        await CaptureResumeStorageAsync(cancellationToken).ConfigureAwait(false);
        await PersistNativeStateAsync(cancellationToken).ConfigureAwait(false);
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopBackgroundServicesAsync().ConfigureAwait(false);
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        await _database.DisposeAsync().ConfigureAwait(false);
        _geoIpReader?.Dispose();
        _native.Dispose();
        _nativeGate.Dispose();
        _databaseGate.Dispose();
    }
}
