namespace WinBitTorrent.Core.Abstractions;

/// <summary>A published release discovered on the update feed (GitHub releases).</summary>
public sealed record UpdateRelease(
    Version Version,
    string Name,
    string ReleaseNotes,
    Uri ReleasePageUrl,
    Uri? InstallerUrl,
    long InstallerSize);

public interface IUpdateService
{
    /// <summary>
    /// Fetches the latest published (non-draft, non-prerelease) release, or null when the
    /// feed is unreachable or has no releases. Does not compare against the current version.
    /// </summary>
    Task<UpdateRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the release's installer to a temporary file and returns its path.
    /// <paramref name="progress"/> reports completion from 0.0 to 1.0.
    /// </summary>
    Task<string> DownloadInstallerAsync(
        UpdateRelease release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
