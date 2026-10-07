using Microsoft.Extensions.Logging.Abstractions;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Infrastructure.Engine;

namespace WinBitTorrent.IntegrationTests;

[Collection("Backend")]
public sealed class StoreEngineTests
{
    [StoreEngineFact]
    public async Task ReducedEngineSupportsTorrentsButRejectsSearchAndPluginInstallation()
    {
        var previousRoot = Environment.GetEnvironmentVariable("WINBITTORRENT_DATA_ROOT");
        var previousHost = Environment.GetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH");
        var root = Path.Combine(Path.GetTempPath(), "WinBitTorrent-StoreEngineTests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", root);
        Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", Environment.GetEnvironmentVariable("WINBITTORRENT_STORE_ENGINE"));
        await using var host = new EngineHostProcess(NullLogger<EngineHostProcess>.Instance);
        try
        {
            await host.StartAsync();
            var client = host.Client!;
            Assert.Empty(await client.Torrents.GetInfoAsync());
            var preferences = await client.Application.GetPreferencesAsync();
            Assert.False(preferences["search_enabled"]!.GetValue<bool>());
            await client.Torrents.AddAsync(new TorrentAddRequest(
                ["magnet:?xt=urn:btih:1111111111111111111111111111111111111111&dn=Store-test"], [],
                SavePath: Path.Combine(root, "downloads"), StartTorrent: false));
            Assert.Single(await client.Torrents.GetInfoAsync());
            await Assert.ThrowsAnyAsync<Exception>(() => client.Search.StartAsync("test"));
            await Assert.ThrowsAnyAsync<Exception>(() => client.Search.InstallPluginAsync("https://example.invalid/plugin.py"));
            await Assert.ThrowsAnyAsync<Exception>(() => client.Search.UpdatePluginsAsync());
            Assert.False(Directory.Exists(Path.Combine(root, "Engine", "SearchPlugins")));
            await client.Torrents.DeleteAsync("all", deleteFiles: false);
        }
        finally
        {
            await host.StopAsync(force: true);
            Environment.SetEnvironmentVariable("WINBITTORRENT_DATA_ROOT", previousRoot);
            Environment.SetEnvironmentVariable("WINBITTORRENT_ENGINE_HOST_PATH", previousHost);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

public sealed class StoreEngineFactAttribute : FactAttribute
{
    public StoreEngineFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("WINBITTORRENT_STORE_ENGINE")))
            Skip = "Set WINBITTORRENT_STORE_ENGINE to the reduced engine executable.";
    }
}
