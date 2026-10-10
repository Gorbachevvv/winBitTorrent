using System.Runtime.InteropServices;

namespace WinBitTorrent.EngineHost;

internal sealed partial class EngineState
{
    private static string ResolveBackendRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("WINBITTORRENT_BACKEND_ROOT");
        if (!string.IsNullOrWhiteSpace(overridden) && Directory.Exists(overridden)) return overridden;
        // Packaged builds use Backend next to EngineHost. Development builds may
        // be nested under bin/<platform>/<configuration>/<framework>/<rid>.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var backend = Path.Combine(directory.FullName, "Backend");
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 && Directory.Exists(Path.Combine(backend, "arm64")))
                return Path.Combine(backend, "arm64");
            if (Directory.Exists(backend)) return backend;
            directory = directory.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "Backend");
    }
}
