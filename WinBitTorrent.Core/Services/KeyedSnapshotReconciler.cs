namespace WinBitTorrent.Core.Services;

/// <summary>
/// Applies a full keyed snapshot without replacing destination objects whose keys still exist.
/// This is useful for UI collections where object identity carries selection and scroll state.
/// </summary>
public static class KeyedSnapshotReconciler
{
    public static void Reconcile<TKey, TSource, TTarget>(
        IDictionary<TKey, TTarget> destination,
        IEnumerable<TSource> snapshot,
        Func<TSource, TKey> keySelector,
        Func<TSource, TTarget> create,
        Action<TTarget, TSource> update,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(update);

        var items = snapshot.Select(item => (Key: keySelector(item), Item: item)).ToArray();
        var snapshotKeys = items.Select(static item => item.Key).ToHashSet(comparer);
        foreach (var staleKey in destination.Keys.Where(key => !snapshotKeys.Contains(key)).ToArray())
            destination.Remove(staleKey);

        foreach (var (key, item) in items)
        {
            if (destination.TryGetValue(key, out var existing))
                update(existing, item);
            else
                destination[key] = create(item);
        }
    }
}
