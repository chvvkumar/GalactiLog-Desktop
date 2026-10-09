using System.Collections.Concurrent;
using System.Diagnostics;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Dashboard;

// Spec 12.15's dashboard cells, view-model side: which cells a target row carries, what they are
// seeded from, what happens to them when the page is replaced, and the night expander's lazy read
// (ruling C11). No database and no window: every read and every write is a delegate, and the
// definitions come from TestSupport/CustomColumnTestFactory rather than being built inline.
public class TargetRowCustomCellTests
{
    private static readonly Guid FirstTarget = Guid.Parse("10000000-0000-0000-0000-000000000000");
    private static readonly Guid SecondTarget = Guid.Parse("20000000-0000-0000-0000-000000000000");
    private static readonly DateOnly FirstNight = new(2026, 3, 14);
    private static readonly DateOnly SecondNight = new(2026, 3, 15);

    [Fact]
    public void ARow_CarriesOneCellPerVisibleTargetScopeColumn_InDisplayOrder()
    {
        // A failure is a filter on the hidden list alone: the session-scope column below is not in
        // it either, and a cell for it on the target row would write a session-scope value
        // with no night in its key. The two target columns are declared out of display order so a
        // list that takes them as given also fails.
        var priority = CustomColumnTestFactory.Dropdown("High", "Low");
        var done = CustomColumnTestFactory.Boolean("Done", order: 2);
        var notes = CustomColumnTestFactory.Text("Notes", order: 1, scope: CustomColumnScope.Session);
        var harness = new Harness([done, priority, notes]);

        var row = harness.LoadOneRow();

        Assert.Equal([priority.Name, done.Name], row.CustomCells.Select(cell => cell.Label));
    }

    [Fact]
    public void AColumnInTheHiddenList_DrawsNoCell()
    {
        // On by default, so only a slug the picker switched off draws nothing, while the picker
        // itself still has a row to tick back on.
        var priority = CustomColumnTestFactory.Dropdown("High");
        var harness = new Harness([priority]) { Hidden = [priority.Slug] };

        var row = harness.LoadOneRow();

        Assert.Empty(row.CustomCells);
        var entry = Assert.Single(harness.List.Columns, column => column.Key == priority.Slug);
        Assert.False(entry.IsVisible);
        Assert.True(entry.CanHide);
        Assert.True(entry.IsGroupEnabled);
    }

    [Fact]
    public void AnUnresolvedGroup_CarriesNoCells()
    {
        // A failure is a cell built with a null TargetId: CustomValueKey.ForTarget cannot be built
        // from one, and a cell that silently writes nothing looks like a working control that has
        // lost the reader's input. The resolved row beside it proves the column is switched on.
        var priority = CustomColumnTestFactory.Dropdown("High");
        var harness = new Harness([priority]);

        harness.Load(Resolved(FirstTarget, "M 31"), Unresolved("obj:Widget"));

        Assert.Single(harness.List.Rows[0].CustomCells);
        Assert.Empty(harness.List.Rows[1].CustomCells);
    }

    [Fact]
    public void ACellsSubjectIsTheTargetName()
    {
        // Spec 12.15's screen reader rule: the column name, then what the cell is about. A failure
        // is a subject built from the slug or from the group key.
        var priority = CustomColumnTestFactory.Dropdown("High");
        var harness = new Harness([priority]);

        var row = harness.LoadOneRow();

        Assert.Equal("Priority, M 31", Assert.Single(row.CustomCells).AutomationName);
    }

    [Fact]
    public void ARowsCells_AreSeededFromTheStoredValues()
    {
        // A failure is a cell that ignores the page's value read, or one that cannot tell "no row
        // stored" from "stored empty". The boolean column has a value; the dropdown has none and
        // renders as Not set.
        var done = CustomColumnTestFactory.Boolean("Done");
        var priority = CustomColumnTestFactory.Define(
            "Priority", CustomColumnType.Dropdown, CustomColumnScope.Target, ["High", "Low"], order: 1);
        var harness = new Harness([done, priority])
        {
            TargetValues = [new CustomValueRow(done.Id, CustomValueKey.ForTarget(FirstTarget), CustomColumnSlug.True)],
        };

        var row = harness.LoadOneRow();

        Assert.True(row.CustomCells[0].IsChecked);
        Assert.Equal("Not set", row.CustomCells[1].Selected);
    }

