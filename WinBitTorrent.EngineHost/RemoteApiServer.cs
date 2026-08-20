using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using WinBitTorrent.Core.EngineProtocol;

namespace WinBitTorrent.EngineHost;

internal sealed class RemoteApiServer : IAsyncDisposable
{
    private readonly WebApplication? _application;
    private readonly ConcurrentDictionary<string, ApiSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LoginFailures> _loginFailures = new(StringComparer.OrdinalIgnoreCase);

    private RemoteApiServer(WebApplication? application) => _application = application;

    public static async Task<RemoteApiServer> StartAsync(EngineState state, CancellationToken cancellationToken = default)
    {
        var options = await state.GetRemoteApiOptionsAsync(cancellationToken).ConfigureAwait(false);
        if (options.Port == 0) return new RemoteApiServer(null);
        if (options.Port is < 1 or > 65535) throw new InvalidOperationException("Remote API port must be between 1 and 65535.");
        var loopback = EngineState.IsLoopbackAddress(options.Address);
        if (!loopback && !options.ExternalEnabled)
            throw new InvalidOperationException("External Remote API binding requires web_ui_external_enabled.");
        if (!loopback && (!options.HttpsEnabled || string.IsNullOrWhiteSpace(options.CertificatePath) || !File.Exists(options.CertificatePath)))
            throw new InvalidOperationException("External Remote API binding requires HTTPS and a valid certificate file.");

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(RemoteApiServer).Assembly.FullName,
            Args = [],
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => ConfigureEndpoint(server, options));
        var application = builder.Build();
        var result = new RemoteApiServer(application);
        result.MapRoutes(state, options);
        await application.StartAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static void ConfigureEndpoint(KestrelServerOptions server, RemoteApiOptions options)
    {
        IPAddress address;
        if (options.Address is "*" or "+") address = IPAddress.Any;
        else if (options.Address.Equals("localhost", StringComparison.OrdinalIgnoreCase)) address = IPAddress.Loopback;
        else if (!IPAddress.TryParse(options.Address, out address!)) throw new InvalidOperationException($"Remote API address '{options.Address}' is invalid.");
        server.Listen(address, options.Port, listen =>
        {
            if (options.HttpsEnabled) listen.UseHttps(options.CertificatePath);
        });
        server.Limits.MaxRequestBodySize = EngineRpcProtocol.MaximumMessageBytes;
        server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
        server.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    }

    private void MapRoutes(EngineState state, RemoteApiOptions options)
    {
        var app = _application!;
        app.MapGet("/api/v1/openapi.json", () => Results.Json(OpenApiDocument(options)));
        app.MapPost("/api/v1/auth/login", ResultRoute(context => LoginAsync(context, state, options)));

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/v1")
                || context.Request.Path.Equals("/api/v1/openapi.json")
                || context.Request.Path.Equals("/api/v1/auth/login"))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var bearer = GetBearer(context.Request.Headers.Authorization);
            if (bearer is not null && await state.ValidateRemoteApiKeyAsync(bearer, context.RequestAborted).ConfigureAwait(false))
            {
                context.Items["wbt-auth"] = "bearer";
                await next(context).ConfigureAwait(false);
                return;
            }

