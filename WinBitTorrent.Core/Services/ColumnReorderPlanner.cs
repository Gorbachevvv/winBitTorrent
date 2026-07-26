namespace WinBitTorrent.Core.Services;

public static class ColumnReorderPlanner
{
    /// <summary>
    /// Plans where a dragged column has to be reinserted into the raw (visible-and-hidden) column
    /// sequence so that, among the currently-visible columns, it lands at
    /// <paramref name="targetVisibleIndex"/> - without disturbing any other column's position
    /// relative to every other column, visible or hidden.
    /// </summary>
    /// <param name="rawWithoutMoved">The full column sequence with the dragged column already removed.</param>
    /// <param name="visibleWithoutMoved">The other currently-visible columns, in their current order.</param>
    /// <param name="targetVisibleIndex">Where the dragged column should land among <paramref name="visibleWithoutMoved"/>.</param>
    /// <returns>The index within <paramref name="rawWithoutMoved"/> to insert the dragged column at.</returns>
    public static int ComputeInsertIndex<T>(IReadOnlyList<T> rawWithoutMoved, IReadOnlyList<T> visibleWithoutMoved, int targetVisibleIndex)
    {
        var dropIndex = Math.Clamp(targetVisibleIndex, 0, visibleWithoutMoved.Count);
        if (dropIndex >= visibleWithoutMoved.Count)
            return rawWithoutMoved.Count;

        // The column that should immediately follow the dragged one after the move. Its raw index
        // (read from the sequence with the dragged column already gone) is exactly where the
        // dragged column belongs - no shift arithmetic needed, since removing the dragged column
        // never changed any OTHER column's relative position.
        var following = visibleWithoutMoved[dropIndex];
        for (var index = 0; index < rawWithoutMoved.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(rawWithoutMoved[index], following))
                return index;
        }
        return rawWithoutMoved.Count;
    }
}
