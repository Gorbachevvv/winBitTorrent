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
            if (Directory.Exists(backend)) return backend;
            if (File.Exists(Path.Combine(directory.FullName, "WinBitTorrent.slnx")))
            {
                var staged = Path.Combine(directory.FullName, "artifacts", "runtime", "Backend");
                if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                    staged = Path.Combine(staged, "arm64");
                if (Directory.Exists(staged)) return staged;
            }
            directory = directory.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "Backend");
    }
}
