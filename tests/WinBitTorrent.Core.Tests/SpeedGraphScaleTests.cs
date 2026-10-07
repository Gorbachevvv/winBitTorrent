using WinBitTorrent.Core.Services;

namespace WinBitTorrent.Core.Tests;

public sealed class SpeedGraphScaleTests
{
    [Theory]
    [InlineData(0L, 1024d)]
    [InlineData(16L * 1024, 32d * 1024)]
    [InlineData(16L * 1024 * 1024, 32d * 1024 * 1024)]
    [InlineData(20L * 1024 * 1024, 32d * 1024 * 1024)]
    [InlineData(100L * 1024 * 1024, 128d * 1024 * 1024)]
    [InlineData(512L * 1024 * 1024, 1024d * 1024 * 1024)]
    [InlineData(1024L * 1024 * 1024, 2048d * 1024 * 1024)]
    [InlineData(5L * 1024 * 1024 * 1024, 8d * 1024 * 1024 * 1024)]
    public void ScaleIncludesHeadroomAcrossBinaryUnitBoundaries(long speed, double expectedMaximum)
    {
        var maximum = SpeedGraphScale.GetMaximum(speed);
        Assert.Equal(expectedMaximum, maximum);
        Assert.True(maximum >= Math.Max(1024, speed * 1.08));
    }
}
