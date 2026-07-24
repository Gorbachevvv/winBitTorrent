using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinBitTorrent.Core.Models;

[JsonConverter(typeof(TorrentInfoJsonConverter))]
public sealed class TorrentInfo
{
    [JsonIgnore]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("total_size")]
    public long TotalSize { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("num_seeds")]
    public int Seeds { get; set; }

    [JsonPropertyName("num_complete")]
    public int TotalSeeds { get; set; }

    [JsonPropertyName("num_leechs")]
    public int Peers { get; set; }

    [JsonPropertyName("num_incomplete")]
    public int TotalPeers { get; set; }

    [JsonPropertyName("dlspeed")]
    public long DownloadSpeed { get; set; }

    [JsonPropertyName("upspeed")]
    public long UploadSpeed { get; set; }

    [JsonPropertyName("eta")]
    public long Eta { get; set; }

    [JsonPropertyName("ratio")]
    public double Ratio { get; set; }

    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public string Tags { get; set; } = string.Empty;

    [JsonPropertyName("added_on")]
    public long AddedOn { get; set; }

    [JsonPropertyName("completion_on")]
    public long CompletedOn { get; set; }

    [JsonPropertyName("created_on")]
    public long CreatedOn { get; set; }

    [JsonPropertyName("tracker")]
    public string Tracker { get; set; } = string.Empty;

    [JsonPropertyName("dl_limit")]
    public long DownloadLimit { get; set; }

    [JsonPropertyName("up_limit")]
    public long UploadLimit { get; set; }

    [JsonPropertyName("downloaded")]
    public long Downloaded { get; set; }

    [JsonPropertyName("uploaded")]
    public long Uploaded { get; set; }

    [JsonPropertyName("downloaded_session")]
    public long DownloadedSession { get; set; }

    [JsonPropertyName("uploaded_session")]
    public long UploadedSession { get; set; }

    [JsonPropertyName("amount_left")]
    public long AmountLeft { get; set; }

    [JsonPropertyName("time_active")]
    public long TimeActive { get; set; }

    [JsonPropertyName("save_path")]
    public string SavePath { get; set; } = string.Empty;

    /// <summary>
    /// Root path of the torrent's actual content: the file itself for single-file
    /// torrents, or the folder containing its files for multi-file torrents. Unlike
    /// <see cref="SavePath"/>, this already accounts for any subfolder qBittorrent
    /// created and for whether the torrent is still using its incomplete-download path.
    /// </summary>
    [JsonPropertyName("content_path")]
    public string ContentPath { get; set; } = string.Empty;

    [JsonPropertyName("completed")]
    public long Completed { get; set; }

    [JsonPropertyName("ratio_limit")]
    public double RatioLimit { get; set; }

    [JsonPropertyName("seen_complete")]
    public long SeenComplete { get; set; }

    [JsonPropertyName("last_activity")]
    public long LastActivity { get; set; }

    [JsonPropertyName("availability")]
    public double Availability { get; set; }

    [JsonPropertyName("download_path")]
    public string DownloadPath { get; set; } = string.Empty;

    [JsonPropertyName("infohash_v1")]
    public string InfoHashV1 { get; set; } = string.Empty;

    [JsonPropertyName("infohash_v2")]
    public string InfoHashV2 { get; set; } = string.Empty;

    [JsonPropertyName("reannounce")]
    public long Reannounce { get; set; }

    [JsonPropertyName("private")]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("priority")]
    public int QueuePosition { get; set; }

    [JsonPropertyName("force_start")]
    public bool ForceStart { get; set; }

    [JsonPropertyName("seq_dl")]
    public bool SequentialDownload { get; set; }

    [JsonPropertyName("f_l_piece_prio")]
    public bool FirstLastPiecePriority { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    [JsonIgnore]
    public HashSet<string> PresentFields { get; } = new(StringComparer.Ordinal);
}

internal sealed class TorrentInfoJsonConverter : JsonConverter<TorrentInfo>
{
    public override TorrentInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected torrent object.");

        var torrent = new TorrentInfo();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return torrent;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected torrent property.");

