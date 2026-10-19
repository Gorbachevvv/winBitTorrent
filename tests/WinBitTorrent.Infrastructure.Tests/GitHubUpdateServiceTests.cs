using System.Runtime.InteropServices;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WinBitTorrent.Infrastructure.Updates;

namespace WinBitTorrent.Infrastructure.Tests;

public sealed class GitHubUpdateServiceTests
{
    [Theory]
    [InlineData(Architecture.Arm64, "setup-arm64.exe")]
    [InlineData(Architecture.X64, "setup.exe")]
    public async Task ChecksAndDownloadsTheMatchingReleaseAsset(Architecture architecture, string suffix)
    {
        // A unique version keeps the fixture away from real pending update downloads.
        var version = $"2.1.{Random.Shared.Next(100000, int.MaxValue)}";
        var expected = $"WinBitTorrent-{version}-{suffix}";
        using var handler = new ReleaseHandler(includeArm64: true, version);
        using var service = new GitHubUpdateService(new HttpClient(handler), architecture, NullLogger<GitHubUpdateService>.Instance);
        var release = await service.GetLatestReleaseAsync();
        Assert.NotNull(release);
        Assert.Equal(Version.Parse(version), release.Version);
        Assert.Equal($"https://example.test/{expected}", release.InstallerUrl?.AbsoluteUri);
        var path = await service.DownloadInstallerAsync(release);
        try
        {
            Assert.Equal(expected, Path.GetFileName(path));
            Assert.Equal(new byte[] { 0x4D, 0x5A, 1, 2 }, await File.ReadAllBytesAsync(path));
            Assert.Equal(release.InstallerUrl, handler.DownloadedUrl);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MissingArm64AssetProvidesTheReleasePageWithoutDownloadingX64()
    {
        using var handler = new ReleaseHandler(includeArm64: false);
        using var service = new GitHubUpdateService(new HttpClient(handler), Architecture.Arm64, NullLogger<GitHubUpdateService>.Instance);
        var release = await service.GetLatestReleaseAsync();
        Assert.NotNull(release);
        Assert.Null(release.InstallerUrl);
        Assert.Equal(0, release.InstallerSize);
        Assert.Equal("https://example.test/releases/v2.1.0", release.ReleasePageUrl.AbsoluteUri);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadInstallerAsync(release));
        Assert.Null(handler.DownloadedUrl);
    }

    [Theory]
    [InlineData(Architecture.Arm64, "WinBitTorrent-2.0.0-setup-arm64.exe")]
    [InlineData(Architecture.X64, "WinBitTorrent-2.0.0-setup.exe")]
    public void SelectsInstallerForTheRunningProcess(Architecture architecture, string expected)
    {
        string?[] assets = [null, "WinBitTorrent-2.0.0-setup-arm64.exe", "WinBitTorrent-2.0.0-setup.exe"];
        Assert.Equal(expected, GitHubUpdateService.SelectInstallerName(assets, architecture));
    }

    [Fact]
    public void Arm64DoesNotFallBackToAnX64Installer()
    {
        string?[] assets = ["WinBitTorrent-2.0.0-setup.exe"];
        Assert.Null(GitHubUpdateService.SelectInstallerName(assets, Architecture.Arm64));
    }

    [Fact]
    public void X64CanStillUpdateFromLegacyReleases()
    {
        string?[] assets = ["WinBitTorrent-2.0.0-setup-arm64.exe", "WinBitTorrent-2.0.0-setup.exe"];
        Assert.Equal("WinBitTorrent-2.0.0-setup.exe", GitHubUpdateService.SelectInstallerName(assets, Architecture.X64));
    }

    [Fact]
    public void LegacyClientIgnoresAnArm64InstallerListedFirst()
    {
        string?[] assets = ["WinBitTorrent-2.0.0-setup-arm64.exe", "WinBitTorrent-2.0.0-setup.exe"];
        // Match the selector shipped before architecture-aware updates existed.
        var legacySelection = assets.FirstOrDefault(name => name is not null && name.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("WinBitTorrent-2.0.0-setup.exe", legacySelection);
        Assert.Equal("WinBitTorrent-2.0.0-setup-arm64.exe", GitHubUpdateService.SelectInstallerName(assets, Architecture.Arm64));
    }

    [Fact]
    public void X64DoesNotSelectAnArm64InstallerWhenNoX64AssetExists()
    {
        string?[] assets = ["WinBitTorrent-2.0.0-setup-arm64.exe"];
        Assert.Null(GitHubUpdateService.SelectInstallerName(assets, Architecture.X64));
    }

    [Theory]
    [InlineData("OtherApp-2.0.0-setup.exe")]
    [InlineData("WinBitTorrent-2.0.0-setup-arm64.exe.zip")]
    [InlineData("WinBitTorrent-2.0.0-arm64-setup.exe")]
    public void DoesNotMistakeOtherAssetsForAnX64Installer(string name)
        => Assert.Null(GitHubUpdateService.SelectInstallerName([name], Architecture.X64));

    private sealed class ReleaseHandler(bool includeArm64, string version = "2.1.0") : HttpMessageHandler
    {
        public Uri? DownloadedUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                var names = new List<string> { $"WinBitTorrent-{version}-setup.exe" };
                if (includeArm64) names.Insert(0, $"WinBitTorrent-{version}-setup-arm64.exe");
                var payload = JsonSerializer.Serialize(new
                {
                    tag_name = $"v{version}", name = $"Version {version}", html_url = $"https://example.test/releases/v{version}",
                    assets = names.Select(name => new { name, browser_download_url = $"https://example.test/{name}", size = 4 })
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            }
            DownloadedUrl = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x4D, 0x5A, 1, 2])
            });
        }
    }
}
