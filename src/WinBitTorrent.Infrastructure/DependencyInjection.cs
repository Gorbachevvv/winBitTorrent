using Microsoft.Extensions.DependencyInjection;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Infrastructure.Backend;
#if !STORE_BUILD
using WinBitTorrent.Infrastructure.Catalog;
using WinBitTorrent.Infrastructure.Trackers;
using WinBitTorrent.Infrastructure.Updates;
#endif
using WinBitTorrent.Infrastructure.Connection;
using WinBitTorrent.Infrastructure.Engine;
using WinBitTorrent.Infrastructure.Storage;

namespace WinBitTorrent.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddWinBitTorrentInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ICredentialStore, PasswordVaultCredentialStore>();
#if !STORE_BUILD
        services.AddSingleton<ITrackerCredentialStore, PasswordVaultTrackerCredentialStore>();
        services.AddSingleton<ITrackerSearchProvider, RuTrackerProvider>();
        services.AddSingleton<ITrackerSearchProvider, PirateBayProvider>();
        services.AddSingleton<ICatalogProvider, TmdbCatalogProvider>();
#endif
        services.AddSingleton<IServerProfileStore, JsonServerProfileStore>();
        services.AddSingleton<IManagedBackendHost, EngineHostProcess>();
        services.AddSingleton<IConnectionCoordinator, ConnectionCoordinator>();
#if !STORE_BUILD
        services.AddSingleton<IUpdateService, GitHubUpdateService>();
#endif
        return services;
    }
}
