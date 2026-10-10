using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

// Namespace: the flat ViewModels one, deliberately, although the file sits in a TargetDetail
// folder. A GalactiLog.App.Tests.ViewModels.TargetDetail namespace shadows the
// GalactiLog.Data.Queries.TargetDetail record for every existing file in
// GalactiLog.App.Tests.ViewModels, TargetDetailViewModelTests among them, which then fails to
// compile on a type it has used since Phase 6.
namespace GalactiLog.App.Tests.ViewModels;

/// <summary>
/// Spec 12.15's session-scope cells on the Nights ledger (Phase 20 Task 6a): which columns a night
/// row draws, where their values come from, what a cell writes, and what a refresh and a close do
/// to them. No database anywhere: every delegate is a lambda and the write is
/// <c>CustomColumnTestFactory.WriteLog</c>.
/// </summary>
public class LedgerCustomCellTests
{
    [Fact]
    public void TheLedgerCells_AreTheSessionScopeColumnsInTheStoredList_InDisplayOrder()
    {
        // The gate is CustomColumnSet.LedgerRow over the FULL definition list: session scope AND
        // the stored list, in display_order. The target-scope column's slug is in the stored list
        // too, so a page that filtered on the list alone would draw it on a night row, where it has
        // no slot to write to. The two session columns are declared in the reverse of their display
        // order, so a page that kept the read's order rather than the set's is red as well.
        var notes = CustomColumnTestFactory.Define(
            "Notes tag", CustomColumnType.Text, CustomColumnScope.Session, [], order: 2);
        var done = CustomColumnTestFactory.Define(
            "Done", CustomColumnType.Boolean, CustomColumnScope.Session, [], order: 1);
        var priority = CustomColumnTestFactory.Define(
            "Priority", CustomColumnType.Dropdown, CustomColumnScope.Target, ["High"], order: 0);

        using var page = LedgerPage.Create(
            columns: [notes, done, priority],
            ledgerKeys: [notes.Slug, done.Slug, priority.Slug]).Settle();

        foreach (var card in page.Page.Sessions)
        {
            Assert.Equal(["Done", "Notes tag"], card.LedgerCells.Select(cell => cell.Column.Name));
        }
    }

    [Fact]
    public void OnAFreshProfile_EveryLedgerCellIsDrawn()
    {
        // Read off the real default rather than off a literal: custom columns ship ON on the
        // ledger, so a library that has never opened the Display tab draws every session column.
        var column = CustomColumnTestFactory.Text("Notes tag", scope: CustomColumnScope.Session);

        // No ledgerKeys at all, so the page reads display.columns.ledger_hidden as a fresh
        // document answers it, which is empty.
        using var page = LedgerPage.Create(columns: [column]).Settle();

        Assert.NotEmpty(page.Page.Sessions);
        Assert.All(page.Page.Sessions, card => Assert.Single(card.LedgerCells));
        Assert.Equal(["Notes tag"], page.Page.LedgerCustomHeadings.Select(heading => heading.Name));
    }

    [Fact]
    public void AToggleOnTheDisplayTabReachesAnOpenPage()
    {
        // Ruling C4: `ledger_hidden` is a table id on the one display.columns writer, and the page
        // follows that writer's Changed event. Red against a page that reads the display document
        // once at construction, which would leave the reader looking at a ledger with no cells
        // until they navigated away and back.
        var column = CustomColumnTestFactory.Text("Notes tag", scope: CustomColumnScope.Session);
        using var page = LedgerPage.Create(columns: [column], ledgerKeys: []).Settle();

        Assert.All(page.Page.Sessions, card => Assert.Empty(card.LedgerCells));

        page.Columns.Write(DisplaySettings.LedgerHiddenTableId, []);

        Assert.All(page.Page.Sessions, card => Assert.Single(card.LedgerCells));
        Assert.Equal(["Notes tag"], page.Page.LedgerCustomHeadings.Select(heading => heading.Name));

        // Without a reload: the toggle is an in-memory republish, not a second trip to the
        // catalogue.
        Assert.Equal(1, page.ColumnReads);
        Assert.Equal(1, page.ValueReads);
    }

    [Fact]
    public void ALibraryWithNoCustomColumn_ReadsNoValuesOnAPageLoad()
    {
        // Ruling C33: a library with no custom column pays nothing. The page read its definitions and
        // then, under a condition that tested only the target id and the delegate, issued a second
        // command that could only return nothing, on every load and every reload. Red against that
        // condition: the value read below is 1.
        using var page = LedgerPage.Create(columns: []).Settle();

        Assert.Equal(1, page.ColumnReads);
        Assert.Equal(0, page.ValueReads);
    }

