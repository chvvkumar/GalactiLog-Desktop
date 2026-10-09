using System.Diagnostics;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.Services;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Merge;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Survey;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;

namespace GalactiLog.App.Tests.TestSupport;

// Phase 6 Task 3. The one place App.Tests builds a TargetDetailViewModel, so Task 4's and Task
// 8's constructor changes are one edit rather than twenty. No window, no dispatcher and no
// database: every query and write is a lambda, the debounce is FakeDelay, and the post seam runs
// its closure inline.
//
// Task 4 made the createCard lambda real: ViewModel.Sessions holds SessionCardViewModels, and a
// card still issues no session query until a test expands it. Tasks 5 and 8 replace the two null
// factory seams inside it.
internal static class TargetDetailViewModelTestFactory
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>The storage form of a resolved group key: upper-case TEXT, which is what
    /// Microsoft.Data.Sqlite writes for a Guid (Task 1 handoff).</summary>
    public const string ResolvedGroupKey = "10000000-0000-0000-0000-000000000000";

    public const string UnresolvedGroupKey = "obj:NGC 7331 field";

    public static readonly Guid TargetId = Guid.Parse(ResolvedGroupKey);

    public static readonly DateOnly FirstSession = new(2024, 1, 5);

    public static readonly DateOnly LastSession = new(2025, 12, 7);

    /// <summary>Every spec 12.4 header field populated, so a test can assert the block binds all
    /// of them.</summary>
    public static TargetHeaderBlock PopulatedHeader(
        string? notes = "an existing note",
        string? referenceThumbnailPath = null,
        double? referenceArcsecPerPixel = null,
        int? referenceFrameWidthPixels = null) => new(
        GroupKey: ResolvedGroupKey,
        TargetId: TargetId,
        PrimaryName: "M 31",
        Aliases: ["NGC 224", "Andromeda Galaxy"],
        ObjectType: "G",
        ObjectCategory: "Galaxy",
        Constellation: "And",
        // 00:42:44.3 and +41:16:09 in sexagesimal.
        Ra: 10.684708333333334d,
        Dec: 41.26916666666667d,
        SizeMajor: 189.1d,
        SizeMinor: 61.7d,
        PositionAngle: 145d,
        VMag: 3.44d,
        SurfaceBrightness: 22.2d,
        SacDescription: "!!! Andromeda Gx; vB, eL, eE",
        SacNotes: "Naked eye object",
        Notes: notes,
        ReferenceThumbnailPath: referenceThumbnailPath,
        ReferenceArcsecPerPixel: referenceArcsecPerPixel,
        ReferenceFrameWidthPixels: referenceFrameWidthPixels,
        NameLocked: false,
        UserDefined: false,
        CatalogMemberships:
        [
            new CatalogMembershipBadge("Messier", "31", null),
            new CatalogMembershipBadge("NGC", "224", "type=G"),
        ]);

    /// <summary>An <c>obj:</c> group: no targets row, so no target id and no catalog fields (Task
    /// 1 handoff).</summary>
    public static TargetHeaderBlock UnresolvedHeader() => new(
        GroupKey: UnresolvedGroupKey,
        TargetId: null,
        PrimaryName: "NGC 7331 field",
        Aliases: [],
        ObjectType: null,
        ObjectCategory: TargetListingCriteria.UnresolvedCategory,
        Constellation: null,
        Ra: null,
        Dec: null,
        SizeMajor: null,
        SizeMinor: null,
        PositionAngle: null,
        VMag: null,
        SurfaceBrightness: null,
        SacDescription: null,
        SacNotes: null,
        Notes: null,
        ReferenceThumbnailPath: null,
        ReferenceArcsecPerPixel: null,
        ReferenceFrameWidthPixels: null,
        NameLocked: false,
        UserDefined: false,
        CatalogMemberships: []);

    public static TargetTotals PopulatedTotals(
        int hfrArcsecExcluded = 23,
        int eccentricityExcluded = 5,
        string? eccentricitySource = "header") => new(
        IntegrationSeconds: 44_640d,
        FrameCount: 148,
        SessionCount: 2,
        FirstSessionDate: FirstSession,
        LastSessionDate: LastSession,
        AvgHfr: 2.34d,
        AvgHfrArcsec: 1.85d,
        HfrArcsecExcludedCount: hfrArcsecExcluded,
        AvgEccentricity: 0.42d,
        EccentricityModalSource: eccentricitySource,
        EccentricityExcludedCount: eccentricityExcluded,
        AvgFwhm: 1.9d,
        AvgGuidingRmsArcsec: 0.45d,
        AvgDetectedStars: 1500d,
        FiltersUsed: ["Ha", "OIII"],
        IntegrationSecondsByFilter: new Dictionary<string, double> { ["Ha"] = 29_760d, ["OIII"] = 14_880d },
        Equipment: ["ASI2600MM", "RC8"]);

    // GuidingProvenance defaults to None so every existing caller keeps the collapsed field set
    // spec 12.4 shipped before Phase 15A (task7.md section 7.2 case 8): a test that wants a
    // provenance mark uses a `with` expression rather than this factory growing a parameter for
    // one phase's cases.
    public static SessionOverview Session(DateOnly date) => new(
        SessionDate: date,
        IntegrationSeconds: 22_320d,
        FrameCount: 74,
        MedianHfr: 2.3d,
        MedianHfrArcsec: 1.8d,
        HfrArcsecExcludedCount: 0,
        MedianEccentricity: 0.4d,
        EccentricitySource: "header",
        MedianFwhm: 1.9d,
        MedianGuidingRmsArcsec: 0.45d,
        MedianDetectedStars: 1490d,
        FiltersUsed: ["Ha"],
        Camera: "ASI2600MM",
        Telescope: "RC8",
        RigCount: 1,
        HasNotes: true,
        GuidingProvenance: GuidingRmsProvenance.None);

    /// <summary>A night with none of the five medians, which Compare nights hides until shown.</summary>
    public static SessionOverview SessionWithoutMetrics(DateOnly date) => Session(date) with
    {
        MedianHfr = null,
        MedianHfrArcsec = null,
        MedianEccentricity = null,
        MedianFwhm = null,
        MedianGuidingRmsArcsec = null,
        MedianDetectedStars = null,
    };

    public static string[] FramePaths =>
    [
        @"C:\Astro\M 31\2024-01-05\frame_0001.fits",
        @"C:\Astro\M 31\2025-12-07\frame_0001.fits",
        @"C:\Astro\M 31\2025-12-07\frame_0074.fits",
    ];

    public static TargetDetail PopulatedDetail(
        TargetHeaderBlock? header = null,
        TargetTotals? totals = null,
        IReadOnlyList<SessionOverview>? sessions = null,
        IReadOnlyList<string>? framePaths = null) => new(
        header ?? PopulatedHeader(),
        totals ?? PopulatedTotals(),
        sessions ?? [Session(LastSession), Session(FirstSession)],
        framePaths ?? FramePaths);

    internal sealed class Harness : IDisposable
    {
        public FakeDelay Delay { get; } = new();

        public RecordingLogger Logger { get; } = new();

        /// <summary>Every path the shell was asked to reveal or open, as the exact
        /// <c>ProcessStartInfo</c> it built. Nothing is started.</summary>
        public List<ProcessStartInfo> Launched { get; } = [];

        public List<string> Clipboard { get; } = [];

        public List<(Guid TargetId, string Name)> Renames { get; } = [];

        public List<(Guid TargetId, string? Notes)> NoteWrites { get; } = [];

        /// <summary>Every (target id, primary name) pair the re-resolve delegate was called with.
        /// The id arrived in Phase 7 Task 7, because re-enrichment writes to that row.</summary>
        public List<(Guid TargetId, string Name)> ReResolves { get; } = [];

        /// <summary>Set when the re-resolve delegate is entered, with the thread that ran it, so
        /// a test can prove it did not run on the calling thread without releasing that thread
        /// back to the pool first.</summary>
        public ManualResetEventSlim ReResolveEntered { get; } = new();

        /// <summary>Parks the re-resolve delegate until a test releases it, so a test can observe
        /// that the command returned while the delegate was still running (F18 follow-up). Null
        /// leaves the delegate unparked, which is what every other test wants.</summary>
        public ManualResetEventSlim? ReResolveRelease { get; set; }

        public int ReResolveThread;

        // Review finding 6: there is no Cards list. ViewModel.Sessions is the page's own card
        // collection and is rebuilt per load, so a second copy that only ever grew was a trap.

        /// <summary>Every (group key, session date) pair a card's expansion query was issued for.
        /// Empty unless a test expands a card, which is the page-level half of ruling Q8.</summary>
        public List<(string GroupKey, DateOnly SessionDate)> SessionQueries { get; } = [];

        /// <summary>Every session note a card wrote, keyed by the date the card owns.</summary>
        public List<(Guid TargetId, DateOnly SessionDate, string? Notes)> SessionNoteWrites { get; } = [];

        /// <summary>How many times the detail query ran. One per load, so a scan-driven refresh
        /// is observable.</summary>
        public int Loads;

        public RenameOutcome RenameResult { get; set; } = RenameOutcome.Renamed;

        /// <summary>What the re-resolve delegate reports. The flag is the writer's "something
        /// changed", which is what makes the page reload.</summary>
        public (bool Changed, string Message) ReResolveOutcome { get; set; } =
            (true, "M 31 resolved from Offline. Updated catalog id, object type.");

        public Exception? ReResolveThrows { get; set; }

        public ScanStatusService? ScanStatus { get; init; }

        /// <summary>Every cache-relative path the reference thumbnail slot factory was asked
        /// for (Phase 8 Task 6). Empty when the header carries no stored path, which is the
        /// placeholder case.</summary>
        public List<string> ReferenceThumbnailPaths { get; } = [];

        /// <summary>Every target id the merge action opened the dialog for (Task 5).</summary>
        public List<Guid> OpenedMerges { get; } = [];

        /// <summary>Every opening of spec 12.4's Copy Frame List dialog (P14A Task 4), with the
        /// group key and the checked nights the page handed over, each carrying whatever detail
        /// the page had already loaded for it.</summary>
        public List<(string GroupKey, IReadOnlyList<FrameListNight> Nights)> OpenedFrameLists { get; } = [];

        /// <summary>Every opening of spec 12.17's Create mosaic dialog (Phase 18 Task 6), with the
        /// target id, its primary name and the checked nights.</summary>
        public List<(Guid TargetId, string TargetName, IReadOnlyList<DateOnly> Nights)> OpenedCreateMosaics { get; } = [];

        /// <summary>Every opening of spec 12.13's Export for stacking page (Phase 16 Task 5b),
        /// with the group key, the target's primary name and the checked nights the page handed
        /// over, in the ledger's own order.</summary>
        public List<(string GroupKey, string TargetName, IReadOnlyList<DateOnly> Nights)> OpenedWbppExports { get; } = [];

        /// <summary>Every (target id, category) pair spec 12.4's object type edit wrote (P14A
        /// Task 6). Recorded rather than written, so a view-model test proves what the pencil
        /// asked for with no database.</summary>
        public List<(Guid TargetId, string Category)> ObjectTypeWrites { get; } = [];

        /// <summary>What that seam completes with. False is a cancelled dialog.</summary>
        public bool MergeResult { get; set; }

        /// <summary>Every group key the merged-away probe was asked about. Empty unless a load
        /// returned null, which is the only path that calls it (ruling Q12).</summary>
        public List<string> MergedIntoProbes { get; } = [];

        /// <summary>What that probe answers. Null is "this key was not a merge", which keeps the
        /// plain stale-key callout.</summary>
        public (Guid TargetId, string PrimaryName, string LoserName)? MergedInto { get; set; }

        /// <summary>The page's merge history, when <see cref="Create"/> was asked for one. The
        /// page owns and disposes it.</summary>
        public MergeHistoryViewModel? MergeHistory => ViewModel.MergeHistory;

        /// <summary>What the page's merge history query returns.</summary>
        public IReadOnlyList<MergeHistoryRow> History { get; set; } = [];

        /// <summary>Every loser id the history's undo unmerged, and every manifest id it undid as
        /// an unresolved-name merge.</summary>
        public List<Guid> Unmerged { get; } = [];

        public List<Guid> UndidUnresolved { get; } = [];

        public UnmergeResult UnmergeOutcome { get; set; } = new(UnmergeStatus.Unmerged, 2, 1, ["NGC 224"]);

        /// <summary>Spec 5.8.3's graph document the shared chart selection reads and writes
        /// (Phase 6 Task 8).</summary>
        public GraphSettings Graph = new();

        /// <summary>Every mutation the page handed to the display writer (P13 Task 5). The
        /// mutation is recorded rather than applied, because that is what the production writer
        /// does: it applies the function to the document as loaded inside the queued write, so a
        /// test proves the mutation's shape by applying it to a document of its own.</summary>
        public List<Func<DisplaySettings, DisplaySettings>> DisplayWrites { get; } = [];

        /// <summary>The live <c>display.target_page</c> holder the page and every card it builds
        /// read and write (P13 phase review P2-1). One per harness unless a test hands its own in,
        /// which is how a page and a card are proved to agree.</summary>
        public TargetPageState TargetPage { get; internal set; } = null!;

        /// <summary>The selection the page's cross-session chart and every card's per-session
        /// chart share. Assigned by <see cref="Create"/> before the page is constructed.</summary>
        public ChartSelectionViewModel Selection { get; internal set; } = null!;

        /// <summary>The general document the page and every card it builds read (polish 2
        /// ruling 2). The card factory reads it at each construction, as AppHost's reads the live
        /// memo, so a test that changes it and raises <see cref="RaiseGeneralChanged"/> sees the new zone.</summary>
        public GeneralSettings General { get; set; } = new() { Timezone = "UTC", Use24HTime = true };

        /// <summary>The handlers the page has on <c>SettingsStore.GeneralChanged</c>.</summary>
        public List<EventHandler<GeneralSettings>> GeneralFollowers { get; } = [];

        public void RaiseGeneralChanged()
        {
            foreach (var handler in GeneralFollowers.ToArray())
            {
                handler(this, General);
            }
        }

        /// <summary>Assigned by <see cref="Create"/> immediately after construction. The
        /// view-model's own load runs on a background thread and touches only the recording lists
        /// above, so nothing reads this before it is set.</summary>
        public TargetDetailViewModel ViewModel { get; internal set; } = null!;

        /// <summary>Joins the in-flight load, so a test asserts against a settled page instead of
        /// sleeping. The blocking wait lives here rather than in a test method, which is what
        /// xunit's own analyzer asks for.</summary>
        public Harness Settle(TimeSpan? budget = null)
        {
            var wait = budget ?? Budget;
            Xunit.Assert.True(
                ViewModel.PendingLoad?.Wait(wait) ?? true,
                $"The page load did not finish within {wait}; a case run on would race its publish.");
            return this;
        }

        /// <summary>Joins every card's in-flight expansion query. Separate from
        /// <see cref="Settle"/> because a card only has one after a test expands it, and the
        /// blocking wait belongs here rather than in a test method. A settled card carrying a
        /// failure fails here unless <paramref name="failureExpected"/>, since a failed publish
        /// can leave the card only partly published.</summary>
        public Harness SettleCards(bool failureExpected = false)
        {
            foreach (var card in ViewModel.Sessions)
            {
                var finished = card.PendingLoad?.Wait(Budget) ?? true;
                Xunit.Assert.True(finished, $"The card for {card.SessionDate} did not settle within {Budget}. LastFailure: {card.LastFailure}");
                Xunit.Assert.True(
                    failureExpected || card.LastFailure is null,
                    $"The card for {card.SessionDate} settled with a failure: {card.LastFailure}");
            }

            return this;
        }

        /// <summary>Joins the merged review card's in-flight load (P25 R2), so a case asserts
        /// against its merged detail or its failure instead of sleeping.</summary>
        public Harness SettleReview()
        {
            var merged = ViewModel.MergedSession;
            Xunit.Assert.NotNull(merged);
            Xunit.Assert.True(
                merged!.PendingLoad?.Wait(Budget) ?? true,
                $"The merged card did not settle within {Budget}. LastFailure: {merged.LastFailure}");
            return this;
        }

        /// <summary>Joins the in-flight note write. Separate from <see cref="Settle"/> because a
        /// debounce window that has not been released yet would never complete.</summary>
        public void SettleNotes()
        {
            Delay.Release();
            ViewModel.Notes.PendingSave.Wait(Budget);
        }

        /// <summary>Joins the merge history's in-flight load, so a test asserts against a settled
        /// list instead of sleeping. The blocking wait lives here rather than in a test method.
        /// </summary>
        public Harness SettleHistory()
        {
            ViewModel.MergeHistory?.PendingLoad?.Wait(Budget);
            return this;
        }

        // Under the inline post a page or card load still running publishes on a pool thread into what
        // this loop enumerates, so every load is joined first. A failure raised here replaces the
        // test body's own, which is accepted: a load that never finishes is the more basic fault.
        public void Dispose()
        {
            for (var pass = 0; ; pass++)
            {
                Xunit.Assert.True(pass < 20, $"loads still start after {pass} joins at disposal");
                if (ViewModel.PendingLoad is { IsCompleted: false } page)
                {
                    Xunit.Assert.True(page.Wait(Budget), "a page load did not finish before the page was disposed");
                    continue;
                }

                if (ViewModel.Sessions.Append(ViewModel.MergedSession).Select(card => card?.PendingLoad).OfType<Task>().FirstOrDefault(load => !load.IsCompleted) is not { } load)
                {
                    break;
                }

                Xunit.Assert.True(load.Wait(Budget), "a card load did not finish before the page was disposed");
            }

            ViewModel.Dispose();
        }
    }

    /// <summary>One merge history row: a target merge, whose undo calls <c>Unmerge</c>.</summary>
    public static MergeHistoryRow HistoryRow(
        Guid? manifestId = null,
        Guid? loserId = null,
        string? loserName = "NGC 224",
        int movedFrameCount = 148,
        DateTime? mergedAt = null) => new(
        manifestId ?? Guid.NewGuid(),
        TargetId,
        "M 31",
        loserId ?? Guid.Parse("30000000-0000-0000-0000-000000000000"),
        loserName,
        mergedAt ?? new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc),
        movedFrameCount);

    /// <param name="get">The detail query. Defaults to a populated detail for any key.</param>
    /// <param name="groupKey">Defaults to the resolved key.</param>
    /// <param name="scanStatus">Passed straight through; null leaves the refresh unsubscribed.
    /// </param>
    /// <param name="withMergeHistory">Builds the page's merge history region over the harness's
    /// own recording delegates. False leaves it null, which is what every test that is not about
    /// the history wants.</param>
    /// <param name="mergedInto">What the merged-away probe answers, set before the page's own
    /// first load runs: that load is the only one a test can catch without a second trigger.
    /// </param>
    /// <param name="referenceSlot">Builds the header's reference thumbnail slot (Phase 8 Task 6).
    /// Null leaves the page with no factory at all, which is what every test that is not about the
    /// thumbnail wants; the calls are recorded either way.</param>
    /// <param name="post">How the page and its cards reach the UI thread. Defaults to running the
    /// closure inline, which is what every view-model test wants. A view test that reloads a shown
    /// page passes <c>action => Dispatcher.UIThread.Post(action)</c>: a publish touches bound
    /// controls through <c>NotifyActionState</c>, and <c>Button.Command</c> calls
    /// <c>VerifyAccess</c>, so an inline publish from the thread pool throws (P12 Task 4 fix
    /// pass).</param>
    /// <param name="targetPage">The stored <c>display.target_page</c> record (P13 R5) the
    /// harness's holder is seeded from. Null is a fresh profile.</param>
    /// <param name="pageState">A holder to use instead of the harness's own, for a test that
    /// proves a page and a card built elsewhere agree (P13 phase review P2-1). It outranks
    /// <paramref name="targetPage"/>, which then seeds nothing.</param>
    /// <param name="initialSessionDate">Spec 12.4's PAR-018 deep link (P14A Task 6): the night to
    /// open on. Null is every route that exists today and selects the newest night.</param>
    /// <param name="fullNight">Every night has a rig, the library has guide logs and the notes box
    /// is open, so every part of the target page has content to show (D19).</param>
    /// <param name="nightDetail">Each night's loaded detail; null is the populated default.</param>
    /// <param name="frameTable">Each loaded night's frame table; null leaves the table out.</param>
    /// <param name="openSurveyView">Core-shapes 2.7. Null leaves <c>OpenSurveyViewCommand</c>
    /// unable to execute, which is what every test that is not about it wants.</param>
    public static Harness Create(
        Func<string, TargetDetail?>? get = null,
        string? groupKey = null,
        ScanStatusService? scanStatus = null,
        bool withMergeHistory = false,
        (Guid TargetId, string PrimaryName, string LoserName)? mergedInto = null,
        Func<string, ThumbnailSlotViewModel>? referenceSlot = null,
        Action<Action>? post = null,
        TargetPageSettings? targetPage = null,
        TargetPageState? pageState = null,
        DateOnly? initialSessionDate = null,
        DerivedDataSource? derivedData = null,
        bool withWbppExport = true,
        GeneralSettings? general = null,
        Func<SurveyTarget, Task>? openSurveyView = null,
        bool fullNight = false,
        Func<DateOnly, SessionDetail>? nightDetail = null,
        Func<SessionDetail, object?>? frameTable = null)
    {
        // Inline, one closure at a time like the UI thread, so a card publish cannot overlap the page's.
        // Under this post a closure that disposes a card with an unsaved cell waits the flush budget,
        // so a case that proves a dispose flush uses a dispatcher or queued post.
        var uiThread = new object();
        post ??= action =>
        {
            lock (uiThread)
            {
                action();
            }
        };

        var harness = new Harness { ScanStatus = scanStatus, MergedInto = mergedInto };
        if (general is not null)
        {
            harness.General = general;
        }
        harness.TargetPage = pageState
            ?? new TargetPageState(targetPage ?? new TargetPageSettings(), harness.DisplayWrites.Add);

        var shell = new ShellIntegration(
            text =>
            {
                harness.Clipboard.Add(text);
                return Task.CompletedTask;
            },
            info =>
            {
                harness.Launched.Add(info);
                return null;
            },
            harness.Logger);

        // Phase 6 Task 8's shared chart selection, over an in-memory graph document: no database,
        // and the alias map is a value rather than the cache, so no settings read either.
        var selection = new ChartSelectionViewModel(
            harness.Graph,
            new GraphSettingsWriter(() => harness.Graph, value => harness.Graph = value),
            () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()));

        harness.Selection = selection;

        // Phase 16 Task 5b: spec 12.13's Export for stacking page. Recorded rather than opened, so
        // a test asserts which nights the command handed over with no modal host. Null when the
        // case is about a page built with no host at all, which is the state the command's body
        // guard is for.
        Func<string, string, IReadOnlyList<DateOnly>, Task>? openWbppExport = null;
        if (withWbppExport)
        {
            openWbppExport = (key, targetName, nights) =>
            {
                lock (harness.OpenedWbppExports)
                {
                    harness.OpenedWbppExports.Add((key, targetName, nights));
                }

                return Task.CompletedTask;
            };
        }

        // The one night read, for a ledger card's own load and for the merged card's member reads
        // (P25 R2), so both are recorded in SessionQueries.
        SessionDetail? ReadNight(string key, DateOnly date)
        {
            lock (harness.SessionQueries)
            {
                harness.SessionQueries.Add((key, date));
            }

            var detail = nightDetail?.Invoke(date) ?? SessionCardViewModelTestFactory.PopulatedDetail(date);
            return fullNight
                ? detail with { Rigs = [new RigGroup(0, "RC8 / ASI2600MM", "RC8", "ASI2600MM", 74, 22_320d, Guid.NewGuid(), "ref.fits", ["ref.fits"])] }
                : detail;
        }

        harness.ViewModel = new TargetDetailViewModel(
            groupKey ?? ResolvedGroupKey,
            key =>
            {
                Interlocked.Increment(ref harness.Loads);
                return get is null ? PopulatedDetail() : get(key);
            },
            // Task 4's real card factory. The card takes the loaded header because its notes
            // writer is keyed on the target id and its expansion query on the group key. A
            // non-null spec builds P25's merged card, on AppHost's rule: the spec's reader, no
            // notes field and no guiding section.
            (header, session, spec) =>
            {
                var card = new SessionCardViewModel(
                    session,
                    header.GroupKey,
                    spec?.GetDetail ?? ReadNight,
                    spec is null && header.TargetId is { } targetId
                        ? (date, notes) => harness.SessionNoteWrites.Add((targetId, date, notes))
                        : null,
                    // Task 5 still leaves the frame table null here; Task 8's chart is real, so
                    // the page-level factory exercises the production shape of that seam.
                    loaded => frameTable?.Invoke(loaded),
                    loaded => new SessionChartViewModel(loaded, selection),
                    new DisplaySettings(),
                    harness.General,
                    delay: harness.Delay.Delay,
                    post: post,
                    logger: harness.Logger,
                    // P12: the ledger row's filter dots resolve through the same lookup the chart
                    // pills use, which is the production shape of that seam.
                    filterTint: selection.FilterTint,
                    // P13 phase review P2-1 and Task 5 review P3-2: the card reads the harness's
                    // holder, the same one the page reads, so a seeded section flag reaches the
                    // card and a toggle on one card is on every other card of this page. The
                    // DisplaySettings above carries the groups only.
                    targetPage: harness.TargetPage,
                    // fullNight: a library with guide logs, so the guiding band exists.
                    anyGuideLogs: spec is null ? () => fullNight : null,
                    nights: spec?.Nights,
                    noteNights: spec?.NoteNights);

                return card;
            },
            (targetId, name) =>
            {
                harness.Renames.Add((targetId, name));
                return harness.RenameResult;
            },
            (targetId, notes) => harness.NoteWrites.Add((targetId, notes)),
            (targetId, name, _) =>
            {
                harness.ReResolves.Add((targetId, name));
                harness.ReResolveThread = Environment.CurrentManagedThreadId;
                harness.ReResolveEntered.Set();
                harness.ReResolveRelease?.Wait(Budget);
                return harness.ReResolveThrows is null
                    ? Task.FromResult(harness.ReResolveOutcome)
                    : Task.FromException<(bool Changed, string Message)>(harness.ReResolveThrows);
            },
            shell,
            selection,
            targetId =>
            {
                lock (harness.OpenedMerges)
                {
                    harness.OpenedMerges.Add(targetId);
                }

                return Task.FromResult(harness.MergeResult);
            },
            key =>
            {
                lock (harness.MergedIntoProbes)
                {
                    harness.MergedIntoProbes.Add(key);
                }

                return harness.MergedInto;
            },
            withMergeHistory
                ? _ => new MergeHistoryViewModel(
                    () => harness.History,
                    loserId =>
                    {
                        lock (harness.Unmerged)
                        {
                            harness.Unmerged.Add(loserId);
                        }

                        return harness.UnmergeOutcome;
                    },
                    manifestId =>
                    {
                        lock (harness.UndidUnresolved)
                        {
                            harness.UndidUnresolved.Add(manifestId);
                        }

                        return harness.UnmergeOutcome;
                    },
                    new GeneralSettings { Timezone = "UTC", Use24HTime = true },
                    post: post,
                    logger: harness.Logger)
                : null,
            scanStatus,
            delay: harness.Delay.Delay,
            post: post,
            logger: harness.Logger,
            createReferenceSlot: referenceSlot is null
                ? null
                : path =>
                {
                    harness.ReferenceThumbnailPaths.Add(path);
                    return referenceSlot(path);
                },
            // P13 R7 and R5: the live target_page holder, which is the one the harness hands
            // every card it builds as well (phase review P2-1). Its writer is the recorder, which
            // keeps the mutation rather than running it, so a test can apply it to whatever
            // document it wants to prove survives.
            targetPage: harness.TargetPage,
            // P14A Task 4: spec 12.4's Copy Frame List dialog. Recorded rather than opened, so a
            // test asserts which nights the command handed over and with what already loaded.
            openFrameList: (key, nights) =>
            {
                lock (harness.OpenedFrameLists)
                {
                    harness.OpenedFrameLists.Add((key, nights));
                }

                return Task.CompletedTask;
            },
            // P14A Task 6: spec 12.4's object type edit. Recorded rather than written, so a test
            // asserts the category the combo box committed with no database behind it.
            setObjectType: (targetId, category) =>
            {
                lock (harness.ObjectTypeWrites)
                {
                    harness.ObjectTypeWrites.Add((targetId, category));
                }
            },
            initialSessionDate: initialSessionDate,
            // Phase 15B fixer F2, with items 30 and 38. Null unless a test hands one over, so
            // every existing case builds the page it built before.
            subscribeDerivedDataChanged: derivedData is null ? null : derivedData.Subscribe,
            unsubscribeDerivedDataChanged: derivedData is null ? null : derivedData.Unsubscribe,
            openWbppExport: openWbppExport,
            // Polish 2 ruling 2: the page's own read of the general document and its follow of
            // the store's event, so a zone change reaches an open page in a test.
            getGeneral: () => harness.General,
            // P25 R2: the merged card's member reads go through the page's own night reader.
            getSessionDetail: ReadNight,
            subscribeGeneralChanged: handler => harness.GeneralFollowers.Add(handler),
            unsubscribeGeneralChanged: handler => harness.GeneralFollowers.Remove(handler),
            openSurveyView: openSurveyView,
            openCreateMosaic: (targetId, targetName, nights) =>
            {
                harness.OpenedCreateMosaics.Add((targetId, targetName, nights));
                return Task.CompletedTask;
            });

        return harness;
    }
}
