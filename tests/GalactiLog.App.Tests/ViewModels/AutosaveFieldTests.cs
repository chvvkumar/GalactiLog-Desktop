using System.Collections.Concurrent;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.ViewModels;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 6 Task 3, the autosave spine. Roadmap Verify line, first clause: autosave fires once
// after the debounce window rather than per keystroke. No window, no dispatcher and no database:
// the delay is FakeDelay (shared with WatcherServiceTests and ScanSchedulerTests) and the post
// seam runs its closure inline.
public class AutosaveFieldTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public void IdleWindow_IsOneSecond()
    {
        // Spec 12.4 gives one figure. Target notes and session notes share this constant, so they
        // cannot drift to different windows (collision map: Task 3 owns it, Task 4 reuses it).
        Assert.Equal(TimeSpan.FromSeconds(1), AutosaveField.IdleWindow);
    }

    [Fact]
    public async Task Typing_SavesOnceAfterTheIdleWindow()
    {
        var delay = new FakeDelay();
        var saves = new ConcurrentQueue<string?>();
        using var field = new AutosaveField(text => saves.Enqueue(text), delay.Delay, Inline);

        field.Text = "M";
        field.Text = "M 3";
        field.Text = "M 31 notes";

        // Three keystrokes opened three windows; the first two were cancelled by the next
        // keystroke, so releasing every parked wait fires exactly one save.
        Assert.Equal(3, delay.Requested.Count);
        Assert.All(delay.Requested, requested => Assert.Equal(AutosaveField.IdleWindow, requested));

        delay.Release();
        await Settle(field);

        Assert.Single(saves);
    }

    [Fact]
    public async Task Typing_SavesTheFinalText_NotAnIntermediateOne()
    {
        var delay = new FakeDelay();
        var saves = new ConcurrentQueue<string?>();
        using var field = new AutosaveField(text => saves.Enqueue(text), delay.Delay, Inline);

        field.Text = "M";
        field.Text = "M 31 notes";

        delay.Release();
        await Settle(field);

        Assert.Equal("M 31 notes", Assert.Single(saves));
    }

    // F18 follow-up. The note write is a synchronous SQLite write and must not run on the thread
    // that typed. Proven by parking the write and observing that the keystroke's caller carried on,
    // not by comparing thread ids: that comparison is only sound while the calling thread stays
    // occupied, and occupying it means a blocking wait that needs the pool to make progress at the
    // same time (TRACKING section 2 item 8).
    [Fact]
    public async Task Save_RunsOffTheCallingThread()
    {
        var delay = new FakeDelay();
        using var release = new ManualResetEventSlim(false);
        var saves = 0;
        using var field = new AutosaveField(
            _ =>
            {
                Interlocked.Increment(ref saves);
                release.Wait(Budget);
            },
            delay.Delay,
            Inline);

        field.Text = "typed";
        delay.Release();

        // Releasing the debounce returned while the write is parked, so the write is not on this
        // thread: a synchronous write would have parked this line instead.
        Assert.False(field.PendingSave.IsCompleted);

        release.Set();
        await Settle(field);

        Assert.Equal(1, Volatile.Read(ref saves));
    }

    [Fact]
    public async Task Save_IsSaving_IsTrueDuringTheWriteAndFalseAfter()
    {
        var delay = new FakeDelay();
        AutosaveField? field = null;
        var duringWrite = false;
        var entered = new ManualResetEventSlim();
        field = new AutosaveField(
            _ =>
            {
                duringWrite = field!.IsSaving;
                entered.Set();
            },
            delay.Delay,
            Inline);

        using (field)
        {
            Assert.False(field.IsSaving);
            field.Text = "typed";
            delay.Release();

            Assert.True(entered.Wait(Budget), "the save never ran");
            await Settle(field);

            Assert.True(duringWrite, "the saving indicator must be on while the write runs");
            Assert.False(field.IsSaving);
        }
    }

    [Fact]
    public async Task Save_EmptyText_SavesNull()
    {
        var delay = new FakeDelay();
        var saves = new ConcurrentQueue<string?>();
        using var field = new AutosaveField(text => saves.Enqueue(text), delay.Delay, Inline);

        field.Text = "typed";
        delay.Release();
        await Settle(field);

        field.Text = "";
        delay.Release();
        await Settle(field);

        // targets.notes returns to null rather than holding an empty string.
        var recorded = saves.ToArray();
        Assert.Equal(2, recorded.Length);
        Assert.Equal("typed", recorded[0]);
        Assert.Null(recorded[1]);
    }

    [Fact]
    public async Task Save_Throwing_SetsLastFailureAndKeepsTheText()
    {
        var delay = new FakeDelay();
        var failure = new InvalidOperationException("database is locked");
        using var field = new AutosaveField(_ => throw failure, delay.Delay, Inline);

        field.Text = "typed";
        delay.Release();
        await Settle(field);

        Assert.Same(failure, field.LastFailure);
        Assert.True(field.HasFailure);
        Assert.Equal("typed", field.Text);
        Assert.False(field.IsSaving);
    }

    [Fact]
    public async Task Save_Succeeding_ClearsLastFailure()
    {
        var delay = new FakeDelay();
        var shouldThrow = true;
        using var field = new AutosaveField(
            _ =>
            {
                if (shouldThrow)
                {
                    throw new InvalidOperationException("database is locked");
                }
            },
            delay.Delay,
            Inline);

        field.Text = "typed";
        delay.Release();
        await Settle(field);
        Assert.NotNull(field.LastFailure);

        shouldThrow = false;
        field.Text = "typed again";
        delay.Release();
        await Settle(field);

        Assert.Null(field.LastFailure);
        Assert.False(field.HasFailure);
    }

    [Fact]
    public async Task Save_TwoWindows_AreSerialised_NotInterleaved()
    {
        var delay = new FakeDelay();
        var events = new ConcurrentQueue<string>();
        var firstEntered = new ManualResetEventSlim();
        var releaseFirst = new ManualResetEventSlim();
        var field = new AutosaveField(
            text =>
            {
                events.Enqueue($"start {text}");
                if (text == "a")
                {
                    firstEntered.Set();
                    releaseFirst.Wait(Budget);
                }

                events.Enqueue($"end {text}");
            },
            delay.Delay,
            Inline);

        using (field)
        {
            field.Text = "a";
            delay.Release();
            Assert.True(firstEntered.Wait(Budget), "the first save never ran");

            // Queued while the first write is still inside the save delegate. Without the write
            // chain the second would start here and the two would interleave.
            field.Text = "b";
            delay.Release();
            releaseFirst.Set();

            await Settle(field);
        }

        Assert.Equal(["start a", "end a", "start b", "end b"], events.ToArray());
    }

    [Fact]
    public void Reseed_WhenClean_AdoptsTheServerValue()
    {
        var delay = new FakeDelay();
        var saves = 0;
        using var field = new AutosaveField(_ => Interlocked.Increment(ref saves), delay.Delay, Inline);

        field.Reseed("from the database");

        Assert.Equal("from the database", field.Text);
        Assert.False(field.IsDirty);

        // An adopted value is already what the database holds, so adopting it must not open a
        // window and write it straight back.
        Assert.Empty(delay.Requested);
        Assert.Equal(0, saves);
    }

    [Fact]
    public void Reseed_WhileDirty_DoesNotOverwriteTheUserText()
    {
        var delay = new FakeDelay();
        using var field = new AutosaveField(_ => { }, delay.Delay, Inline);

        field.Text = "what the user is typing";
        Assert.True(field.IsDirty);

        field.Reseed("what a scan-driven refresh found");

        Assert.Equal("what the user is typing", field.Text);
    }

    [Fact]
    public async Task Reseed_WithThePreSaveValue_AfterASave_DoesNotRevert()
    {
        var delay = new FakeDelay();
        using var field = new AutosaveField(_ => { }, delay.Delay, Inline);

        field.Reseed("the old note");
        field.Text = "the new note";
        delay.Release();
        await Settle(field);
        Assert.False(field.IsDirty);

        // A page refresh whose read happened before the write landed carries the pre-save text.
        // It equals the last value adopted from the database, so it is ignored.
        field.Reseed("the old note");

        Assert.Equal("the new note", field.Text);
    }

    [Fact]
    public async Task FlushAsync_SavesImmediatelyWithoutWaitingOutTheWindow()
    {
        var delay = new FakeDelay();
        var saves = new ConcurrentQueue<string?>();
        using var field = new AutosaveField(text => saves.Enqueue(text), delay.Delay, Inline);

        field.Text = "typed and navigated away from";

        // The parked wait is never released: a flush must not depend on the debounce elapsing.
        await field.FlushAsync().WaitAsync(Budget);

        Assert.Equal("typed and navigated away from", Assert.Single(saves));

        // And releasing the window afterwards must not write a second time.
        delay.Release();
        await Settle(field);
        Assert.Single(saves);
    }

    [Fact]
    public async Task FlushAsync_DuringAnInFlightWrite_JoinsItInsteadOfWritingTwice()
    {
        // Review finding 1. IsDirty stays true until the write carrying the text completes, so a
        // page closed while the debounced write is still running used to queue the same text a
        // second time, and both writes attached to the same antecedent and ran in parallel.
        var delay = new FakeDelay();
        var events = new ConcurrentQueue<string>();
        var entered = new ManualResetEventSlim();
        var releaseWrite = new ManualResetEventSlim();
        var field = new AutosaveField(
            text =>
            {
                events.Enqueue($"start {text}");
                entered.Set();
                releaseWrite.Wait(Budget);
                events.Enqueue($"end {text}");
            },
            delay.Delay,
            Inline);

        using (field)
        {
            field.Text = "final";
            delay.Release();
            Assert.True(entered.Wait(Budget), "the debounced write never started");
            Assert.True(field.IsDirty, "the flag is still set while the write is in flight");

            var flush = field.FlushAsync();
            releaseWrite.Set();
            await flush.WaitAsync(Budget);
            await Settle(field);
        }

        // Exactly one write, carrying the final text, with no second start between the pair.
        Assert.Equal(["start final", "end final"], events.ToArray());
    }

    [Fact]
    public async Task FlushAsync_WhenClean_WritesNothing()
    {
        var delay = new FakeDelay();
        var saves = 0;
        using var field = new AutosaveField(_ => Interlocked.Increment(ref saves), delay.Delay, Inline);

        await field.FlushAsync().WaitAsync(Budget);

        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task Dispose_CancelsAPendingWindow()
    {
        var delay = new FakeDelay();
        var saves = 0;
        var field = new AutosaveField(_ => Interlocked.Increment(ref saves), delay.Delay, Inline);

        field.Text = "typed";
        var pending = field.PendingSave;
        field.Dispose();

        delay.Release();
        await pending.WaitAsync(Budget);

        Assert.Equal(0, saves);
    }

    private static Task Settle(AutosaveField field) => field.PendingSave.WaitAsync(Budget);

    // The post seam. No dispatcher in these tests, so the closure runs on whatever thread reached
    // it, which is the same shape DashboardViewModelTestFactory uses.
    private static void Inline(Action action) => action();
}
