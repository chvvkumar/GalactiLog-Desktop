using System.Collections.Concurrent;
using System.Reflection;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.CustomColumnTestFactory;

namespace GalactiLog.App.Tests.ViewModels.CustomColumns;

// Phase 20 ruling C20. Cell lifetime has one owner: a surface holds a group, not a bare list, so
// the flush before a page closes and the dispose on a row recycle are owed in one place rather than
// at every surface that draws a cell.
public class CustomCellGroupTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public void Empty_HoldsNothing()
    {
        // The group a surface uses where it has no custom columns, so no caller writes a null check
        // or builds an empty list of its own.
        Assert.Empty(CustomCellGroup.Empty.Cells);
        Assert.True(CustomCellGroup.Empty.FlushAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public void DisposingEmpty_IsANoOp()
    {
        // Phase review target P2-1. Empty is a process-wide instance, so one surface disposing it
        // marks it disposed for every other surface in the process. The rule was written out at five
        // call sites, remembered at three and forgotten at two, so it is enforced in the type: the
        // proof is that the instance is still usable as a group after a dispose that a careless
        // caller performed. Red against a Dispose with no identity check, where FlushAsync's task is
        // still completed but the instance is marked disposed for good.
        CustomCellGroup.Empty.Dispose();
        CustomCellGroup.Empty.Dispose();

        Assert.Empty(CustomCellGroup.Empty.Cells);
        Assert.True(CustomCellGroup.Empty.FlushAsync().IsCompletedSuccessfully);
        Assert.False(IsMarkedDisposed(CustomCellGroup.Empty));
    }

    [Fact]
    public async Task FlushAsync_ReachesACheckBoxCellsInFlightWrite()
    {
        // Ruling C20 declares the group's flush as every cell. It returned a completed task on the
        // two at-once kinds, so the bounded flush at page close and at process exit returned at once
        // for a card whose only pending work was a toggle, and that toggle could be cut short by the
        // log closing and the process returning. Red against
        // `Text?.FlushAsync() ?? Task.CompletedTask`, where the flush below completes with the write
        // still parked inside the delegate.
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var log = new Factory.WriteLog();

        using var group = new CustomCellGroup([Factory.Cell(Factory.Boolean("Done"), write: Write)]);
        group.Cells[0].IsChecked = true;
        Assert.True(started.Wait(Budget), "The at-once write never reached the delegate.");

        var flush = group.FlushAsync();
        Assert.False(flush.IsCompleted);

        release.Set();
        await flush.WaitAsync(Budget);
        Assert.Equal([CustomColumnSlug.True], log.Values);

        CustomWriteResult Write(Guid columnId, CustomValueKey key, string? value)
        {
            started.Set();
            release.Wait(Budget);
            return log.Write(columnId, key, value);
        }
    }

    // The shared instance holds no cell, so disposing it changes nothing a caller can see except
    // the flag itself: that is exactly why the defect was invisible to every per-unit review, and
    // why the flag is what this case reads. A group that owns nothing today can own something
    // tomorrow, and the process-wide instance would already be marked disposed when it did.
    private static bool IsMarkedDisposed(CustomCellGroup group)
        => (bool)typeof(CustomCellGroup)
            .GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(group)!;

    [Fact]
    public async Task FlushAsync_ReachesEveryCell()
    {
        var delay = new FakeDelay();
        var log = new Factory.WriteLog();
        using var group = new CustomCellGroup(
        [
            Factory.Cell(Factory.Text("First"), write: log.Write, delay: delay.Delay),
            Factory.Cell(Factory.Text("Second"), write: log.Write, delay: delay.Delay),
            Factory.Cell(Factory.Text("Third"), write: log.Write, delay: delay.Delay),
        ]);

        group.Cells[0].Text!.Text = "one";
        group.Cells[1].Text!.Text = "two";
        group.Cells[2].Text!.Text = "three";

        // The parked windows are never released: a flush must not depend on the debounce elapsing,
        // and it must reach every cell rather than the first one.
        await group.FlushAsync().WaitAsync(Budget);

        Assert.Equal(3, log.Calls.Count);
        Assert.Contains("one", log.Values);
        Assert.Contains("two", log.Values);
        Assert.Contains("three", log.Values);
    }

    [Fact]
    public async Task Dispose_FlushesEveryCellAndThenDisposesIt()
    {
        var delay = new FakeDelay();
        var writes = new ConcurrentQueue<string?>();
        var both = new TaskCompletionSource();
        var outstanding = 2;

        var group = new CustomCellGroup(
        [
            Factory.Cell(Factory.Text("First"), write: Record, delay: delay.Delay),
            Factory.Cell(Factory.Text("Second"), write: Record, delay: delay.Delay),
        ]);

        group.Cells[0].Text!.Text = "one";
        group.Cells[1].Text!.Text = "two";

        group.Dispose();

        // The flush half: Dispose cannot await, but AutosaveField attaches the pending text to its
        // write chain before Dispose returns, and disposing cancels only the parked window, so both
        // writes still land. A Dispose that only disposed would lose the last second of typing.
        await both.Task.WaitAsync(Budget);
        Assert.Equal<string?[]>(["one", "two"], [.. writes.Order()]);

        // The dispose half: each cell is really disposed, so a keystroke afterwards writes nothing
        // however long its window is left open.
        group.Cells[0].Text!.Text = "typed after the row was recycled";
        delay.Release();
        await Factory.SettleAsync(group.Cells[0]);
        Assert.Equal(2, writes.Count);

        CustomWriteResult Record(Guid columnId, CustomValueKey key, string? value)
        {
            writes.Enqueue(value);
            if (Interlocked.Decrement(ref outstanding) == 0)
            {
                both.TrySetResult();
            }

            return Factory.Written;
        }
    }

    [Fact]
    public async Task ASecondDispose_IsHarmless()
    {
        var delay = new FakeDelay();
        var log = new Factory.WriteLog();
        var group = new CustomCellGroup(
        [
            Factory.Cell(Factory.Text("First"), write: log.Write, delay: delay.Delay),
        ]);

        group.Cells[0].Text!.Text = "one";

        group.Dispose();
        await group.Cells[0].FlushAsync().WaitAsync(Budget);
        var afterFirst = log.Calls.Count;

        // A surface that replaces a group and then closes the page disposes the same group twice.
        group.Dispose();
        await group.Cells[0].FlushAsync().WaitAsync(Budget);

        Assert.Equal(afterFirst, log.Calls.Count);
    }
}
