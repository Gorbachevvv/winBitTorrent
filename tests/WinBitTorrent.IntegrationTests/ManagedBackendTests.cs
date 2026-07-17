using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Infrastructure.Api;
using WinBitTorrent.Infrastructure.Backend;

namespace WinBitTorrent.IntegrationTests;

[CollectionDefinition("Backend", DisableParallelization = true)]
public sealed class BackendCollection;

[Collection("Backend")]
public sealed class ManagedBackendTests
{
    [BackendFact]
    public async Task BootstrapsApiKeyExercisesModulesAndShutsDownCleanly()
    {
        var backend = BackendFactAttribute.FindBackend()!;
        var dataRoot = Path.Combine(Path.GetTempPath(), "WinBitTorrent.Tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINBITTORRENT_BACKEND_PATH", backend);
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", dataRoot);
        var secrets = new MemoryCredentialStore();
        await using var host = new ManagedBackendHost(secrets, NullLogger<ManagedBackendHost>.Instance);

        try
        {
            var session = await host.StartAsync();
            Assert.Equal("v5.2.3", session.QbittorrentVersion);
            Assert.Equal("2.15.1", session.WebApiVersion);
            Assert.True(IPAddress.IsLoopback(session.BaseAddress.HostNameType == UriHostNameType.IPv4
                ? IPAddress.Parse(session.BaseAddress.Host)
                : IPAddress.Loopback));
            Assert.NotNull(await secrets.GetSecretAsync(WinBitTorrent.Core.Models.ServerProfile.LocalProfileId));

            await using var api = QbittorrentApi.Create(ServerProfile.CreateLocal(session.BaseAddress), await secrets.GetSecretAsync(ServerProfile.LocalProfileId));
            Assert.Equal("v5.2.3", await api.Application.GetVersionAsync());
            Assert.NotNull(await api.Application.GetProcessInfoAsync());
            Assert.NotNull(await api.Application.GetPreferencesAsync());
            Assert.NotNull(await api.Sync.GetMainDataAsync(0));
            Assert.NotNull(await api.Rss.GetItemsAsync());
            Assert.NotNull(await api.Rss.GetRulesAsync());
            Assert.NotNull(await api.Search.GetPluginsAsync());

            var processId = session.ProcessId;
            await host.StopAsync();
            Assert.False(host.IsRunning);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
        }
        finally
        {
            await host.StopAsync(force: true);
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<Guid, string> _values = [];
        public Task<string?> GetSecretAsync(Guid profileId, CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(profileId));
        public Task SetSecretAsync(Guid profileId, string secret, CancellationToken cancellationToken = default) { _values[profileId] = secret; return Task.CompletedTask; }
        public Task DeleteSecretAsync(Guid profileId, CancellationToken cancellationToken = default) { _values.Remove(profileId); return Task.CompletedTask; }
    }
}

public sealed class BackendFactAttribute : FactAttribute
{
    public BackendFactAttribute()
    {
        if (FindBackend() is null)
            Skip = "Build Backend/qbittorrent-nox.exe with build/build-backend.ps1 to run native integration tests.";
    }

    public static string? FindBackend()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("WINBITTORRENT_BACKEND_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment)) return Path.GetFullPath(fromEnvironment);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Backend", "qbittorrent-nox.exe");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }
}
