using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Infrastructure.Api;
using WinBitTorrent.Infrastructure.Backend;

namespace WinBitTorrent.Infrastructure.Connection;

public sealed class ConnectionCoordinator : IConnectionCoordinator
{
    private readonly IManagedBackendHost _backendHost;
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<ConnectionCoordinator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConnectionCoordinator(
        IManagedBackendHost backendHost,
        ICredentialStore credentialStore,
        ILogger<ConnectionCoordinator> logger)
    {
        _backendHost = backendHost;
        _credentialStore = credentialStore;
        _logger = logger;
        _backendHost.Failed += OnBackendFailed;
    }

    public ConnectionSnapshot Snapshot { get; private set; } = new(ConnectionState.Disconnected, null, null);
    public IQBittorrentApi? Api { get; private set; }
    public event EventHandler<ConnectionSnapshot>? StateChanged;

    public async Task<IQBittorrentApi> ConnectAsync(ServerProfile profile, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync(cancellationToken).ConfigureAwait(false);
            SetState(profile.Kind == ProfileKind.LocalManaged ? ConnectionState.StartingBackend : ConnectionState.Connecting, profile);

            BackendSession? session = null;
            var effectiveProfile = profile;
            if (profile.Kind == ProfileKind.LocalManaged)
            {
                session = await _backendHost.StartAsync(cancellationToken).ConfigureAwait(false);
                effectiveProfile = profile with { BaseAddress = session.BaseAddress };
            }

            SetState(ConnectionState.Authenticating, effectiveProfile, session);
            var secret = await _credentialStore.GetSecretAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            var api = CreateApi(effectiveProfile, secret);

            if (profile.Authentication == AuthenticationMode.UserNamePassword)
            {
                if (string.IsNullOrWhiteSpace(profile.UserName) || string.IsNullOrEmpty(secret))
                    throw new InvalidOperationException("The remote profile does not contain a user name and password.");
                await api.Auth.LoginAsync(profile.UserName, secret, cancellationToken).ConfigureAwait(false);
            }

            var version = (await api.Application.GetVersionAsync(cancellationToken).ConfigureAwait(false)).Trim();
            var webApiVersion = (await api.Application.GetWebApiVersionAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (!version.Equals(ManagedBackendHost.RequiredQbittorrentVersion, StringComparison.OrdinalIgnoreCase)
                || !webApiVersion.Equals(ManagedBackendHost.RequiredWebApiVersion, StringComparison.OrdinalIgnoreCase))
            {
                await api.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"This profile exposes {version}/API {webApiVersion}; WinBitTorrent requires " +
                    $"{ManagedBackendHost.RequiredQbittorrentVersion}/API {ManagedBackendHost.RequiredWebApiVersion}.");
            }

            Api = api;
            SetState(ConnectionState.Connected, effectiveProfile, session);
            _logger.LogInformation("Connected to qBittorrent profile {ProfileName} at {Address}.", profile.Name, effectiveProfile.BaseAddress);
            return api;
        }
        catch (Exception exception)
        {
            if (profile.Kind == ProfileKind.LocalManaged && _backendHost.IsRunning)
            {
                try { await _backendHost.StopAsync(force: false, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception stopException) { _logger.LogWarning(stopException, "Unable to stop backend after a failed connection."); }
            }
            SetState(ConnectionState.Faulted, profile, _backendHost.Session, exception.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        if (Api is null)
        {
            if (_backendHost.IsRunning)
                await _backendHost.StopAsync(force: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        var stopManagedBackend = Api.Profile.Kind == ProfileKind.LocalManaged;

        try
        {
            if (Api.Profile.Authentication == AuthenticationMode.UserNamePassword)
                await Api.Auth.LogoutAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or QbittorrentApiException)
        {
            _logger.LogDebug(exception, "Logout failed while disconnecting a qBittorrent profile.");
        }

        await Api.DisposeAsync().ConfigureAwait(false);
        Api = null;
        if (stopManagedBackend && _backendHost.IsRunning)
            await _backendHost.StopAsync(force: false, cancellationToken).ConfigureAwait(false);
        SetState(ConnectionState.Disconnected, null);
    }

    private static QbittorrentApi CreateApi(ServerProfile profile, string? secret)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors)
                => ValidateCertificate(profile, certificate, errors)
        };

        var apiKey = profile.Authentication is AuthenticationMode.ApiKey or AuthenticationMode.LocalApiKey ? secret : null;
        return QbittorrentApi.Create(profile, apiKey, handler);
    }

    private static bool ValidateCertificate(ServerProfile profile, X509Certificate2? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
            return true;
        if (certificate is null || string.IsNullOrWhiteSpace(profile.TrustedCertificateSha256))
            return false;

        var actual = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        return actual.Equals(profile.TrustedCertificateSha256.Replace(":", string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    private void SetState(ConnectionState state, ServerProfile? profile, BackendSession? backend = null, string? error = null)
    {
        Snapshot = new ConnectionSnapshot(state, profile, backend, error);
        StateChanged?.Invoke(this, Snapshot);
    }

    private void OnBackendFailed(object? sender, Exception exception)
        => SetState(ConnectionState.Reconnecting, Snapshot.Profile, _backendHost.Session, exception.Message);

    public async ValueTask DisposeAsync()
    {
        _backendHost.Failed -= OnBackendFailed;
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