    [Fact]
    public async Task ALedgerCellWritesAValueKeyedByThisTargetAndThatNight()
    {
        // Ruling C17's session-scope key shape, all four parts: the target, the night, no rig label
        // and no mosaic. A cell built with CustomValueKey.ForTarget or with the page's newest night
        // instead of its own row's is red here.
        var column = CustomColumnTestFactory.Boolean("Done", scope: CustomColumnScope.Session);
        using var page = LedgerPage.Create(columns: [column], ledgerKeys: [column.Slug]).Settle();

        var card = page.Page.Sessions.Last();
        var cell = Assert.Single(card.LedgerCells);
        cell.IsChecked = true;
        await CustomColumnTestFactory.SettleAsync(cell);

        var call = Assert.Single(page.Writes.Calls);
        Assert.Equal(column.Id, call.ColumnId);
        Assert.Equal(Factory.TargetId, call.Key.TargetId);
        Assert.Null(call.Key.MosaicId);
        Assert.Equal(card.SessionDate, call.Key.SessionDate);
        Assert.Null(call.Key.RigLabel);
        Assert.Equal(CustomColumnSlug.True, call.Value);
    }

    [Fact]
    public void EachNightsCellHoldsThatNightsStoredValue_AndARigValueNeverReachesOne()
    {
        // The read path: the map the page indexes on the full four-part key, and the per-card lookup
        // that rebuilds the same key from the card's own night. Three seeded rows on ONE column, so
        // nothing here can pass by picking the only value there is.
        //
        // Red three ways. Against a lookup keyed with CustomValueKey.ForTarget, or one that drops
        // the session date: every night then shows the same value, and the middle nights stop being
        // empty. Against a lookup that ignores the null rig label, or a page that does not partition
        // what ValuesForTarget answers: the rig row carries this target, this column and the newest
        // night, so it collides with the newest night's own slot and the cell shows the rig's value
        // instead.
        var column = CustomColumnTestFactory.Text("Notes tag", scope: CustomColumnScope.Session);
        var newest = Factory.LastSession;
        var oldest = Factory.FirstSession;

        using var page = LedgerPage.Create(
            columns: [column],
            ledgerKeys: [column.Slug],
            sessions: [Factory.Session(newest), Factory.Session(new DateOnly(2025, 6, 2)), Factory.Session(oldest)],
            values:
            [
                new CustomValueRow(column.Id, CustomValueKey.ForSession(Factory.TargetId, newest), "first light"),
                new CustomValueRow(column.Id, CustomValueKey.ForSession(Factory.TargetId, oldest), "clouded out"),

                // The rig-scope row ValuesForTarget returns in the same list, on the same target,
                // the same column and the same night as the first value above. Only its non-null
                // rig label keeps it off the ledger.
                new CustomValueRow(
                    column.Id,
                    CustomValueKey.ForRig(Factory.TargetId, newest, "RC8 / ASI2600MM"),
                    "rig only"),
            ]).Settle();

        var cells = page.Page.Sessions
            .Select(card => (card.SessionDate, Text: Assert.Single(card.LedgerCells).Text!.Text))
            .ToList();

        Assert.Equal(
            [(newest, "first light"), (new DateOnly(2025, 6, 2), ""), (oldest, "clouded out")],
            cells);

        // Nothing was written by the seeding: a stored value reaches a cell through its constructor
        // and through Reseed, never through the write delegate.
        Assert.Empty(page.Writes.Calls);
    }

    [Fact]
    public void BelowTheBreakpoint_EveryShownColumnIsStillDrawn()
    {
        // Spec.md, Nights list: fully open shows every custom column, and the divider covers what
        // does not fit. Red against a narrow page that draws none.
        var columns = new[]
        {
            CustomColumnTestFactory.Define("Notes tag", CustomColumnType.Text, CustomColumnScope.Session, [], order: 0),
            CustomColumnTestFactory.Define("Seeing", CustomColumnType.Dropdown, CustomColumnScope.Session, ["Good"], order: 1),
            CustomColumnTestFactory.Define("Done", CustomColumnType.Boolean, CustomColumnScope.Session, [], order: 2),
        };
        using var page = LedgerPage.Create(
            columns: columns, ledgerKeys: [.. columns.Select(column => column.Slug)], wide: false).Settle();

        Assert.False(page.Page.IsWide);
        Assert.Equal(["Notes tag", "Seeing", "Done"], page.Page.LedgerCustomHeadings.Select(heading => heading.Name));
        Assert.All(page.Page.Sessions, card => Assert.Equal(3, card.LedgerCells.Count));
    }

