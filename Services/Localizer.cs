using Windows.ApplicationModel.Resources;

namespace WinBitTorrent.Services;

public static class Localizer
{
    private static ResourceLoader? _loader;

    public static string Get(string key, string fallback)
    {
        try
        {
            _loader ??= ResourceLoader.GetForViewIndependentUse();
            var value = _loader.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch
        {
            // Unit tests and unpackaged tooling may not have an MRT resource context.
            return fallback;
        }
    }
}
