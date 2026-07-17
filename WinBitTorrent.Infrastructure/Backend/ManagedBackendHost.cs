using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Infrastructure.Api;
using WinBitTorrent.Infrastructure.Storage;

namespace WinBitTorrent.Infrastructure.Backend;

public sealed partial class ManagedBackendHost : IManagedBackendHost
{
    public const string RequiredQbittorrentVersion = "v5.2.3";
    public const string RequiredWebApiVersion = "2.15.1";

    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<ManagedBackendHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _recentOutput = [];
    private Process? _process;
    private NativeJob? _job;
    private QbittorrentApi? _api;
    private TaskCompletionSource<string>? _temporaryPassword;
    private bool _stopping;
    private int _restartAttempts;

    public ManagedBackendHost(ICredentialStore credentialStore, ILogger<ManagedBackendHost> logger)
    {
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public BackendSession? Session { get; private set; }
    public bool IsRunning => _process is { HasExited: false };
    public event EventHandler<string>? OutputReceived;
    public event EventHandler<Exception>? Failed;

    public async Task<BackendSession> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning && Session is not null)
                return Session;

            _stopping = false;
            AppPaths.EnsureCreated();

            var executable = FindBackendExecutable();
            var backendDirectory = Path.GetDirectoryName(executable)!;
            var port = await GetOrCreatePortAsync(cancellationToken).ConfigureAwait(false);
            EnsureSecureConfiguration(port);
            SeedBundledSearchPlugins(backendDirectory);

            _temporaryPassword = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _recentOutput.Clear();

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = backendDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add($"--profile={AppPaths.BackendProfile}");
            startInfo.ArgumentList.Add($"--webui-port={port}");
            startInfo.ArgumentList.Add("--confirm-legal-notice");

            ConfigurePython(startInfo, backendDirectory);

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnOutputDataReceived;
            _process.ErrorDataReceived += OnOutputDataReceived;
            _process.Exited += OnProcessExited;

            if (!_process.Start())
                throw new InvalidOperationException("Unable to start the bundled qBittorrent backend.");

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _job = new NativeJob();
            _job.Assign(_process.Handle);

            var baseAddress = new Uri($"http://127.0.0.1:{port}/");
            _api = await AuthenticateAsync(baseAddress, cancellationToken).ConfigureAwait(false);

