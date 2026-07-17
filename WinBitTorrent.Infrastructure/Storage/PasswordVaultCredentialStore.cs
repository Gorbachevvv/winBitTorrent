using System.Runtime.InteropServices;
using Windows.Security.Credentials;
using WinBitTorrent.Core.Abstractions;

namespace WinBitTorrent.Infrastructure.Storage;

public sealed class PasswordVaultCredentialStore : ICredentialStore
{
    private const string Resource = "WinBitTorrent.ServerProfiles";

    public Task<string?> GetSecretAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, profileId.ToString("D"));
            credential.RetrievePassword();
            return Task.FromResult<string?>(credential.Password);
        }
        catch (COMException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetSecretAsync(Guid profileId, string secret, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var vault = new PasswordVault();
        try
        {
            var existing = vault.Retrieve(Resource, profileId.ToString("D"));
            vault.Remove(existing);
        }
        catch (COMException)
        {
        }

        vault.Add(new PasswordCredential(Resource, profileId.ToString("D"), secret));
        return Task.CompletedTask;
    }

    public Task DeleteSecretAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var vault = new PasswordVault();
        try
        {
            vault.Remove(vault.Retrieve(Resource, profileId.ToString("D")));
        }
        catch (COMException)
        {
        }

        return Task.CompletedTask;
    }
}
