using System.Net;
using Microsoft.Win32;

namespace WinBitTorrent.Infrastructure.Net;

// HttpClient's implicit default proxy resolution goes through WinHTTP (the machine-wide proxy),
// which is usually empty even when the interactive user has a proxy/VPN client configured through
// Internet Settings (the browser-level "ProxyServer" registry value). Read that value directly so
// requests follow the same route the user's browser and other apps already use.
internal static class SystemProxyResolver
{
    public static IWebProxy Create(Uri probeDestination)
    {
        using (var internetSettings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
        {
            var enabled = internetSettings?.GetValue("ProxyEnable") is int value && value != 0;
            var configured = internetSettings?.GetValue("ProxyServer") as string;
            var address = enabled ? ParseWindowsProxyAddress(configured) : null;
            if (address is not null)
            {
                return new WebProxy(address)
                {
                    BypassProxyOnLocal = false,
                    Credentials = CredentialCache.DefaultCredentials
                };
            }
        }

#pragma warning disable SYSLIB0014 // This is the Windows user proxy/PAC source; HttpClient's implicit proxy can use WinHTTP instead.
        var proxy = WebRequest.GetSystemWebProxy();
#pragma warning restore SYSLIB0014
        proxy.Credentials = CredentialCache.DefaultCredentials;
        if (!proxy.IsBypassed(probeDestination))
        {
            var address = proxy.GetProxy(probeDestination);
            if (address is not null && address != probeDestination)
            {
                return new WebProxy(address)
                {
                    BypassProxyOnLocal = false,
                    Credentials = CredentialCache.DefaultCredentials
                };
            }
        }
        return proxy;
    }

    private static Uri? ParseWindowsProxyAddress(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        var address = configured.Trim();
        if (address.Contains(';', StringComparison.Ordinal))
        {
            var entries = address.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static entry => entry.Split('=', 2, StringSplitOptions.TrimEntries))
                .Where(static entry => entry.Length == 2)
                .ToDictionary(static entry => entry[0], static entry => entry[1], StringComparer.OrdinalIgnoreCase);
            if (!entries.TryGetValue("https", out address) && !entries.TryGetValue("http", out address))
                return null;
        }

        if (!address.Contains("://", StringComparison.Ordinal))
            address = "http://" + address;
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri : null;
    }
}