            var propertyName = reader.GetString() ?? string.Empty;
            reader.Read();
            torrent.PresentFields.Add(propertyName);

            switch (propertyName)
            {
                case "hash": torrent.Hash = ReadString(ref reader); break;
                case "name": torrent.Name = ReadString(ref reader); break;
                case "size": torrent.Size = ReadInt64(ref reader); break;
                case "total_size": torrent.TotalSize = ReadInt64(ref reader); break;
                case "progress": torrent.Progress = ReadDouble(ref reader); break;
                case "state": torrent.State = ReadString(ref reader); break;
                case "num_seeds": torrent.Seeds = ReadInt32(ref reader); break;
                case "num_complete": torrent.TotalSeeds = ReadInt32(ref reader); break;
                case "num_leechs": torrent.Peers = ReadInt32(ref reader); break;
                case "num_incomplete": torrent.TotalPeers = ReadInt32(ref reader); break;
                case "dlspeed": torrent.DownloadSpeed = ReadInt64(ref reader); break;
                case "upspeed": torrent.UploadSpeed = ReadInt64(ref reader); break;
                case "eta": torrent.Eta = ReadInt64(ref reader); break;
                case "ratio": torrent.Ratio = ReadDouble(ref reader); break;
                case "popularity": torrent.Popularity = ReadDouble(ref reader); break;
                case "category": torrent.Category = ReadString(ref reader); break;
                case "tags": torrent.Tags = ReadString(ref reader); break;
                case "added_on": torrent.AddedOn = ReadInt64(ref reader); break;
                case "completion_on": torrent.CompletedOn = ReadInt64(ref reader); break;
                case "created_on": torrent.CreatedOn = ReadInt64(ref reader); break;
                case "tracker": torrent.Tracker = ReadString(ref reader); break;
                case "dl_limit": torrent.DownloadLimit = ReadInt64(ref reader); break;
                case "up_limit": torrent.UploadLimit = ReadInt64(ref reader); break;
                case "downloaded": torrent.Downloaded = ReadInt64(ref reader); break;
                case "uploaded": torrent.Uploaded = ReadInt64(ref reader); break;
                case "downloaded_session": torrent.DownloadedSession = ReadInt64(ref reader); break;
                case "uploaded_session": torrent.UploadedSession = ReadInt64(ref reader); break;
                case "amount_left": torrent.AmountLeft = ReadInt64(ref reader); break;
                case "time_active": torrent.TimeActive = ReadInt64(ref reader); break;
                case "save_path": torrent.SavePath = ReadString(ref reader); break;
                case "content_path": torrent.ContentPath = ReadString(ref reader); break;
                case "completed": torrent.Completed = ReadInt64(ref reader); break;
                case "ratio_limit": torrent.RatioLimit = ReadDouble(ref reader); break;
                case "seen_complete": torrent.SeenComplete = ReadInt64(ref reader); break;
                case "last_activity": torrent.LastActivity = ReadInt64(ref reader); break;
                case "availability": torrent.Availability = ReadDouble(ref reader); break;
                case "download_path": torrent.DownloadPath = ReadString(ref reader); break;
                case "infohash_v1": torrent.InfoHashV1 = ReadString(ref reader); break;
                case "infohash_v2": torrent.InfoHashV2 = ReadString(ref reader); break;
                case "reannounce": torrent.Reannounce = ReadInt64(ref reader); break;
                case "private": torrent.IsPrivate = ReadBoolean(ref reader); break;
                case "priority": torrent.QueuePosition = ReadInt32(ref reader); break;
                case "force_start": torrent.ForceStart = ReadBoolean(ref reader); break;
                case "seq_dl": torrent.SequentialDownload = ReadBoolean(ref reader); break;
                case "f_l_piece_prio": torrent.FirstLastPiecePriority = ReadBoolean(ref reader); break;
                default:
                    torrent.AdditionalData ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    torrent.AdditionalData[propertyName] = JsonElement.ParseValue(ref reader);
                    break;
            }
        }

        throw new JsonException("Unexpected end of torrent object.");
    }

    public override void Write(Utf8JsonWriter writer, TorrentInfo value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WriteNumber("size", value.Size);
        writer.WriteNumber("total_size", value.TotalSize);
        writer.WriteNumber("progress", value.Progress);
        writer.WriteString("state", value.State);
        writer.WriteNumber("num_seeds", value.Seeds);
        writer.WriteNumber("num_complete", value.TotalSeeds);
        writer.WriteNumber("num_leechs", value.Peers);
        writer.WriteNumber("num_incomplete", value.TotalPeers);
        writer.WriteNumber("dlspeed", value.DownloadSpeed);
        writer.WriteNumber("upspeed", value.UploadSpeed);
        writer.WriteNumber("eta", value.Eta);
        writer.WriteNumber("ratio", value.Ratio);
        writer.WriteNumber("popularity", value.Popularity);
        writer.WriteString("category", value.Category);
        writer.WriteString("tags", value.Tags);
        writer.WriteNumber("added_on", value.AddedOn);
        writer.WriteNumber("completion_on", value.CompletedOn);
        writer.WriteNumber("created_on", value.CreatedOn);
        writer.WriteString("tracker", value.Tracker);
        writer.WriteNumber("dl_limit", value.DownloadLimit);
        writer.WriteNumber("up_limit", value.UploadLimit);
        writer.WriteNumber("downloaded", value.Downloaded);
        writer.WriteNumber("uploaded", value.Uploaded);
        writer.WriteNumber("downloaded_session", value.DownloadedSession);
        writer.WriteNumber("uploaded_session", value.UploadedSession);
        writer.WriteNumber("amount_left", value.AmountLeft);
        writer.WriteNumber("time_active", value.TimeActive);
        writer.WriteString("save_path", value.SavePath);
        writer.WriteString("content_path", value.ContentPath);
        writer.WriteNumber("completed", value.Completed);
        writer.WriteNumber("ratio_limit", value.RatioLimit);
        writer.WriteNumber("seen_complete", value.SeenComplete);
        writer.WriteNumber("last_activity", value.LastActivity);
        writer.WriteNumber("availability", value.Availability);
        writer.WriteString("download_path", value.DownloadPath);
        writer.WriteString("infohash_v1", value.InfoHashV1);
        writer.WriteString("infohash_v2", value.InfoHashV2);
        writer.WriteNumber("reannounce", value.Reannounce);
        writer.WriteBoolean("private", value.IsPrivate);
        writer.WriteNumber("priority", value.QueuePosition);
        writer.WriteBoolean("force_start", value.ForceStart);
        writer.WriteBoolean("seq_dl", value.SequentialDownload);
        writer.WriteBoolean("f_l_piece_prio", value.FirstLastPiecePriority);

        if (value.AdditionalData is not null)
        {
            foreach (var (key, element) in value.AdditionalData)
            {
                writer.WritePropertyName(key);
                element.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static string ReadString(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.Null ? string.Empty : reader.GetString() ?? string.Empty;

    private static int ReadInt32(ref Utf8JsonReader reader)
        => checked((int)ReadInt64(ref reader));

    private static long ReadInt64(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(), out var value)
            ? value
            : reader.GetInt64();

    private static double ReadDouble(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.String && double.TryParse(reader.GetString(), out var value)
            ? value
            : reader.GetDouble();

    private static bool ReadBoolean(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.String && bool.TryParse(reader.GetString(), out var value)
            ? value
            : reader.GetBoolean();
}

public sealed class MainDataResponse
{
    [JsonPropertyName("rid")]
    public int ResponseId { get; set; }

    [JsonPropertyName("full_update")]
    public bool FullUpdate { get; set; }

    [JsonPropertyName("torrents")]
    public Dictionary<string, TorrentInfo>? Torrents { get; set; }

    [JsonPropertyName("torrents_removed")]
    public List<string>? TorrentsRemoved { get; set; }

    [JsonPropertyName("categories")]
    public Dictionary<string, TorrentCategory>? Categories { get; set; }

    [JsonPropertyName("categories_removed")]
    public List<string>? CategoriesRemoved { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("tags_removed")]
    public List<string>? TagsRemoved { get; set; }

    [JsonPropertyName("server_state")]
    public ServerState? ServerState { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class TorrentCategory
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("savePath")]
    public string SavePath { get; set; } = string.Empty;

    [JsonPropertyName("downloadPath")]
    public string DownloadPath { get; set; } = string.Empty;
}

public sealed class ServerState
{
    [JsonPropertyName("connection_status")]
    public string ConnectionStatus { get; set; } = "disconnected";

    [JsonPropertyName("dht_nodes")]
    public int DhtNodes { get; set; }

    [JsonPropertyName("dl_info_speed")]
    public long DownloadSpeed { get; set; }

    [JsonPropertyName("up_info_speed")]
    public long UploadSpeed { get; set; }

    [JsonPropertyName("dl_info_data")]
    public long DownloadedSession { get; set; }

    [JsonPropertyName("up_info_data")]
    public long UploadedSession { get; set; }

    [JsonPropertyName("alltime_dl")]
    public long DownloadedAllTime { get; set; }

    [JsonPropertyName("alltime_ul")]
    public long UploadedAllTime { get; set; }

    [JsonPropertyName("free_space_on_disk")]
    public long FreeSpaceOnDisk { get; set; }

    [JsonPropertyName("use_alt_speed_limits")]
    public bool UseAlternativeSpeedLimits { get; set; }

    [JsonPropertyName("queueing")]
    public bool Queueing { get; set; }

    [JsonPropertyName("refresh_interval")]
    public int RefreshInterval { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class TorrentProperties
{
    [JsonPropertyName("time_elapsed")]
    public long TimeElapsed { get; set; }

    [JsonPropertyName("eta")]
    public long Eta { get; set; }

    [JsonPropertyName("nb_connections")]
    public int Connections { get; set; }

    [JsonPropertyName("total_downloaded")]
    public long TotalDownloaded { get; set; }

    [JsonPropertyName("total_uploaded")]
    public long TotalUploaded { get; set; }

    [JsonPropertyName("total_wasted")]
    public long TotalWasted { get; set; }

    [JsonPropertyName("seeds")]
    public int Seeds { get; set; }

    [JsonPropertyName("peers")]
    public int Peers { get; set; }

    [JsonPropertyName("share_ratio")]
    public double ShareRatio { get; set; }

    [JsonPropertyName("piece_size")]
    public long PieceSize { get; set; }

    [JsonPropertyName("pieces_num")]
    public int Pieces { get; set; }

    [JsonPropertyName("comment")]
    public string Comment { get; set; } = string.Empty;

    [JsonPropertyName("created_by")]
    public string CreatedBy { get; set; } = string.Empty;

    [JsonPropertyName("addition_date")]
    public long AdditionDate { get; set; }

    [JsonPropertyName("completion_date")]
    public long CompletionDate { get; set; }

    [JsonPropertyName("save_path")]
    public string SavePath { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class TorrentTracker
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("tier")]
    public int Tier { get; set; }

    [JsonPropertyName("num_seeds")]
    public int Seeds { get; set; }

    [JsonPropertyName("num_leeches")]
    public int Leeches { get; set; }

    [JsonPropertyName("msg")]
    public string Message { get; set; } = string.Empty;
}

public sealed class TorrentFile
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("is_seed")]
    public bool IsSeed { get; set; }

    [JsonPropertyName("availability")]
    public double Availability { get; set; }
}

public sealed record TorrentAddRequest(
    IReadOnlyList<string> Urls,
    IReadOnlyList<string> TorrentFiles,
    string? SavePath = null,
    string? DownloadPath = null,
    string? Category = null,
    string? Tags = null,
    bool StartTorrent = true,
    bool SequentialDownload = false,
    bool FirstLastPiecePriority = false,
    bool AutomaticTorrentManagement = false,
    bool SkipChecking = false,
    int? UploadLimit = null,
    int? DownloadLimit = null);

public enum TorrentCommand
{
    Start,
    Stop,
    Recheck,
    Reannounce,
    IncreasePriority,
    DecreasePriority,
    TopPriority,
    BottomPriority,
    ToggleSequentialDownload,
    ToggleFirstLastPiecePriority
}
