using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Infrastructure.Engine;

namespace WinBitTorrent.IntegrationTests;

[Collection("Backend")]
public sealed class EngineHostTests
{
    [Fact]
    public async Task RefreshesOneThousandTorrentsWithinOneSecondWithoutBackpressureFailure()
    {
        var engineHost = FindEngineHost();
        var dataRoot = Path.Combine(Path.GetTempPath(), "WinBitTorrent.EngineHost.Scale", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", engineHost);
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", dataRoot);
        await using var host = new EngineHostProcess(NullLogger<EngineHostProcess>.Instance);
        try
        {
            await host.StartAsync();
            var fixtureRoot = Path.Combine(dataRoot, "fixtures");
            var downloads = Path.Combine(dataRoot, "downloads");
            Directory.CreateDirectory(fixtureRoot);
            Directory.CreateDirectory(downloads);
            var paths = new string[1000];
            for (var index = 0; index < paths.Length; index++)
            {
                paths[index] = Path.Combine(fixtureRoot, $"torrent-{index:D4}.torrent");
                await File.WriteAllBytesAsync(paths[index], CreateSingleFileTorrent($"payload-{index:D4}.bin", [(byte)(index % 251)]));
            }

            await host.Client!.Torrents.AddAsync(new TorrentAddRequest([], paths, SavePath: downloads, StartTorrent: false));
            Assert.Equal(1000, (await host.Client.Torrents.GetInfoAsync()).Count);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var stopwatch = Stopwatch.StartNew();
                var snapshot = await host.Client.Sync.GetMainDataAsync(0);
                stopwatch.Stop();
                Assert.Equal(1000, snapshot.Torrents?.Count);
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"1,000-torrent snapshot took {stopwatch.Elapsed}.");
            }
            await host.Client.Torrents.DeleteAsync("all", deleteFiles: false);
            await host.StopAsync();
        }
        finally
        {
            await host.StopAsync(force: true);
            Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", null);
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", null);
            DeleteWritableTree(dataRoot);
        }
    }

    [Fact]
    public async Task ReportsUnexpectedWorkerExitAndCanRestartCleanly()
    {
        var engineHost = FindEngineHost();
        var dataRoot = Path.Combine(Path.GetTempPath(), "WinBitTorrent.EngineHost.Crash", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", engineHost);
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", dataRoot);
        await using var host = new EngineHostProcess(NullLogger<EngineHostProcess>.Instance);
        try
        {
            var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Failed += (_, exception) => failure.TrySetResult(exception);
            var first = await host.StartAsync();
            using (var worker = Process.GetProcessById(first.ProcessId))
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
            }
            var reported = await failure.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("exited unexpectedly", reported.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(host.IsRunning);

            var restarted = await host.StartAsync();
            Assert.NotEqual(first.ProcessId, restarted.ProcessId);
            Assert.True(host.IsRunning);
            await host.StopAsync();
        }
        finally
        {
            await host.StopAsync(force: true);
            Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", null);
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", null);
            DeleteWritableTree(dataRoot);
        }
    }

    [Fact]
    public async Task RejectsWrongPipeSecretAndDoesNotExposeItInArguments()
    {
        var engineHost = FindEngineHost();
        var dataRoot = Path.Combine(Path.GetTempPath(), "WinBitTorrent.EngineHost.Pipe", Guid.NewGuid().ToString("N"));
        var pipeName = $"WinBitTorrent.Engine.Test.{Guid.NewGuid():N}";
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var startInfo = new ProcessStartInfo
        {
            FileName = engineHost,
            WorkingDirectory = Path.GetDirectoryName(engineHost)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add($"--pipe={pipeName}");
        startInfo.ArgumentList.Add($"--data-root={Path.Combine(dataRoot, "Engine")}");
        using var process = new Process { StartInfo = startInfo };
        try
        {
            Assert.True(process.Start());
            await process.StandardInput.WriteLineAsync(secret);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            Assert.DoesNotContain(secret, string.Join(' ', startInfo.ArgumentList), StringComparison.Ordinal);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var rejected = await Assert.ThrowsAsync<LocalEngineException>(async () =>
                await EnginePipeClient.ConnectAsync(pipeName, "invalid-secret", timeout.Token));
            Assert.Equal("authentication_failed", rejected.Code);

            var authenticated = await EnginePipeClient.ConnectAsync(pipeName, secret, timeout.Token);
            await using var client = authenticated.Client;
            Assert.Equal("2.0.13.0", authenticated.Hello.LibtorrentVersion);
            await client.InvokeAsync(WinBitTorrent.Core.EngineProtocol.EngineRpcMethods.Shutdown, cancellationToken: timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token) + await process.StandardError.ReadToEndAsync(timeout.Token);
            Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            DeleteWritableTree(dataRoot);
        }
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("Legacy")]
    public async Task DownloadsFromHermeticWebSeedAndResumesAfterWorkerRestart(string resumeStorage)
    {
        var engineHost = FindEngineHost();
        var dataRoot = Path.Combine(Path.GetTempPath(), "WinBitTorrent.EngineHost.Swarm", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", engineHost);
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", dataRoot);
        var content = new byte[4 * 1024 * 1024];
        new Random(42).NextBytes(content);
        await using var webSeed = await RangeHttpFileServer.StartAsync(content, TimeSpan.FromMilliseconds(10));
        await using var host = new EngineHostProcess(NullLogger<EngineHostProcess>.Instance);
        try
        {
            await host.StartAsync();
            await host.Client!.Application.SetPreferencesAsync(new JsonObject
            {
                ["resume_data_storage_type"] = resumeStorage
            });
            var torrentPath = Path.Combine(dataRoot, "web-seed.torrent");
            var downloads = Path.Combine(dataRoot, "downloads");
            var incomplete = Path.Combine(dataRoot, "incomplete");
            Directory.CreateDirectory(downloads);
            Directory.CreateDirectory(incomplete);
            await File.WriteAllBytesAsync(torrentPath, CreateSingleFileTorrent("payload.bin", content));
            await host.Client.Torrents.AddAsync(new TorrentAddRequest(
                [], [torrentPath], SavePath: downloads, DownloadPath: incomplete,
                UseDownloadPath: true, StartTorrent: false));
            var torrent = Assert.Single(await host.Client.Torrents.GetInfoAsync());
            await host.Client.Torrents.AddWebSeedsAsync(torrent.Hash, [webSeed.Url]);
            await host.Client.Torrents.ExecuteAsync(TorrentCommand.Start, torrent.Hash);

            await WaitUntilAsync(async () => (await host.Client.Torrents.GetInfoAsync()).Single().Downloaded > 128 * 1024, TimeSpan.FromSeconds(20));
            await host.Client.Torrents.ExecuteAsync(TorrentCommand.Stop, torrent.Hash);
            var beforeRestart = (await host.Client.Torrents.GetInfoAsync()).Single().Downloaded;
            Assert.InRange(beforeRestart, 1, content.Length - 1);
            await host.StopAsync(force: true);

            await host.StartAsync();
            torrent = Assert.Single(await host.Client!.Torrents.GetInfoAsync());
            Assert.True(torrent.Downloaded >= beforeRestart);
            await host.Client.Torrents.ExecuteAsync(TorrentCommand.Start, torrent.Hash);
            await WaitUntilAsync(() => Task.FromResult(File.Exists(Path.Combine(downloads, "payload.bin"))), TimeSpan.FromSeconds(30));
            await Task.Delay(750); // let EngineHost persist storage_moved without any UI poll
            await host.StopAsync(force: true);

            await host.StartAsync();
            var completedAfterRestart = Assert.Single(await host.Client!.Torrents.GetInfoAsync());
            Assert.Equal(1d, completedAfterRestart.Progress, precision: 6);
            Assert.Equal(content.Length, completedAfterRestart.Completed);
            Assert.Equal(downloads, (await host.Client.Torrents.GetPropertiesAsync(completedAfterRestart.Hash)).SavePath);

            var relocated = Path.Combine(dataRoot, "Новое расположение", "relocated files");
            await host.Client.Torrents.SetLocationAsync(completedAfterRestart.Hash, relocated);
            await WaitUntilAsync(() => Task.FromResult(File.Exists(Path.Combine(relocated, "payload.bin"))), TimeSpan.FromSeconds(20));
            await Task.Delay(750);
            await host.StopAsync(force: true);

            await host.StartAsync();
            var relocatedAfterRestart = Assert.Single(await host.Client!.Torrents.GetInfoAsync());
            Assert.Equal(1d, relocatedAfterRestart.Progress, precision: 6);
            Assert.Equal(relocated, relocatedAfterRestart.SavePath);
            Assert.False(File.Exists(Path.Combine(downloads, "payload.bin")));
            Assert.Equal(relocated, (await host.Client.Torrents.GetPropertiesAsync(relocatedAfterRestart.Hash)).SavePath);
            await host.Client.Torrents.ExecuteAsync(TorrentCommand.Recheck, relocatedAfterRestart.Hash);
            await WaitUntilAsync(async () =>
                (await host.Client.Torrents.GetInfoAsync()).Single().Progress >= 1,
                TimeSpan.FromSeconds(20));
            await host.StopAsync();

            var downloaded = await File.ReadAllBytesAsync(Path.Combine(relocated, "payload.bin"));
            Assert.Equal(SHA256.HashData(content), SHA256.HashData(downloaded));
            Assert.True(webSeed.BytesServed < content.Length * 2L, $"Resume downloaded too much data: {webSeed.BytesServed} bytes.");
        }
        finally
        {
            await host.StopAsync(force: true);
            Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", null);
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", null);
            DeleteWritableTree(dataRoot);
        }
    }

    [Fact]
    public async Task FailedLocationChangeKeepsConfirmedPathAndCanBeRetried()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "WinBitTorrent.EngineHost.Move", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", FindEngineHost());
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", dataRoot);
        await using var host = new EngineHostProcess(NullLogger<EngineHostProcess>.Instance);
        try
        {
            var original = Path.Combine(dataRoot, "original");
            var destination = Path.Combine(dataRoot, "destination");
            Directory.CreateDirectory(original);
            Directory.CreateDirectory(destination);
            var content = new byte[512 * 1024];
            new Random(82).NextBytes(content);
            await File.WriteAllBytesAsync(Path.Combine(original, "payload.bin"), content);
            var torrentPath = Path.Combine(dataRoot, "test.torrent");
            await File.WriteAllBytesAsync(torrentPath, CreateSingleFileTorrent("payload.bin", content));
            await host.StartAsync();
            var api = host.Client!;
            await api.Torrents.AddAsync(new TorrentAddRequest([], [torrentPath], SavePath: original, StartTorrent: false));
            var torrent = Assert.Single(await api.Torrents.GetInfoAsync());
            await api.Torrents.ExecuteAsync(TorrentCommand.Recheck, torrent.Hash);
            await WaitUntilAsync(async () => (await api.Torrents.GetInfoAsync()).Single().Progress == 1, TimeSpan.FromSeconds(20));
            await api.Torrents.ExecuteAsync(TorrentCommand.Stop, torrent.Hash);

            await Assert.ThrowsAnyAsync<Exception>(() => api.Torrents.SetLocationAsync(torrent.Hash, "relative-path"));
            await Assert.ThrowsAnyAsync<Exception>(() => api.Torrents.SetLocationAsync(torrent.Hash, Path.Combine(original, "payload.bin")));
            Assert.Equal(original, (await api.Torrents.GetInfoAsync()).Single().SavePath);

            // Force a real asynchronous disk failure, after the command was accepted.
            using (var lockedTarget = new FileStream(Path.Combine(destination, "payload.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                await api.Torrents.SetLocationAsync(torrent.Hash, destination);
                await WaitUntilAsync(async () => (await api.Logs.GetMainAsync()).Any(entry =>
                    entry?["message"]?.GetValue<string>().Contains("storage", StringComparison.OrdinalIgnoreCase) == true
                    && entry?["type"]?.GetValue<int>() == 8), TimeSpan.FromSeconds(20));
                Assert.Equal(original, (await api.Torrents.GetInfoAsync()).Single().SavePath);
                Assert.Equal(original, (await api.Torrents.GetPropertiesAsync(torrent.Hash)).SavePath);
                Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(original, "payload.bin")));
            }

            await host.StopAsync(force: true);
            await host.StartAsync();
            api = host.Client!;
            Assert.Equal(original, (await api.Torrents.GetInfoAsync()).Single().SavePath);
            await api.Torrents.SetLocationAsync(torrent.Hash, destination);
            await WaitUntilAsync(async () => (await api.Torrents.GetInfoAsync()).Single().SavePath == destination, TimeSpan.FromSeconds(20));
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(destination, "payload.bin")));
            Assert.False(File.Exists(Path.Combine(original, "payload.bin")));
            await Task.Delay(750);
            await host.StopAsync(force: true);
            await host.StartAsync();
            var restored = Assert.Single(await host.Client!.Torrents.GetInfoAsync());
            Assert.Equal(destination, restored.SavePath);
            Assert.Equal(1d, restored.Progress, precision: 6);
            Assert.Equal("stoppedUP", restored.State);
        }
        finally
        {
            await host.StopAsync(force: true);
            Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", null);
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", null);
            DeleteWritableTree(dataRoot);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelocatesIncompleteTorrentWithoutLosingDownloadedPieces(bool stopBeforeMove)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "WinBitTorrent.EngineHost.PartialMove", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", FindEngineHost());
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", dataRoot);
        var content = new byte[4 * 1024 * 1024];
        new Random(83).NextBytes(content);
        await using var webSeed = await RangeHttpFileServer.StartAsync(content, TimeSpan.FromMilliseconds(10));
        await using var host = new EngineHostProcess(NullLogger<EngineHostProcess>.Instance);
        try
        {
            await host.StartAsync();
            var api = host.Client!;
            var original = Path.Combine(dataRoot, "incomplete");
            var destination = Path.Combine(dataRoot, "new location");
            var torrentPath = Path.Combine(dataRoot, "test.torrent");
            await File.WriteAllBytesAsync(torrentPath, CreateSingleFileTorrent("payload.bin", content));
            await api.Torrents.AddAsync(new TorrentAddRequest([], [torrentPath], SavePath: Path.Combine(dataRoot, "old complete"),
                DownloadPath: original, UseDownloadPath: true, StartTorrent: false));
            var torrent = Assert.Single(await api.Torrents.GetInfoAsync());
            await api.Torrents.AddWebSeedsAsync(torrent.Hash, [webSeed.Url]);
            await api.Torrents.ExecuteAsync(TorrentCommand.Start, torrent.Hash);
            await WaitUntilAsync(async () => (await api.Torrents.GetInfoAsync()).Single().Downloaded > 128 * 1024, TimeSpan.FromSeconds(20));
            if (stopBeforeMove)
                await api.Torrents.ExecuteAsync(TorrentCommand.Stop, torrent.Hash);
            var beforeMove = (await api.Torrents.GetInfoAsync()).Single();
            Assert.InRange(beforeMove.Progress, 0.001, 0.999);
            await api.Torrents.SetLocationAsync(torrent.Hash, destination);
            await WaitUntilAsync(async () => (await api.Torrents.GetInfoAsync()).Single().SavePath == destination, TimeSpan.FromSeconds(20));
            var afterMove = (await api.Torrents.GetInfoAsync()).Single();
            Assert.True(afterMove.Completed >= beforeMove.Completed);
            if (stopBeforeMove)
                Assert.Equal("stoppedDL", afterMove.State);
            await api.Torrents.ExecuteAsync(TorrentCommand.Start, torrent.Hash);
            await WaitUntilAsync(async () => (await api.Torrents.GetInfoAsync()).Single().Progress == 1, TimeSpan.FromSeconds(30));
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(destination, "payload.bin")));
            Assert.False(File.Exists(Path.Combine(original, "payload.bin")));
            Assert.False(File.Exists(Path.Combine(dataRoot, "old complete", "payload.bin")));
            Assert.True(webSeed.BytesServed < content.Length * 2L);
        }
        finally
        {
            await host.StopAsync(force: true);
            Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", null);
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", null);
            DeleteWritableTree(dataRoot);
        }
    }

    [Fact]
    public async Task RechecksFilesChangedAfterTheLastResumeSnapshot()
    {
        var engineHost = FindEngineHost();
        var dataRoot = Path.Combine(Path.GetTempPath(), "WinBitTorrent.EngineHost.ExternalData", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", engineHost);
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", dataRoot);
        var content = new byte[512 * 1024];
        new Random(73).NextBytes(content);
        await using var host = new EngineHostProcess(NullLogger<EngineHostProcess>.Instance);
        try
        {
            await host.StartAsync();
            await host.Client!.Application.SetPreferencesAsync(new JsonObject
            {
                ["resume_data_storage_type"] = "Legacy"
            });
            var torrentPath = Path.Combine(dataRoot, "external-data.torrent");
            var downloads = Path.Combine(dataRoot, "downloads");
            Directory.CreateDirectory(downloads);
            await File.WriteAllBytesAsync(torrentPath, CreateSingleFileTorrent("external-data.bin", content));
            await host.Client.Torrents.AddAsync(new TorrentAddRequest([], [torrentPath], SavePath: downloads, StartTorrent: false));
            var torrent = Assert.Single(await host.Client.Torrents.GetInfoAsync());
            Assert.Equal(0, torrent.Progress);
            await host.StopAsync();

            var downloadedPath = Path.Combine(downloads, "external-data.bin");
            await File.WriteAllBytesAsync(downloadedPath, content);
            File.SetLastWriteTimeUtc(downloadedPath, DateTime.UtcNow.AddSeconds(2));

            await host.StartAsync();
            await WaitUntilAsync(async () =>
                (await host.Client!.Torrents.GetInfoAsync()).Single().Progress >= 1,
                TimeSpan.FromSeconds(20));
            var recovered = Assert.Single(await host.Client!.Torrents.GetInfoAsync());
            Assert.Equal(content.Length, recovered.Completed);

            var corrupted = (byte[])content.Clone();
            corrupted[0] ^= 0xff;
            await File.WriteAllBytesAsync(downloadedPath, corrupted);
            await host.Client.Torrents.ExecuteAsync(TorrentCommand.Recheck, recovered.Hash);
            await WaitUntilAsync(async () =>
                (await host.Client.Torrents.GetInfoAsync()).Single().Progress < 1,
                TimeSpan.FromSeconds(20));
            var failedCheck = Assert.Single(await host.Client.Torrents.GetInfoAsync());
            Assert.Equal("stoppedDL", failedCheck.State);

            await File.WriteAllBytesAsync(downloadedPath, content);
            await host.Client.Torrents.ExecuteAsync(TorrentCommand.Recheck, recovered.Hash);
            await WaitUntilAsync(async () =>
                (await host.Client.Torrents.GetInfoAsync()).Single().Progress >= 1,
                TimeSpan.FromSeconds(20));
            var successfulCheck = Assert.Single(await host.Client.Torrents.GetInfoAsync());
            Assert.Equal("stoppedUP", successfulCheck.State);
            await host.StopAsync();
        }
        finally
        {
            await host.StopAsync(force: true);
            Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", null);
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", null);
            DeleteWritableTree(dataRoot);
        }
    }

    [Fact]
    public async Task PersistsMagnetWithoutMetadataAcrossWorkerCrash()
    {
        var engineHost = FindEngineHost();
        var dataRoot = Path.Combine(Path.GetTempPath(), "WinBitTorrent.EngineHost.Magnet", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", engineHost);
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", dataRoot);
        await using var host = new EngineHostProcess(NullLogger<EngineHostProcess>.Instance);
        const string hash = "0123456789abcdef0123456789abcdef01234567";
        try
        {
            await host.StartAsync();
            await host.Client!.Torrents.AddAsync(new TorrentAddRequest(
                [$"magnet:?xt=urn:btih:{hash}&dn=metadata-pending"], [],
                SavePath: Path.Combine(dataRoot, "downloads"), StartTorrent: false));
            Assert.Equal(hash, Assert.Single(await host.Client.Torrents.GetInfoAsync()).Hash, ignoreCase: true);
            await host.StopAsync(force: true);

            await host.StartAsync();
            var restored = Assert.Single(await host.Client!.Torrents.GetInfoAsync());
            Assert.Equal(hash, restored.Hash, ignoreCase: true);
            Assert.Equal("stoppedDL", restored.State);
            await host.StopAsync();
        }
        finally
        {
            await host.StopAsync(force: true);
            Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", null);
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", null);
            DeleteWritableTree(dataRoot);
        }
    }

    [Fact]
    public async Task CorruptedResumeRestoresTorrentAtItsOriginalPath()
    {
        var engineHost = FindEngineHost();
        var dataRoot = Path.Combine(Path.GetTempPath(), "WinBitTorrent.EngineHost.CorruptResume", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", engineHost);
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", dataRoot);
        await using var host = new EngineHostProcess(NullLogger<EngineHostProcess>.Instance);
        try
        {
            await host.StartAsync();
            await host.Client!.Application.SetPreferencesAsync(new JsonObject
            {
                ["resume_data_storage_type"] = "Legacy"
            });
            var downloads = Path.Combine(dataRoot, "downloads");
            var torrentPath = Path.Combine(dataRoot, "corrupt-resume.torrent");
            Directory.CreateDirectory(downloads);
            await File.WriteAllBytesAsync(torrentPath, CreateSingleFileTorrent("corrupt-resume.bin", new byte[64 * 1024]));
            await host.Client.Torrents.AddAsync(new TorrentAddRequest([], [torrentPath], SavePath: downloads, StartTorrent: false));
            var torrent = Assert.Single(await host.Client.Torrents.GetInfoAsync());
            await host.StopAsync();

            await File.WriteAllTextAsync(Path.Combine(dataRoot, "Engine", "resume", torrent.Hash + ".fastresume"), "damaged resume data");
            await host.StartAsync();
            var restored = Assert.Single(await host.Client!.Torrents.GetInfoAsync());
            Assert.Equal("stoppedDL", restored.State);
            Assert.Equal(Path.GetFullPath(downloads), Path.GetFullPath((await host.Client.Torrents.GetPropertiesAsync(restored.Hash)).SavePath));
            Assert.False(File.Exists(Path.Combine(dataRoot, "Engine", "corrupt-resume.bin")));
            await host.StopAsync();
        }
        finally
        {
            await host.StopAsync(force: true);
            Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", null);
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", null);
            DeleteWritableTree(dataRoot);
        }
    }

    [Fact]
    public async Task LocalEngineExercisesTheTypedTransferContract()
    {
        var engineHost = FindEngineHost();
        var dataRoot = Path.Combine(Path.GetTempPath(), "WinBitTorrent.EngineHost.Contract", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", engineHost);
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", dataRoot);
        await using var host = new EngineHostProcess(NullLogger<EngineHostProcess>.Instance);
        try
        {
            await host.StartAsync();
            var api = host.Client!;
            Assert.Contains("WinBitTorrent Engine", await api.Application.GetVersionAsync());
            Assert.NotNull((await api.Application.GetBuildInfoAsync())["libtorrent"]);
            Assert.True((await api.Application.GetProcessInfoAsync())["pid"]!.GetValue<int>() > 0);
            Assert.NotEmpty(await api.Application.GetDirectoryContentAsync(dataRoot));
            await api.Application.SetCookiesAsync(new JsonArray(new JsonObject
            {
                ["domain"] = "example.test", ["path"] = "/", ["name"] = "session", ["value"] = "opaque",
                ["expirationDate"] = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(), ["secure"] = true, ["httpOnly"] = true
            }));
            Assert.Equal("opaque", Assert.IsType<JsonObject>(Assert.Single(await api.Application.GetCookiesAsync()))["value"]!.GetValue<string>());

            await api.Application.SetPreferencesAsync(new JsonObject
            {
                ["queueing_enabled"] = true,
                ["max_connec_per_torrent"] = 25,
                ["max_uploads_per_torrent"] = 4,
                ["preallocate_all"] = true,
                ["auto_tmm_enabled"] = false
            });
            await api.Transfer.SetDownloadLimitAsync(4096);
            await api.Transfer.SetUploadLimitAsync(2048);
            Assert.Equal(4096, await api.Transfer.GetDownloadLimitAsync());
            Assert.Equal(2048, await api.Transfer.GetUploadLimitAsync());
            await api.Transfer.SetAlternativeSpeedLimitsAsync(true);
            Assert.True(await api.Transfer.GetAlternativeSpeedLimitsAsync());
            Assert.Equal("connected", (await api.Transfer.GetInfoAsync())["connection_status"]!.GetValue<string>());

            var source = Path.Combine(dataRoot, "sources");
            var downloads = Path.Combine(dataRoot, "downloads");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(downloads);
            var firstPath = Path.Combine(source, "first.torrent");
            var secondPath = Path.Combine(source, "second.torrent");
            await File.WriteAllBytesAsync(firstPath, CreateSingleFileTorrent("first.bin", "first contract payload"u8.ToArray()));
            await File.WriteAllBytesAsync(secondPath, CreateSingleFileTorrent("second.bin", "second contract payload"u8.ToArray()));
            await api.Torrents.AddAsync(new TorrentAddRequest([], [firstPath], SavePath: downloads, StartTorrent: false, AutomaticTorrentManagement: true));
            await api.Torrents.AddAsync(new TorrentAddRequest([], [secondPath], SavePath: downloads, StartTorrent: false));
            var torrents = await api.Torrents.GetInfoAsync();
            Assert.Equal(2, torrents.Count);
            var first = torrents.Single(torrent => torrent.Name == "first.bin");
            var second = torrents.Single(torrent => torrent.Name == "second.bin");

            var sync = await api.Sync.GetMainDataAsync(0);
            Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyDictionary<string, TorrentInfo>>(sync.Torrents).Count);
            Assert.Empty((await api.Sync.GetTorrentPeersAsync(first.Hash, 0))["peers"]!.AsObject());
            Assert.Equal(1, (await api.Torrents.GetPropertiesAsync(first.Hash)).Pieces);
            Assert.NotEmpty(await api.Torrents.GetTrackersAsync(first.Hash));
            Assert.Single(await api.Torrents.GetFilesAsync(first.Hash));
            Assert.Single(await api.Torrents.GetPieceStatesAsync(first.Hash));
            Assert.Single(await api.Torrents.GetPieceAvailabilityAsync(first.Hash));

            var managedPath = Path.Combine(dataRoot, "managed");
            await api.Torrents.CreateCategoryAsync("Managed", managedPath);
            await api.Torrents.SetCategoryAsync(first.Hash, "Managed");
            await api.Torrents.CreateTagsAsync(["contract", "temporary"]);
            await api.Torrents.AddTagsAsync(first.Hash, "contract, temporary");
            await api.Torrents.RemoveTagsAsync(first.Hash, "temporary");
            await api.Torrents.SetDownloadLimitAsync(first.Hash, 12345);
            await api.Torrents.SetUploadLimitAsync(first.Hash, 54321);
            await api.Torrents.SetShareLimitsAsync(first.Hash, 0.75, 60, 30);
            await api.Torrents.RenameAsync(first.Hash, "Renamed contract torrent");
            await api.Torrents.SetFilePriorityAsync(first.Hash, [0], 7);
            await api.Torrents.AddTrackersAsync(first.Hash, ["https://tracker.example/announce"]);
            await api.Torrents.AddWebSeedsAsync(first.Hash, ["https://seed.example/file"]);
            await api.Torrents.SetForceStartAsync(first.Hash, true);
            await api.Torrents.SetForceStartAsync(first.Hash, false);
            await api.Torrents.SetSuperSeedingAsync(first.Hash, true);
            await api.Torrents.ExecuteAsync(TorrentCommand.ToggleSequentialDownload, first.Hash);
            await api.Torrents.ExecuteAsync(TorrentCommand.ToggleFirstLastPiecePriority, first.Hash);
            await api.Torrents.ExecuteAsync(TorrentCommand.IncreasePriority, second.Hash);
            await api.Torrents.ExecuteAsync(TorrentCommand.TopPriority, first.Hash);
            await api.Torrents.ExecuteAsync(TorrentCommand.Reannounce, first.Hash);
            await api.Torrents.ExecuteAsync(TorrentCommand.Start, first.Hash);
            await api.Torrents.ExecuteAsync(TorrentCommand.Stop, first.Hash);

            first = (await api.Torrents.GetInfoAsync()).Single(torrent => torrent.Hash == first.Hash);
            Assert.Equal("Renamed contract torrent", first.Name);
            Assert.Equal("Managed", first.Category);
            Assert.Equal(Path.GetFullPath(managedPath), Path.GetFullPath(first.SavePath));
            Assert.Contains("contract", first.Tags);
            Assert.DoesNotContain("temporary", first.Tags);
            Assert.Equal(12345, first.DownloadLimit);
            Assert.Equal(54321, first.UploadLimit);
            Assert.Equal(0.75, first.RatioLimit, 3);
            Assert.True(first.SequentialDownload);
            Assert.True(first.FirstLastPiecePriority);
            Assert.True(first.SuperSeeding);
            Assert.Equal(7, Assert.Single(await api.Torrents.GetFilesAsync(first.Hash)).Priority);
            Assert.Contains((await api.Torrents.GetTrackersAsync(first.Hash)), tracker => tracker.Url == "https://tracker.example/announce");
            Assert.Contains("https://seed.example/file", await api.Torrents.GetWebSeedsAsync(first.Hash));

            await api.Torrents.RemoveTrackersAsync(first.Hash, ["https://tracker.example/announce"]);
            await api.Torrents.RemoveWebSeedsAsync(first.Hash, ["https://seed.example/file"]);
            await api.Torrents.RemoveCategoriesAsync(["Managed"]);
            await api.Torrents.DeleteTagsAsync(["temporary"]);
            Assert.Equal(string.Empty, (await api.Torrents.GetInfoAsync()).Single(torrent => torrent.Hash == first.Hash).Category);
            await api.Transfer.BanPeersAsync(["192.0.2.10:6881", "[2001:db8::10]:6881"]);
            Assert.Equal(2, (await api.Logs.GetPeersAsync()).Count);
            Assert.Contains("192.0.2.10", (await api.Application.GetPreferencesAsync())["banned_IPs"]!.GetValue<string>());
            Assert.NotEmpty(await api.Torrents.ExportAsync(first.Hash));
            Assert.Equal(first.Hash, (await api.Torrents.ParseMetadataAsync(firstPath))["hash"]!.GetValue<string>(), ignoreCase: true);

            await api.Torrents.DeleteAsync(second.Hash, deleteFiles: false);
            Assert.Single(await api.Torrents.GetInfoAsync());
            await host.StopAsync();
        }
        finally
        {
            await host.StopAsync(force: true);
            Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", null);
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", null);
            DeleteWritableTree(dataRoot);
        }
    }

    [Fact]
    public async Task MigratesLegacyFastresumeTransactionallyAndOnlyDeletesBackupOnRequest()
    {
        var engineHost = FindEngineHost();
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "WinBitTorrent.EngineHost.MigrationFixture", Guid.NewGuid().ToString("N"));
        var targetRoot = Path.Combine(Path.GetTempPath(), "WinBitTorrent.EngineHost.MigrationTarget", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", engineHost);
        try
        {
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", fixtureRoot);
            string hash;
            string corruptHash;
            await using (var fixtureHost = new EngineHostProcess(NullLogger<EngineHostProcess>.Instance))
            {
                await fixtureHost.StartAsync();
                await fixtureHost.Client!.Application.SetPreferencesAsync(new JsonObject
                {
                    ["resume_data_storage_type"] = "Legacy"
                });
                var torrentPath = Path.Combine(fixtureRoot, "migration.torrent");
                var downloads = Path.Combine(targetRoot, "Downloads");
                Directory.CreateDirectory(downloads);
                await File.WriteAllBytesAsync(torrentPath, CreateSingleFileTorrent("migration.txt", "legacy migration data"u8.ToArray()));
                await fixtureHost.Client.Torrents.AddAsync(new TorrentAddRequest(
                    [], [torrentPath], SavePath: downloads, StartTorrent: false,
                    Category: "Archive", Tags: "legacy, important"));
                hash = Assert.Single(await fixtureHost.Client.Torrents.GetInfoAsync()).Hash;
                var corruptTorrentPath = Path.Combine(fixtureRoot, "corrupt-resume.torrent");
                await File.WriteAllBytesAsync(corruptTorrentPath, CreateSingleFileTorrent("corrupt-resume.txt", "resume requires a hash check"u8.ToArray()));
                await fixtureHost.Client.Torrents.AddAsync(new TorrentAddRequest(
                    [], [corruptTorrentPath], SavePath: downloads, StartTorrent: false));
                corruptHash = (await fixtureHost.Client.Torrents.GetInfoAsync()).Single(value => !value.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase)).Hash;
                await fixtureHost.StopAsync();
            }

            var legacyRoot = Path.Combine(targetRoot, "Backend", "Profile", "qBittorrent");
            var backupRoot = Path.Combine(legacyRoot, "data", "BT_backup");
            var configRoot = Path.Combine(legacyRoot, "config");
            Directory.CreateDirectory(backupRoot);
            Directory.CreateDirectory(configRoot);
            File.Copy(Path.Combine(fixtureRoot, "Engine", "torrents", hash + ".torrent"), Path.Combine(backupRoot, hash + ".torrent"));
            var qbitResumePath = Path.Combine(backupRoot, hash + ".fastresume");
            File.Copy(Path.Combine(fixtureRoot, "Engine", "resume", hash + ".fastresume"), qbitResumePath);
            AddQbittorrentResumeFields(qbitResumePath);
            File.Copy(Path.Combine(fixtureRoot, "Engine", "torrents", corruptHash + ".torrent"), Path.Combine(backupRoot, corruptHash + ".torrent"));
            await File.WriteAllBytesAsync(Path.Combine(backupRoot, corruptHash + ".fastresume"), "damaged resume data"u8.ToArray());
            await File.WriteAllTextAsync(Path.Combine(configRoot, "qBittorrent.ini"), $"""
                [BitTorrent]
                Session\ResumeDataStorageType=Legacy
                Session\DefaultSavePath={Path.Combine(targetRoot, "Downloads")}
                Session\Tags=legacy,important
                """);
            await File.WriteAllTextAsync(Path.Combine(configRoot, "categories.json"), """
                {"Archive":{"savePath":"","downloadPath":""}}
                """);
            var dataSentinel = Path.Combine(targetRoot, "Downloads", "do-not-move.bin");
            await File.WriteAllTextAsync(dataSentinel, "downloaded data remains owned by the user");
            var sentinelHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(dataSentinel)));

            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", targetRoot);
            await using var host = new EngineHostProcess(NullLogger<EngineHostProcess>.Instance);
            await host.StartAsync();
            var migratedTorrents = await host.Client!.Torrents.GetInfoAsync();
            Assert.Equal(2, migratedTorrents.Count);
            var migrated = migratedTorrents.Single(value => value.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(hash, migrated.Hash, ignoreCase: true);
            Assert.Equal("Archive", migrated.Category);
            Assert.Contains("legacy", migrated.Tags);
            var recovered = migratedTorrents.Single(value => value.Hash.Equals(corruptHash, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Path.Combine(targetRoot, "Downloads"), recovered.SavePath, ignoreCase: true);
            Assert.Equal("stoppedDL", recovered.State);
            Assert.Equal("Legacy", (await host.Client.Application.GetPreferencesAsync())["resume_data_storage_type"]!.GetValue<string>());
            Assert.Equal(sentinelHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(dataSentinel))));

            var markerPath = Path.Combine(targetRoot, "Engine", "migration.json");
            Assert.True(File.Exists(markerPath));
            var report = await host.Client.ClientData.LoadAsync("migration.report");
            Assert.True(report["pending"]!.GetValue<bool>());
            Assert.Equal(2, report["torrentCount"]!.GetValue<int>());
            Assert.Contains(report["needsHashCheck"]!.AsArray(), value =>
                value!.GetValue<string>().Equals(corruptHash, StringComparison.OrdinalIgnoreCase));
            var immutableBackup = report["backupPath"]!.GetValue<string>();
            Assert.True(Directory.Exists(immutableBackup));
            Assert.True(File.GetAttributes(Path.Combine(immutableBackup, "manifest.json")).HasFlag(FileAttributes.ReadOnly));

            await host.StopAsync();
            await host.StartAsync();
            Assert.Equal(2, (await host.Client!.Torrents.GetInfoAsync()).Count);
            Assert.Equal(immutableBackup, (await host.Client.ClientData.LoadAsync("migration.report"))["backupPath"]!.GetValue<string>());
            Assert.True(await host.Client.Application.DeleteMigrationBackupAsync());
            Assert.False(Directory.Exists(immutableBackup));
            Assert.False(await host.Client.Application.DeleteMigrationBackupAsync());
            await host.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", null);
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", null);
            DeleteWritableTree(fixtureRoot);
            DeleteWritableTree(targetRoot);
        }
    }

    [Fact]
    public async Task AuthenticatedPipePersistsPreferencesAndClientData()
    {
        var engineHost = FindEngineHost();
        Assert.True(File.Exists(engineHost), $"EngineHost was not built at {engineHost}");
        var dataRoot = Path.Combine(Path.GetTempPath(), "WinBitTorrent.EngineHost.Tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", engineHost);
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", dataRoot);
        await using var host = new EngineHostProcess(NullLogger<EngineHostProcess>.Instance);

        try
        {
            var session = await host.StartAsync();
            Assert.True(host.IsRunning);
            Assert.NotNull(host.Client);
            Assert.Equal(session.ProcessId, host.Session?.ProcessId);
            Assert.Contains("libtorrent 2.0.13", session.BackendVersion, StringComparison.OrdinalIgnoreCase);

            await host.Client!.Application.SetPreferencesAsync(new JsonObject
            {
                ["save_path"] = Path.Combine(dataRoot, "Downloads"),
                ["dht"] = true,
                ["resume_data_storage_type"] = "SQLite"
            });
            var preferences = await host.Client.Application.GetPreferencesAsync();
            Assert.True(preferences["dht"]!.GetValue<bool>());
            Assert.Equal(Path.Combine(dataRoot, "Downloads"), await host.Client.Application.GetDefaultSavePathAsync());

            await host.Client.ClientData.StoreAsync("test", new JsonObject { ["answer"] = 42 });
            var clientData = await host.Client.ClientData.LoadAsync("test");
            Assert.Equal(42, clientData["answer"]!.GetValue<int>());

            var torrentPath = Path.Combine(dataRoot, "sample.torrent");
            var downloadPath = Path.Combine(dataRoot, "Downloads");
            Directory.CreateDirectory(downloadPath);
            await File.WriteAllBytesAsync(torrentPath, CreateSingleFileTorrent("sample.txt", "engine integration test"u8.ToArray()));
            await host.Client.Torrents.AddAsync(new TorrentAddRequest(
                [], [torrentPath], SavePath: downloadPath, StartTorrent: false,
                Category: "Tests", Tags: "native, engine"));

            var torrents = await host.Client.Torrents.GetInfoAsync();
            var torrent = Assert.Single(torrents);
            Assert.Equal("sample.txt", torrent.Name);
            Assert.Equal("Tests", torrent.Category);
            Assert.Contains("native", torrent.Tags);
            Assert.Equal("stoppedDL", torrent.State);
            Assert.Single(await host.Client.Torrents.GetFilesAsync(torrent.Hash));
            Assert.NotEmpty(await host.Client.Torrents.ExportAsync(torrent.Hash));

            await host.Client.Torrents.SetFilePriorityAsync(torrent.Hash, [0], 0);
            Assert.Equal(0, Assert.Single(await host.Client.Torrents.GetFilesAsync(torrent.Hash)).Priority);

            var plugins = await host.Client.Search.GetPluginsAsync();
            Assert.NotEmpty(plugins);
            await host.Client.Application.SetPreferencesAsync(new JsonObject { ["search_enabled"] = false });
            await Assert.ThrowsAsync<LocalEngineException>(() => host.Client.Search.StartAsync("must not run"));
            await host.Client.Application.SetPreferencesAsync(new JsonObject { ["search_enabled"] = true });

            var creatorSource = Path.Combine(dataRoot, "creator-source.txt");
            await File.WriteAllTextAsync(creatorSource, "created directly by libtorrent");
            var creator = await host.Client.TorrentCreator.AddTaskAsync(new JsonObject
            {
                ["sourcePath"] = creatorSource,
                ["pieceSize"] = 16384,
                ["isPrivate"] = true,
                ["comment"] = "EngineHost integration test",
                ["trackers"] = "https://tracker.example/announce"
            });
            var creatorId = creator["taskID"]!.GetValue<string>();
            JsonObject creatorStatus;
            do
            {
                await Task.Delay(50);
                creatorStatus = await host.Client.TorrentCreator.GetStatusAsync(creatorId);
            } while (creatorStatus["status"]!.GetValue<string>() is "Queued" or "Running");
            Assert.Equal("Finished", creatorStatus["status"]!.GetValue<string>());
            Assert.NotEmpty(await host.Client.TorrentCreator.GetTorrentFileAsync(creatorId));
            await host.Client.TorrentCreator.DeleteTaskAsync(creatorId);

            await host.Client.Rss.AddFolderAsync("News");
            await using (var feed = await OneShotHttpServer.StartAsync("""
                <?xml version="1.0" encoding="utf-8"?>
                <rss version="2.0"><channel><title>Engine news</title><item>
                <guid>article-1</guid><title>WinBitTorrent 1.0</title>
                <link>https://example.test/article</link><enclosure url="magnet:?xt=urn:btih:0123456789012345678901234567890123456789" />
                <pubDate>Mon, 17 Aug 2026 12:00:00 GMT</pubDate><description>Test feed</description>
                </item></channel></rss>
                """))
            {
                await host.Client.Rss.AddFeedAsync(feed.Url, "News\\Releases");
            }
            var rssItems = await host.Client.Rss.GetItemsAsync();
            var rssFeed = rssItems["News"]!["Releases"]!.AsObject();
            Assert.Equal("Engine news", rssFeed["title"]!.GetValue<string>());
            Assert.Single(rssFeed["articles"]!.AsArray());
            await host.Client.Rss.SetRuleAsync("WinBitTorrent", new JsonObject
            {
                ["enabled"] = true,
                ["mustContain"] = "WinBitTorrent",
                ["mustNotContain"] = "nightly",
                ["affectedFeeds"] = new JsonArray("News\\Releases")
            });
            Assert.Single(await host.Client.Rss.GetMatchingArticlesAsync("WinBitTorrent"));
            Assert.NotEmpty(await host.Client.Logs.GetMainAsync());

            var remoteApiPort = GetFreeTcpPort();
            var remoteApiKey = await host.Client.Application.RotateApiKeyAsync();
            const string remoteApiPassword = "RemoteApi-Test-Password-42";
            await host.Client.Application.ChangeRemoteApiPasswordAsync(remoteApiPassword);
            await host.Client.Application.SetPreferencesAsync(new JsonObject
            {
                ["web_ui_address"] = "127.0.0.1",
                ["web_ui_port"] = remoteApiPort
            });

            await host.StopAsync();
            Assert.False(host.IsRunning);
            AssertResumeFiles(dataRoot, torrent.Hash, expected: false);
            await AssertResumeBlobsAsync(dataRoot, torrent.Hash);

            await host.StartAsync();
            var restored = Assert.Single(await host.Client!.Torrents.GetInfoAsync());
            Assert.Equal("Tests", restored.Category);
            Assert.Contains("native", restored.Tags);
            Assert.Equal(0, Assert.Single(await host.Client.Torrents.GetFilesAsync(restored.Hash)).Priority);
            using (var remoteApi = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{remoteApiPort}/api/v1/") })
            {
                Assert.Equal(HttpStatusCode.Unauthorized, (await remoteApi.GetAsync("torrents")).StatusCode);
                remoteApi.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", remoteApiKey);
                Assert.Equal(HttpStatusCode.OK, (await remoteApi.GetAsync("torrents")).StatusCode);
                var openApi = await remoteApi.GetFromJsonAsync<JsonObject>("openapi.json");
                var paths = openApi!["paths"]!.AsObject();
                Assert.Contains("/torrents/{hashes}/command", paths);
                Assert.Contains("/rss/action", paths);
                Assert.Contains("/search/{id}", paths);
                Assert.Contains("/creator/{taskId}/file", paths);
                Assert.Contains("/events", paths);

                await host.Client.Application.DeleteApiKeyAsync();
                Assert.Equal(HttpStatusCode.Unauthorized, (await remoteApi.GetAsync("torrents")).StatusCode);
            }

            var apiBaseAddress = new Uri($"http://127.0.0.1:{remoteApiPort}/api/v1/");
            using (var cookies = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true })
            using (var remoteApi = new HttpClient(cookies) { BaseAddress = apiBaseAddress })
            {
                var invalidLogin = await remoteApi.PostAsJsonAsync("auth/login", new { username = "admin", password = "wrong-password" });
                Assert.True(invalidLogin.StatusCode == HttpStatusCode.Unauthorized,
                    $"Invalid login returned {(int)invalidLogin.StatusCode} from {invalidLogin.RequestMessage?.RequestUri}: " +
                    $"headers={string.Join(";", invalidLogin.Headers.Select(static item => $"{item.Key}={string.Join(',', item.Value)}"))}; " +
                    $"body={await invalidLogin.Content.ReadAsStringAsync()}");

                var login = await remoteApi.PostAsJsonAsync("auth/login", new { username = "admin", password = remoteApiPassword });
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                var csrf = (await login.Content.ReadFromJsonAsync<JsonObject>())!["csrfToken"]!.GetValue<string>();
                Assert.Equal(HttpStatusCode.OK, (await remoteApi.GetAsync("settings")).StatusCode);

                using var rejectedPatch = new HttpRequestMessage(HttpMethod.Patch, "settings")
                {
                    Content = JsonContent.Create(new { dht = true })
                };
                Assert.Equal(HttpStatusCode.Forbidden, (await remoteApi.SendAsync(rejectedPatch)).StatusCode);

                using var acceptedPatch = new HttpRequestMessage(HttpMethod.Patch, "settings")
                {
                    Content = JsonContent.Create(new { dht = true })
                };
                acceptedPatch.Headers.Add("X-WinBitTorrent-CSRF", csrf);
                Assert.Equal(HttpStatusCode.OK, (await remoteApi.SendAsync(acceptedPatch)).StatusCode);
            }

            await host.Client.Application.SetPreferencesAsync(new JsonObject
            {
                ["resume_data_storage_type"] = "Legacy"
            });
            AssertResumeFiles(dataRoot, restored.Hash, expected: true);
            await host.StopAsync();
            AssertResumeFiles(dataRoot, restored.Hash, expected: true);

            await host.StartAsync();
            Assert.Single(await host.Client!.Torrents.GetInfoAsync());
            await host.Client.Application.SetPreferencesAsync(new JsonObject
            {
                ["resume_data_storage_type"] = "SQLite"
            });
            AssertResumeFiles(dataRoot, restored.Hash, expected: false);
            await AssertResumeBlobsAsync(dataRoot, restored.Hash);
            await host.StopAsync();
        }
        finally
        {
            await host.StopAsync(force: true);
            Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", null);
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", null);
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static string FindEngineHost()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "WinBitTorrent.EngineHost",
                "bin",
                "Debug",
                "net8.0-windows10.0.19041.0",
                "WinBitTorrent.EngineHost.exe");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        return string.Empty;
    }

    private static byte[] CreateSingleFileTorrent(string name, byte[] contents)
    {
        using var output = new MemoryStream();
        static void Write(MemoryStream stream, string value)
            => stream.Write(Encoding.UTF8.GetBytes(value));

        Write(output, "d4:infod6:lengthi");
        Write(output, contents.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Write(output, "e4:name");
        Write(output, Encoding.UTF8.GetByteCount(name).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Write(output, ":");
        Write(output, name);
        Write(output, "12:piece lengthi16384e6:pieces");
        var pieceHashes = new byte[((contents.Length + 16383) / 16384) * 20];
        for (var offset = 0; offset < contents.Length; offset += 16384)
        {
            var piece = contents.AsSpan(offset, Math.Min(16384, contents.Length - offset));
            SHA1.HashData(piece).CopyTo(pieceHashes.AsSpan(offset / 16384 * 20, 20));
        }
        Write(output, pieceHashes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Write(output, ":");
        output.Write(pieceHashes);
        Write(output, "ee");
        return output.ToArray();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!await condition())
            await Task.Delay(100, cancellation.Token);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void DeleteWritableTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(path, recursive: true);
    }

    private static void AddQbittorrentResumeFields(string path)
    {
        var resume = File.ReadAllBytes(path);
        Assert.NotEmpty(resume);
        Assert.Equal((byte)'e', resume[^1]);
        var qbitFields = Encoding.UTF8.GetBytes("12:qBt-category7:Archive8:qBt-tagsl6:legacy9:importante");
        var result = new byte[resume.Length - 1 + qbitFields.Length + 1];
        Buffer.BlockCopy(resume, 0, result, 0, resume.Length - 1);
        Buffer.BlockCopy(qbitFields, 0, result, resume.Length - 1, qbitFields.Length);
        result[^1] = (byte)'e';
        File.WriteAllBytes(path, result);
    }

    private static void AssertResumeFiles(string dataRoot, string hash, bool expected)
    {
        var engineRoot = Path.Combine(dataRoot, "Engine");
        Assert.Equal(expected, File.Exists(Path.Combine(engineRoot, "torrents", hash + ".torrent")));
        Assert.Equal(expected, File.Exists(Path.Combine(engineRoot, "resume", hash + ".fastresume")));
    }

    private static async Task AssertResumeBlobsAsync(string dataRoot, string hash)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(dataRoot, "Engine", "engine.db")};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT length(metadata), length(resume_data) FROM torrents WHERE hash=$hash";
        command.Parameters.AddWithValue("$hash", hash);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetInt64(0) > 0);
        Assert.True(reader.GetInt64(1) > 0);
    }

    private sealed class OneShotHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _server;

        private OneShotHttpServer(TcpListener listener, Task server, string url)
        {
            _listener = listener;
            _server = server;
            Url = url;
        }

        public string Url { get; }

        public static Task<OneShotHttpServer> StartAsync(string body)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                var bytes = Encoding.UTF8.GetBytes(body);
                var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/rss+xml; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers);
                await stream.WriteAsync(bytes);
            });
            return Task.FromResult(new OneShotHttpServer(listener, server, $"http://127.0.0.1:{port}/feed.xml"));
        }

        public async ValueTask DisposeAsync()
        {
            try { await _server.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            _listener.Stop();
        }
    }

    private sealed class RangeHttpFileServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _content;
        private readonly TimeSpan _chunkDelay;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private readonly Task _acceptLoop;
        private long _bytesServed;

        private RangeHttpFileServer(TcpListener listener, byte[] content, TimeSpan chunkDelay)
        {
            _listener = listener;
            _content = content;
            _chunkDelay = chunkDelay;
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/payload.bin";
            _acceptLoop = AcceptLoopAsync(_lifetime.Token);
        }

        public string Url { get; }
        public long BytesServed => Interlocked.Read(ref _bytesServed);

        public static Task<RangeHttpFileServer> StartAsync(byte[] content, TimeSpan chunkDelay)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new RangeHttpFileServer(listener, content, chunkDelay));
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    _connections.Add(HandleClientAsync(client, cancellationToken));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                try
                {
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync(cancellationToken);
                    var isHead = requestLine?.StartsWith("HEAD ", StringComparison.OrdinalIgnoreCase) == true;
                    long start = 0;
                    long end = _content.Length - 1L;
                    var partial = false;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken)))
                    {
                        if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var value = line[(line.IndexOf(':') + 1)..].Trim();
                        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var range = value[6..].Split(',', 2)[0];
                        var separator = range.IndexOf('-');
                        if (separator < 0 || !long.TryParse(range[..separator], out start))
                            continue;
                        if (separator + 1 < range.Length && long.TryParse(range[(separator + 1)..], out var requestedEnd))
                            end = requestedEnd;
                        start = Math.Clamp(start, 0, _content.Length - 1L);
                        end = Math.Clamp(end, start, _content.Length - 1L);
                        partial = true;
                    }

                    var length = end - start + 1;
                    var status = partial ? "206 Partial Content" : "200 OK";
                    var contentRange = partial ? $"Content-Range: bytes {start}-{end}/{_content.Length}\r\n" : string.Empty;
                    var headers = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status}\r\nContent-Type: application/octet-stream\r\nAccept-Ranges: bytes\r\n{contentRange}Content-Length: {length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, cancellationToken);
                    if (isHead)
                        return;

                    await _requestGate.WaitAsync(cancellationToken);
                    try
                    {
                        for (var offset = start; offset <= end; offset += 16 * 1024)
                        {
                            var count = (int)Math.Min(16 * 1024, end - offset + 1);
                            await stream.WriteAsync(_content.AsMemory((int)offset, count), cancellationToken);
                            Interlocked.Add(ref _bytesServed, count);
                            if (_chunkDelay > TimeSpan.Zero)
                                await Task.Delay(_chunkDelay, cancellationToken);
                        }
                    }
                    finally
                    {
                        _requestGate.Release();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (IOException) { }
                catch (SocketException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _listener.Stop();
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            try { await Task.WhenAll(_connections.ToArray()).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            _requestGate.Dispose();
            _lifetime.Dispose();
        }
    }
}
