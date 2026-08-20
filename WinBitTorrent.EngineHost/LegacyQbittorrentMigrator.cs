using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using WinBitTorrent.Core.EngineProtocol;

namespace WinBitTorrent.EngineHost;

internal static class LegacyQbittorrentMigrator
{
    private const int MigrationVersion = 1;

    public static async Task MigrateIfNeededAsync(string engineRoot, CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(engineRoot, "migration.json");
        if (File.Exists(markerPath))
            return;

        var applicationRoot = Directory.GetParent(engineRoot)?.FullName
            ?? throw new InvalidOperationException("The engine data directory has no parent.");
        var legacyRoot = Path.Combine(applicationRoot, "Backend", "Profile", "qBittorrent");
        var legacyData = Path.Combine(legacyRoot, "data");
        var fileStorage = Path.Combine(legacyData, "BT_backup");
        var databaseStorage = Path.Combine(legacyData, "torrents.db");
        if (!Directory.Exists(legacyRoot)
            || (!Directory.Exists(fileStorage) && !File.Exists(databaseStorage)))
            return;

        Directory.CreateDirectory(engineRoot);
        await using var migrationLock = new FileStream(
            Path.Combine(engineRoot, "migration.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);

        if (File.Exists(markerPath))
            return;
        if (Process.GetProcessesByName("qbittorrent-nox").Any(static process => !process.HasExited)
            || Process.GetProcessesByName("qbittorrent").Any(static process => !process.HasExited))
        {
            throw new InvalidOperationException("qBittorrent must be stopped before its local profile can be migrated.");
        }

        EnsureActivationTargetIsEmpty(Path.Combine(engineRoot, "torrents"));
        EnsureActivationTargetIsEmpty(Path.Combine(engineRoot, "resume"));

        var backupRoot = await CreateImmutableBackupAsync(engineRoot, legacyRoot, cancellationToken).ConfigureAwait(false);
        var stagingRoot = Path.Combine(engineRoot, $"migration-staging-{Guid.NewGuid():N}");
        var stagingTorrents = Path.Combine(stagingRoot, "torrents");
        var stagingResume = Path.Combine(stagingRoot, "resume");
        Directory.CreateDirectory(stagingTorrents);
        Directory.CreateDirectory(stagingResume);

        try
        {
            var configPath = Path.Combine(legacyRoot, "config", "qBittorrent.ini");
            var storageType = ReadIni(configPath).GetValueOrDefault("BitTorrent/Session\\ResumeDataStorageType");
            var useDatabase = File.Exists(databaseStorage)
                && string.Equals(storageType, "SQLite", StringComparison.OrdinalIgnoreCase);
            var import = useDatabase
                ? await StageDatabaseAsync(databaseStorage, stagingRoot, cancellationToken).ConfigureAwait(false)
                : await StageFilesAsync(fileStorage, stagingRoot, cancellationToken).ConfigureAwait(false);

            import["categories"] = ReadLegacyCategories(Path.Combine(legacyRoot, "config", "categories.json"));
            import["settings"] = MapLegacySettings(configPath);
            import["settings"]!["resume_data_storage_type"] = useDatabase ? "SQLite" : "Legacy";
            import["tags"] = new JsonArray(SplitCsv(
                ReadIni(configPath).GetValueOrDefault("BitTorrent/Session\\Tags") ?? string.Empty)
                .Select(static value => (JsonNode?)value).ToArray());
            import["sourceKind"] = useDatabase ? "SQLiteV9" : "FastresumeFiles";
            import["backupPath"] = backupRoot;

            CopyDirectoryIfPresent(Path.Combine(legacyRoot, "config", "rss"), Path.Combine(stagingRoot, "LegacyRss", "config"));
            CopyDirectoryIfPresent(Path.Combine(legacyData, "rss"), Path.Combine(stagingRoot, "LegacyRss", "data"));
            CopyDirectoryIfPresent(Path.Combine(legacyData, "nova3"), Path.Combine(stagingRoot, "SearchPlugins", "nova3"));
            CopyDirectoryIfPresent(Path.Combine(legacyData, "GeoDB"), Path.Combine(stagingRoot, "GeoDB"));

            var expectedHashes = import["expectedHashes"]!.AsArray()
                .Select(static value => value!.GetValue<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            using (var preflight = NativeEngine.Open(stagingRoot))
            {
                var snapshot = preflight.Invoke(EngineRpcMethods.TorrentsInfo, EngineJson.Element(new { filter = "all" }));
                var matched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var snapshots = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var torrent in snapshot.EnumerateArray())
                {
                    var canonical = torrent.GetProperty("hash").GetString()!;
                    var sourceHash = TorrentIdentifiers(torrent).FirstOrDefault(expectedHashes.Contains);
                    if (sourceHash is not null)
                    {
                        matched[sourceHash] = canonical;
                        snapshots[sourceHash] = torrent.Clone();
                    }
                }
                if (matched.Count != expectedHashes.Count)
                {
                    var missing = expectedHashes.Except(matched.Keys, StringComparer.OrdinalIgnoreCase);
                    throw new InvalidDataException($"Migration preflight failed. Missing torrents: {string.Join(", ", missing)}");
                }
                PopulateImportedTorrentState(import, stagingRoot, matched, snapshots);
                CanonicalizeStagedFiles(stagingRoot, matched);
            }

            await File.WriteAllTextAsync(
                Path.Combine(stagingRoot, "legacy-import.json"),
                import.ToJsonString(EngineJson.Options),
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);

            ActivateDirectory(stagingTorrents, Path.Combine(engineRoot, "torrents"));
            ActivateDirectory(stagingResume, Path.Combine(engineRoot, "resume"));
            ActivateDirectoryIfPresent(Path.Combine(stagingRoot, "LegacyRss"), Path.Combine(engineRoot, "LegacyRss"));
            ActivateDirectoryIfPresent(Path.Combine(stagingRoot, "SearchPlugins"), Path.Combine(engineRoot, "SearchPlugins"));
            ActivateDirectoryIfPresent(Path.Combine(stagingRoot, "GeoDB"), Path.Combine(engineRoot, "GeoDB"));
            File.Move(Path.Combine(stagingRoot, "legacy-import.json"), Path.Combine(engineRoot, "legacy-import.json"));

            var marker = new JsonObject
            {
                ["version"] = MigrationVersion,
                ["completedAt"] = DateTimeOffset.UtcNow,
                ["source"] = legacyRoot,
                ["sourceKind"] = import["sourceKind"]!.DeepClone(),
                ["backupPath"] = backupRoot,
                ["torrentCount"] = expectedHashes.Count
            };
            var temporaryMarker = markerPath + ".tmp";
            await File.WriteAllTextAsync(temporaryMarker, marker.ToJsonString(EngineJson.Options), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryMarker, markerPath);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);
        }
    }

    private static async Task<JsonObject> StageFilesAsync(string source, string stagingRoot, CancellationToken cancellationToken)
    {
        var torrentsRoot = Path.Combine(stagingRoot, "torrents");
        var resumeRoot = Path.Combine(stagingRoot, "resume");
        var hashes = new JsonArray();
        var corrupt = new JsonArray();
        if (!Directory.Exists(source))
            return new JsonObject { ["expectedHashes"] = hashes, ["torrents"] = new JsonArray(), ["needsRecheck"] = corrupt };

        foreach (var torrentFile in Directory.EnumerateFiles(source, "*.torrent", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = Path.GetFileNameWithoutExtension(torrentFile).ToLowerInvariant();
            if (!IsTorrentId(hash))
                continue;
            File.Copy(torrentFile, Path.Combine(torrentsRoot, hash + ".torrent"), overwrite: false);
            var resumeFile = Path.Combine(source, hash + ".fastresume");
            if (File.Exists(resumeFile))
                File.Copy(resumeFile, Path.Combine(resumeRoot, hash + ".fastresume"), overwrite: false);
            else
                corrupt.Add(hash);
            hashes.Add(hash);
        }
        await Task.CompletedTask;
        return new JsonObject { ["expectedHashes"] = hashes, ["torrents"] = new JsonArray(), ["needsRecheck"] = corrupt };
    }

    private static async Task<JsonObject> StageDatabaseAsync(string databasePath, string stagingRoot, CancellationToken cancellationToken)
    {
        var torrentsRoot = Path.Combine(stagingRoot, "torrents");
        var resumeRoot = Path.Combine(stagingRoot, "resume");
        var hashes = new JsonArray();
        var torrents = new JsonArray();
        await using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var versionCommand = database.CreateCommand())
        {
            versionCommand.CommandText = "SELECT value FROM meta WHERE name='version'";
            var rawVersion = await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (Convert.ToInt32(rawVersion, System.Globalization.CultureInfo.InvariantCulture) != 9)
                throw new InvalidDataException("Only qBittorrent SQLite resume schema v9 is supported.");
        }

        await using var command = database.CreateCommand();
        command.CommandText = "SELECT * FROM torrents ORDER BY queue_position";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var hash = ReadText(reader, "torrent_id").ToLowerInvariant();
            if (!IsTorrentId(hash))
                throw new InvalidDataException($"Invalid torrent id in qBittorrent database: {hash}");
            var resume = (byte[])reader["libtorrent_resume_data"];
            await File.WriteAllBytesAsync(Path.Combine(resumeRoot, hash + ".fastresume"), resume, cancellationToken).ConfigureAwait(false);
            if (reader["metadata"] is byte[] { Length: > 0 } metadata)
                await File.WriteAllBytesAsync(Path.Combine(torrentsRoot, hash + ".torrent"), metadata, cancellationToken).ConfigureAwait(false);
            hashes.Add(hash);
            var operatingMode = ReadText(reader, "operating_mode");
            torrents.Add(new JsonObject
            {
                ["hash"] = hash,
                ["category"] = ReadText(reader, "category"),
                ["tags"] = ReadText(reader, "tags"),
                ["displayName"] = ReadText(reader, "name"),
                ["downloadPath"] = ReadText(reader, "download_path"),
                ["firstLast"] = ReadBoolean(reader, "has_outer_pieces_priority"),
                ["forceStart"] = string.Equals(operatingMode, "Forced", StringComparison.OrdinalIgnoreCase),
                ["automaticTmm"] = string.Equals(operatingMode, "AutoManaged", StringComparison.OrdinalIgnoreCase),
                ["ratioLimit"] = Convert.ToInt32(reader["ratio_limit"], System.Globalization.CultureInfo.InvariantCulture) / 1000.0,
                ["seedingTimeLimit"] = Convert.ToInt32(reader["seeding_time_limit"], System.Globalization.CultureInfo.InvariantCulture),
                ["inactiveSeedingTimeLimit"] = Convert.ToInt32(reader["inactive_seeding_time_limit"], System.Globalization.CultureInfo.InvariantCulture),
                ["queuePosition"] = Convert.ToInt32(reader["queue_position"], System.Globalization.CultureInfo.InvariantCulture)
            });
        }
        return new JsonObject { ["expectedHashes"] = hashes, ["torrents"] = torrents, ["needsRecheck"] = new JsonArray() };
    }

