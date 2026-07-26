using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Core.Services;

public static class TrackerQueryBuilder
{
    /// <summary>
    /// Turns a catalog title into the text a tracker is searched with.
    /// </summary>
    /// <param name="localizedSeasonFormat">
    /// UI-language "season {0}" pattern, used only when the title itself is not the English one -
    /// a mixed "Bleach сезон 1" matches nothing on a tracker indexing English release names.
    /// </param>
    public static string Build(CatalogTrackerQuery query, string localizedSeasonFormat)
    {
        if (query.Season is not { } seasonNumber)
            return query.Year is null ? query.Title : $"{query.Title} {query.Year}";

        var format = query.IsEnglishTitle ? "season {0}" : localizedSeasonFormat;
        return $"{query.Title} {string.Format(format, seasonNumber)}";
    }
}
