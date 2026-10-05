using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Phase 6 Task 5. The one writer of display.columns (design-spec 5.8.2). Two tables persist their
// column lists and each write is a load-modify-save of the whole document, so what these tests
// are really about is that the load happens inside the queued write and that the queue is a queue.
public class DisplayColumnWriterTests
{
    // The display document, in memory. Same shape as TargetListViewModelTests' fake, which is the
    // store this writer was extracted out from behind.
    private sealed class FakeDisplayStore(DisplaySettings? seed = null)
    {
        public DisplaySettings Current { get; private set; } = seed ?? new DisplaySettings();

        public int Saves { get; private set; }

        public DisplaySettings Get() => Current;

        public void Save(DisplaySettings value)
        {
            Current = value;
            Saves++;
        }
    }

    [Fact]
    public async Task Write_PersistsTheOrderedKeyList()
    {
        var store = new FakeDisplayStore();
        var writer = new DisplayColumnWriter(store.Get, store.Save);

        writer.Write(DisplaySettings.FramesTableId, ["time", "file_name", "median_hfr"]);
        await writer.Pending;

        Assert.Equal(
            ["time", "file_name", "median_hfr"],
            store.Current.ColumnsFor(DisplaySettings.FramesTableId));
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public async Task Write_PreservesEveryOtherKeyOfTheDisplayDocument()
    {
        // The other table's entry and the whole groups document survive a write. This is what a
        // save of a snapshotted document would break.
        var seed = new DisplaySettings();
        seed.Groups["weather"] = seed.Groups["weather"] with { Enabled = true };
        var store = new FakeDisplayStore(seed);
        var writer = new DisplayColumnWriter(store.Get, store.Save);

        writer.Write(DisplaySettings.FramesTableId, ["time"]);
        await writer.Pending;

        Assert.Equal(["time"], store.Current.ColumnsFor(DisplaySettings.FramesTableId));
        Assert.Equal(
            DisplaySettings.DefaultColumns[DisplaySettings.DashboardTableId],
            store.Current.ColumnsFor(DisplaySettings.DashboardTableId));
        Assert.True(store.Current.Groups["weather"].Enabled);
        Assert.True(store.Current.Groups["quality"].Fields["hfr"]);
    }

    [Fact]
    public async Task Write_TwoTables_BothSurvive()
    {
        // The lost update this class exists to prevent. Before it, the dashboard and the frame
        // table each held their own chain and each load-modify-saved the whole document, so the
        // second save could be built on a document read before the first one landed.
        var store = new FakeDisplayStore();
        var writer = new DisplayColumnWriter(store.Get, store.Save);

        writer.Write(DisplaySettings.DashboardTableId, ["name", "integration"]);
        writer.Write(DisplaySettings.FramesTableId, ["time", "fwhm"]);
        await writer.Pending;

        Assert.Equal(["name", "integration"], store.Current.ColumnsFor(DisplaySettings.DashboardTableId));
        Assert.Equal(["time", "fwhm"], store.Current.ColumnsFor(DisplaySettings.FramesTableId));
    }

    // F18 follow-up: a column toggle must never block the UI thread on SQLite. Proven by parking
    // the save and observing that Write returned anyway. The old form awaited the write and then
    // compared thread ids, which is unsound in the other direction: awaiting frees the calling
    // thread, so the pool may legitimately run the save on it (TRACKING section 2 item 8).
    [Fact]
    public async Task Write_RunsOffTheCallingThread()
    {
        using var release = new ManualResetEventSlim(false);
        var saves = 0;
        var display = new DisplaySettings();
        var writer = new DisplayColumnWriter(
            () => display,
            value =>
            {
                Interlocked.Increment(ref saves);
                release.Wait(TimeSpan.FromSeconds(30));
                display = value;
            });

        writer.Write(DisplaySettings.FramesTableId, ["time"]);

        // Write returned while the save is parked: a synchronous save would have parked Write.
        Assert.False(writer.Pending.IsCompleted);

        release.Set();
        await writer.Pending;

        Assert.Equal(1, Volatile.Read(ref saves));
        Assert.Equal(["time"], display.ColumnsFor(DisplaySettings.FramesTableId));
    }

    [Fact]
    public async Task Write_QueuedWritesAreSerialised()
    {
        // The first save is held inside the delegate until the second has been queued. The two
        // must still land in queue order and must never overlap.
        var overlapped = false;
        var concurrent = 0;
        var released = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        var writes = new List<string[]>();
        var display = new DisplaySettings();

        void Save(DisplaySettings value)
        {
            if (Interlocked.Increment(ref concurrent) > 1)
            {
                overlapped = true;
            }

            if (writes.Count == 0)
            {
                entered.Set();
                released.Wait(TimeSpan.FromSeconds(30));
            }

            display = value;
            writes.Add(value.ColumnsFor(DisplaySettings.FramesTableId));
            Interlocked.Decrement(ref concurrent);
        }

        var writer = new DisplayColumnWriter(() => display, Save);

        writer.Write(DisplaySettings.FramesTableId, ["time"]);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        writer.Write(DisplaySettings.FramesTableId, ["time", "fwhm"]);
        released.Set();
        await writer.Pending;

        Assert.False(overlapped);
        Assert.Equal(2, writes.Count);
        Assert.Equal(["time"], writes[0]);
        Assert.Equal(["time", "fwhm"], writes[1]);
    }

    [Fact]
    public async Task Write_Throwing_IsLoggedAndDropped()
    {
        // The click has already changed what is on screen. A locked database must not take the
        // window down, and must not break the chain: the next write still runs.
        var attempts = 0;
        var logger = new RecordingLogger();
        var display = new DisplaySettings();
        var writer = new DisplayColumnWriter(
            () => display,
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("database is locked");
            },
            logger);

        writer.Write(DisplaySettings.FramesTableId, ["time"]);
        writer.Write(DisplaySettings.DashboardTableId, ["name"]);
        await writer.Pending;

        Assert.Equal(2, attempts);
        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        Assert.All(logger.Entries, entry => Assert.IsType<InvalidOperationException>(entry.Exception));
    }

