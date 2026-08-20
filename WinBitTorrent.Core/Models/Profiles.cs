namespace WinBitTorrent.Core.Models;

public enum ProfileKind
{
    LocalLibtorrent = 0,
    RemoteQbittorrent = 1,

    // Serialized profiles from releases through 1.2 use these numeric values.
    // Keep aliases so upgrades do not invalidate profiles.json.
    LocalManaged = LocalLibtorrent,
    Remote = RemoteQbittorrent
}

[Flags]
public enum BackendCapabilities
{
    None = 0,
    LocalFileSystem = 1 << 0,
    Preferences = 1 << 1,
    Rss = 1 << 2,
    Search = 1 << 3,
    TorrentCreator = 1 << 4,
    Logs = 1 << 5,
    RemoteAccess = 1 << 6,
    All = LocalFileSystem | Preferences | Rss | Search | TorrentCreator | Logs | RemoteAccess
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
        "Local WinBitTorrent",
        ProfileKind.LocalLibtorrent,
        baseAddress,
        AuthenticationMode.LocalApiKey,
        IsBuiltIn: true);
}

public sealed record BackendSession(
    int ProcessId,
    Uri BaseAddress,
    string BackendVersion,
    string ProtocolVersion,
    DateTimeOffset StartedAt);

public sealed record ConnectionSnapshot(
    ConnectionState State,
    ServerProfile? Profile,
    BackendSession? Backend,
    string? Error = null);
