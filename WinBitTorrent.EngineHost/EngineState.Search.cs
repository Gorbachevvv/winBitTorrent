using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinBitTorrent.EngineHost;

internal sealed partial class EngineState
{
    private readonly ConcurrentDictionary<int, SearchJob> _searchJobs = new();
    private int _nextSearchId;
    private string _searchRoot = string.Empty;
    private string _searchEnginePath = string.Empty;
    private string _bundledPythonPath = string.Empty;
    private string _pythonPath = string.Empty;
    private string _searchStatePath = string.Empty;
    private readonly SemaphoreSlim _searchPluginGate = new(1, 1);

    private void InitializeSearchRuntime()
    {
        var backendRoot = ResolveBackendRoot();
        _bundledPythonPath = Path.Combine(backendRoot, "Python", "python.exe");
        _pythonPath = _bundledPythonPath;
        var bundled = Path.Combine(backendRoot, "SearchPlugins");
        _searchRoot = Path.Combine(_dataRoot, "SearchPlugins", "nova3");
        _searchEnginePath = Path.Combine(_searchRoot, "nova2.py");
        _searchStatePath = Path.Combine(_dataRoot, "search-plugins.json");
        Directory.CreateDirectory(_searchRoot);
        Directory.CreateDirectory(Path.Combine(_searchRoot, "engines"));

        foreach (var file in Directory.Exists(Path.Combine(bundled, "nova3"))
            ? Directory.EnumerateFiles(Path.Combine(bundled, "nova3"), "*.py", SearchOption.TopDirectoryOnly)
            : [])
        {
            File.Copy(file, Path.Combine(_searchRoot, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var file in Directory.Exists(Path.Combine(bundled, "engines"))
            ? Directory.EnumerateFiles(Path.Combine(bundled, "engines"), "*.py", SearchOption.TopDirectoryOnly)
            : [])
        {
            var target = Path.Combine(_searchRoot, "engines", Path.GetFileName(file));
            if (!File.Exists(target)) File.Copy(file, target);
        }
        var package = Path.Combine(_searchRoot, "engines", "__init__.py");
        if (!File.Exists(package)) File.WriteAllText(package, string.Empty, new UTF8Encoding(false));
    }

    private async Task<JsonElement> StartSearchAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        await ApplySearchPreferencesAsync(requireEnabled: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSearchRuntime();
        var pattern = payload.GetProperty("pattern").GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("Search pattern is required.");
        var category = payload.TryGetProperty("category", out var categoryValue) ? categoryValue.GetString() ?? "all" : "all";
        var requested = payload.TryGetProperty("plugins", out var pluginsValue) ? pluginsValue.GetString() ?? "all" : "all";
        var plugins = await ResolveRequestedPluginsAsync(requested, cancellationToken).ConfigureAwait(false);
        if (plugins.Length == 0) throw new InvalidOperationException("No enabled search plugins are available.");

        var id = Interlocked.Increment(ref _nextSearchId);
        var job = new SearchJob(id);
        if (!_searchJobs.TryAdd(id, job)) throw new InvalidOperationException("Unable to allocate a search job.");
        job.Work = RunSearchAsync(job, pattern, category, plugins);
        PruneSearchJobs();
        await AppendLogAsync(1, $"Search job {id} started with {plugins.Length} plugin(s).", cancellationToken).ConfigureAwait(false);
        return EngineJson.Element(id);
    }

    private JsonElement GetSearchStatus(JsonElement payload)
    {
        var id = payload.TryGetProperty("id", out var idValue) && idValue.ValueKind != JsonValueKind.Null ? idValue.GetInt32() : (int?)null;
        var result = new JsonArray();
        foreach (var job in _searchJobs.Values.OrderBy(static value => value.Id))
        {
            if (id is not null && job.Id != id.Value) continue;
            result.Add(new JsonObject
            {
                ["id"] = job.Id,
                ["status"] = job.Status,
                ["total"] = job.ResultCount
            });
        }
        return EngineJson.Element(result);
    }

    private JsonElement GetSearchResults(JsonElement payload)
    {
        var id = payload.GetProperty("id").GetInt32();
        if (!_searchJobs.TryGetValue(id, out var job)) throw new ArgumentException($"Search job {id} does not exist.");
        var limit = payload.TryGetProperty("limit", out var limitValue) ? Math.Clamp(limitValue.GetInt32(), 0, 20000) : 500;
        var offset = payload.TryGetProperty("offset", out var offsetValue) ? Math.Max(0, offsetValue.GetInt32()) : 0;
        JsonArray values;
        lock (job.Results)
            values = new JsonArray(job.Results.Skip(offset).Take(limit).Select(static value => value.DeepClone()).ToArray());
        return EngineJson.Element(new JsonObject
        {
            ["results"] = values,
            ["status"] = job.Status,
            ["total"] = job.ResultCount
        });
    }

    private async Task<JsonElement> GetSearchPluginsAsync(CancellationToken cancellationToken)
    {
        await ApplySearchPreferencesAsync(requireEnabled: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSearchRuntime();
        var capabilities = await ReadSearchCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        var enabled = await LoadSearchPluginStateAsync(cancellationToken).ConfigureAwait(false);
        var result = new JsonArray();
        foreach (var capability in capabilities)
        {
            var path = Path.Combine(_searchRoot, "engines", capability.Name + ".py");
            result.Add(new JsonObject
            {
                ["name"] = capability.Name,
                ["fullName"] = capability.FullName,
                ["url"] = capability.Url,
                ["enabled"] = !enabled.TryGetValue(capability.Name, out var isEnabled) || isEnabled,
                ["supportedCategories"] = new JsonArray(capability.Categories.Select(static value => (JsonNode?)value).ToArray()),
                ["version"] = ReadPluginVersion(path)
            });
        }
        return EngineJson.Element(result);
    }

    private async Task<JsonElement> HandleSearchActionAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        await ApplySearchPreferencesAsync(requireEnabled: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        var action = payload.GetProperty("action").GetString() ?? throw new ArgumentException("action is required.");
        var parameters = payload.GetProperty("parameters");
        string Parameter(string name) => parameters.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
        switch (action)
        {
            case "installPlugin":
                await InstallSearchPluginAsync(Parameter("sources"), cancellationToken).ConfigureAwait(false);
                break;
            case "enablePlugin":
                await SetSearchPluginsEnabledAsync(Parameter("names"), Parameter("enable") == "true", cancellationToken).ConfigureAwait(false);
                break;
            case "uninstallPlugin":
                await UninstallSearchPluginsAsync(Parameter("names"), cancellationToken).ConfigureAwait(false);
                break;
            case "updatePlugins":
                InitializeSearchRuntime();
                break;
            case "stop":
                StopSearch(Parameter("id"));
                break;
            default:
                throw new NotSupportedException($"Search action '{action}' is not implemented.");
        }
        return EngineJson.EmptyObject;
    }

    private async Task RunSearchAsync(SearchJob job, string pattern, string category, string[] plugins)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _pythonPath,
                WorkingDirectory = _searchRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-I");
            startInfo.ArgumentList.Add("-X");
            startInfo.ArgumentList.Add("utf8");
            startInfo.ArgumentList.Add(_searchEnginePath);
            startInfo.ArgumentList.Add(string.Join(',', plugins));
            startInfo.ArgumentList.Add(category);
            foreach (var word in pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                startInfo.ArgumentList.Add(word);
            await ApplySearchProxyEnvironmentAsync(startInfo, job.Lifetime.Token).ConfigureAwait(false);

            using var process = new Process { StartInfo = startInfo };
            job.Process = process;
            if (!process.Start()) throw new InvalidOperationException("Unable to start the bundled Python search helper.");
            var stdout = ReadSearchOutputAsync(job, process.StandardOutput, job.Lifetime.Token);
            var stderr = process.StandardError.ReadToEndAsync(job.Lifetime.Token);
            await process.WaitForExitAsync(job.Lifetime.Token).WaitAsync(TimeSpan.FromMinutes(3), job.Lifetime.Token).ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
            var error = (await stderr.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0 && !job.Lifetime.IsCancellationRequested)
                throw new InvalidOperationException(error.Length == 0 ? $"Search helper exited with code {process.ExitCode}." : error);
            job.Status = "Stopped";
        }
        catch (OperationCanceledException)
        {
            TryKill(job.Process);
            job.Status = "Stopped";
        }
        catch (Exception exception)
        {
            TryKill(job.Process);
            job.Error = exception.Message;
            job.Status = "Stopped";
            await AppendLogAsync(4, $"Search job {job.Id} failed: {exception.Message}", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            job.Process = null;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }
    }

    private static async Task ReadSearchOutputAsync(SearchJob job, StreamReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (TryParseSearchResult(line, out var result))
            {
                lock (job.Results)
                {
                    if (job.Results.Count >= 20000) continue;
                    var url = result["fileUrl"]?.GetValue<string>() ?? string.Empty;
                    if (!job.Urls.Add(url)) continue;
                    job.Results.Add(result);
                    job.ResultCount = job.Results.Count;
                }
            }
        }
    }

    private static bool TryParseSearchResult(string line, out JsonObject result)
    {
        result = [];
        var parts = line.Split('|');
        if (parts.Length < 6) return false;
        static long Number(string value) => long.TryParse(value.Trim(), out var parsed) && parsed >= 0 ? parsed : -1;
        var published = parts.Length > 7 && long.TryParse(parts[7].Trim(), out var timestamp) && timestamp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp).ToString("O") : string.Empty;
        result = new JsonObject
        {
            ["fileUrl"] = parts[0].Trim(),
            ["fileName"] = parts[1].Trim(),
            ["fileSize"] = Number(parts[2]),
            ["nbSeeders"] = Number(parts[3]),
            ["nbLeechers"] = Number(parts[4]),
            ["siteUrl"] = parts[5].Trim(),
            ["descrLink"] = parts.Length > 6 ? parts[6].Trim() : string.Empty,
            ["filePubDate"] = published
        };
        return !string.IsNullOrWhiteSpace(result["fileUrl"]?.GetValue<string>());
    }

    private async Task<SearchCapability[]> ReadSearchCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonPath,
            WorkingDirectory = _searchRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add("-X");
        startInfo.ArgumentList.Add("utf8");
        startInfo.ArgumentList.Add(_searchEnginePath);
        startInfo.ArgumentList.Add("--capabilities");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to inspect Nova search plugins.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = (await errorTask.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Length == 0 ? "Nova plugin inspection failed." : error);
        var document = XDocument.Parse(output);
        return document.Root?.Elements().Select(static element => new SearchCapability(
            element.Name.LocalName,
            element.Element("name")?.Value ?? element.Name.LocalName,
            element.Element("url")?.Value ?? string.Empty,
            (element.Element("categories")?.Value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToArray() ?? [];
    }

    private async Task<string[]> ResolveRequestedPluginsAsync(string requested, CancellationToken cancellationToken)
    {
        var capabilities = await ReadSearchCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        var available = capabilities.Select(static value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var state = await LoadSearchPluginStateAsync(cancellationToken).ConfigureAwait(false);
        if (requested.Equals("all", StringComparison.OrdinalIgnoreCase))
            return available.Where(name => !state.TryGetValue(name, out var enabled) || enabled).ToArray();
        return requested.Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(available.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<Dictionary<string, bool>> LoadSearchPluginStateAsync(CancellationToken cancellationToken)
    {
        await _searchPluginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_searchStatePath)) return new(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(await File.ReadAllTextAsync(_searchStatePath, cancellationToken).ConfigureAwait(false), EngineJson.Options)
                ?? new(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _searchPluginGate.Release();
        }
    }

    private async Task SaveSearchPluginStateAsync(Dictionary<string, bool> state, CancellationToken cancellationToken)
    {
        await _searchPluginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporary = _searchStatePath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, EngineJson.Options), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _searchStatePath, overwrite: true);
        }
        finally
        {
            _searchPluginGate.Release();
        }
    }

    private async Task InstallSearchPluginAsync(string source, CancellationToken cancellationToken)
    {
        EnsureSearchRuntime();
        byte[] bytes;
        string fileName;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            using var client = await CreateHttpClientAsync("proxy_misc", cancellationToken).ConfigureAwait(false);
            bytes = await client.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
            fileName = Path.GetFileName(uri.LocalPath);
        }
        else
        {
            var fullPath = Path.GetFullPath(source);
            bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            fileName = Path.GetFileName(fullPath);
        }
        if (!fileName.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A Nova plugin must be a .py file.");
        var pluginName = Path.GetFileNameWithoutExtension(fileName);
        if (!Regex.IsMatch(pluginName, "^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)) throw new ArgumentException("The plugin file name is invalid.");
        var target = Path.Combine(_searchRoot, "engines", pluginName + ".py");
        await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
        try { _ = await ReadSearchCapabilitiesAsync(cancellationToken).ConfigureAwait(false); }
        catch { File.Delete(target); throw; }
        var state = await LoadSearchPluginStateAsync(cancellationToken).ConfigureAwait(false);
        state[pluginName] = true;
        await SaveSearchPluginStateAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetSearchPluginsEnabledAsync(string names, bool enabled, CancellationToken cancellationToken)
    {
        var state = await LoadSearchPluginStateAsync(cancellationToken).ConfigureAwait(false);
        foreach (var name in names.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) state[name] = enabled;
        await SaveSearchPluginStateAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task UninstallSearchPluginsAsync(string names, CancellationToken cancellationToken)
    {
        var state = await LoadSearchPluginStateAsync(cancellationToken).ConfigureAwait(false);
        foreach (var name in names.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Regex.IsMatch(name, "^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)) continue;
            var path = Path.Combine(_searchRoot, "engines", name + ".py");
            if (File.Exists(path)) File.Delete(path);
            state.Remove(name);
        }
        await SaveSearchPluginStateAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private void StopSearch(string idText)
    {
        if (!int.TryParse(idText, out var id) || !_searchJobs.TryGetValue(id, out var job)) return;
        job.Lifetime.Cancel();
        TryKill(job.Process);
    }

    private async Task StopSearchServicesAsync()
    {
        foreach (var job in _searchJobs.Values)
        {
            job.Lifetime.Cancel();
            TryKill(job.Process);
        }
        var work = _searchJobs.Values.Select(static value => value.Work).Where(static value => value is not null).Cast<Task>().ToArray();
        try { await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
        foreach (var job in _searchJobs.Values) job.Lifetime.Dispose();
        _searchPluginGate.Dispose();
    }

    private async Task ApplySearchProxyEnvironmentAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        var preferences = await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
        if (!preferences.TryGetProperty("proxy_misc", out var enabled) || !enabled.GetBoolean()) return;
        var type = preferences.GetProperty("proxy_type").GetString();
        if (string.Equals(type, "None", StringComparison.OrdinalIgnoreCase)) return;
        var host = preferences.GetProperty("proxy_ip").GetString();
        var port = preferences.GetProperty("proxy_port").GetInt32();
        if (string.IsNullOrWhiteSpace(host) || port <= 0) return;
        var scheme = type?.StartsWith("SOCKS", StringComparison.OrdinalIgnoreCase) == true ? "socks5" : "http";
        var credential = preferences.GetProperty("proxy_auth_enabled").GetBoolean()
            ? $"{Uri.EscapeDataString(preferences.GetProperty("proxy_username").GetString() ?? string.Empty)}:{Uri.EscapeDataString(preferences.GetProperty("proxy_password").GetString() ?? string.Empty)}@" : string.Empty;
        var value = $"{scheme}://{credential}{host}:{port}";
        startInfo.Environment["HTTP_PROXY"] = value;
        startInfo.Environment["HTTPS_PROXY"] = value;
    }

    private static void TryKill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
    }

    private void PruneSearchJobs()
    {
        foreach (var job in _searchJobs.Values.Where(static value => value.CompletedAt < DateTimeOffset.UtcNow.AddHours(-1)).OrderBy(static value => value.CompletedAt).ToArray())
        {
            if (_searchJobs.TryRemove(job.Id, out var removed)) removed.Lifetime.Dispose();
        }
    }

    private void EnsureSearchRuntime()
    {
        if (!File.Exists(_pythonPath)) throw new FileNotFoundException("The bundled Python runtime was not found.", _pythonPath);
        if (!File.Exists(_searchEnginePath)) throw new FileNotFoundException("The Nova search runtime was not found.", _searchEnginePath);
    }

    private async Task ApplySearchPreferencesAsync(bool requireEnabled, CancellationToken cancellationToken)
    {
        var preferences = await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
        if (requireEnabled && preferences.TryGetProperty("search_enabled", out var enabled) && !enabled.GetBoolean())
            throw new InvalidOperationException("The torrent search engine is disabled in settings.");
        var configured = preferences.TryGetProperty("python_executable_path", out var value)
            ? value.GetString()?.Trim() : null;
        _pythonPath = string.IsNullOrWhiteSpace(configured)
            ? _bundledPythonPath
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
    }

    private static string ResolveBackendRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("WINBITTORRENT_BACKEND_ROOT");
        var candidates = new[]
        {
            overridden,
            Path.Combine(AppContext.BaseDirectory, "Backend"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Backend")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Backend"))
        };
        return candidates.FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            ?? Path.Combine(AppContext.BaseDirectory, "Backend");
    }

    private static string ReadPluginVersion(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        foreach (var line in File.ReadLines(path).Take(30))
            if (Regex.Match(line, @"^\s*#\s*VERSION\s*:\s*(.+)$", RegexOptions.IgnoreCase) is { Success: true } match) return match.Groups[1].Value.Trim();
        return string.Empty;
    }

    private sealed record SearchCapability(string Name, string FullName, string Url, string[] Categories);

    private sealed class SearchJob(int id)
    {
        public int Id { get; } = id;
        public string Status { get; set; } = "Running";
        public string? Error { get; set; }
        public int ResultCount;
        public List<JsonObject> Results { get; } = [];
        public HashSet<string> Urls { get; } = new(StringComparer.OrdinalIgnoreCase);
        public CancellationTokenSource Lifetime { get; } = new();
        public Process? Process { get; set; }
        public Task? Work { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
    }
}
