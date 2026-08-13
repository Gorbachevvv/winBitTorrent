namespace WinBitTorrent.ViewModels;

/// <summary>
/// A size-weighted slice used by the availability map. qBittorrent exposes availability per
/// file but not per piece through its Web API, so file boundaries are retained explicitly.
/// </summary>
public sealed record TorrentAvailabilitySegment(long Size, double Availability);