    [Fact]
    public void PastTheOldCap_EveryShownColumnIsStillDrawn()
    {
        // The old wide cap held two cells. Red against a page that still drops past it.
        var names = Enumerable.Range(1, 5).Select(index => $"Col {index}").ToList();
        var columns = names
            .Select((name, index) => CustomColumnTestFactory.Define(name, CustomColumnType.Text, CustomColumnScope.Session, [], order: index))
            .ToList();
        using var page = LedgerPage.Create(
            columns: columns, ledgerKeys: [.. columns.Select(column => column.Slug)]).Settle();

        Assert.Equal(names, page.Page.LedgerCustomHeadings.Select(heading => heading.Name));
        Assert.All(page.Page.Sessions, card => Assert.Equal(5, card.LedgerCells.Count));
    }

    [Fact]
    public void TheValuesAreReadOnceForTheWholePage()
    {
        // One ValuesForTarget read per load, whatever the ledger holds. Red against a read per
        // card, which on this ten-night ledger would be ten round trips to draw one strip, and
        // which Task 6b's rig rows would then double.
        var column = CustomColumnTestFactory.Boolean("Done", scope: CustomColumnScope.Session);
        var nights = Enumerable.Range(1, 10)
            .Select(day => Factory.Session(new DateOnly(2025, 3, day)))
            .ToList();

        using var page = LedgerPage.Create(
            columns: [column], ledgerKeys: [column.Slug], sessions: nights).Settle();

        Assert.Equal(10, page.Page.Sessions.Count);
        Assert.All(page.Page.Sessions, card => Assert.Single(card.LedgerCells));
        Assert.Equal(1, page.ValueReads);
        Assert.Equal(1, page.ColumnReads);
    }

    [Fact]
    public async Task ARefresh_ReseedsRatherThanRebuilding_AndDoesNotClobberTyping()
    {
        // The rule ruling C20 and Task 3's review both state: on a refresh, reseed the cell you
        // already have. Red against a page that rebuilds every card's group on each load, which
        // drops the open debounce window with the cell that held it and takes the half-typed value
        // and the caret with it.
        var column = CustomColumnTestFactory.Text("Notes tag", scope: CustomColumnScope.Session);
        var derived = new DerivedDataSource();
        using var page = LedgerPage.Create(
            columns: [column], ledgerKeys: [column.Slug], derivedData: derived).Settle();

        var card = page.Page.Sessions[0];
        var cell = Assert.Single(card.LedgerCells);
        cell.Text!.Text = "half typed";

        derived.Raise();
        page.Settle();

        Assert.Same(cell, Assert.Single(page.Page.Sessions[0].LedgerCells));
        Assert.Equal("half typed", cell.Text!.Text);
        Assert.Empty(page.Writes.Calls);

        // The window the refresh did not drop still commits, which is the half that proves the cell
        // is live rather than merely the same object.
        page.Delay.Release();
        await CustomColumnTestFactory.SettleAsync(cell);
        Assert.Equal(["half typed"], page.Writes.Values);
    }

    [Fact]
    public async Task ClosingThePage_DisposesEveryLedgerCell()
    {
        // A cell holds a debounce window and an IDisposable, and the page is transient: the shell
        // disposes the outgoing page on every navigation. Red against a page that leaves its cards'
        // groups alive, which would let a recycled cell write after the page is gone. Asserted
        // through behaviour rather than a flag: a disposed cell drops the write.
        var column = CustomColumnTestFactory.Boolean("Done", scope: CustomColumnScope.Session);
        var page = LedgerPage.Create(columns: [column], ledgerKeys: [column.Slug]).Settle();

        var cells = page.Page.Sessions.Select(card => Assert.Single(card.LedgerCells)).ToList();
        Assert.NotEmpty(cells);

        page.Dispose();

        foreach (var cell in cells)
        {
            cell.IsChecked = true;
            await CustomColumnTestFactory.SettleAsync(cell);
        }

        Assert.Empty(page.Writes.Calls);
    }
}

