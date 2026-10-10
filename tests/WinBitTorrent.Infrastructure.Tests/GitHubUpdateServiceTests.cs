using System.Runtime.InteropServices;
using WinBitTorrent.Infrastructure.Updates;

namespace WinBitTorrent.Infrastructure.Tests;

public sealed class GitHubUpdateServiceTests
{
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
}
