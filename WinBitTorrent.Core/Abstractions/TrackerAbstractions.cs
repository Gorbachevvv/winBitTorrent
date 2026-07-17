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

public interface ITrackerProxyOptions
{
    bool UseBuiltInProxy { get; set; }
    string BuiltInProxyDescription { get; }
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
