using System.Text.Json;
using WinBitTorrent.Core.Models;

namespace WinBitTorrent.Services;

/// <summary>A single bookmarked catalog title, persisted in client settings.</summary>
public sealed record CatalogFavorite(
    string Id,
    CatalogKind Kind,
    string Title,
    string? Year,
    string? PosterUrl,
    string RatingText);

/// <summary>
/// Local, account-free bookmarks for the movie/TV catalog. Stored as a JSON list in
/// <see cref="ClientSettings"/> so favorites survive restarts without any TMDB login.
/// </summary>
public static class CatalogFavoritesStore
{
    private const string Key = "catalog.favorites";

    public static IReadOnlyList<CatalogFavorite> Load()
    {
        var json = ClientSettings.Get<string>(Key);
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<CatalogFavorite>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static bool Contains(string id, CatalogKind kind)
        => Load().Any(favorite => favorite.Kind == kind && string.Equals(favorite.Id, id, StringComparison.Ordinal));

    /// <summary>Adds the title if absent, removes it if present. Returns the new favorite state.</summary>
    public static bool Toggle(CatalogFavorite favorite)
    {
        var list = Load().ToList();
        var index = list.FindIndex(existing => existing.Kind == favorite.Kind && string.Equals(existing.Id, favorite.Id, StringComparison.Ordinal));
        bool added;
        if (index >= 0)
        {
            list.RemoveAt(index);
            added = false;
        }
        else
        {
            list.Insert(0, favorite);
            added = true;
        }

        ClientSettings.SetValue(Key, JsonSerializer.Serialize(list));
        return added;
    }
}
