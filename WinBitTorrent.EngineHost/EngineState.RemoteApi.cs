using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinBitTorrent.EngineHost;

internal sealed partial class EngineState
{
    private const int PasswordIterations = 600_000;

    internal async Task<RemoteApiOptions> GetRemoteApiOptionsAsync(CancellationToken cancellationToken)
    {
        var preferences = await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
        static string Text(JsonElement value, string name, string fallback = "")
            => value.TryGetProperty(name, out var property) ? property.GetString() ?? fallback : fallback;
        static bool Bool(JsonElement value, string name, bool fallback = false)
            => value.TryGetProperty(name, out var property) ? property.GetBoolean() : fallback;
        static int Int(JsonElement value, string name, int fallback = 0)
            => value.TryGetProperty(name, out var property) && property.TryGetInt32(out var result) ? result : fallback;
        return new RemoteApiOptions(
            Text(preferences, "web_ui_address", "127.0.0.1"),
            Int(preferences, "web_ui_port"),
            Text(preferences, "web_ui_username", "admin"),
            Bool(preferences, "web_ui_csrf_protection_enabled", true),
            Bool(preferences, "web_ui_external_enabled"),
            Bool(preferences, "web_ui_https_enabled"),
            Text(preferences, "web_ui_https_certificate_path"));
    }

    private async Task<JsonElement> RotateRemoteApiKeyAsync(CancellationToken cancellationToken)
    {
        var key = "wbt_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = await _database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var clear = _database.CreateCommand())
            {
                clear.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                clear.CommandText = "DELETE FROM api_keys";
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var insert = _database.CreateCommand())
            {
                insert.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                insert.CommandText = "INSERT INTO api_keys(id, hash, created_at) VALUES($id, $hash, $created)";
                insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                insert.Parameters.AddWithValue("$hash", hash);
                insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
        await AppendLogAsync(2, "Remote API key rotated.", cancellationToken).ConfigureAwait(false);
        return EngineJson.Element(key);
    }

    private async Task<JsonElement> DeleteRemoteApiKeysAsync(CancellationToken cancellationToken)
    {
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "DELETE FROM api_keys";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
        await AppendLogAsync(2, "All Remote API keys deleted.", cancellationToken).ConfigureAwait(false);
        return EngineJson.EmptyObject;
    }

    internal async Task<bool> ValidateRemoteApiKeyAsync(string candidate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        var hashes = new List<byte[]>();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "SELECT hash FROM api_keys";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) hashes.Add((byte[])reader[0]);
        }
        finally
        {
            _databaseGate.Release();
        }
        return hashes.Any(hash => hash.Length == candidateHash.Length && CryptographicOperations.FixedTimeEquals(hash, candidateHash));
    }

    internal async Task<bool> ValidateRemotePasswordAsync(string password, CancellationToken cancellationToken)
    {
        byte[]? salt = null;
        byte[]? expected = null;
        var iterations = PasswordIterations;
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "SELECT salt, hash, iterations FROM api_credentials WHERE kind='password'";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                salt = (byte[])reader[0];
                expected = (byte[])reader[1];
                iterations = reader.GetInt32(2);
            }
        }
        finally
        {
            _databaseGate.Release();
        }
        if (salt is null || expected is null) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    internal async Task SetRemotePasswordAsync(string password, CancellationToken cancellationToken)
    {
        if (password.Length < 12) throw new ArgumentException("Remote API password must contain at least 12 characters.");
        var salt = RandomNumberGenerator.GetBytes(32);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "INSERT INTO api_credentials(kind, salt, hash, iterations) VALUES('password', $salt, $hash, $iterations) ON CONFLICT(kind) DO UPDATE SET salt=excluded.salt, hash=excluded.hash, iterations=excluded.iterations";
            command.Parameters.AddWithValue("$salt", salt);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$iterations", PasswordIterations);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
        await AppendLogAsync(2, "Remote API password changed.", cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> ChangeRemoteApiPasswordAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var password = payload.TryGetProperty("newPassword", out var value) ? value.GetString() : null;
        if (password is null) throw new ArgumentException("newPassword is required.");
        await SetRemotePasswordAsync(password, cancellationToken).ConfigureAwait(false);
        return EngineJson.EmptyObject;
    }

    private async Task<JsonElement> DeleteMigrationBackupAsync(CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(_dataRoot, "migration.json");
        if (!File.Exists(markerPath)) return EngineJson.Element(false);
        var marker = JsonNode.Parse(await File.ReadAllTextAsync(markerPath, cancellationToken).ConfigureAwait(false))?.AsObject()
            ?? throw new InvalidDataException("The migration marker is invalid.");
        var storedPath = marker["backupPath"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(storedPath) || !Directory.Exists(storedPath))
            return EngineJson.Element(false);

        var backupsRoot = Path.GetFullPath(Path.Combine(_dataRoot, "Backups")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var backupPath = Path.GetFullPath(storedPath).TrimEnd(Path.DirectorySeparatorChar);
        if (!backupPath.StartsWith(backupsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Migration backup cleanup target escaped the Engine backup directory.");

        foreach (var file in Directory.EnumerateFiles(backupPath, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(backupPath, recursive: true);
        marker["backupDeletedAt"] = DateTimeOffset.UtcNow;
        var temporary = markerPath + ".tmp";
        await File.WriteAllTextAsync(temporary, marker.ToJsonString(EngineJson.Options), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, markerPath, overwrite: true);
        await AppendLogAsync(2, "Legacy qBittorrent migration backup deleted by the user.", cancellationToken).ConfigureAwait(false);
        return EngineJson.Element(true);
    }

    internal static bool IsLoopbackAddress(string value)
    {
        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(value, out var address) && IPAddress.IsLoopback(address);
    }
}

internal sealed record RemoteApiOptions(
    string Address,
    int Port,
    string UserName,
    bool CsrfProtection,
    bool ExternalEnabled,
    bool HttpsEnabled,
    string CertificatePath);
