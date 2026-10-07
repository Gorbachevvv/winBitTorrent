namespace WinBitTorrent.EngineHost;

internal sealed partial class EngineState
{
    private static string ResolveBackendRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("WINBITTORRENT_BACKEND_ROOT");
        var candidates = new[]
        {
            overridden,
            Path.Combine(AppContext.BaseDirectory, "Backend"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Backend")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Backend"))
        };
        return candidates.FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            ?? Path.Combine(AppContext.BaseDirectory, "Backend");
    }
}
