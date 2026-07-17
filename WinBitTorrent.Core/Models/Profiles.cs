namespace WinBitTorrent.Core.Models;

public enum ProfileKind
{
    LocalManaged,
    Remote
}

public enum AuthenticationMode
{
    LocalApiKey,
    UserNamePassword,
    ApiKey
}

public enum ConnectionState
{
    Disconnected,
    StartingBackend,
    Connecting,
    Authenticating,
    Connected,
    Reconnecting,
    Faulted,
    Stopping
}

public sealed record ServerProfile(
    Guid Id,
    string Name,
    ProfileKind Kind,
    Uri BaseAddress,
    AuthenticationMode Authentication,
    string? UserName = null,
    string? TrustedCertificateSha256 = null,
    bool IsBuiltIn = false)
{
    public static readonly Guid LocalProfileId = Guid.Parse("4d88f824-4189-48c8-8dba-a12cb4394529");

    public static ServerProfile CreateLocal(Uri baseAddress) => new(
        LocalProfileId,
        "Local qBittorrent",
        ProfileKind.LocalManaged,
        baseAddress,
        AuthenticationMode.LocalApiKey,
        IsBuiltIn: true);
}

public sealed record BackendSession(
    int ProcessId,
    Uri BaseAddress,
    string QbittorrentVersion,
    string WebApiVersion,
    DateTimeOffset StartedAt);

public sealed record ConnectionSnapshot(
    ConnectionState State,
    ServerProfile? Profile,
    BackendSession? Backend,
    string? Error = null);
