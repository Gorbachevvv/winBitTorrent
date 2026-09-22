using System.Text.Json.Serialization;
using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Infrastructure;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TrackerCredentials))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<TorrentFile>))]
[JsonSerializable(typeof(List<TorrentInfo>))]
[JsonSerializable(typeof(List<TorrentTracker>))]
[JsonSerializable(typeof(MainDataResponse))]
[JsonSerializable(typeof(ServerProfile))]
[JsonSerializable(typeof(TorrentAddRequest))]
[JsonSerializable(typeof(TorrentProperties))]
internal sealed partial class InfrastructureJsonContext : JsonSerializerContext;