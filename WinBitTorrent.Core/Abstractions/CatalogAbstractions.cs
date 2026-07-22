using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Core.Abstractions;

public interface ICatalogProvider
{
    string Id { get; }
    string? ApiKey { get; set; }
    bool IsConfigured { get; }

    Task<IReadOnlyList<CatalogItem>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CatalogItem>> GetSectionAsync(CatalogSection section, int page = 1, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CatalogItem>> GetSimilarAsync(string id, CatalogKind kind, int page = 1, CancellationToken cancellationToken = default);
    Task<CatalogItemDetails> GetDetailsAsync(string id, CatalogKind kind, CancellationToken cancellationToken = default);
}

public sealed class CatalogNotConfiguredException : Exception
{
    public CatalogNotConfiguredException(string message) : base(message)
    {
    }
}

public sealed class CatalogException : Exception
{
    public CatalogException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