            if (context.Request.Cookies.TryGetValue("wbt_session", out var token)
                && _sessions.TryGetValue(token, out var session)
                && session.ExpiresAt > DateTimeOffset.UtcNow)
            {
                session.ExpiresAt = DateTimeOffset.UtcNow.AddHours(8);
                context.Items["wbt-auth"] = "cookie";
                if (options.CsrfProtection && IsMutation(context.Request.Method)
                    && (!context.Request.Headers.TryGetValue("X-WinBitTorrent-CSRF", out var csrf)
                        || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(csrf.ToString()), Encoding.UTF8.GetBytes(session.CsrfToken))))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "csrf_validation_failed" }, context.RequestAborted).ConfigureAwait(false);
                    return;
                }
                await next(context).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "authentication_required" }, context.RequestAborted).ConfigureAwait(false);
        });

        app.MapPost("/api/v1/auth/logout", (HttpContext context) =>
        {
            if (context.Request.Cookies.TryGetValue("wbt_session", out var token)) _sessions.TryRemove(token, out _);
            context.Response.Cookies.Delete("wbt_session");
            context.Response.Cookies.Delete("wbt_csrf");
            return Results.NoContent();
        });
        app.MapPut("/api/v1/auth/password", ResultRoute(async context =>
        {
            var body = await ReadObjectAsync(context).ConfigureAwait(false);
            var password = body["newPassword"]?.GetValue<string>() ?? throw new Microsoft.AspNetCore.Http.BadHttpRequestException("newPassword is required.");
            await state.SetRemotePasswordAsync(password, context.RequestAborted).ConfigureAwait(false);
            return Results.NoContent();
        }));

        app.MapGet("/api/v1/torrents", ResultRoute(async context => Json(await state.HandleAsync(EngineRpcMethods.TorrentsInfo,
            EngineJson.Element(new
            {
                filter = context.Request.Query["filter"].FirstOrDefault() ?? "all",
                category = context.Request.Query["category"].FirstOrDefault(),
                tag = context.Request.Query["tag"].FirstOrDefault()
            }), context.RequestAborted).ConfigureAwait(false))));
        app.MapPost("/api/v1/torrents", ResultRoute(async context => Json(await state.HandleAsync(EngineRpcMethods.TorrentsAdd, await ReadElementAsync(context).ConfigureAwait(false), context.RequestAborted).ConfigureAwait(false))));
        app.MapDelete("/api/v1/torrents/{hashes}", async (string hashes, bool deleteFiles, HttpContext context) => Json(await state.HandleAsync(EngineRpcMethods.TorrentsDelete, EngineJson.Element(new { hashes, deleteFiles }), context.RequestAborted).ConfigureAwait(false)));
        app.MapPost("/api/v1/torrents/{hashes}/command", async (string hashes, HttpContext context) =>
        {
            var body = await ReadObjectAsync(context).ConfigureAwait(false);
            body["hashes"] = hashes;
            return Json(await state.HandleAsync(EngineRpcMethods.TorrentsCommand, EngineJson.Element(body), context.RequestAborted).ConfigureAwait(false));
        });
        app.MapPost("/api/v1/torrents/action", ResultRoute(async context => Json(await state.HandleAsync(EngineRpcMethods.TorrentsAction, await ReadElementAsync(context).ConfigureAwait(false), context.RequestAborted).ConfigureAwait(false))));

        app.MapGet("/api/v1/settings", ResultRoute(async context => Json(await state.HandleAsync(EngineRpcMethods.ApplicationGetPreferences, EngineJson.EmptyObject, context.RequestAborted).ConfigureAwait(false))));
        app.MapMethods("/api/v1/settings", ["PATCH", "PUT"], ResultRoute(async context => Json(await state.HandleAsync(EngineRpcMethods.ApplicationSetPreferences, await ReadElementAsync(context).ConfigureAwait(false), context.RequestAborted).ConfigureAwait(false))));
        app.MapGet("/api/v1/logs", ResultRoute(async context => Json(await state.HandleAsync(EngineRpcMethods.LogsMain, EngineJson.Element(new
        {
            lastKnownId = ParseLong(context.Request.Query["after"].FirstOrDefault(), -1), normal = true, info = true, warning = true, critical = true
        }), context.RequestAborted).ConfigureAwait(false))));

        app.MapGet("/api/v1/rss/items", ResultRoute(async context => Json(await state.HandleAsync(EngineRpcMethods.RssItems, EngineJson.Element(new { withData = true }), context.RequestAborted).ConfigureAwait(false))));
        app.MapGet("/api/v1/rss/rules", ResultRoute(async context => Json(await state.HandleAsync(EngineRpcMethods.RssRules, EngineJson.EmptyObject, context.RequestAborted).ConfigureAwait(false))));
        app.MapPost("/api/v1/rss/action", ResultRoute(async context => Json(await state.HandleAsync(EngineRpcMethods.RssAction, await ReadElementAsync(context).ConfigureAwait(false), context.RequestAborted).ConfigureAwait(false))));

        app.MapPost("/api/v1/search", ResultRoute(async context => Json(await state.HandleAsync(EngineRpcMethods.SearchStart, await ReadElementAsync(context).ConfigureAwait(false), context.RequestAborted).ConfigureAwait(false))));
        app.MapGet("/api/v1/search/{id:int}", async (int id, int limit, int offset, HttpContext context) => Json(await state.HandleAsync(EngineRpcMethods.SearchResults, EngineJson.Element(new { id, limit = limit <= 0 ? 500 : limit, offset }), context.RequestAborted).ConfigureAwait(false)));
        app.MapGet("/api/v1/search/{id:int}/status", async (int id, HttpContext context) => Json(await state.HandleAsync(EngineRpcMethods.SearchStatus, EngineJson.Element(new { id }), context.RequestAborted).ConfigureAwait(false)));
        app.MapGet("/api/v1/search/plugins", ResultRoute(async context => Json(await state.HandleAsync(EngineRpcMethods.SearchPlugins, EngineJson.EmptyObject, context.RequestAborted).ConfigureAwait(false))));
        app.MapPost("/api/v1/search/action", ResultRoute(async context => Json(await state.HandleAsync(EngineRpcMethods.SearchAction, await ReadElementAsync(context).ConfigureAwait(false), context.RequestAborted).ConfigureAwait(false))));

        app.MapPost("/api/v1/creator", ResultRoute(async context => Json(await state.HandleAsync(EngineRpcMethods.CreatorAdd, await ReadElementAsync(context).ConfigureAwait(false), context.RequestAborted).ConfigureAwait(false))));
        app.MapGet("/api/v1/creator/{taskId}", async (string taskId, HttpContext context) => Json(await state.HandleAsync(EngineRpcMethods.CreatorStatus, EngineJson.Element(new { taskId }), context.RequestAborted).ConfigureAwait(false)));
        app.MapGet("/api/v1/creator/{taskId}/file", async (string taskId, HttpContext context) =>
        {
            var value = await state.HandleAsync(EngineRpcMethods.CreatorFile, EngineJson.Element(new { taskId }), context.RequestAborted).ConfigureAwait(false);
            return Results.File(value.EnumerateArray().Select(static item => item.GetByte()).ToArray(), "application/x-bittorrent");
        });
        app.MapDelete("/api/v1/creator/{taskId}", async (string taskId, HttpContext context) => Json(await state.HandleAsync(EngineRpcMethods.CreatorDelete, EngineJson.Element(new { taskId }), context.RequestAborted).ConfigureAwait(false)));

        app.MapGet("/api/v1/client-data/{key}", async (string key, HttpContext context) => Json(await state.HandleAsync(EngineRpcMethods.ClientDataLoad, EngineJson.Element(new { key }), context.RequestAborted).ConfigureAwait(false)));
        app.MapPut("/api/v1/client-data/{key}", async (string key, HttpContext context) => Json(await state.HandleAsync(EngineRpcMethods.ClientDataStore, EngineJson.Element(new { key, value = await ReadElementAsync(context).ConfigureAwait(false) }), context.RequestAborted).ConfigureAwait(false)));

        app.MapPost("/api/v1/rpc/{method}", async (string method, HttpContext context) => Json(await state.HandleAsync(method, await ReadElementAsync(context).ConfigureAwait(false), context.RequestAborted).ConfigureAwait(false)));
        app.MapGet("/api/v1/events", async (HttpContext context) => await StreamEventsAsync(context, state).ConfigureAwait(false));
    }

    private async Task<IResult> LoginAsync(HttpContext context, EngineState state, RemoteApiOptions options)
    {
        var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (_loginFailures.TryGetValue(remote, out var failures) && failures.BlockedUntil > DateTimeOffset.UtcNow)
            return Results.Json(new { error = "too_many_attempts" }, statusCode: StatusCodes.Status429TooManyRequests);
        var body = await ReadObjectAsync(context).ConfigureAwait(false);
        var username = body["username"]?.GetValue<string>() ?? string.Empty;
        var password = body["password"]?.GetValue<string>() ?? string.Empty;
        var validUser = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(username), Encoding.UTF8.GetBytes(options.UserName));
        var validPassword = await state.ValidateRemotePasswordAsync(password, context.RequestAborted).ConfigureAwait(false);
        if (!validUser || !validPassword)
        {
            var updated = _loginFailures.AddOrUpdate(remote, static _ => new LoginFailures(1, DateTimeOffset.MinValue), static (_, prior) => prior with { Count = prior.Count + 1 });
            if (updated.Count >= 5) _loginFailures[remote] = new LoginFailures(0, DateTimeOffset.UtcNow.AddMinutes(5));
            return Results.Json(new { error = "invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized);
        }
        _loginFailures.TryRemove(remote, out _);
        var token = Token();
        var csrf = Token();
        _sessions[token] = new ApiSession(csrf, DateTimeOffset.UtcNow.AddHours(8));
        var secure = context.Request.IsHttps;
        context.Response.Cookies.Append("wbt_session", token, new CookieOptions { HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromHours(8), Path = "/api/v1" });
        context.Response.Cookies.Append("wbt_csrf", csrf, new CookieOptions { HttpOnly = false, Secure = secure, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromHours(8), Path = "/api/v1" });
        return Results.Json(new { csrfToken = csrf, expiresIn = 28800 });
    }

    private static async Task StreamEventsAsync(HttpContext context, EngineState state)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        var responseId = 0;
        while (!context.RequestAborted.IsCancellationRequested)
        {
            var data = await state.HandleAsync(EngineRpcMethods.SyncMainData, EngineJson.Element(new { responseId }), context.RequestAborted).ConfigureAwait(false);
            if (data.TryGetProperty("rid", out var rid)) responseId = rid.GetInt32();
            await context.Response.WriteAsync($"event: sync\ndata: {data.GetRawText()}\n\n", context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            await Task.Delay(1000, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static JsonObject OpenApiDocument(RemoteApiOptions options) => new()
    {
        ["openapi"] = "3.1.0",
        ["info"] = new JsonObject { ["title"] = "WinBitTorrent Remote API", ["version"] = "1.0" },
        ["servers"] = new JsonArray(new JsonObject { ["url"] = $"{(options.HttpsEnabled ? "https" : "http")}://{options.Address}:{options.Port}/api/v1" }),
        ["components"] = new JsonObject { ["securitySchemes"] = new JsonObject { ["bearerApiKey"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" } } },
        ["security"] = new JsonArray(new JsonObject { ["bearerApiKey"] = new JsonArray() }),
        ["paths"] = new JsonObject
        {
            ["/auth/login"] = Operations("post"),
            ["/auth/logout"] = Operations("post"),
            ["/auth/password"] = Operations("put"),
            ["/torrents"] = Operations("get", "post"),
            ["/torrents/{hashes}"] = Operations("delete"),
            ["/torrents/{hashes}/command"] = Operations("post"),
            ["/torrents/action"] = Operations("post"),
            ["/settings"] = Operations("get", "patch", "put"),
            ["/logs"] = Operations("get"),
            ["/rss/items"] = Operations("get"),
            ["/rss/rules"] = Operations("get"),
            ["/rss/action"] = Operations("post"),
            ["/search"] = Operations("post"),
            ["/search/{id}"] = Operations("get"),
            ["/search/{id}/status"] = Operations("get"),
            ["/search/plugins"] = Operations("get"),
            ["/search/action"] = Operations("post"),
            ["/creator"] = Operations("post"),
            ["/creator/{taskId}"] = Operations("get", "delete"),
            ["/creator/{taskId}/file"] = Operations("get"),
            ["/client-data/{key}"] = Operations("get", "put"),
            ["/rpc/{method}"] = Operations("post"),
            ["/events"] = Operations("get")
        }
    };

    private static JsonObject Operations(params string[] methods)
    {
        var result = new JsonObject();
        foreach (var method in methods) result[method] = new JsonObject { ["responses"] = new JsonObject { ["200"] = new JsonObject { ["description"] = "Success" } } };
        return result;
    }

    private static IResult Json(JsonElement value) => Results.Json(value, EngineJson.Options);
    private static Func<HttpContext, Task<IResult>> ResultRoute(Func<HttpContext, Task<IResult>> route) => route;
    private static async Task<JsonElement> ReadElementAsync(HttpContext context)
    {
        if (context.Request.ContentLength == 0) return EngineJson.EmptyObject;
        return (await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false)).RootElement.Clone();
    }
    private static async Task<JsonObject> ReadObjectAsync(HttpContext context)
        => JsonNode.Parse((await ReadElementAsync(context).ConfigureAwait(false)).GetRawText()) as JsonObject ?? throw new Microsoft.AspNetCore.Http.BadHttpRequestException("A JSON object is required.");
    private static string? GetBearer(string? value) => value?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? value[7..].Trim() : null;
    private static bool IsMutation(string method) => method is not ("GET" or "HEAD" or "OPTIONS");
    private static long ParseLong(string? value, long fallback) => long.TryParse(value, out var parsed) ? parsed : fallback;
    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        if (_application is null) return;
        await _application.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class ApiSession(string csrfToken, DateTimeOffset expiresAt)
    {
        public string CsrfToken { get; } = csrfToken;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
    }
    private sealed record LoginFailures(int Count, DateTimeOffset BlockedUntil);
}