    [Fact]
    public void LastWritten_RemembersTheKeysPerTable_AndNothingBeforeTheFirstWrite()
    {
        // Review finding 1: this is what a table built after another one's toggle starts from,
        // instead of the startup snapshot. Recorded at queue time, so it does not wait on SQLite.
        var store = new FakeDisplayStore();
        var writer = new DisplayColumnWriter(store.Get, store.Save);

        Assert.Null(writer.LastWritten(DisplaySettings.FramesTableId));

        writer.Write(DisplaySettings.FramesTableId, ["time", "fwhm"]);

        Assert.Equal(["time", "fwhm"], writer.LastWritten(DisplaySettings.FramesTableId)!);
        Assert.Null(writer.LastWritten(DisplaySettings.DashboardTableId));

        writer.Write(DisplaySettings.DashboardTableId, ["name"]);
        Assert.Equal(["name"], writer.LastWritten(DisplaySettings.DashboardTableId)!);
        Assert.Equal(["time", "fwhm"], writer.LastWritten(DisplaySettings.FramesTableId)!);

        // A copy: a caller sorting or trimming what it got back must not rewrite the memory.
        writer.LastWritten(DisplaySettings.FramesTableId)![0] = "clobbered";
        Assert.Equal(["time", "fwhm"], writer.LastWritten(DisplaySettings.FramesTableId)!);
    }

    // The second overload writes the keys of the display document that are not columns, through
    // the same chain, so a column toggle and two target_page choices cannot interleave into a lost
    // update. These two cases go through the real
    // writer rather than through a recorded mutation, because a recorder cannot interleave.

    [Fact]
    public async Task Write_ATargetPageMutation_LeavesTheColumnsIntact()
    {
        var seed = new DisplaySettings();
        seed.Groups["weather"] = seed.Groups["weather"] with { Enabled = true };
        var store = new FakeDisplayStore(seed);
        var writer = new DisplayColumnWriter(store.Get, store.Save);

        writer.Write(DisplaySettings.FramesTableId, ["time", "fwhm"]);
        writer.Write(display => display with
        {
            TargetPage = display.TargetPage with { FrameListIncludeUnmeasured = false },
        });
        await writer.Pending;

        Assert.False(store.Current.TargetPage.FrameListIncludeUnmeasured);
        Assert.Equal(["time", "fwhm"], store.Current.ColumnsFor(DisplaySettings.FramesTableId));
        Assert.Equal(
            DisplaySettings.DefaultColumns[DisplaySettings.DashboardTableId],
            store.Current.ColumnsFor(DisplaySettings.DashboardTableId));
        Assert.True(store.Current.Groups["weather"].Enabled);
        Assert.Equal(2, store.Saves);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Write_TwoTargetPageMutations_BothLand_InEitherOrder(bool ledgerFirst)
    {
        // Two target_page choices share one object on one document. Each mutation is applied to the document as loaded inside its own queued
        // write, so whichever runs second builds on what the first one saved. A snapshot taken at
        // queue time would drop one of the two, and the order must not decide which.
        var store = new FakeDisplayStore();
        var writer = new DisplayColumnWriter(store.Get, store.Save);

        Action ledger = () => writer.Write(display => display with
        {
            TargetPage = display.TargetPage with { FrameListMode = "bad" },
        });

        Action section = () => writer.Write(display => display with
        {
            TargetPage = display.TargetPage with { GradingBaseline = "rig" },
        });

        if (ledgerFirst)
        {
            ledger();
            section();
        }
        else
        {
            section();
            ledger();
        }

        await writer.Pending;

        Assert.Equal("bad", store.Current.TargetPage.FrameListMode);
        Assert.Equal("rig", store.Current.TargetPage.GradingBaseline);

        // And the key neither of them named keeps its default.
        Assert.True(store.Current.TargetPage.FrameListIncludeUnmeasured);
        Assert.Equal(2, store.Saves);
    }

    [Fact]
    public async Task Write_TheTargetPageHoldersThreeToggles_AllReachTheDocument()
    {
        // P13 phase review P2-1: the holder is the production caller of the overload above, so the
        // chain is proved from the object the cards and the page actually write through rather
        // than from a mutation a test wrote by hand.
        var store = new FakeDisplayStore();
        var writer = new DisplayColumnWriter(store.Get, store.Save);
        var state = new TargetPageState(new TargetPageSettings(), writer.Write);

        writer.Write(DisplaySettings.FramesTableId, ["time", "fwhm"]);
        state.GradingBaseline = GradingBaseline.Rig;
        state.FrameListMode = "bad";
        state.FrameListIncludeUnmeasured = false;
        await writer.Pending;

        Assert.Equal("rig", store.Current.TargetPage.GradingBaseline);
        Assert.Equal("bad", store.Current.TargetPage.FrameListMode);
        Assert.False(store.Current.TargetPage.FrameListIncludeUnmeasured);
        Assert.Equal(["time", "fwhm"], store.Current.ColumnsFor(DisplaySettings.FramesTableId));
        Assert.Equal(4, store.Saves);
    }

    [Fact]
    public async Task Write_AThrowingTargetPageSave_IsLoggedAndDropped_AndTheChainSurvives()
    {
        // The same contract the column overload has: a locked database must not take the window
        // down and must not break the chain.
        var logger = new RecordingLogger();
        var display = new DisplaySettings();
        var attempts = 0;
        var writer = new DisplayColumnWriter(
            () => display,
            value =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("database is locked");
                }

                display = value;
            },
            logger);

