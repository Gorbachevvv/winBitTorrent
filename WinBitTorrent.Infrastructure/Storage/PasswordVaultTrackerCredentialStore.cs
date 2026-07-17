using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Security.Credentials;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Infrastructure.Storage;

public sealed class PasswordVaultTrackerCredentialStore : ITrackerCredentialStore
{
    private const string Resource = "WinBitTorrent.TrackerAccounts";

    public Task<TrackerCredentials?> GetAsync(string trackerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, trackerId);
            credential.RetrievePassword();
            return Task.FromResult(JsonSerializer.Deserialize<TrackerCredentials>(credential.Password));
        }
        catch (COMException)
        {
            return Task.FromResult<TrackerCredentials?>(null);
        }
        catch (JsonException)
        {
            return Task.FromResult<TrackerCredentials?>(null);
        }
    }

    public Task SaveAsync(string trackerId, TrackerCredentials credentials, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var vault = new PasswordVault();
        Remove(vault, trackerId);
        vault.Add(new PasswordCredential(Resource, trackerId, JsonSerializer.Serialize(credentials)));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string trackerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Remove(new PasswordVault(), trackerId);
        return Task.CompletedTask;
    }

    private static void Remove(PasswordVault vault, string trackerId)
    {
        try
        {
            vault.Remove(vault.Retrieve(Resource, trackerId));
        }
        catch (COMException)
        {
        }
    }
}
