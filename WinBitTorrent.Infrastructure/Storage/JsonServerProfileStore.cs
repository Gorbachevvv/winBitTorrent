using System.Text.Json;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Infrastructure.Storage;

public sealed class JsonServerProfileStore : IServerProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<ServerProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return document.Profiles;
    }

    public async Task<ServerProfile?> GetSelectedAsync(CancellationToken cancellationToken = default)
    {
        var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return document.Profiles.FirstOrDefault(profile => profile.Id == document.SelectedProfileId)
            ?? document.Profiles.FirstOrDefault(profile => profile.IsBuiltIn)
            ?? document.Profiles.FirstOrDefault();
    }

    public async Task SaveAsync(ServerProfile profile, CancellationToken cancellationToken = default)
    {
        await MutateAsync(document =>
        {
            var index = document.Profiles.FindIndex(item => item.Id == profile.Id);
            if (index >= 0)
                document.Profiles[index] = profile;
            else
                document.Profiles.Add(profile);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task SelectAsync(Guid id, CancellationToken cancellationToken = default)
        => MutateAsync(document =>
        {
            if (document.Profiles.All(profile => profile.Id != id))
                throw new InvalidOperationException("The selected server profile does not exist.");
            document.SelectedProfileId = id;
        }, cancellationToken);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => MutateAsync(document =>
        {
            var profile = document.Profiles.FirstOrDefault(item => item.Id == id);
            if (profile?.IsBuiltIn == true)
                throw new InvalidOperationException("The managed local profile cannot be deleted.");
            document.Profiles.RemoveAll(item => item.Id == id);
            if (document.SelectedProfileId == id)
                document.SelectedProfileId = ServerProfile.LocalProfileId;
        }, cancellationToken);

    private async Task MutateAsync(Action<ProfileDocument> mutation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            mutation(document);
            await WriteCoreAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ProfileDocument> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<ProfileDocument> ReadCoreAsync(CancellationToken cancellationToken)
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(AppPaths.ProfilesFile))
        {
            var created = ProfileDocument.CreateDefault();
            await WriteCoreAsync(created, cancellationToken).ConfigureAwait(false);
            return created;
        }

        await using var stream = File.OpenRead(AppPaths.ProfilesFile);
        var document = await JsonSerializer.DeserializeAsync<ProfileDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? ProfileDocument.CreateDefault();

        if (document.Profiles.All(profile => profile.Id != ServerProfile.LocalProfileId))
            document.Profiles.Insert(0, ServerProfile.CreateLocal(new Uri("http://127.0.0.1:8080/")));
        return document;
    }

    private static async Task WriteCoreAsync(ProfileDocument document, CancellationToken cancellationToken)
    {
        AppPaths.EnsureCreated();
        var temporary = AppPaths.ProfilesFile + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, AppPaths.ProfilesFile, true);
    }

    private sealed class ProfileDocument
    {
        public Guid SelectedProfileId { get; set; } = ServerProfile.LocalProfileId;
        public List<ServerProfile> Profiles { get; set; } = [];

        public static ProfileDocument CreateDefault() => new()
        {
            Profiles = [ServerProfile.CreateLocal(new Uri("http://127.0.0.1:8080/"))]
        };
    }
}