/// <summary>
/// One Target detail page wired for spec 12.15, built here rather than in
/// <c>TargetDetailViewModelTestFactory</c> because Task 6a's five new constructor arguments are not
/// that factory's to grow while five other units share the tree. Everything it does not need comes
/// from that factory's own data builders, so there is one shape of populated header, totals and
/// night in this assembly and not two.
/// </summary>
internal sealed class LedgerPage : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private LedgerPage()
    {
    }

    /// <summary>The recording write delegate every cell on this page is given.</summary>
    public CustomColumnTestFactory.WriteLog Writes { get; } = new();

    /// <summary>The debounce seam. Parked until a case releases it, so no case sleeps.</summary>
    public FakeDelay Delay { get; } = new();

    /// <summary>The one display.columns writer, so a case can drive the Display tab's own toggle.
    /// </summary>
    public DisplayColumnWriter Columns { get; private set; } = null!;

    public TargetDetailViewModel Page { get; private set; } = null!;

    /// <summary>The target_page writes, recorded rather than run, as the page factory's harness
    /// records them.</summary>
    public List<Func<DisplaySettings, DisplaySettings>> DisplayWrites { get; } = [];

    /// <summary>How many times the definition list and the value list were read. One each per page
    /// load is the contract.</summary>
    public int ColumnReads;

    public int ValueReads;

    public static LedgerPage Create(
        IReadOnlyList<CustomColumnDefinition>? columns = null,
        IReadOnlyList<CustomValueRow>? values = null,
        IReadOnlyList<string>? ledgerKeys = null,
        IReadOnlyList<SessionOverview>? sessions = null,
        DerivedDataSource? derivedData = null,
        Action<Action>? post = null,
        bool withDelegates = true,
        bool wide = true,
        TargetPageSettings? targetPage = null)
    {
        var harness = new LedgerPage();
        var logger = new RecordingLogger();
        post ??= action => action();

        // ledgerKeys names the slugs the ledger shows; the document stores the rest as hidden. Null
        // stores nothing, which is a fresh profile.
        var display = new DisplaySettings();
        if (ledgerKeys is not null)
        {
            display.Columns[DisplaySettings.LedgerHiddenTableId] =
                [.. (columns ?? []).Select(column => column.Slug).Where(slug => !ledgerKeys.Contains(slug))];
        }

        harness.Columns = new DisplayColumnWriter(() => display, _ => { }, logger);

        var shell = new ShellIntegration(_ => Task.CompletedTask, _ => null, logger);
        var graph = new GraphSettings();
        var selection = new ChartSelectionViewModel(
            graph,
            new GraphSettingsWriter(() => graph, value => graph = value),
            () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()));

        var detail = Factory.PopulatedDetail(sessions: sessions);
        var pageState = new TargetPageState(targetPage ?? new TargetPageSettings(), harness.DisplayWrites.Add);

        harness.Page = new TargetDetailViewModel(
            Factory.ResolvedGroupKey,
            _ => detail,
            (header, session, _) => new SessionCardViewModel(
                session,
                header.GroupKey,
                (_, date) => SessionCardViewModelTestFactory.PopulatedDetail(date),
                (_, _) => { },
                _ => null,
                _ => null,
                new DisplaySettings(),
                new GeneralSettings { Timezone = "UTC", Use24HTime = true },
                delay: harness.Delay.Delay,
                post: post,
                logger: logger,
                filterTint: selection.FilterTint,
                targetPage: pageState),
            (_, _) => RenameOutcome.Renamed,
            (_, _) => { },
            (_, _, _) => Task.FromResult<(bool Changed, string Message)>((false, "")),
            shell,
            selection,
            delay: harness.Delay.Delay,
            post: post,
            logger: logger,
            subscribeDerivedDataChanged: derivedData is null ? null : derivedData.Subscribe,
            unsubscribeDerivedDataChanged: derivedData is null ? null : derivedData.Unsubscribe,
            display: display,
            displayColumns: harness.Columns,
            targetPage: pageState,
            loadCustomColumns: withDelegates
                ? () =>
                {
                    Interlocked.Increment(ref harness.ColumnReads);
                    return columns ?? [];
                }
                : null,
            loadValuesForTarget: withDelegates
                ? _ =>
                {
                    Interlocked.Increment(ref harness.ValueReads);
                    return values ?? [];
                }
                : null,
            writeCustomValue: withDelegates ? harness.Writes.Write : null);

        harness._wide = wide;
        return harness;
    }

    /// <summary>Joins the in-flight load, so a case asserts against a settled page instead of
    /// sleeping. The blocking wait lives here rather than in a test method, which is what xunit's
    /// own analyzer asks for.</summary>
    public LedgerPage Settle()
    {
        Page.PendingLoad?.Wait(Budget);

        // The page's breakpoint, reported after the load rather than at construction, as the view
        // does on its first SizeChanged. It decides the Details drawer only; the Nights list draws
        // every shown custom column either side of it.
        if (_wide)
        {
            Page.ApplyWidth(TargetDetailViewModel.WideBreakpoint);
        }

        return this;
    }

    private bool _wide;

    public void Dispose() => Page.Dispose();
}
