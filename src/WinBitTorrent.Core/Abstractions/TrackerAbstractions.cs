using System.Net;
using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Core.Abstractions;

public interface ITrackerCredentialStore
{
    Task<TrackerCredentials?> GetAsync(string trackerId, CancellationToken cancellationToken = default);
    Task SaveAsync(string trackerId, TrackerCredentials credentials, CancellationToken cancellationToken = default);
    Task DeleteAsync(string trackerId, CancellationToken cancellationToken = default);
}

public interface ITrackerSearchProvider
{
    string Id { get; }
    string DisplayName { get; }
    Uri HomePage { get; }

    Task SignInAsync(TrackerCredentials credentials, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrackerSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<byte[]> DownloadTorrentAsync(string resultId, CancellationToken cancellationToken = default);
}

// Trackers that answer searches without an account (public JSON APIs). The search UI skips the
// sign-in step for these providers and never offers a "sign out" action.
public interface ITrackerAnonymousAccess
{
}

public interface ITrackerProxyOptions
{
    bool UseBuiltInProxy { get; set; }
    string BuiltInProxyDescription { get; }

    /// <summary>
    /// Address of the built-in proxy, so the embedded sign-in browser can be routed through the same
    /// hop the provider's own requests use when the tracker is blocked by the ISP.
    /// </summary>
    Uri BuiltInProxyAddress { get; }
}

public interface ITrackerInteractiveAuthentication
{
    Uri LoginPage { get; }
    Task ImportSessionCookiesAsync(IReadOnlyCollection<Cookie> cookies, CancellationToken cancellationToken = default);
}

public interface ITrackerSessionControl
{
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

public sealed class TrackerAuthenticationException : Exception
{
    public TrackerAuthenticationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
