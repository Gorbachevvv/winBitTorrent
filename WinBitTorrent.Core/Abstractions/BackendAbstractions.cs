using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Core.Abstractions;

public interface IManagedBackendHost : IAsyncDisposable
{
    BackendSession? Session { get; }
    bool IsRunning { get; }
    event EventHandler<string>? OutputReceived;
    event EventHandler<Exception>? Failed;

    Task<BackendSession> StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(bool force = false, CancellationToken cancellationToken = default);
}

public interface IServerProfileStore
{
    Task<IReadOnlyList<ServerProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ServerProfile?> GetSelectedAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ServerProfile profile, CancellationToken cancellationToken = default);
    Task SelectAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ICredentialStore
{
    Task<string?> GetSecretAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task SetSecretAsync(Guid profileId, string secret, CancellationToken cancellationToken = default);
    Task DeleteSecretAsync(Guid profileId, CancellationToken cancellationToken = default);
}

public interface IConnectionCoordinator : IAsyncDisposable
{
    ConnectionSnapshot Snapshot { get; }
    IQBittorrentApi? Api { get; }
    event EventHandler<ConnectionSnapshot>? StateChanged;

    Task<IQBittorrentApi> ConnectAsync(ServerProfile profile, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
