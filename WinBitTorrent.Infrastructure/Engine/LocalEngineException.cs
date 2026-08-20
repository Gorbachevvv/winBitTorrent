using WinBitTorrent.Core.Abstractions;

namespace WinBitTorrent.Infrastructure.Engine;

public sealed class LocalEngineException : TorrentBackendException
{
    public LocalEngineException(string message, string? code = null, string? details = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Details = details;
    }

    public string? Code { get; }
    public string? Details { get; }
}