            var version = (await _api.Application.GetVersionAsync(cancellationToken).ConfigureAwait(false)).Trim();
            var apiVersion = (await _api.Application.GetWebApiVersionAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (!version.Equals(RequiredQbittorrentVersion, StringComparison.OrdinalIgnoreCase)
                || !apiVersion.Equals(RequiredWebApiVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Unsupported backend contract. Expected {RequiredQbittorrentVersion}/API {RequiredWebApiVersion}, got {version}/API {apiVersion}.");
            }

            Session = new BackendSession(_process.Id, baseAddress, version, apiVersion, DateTimeOffset.Now);
            _restartAttempts = 0;
            _logger.LogInformation("Managed qBittorrent {Version} started on {Address} as process {ProcessId}.", version, baseAddress, _process.Id);
            return Session;
        }
        catch
        {
            await StopCoreAsync(force: true, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(force, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<QbittorrentApi> AuthenticateAsync(Uri baseAddress, CancellationToken cancellationToken)
    {
        var profile = ServerProfile.CreateLocal(baseAddress);
        var existingKey = await _credentialStore.GetSecretAsync(ServerProfile.LocalProfileId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existingKey))
        {
            var keyedApi = QbittorrentApi.Create(profile, existingKey);
            if (await CanReadVersionAsync(keyedApi, cancellationToken).ConfigureAwait(false))
                return keyedApi;
            await keyedApi.DisposeAsync().ConfigureAwait(false);
        }

        string temporaryPassword;
        try
        {
            temporaryPassword = await _temporaryPassword!.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                "qBittorrent started but did not expose a temporary administrator password. " +
                "Remove the managed backend profile to bootstrap it again.", exception);
        }

        var loginApi = QbittorrentApi.Create(profile);
        Exception? lastError = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited();
            try
            {
                await loginApi.Auth.LoginAsync("admin", temporaryPassword, cancellationToken).ConfigureAwait(false);
                var apiKey = await loginApi.Application.RotateApiKeyAsync(cancellationToken).ConfigureAwait(false);
                await _credentialStore.SetSecretAsync(ServerProfile.LocalProfileId, apiKey, cancellationToken).ConfigureAwait(false);
                await loginApi.DisposeAsync().ConfigureAwait(false);

                var keyedApi = QbittorrentApi.Create(profile, apiKey);
                if (!await CanReadVersionAsync(keyedApi, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("The generated qBittorrent API key could not be verified.");
                return keyedApi;
            }
            catch (Exception exception) when (exception is HttpRequestException or QbittorrentApiException)
            {
                lastError = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }

        await loginApi.DisposeAsync().ConfigureAwait(false);
        throw new InvalidOperationException("Unable to authenticate with the local qBittorrent backend.", lastError);
    }

    private static async Task<bool> CanReadVersionAsync(QbittorrentApi api, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                var value = await api.Application.GetVersionAsync(cancellationToken).ConfigureAwait(false);
                return !string.IsNullOrWhiteSpace(value);
            }
            catch (HttpRequestException)
            {
            }
            catch (QbittorrentApiException exception) when (exception.StatusCode is 401 or 403)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task StopCoreAsync(bool force, CancellationToken cancellationToken)
    {
        _stopping = true;
        var process = _process;
        if (process is null)
            return;

        if (!force && !process.HasExited && _api is not null)
        {
            try
            {
                await _api.Application.ShutdownAsync(cancellationToken).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or QbittorrentApiException)
            {
                _logger.LogWarning(exception, "Graceful qBittorrent shutdown failed; terminating the managed process.");
            }
        }

        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (_api is not null)
            await _api.DisposeAsync().ConfigureAwait(false);
        _api = null;

        process.OutputDataReceived -= OnOutputDataReceived;
        process.ErrorDataReceived -= OnOutputDataReceived;
        process.Exited -= OnProcessExited;
        process.Dispose();
        _process = null;
        _job?.Dispose();
        _job = null;
        Session = null;
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data))
            return;

        var line = args.Data.Trim();
        lock (_recentOutput)
        {
            _recentOutput.Add(Redact(line));
            if (_recentOutput.Count > 100)
                _recentOutput.RemoveAt(0);
        }

        var match = TemporaryPasswordRegex().Match(line);
        if (match.Success)
            _temporaryPassword?.TrySetResult(match.Groups[2].Value.Trim());

        OutputReceived?.Invoke(this, Redact(line));
    }

    private void OnProcessExited(object? sender, EventArgs args)
    {
        if (_stopping)
            return;

        var exception = new InvalidOperationException(
            $"The managed qBittorrent backend exited unexpectedly with code {_process?.ExitCode}.\n{string.Join(Environment.NewLine, _recentOutput.TakeLast(10))}");
        _logger.LogError(exception, "Managed qBittorrent exited unexpectedly.");
        Failed?.Invoke(this, exception);

        if (Interlocked.Increment(ref _restartAttempts) <= 3)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await StopCoreAsync(force: true, CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        _gate.Release();
                    }

                    await Task.Delay(TimeSpan.FromSeconds(_restartAttempts)).ConfigureAwait(false);
                    await StartAsync().ConfigureAwait(false);
                }
                catch (Exception restartException)
                {
                    Failed?.Invoke(this, restartException);
                }
            });
        }
    }

    private void ThrowIfExited()
    {
        if (_process?.HasExited != true)
            return;
        throw new InvalidOperationException(
            $"qBittorrent exited during startup with code {_process.ExitCode}.\n{string.Join(Environment.NewLine, _recentOutput.TakeLast(20))}");
    }

    private static string Redact(string line)
        => line.Contains("temporary password", StringComparison.OrdinalIgnoreCase)
            ? TemporaryPasswordRegex().Replace(line, "$1<redacted>")
            : line;

    private static string FindBackendExecutable()
    {
        var overridden = Environment.GetEnvironmentVariable("WINBITTORRENT_BACKEND_PATH");
        var candidates = new[]
        {
            overridden,
            Path.Combine(AppContext.BaseDirectory, "Backend", "qbittorrent-nox.exe"),
            Path.Combine(AppContext.BaseDirectory, "qbittorrent-nox.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Backend", "qbittorrent-nox.exe"))
        };

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new FileNotFoundException(
                "The native Windows qbittorrent-nox.exe is not packaged. Run build\build-backend.ps1 before launching WinBitTorrent.",
                candidates[0]);
    }

    private static void ConfigurePython(ProcessStartInfo startInfo, string backendDirectory)
    {
        var pythonDirectory = Path.Combine(backendDirectory, "Python");
        if (!Directory.Exists(pythonDirectory))
            return;

        startInfo.Environment["PYTHONHOME"] = pythonDirectory;
        startInfo.Environment["PYTHONPATH"] = $"{Path.Combine(backendDirectory, "SearchPlugins")};{pythonDirectory}";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        startInfo.Environment.TryGetValue("PATH", out var currentPath);
        startInfo.Environment["PATH"] = $"{pythonDirectory};{backendDirectory};{currentPath}";
    }

    private static void SeedBundledSearchPlugins(string backendDirectory)
    {
        var bundledRoot = Path.Combine(backendDirectory, "SearchPlugins");
        var bundledEngines = Path.Combine(bundledRoot, "engines");
        if (!Directory.Exists(bundledRoot) && !Directory.Exists(bundledEngines))
            return;

        var novaData = Path.Combine(AppPaths.BackendData, "nova3");
        var engineData = Path.Combine(novaData, "engines");
        Directory.CreateDirectory(novaData);
        Directory.CreateDirectory(engineData);

        var bundledRuntime = Path.Combine(bundledRoot, "nova3");
        if (Directory.Exists(bundledRuntime))
        {
            foreach (var source in Directory.EnumerateFiles(bundledRuntime, "*.py", SearchOption.TopDirectoryOnly))
            {
                File.Copy(source, Path.Combine(novaData, Path.GetFileName(source)), overwrite: true);
            }
        }

        if (!Directory.Exists(bundledEngines))
            return;

        foreach (var source in Directory.EnumerateFiles(bundledEngines, "*.py", SearchOption.TopDirectoryOnly))
        {
            var target = Path.Combine(engineData, Path.GetFileName(source));
            if (!File.Exists(target))
                File.Copy(source, target);
        }
    }

    private static async Task<int> GetOrCreatePortAsync(CancellationToken cancellationToken)
    {
        AppPaths.EnsureCreated();
        if (File.Exists(AppPaths.BackendState))
        {
            try
            {
                await using var existing = File.OpenRead(AppPaths.BackendState);
                var state = await JsonSerializer.DeserializeAsync<HostState>(existing, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (state is { Port: > 0 } && IsPortAvailable(state.Port))
                    return state.Port;
            }
            catch (JsonException)
            {
            }
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await using var created = File.Create(AppPaths.BackendState);
        await JsonSerializer.SerializeAsync(created, new HostState(port), cancellationToken: cancellationToken).ConfigureAwait(false);
        return port;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void EnsureSecureConfiguration(int port)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.BackendConfig)!);
        var editor = IniDocument.Load(AppPaths.BackendConfig);
        editor.Set("LegalNotice", "Accepted", "true");
        editor.Set("Preferences", "WebUI\\Enabled", "true");
        editor.Set("Preferences", "WebUI\\Address", "127.0.0.1");
        editor.Set("Preferences", "WebUI\\Port", port.ToString());
        editor.Set("Preferences", "WebUI\\HostHeaderValidation", "true");
        editor.Set("Preferences", "WebUI\\ServerDomains", "localhost;127.0.0.1");
        editor.Save(AppPaths.BackendConfig);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(force: false).ConfigureAwait(false);
        _gate.Dispose();
    }

    [GeneratedRegex(@"(?i)((?:temporary password(?: is provided for this session)?|временн\S*\s+парол\S*)\s*:\s*)(\S+)\s*$")]
    private static partial Regex TemporaryPasswordRegex();

    private sealed record HostState(int Port);

    private sealed class IniDocument
    {
        private readonly List<string> _lines;

        private IniDocument(List<string> lines) => _lines = lines;

        public static IniDocument Load(string path)
            => new(File.Exists(path) ? File.ReadAllLines(path).ToList() : []);

        public void Set(string section, string key, string value)
        {
            var sectionLine = $"[{section}]";
            var sectionIndex = _lines.FindIndex(line => line.Trim().Equals(sectionLine, StringComparison.OrdinalIgnoreCase));
            if (sectionIndex < 0)
            {
                if (_lines.Count > 0 && !string.IsNullOrWhiteSpace(_lines[^1]))
                    _lines.Add(string.Empty);
                sectionIndex = _lines.Count;
                _lines.Add(sectionLine);
            }

            var nextSection = _lines.FindIndex(sectionIndex + 1, line => line.TrimStart().StartsWith('['));
            if (nextSection < 0)
                nextSection = _lines.Count;

            for (var index = sectionIndex + 1; index < nextSection; index++)
            {
                var separator = _lines[index].IndexOf('=');
                if (separator <= 0 || !_lines[index][..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;
                _lines[index] = $"{key}={value}";
                return;
            }

            _lines.Insert(nextSection, $"{key}={value}");
        }

        public void Save(string path) => File.WriteAllLines(path, _lines);
    }
}