    [Fact]
    public async Task ReplacingThePage_DisposesTheOldRowsCells()
    {
        // The leak: a row recycled while its text cell holds an open debounce window. Without the
        // dispose the typed value reaches the catalogue only when that window fires, from a row
        // nothing renders any more, and a window opened at shutdown never fires at all.
        //
        // The dispose flushes first and cancels the window second (CustomCellGroup's own contract),
        // so the proof is in two halves: the write lands as soon as the page is replaced, with the
        // window still parked, and releasing the window afterwards adds no second write.
        var notes = CustomColumnTestFactory.Text("Notes");
        var delay = new FakeDelay();
        var harness = new Harness([notes]) { Delay = delay.Delay };

        var row = harness.LoadOneRow();
        var cell = Assert.Single(row.CustomCells);
        cell.Text!.Text = "a note";

        Assert.Single(delay.Requested);
        Assert.Empty(harness.Writes.Values);

        // The write is awaited through the delegate itself, not through the cell's own pending
        // window: that window ends the moment the dispose cancels it, well before the flush it
        // queued has reached the chain. Without the dispose nothing writes at all and this await
        // ends as a timeout, which is the red this case is for.
        harness.Load(Resolved(SecondTarget, "M 42"));
        await harness.Wrote.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["a note"], harness.Writes.Values);

