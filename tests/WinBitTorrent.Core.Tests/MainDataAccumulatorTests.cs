using System.Text.Json;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Core.Services;

namespace WinBitTorrent.Core.Tests;

public sealed class MainDataAccumulatorTests
{
    [Fact]
    public void AppliesFullAndIncrementalRidUpdates()
    {
        var accumulator = new MainDataAccumulator();
        var initial = accumulator.Apply(new MainDataResponse
        {
            ResponseId = 7,
            FullUpdate = true,
            Torrents = new()
            {
                ["a"] = new TorrentInfo { Name = "Alpha", Progress = .25 },
                ["b"] = new TorrentInfo { Name = "Beta", Progress = 1 }
            },
            Categories = new() { ["Linux"] = new TorrentCategory { Name = "Linux" } },
            Tags = ["iso"],
            ServerState = new ServerState { DownloadSpeed = 1024 }
        });

        Assert.True(initial.FullUpdate);
        Assert.Equal(7, accumulator.ResponseId);
        Assert.Equal("a", accumulator.Torrents["a"].Hash);
        Assert.Equal(2, accumulator.Torrents.Count);

        var delta = accumulator.Apply(new MainDataResponse
        {
            ResponseId = 8,
            Torrents = new() { ["a"] = new TorrentInfo { Name = "Alpha", Progress = .75 } },
            TorrentsRemoved = ["b"],
            TagsRemoved = ["iso"]
        });

        Assert.False(delta.FullUpdate);
        Assert.Equal(8, accumulator.ResponseId);
        Assert.Single(accumulator.Torrents);
        Assert.Equal(.75, accumulator.Torrents["a"].Progress);
        Assert.Equal(["b"], delta.RemovedHashes);
        Assert.Empty(accumulator.Tags);
    }

    [Fact]
    public void IncrementalTorrentPatchPreservesFieldsMissingFromDelta()
    {
        var accumulator = new MainDataAccumulator();
        var initial = JsonSerializer.Deserialize<MainDataResponse>(
            """
            {
              "rid": 1,
              "full_update": true,
              "torrents": {
                "abc": {
                  "name": "Ubuntu ISO",
                  "save_path": "C:\\Downloads\\",
                  "size": 1024,
                  "progress": 0.10,
                  "dlspeed": 0
                }
              }
            }
            """)!;
        var delta = JsonSerializer.Deserialize<MainDataResponse>(
            """
            {
              "rid": 2,
              "full_update": false,
              "torrents": {
                "abc": {
                  "progress": 0.42,
                  "dlspeed": 2048
                }
              }
            }
            """)!;

        accumulator.Apply(initial);
        var change = accumulator.Apply(delta);

        var torrent = accumulator.Torrents["abc"];
        Assert.Equal("Ubuntu ISO", torrent.Name);
        Assert.Equal("C:\\Downloads\\", torrent.SavePath);
        Assert.Equal(1024, torrent.Size);
        Assert.Equal(0.42, torrent.Progress);
        Assert.Equal(2048, torrent.DownloadSpeed);
        Assert.Same(torrent, change.ChangedTorrents.Single());
    }

    [Fact]
    public void IncrementalServerStatePatchPreservesFieldsMissingFromDelta()
    {
        // qBittorrent's sync/maindata sends server_state as a delta too, same as torrents - a
        // live session confirmed that queueing (and by the same logic use_alt_speed_limits) got
        // silently reset to false on the very next poll after being turned on, because the old
        // code replaced the whole ServerState object with whatever the delta contained instead
        // of patching just the fields the delta actually mentioned.
        var accumulator = new MainDataAccumulator();
        var initial = JsonSerializer.Deserialize<MainDataResponse>(
            """
            {
              "rid": 1,
              "full_update": true,
              "server_state": {
                "queueing": true,
                "use_alt_speed_limits": true,
                "dl_info_speed": 1024
              }
            }
            """)!;
        var delta = JsonSerializer.Deserialize<MainDataResponse>(
            """
            {
              "rid": 2,
              "full_update": false,
              "server_state": {
                "dl_info_speed": 4096
              }
            }
            """)!;

        accumulator.Apply(initial);
        var change = accumulator.Apply(delta);

        Assert.True(change.ServerState.Queueing);
        Assert.True(change.ServerState.UseAlternativeSpeedLimits);
        Assert.Equal(4096, change.ServerState.DownloadSpeed);
    }

    [Fact]
    public void IncrementalTorrentPatchAppliesSuperSeeding()
    {
        // super_seeding was missing from ApplyTorrentPatch's switch entirely - a delta-only
        // update that toggled it would parse fine but never actually reach the tracked row.
        var accumulator = new MainDataAccumulator();
        var initial = JsonSerializer.Deserialize<MainDataResponse>(
            """
            {
              "rid": 1,
              "full_update": true,
              "torrents": { "abc": { "name": "Ubuntu ISO", "super_seeding": false } }
            }
            """)!;
        var delta = JsonSerializer.Deserialize<MainDataResponse>(
            """
            {
              "rid": 2,
              "full_update": false,
              "torrents": { "abc": { "super_seeding": true } }
            }
            """)!;

        accumulator.Apply(initial);
        accumulator.Apply(delta);

        Assert.True(accumulator.Torrents["abc"].SuperSeeding);
    }

    [Fact]
    public void HandlesTenThousandRowsInOneBatch()
    {
        var torrents = Enumerable.Range(0, 10_000).ToDictionary(
            index => index.ToString("x40"),
            index => new TorrentInfo { Name = $"Torrent {index}", Progress = index / 10_000d });
        var accumulator = new MainDataAccumulator();

        var result = accumulator.Apply(new MainDataResponse { ResponseId = 1, FullUpdate = true, Torrents = torrents });

        Assert.Equal(10_000, result.ChangedTorrents.Count);
        Assert.Equal(10_000, accumulator.Torrents.Count);
    }
}
