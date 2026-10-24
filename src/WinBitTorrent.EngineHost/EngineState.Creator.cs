using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinBitTorrent.EngineHost;

internal sealed partial class EngineState
{
    private readonly ConcurrentDictionary<string, CreatorJob> _creatorJobs = new(StringComparer.Ordinal);

    private JsonElement StartCreatorTask(JsonElement payload)
    {
        var sourcePath = payload.TryGetProperty("sourcePath", out var source) ? source.GetString() : null;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            throw new ArgumentException("The torrent creator source does not exist.");
        var id = Guid.NewGuid().ToString("N");
        var job = new CreatorJob(id, payload.Clone());
        if (!_creatorJobs.TryAdd(id, job)) throw new InvalidOperationException("Unable to allocate a torrent creator task.");
        job.Work = RunCreatorTaskAsync(job);
        PruneCreatorJobs();
        return EngineJson.Element(new JsonObject { ["taskID"] = id });
    }

    private JsonElement GetCreatorStatus(JsonElement payload)
    {
        var job = RequireCreatorJob(payload);
        return EngineJson.Element(new JsonObject
        {
            ["taskID"] = job.Id,
            ["status"] = job.Status,
            ["progress"] = job.Progress,
            ["error"] = job.Error ?? string.Empty
        });
    }

    private JsonElement GetCreatorFile(JsonElement payload)
    {
        var job = RequireCreatorJob(payload);
        if (job.Status == "Failed") throw new InvalidOperationException(job.Error ?? "Torrent creation failed.");
        if (job.Status != "Finished" || job.Data is null) throw new InvalidOperationException("The torrent creator task has not finished.");
        return EngineJson.Element(job.Data);
    }

    private JsonElement DeleteCreatorTask(JsonElement payload)
    {
        var job = RequireCreatorJob(payload);
        job.Lifetime.Cancel();
        if (job.Work?.IsCompleted == true && _creatorJobs.TryRemove(job.Id, out var removed)) removed.Lifetime.Dispose();
        return EngineJson.EmptyObject;
    }

    private CreatorJob RequireCreatorJob(JsonElement payload)
    {
        var id = payload.TryGetProperty("taskId", out var taskId) ? taskId.GetString()
            : payload.TryGetProperty("taskID", out var alternate) ? alternate.GetString() : null;
        if (id is null || !_creatorJobs.TryGetValue(id, out var job)) throw new ArgumentException($"Torrent creator task '{id}' does not exist.");
        return job;
    }

    private async Task RunCreatorTaskAsync(CreatorJob job)
    {
        try
        {
            job.Status = "Running";
            var data = await Task.Run(() => NativeEngine.CreateTorrent(job.Request, (completed, total) =>
            {
                job.Progress = total <= 0 ? 0 : Math.Clamp((double)completed / total, 0, 1);
                return !job.Lifetime.IsCancellationRequested;
            }), job.Lifetime.Token).ConfigureAwait(false);
            job.Lifetime.Token.ThrowIfCancellationRequested();
            job.Data = data;
            job.Progress = 1;

            if (job.Request.TryGetProperty("startSeeding", out var seed) && seed.GetBoolean())
                await StartSeedingCreatedTorrentAsync(job, job.Lifetime.Token).ConfigureAwait(false);
            job.Status = "Finished";
            await AppendLogAsync(2, $"Torrent creator task {job.Id} finished.", CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job.Status = "Failed";
            job.Error = "Torrent creation was cancelled.";
        }
        catch (Exception exception)
        {
            job.Status = "Failed";
            job.Error = exception.Message;
            await AppendLogAsync(4, $"Torrent creator task {job.Id} failed: {exception.Message}", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            job.CompletedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task StartSeedingCreatedTorrentAsync(CreatorJob job, CancellationToken cancellationToken)
    {
        var sourcePath = job.Request.GetProperty("sourcePath").GetString()!;
        var savePath = Directory.Exists(sourcePath) ? Directory.GetParent(sourcePath)?.FullName : Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(savePath) || job.Data is null) return;
        var staging = Path.Combine(_dataRoot, "staging");
        Directory.CreateDirectory(staging);
        var torrentPath = Path.Combine(staging, $"creator-{job.Id}.torrent");
        try
        {
            await File.WriteAllBytesAsync(torrentPath, job.Data, cancellationToken).ConfigureAwait(false);
            var request = new JsonObject
            {
                ["torrentFiles"] = new JsonArray(torrentPath),
                ["urls"] = new JsonArray(),
                ["savePath"] = savePath,
                ["startTorrent"] = true,
                ["skipChecking"] = true
            };
            await AddTorrentsAsync(EngineJson.Element(request), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(torrentPath); } catch (IOException) { }
        }
    }

    private void PruneCreatorJobs()
    {
        foreach (var job in _creatorJobs.Values.Where(static value => value.CompletedAt < DateTimeOffset.UtcNow.AddHours(-1)).ToArray())
        {
            if (_creatorJobs.TryRemove(job.Id, out var removed)) removed.Lifetime.Dispose();
        }
    }

    private async Task StopCreatorServicesAsync()
    {
        foreach (var job in _creatorJobs.Values) job.Lifetime.Cancel();
        var work = _creatorJobs.Values.Select(static value => value.Work).Where(static value => value is not null).Cast<Task>().ToArray();
        try { await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
        foreach (var job in _creatorJobs.Values) job.Lifetime.Dispose();
    }

    private sealed class CreatorJob(string id, JsonElement request)
    {
        public string Id { get; } = id;
        public JsonElement Request { get; } = request;
        public string Status { get; set; } = "Queued";
        public double Progress;
        public string? Error { get; set; }
        public byte[]? Data { get; set; }
        public CancellationTokenSource Lifetime { get; } = new();
        public Task? Work { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
    }
}
