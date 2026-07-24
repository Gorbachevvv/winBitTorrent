using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Nodes;
using WinBitTorrent.Core.Services;

namespace WinBitTorrent.ViewModels;

public sealed partial class PeerRowViewModel : ObservableObject
{
    private PeerRowViewModel(string id) => Id = id;

    public string Id { get; }

    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private string _countryName = string.Empty;
    [ObservableProperty] private string _countryFlagUri = string.Empty;
    [ObservableProperty] private string _client = string.Empty;
    [ObservableProperty] private string _connection = string.Empty;
    [ObservableProperty] private string _flags = string.Empty;
    [ObservableProperty] private string _progress = string.Empty;
    [ObservableProperty] private string _downloadSpeed = string.Empty;
    [ObservableProperty] private string _uploadSpeed = string.Empty;
    [ObservableProperty] private string _downloaded = string.Empty;
    [ObservableProperty] private string _uploaded = string.Empty;

    public static PeerRowViewModel FromJson(string id, JsonObject value)
    {
        var peer = new PeerRowViewModel(id);
        peer.Update(value);
        return peer;
    }

    public void Update(JsonObject value)
    {
        var ip = value["ip"]?.GetValue<string>() ?? Id;
        var port = value["port"]?.GetValue<int?>();
        var progress = value["progress"]?.GetValue<double?>() ?? 0;
        Address = port is > 0 ? $"{ip}:{port}" : ip;
        CountryName = value["country"]?.GetValue<string>() ?? string.Empty;
        // qBittorrent returns an ISO 3166-1 alpha-2 code (e.g. "RU"); map it to a bundled flag
        // image. An empty/unknown code leaves the flag blank rather than showing "N/A" text.
        var code = value["country_code"]?.GetValue<string>() ?? string.Empty;
        CountryFlagUri = code.Length == 2
            ? $"ms-appx:///Assets/Flags/{code.ToLowerInvariant()}.png"
            : string.Empty;
        Client = value["client"]?.GetValue<string>() ?? string.Empty;
        Connection = value["connection"]?.GetValue<string>() ?? string.Empty;
        Flags = value["flags"]?.GetValue<string>() ?? string.Empty;
        Progress = ValueFormatter.Percentage(progress);
        DownloadSpeed = ValueFormatter.Speed(value["dl_speed"]?.GetValue<long?>() ?? 0);
        UploadSpeed = ValueFormatter.Speed(value["up_speed"]?.GetValue<long?>() ?? 0);
        Downloaded = ValueFormatter.Size(value["downloaded"]?.GetValue<long?>() ?? 0);
        Uploaded = ValueFormatter.Size(value["uploaded"]?.GetValue<long?>() ?? 0);
    }
}
