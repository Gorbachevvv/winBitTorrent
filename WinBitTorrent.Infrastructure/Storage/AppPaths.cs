namespace WinBitTorrent.Infrastructure.Storage;

public static class AppPaths
{
    public static string Root { get; } = Environment.GetEnvironmentVariable("WINBITTORRENT_DATA_ROOT") is { Length: > 0 } overridden
        ? Path.GetFullPath(overridden)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinBitTorrent");

    public static string ProfilesFile => Path.Combine(Root, "profiles.json");
    public static string BackendRoot => Path.Combine(Root, "Backend");
    public static string BackendProfile => Path.Combine(BackendRoot, "Profile");
    public static string BackendProfileBase => Path.Combine(BackendProfile, "qBittorrent");
    public static string BackendConfig => Path.Combine(BackendProfileBase, "config", "qBittorrent.ini");
    public static string BackendData => Path.Combine(BackendProfileBase, "data");
    public static string BackendState => Path.Combine(BackendRoot, "host.json");
    public static string Logs => Path.Combine(Root, "Logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BackendRoot);
        Directory.CreateDirectory(BackendProfile);
        Directory.CreateDirectory(BackendProfileBase);
        Directory.CreateDirectory(Logs);
    }
}
