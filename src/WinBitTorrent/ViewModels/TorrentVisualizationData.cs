namespace WinBitTorrent.ViewModels;

/// <summary>
/// A size-weighted slice used by the availability map. Local libtorrent profiles provide one
/// slice per piece; remote qBittorrent profiles fall back to the per-file availability API.
/// </summary>
public sealed record TorrentAvailabilitySegment(long Size, double Availability);
