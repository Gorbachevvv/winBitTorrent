using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Infrastructure.Backend;
using WinBitTorrent.Infrastructure.Storage;

namespace WinBitTorrent.Infrastructure.Engine;

public sealed class EngineHostProcess : IManagedBackendHost
{
    private readonly ILogger<EngineHostProcess> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private NativeJob? _job;
    private EnginePipeClient? _pipe;
    private bool _stopping;

    public EngineHostProcess(ILogger<EngineHostProcess> logger) => _logger = logger;

    public BackendSession? Session { get; private set; }
    public ITorrentBackendClient? Client { get; private set; }
    public bool IsRunning => _process is { HasExited: false };
    public event EventHandler<string>? OutputReceived;
    public event EventHandler<Exception>? Failed;

    public async Task<BackendSession> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning && Session is not null && Client is not null)
                return Session;

            await StopCoreAsync(force: true, CancellationToken.None).ConfigureAwait(false);
            AppPaths.EnsureCreated();
            _stopping = false;

            var executable = FindExecutable();
            var pipeName = $"WinBitTorrent.Engine.{Environment.ProcessId}.{Guid.NewGuid():N}";
            var authenticationToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add($"--pipe={pipeName}");
            startInfo.ArgumentList.Add($"--data-root={AppPaths.EngineRoot}");

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnOutput;
            _process.ErrorDataReceived += OnOutput;
            _process.Exited += OnExited;
            if (!_process.Start())
                throw new InvalidOperationException("Unable to start WinBitTorrent.EngineHost.");

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            await _process.StandardInput.WriteLineAsync(authenticationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            _process.StandardInput.Close();

            _job = new NativeJob();
            _job.Assign(_process.Handle);

            var connection = await EnginePipeClient.ConnectAsync(pipeName, authenticationToken, cancellationToken).ConfigureAwait(false);
            _pipe = connection.Client;
            var profile = ServerProfile.CreateLocal(new Uri("wbt://local/"));
            Client = new LocalLibtorrentBackendClient(profile, _pipe);
            Session = new BackendSession(
                _process.Id,
                profile.BaseAddress,
                $"WinBitTorrent Engine {connection.Hello.EngineVersion} / libtorrent {connection.Hello.LibtorrentVersion}",
                $"EngineRPC/{Core.EngineProtocol.EngineRpcProtocol.Version}",
                connection.Hello.StartedAt);
            _logger.LogInformation("WinBitTorrent engine started as process {ProcessId}.", _process.Id);
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

    private async Task StopCoreAsync(bool force, CancellationToken cancellationToken)
    {
        _stopping = true;
        if (!force && Client is not null && _process is { HasExited: false })
        {
            try
            {
                await Client.Application.ShutdownAsync(cancellationToken).ConfigureAwait(false);
                await _process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or TorrentBackendException or OperationCanceledException)
            {
                _logger.LogWarning(exception, "Graceful engine shutdown failed; terminating the worker.");
            }
        }

        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            try { await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }

        if (_pipe is not null)
            await _pipe.DisposeAsync().ConfigureAwait(false);
        _pipe = null;
        Client = null;
        Session = null;
        _job?.Dispose();
        _job = null;
        if (_process is not null)
        {
            _process.OutputDataReceived -= OnOutput;
            _process.ErrorDataReceived -= OnOutput;
            _process.Exited -= OnExited;
            _process.Dispose();
            _process = null;
        }
    }

    private static string FindExecutable()
    {
        var overridden = Environment.GetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH");
        var candidates = new[]
        {
            overridden,
            Path.Combine(AppContext.BaseDirectory, "EngineHost", "WinBitTorrent.EngineHost.exe"),
            Path.Combine(AppContext.BaseDirectory, "WinBitTorrent.EngineHost.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "WinBitTorrent.EngineHost", "bin", "Debug", "net8.0-windows10.0.19041.0", "WinBitTorrent.EngineHost.exe"))
        };
        return candidates.FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new FileNotFoundException("WinBitTorrent.EngineHost.exe was not found. Build the EngineHost project first.");
    }

    private void OnOutput(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data))
            return;
        OutputReceived?.Invoke(this, args.Data);
        _logger.LogInformation("EngineHost: {Line}", args.Data);
    }

    private void OnExited(object? sender, EventArgs args)
    {
        if (_stopping)
            return;
        var exception = new InvalidOperationException($"WinBitTorrent.EngineHost exited unexpectedly with code {_process?.ExitCode}.");
        _logger.LogError(exception, "The local torrent engine exited unexpectedly.");
        Failed?.Invoke(this, exception);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(force: false).ConfigureAwait(false);
        _gate.Dispose();
    }
}
