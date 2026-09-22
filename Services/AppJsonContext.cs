using System.Text.Json.Serialization;
using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Services;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ClientSettingsDocument))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<CatalogFavorite>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<TorrentColumnLayout>))]
[JsonSerializable(typeof(OnboardingDraft))]
[JsonSerializable(typeof(string))]
internal sealed partial class AppJsonContext : JsonSerializerContext;