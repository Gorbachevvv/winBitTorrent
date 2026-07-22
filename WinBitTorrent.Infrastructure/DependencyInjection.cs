using Microsoft.Extensions.DependencyInjection;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Infrastructure.Backend;
using WinBitTorrent.Infrastructure.Catalog;
using WinBitTorrent.Infrastructure.Connection;
using WinBitTorrent.Infrastructure.Storage;
using WinBitTorrent.Infrastructure.Trackers;
using WinBitTorrent.Infrastructure.Updates;

namespace WinBitTorrent.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddWinBitTorrentInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ICredentialStore, PasswordVaultCredentialStore>();
        services.AddSingleton<ITrackerCredentialStore, PasswordVaultTrackerCredentialStore>();
        services.AddSingleton<ITrackerSearchProvider, RuTrackerProvider>();
        services.AddSingleton<ICatalogProvider, TmdbCatalogProvider>();
        services.AddSingleton<IServerProfileStore, JsonServerProfileStore>();
        services.AddSingleton<IManagedBackendHost, ManagedBackendHost>();
        services.AddSingleton<IConnectionCoordinator, ConnectionCoordinator>();
        services.AddSingleton<IUpdateService, GitHubUpdateService>();
        return services;
    }
}
