namespace WinBitTorrent.Core.Services;

public static class SpeedGraphScale
{
    public static double GetMaximum(long largestSpeed)
    {
        // Powers of two keep binary-unit ticks readable without capping each unit at 16.
        var target = Math.Max(1024, largestSpeed * 1.08);
        return Math.Pow(2, Math.Ceiling(Math.Log2(target)));
    }
}