    private static async Task<string> CreateImmutableBackupAsync(string engineRoot, string legacyRoot, CancellationToken cancellationToken)
    {
        var backupsRoot = Path.Combine(engineRoot, "Backups");
        Directory.CreateDirectory(backupsRoot);
        var backupRoot = Path.Combine(backupsRoot, $"qBittorrent-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupRoot);
        var manifestFiles = new JsonArray();
        foreach (var source in Directory.EnumerateFiles(legacyRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(legacyRoot, source);
            var target = Path.Combine(backupRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
            await using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            manifestFiles.Add(new JsonObject
            {
                ["path"] = relative.Replace(Path.DirectorySeparatorChar, '/'),
                ["length"] = stream.Length,
                ["sha256"] = hash
            });
            File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        }
        var manifestPath = Path.Combine(backupRoot, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, new JsonObject
        {
            ["version"] = 1,
            ["source"] = legacyRoot,
            ["createdAt"] = DateTimeOffset.UtcNow,
            ["files"] = manifestFiles
        }.ToJsonString(EngineJson.Options), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.SetAttributes(manifestPath, File.GetAttributes(manifestPath) | FileAttributes.ReadOnly);
        return backupRoot;
    }

    private static JsonObject MapLegacySettings(string iniPath)
    {
        var ini = ReadIni(iniPath);
        var result = new JsonObject();
        Map("BitTorrent/Session\\DefaultSavePath", "save_path", ParseQtString, result, ini);
        Map("BitTorrent/Session\\TempPathEnabled", "temp_path_enabled", ParseBoolean, result, ini);
        Map("BitTorrent/Session\\TempPath", "temp_path", ParseQtString, result, ini);
        Map("BitTorrent/Session\\CreateTorrentSubfolder", "create_subfolder_enabled", ParseBoolean, result, ini);
        Map("BitTorrent/Session\\AutoTMMEnabled", "auto_tmm_enabled", ParseBoolean, result, ini);
        Map("BitTorrent/Session\\Preallocation", "preallocate_all", ParseBoolean, result, ini);
        Map("BitTorrent/Session\\BTProtocol", "bittorrent_protocol", ParsePeerProtocol, result, ini);
        Map("BitTorrent/Session\\Port", "listen_port", ParseInteger, result, ini);
        Map("BitTorrent/Session\\AlternativeGlobalDLSpeedLimit", "alt_dl_limit", ParseInteger, result, ini);
        Map("BitTorrent/Session\\AlternativeGlobalUPSpeedLimit", "alt_up_limit", ParseInteger, result, ini);
        Map("BitTorrent/Session\\GlobalDLSpeedLimit", "dl_limit", ParseInteger, result, ini);
        Map("BitTorrent/Session\\GlobalUPSpeedLimit", "up_limit", ParseInteger, result, ini);
        Map("BitTorrent/Session\\UseAlternativeGlobalSpeedLimit", "use_alt_speed_limits", ParseBoolean, result, ini);
        Map("BitTorrent/Session\\SchedulerEnabled", "scheduler_enabled", ParseBoolean, result, ini);
        Map("BitTorrent/Session\\SchedulerFromHour", "schedule_from_hour", ParseInteger, result, ini);
        Map("BitTorrent/Session\\SchedulerFromMin", "schedule_from_min", ParseInteger, result, ini);
        Map("BitTorrent/Session\\SchedulerToHour", "schedule_to_hour", ParseInteger, result, ini);
        Map("BitTorrent/Session\\SchedulerToMin", "schedule_to_min", ParseInteger, result, ini);
        Map("BitTorrent/Session\\SchedulerDays", "scheduler_days", ParseInteger, result, ini);
        Map("BitTorrent/Session\\MaxConnections", "max_connec", ParseInteger, result, ini);
        Map("BitTorrent/Session\\MaxConnectionsPerTorrent", "max_connec_per_torrent", ParseInteger, result, ini);
        Map("BitTorrent/Session\\MaxUploads", "max_uploads", ParseInteger, result, ini);
        Map("BitTorrent/Session\\MaxUploadsPerTorrent", "max_uploads_per_torrent", ParseInteger, result, ini);
        Map("BitTorrent/Session\\QueueingSystemEnabled", "queueing_enabled", ParseBoolean, result, ini);
        Map("BitTorrent/Session\\MaxActiveDownloads", "max_active_downloads", ParseInteger, result, ini);
        Map("BitTorrent/Session\\MaxActiveUploads", "max_active_uploads", ParseInteger, result, ini);
        Map("BitTorrent/Session\\DHTEnabled", "dht", ParseBoolean, result, ini);
        Map("BitTorrent/Session\\PeXEnabled", "pex", ParseBoolean, result, ini);
        Map("BitTorrent/Session\\LSDEnabled", "lsd", ParseBoolean, result, ini);
        Map("BitTorrent/Session\\Encryption", "encryption", ParseInteger, result, ini);
        Map("BitTorrent/Session\\AnonymousModeEnabled", "anonymous_mode", ParseBoolean, result, ini);
        Map("BitTorrent/Session\\I2PEnabled", "i2p_enabled", ParseBoolean, result, ini);
        Map("BitTorrent/Session\\I2PAddress", "i2p_address", ParseQtString, result, ini);
        Map("BitTorrent/Session\\I2PPort", "i2p_port", ParseQtPort, result, ini);
        Map("BitTorrent/Session\\I2PMixedMode", "i2p_mixed_mode", ParseBoolean, result, ini);
        Map("BitTorrent/Session\\IPFilterEnabled", "ip_filter_enabled", ParseBoolean, result, ini);
        Map("BitTorrent/Session\\IPFilter", "ip_filter_path", ParseQtString, result, ini);
        Map("BitTorrent/Session\\IPFilterTrackers", "ip_filter_trackers", ParseBoolean, result, ini);
        Map("Network/PortForwardingEnabled", "upnp", ParseBoolean, result, ini);
        Map("Network/Proxy\\Type", "proxy_type", static value => value, result, ini);
        Map("Network/Proxy\\IP", "proxy_ip", ParseQtString, result, ini);
        Map("Network/Proxy\\Port", "proxy_port", ParseQtPort, result, ini);
        Map("Network/Proxy\\Username", "proxy_username", ParseQtString, result, ini);
        Map("Network/Proxy\\Password", "proxy_password", ParseQtString, result, ini);
        Map("Network/Proxy\\AuthEnabled", "proxy_auth_enabled", ParseBoolean, result, ini);
        Map("Network/Proxy\\HostnameLookupEnabled", "proxy_hostname_lookup", ParseBoolean, result, ini);
        Map("Network/Proxy\\Profiles\\BitTorrent", "proxy_bittorrent", ParseBoolean, result, ini);
        Map("Network/Proxy\\Profiles\\PeerConnections", "proxy_peer_connections", ParseBoolean, result, ini);
        Map("Network/Proxy\\Profiles\\RSS", "proxy_rss", ParseBoolean, result, ini);
        Map("Network/Proxy\\Profiles\\Misc", "proxy_misc", ParseBoolean, result, ini);
        Map("Preferences/Connection\\ResolvePeerCountries", "resolve_peer_countries", ParseBoolean, result, ini);
        Map("RSS/Session\\EnableProcessing", "rss_processing_enabled", ParseBoolean, result, ini);
        Map("RSS/Session\\RefreshInterval", "rss_refresh_interval", ParseInteger, result, ini);
        Map("RSS/Session\\MaxArticlesPerFeed", "rss_max_articles_per_feed", ParseInteger, result, ini);
        Map("RSS/Session\\AutoDownloaderEnabled", "rss_auto_downloading_enabled", ParseBoolean, result, ini);
        Map("Preferences/WebUI\\Address", "web_ui_address", ParseQtString, result, ini);
        Map("Preferences/WebUI\\Port", "web_ui_port", ParseQtPort, result, ini);
        Map("Preferences/WebUI\\Username", "web_ui_username", ParseQtString, result, ini);
        Map("Preferences/WebUI\\UseUPnP", "web_ui_upnp", ParseBoolean, result, ini);
        Map("Preferences/WebUI\\CSRFProtection", "web_ui_csrf_protection_enabled", ParseBoolean, result, ini);
        Map("Preferences/WebUI\\HTTPS\\Enabled", "web_ui_https_enabled", ParseBoolean, result, ini);
        Map("Preferences/WebUI\\HTTPS\\CertificatePath", "web_ui_https_certificate_path", ParseQtString, result, ini);
        Map("BitTorrent/Session\\DiskCacheSize", "disk_cache", ParseInteger, result, ini);
        Map("BitTorrent/Session\\AsyncIOThreadsCount", "async_io_threads", ParseInteger, result, ini);
        Map("BitTorrent/Session\\RecheckOnCompletion", "recheck_completed_torrents", ParseBoolean, result, ini);
        if (result["listen_port"]?.GetValue<long>() is 0) result["random_port"] = true;
        else if (result.ContainsKey("listen_port")) result["random_port"] = false;
        if (ini.TryGetValue("Preferences/WebUI\\Enabled", out var webUiEnabled) && !ParseBooleanValue(webUiEnabled))
            result["web_ui_port"] = 0;
        return result;
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        var section = string.Empty;
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            result[$"{section}/{line[..separator].Trim()}"] = line[(separator + 1)..].Trim();
        }
        return result;
    }

    private static void Map(string source, string target, Func<string, object> convert, JsonObject result, Dictionary<string, string> ini)
    {
        if (ini.TryGetValue(source, out var value)) result[target] = JsonValue.Create(convert(value));
    }

    private static object ParseInteger(string value)
        => long.TryParse(value, out var result) ? result : 0;

    private static object ParseBoolean(string value)
        => ParseBooleanValue(value);

    private static bool ParseBooleanValue(string value)
        => bool.TryParse(value, out var result) && result;

    private static object ParseQtString(string value)
        => value.Replace("\\\\", "\\", StringComparison.Ordinal);

    private static object ParsePeerProtocol(string value) => value.ToLowerInvariant() switch
    {
        "tcp" => 1,
        "utp" => 2,
        _ => 0
    };

    private static object ParseQtPort(string value)
    {
        if (int.TryParse(value, out var direct)) return direct;
        var bytes = System.Text.RegularExpressions.Regex.Matches(value, @"\\x([0-9a-fA-F]+)")
            .Select(static match => byte.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : (byte)0)
            .ToArray();
        if (bytes.Length < 2) return 0;
        return bytes[^2] << 8 | bytes[^1];
    }

    private static JsonNode ReadJsonOrDefault(string path, JsonNode fallback)
    {
        if (!File.Exists(path)) return fallback;
        try { return JsonNode.Parse(File.ReadAllText(path)) ?? fallback; }
        catch (JsonException) { return fallback; }
    }

    private static JsonObject ReadLegacyCategories(string path)
    {
        var source = ReadJsonOrDefault(path, new JsonObject()) as JsonObject ?? new JsonObject();
        var result = new JsonObject();
        foreach (var (name, value) in source)
        {
            if (value is not JsonObject category) continue;
            result[name] = new JsonObject
            {
                ["savePath"] = category["savePath"]?.DeepClone() ?? category["save_path"]?.DeepClone() ?? string.Empty,
                ["downloadPath"] = category["downloadPath"]?.DeepClone() ?? category["download_path"]?.DeepClone() ?? string.Empty
            };
        }
        return result;
    }

    private static string ReadText(SqliteDataReader reader, string name)
    {
        var value = reader[name];
        return value switch
        {
            DBNull => string.Empty,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static bool ReadBoolean(SqliteDataReader reader, string name)
        => reader[name] is not DBNull && Convert.ToBoolean(reader[name], System.Globalization.CultureInfo.InvariantCulture);

    private static IEnumerable<string> SplitCsv(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsTorrentId(string value)
        => (value.Length is 40 or 64) && value.All(Uri.IsHexDigit);

    private static IEnumerable<string> TorrentIdentifiers(JsonElement torrent)
    {
        foreach (var name in new[] { "hash", "infohash_v1", "infohash_v2" })
        {
            if (!torrent.TryGetProperty(name, out var property)) continue;
            var value = property.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            yield return value;
            if (name == "infohash_v2" && value.Length >= 40)
                yield return value[..40];
        }
    }

    private static void PopulateImportedTorrentState(
        JsonObject import,
        string stagingRoot,
        IReadOnlyDictionary<string, string> mappings,
        IReadOnlyDictionary<string, JsonElement> snapshots)
    {
        var imported = import["torrents"] as JsonArray ?? new JsonArray();
        import["torrents"] = imported;
        var existing = imported.OfType<JsonObject>()
            .Where(static value => value["hash"] is not null)
            .ToDictionary(static value => value["hash"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
        var needsRecheck = (import["needsRecheck"] as JsonArray ?? new JsonArray())
            .Where(static value => value is not null)
            .Select(static value => value!.GetValue<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaultSavePath = import["settings"]?["save_path"]?.GetValue<string>() ?? string.Empty;

        foreach (var (sourceHash, canonicalHash) in mappings)
        {
            var snapshot = snapshots[sourceHash];
            if (!existing.TryGetValue(sourceHash, out var state))
            {
                state = new JsonObject();
                imported.Add(state);
            }

            var nativeSavePath = snapshot.GetProperty("save_path").GetString() ?? string.Empty;
            var usedFallback = PathsEqual(nativeSavePath, stagingRoot);
            if (usedFallback) needsRecheck.Add(sourceHash);
            state["hash"] = canonicalHash;
            state["category"] ??= snapshot.GetProperty("category").GetString() ?? string.Empty;
            state["tags"] ??= snapshot.GetProperty("tags").GetString() ?? string.Empty;
            state["displayName"] ??= snapshot.GetProperty("display_name").GetString() ?? string.Empty;
            state["savePath"] = usedFallback && !string.IsNullOrWhiteSpace(defaultSavePath) ? defaultSavePath : nativeSavePath;
            state["downloadPath"] ??= snapshot.GetProperty("download_path").GetString() ?? string.Empty;
            state["firstLast"] ??= snapshot.GetProperty("f_l_piece_prio").GetBoolean();
            state["forceStart"] ??= snapshot.GetProperty("force_start").GetBoolean();
            state["automaticTmm"] ??= snapshot.GetProperty("auto_tmm").GetBoolean();
            state["ratioLimit"] ??= snapshot.GetProperty("ratio_limit").GetDouble();
            state["seedingTimeLimit"] ??= snapshot.GetProperty("seeding_time_limit").GetInt32();
            state["inactiveSeedingTimeLimit"] ??= snapshot.GetProperty("inactive_seeding_time_limit").GetInt32();
            state["queuePosition"] ??= snapshot.GetProperty("priority").GetInt32();
            state["needsRecheck"] = usedFallback || needsRecheck.Contains(sourceHash);
        }

        import["needsRecheck"] = new JsonArray(needsRecheck
            .Select(hash => mappings.GetValueOrDefault(hash, hash))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(static hash => (JsonNode?)hash)
            .ToArray());
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void CanonicalizeStagedFiles(string stagingRoot, IReadOnlyDictionary<string, string> mappings)
    {
        foreach (var (sourceHash, canonicalHash) in mappings)
        {
            if (sourceHash.Equals(canonicalHash, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var (directory, extension) in new[] { ("resume", ".fastresume"), ("torrents", ".torrent") })
            {
                var source = Path.Combine(stagingRoot, directory, sourceHash + extension);
                var target = Path.Combine(stagingRoot, directory, canonicalHash + extension);
                if (!File.Exists(source)) continue;
                if (File.Exists(target)) throw new InvalidDataException($"Migration produced duplicate canonical torrent id {canonicalHash}.");
                File.Move(source, target);
            }
        }
    }

    private static void EnsureActivationTargetIsEmpty(string path)
    {
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException($"Migration cannot replace the non-empty engine directory '{path}'.");
    }

    private static void ActivateDirectory(string source, string target)
    {
        if (Directory.Exists(target)) Directory.Delete(target);
        Directory.Move(source, target);
    }

    private static void ActivateDirectoryIfPresent(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        EnsureActivationTargetIsEmpty(target);
        ActivateDirectory(source, target);
    }

    private static void CopyDirectoryIfPresent(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }
}
