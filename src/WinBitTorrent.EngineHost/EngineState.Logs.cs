using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinBitTorrent.EngineHost;

internal sealed partial class EngineState
{
    private async Task AppendLogAsync(int type, string message, CancellationToken cancellationToken)
    {
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "INSERT INTO engine_logs(timestamp, type, message) VALUES($time, $type, $message)";
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$message", message);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var trim = _database.CreateCommand();
            trim.CommandText = "DELETE FROM engine_logs WHERE id <= (SELECT COALESCE(MAX(id), 0) - 10000 FROM engine_logs)";
            await trim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task<JsonElement> GetLogsAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var lastId = payload.TryGetProperty("lastKnownId", out var lastValue) ? lastValue.GetInt64() : -1;
        var enabledTypes = new HashSet<int>();
        if (!payload.TryGetProperty("normal", out var normal) || normal.GetBoolean()) enabledTypes.Add(1);
        if (!payload.TryGetProperty("info", out var info) || info.GetBoolean()) enabledTypes.Add(2);
        if (!payload.TryGetProperty("warning", out var warning) || warning.GetBoolean()) enabledTypes.Add(4);
        if (!payload.TryGetProperty("critical", out var critical) || critical.GetBoolean()) enabledTypes.Add(8);

        var result = new JsonArray();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "SELECT id, timestamp, type, message FROM engine_logs WHERE id > $id ORDER BY id LIMIT 5000";
            command.Parameters.AddWithValue("$id", lastId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var type = reader.GetInt32(2);
                if (!enabledTypes.Contains(type)) continue;
                result.Add(new JsonObject
                {
                    ["id"] = reader.GetInt64(0),
                    ["timestamp"] = reader.GetInt64(1),
                    ["type"] = type,
                    ["message"] = reader.GetString(3)
                });
            }
        }
        finally
        {
            _databaseGate.Release();
        }
        return EngineJson.Element(result);
    }

    private async Task AppendPeerLogAsync(string address, bool blocked, string reason, CancellationToken cancellationToken)
    {
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "INSERT INTO peer_logs(timestamp, ip, blocked, reason) VALUES($time, $ip, $blocked, $reason)";
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$ip", address);
            command.Parameters.AddWithValue("$blocked", blocked ? 1 : 0);
            command.Parameters.AddWithValue("$reason", reason);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task<JsonElement> GetPeerLogsAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var lastId = payload.TryGetProperty("lastKnownId", out var lastValue) ? lastValue.GetInt64() : -1;
        var result = new JsonArray();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "SELECT id, timestamp, ip, blocked, reason FROM peer_logs WHERE id > $id ORDER BY id LIMIT 5000";
            command.Parameters.AddWithValue("$id", lastId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new JsonObject
                {
                    ["id"] = reader.GetInt64(0),
                    ["timestamp"] = reader.GetInt64(1),
                    ["ip"] = reader.GetString(2),
                    ["blocked"] = reader.GetBoolean(3),
                    ["reason"] = reader.GetString(4)
                });
            }
        }
        finally
        {
            _databaseGate.Release();
        }
        return EngineJson.Element(result);
    }

    private async Task<JsonElement> GetCookiesAsync(CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "SELECT domain, path, name, value, expires, secure, http_only FROM cookies ORDER BY domain, path, name";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new JsonObject
                {
                    ["domain"] = reader.GetString(0),
                    ["path"] = reader.GetString(1),
                    ["name"] = reader.GetString(2),
                    ["value"] = reader.GetString(3),
                    ["expirationDate"] = reader.GetInt64(4),
                    ["secure"] = reader.GetBoolean(5),
                    ["httpOnly"] = reader.GetBoolean(6)
                });
            }
        }
        finally
        {
            _databaseGate.Release();
        }
        return EngineJson.Element(result);
    }

    private async Task<JsonElement> SetCookiesAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (payload.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("A JSON cookie array is required.");

        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = await _database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var clear = _database.CreateCommand())
            {
                clear.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                clear.CommandText = "DELETE FROM cookies";
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var cookie in payload.EnumerateArray())
            {
                var domain = cookie.TryGetProperty("domain", out var domainValue) ? domainValue.GetString() : null;
                var name = cookie.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(name)) continue;
                await using var command = _database.CreateCommand();
                command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                command.CommandText = "INSERT INTO cookies(domain, path, name, value, expires, secure, http_only) VALUES($domain, $path, $name, $value, $expires, $secure, $httpOnly)";
                command.Parameters.AddWithValue("$domain", domain);
                command.Parameters.AddWithValue("$path", cookie.TryGetProperty("path", out var pathValue) ? pathValue.GetString() ?? "/" : "/");
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$value", cookie.TryGetProperty("value", out var value) ? value.GetString() ?? string.Empty : string.Empty);
                command.Parameters.AddWithValue("$expires", cookie.TryGetProperty("expirationDate", out var expires) && expires.TryGetInt64(out var timestamp) ? timestamp : 0);
                command.Parameters.AddWithValue("$secure", cookie.TryGetProperty("secure", out var secure) && secure.GetBoolean() ? 1 : 0);
                command.Parameters.AddWithValue("$httpOnly", cookie.TryGetProperty("httpOnly", out var httpOnly) && httpOnly.GetBoolean() ? 1 : 0);
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

    private async Task ApplyCookiesAsync(CookieContainer container, CancellationToken cancellationToken)
    {
        var values = await GetCookiesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var value in values.EnumerateArray())
        {
            var domain = value.GetProperty("domain").GetString();
            var name = value.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(name)) continue;
            var cookie = new Cookie(name, value.GetProperty("value").GetString() ?? string.Empty,
                value.GetProperty("path").GetString() ?? "/", domain)
            {
                Secure = value.GetProperty("secure").GetBoolean(),
                HttpOnly = value.GetProperty("httpOnly").GetBoolean()
            };
            container.Add(cookie);
        }
    }
}
