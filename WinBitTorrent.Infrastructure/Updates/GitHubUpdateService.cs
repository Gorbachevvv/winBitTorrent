using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WinBitTorrent.Core.Abstractions;

namespace WinBitTorrent.Infrastructure.Updates;

/// <summary>
/// Checks the project's GitHub "releases" feed for a newer build and downloads its
/// installer, following the common self-updating desktop-app pattern.
/// </summary>
public sealed class GitHubUpdateService : IUpdateService, IDisposable
{
    private const string Owner = "Gorbachevvv";
    private const string Repository = "winBitTorrent";
    private static readonly Uri LatestReleaseEndpoint =
        new($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");

    private readonly HttpClient _client;
    private readonly ILogger<GitHubUpdateService> _logger;

    public GitHubUpdateService(ILogger<GitHubUpdateService> logger)
    {
        _logger = logger;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub's API rejects requests without a User-Agent.
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinBitTorrent", "1.0"));
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<UpdateRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _client.GetAsync(LatestReleaseEndpoint, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null; // Repository has no published releases yet.
            response.EnsureSuccessStatusCode();

            var payload = await response.Content
                .ReadFromJsonAsync<GitHubRelease>(cancellationToken)
                .ConfigureAwait(false);
            if (payload is null || payload.Draft || payload.Prerelease || string.IsNullOrWhiteSpace(payload.TagName))
                return null;

            if (!TryParseVersion(payload.TagName, out var version))
            {
                _logger.LogWarning("Could not parse a version from release tag '{Tag}'.", payload.TagName);
                return null;
            }

            var installer = payload.Assets?.FirstOrDefault(IsInstallerAsset);
            var releasePage = Uri.TryCreate(payload.HtmlUrl, UriKind.Absolute, out var page)
                ? page
                : new Uri($"https://github.com/{Owner}/{Repository}/releases");
            Uri? installerUrl = installer is not null && Uri.TryCreate(installer.DownloadUrl, UriKind.Absolute, out var asset)
                ? asset
                : null;

            return new UpdateRelease(
                version,
                string.IsNullOrWhiteSpace(payload.Name) ? $"v{version}" : payload.Name!,
                payload.Body?.Trim() ?? string.Empty,
                releasePage,
                installerUrl,
                installer?.Size ?? 0);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or NotSupportedException or System.Text.Json.JsonException)
        {
            _logger.LogInformation(exception, "Update check failed; skipping.");
            return null;
        }
    }

    public async Task<string> DownloadInstallerAsync(
        UpdateRelease release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (release.InstallerUrl is null)
            throw new InvalidOperationException("The release does not provide an installer asset to download.");

        var directory = Path.Combine(Path.GetTempPath(), "WinBitTorrent-update");
        Directory.CreateDirectory(directory);
        var fileName = Path.GetFileName(release.InstallerUrl.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"WinBitTorrent-{release.Version}-setup.exe";
        var targetPath = Path.Combine(directory, fileName);

        using var response = await _client
            .GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? release.InstallerSize;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (var destination = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                readTotal += read;
                if (total > 0)
                    progress?.Report(Math.Clamp((double)readTotal / total, 0d, 1d));
            }
        }

        progress?.Report(1d);
        return targetPath;
    }

    private static bool IsInstallerAsset(GitHubAsset asset)
        => asset.Name is not null
            && asset.Name.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseVersion(string tag, out Version version)
    {
        var trimmed = tag.Trim().TrimStart('v', 'V');
        // Keep only the leading numeric-dotted portion (e.g. "1.2.3" out of "1.2.3-beta").
        var numeric = new string(trimmed.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (Version.TryParse(numeric.Contains('.') ? numeric : $"{numeric}.0", out var parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    public void Dispose() => _client.Dispose();

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