        writer.Write(d => d with { TargetPage = d.TargetPage with { FrameListMode = "bad" } });
        writer.Write(d => d with { TargetPage = d.TargetPage with { GradingBaseline = "rig" } });
        await writer.Pending;

        Assert.Equal(2, attempts);
        Assert.Equal(1, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        Assert.Equal("rig", display.TargetPage.GradingBaseline);
    }

    [Fact]
    public async Task Write_CapturesTheKeysOnTheCallingThread()
    {
        // The queued write records the state of the click that queued it. The caller's list is
        // copied, so mutating it afterwards cannot rewrite a pending write.
        var store = new FakeDisplayStore();
        var writer = new DisplayColumnWriter(store.Get, store.Save);
        var keys = new List<string> { "time", "file_name" };

        writer.Write(DisplaySettings.FramesTableId, keys);
        keys.Clear();
        await writer.Pending;

        Assert.Equal(["time", "file_name"], store.Current.ColumnsFor(DisplaySettings.FramesTableId));
    }

    [Fact]
    public async Task WriteWidth_PersistsAndClearsOneColumnWidth_AndLeavesTheRest()
    {
        // Phase 24 R5 (ruling R9). A drag stores one column's width beside the visibility list, a
        // double-click removes that entry alone, and neither touches the other columns or the
        // other table's widths. Red if the write replaced the map instead of merging into it.
        var seed = new DisplaySettings().WithColumnWidth(DisplaySettings.DashboardTableId, "name", 200d);
        var store = new FakeDisplayStore(seed);
        var writer = new DisplayColumnWriter(store.Get, store.Save);

        writer.WriteWidth(DisplaySettings.FramesTableId, "file_name", 312d);
        writer.WriteWidth(DisplaySettings.FramesTableId, "median_hfr", 64d);
        writer.WriteWidth(DisplaySettings.FramesTableId, "file_name", null);
        await writer.Pending;

        var frames = store.Current.ColumnWidthsFor(DisplaySettings.FramesTableId);
        Assert.Equal(new Dictionary<string, double> { ["median_hfr"] = 64d }, frames);
        Assert.Equal(200d, store.Current.ColumnWidthsFor(DisplaySettings.DashboardTableId)["name"]);
        Assert.Equal(3, store.Saves);
    }

    [Fact]
    public void LastWrittenWidths_RemembersTheWidthsPerTable_AClearedOneAsNull()
    {
        // The memo a table built after a drag starts from, since AppHost hands every table the
        // startup snapshot: a width is the value, a clear is a null entry, and a table never
        // written answers an empty map rather than throwing.
        var store = new FakeDisplayStore();
        var writer = new DisplayColumnWriter(store.Get, store.Save);

        Assert.Empty(writer.LastWrittenWidths(DisplaySettings.FramesTableId));

        writer.WriteWidth(DisplaySettings.FramesTableId, "file_name", 312d);
        writer.WriteWidth(DisplaySettings.FramesTableId, "fwhm", 70d);
        writer.WriteWidth(DisplaySettings.FramesTableId, "fwhm", null);

        var memo = writer.LastWrittenWidths(DisplaySettings.FramesTableId);
        Assert.Equal(312d, memo["file_name"]);
        Assert.True(memo.ContainsKey("fwhm"));
        Assert.Null(memo["fwhm"]);
        Assert.Empty(writer.LastWrittenWidths(DisplaySettings.DashboardTableId));
    }
}
