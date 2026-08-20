using WinBitTorrent.Core.Services;

namespace WinBitTorrent.Core.Tests;

public sealed class KeyedSnapshotReconcilerTests
{
    [Fact]
    public void FullSnapshotUpdatesExistingObjectsWithoutReplacingTheirIdentity()
    {
        var selected = new Row("kept", "old");
        var removed = new Row("removed", "old");
        var rows = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase)
        {
            [selected.Key] = selected,
            [removed.Key] = removed
        };

        KeyedSnapshotReconciler.Reconcile(
            rows,
            [new RowData("KEPT", "updated"), new RowData("added", "new")],
            static item => item.Key,
            static item => new Row(item.Key, item.Value),
            static (row, item) => row.Value = item.Value,
            StringComparer.OrdinalIgnoreCase);

        Assert.Same(selected, rows["kept"]);
        Assert.Equal("updated", selected.Value);
        Assert.DoesNotContain("removed", rows.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("new", rows["added"].Value);
    }

    private sealed record RowData(string Key, string Value);

    private sealed class Row(string key, string value)
    {
        public string Key { get; } = key;
        public string Value { get; set; } = value;
    }
}
