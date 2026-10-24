namespace WinBitTorrent.Infrastructure.Storage;

public static class AppPaths
{
#if STORE_BUILD
    private const string DataFolder = "WinBitTorrent.Store";
#else
    private const string DataFolder = "WinBitTorrent";
#endif
    public static string Root => Environment.GetEnvironmentVariable("WINBITTORRENT_DATA_ROOT") is { Length: > 0 } overridden
        ? Path.GetFullPath(overridden)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolder);

    public static string ProfilesFile => Path.Combine(Root, "profiles.json");
    public static string EngineRoot => Path.Combine(Root, "Engine");
    public static string Logs => Path.Combine(Root, "Logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(EngineRoot);
        Directory.CreateDirectory(Logs);
    }
}
