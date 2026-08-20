using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace WinBitTorrent.EngineHost;

internal sealed partial class EngineState
{
    private async Task<JsonElement> GetRssItemsAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var withData = !payload.TryGetProperty("withData", out var withDataValue) || withDataValue.GetBoolean();
        var root = new JsonObject();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "SELECT path, kind, url, title, last_refresh, error FROM rss_items ORDER BY path";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var path = reader.GetString(0);
                var target = EnsureRssContainer(root, ParentRssPath(path));
                var name = RssName(path);
                if (reader.GetInt32(1) == 0)
                {
                    target[name] ??= new JsonObject { ["isFolder"] = true };
                    continue;
                }

                var feed = new JsonObject
                {
                    ["isFolder"] = false,
                    ["url"] = reader.GetString(2),
                    ["title"] = string.IsNullOrWhiteSpace(reader.GetString(3)) ? name : reader.GetString(3),
                    ["lastBuildDate"] = reader.GetInt64(4),
                    ["isLoading"] = false,
                    ["hasError"] = !string.IsNullOrEmpty(reader.GetString(5))
                };
                if (withData)
                    feed["articles"] = await LoadRssArticlesWithinGateAsync(path, cancellationToken).ConfigureAwait(false);
                target[name] = feed;
            }
        }
        finally
        {
            _databaseGate.Release();
        }
        return EngineJson.Element(root);
    }

    private async Task<JsonArray> LoadRssArticlesWithinGateAsync(string feedPath, CancellationToken cancellationToken)
    {
        var articles = new JsonArray();
        await using var command = _database.CreateCommand();
        command.CommandText = "SELECT article_id, title, link, download_url, description, published FROM rss_articles WHERE feed_path=$path ORDER BY published DESC, article_id LIMIT 10000";
        command.Parameters.AddWithValue("$path", feedPath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            articles.Add(new JsonObject
            {
                ["id"] = reader.GetString(0),
                ["title"] = reader.GetString(1),
                ["link"] = string.IsNullOrWhiteSpace(reader.GetString(3)) ? reader.GetString(2) : reader.GetString(3),
                ["description"] = reader.GetString(4),
                ["date"] = reader.GetInt64(5)
            });
        }
        return articles;
    }

    private async Task<JsonElement> GetRssRulesAsync(CancellationToken cancellationToken)
    {
        var result = new JsonObject();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "SELECT name, definition_json FROM rss_rules ORDER BY name";
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

    private async Task<JsonElement> GetRssMatchingArticlesAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var ruleName = payload.GetProperty("ruleName").GetString() ?? throw new ArgumentException("ruleName is required.");
        var rule = await LoadRssRuleAsync(ruleName, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"RSS rule '{ruleName}' does not exist.");
        var result = new JsonArray();
        foreach (var article in await LoadAllRssArticlesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (RssRuleMatches(rule, article)) result.Add(article.Title);
        }
        return EngineJson.Element(result);
    }

    private async Task<JsonElement> HandleRssActionAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var action = payload.GetProperty("action").GetString() ?? throw new ArgumentException("action is required.");
        var parameters = payload.GetProperty("parameters");
        string Parameter(string name) => parameters.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

        switch (action)
        {
            case "addFolder":
                await AddRssFolderAsync(Parameter("path"), cancellationToken).ConfigureAwait(false);
                break;
            case "addFeed":
                await AddRssFeedAsync(Parameter("url"), Parameter("path"), cancellationToken).ConfigureAwait(false);
                break;
            case "refreshItem":
                await RefreshRssItemAsync(Parameter("itemPath"), cancellationToken).ConfigureAwait(false);
                break;
            case "removeItem":
                await RemoveRssItemAsync(Parameter("path"), cancellationToken).ConfigureAwait(false);
                break;
            case "setRule":
                await SetRssRuleAsync(Parameter("ruleName"), Parameter("ruleDef"), cancellationToken).ConfigureAwait(false);
                break;
            case "removeRule":
                await RemoveRssRuleAsync(Parameter("ruleName"), cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException($"RSS action '{action}' is not implemented.");
        }
        return EngineJson.EmptyObject;
    }

    private async Task AddRssFolderAsync(string path, CancellationToken cancellationToken)
    {
        path = NormalizeRssPath(path);
        if (path.Length == 0) throw new ArgumentException("RSS folder path is required.");
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRssParentFoldersWithinGateAsync(path, cancellationToken).ConfigureAwait(false);
            await using var command = _database.CreateCommand();
            command.CommandText = "INSERT INTO rss_items(path, kind) VALUES($path, 0)";
            command.Parameters.AddWithValue("$path", path);
            try { await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            { throw new ArgumentException($"RSS item '{path}' already exists.", exception); }
        }
        finally
        {
            _databaseGate.Release();
        }
        await AppendLogAsync(1, $"RSS folder added: {path}", cancellationToken).ConfigureAwait(false);
    }

    private async Task AddRssFeedAsync(string url, string path, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("RSS feed URL must use HTTP or HTTPS.");
        path = NormalizeRssPath(string.IsNullOrWhiteSpace(path) ? url : path);
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRssParentFoldersWithinGateAsync(path, cancellationToken).ConfigureAwait(false);
            await using var command = _database.CreateCommand();
            command.CommandText = "INSERT INTO rss_items(path, kind, url, title) VALUES($path, 1, $url, $title)";
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$url", url);
            command.Parameters.AddWithValue("$title", RssName(path));
            try { await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            { throw new ArgumentException("An RSS item with the same path or URL already exists.", exception); }
        }
        finally
        {
            _databaseGate.Release();
        }
        await RefreshRssFeedAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureRssParentFoldersWithinGateAsync(string path, CancellationToken cancellationToken)
    {
        var components = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        for (var index = 0; index < components.Length - 1; index++)
        {
            current = current.Length == 0 ? components[index] : $"{current}\\{components[index]}";
            await using var command = _database.CreateCommand();
            command.CommandText = "INSERT INTO rss_items(path, kind) VALUES($path, 0) ON CONFLICT(path) DO NOTHING";
            command.Parameters.AddWithValue("$path", current);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RemoveRssItemAsync(string path, CancellationToken cancellationToken)
    {
        path = NormalizeRssPath(path);
        if (path.Length == 0) throw new ArgumentException("The RSS root cannot be removed.");
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var transaction = await _database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var articles = _database.CreateCommand())
            {
                articles.Transaction = (SqliteTransaction)transaction;
                articles.CommandText = "DELETE FROM rss_articles WHERE feed_path=$path OR feed_path LIKE $children ESCAPE '~'";
                articles.Parameters.AddWithValue("$path", path);
                articles.Parameters.AddWithValue("$children", EscapeLike(path) + "\\%");
                await articles.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var items = _database.CreateCommand())
            {
                items.Transaction = (SqliteTransaction)transaction;
                items.CommandText = "DELETE FROM rss_items WHERE path=$path OR path LIKE $children ESCAPE '~'";
                items.Parameters.AddWithValue("$path", path);
                items.Parameters.AddWithValue("$children", EscapeLike(path) + "\\%");
                if (await items.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                    throw new ArgumentException($"RSS item '{path}' does not exist.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task RefreshRssItemAsync(string path, CancellationToken cancellationToken)
    {
        path = NormalizeRssPath(path);
        var feeds = new List<string>();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = path.Length == 0
                ? "SELECT path FROM rss_items WHERE kind=1 ORDER BY path"
                : "SELECT path FROM rss_items WHERE kind=1 AND (path=$path OR path LIKE $children ESCAPE '~') ORDER BY path";
            if (path.Length != 0)
            {
                command.Parameters.AddWithValue("$path", path);
                command.Parameters.AddWithValue("$children", EscapeLike(path) + "\\%");
            }
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) feeds.Add(reader.GetString(0));
        }
        finally
        {
            _databaseGate.Release();
        }
        if (feeds.Count == 0 && path.Length != 0) throw new ArgumentException($"RSS item '{path}' does not exist.");
        foreach (var feed in feeds) await RefreshRssFeedAsync(feed, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshRssFeedAsync(string path, CancellationToken cancellationToken)
    {
        string url;
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "SELECT url FROM rss_items WHERE path=$path AND kind=1";
            command.Parameters.AddWithValue("$path", path);
            url = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
                ?? throw new ArgumentException($"RSS feed '{path}' does not exist.");
        }
        finally
        {
            _databaseGate.Release();
        }

        try
        {
            using var client = await CreateHttpClientAsync("proxy_rss", cancellationToken).ConfigureAwait(false);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            var parsed = ParseRssDocument(document, url);
            var maxArticles = await GetIntPreferenceAsync("rss_max_articles_per_feed", 50, cancellationToken).ConfigureAwait(false);
            var newArticles = new List<RssArticle>();
            await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var transaction = await _database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                foreach (var article in parsed.Articles.Take(Math.Clamp(maxArticles, 1, 10000)))
                {
                    await using var exists = _database.CreateCommand();
                    exists.Transaction = (SqliteTransaction)transaction;
                    exists.CommandText = "SELECT 1 FROM rss_articles WHERE feed_path=$feed AND article_id=$id";
                    exists.Parameters.AddWithValue("$feed", path);
                    exists.Parameters.AddWithValue("$id", article.Id);
                    if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null) newArticles.Add(article with { FeedPath = path });

                    await using var insert = _database.CreateCommand();
                    insert.Transaction = (SqliteTransaction)transaction;
                    insert.CommandText = """
                        INSERT INTO rss_articles(feed_path, article_id, title, link, download_url, description, published)
                        VALUES($feed, $id, $title, $link, $download, $description, $published)
                        ON CONFLICT(feed_path, article_id) DO UPDATE SET title=excluded.title, link=excluded.link,
                            download_url=excluded.download_url, description=excluded.description, published=excluded.published
                        """;
                    insert.Parameters.AddWithValue("$feed", path);
                    insert.Parameters.AddWithValue("$id", article.Id);
                    insert.Parameters.AddWithValue("$title", article.Title);
                    insert.Parameters.AddWithValue("$link", article.Link);
                    insert.Parameters.AddWithValue("$download", article.DownloadUrl);
                    insert.Parameters.AddWithValue("$description", article.Description);
                    insert.Parameters.AddWithValue("$published", article.Published);
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await using (var trim = _database.CreateCommand())
                {
                    trim.Transaction = (SqliteTransaction)transaction;
                    trim.CommandText = "DELETE FROM rss_articles WHERE feed_path=$feed AND article_id NOT IN (SELECT article_id FROM rss_articles WHERE feed_path=$feed ORDER BY published DESC LIMIT $limit)";
                    trim.Parameters.AddWithValue("$feed", path);
                    trim.Parameters.AddWithValue("$limit", Math.Clamp(maxArticles, 1, 10000));
                    await trim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await using (var update = _database.CreateCommand())
                {
                    update.Transaction = (SqliteTransaction)transaction;
                    update.CommandText = "UPDATE rss_items SET title=$title, last_refresh=$time, error='' WHERE path=$path";
                    update.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(parsed.Title) ? RssName(path) : parsed.Title);
                    update.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    update.Parameters.AddWithValue("$path", path);
                    await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _databaseGate.Release();
            }
            await ProcessRssRulesAsync(newArticles, cancellationToken).ConfigureAwait(false);
            await AppendLogAsync(2, $"RSS feed '{path}' refreshed; {newArticles.Count} new article(s).", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _databaseGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await using var command = _database.CreateCommand();
                command.CommandText = "UPDATE rss_items SET error=$error, last_refresh=$time WHERE path=$path";
                command.Parameters.AddWithValue("$error", exception.Message);
                command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("$path", path);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                _databaseGate.Release();
            }
            throw;
        }
    }

    private async Task<HttpClient> CreateHttpClientAsync(string proxyPreference, CancellationToken cancellationToken)
    {
        var preferences = await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
        var cookies = new CookieContainer();
        await ApplyCookiesAsync(cookies, cancellationToken).ConfigureAwait(false);
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            CookieContainer = cookies,
            UseCookies = true,
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };
        if (preferences.TryGetProperty(proxyPreference, out var useProxy) && useProxy.GetBoolean()
            && preferences.TryGetProperty("proxy_type", out var proxyType)
            && !string.Equals(proxyType.GetString(), "None", StringComparison.OrdinalIgnoreCase))
        {
            var host = preferences.GetProperty("proxy_ip").GetString();
            var port = preferences.GetProperty("proxy_port").GetInt32();
            if (!string.IsNullOrWhiteSpace(host) && port > 0)
            {
                var scheme = proxyType.GetString()?.StartsWith("SOCKS", StringComparison.OrdinalIgnoreCase) == true ? "socks5" : "http";
                var proxy = new WebProxy(new Uri($"{scheme}://{host}:{port}"));
                if (preferences.TryGetProperty("proxy_auth_enabled", out var auth) && auth.GetBoolean())
                    proxy.Credentials = new NetworkCredential(preferences.GetProperty("proxy_username").GetString(), preferences.GetProperty("proxy_password").GetString());
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
        }
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(2) };
    }

    private static RssFeed ParseRssDocument(XDocument document, string sourceUrl)
    {
        var root = document.Root ?? throw new InvalidDataException("RSS document is empty.");
        var isAtom = root.Name.LocalName.Equals("feed", StringComparison.OrdinalIgnoreCase);
        var container = isAtom ? root : root.Descendants().FirstOrDefault(static value => value.Name.LocalName == "channel") ?? root;
        var title = ChildText(container, "title");
        var elements = isAtom
            ? container.Elements().Where(static value => value.Name.LocalName == "entry")
            : container.Descendants().Where(static value => value.Name.LocalName == "item");
        var articles = new List<RssArticle>();
        foreach (var item in elements)
        {
            var articleTitle = ChildText(item, "title");
            var link = isAtom
                ? item.Elements().FirstOrDefault(static value => value.Name.LocalName == "link" && ((string?)value.Attribute("rel") is null or "alternate"))?.Attribute("href")?.Value ?? string.Empty
                : ChildText(item, "link");
            var enclosure = item.Elements().FirstOrDefault(static value => value.Name.LocalName is "enclosure" or "link"
                && ((string?)value.Attribute("rel") == "enclosure" || value.Name.LocalName == "enclosure"))?.Attribute("url")?.Value
                ?? item.Elements().FirstOrDefault(static value => value.Name.LocalName == "link" && (string?)value.Attribute("rel") == "enclosure")?.Attribute("href")?.Value
                ?? string.Empty;
            var id = ChildText(item, "guid");
            if (string.IsNullOrWhiteSpace(id)) id = ChildText(item, "id");
            if (string.IsNullOrWhiteSpace(id)) id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{link}\n{articleTitle}"))).ToLowerInvariant();
            var dateText = ChildText(item, "pubDate");
            if (string.IsNullOrWhiteSpace(dateText)) dateText = ChildText(item, "published");
            if (string.IsNullOrWhiteSpace(dateText)) dateText = ChildText(item, "updated");
            var published = DateTimeOffset.TryParse(dateText, out var date) ? date.ToUnixTimeSeconds() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var description = ChildText(item, "description");
            if (string.IsNullOrWhiteSpace(description)) description = ChildText(item, "summary");
            if (string.IsNullOrWhiteSpace(description)) description = ChildText(item, "content");
            articles.Add(new RssArticle(string.Empty, id, articleTitle, MakeAbsolute(sourceUrl, link), MakeAbsolute(sourceUrl, enclosure), description, published));
        }
        return new RssFeed(title, articles.OrderByDescending(static article => article.Published).ToArray());
    }

    private async Task SetRssRuleAsync(string name, string json, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("RSS rule name is required.");
        var definition = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("RSS rule definition is invalid.");
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "INSERT INTO rss_rules(name, definition_json) VALUES($name, $definition) ON CONFLICT(name) DO UPDATE SET definition_json=excluded.definition_json";
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$definition", definition.ToJsonString(EngineJson.Options));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task RemoveRssRuleAsync(string name, CancellationToken cancellationToken)
    {
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "DELETE FROM rss_rules WHERE name=$name";
            command.Parameters.AddWithValue("$name", name);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task<JsonObject?> LoadRssRuleAsync(string name, CancellationToken cancellationToken)
    {
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "SELECT definition_json FROM rss_rules WHERE name=$name";
            command.Parameters.AddWithValue("$name", name);
            var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            return json is null ? null : JsonNode.Parse(json) as JsonObject;
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task<List<RssArticle>> LoadAllRssArticlesAsync(CancellationToken cancellationToken)
    {
        var result = new List<RssArticle>();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "SELECT feed_path, article_id, title, link, download_url, description, published FROM rss_articles ORDER BY published DESC";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                result.Add(new RssArticle(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6)));
        }
        finally
        {
            _databaseGate.Release();
        }
        return result;
    }

    private async Task ProcessRssRulesAsync(IEnumerable<RssArticle> articles, CancellationToken cancellationToken)
    {
        var preferences = await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
        if (preferences.TryGetProperty("rss_auto_downloading_enabled", out var enabled) && !enabled.GetBoolean()) return;
        var rulesElement = await GetRssRulesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (name, node) in JsonNode.Parse(rulesElement.GetRawText())!.AsObject())
        {
            if (node is not JsonObject rule || rule["enabled"]?.GetValue<bool>() == false) continue;
            foreach (var article in articles)
            {
                if (!RssRuleMatches(rule, article) || string.IsNullOrWhiteSpace(article.DownloadUrl) && string.IsNullOrWhiteSpace(article.Link)) continue;
                if (!await MarkRssDownloadAsync(name, article, cancellationToken).ConfigureAwait(false)) continue;
                var request = new JsonObject
                {
                    ["torrentFiles"] = new JsonArray(),
                    ["urls"] = new JsonArray(article.DownloadUrl.Length == 0 ? article.Link : article.DownloadUrl),
                    ["savePath"] = rule["savePath"]?.GetValue<string>() ?? string.Empty,
                    ["category"] = rule["assignedCategory"]?.GetValue<string>() ?? string.Empty,
                    ["tags"] = string.Empty,
                    ["startTorrent"] = rule["addPaused"]?.GetValue<bool>() != true
                };
                try { await AddTorrentsAsync(EngineJson.Element(request), cancellationToken).ConfigureAwait(false); }
                catch
                {
                    await UnmarkRssDownloadAsync(name, article, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
        }
    }

    private static bool RssRuleMatches(JsonObject rule, RssArticle article)
    {
        if (rule["enabled"]?.GetValue<bool>() == false) return false;
        if (rule["affectedFeeds"] is JsonArray feeds && feeds.Count > 0
            && !feeds.Any(feed => FeedMatches(article.FeedPath, feed?.GetValue<string>() ?? string.Empty))) return false;
        var regex = rule["useRegex"]?.GetValue<bool>() == true;
        if (!MatchesExpressions(article.Title, rule["mustContain"]?.GetValue<string>() ?? string.Empty, regex, requireMatch: true)) return false;
        if (!MatchesExpressions(article.Title, rule["mustNotContain"]?.GetValue<string>() ?? string.Empty, regex, requireMatch: false)) return false;
        var episode = rule["episodeFilter"]?.GetValue<string>() ?? string.Empty;
        return episode.Length == 0 || MatchesExpressions(article.Title, episode, regex: true, requireMatch: true);
    }

    private static bool MatchesExpressions(string input, string expressions, bool regex, bool requireMatch)
    {
        var values = expressions.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0) return true;
        bool Match(string expression)
        {
            try
            {
                var pattern = regex ? expression : Regex.Escape(expression).Replace("\\*", ".*").Replace("\\?", ".");
                return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException) { return false; }
        }
        var any = values.Any(Match);
        return requireMatch ? any : !any;
    }

    private async Task<bool> MarkRssDownloadAsync(string rule, RssArticle article, CancellationToken cancellationToken)
    {
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "INSERT INTO rss_downloads(rule_name, feed_path, article_id, created_at) VALUES($rule, $feed, $id, $time) ON CONFLICT DO NOTHING";
            command.Parameters.AddWithValue("$rule", rule);
            command.Parameters.AddWithValue("$feed", article.FeedPath);
            command.Parameters.AddWithValue("$id", article.Id);
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 0;
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task UnmarkRssDownloadAsync(string rule, RssArticle article, CancellationToken cancellationToken)
    {
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _database.CreateCommand();
            command.CommandText = "DELETE FROM rss_downloads WHERE rule_name=$rule AND feed_path=$feed AND article_id=$id";
            command.Parameters.AddWithValue("$rule", rule);
            command.Parameters.AddWithValue("$feed", article.FeedPath);
            command.Parameters.AddWithValue("$id", article.Id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task<int> GetIntPreferenceAsync(string key, int fallback, CancellationToken cancellationToken)
    {
        var preferences = await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
        return preferences.TryGetProperty(key, out var value) && value.TryGetInt32(out var result) ? result : fallback;
    }

    private async Task ImportLegacyRssAsync(CancellationToken cancellationToken)
    {
        var marker = Path.Combine(_dataRoot, "LegacyRss", ".imported");
        if (File.Exists(marker)) return;
        var configRoot = Path.Combine(_dataRoot, "LegacyRss", "config");
        var feedsPath = Path.Combine(configRoot, "feeds.json");
        var rulesPath = Path.Combine(configRoot, "download_rules.json");
        if (!File.Exists(feedsPath) && !File.Exists(rulesPath)) return;

        if (File.Exists(feedsPath) && JsonNode.Parse(await File.ReadAllTextAsync(feedsPath, cancellationToken).ConfigureAwait(false)) is JsonObject feeds)
            await ImportLegacyRssFolderAsync(feeds, string.Empty, cancellationToken).ConfigureAwait(false);
        if (File.Exists(rulesPath) && JsonNode.Parse(await File.ReadAllTextAsync(rulesPath, cancellationToken).ConfigureAwait(false)) is JsonObject rules)
        {
            foreach (var (name, definition) in rules)
                if (definition is JsonObject rule) await SetRssRuleAsync(name, rule.ToJsonString(EngineJson.Options), cancellationToken).ConfigureAwait(false);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker, "1", cancellationToken).ConfigureAwait(false);
    }

    private async Task ImportLegacyRssFolderAsync(JsonObject folder, string parent, CancellationToken cancellationToken)
    {
        foreach (var (name, node) in folder)
        {
            var path = parent.Length == 0 ? name : $"{parent}\\{name}";
            if (node is JsonValue value && value.TryGetValue<string>(out var legacyUrl))
            {
                if (!string.IsNullOrWhiteSpace(legacyUrl)) await InsertImportedFeedAsync(path, legacyUrl, cancellationToken).ConfigureAwait(false);
            }
            else if (node is JsonObject item && item["url"]?.GetValue<string>() is { Length: > 0 } url)
                await InsertImportedFeedAsync(path, url, cancellationToken).ConfigureAwait(false);
            else if (node is JsonObject child)
            {
                try { await AddRssFolderAsync(path, cancellationToken).ConfigureAwait(false); } catch (ArgumentException) { }
                await ImportLegacyRssFolderAsync(child, path, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task InsertImportedFeedAsync(string path, string url, CancellationToken cancellationToken)
    {
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRssParentFoldersWithinGateAsync(path, cancellationToken).ConfigureAwait(false);
            await using var command = _database.CreateCommand();
            command.CommandText = "INSERT INTO rss_items(path, kind, url, title) VALUES($path, 1, $url, $title) ON CONFLICT DO NOTHING";
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$url", url);
            command.Parameters.AddWithValue("$title", RssName(path));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private static JsonObject EnsureRssContainer(JsonObject root, string path)
    {
        var current = root;
        foreach (var component in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current[component] is not JsonObject folder)
            {
                folder = new JsonObject { ["isFolder"] = true };
                current[component] = folder;
            }
            current = folder;
        }
        return current;
    }

    private static string NormalizeRssPath(string value)
        => value.Trim().Replace('/', '\\').Trim('\\');
    private static string ParentRssPath(string value) => value.Contains('\\') ? value[..value.LastIndexOf('\\')] : string.Empty;
    private static string RssName(string value) => value.Contains('\\') ? value[(value.LastIndexOf('\\') + 1)..] : value;
    private static string EscapeLike(string value) => value.Replace("~", "~~").Replace("%", "~%").Replace("_", "~_");
    private static bool FeedMatches(string path, string affected) => path.Equals(NormalizeRssPath(affected), StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(NormalizeRssPath(affected) + "\\", StringComparison.OrdinalIgnoreCase);
    private static string ChildText(XElement element, string name) => element.Elements().FirstOrDefault(value => value.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? string.Empty;
    private static string MakeAbsolute(string source, string value) => Uri.TryCreate(value, UriKind.Absolute, out var absolute) ? absolute.ToString()
        : Uri.TryCreate(new Uri(source), value, out var relative) ? relative.ToString() : value;

    private sealed record RssFeed(string Title, IReadOnlyList<RssArticle> Articles);
    private sealed record RssArticle(string FeedPath, string Id, string Title, string Link, string DownloadUrl, string Description, long Published);
}
