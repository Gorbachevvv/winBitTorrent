using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WinBitTorrent.EngineHost;

internal sealed partial class EngineState
{
    private async Task CaptureResumeStorageAsync(CancellationToken cancellationToken, bool force = false)
    {
        var storage = await GetSettingStringAsync("resume_data_storage_type", "SQLite", cancellationToken).ConfigureAwait(false);
        if (!force && !storage.Equals("SQLite", StringComparison.OrdinalIgnoreCase)) return;

        var snapshot = _native.Invoke(WinBitTorrent.Core.EngineProtocol.EngineRpcMethods.TorrentsInfo, EngineJson.Element(new { filter = "all" }));
        var values = new List<(string Hash, byte[]? Metadata, byte[]? Resume)>();
        foreach (var torrent in snapshot.EnumerateArray())
        {
            var hash = torrent.GetProperty("hash").GetString()!;
            var metadataPath = Path.Combine(_dataRoot, "torrents", hash + ".torrent");
            var resumePath = Path.Combine(_dataRoot, "resume", hash + ".fastresume");
            values.Add((hash,
                File.Exists(metadataPath) ? await File.ReadAllBytesAsync(metadataPath, cancellationToken).ConfigureAwait(false) : null,
                File.Exists(resumePath) ? await File.ReadAllBytesAsync(resumePath, cancellationToken).ConfigureAwait(false) : null));
        }

        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = await _database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var value in values)
            {
                await using var command = _database.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "UPDATE torrents SET metadata=COALESCE($metadata, metadata), resume_data=COALESCE($resume, resume_data) WHERE hash=$hash";
                command.Parameters.AddWithValue("$hash", value.Hash);
                command.Parameters.AddWithValue("$metadata", (object?)value.Metadata ?? DBNull.Value);
                command.Parameters.AddWithValue("$resume", (object?)value.Resume ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }

        DeleteResumeFiles("resume", "*.fastresume");
        DeleteResumeFiles("torrents", "*.torrent");
    }

    private static async Task HydrateResumeFilesFromDatabaseAsync(
        SqliteConnection database,
        string dataRoot,
        bool force,
        CancellationToken cancellationToken)
    {
        if (!force)
        {
            await using var setting = database.CreateCommand();
            setting.CommandText = "SELECT value_json FROM settings WHERE key='resume_data_storage_type'";
            var json = await setting.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (json is not null && !string.Equals(JsonSerializer.Deserialize<string>(json), "SQLite", StringComparison.OrdinalIgnoreCase)) return;
        }
        var torrentsRoot = Path.Combine(dataRoot, "torrents");
        var resumeRoot = Path.Combine(dataRoot, "resume");
        Directory.CreateDirectory(torrentsRoot);
        Directory.CreateDirectory(resumeRoot);
        await using var command = database.CreateCommand();
        command.CommandText = "SELECT hash, metadata, resume_data FROM torrents WHERE metadata IS NOT NULL OR resume_data IS NOT NULL";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var hash = reader.GetString(0);
            if (hash.Length is not (40 or 64) || !hash.All(Uri.IsHexDigit)) continue;
            if (!reader.IsDBNull(1)) await WriteAtomicAsync(Path.Combine(torrentsRoot, hash + ".torrent"), (byte[])reader[1], cancellationToken).ConfigureAwait(false);
            if (!reader.IsDBNull(2)) await WriteAtomicAsync(Path.Combine(resumeRoot, hash + ".fastresume"), (byte[])reader[2], cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteAtomicAsync(string path, byte[] value, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, value, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private void DeleteResumeFiles(string directory, string pattern)
    {
        var root = Path.GetFullPath(Path.Combine(_dataRoot, directory));
        var dataRoot = Path.GetFullPath(_dataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Resume cleanup target escaped the Engine data directory.");
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)) File.Delete(file);
    }
}