        // And the window really was cancelled: releasing it adds nothing. The flush below drains
        // the chain rather than queueing anything, because the field already holds this text.
        delay.Release();
        await CustomColumnTestFactory.SettleAsync(cell);
        await cell.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["a note"], harness.Writes.Values);
    }

    [Fact]
    public async Task DisposingTheList_WaitsForADirtyCellsWrite()
    {
        // Review P2 3, the shutdown half. Disposing a row only STARTS the flush
        // (CustomCellGroup.Dispose does `_ = FlushAsync()`), and at shutdown nothing runs after
        // Dispose returns, so the started write can die with the process. TargetListViewModel.Dispose
        // therefore awaits every row's flush once, on the same two second budget
        // SessionCardViewModel spends on the Target page, before it disposes the rows.
        //
        // The debounce window is parked for the whole case, so the write can only come from the
        // flush, and the write delegate itself parks inside the delegate. Pinning "the write had
        // landed by the time Dispose returned" alone is not enough: with the wait removed the pool
        // still finishes the write during the row disposals that follow, and the mutated tree
        // passed two runs of three. What only a real wait can produce is a Dispose that does not
        // return while the write is outstanding, so the budget itself is the assertion: a Dispose
        // that does not wait returns in under a millisecond.
        var notes = CustomColumnTestFactory.Text("Notes");
        var delay = new FakeDelay();
        var releaseWrite = new ManualResetEventSlim();
        var received = new ConcurrentQueue<string?>();
        var harness = new Harness([notes])
        {
            Delay = delay.Delay,
            OnWrite = value =>
            {
                received.Enqueue(value);
                releaseWrite.Wait(TimeSpan.FromSeconds(30));
            },
        };

        var row = harness.LoadOneRow();
        var cell = Assert.Single(row.CustomCells);
        cell.Text!.Text = "a note";

        Assert.Single(delay.Requested);
        Assert.Empty(harness.Writes.Values);

        var stopwatch = Stopwatch.StartNew();
        harness.List.Dispose();
        var blocked = stopwatch.Elapsed;

        // The typed text reached the write delegate before Dispose returned, and Dispose spent its
        // budget there rather than returning on a write still in flight. One second, not two: the
        // lower bound only has to separate a real wait from none.
        Assert.Equal(["a note"], received);
        Assert.True(
            blocked >= TimeSpan.FromSeconds(1),
            $"Dispose returned after {blocked.TotalMilliseconds:0} ms with a cell write still in flight");

        // And the wait is bounded, not a hang: Dispose returned while this write was still parked,
        // and the write finishes normally once it is released.
        releaseWrite.Set();
        await harness.Wrote.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["a note"], harness.Writes.Values);
    }

    [Fact]
    public void TheColumnRows_SeedFromLastWrittenBeforeTheDisplayDocument()
    {
        // The defect ColumnPickerViewModel records for the frame table: the display document the
        // host read at startup is a snapshot and is stale the moment any table toggles a column.
        // Seeded from the snapshot alone, the column below reads as shown although this process
        // has already queued it as hidden, and the list's own next write would revert the hide.
        var priority = CustomColumnTestFactory.Dropdown("High");
        var stored = new DisplaySettings();
        var writer = new DisplayColumnWriter(() => stored, value => stored = value, NullLogger.Instance);
        writer.Write(DisplaySettings.DashboardHiddenTableId, [priority.Slug]);

        var harness = new Harness([priority]) { Writer = writer };
        var row = harness.LoadOneRow();

        Assert.False(Assert.Single(harness.List.Columns, column => column.Key == priority.Slug).IsVisible);
        Assert.Empty(row.CustomCells);
    }

    [Fact]
    public void SwitchingACustomColumnOff_WritesItToTheHiddenList()
    {
        // The stored half of the default: a column the reader switches off has to be told apart
        // from one created later, so the hide is written as a hidden slug.
        var priority = CustomColumnTestFactory.Dropdown("High");
        var stored = new DisplaySettings();
        var writer = new DisplayColumnWriter(() => stored, value => stored = value, NullLogger.Instance);
        var harness = new Harness([priority]) { Writer = writer };
        harness.LoadOneRow();

        harness.List.ToggleColumnCommand.Execute(
            harness.List.Columns.Single(column => column.Key == priority.Slug));

        Assert.Equal([priority.Slug], writer.LastWritten(DisplaySettings.DashboardHiddenTableId)!);
        Assert.Empty(harness.List.Rows[0].CustomCells);
    }

    [Fact]
    public async Task ExpandingARow_ReadsItsSessionValues_AndReadsThemAgainOnEveryExpansion()
    {
        // Ruling C11's half that stands: the read is lazy. A failure is an eager read with the page,
        // which at 50 rows is 50 reads of a table nobody has looked at.
        //
        // Ruling C27 replaced the other half. A read-once gate left the reader no way at all to
        // refresh a night value the Target page had changed, short of a scan or a filter change, and
        // the stale cell's next keystroke wrote its own whole content over the newer value.
        // Collapsing and re-expanding is that recovery now, at the cost of one read of one target.
        var notes = CustomColumnTestFactory.Text("Notes", scope: CustomColumnScope.Session);
        var harness = new Harness([notes]);

        var row = harness.LoadOneRow();

        // Nothing has been started, which is what an eager load would have done in the row's own
        // constructor. The counter alone cannot say so: the read runs on a thread-pool thread and
        // would not have reached the counter yet.
        Assert.Null(row.PendingSessionCells);
        Assert.Equal(0, harness.PerTargetReads);
        Assert.Empty(row.Sessions[0].CustomCells);

        row.ToggleSessionsCommand.Execute(null);
        await harness.SettleSessions(row);

        Assert.Equal(1, harness.PerTargetReads);
        Assert.Single(row.Sessions[0].CustomCells);

        // Collapse reads nothing, and the expansion after it reads again.
        row.ToggleSessionsCommand.Execute(null);
        await harness.SettleSessions(row);
        Assert.Equal(1, harness.PerTargetReads);

        row.ToggleSessionsCommand.Execute(null);
        await harness.SettleSessions(row);
        Assert.Equal(2, harness.PerTargetReads);
    }

    [Fact]
    public async Task ReExpandingARow_ShowsTheValueTheTargetPageStored()
    {
        // Ruling C27, the journey: expand a dashboard row, open that target, set the night's value
        // on the Nights ledger, come back. The dashboard is a singleton behind an overlay, so its
        // rows are never re-queried by the navigation. Red against the read-once gate, where the
        // second expansion shows the pre-edit value for the rest of the process and the next
        // keystroke in that cell writes it back over the newer one.
        var notes = CustomColumnTestFactory.Text("Notes", scope: CustomColumnScope.Session);
        var stored = new List<CustomValueRow>();
        var harness = new Harness([notes]) { PerTargetValuesSource = () => stored };

        var row = harness.LoadOneRow();
        row.ToggleSessionsCommand.Execute(null);
        await harness.SettleSessions(row);
        Assert.Equal("", row.Sessions[0].CustomCells[0].Text!.Text);

        // What the Target page wrote while this row sat behind it.
        stored.Add(new CustomValueRow(notes.Id, CustomValueKey.ForSession(FirstTarget, FirstNight), "clear night"));

        row.ToggleSessionsCommand.Execute(null);
        row.ToggleSessionsCommand.Execute(null);
        await harness.SettleSessions(row);

        Assert.Equal("clear night", row.Sessions[0].CustomCells[0].Text!.Text);
    }

    [Fact]
    public async Task ARefreshOfTheNightValues_LeavesACellThatIsBeingTypedInAlone()
    {
        // Ruling C27's second half: a reseed never touches a cell that is dirty. The re-read arrives
        // while the reader is mid word, and the value it carries is older than what is on screen.
        // A failure is a rebuilt or reseeded cell, either of which discards the typing.
        var notes = CustomColumnTestFactory.Text("Notes", scope: CustomColumnScope.Session);
        var stored = new List<CustomValueRow>();
        var delay = new FakeDelay();
        var harness = new Harness([notes]) { PerTargetValuesSource = () => stored, Delay = delay.Delay };

        var row = harness.LoadOneRow();
        row.ToggleSessionsCommand.Execute(null);
        await harness.SettleSessions(row);

        var cell = row.Sessions[0].CustomCells[0];
        cell.Text!.Text = "half typ";
        stored.Add(new CustomValueRow(notes.Id, CustomValueKey.ForSession(FirstTarget, FirstNight), "something older"));

        row.RefreshSessionCells();
        await harness.SettleSessions(row);

        Assert.Same(cell, row.Sessions[0].CustomCells[0]);
        Assert.Equal("half typ", cell.Text.Text);
    }

    [Fact]
    public void ALibraryWithNoCustomColumn_ReadsNoValues()
    {
        // Ruling C33: a library with no custom column pays nothing. Red against the tree before it,
        // where every dashboard query issued a TargetValues command with the page's whole id list,
        // which could only ever return nothing.
        var harness = new Harness([]);

        harness.Load(Resolved(FirstTarget, "M 31"), Resolved(SecondTarget, "M 42"));

        Assert.Equal(1, harness.DefinitionReads);
        Assert.Equal(0, harness.TargetValueReads);

        // And the read is still made on a library that has one, so the guard is the definition list
        // and not the feature.
        var withOne = new Harness([CustomColumnTestFactory.Dropdown("High")]);
        withOne.Load(Resolved(FirstTarget, "M 31"));
        Assert.Equal(1, withOne.TargetValueReads);
    }

    [Fact]
    public async Task ARepublishOfTheSameColumns_KeepsWhatIsBeingTypedIntoATargetCell()
    {
        // Phase review target P2-3. The Target page reseeded its cells on a refresh and the dashboard
        // rebuilt its own unconditionally, so the same value in the same kind of cell survived a
        // refresh on one page and not on the other. Red against RebuildCustomCells as it stood: the
        // cell below is a new instance and the typing is gone.
        var notes = CustomColumnTestFactory.Text("Notes");
        var delay = new FakeDelay();
        var harness = new Harness([notes]) { Delay = delay.Delay };

        var row = harness.LoadOneRow();
        var cell = Assert.Single(row.CustomCells);
        cell.Text!.Text = "half typ";

        // What a gear toggle of an unrelated column, or any other republish of the same definitions,
        // does to this row.
        row.RebuildCustomCells(harness.SameWiring(), harness.TargetValues);

        Assert.Same(cell, Assert.Single(row.CustomCells));
        Assert.Equal("half typ", cell.Text.Text);

        // And nothing was flushed by a dispose that did not happen.
        Assert.Empty(harness.Writes.Calls);
        delay.Release();
        await CustomColumnTestFactory.SettleAsync(cell);
        Assert.Equal(new string?[] { "half typ" }, harness.Writes.Values);
    }

    [Fact]
    public async Task ASessionLine_CarriesOneCellPerSessionScopeColumn_UngatedByAnyPicker()
    {
        // User choice 4: the night expander has no picker, so the stored visible list says nothing
        // about it. A failure is a gate copied from the target row, which would leave the expander
        // permanently empty on every fresh profile. The target-scope column beside it must not
        // appear on a night line.
        var notes = CustomColumnTestFactory.Text("Notes", scope: CustomColumnScope.Session);
        var done = CustomColumnTestFactory.Boolean("Done", order: 1, scope: CustomColumnScope.Session);
        var priority = CustomColumnTestFactory.Dropdown("High");
        var harness = new Harness([notes, done, priority]);

        var row = harness.LoadOneRow();
        row.ToggleSessionsCommand.Execute(null);
        await harness.SettleSessions(row);

        Assert.All(
            row.Sessions,
            session => Assert.Equal([notes.Name, done.Name], session.CustomCells.Select(cell => cell.Label)));
    }

    [Fact]
    public async Task ASessionCellsSubjectIsTheIsoNight()
    {
        var notes = CustomColumnTestFactory.Text("Notes", scope: CustomColumnScope.Session);
        var harness = new Harness([notes]);

        var row = harness.LoadOneRow();
        row.ToggleSessionsCommand.Execute(null);
        await harness.SettleSessions(row);

        Assert.Equal("Notes, 2026-03-14", row.Sessions[0].CustomCells[0].AutomationName);
        Assert.Equal("Notes, 2026-03-15", row.Sessions[1].CustomCells[0].AutomationName);
    }

    [Fact]
    public async Task ASessionCell_WritesAValueKeyedByThisTargetAndThatNight()
    {
        // Ruling C17's session key, all four parts. A failure is a key that carries the rig label,
        // which is the rig scope's key and would collide with a rig row's own slot, or one that
        // carries a mosaic id, which nothing in this phase may write.
        //
        // The read answers this target's session-scope AND rig-scope rows in one list, so the rig
        // row below also proves the night cell is not seeded from it.
        var done = CustomColumnTestFactory.Boolean("Done", scope: CustomColumnScope.Session);
        var harness = new Harness([done])
        {
            PerTargetValues =
            [
                new CustomValueRow(done.Id, CustomValueKey.ForRig(FirstTarget, FirstNight, "RC8 / ASI2600MM"), CustomColumnSlug.True),
            ],
        };

        var row = harness.LoadOneRow();
        row.ToggleSessionsCommand.Execute(null);
        await harness.SettleSessions(row);

        var cell = row.Sessions[0].CustomCells[0];
        Assert.False(cell.IsChecked);

        cell.IsChecked = true;
        await CustomColumnTestFactory.SettleAsync(cell);

        var call = Assert.Single(harness.Writes.Calls);
        Assert.Equal(done.Id, call.ColumnId);
        Assert.Equal(FirstTarget, call.Key.TargetId);
        Assert.Null(call.Key.MosaicId);
        Assert.Equal(FirstNight, call.Key.SessionDate);
        Assert.Null(call.Key.RigLabel);
        Assert.Equal(CustomColumnSlug.True, call.Value);
    }

    [Fact]
    public async Task ARowDisposedWhileItsSessionReadIsInFlight_PublishesNoCells()
    {
        // The leak: the lazy read publishes through the page's post seam, so the publish lands on a
        // later turn. Expanding a row and then sorting puts the page's own disposal between the two,
        // and the publish then builds a second cell group on a row whose Dispose has already run.
        // Nothing will ever dispose that group, so every debounce window inside it stays open.
        var notes = CustomColumnTestFactory.Text("Notes", scope: CustomColumnScope.Session);
        var harness = new Harness([notes]) { HoldRead = true };

        var row = harness.LoadOneRow();
        row.ToggleSessionsCommand.Execute(null);
        Assert.True(harness.ReadEntered.Wait(TimeSpan.FromSeconds(30)), "The lazy read never started.");

        // The page is replaced with the read still parked, which is what disposes this row.
        harness.Load(Resolved(SecondTarget, "M 42"));
        harness.ReleaseRead.Set();

        // Bounded: the read's own task is awaited to completion, and the post seam runs its closure
        // inline, so a publish that was going to happen has happened by here.
        await harness.SettleSessions(row);

        Assert.All(row.Sessions, session => Assert.Empty(session.CustomCells));
    }

    [Fact]
    public void ASecondQueryWithTheSameColumns_LeavesTheColumnRowsAlone()
    {
        // Every query used to delete every custom entry from Columns and append it again, so two
        // custom columns raised four collection changes per page. Each one rebuilds the header
        // strip and resets any control bound to Columns, the open column gear flyout included,
        // which is the Phase 16 flyout trap in another shape.
        var priority = CustomColumnTestFactory.Dropdown("High");
        var harness = new Harness([priority]);
        harness.LoadOneRow();

        var changes = 0;
        harness.List.Columns.CollectionChanged += (_, _) => changes++;
        harness.Load(Resolved(FirstTarget, "M 31"));

        Assert.Equal(0, changes);
        Assert.Single(harness.List.Columns, column => column.Key == priority.Slug);
    }

    // ---- fixture ----

    private static TargetRow Resolved(Guid targetId, string name) => new(
        GroupKey: targetId.ToString(),
        TargetId: targetId,
        Name: name,
        CommonName: null,
        CatalogId: name,
        ObjectType: "G",
        ObjectCategory: "Galaxy",
        IntegrationSeconds: 3_600d,
        FrameCount: 10,
        SessionCount: 2,
        FirstSession: FirstNight,
        LastSession: SecondNight,
        Palette: [],
        Equipment: ["RC8 / ASI2600MM"],
        Aliases: [],
        Sessions:
        [
            new SessionSummary(FirstNight, 5, 1_800d),
            new SessionSummary(SecondNight, 5, 1_800d),
        ]);

    private static TargetRow Unresolved(string groupKey) => new(
        GroupKey: groupKey,
        TargetId: null,
        Name: "Widget",
        CommonName: null,
        CatalogId: null,
        ObjectType: null,
        ObjectCategory: "Unknown",
        IntegrationSeconds: 600d,
        FrameCount: 2,
        SessionCount: 1,
        FirstSession: FirstNight,
        LastSession: FirstNight,
        Palette: [],
        Equipment: [],
        Aliases: [],
        Sessions: [new SessionSummary(FirstNight, 2, 600d)]);

    // One list wired to delegates that record what reached them. The post seam runs its closure
    // inline, the same shape AutosaveFieldTests uses, so nothing here needs a dispatcher.
    private sealed class Harness
    {
        private readonly IReadOnlyList<CustomColumnDefinition> _definitions;
        private DisplaySettings _stored = new();
        private TargetListViewModel? _list;

        public Harness(IReadOnlyList<CustomColumnDefinition> definitions) => _definitions = definitions;

        /// <summary>The custom slugs stored as switched off; empty shows every column.</summary>
        public IReadOnlyList<string> Hidden { get; init; } = [];

        public IReadOnlyList<CustomValueRow> TargetValues { get; init; } = [];

        public IReadOnlyList<CustomValueRow> PerTargetValues { get; init; } = [];

        /// <summary>What the lazy night read answers, evaluated per read rather than once, so a case
        /// can change what the catalogue holds between two expansions.</summary>
        public Func<IReadOnlyList<CustomValueRow>>? PerTargetValuesSource { get; init; }

        public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }

        /// <summary>Runs inside the write delegate with the value, before the call is recorded, so
        /// a case can park a write while it is in flight.</summary>
        public Action<string?>? OnWrite { get; init; }

        public DisplayColumnWriter? Writer { get; init; }

        /// <summary>Parks the lazy session read inside the delegate, so a case can act while it is
        /// in flight. Released through <see cref="ReleaseRead"/>.</summary>
        public bool HoldRead { get; init; }

        /// <summary>Set by the read delegate as it is entered.</summary>
        public ManualResetEventSlim ReadEntered { get; } = new();

        public ManualResetEventSlim ReleaseRead { get; } = new();

        public CustomColumnTestFactory.WriteLog Writes { get; } = new();

        /// <summary>Completes on the first write that reaches the delegate, so a case awaits the
        /// write itself rather than a cell's own window.</summary>
        public TaskCompletionSource Wrote { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PerTargetReads { get; private set; }

        /// <summary>How many times the definition list and the page-wide target value list were
        /// read, for ruling C33's two guards.</summary>
        public int DefinitionReads { get; private set; }

        public int TargetValueReads { get; private set; }

        public TargetListViewModel List => _list ??= Build();

        /// <summary>
        /// The page-wide cell wiring, composed the way <c>RefreshCustomColumnSet</c> composes it, so
        /// a case can republish the SAME definitions into a row. That is the shape ruling C22's
        /// route will take: a rename or an option edit on one column republishes every definition to
        /// every row, and the rows whose own columns did not move must keep their cells.
        /// </summary>
        public CustomCellContext SameWiring() => new(
            CustomColumnSet.DashboardRow(
                _definitions,
                [.. List.Columns
                    .Where(column => CustomColumnSlug.IsCustom(column.Key) && !column.IsVisible)
                    .Select(column => column.Key)]),
            CustomColumnSet.NightExpander(_definitions),
            _ => PerTargetValuesSource?.Invoke() ?? PerTargetValues,
            Writes.Write,
            action => action(),
            Delay ?? CustomColumnTestFactory.Instant);

        public TargetRowViewModel LoadOneRow()
        {
            Load(Resolved(FirstTarget, "M 31"));
            return List.Rows[0];
        }

        public void Load(params TargetRow[] rows)
        {
            var page = new TargetListingPage(rows, rows.Length, 3_600d, 10, 1, 50);
            List.Load(page, List.ReadCustomColumns(page));
        }

        public Task SettleSessions(TargetRowViewModel row)
            => (row.PendingSessionCells ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));

        private TargetListViewModel Build()
        {
            var display = new DisplaySettings();
            display.Columns[DisplaySettings.DashboardHiddenTableId] = [.. Hidden];

            return new TargetListViewModel(
                display,
                Writer ?? new DisplayColumnWriter(() => _stored, value => _stored = value, NullLogger.Instance),
                50,
                writeDefaultPageSize: null,
                loadCustomColumns: () =>
                {
                    DefinitionReads++;
                    return _definitions;
                },
                loadTargetValues: _ =>
                {
                    TargetValueReads++;
                    return TargetValues;
                },
                loadValuesForTarget: _ =>
                {
                    PerTargetReads++;
                    if (HoldRead)
                    {
                        ReadEntered.Set();
                        ReleaseRead.Wait(TimeSpan.FromSeconds(30));
                    }

                    return PerTargetValuesSource?.Invoke() ?? PerTargetValues;
                },
                writeValue: (columnId, key, value) =>
                {
                    OnWrite?.Invoke(value);
                    var result = Writes.Write(columnId, key, value);
                    Wrote.TrySetResult();
                    return result;
                },
                post: action => action(),
                cellDelay: Delay ?? CustomColumnTestFactory.Instant);
        }
    }
}
