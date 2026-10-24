using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Core.Abstractions;

public interface ICatalogProvider
{
    string Id { get; }
    string? ApiKey { get; set; }

    /// <summary>TMDB language tag (e.g. "ru-RU", "en-US", "be") applied to titles, posters and overviews.</summary>
    string? Language { get; set; }

    /// <summary>
    /// Secondary language used when <see cref="Language"/> has no translation (e.g. Belarusian falls
    /// back to Russian, Russian falls back to English). English is always tried last.
    /// </summary>
    string? FallbackLanguage { get; set; }

    /// <summary>ISO 3166-1 region (e.g. "RU", "US") used to keep regional sections relevant.</summary>
    string? Region { get; set; }

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
