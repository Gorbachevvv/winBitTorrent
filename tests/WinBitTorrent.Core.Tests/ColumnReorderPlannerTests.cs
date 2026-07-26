using WinBitTorrent.Core.Services;

namespace WinBitTorrent.Core.Tests;

public sealed class ColumnReorderPlannerTests
{
    // A column dragged next to another VISIBLE one has to slot in relative to that raw sequence,
    // sliding past whichever hidden columns happen to sit physically between the two spots -
    // exactly the case WinUI.TableView's own Move() gets wrong once anything is hidden.
    [Fact]
    public void DraggingPastAHiddenColumnLeavesTheHiddenColumnWhereItWas()
    {
        // Raw: Icon, Name, Size, TotalSize(hidden), Progress, Status. Visible: Icon, Name, Size, Progress, Status.
        var raw = new[] { "Icon", "Name", "Size", "TotalSize", "Progress", "Status" };
        var visibleWithoutMoved = new[] { "Icon", "Name", "Size", "Status" }; // Progress excluded (it's the one being moved)
        var rawWithoutMoved = raw.Where(c => c != "Progress").ToArray();

        // Drag "Progress" to visible index 1 (right after "Icon", before "Name").
        var insertAt = ColumnReorderPlanner.ComputeInsertIndex(rawWithoutMoved, visibleWithoutMoved, targetVisibleIndex: 1);

        var result = rawWithoutMoved.ToList();
        result.Insert(insertAt, "Progress");

        Assert.Equal(["Icon", "Progress", "Name", "Size", "TotalSize", "Status"], result);
        // TotalSize's position relative to Name/Size/Status is untouched by the move.
        Assert.Equal(
            new[] { "Icon", "Progress", "Name", "Size", "Status" },
            result.Where(c => c != "TotalSize"));
    }

    [Fact]
    public void DraggingToTheEndAppendsAfterEveryVisibleColumn()
    {
        var raw = new[] { "Icon", "Name", "Hidden", "Size" };
        var visibleWithoutMoved = new[] { "Icon", "Size" };
        var rawWithoutMoved = new[] { "Icon", "Hidden", "Size" };

        var insertAt = ColumnReorderPlanner.ComputeInsertIndex(rawWithoutMoved, visibleWithoutMoved, targetVisibleIndex: 2);

        Assert.Equal(rawWithoutMoved.Length, insertAt);
    }

    [Fact]
    public void DraggingToTheStartInsertsBeforeEveryVisibleColumn()
    {
        var rawWithoutMoved = new[] { "Icon", "Hidden", "Size" };
        var visibleWithoutMoved = new[] { "Icon", "Size" };

        var insertAt = ColumnReorderPlanner.ComputeInsertIndex(rawWithoutMoved, visibleWithoutMoved, targetVisibleIndex: 0);

        Assert.Equal(0, insertAt);
    }

    [Fact]
    public void OutOfRangeTargetIndexClampsInsteadOfThrowing()
    {
        var rawWithoutMoved = new[] { "A", "B" };
        var visibleWithoutMoved = new[] { "A", "B" };

        Assert.Equal(rawWithoutMoved.Length, ColumnReorderPlanner.ComputeInsertIndex(rawWithoutMoved, visibleWithoutMoved, targetVisibleIndex: 99));
        Assert.Equal(0, ColumnReorderPlanner.ComputeInsertIndex(rawWithoutMoved, visibleWithoutMoved, targetVisibleIndex: -5));
    }

    [Fact]
    public void MovingToItsOwnCurrentSpotLeavesTheSequenceUnchanged()
    {
        var raw = new[] { "Icon", "Name", "Size" };
        var visibleWithoutMoved = new[] { "Icon", "Size" };
        var rawWithoutMoved = new[] { "Icon", "Size" };

        // "Name" is already at visible index 1 (between Icon and Size).
        var insertAt = ColumnReorderPlanner.ComputeInsertIndex(rawWithoutMoved, visibleWithoutMoved, targetVisibleIndex: 1);
        var result = rawWithoutMoved.ToList();
        result.Insert(insertAt, "Name");

        Assert.Equal(raw, result);
    }
}
