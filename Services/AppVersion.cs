using System.Reflection;

namespace WinBitTorrent.Services;

/// <summary>
/// The running application's version, taken from the single source in
/// Directory.Build.props (&lt;Version&gt;) via the compiled assembly metadata.
/// </summary>
public static class AppVersion
{
    /// <summary>The current version as major.minor.patch (build metadata stripped).</summary>
    public static Version Current { get; } = Resolve();

    /// <summary>Human-friendly form, e.g. "1.2.3".</summary>
    public static string Display => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    private static Version Resolve()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is not null)
        {
            // Drop any "+metadata" / "-prerelease" suffix a build might append.
            var numeric = informational.Split('+', '-')[0];
            if (Version.TryParse(numeric, out var parsed))
                return Normalize(parsed);
        }

        return Normalize(assembly.GetName().Version ?? new Version(0, 0, 0));
    }

    private static Version Normalize(Version value)
        => new(value.Major, value.Minor, Math.Max(value.Build, 0));
}
