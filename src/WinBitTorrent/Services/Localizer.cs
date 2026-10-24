using Microsoft.Windows.ApplicationModel.Resources;

namespace WinBitTorrent.Services;

public static class Localizer
{
    private static ResourceLoader? _loader;

    public static string Get(string key, string fallback)
    {
        try
        {
            _loader ??= new ResourceLoader();
            var value = _loader.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch
        {
            // Unit tests and hosts without a WindowsAppRuntime bootstrap have no resource context.
            return fallback;
        }
    }
}
