using GalactiLog.App.Tests.Services;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.Tests.TestSupport;

// Phase 6 Task 4. The one place App.Tests builds a SessionCardViewModel, so Task 5's and Task 8's
// factory-seam changes are one edit rather than twenty. No window, no dispatcher and no database:
// the session query and the notes write are lambdas, the debounce is FakeDelay, and the post seam
// runs its closure inline.
//
// Task 5: set Harness.FrameTableResult to your FrameTableViewModel and read FrameTableRequests.
// Task 8: the same through ChartResult and ChartRequests.
internal static class SessionCardViewModelTestFactory
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>Every field of spec 12.4's expanded card populated, including the three weather
    /// medians the seeder still leaves null (Task 2 deviation D6), so a view-model test can assert
    /// all of them without a database.</summary>
    public static SessionDetail PopulatedDetail(
        DateOnly? sessionDate = null,
        string? notes = "a session note",
        IReadOnlyList<SessionInsight>? insights = null) => new(
        GroupKey: TargetDetailViewModelTestFactory.ResolvedGroupKey,
        SessionDate: sessionDate ?? TargetDetailViewModelTestFactory.LastSession,
        FrameCount: 74,
        IntegrationSeconds: 22_320d,
        Hfr: new MetricRangeSummary(1.95d, 3.10d, 2.30d),
        MedianHfrArcsec: 1.80d,
        HfrArcsecExcludedCount: 2,
        Eccentricity: new MetricRangeSummary(0.30d, 0.55d, 0.40d),
        EccentricitySource: "header",
        EccentricityExcludedCount: 5,
        Fwhm: new MetricRangeSummary(1.60d, 2.40d, 1.90d),
        GuidingRmsArcsec: new MetricRangeSummary(0.31d, 0.72d, 0.45d),
        SensorTemp: new MetricRangeSummary(-10.5d, -9.5d, -10.0d),
        Gain: 100,
        ExposureTimes: [180d, 300d],
        // 2025-12-07 21:05 and 2025-12-08 03:40 UTC.
        FirstFrameTime: new DateTime(2025, 12, 7, 21, 5, 0, DateTimeKind.Utc),
        LastFrameTime: new DateTime(2025, 12, 8, 3, 40, 0, DateTimeKind.Utc),
        FilterMedians:
        [
            new FilterMedians("Ha", 2.28d, 0.39d, 1.88d, 0.44d, 1490d),
            new FilterMedians("OIII", 2.35d, 0.42d, 1.94d, 0.47d, 1310d),
        ],
        FilterDetails:
        [
            new FilterDetailRow("Ha", 40, 12_000d, 2.28d, 0.39d, 300d),
            new FilterDetailRow("OIII", 34, 10_320d, 2.35d, 0.42d, 180d),
        ],
        MedianAirmass: 1.23d,
        MedianAmbientTemp: 4.5d,
        MedianHumidity: 62d,
        Insights: insights ??
        [
            new SessionInsight(InsightLevel.Warning, "hfr_outliers", "4 of 74 frames are HFR outliers."),
            new SessionInsight(InsightLevel.Info, "eccentricity_outliers", "2 of 74 frames are eccentricity outliers."),
            new SessionInsight(InsightLevel.Good, "eccentricity_vs_rig", "[RC8 / ASI2600MM / Ha] eccentricity is better than the rig baseline."),
        ],
        Frames: [],
        Notes: notes);

    /// <summary>A metric group turned off, for the assertions that the gate governs the frame
    /// table's columns and nothing on the card (review findings 1 and 2). Everything else keeps
    /// spec 5.8.2's defaults.</summary>
    public static DisplaySettings DisplayWithout(params string[] disabledGroups)
    {
        var display = new DisplaySettings();
        foreach (var group in disabledGroups)
        {
            display.Groups[group] = display.Groups[group] with { Enabled = false };
        }

        return display;
    }

    internal sealed class DisposableStub : IDisposable
    {
        public int Disposals { get; private set; }

        public void Dispose() => Disposals++;
    }

    internal sealed class Harness : IDisposable
    {
        public FakeDelay Delay { get; } = new();

        public RecordingLogger Logger { get; } = new();

        /// <summary>How many times the session detail query ran. The whole point of the laziness
        /// assertions (ruling Q8).</summary>
        public int Queries;

        /// <summary>The thread the query ran on, and an event a test can block on so the calling
        /// thread is still occupied and cannot have been reused for it.</summary>
        public int QueryThread;

        public ManualResetEventSlim QueryEntered { get; } = new();

        /// <summary>Set by default. Resetting it parks the query inside the delegate, which is how
        /// the double-start guard is observable.</summary>
        public ManualResetEventSlim QueryGate { get; } = new(initialState: true);

        /// <summary>The (group key, session date) pairs the query was asked for, so a test can
        /// prove the card named its own night.</summary>
        public List<(string GroupKey, DateOnly SessionDate)> QueryArguments { get; } = [];

        public List<(DateOnly SessionDate, string? Notes)> NoteWrites { get; } = [];

        /// <summary>P13 R5's display-document writes, in order, as the mutations the holder queued.
        /// A test applies one to a document of its own choosing, which is what proves the write is
        /// a nested <c>with</c> expression rather than a snapshot.</summary>
        public List<Func<DisplaySettings, DisplaySettings>> DisplayWrites { get; } = [];

        /// <summary>The live <c>display.target_page</c> holder this card reads and writes
        /// (P13 phase review P2-1). One per harness unless a test hands two harnesses the same
        /// one, which is how two nights are proved to share a toggle.</summary>
        public TargetPageState TargetPage { get; internal set; } = null!;

        /// <summary>P14A PAR-008: the frame paths the card's thumbnail strip asked for, in order.
        /// Empty when the harness was built without a thumbnail factory, which is the default and
        /// is what a test that is not about thumbnails wants.</summary>
        public List<string> ThumbnailRequests { get; } = [];

        /// <summary>Phase 15B: the (night, rig filter) pairs the Guiding section's night query was
        /// asked for, in order. Empty is the whole point of spec 12.4's staging rule, so a case
        /// asserts against this list rather than against what is on screen.</summary>
        public List<(DateOnly Night, string? Telescope)> GuidingRequests { get; } = [];

        /// <summary>What the Guiding section's night query returns. The empty rollup by default,
        /// which is what a night with no guiding session answers.</summary>
        public Phd2NightGuiding GuidingResult { get; set; } = new(new Phd2NightSummary(), []);

        /// <summary>What the night query returns for a given rig filter, when a case needs the
        /// answer to depend on the argument. Null falls back to <see cref="GuidingResult"/>, which
        /// is what every case that is not about the rig narrowing wants.</summary>
        public Func<string?, Phd2NightGuiding>? GuidingResultFor { get; set; }

        /// <summary>Set inside the night query before it parks, in <see cref="QueryEntered"/>'s
        /// shape: a case that means to change the section's state while the read is in flight
        /// waits on this rather than on the read having started by luck.</summary>
        public ManualResetEventSlim GuidingEntered { get; } = new();

        /// <summary>Set by default. Resetting it parks the night query inside the delegate, which
        /// is how a publish that lands while the band is closed is reachable at all.</summary>
        public ManualResetEventSlim GuidingGate { get; } = new(initialState: true);

        /// <summary>Thrown by the Guiding section's night query when set, for spec 12.4's failed
        /// state.</summary>
        public Exception? GuidingThrows { get; set; }

        /// <summary>Joins the Guiding section's in-flight night query, so a case asserts against a
        /// settled section instead of sleeping.</summary>
        public Harness SettleGuiding()
        {
            Card.Guiding?.PendingLoad?.Wait(Budget);
            return this;
        }

        /// <summary>Phase 15B Task 4b: the session ids the guide graph's frames read was asked
        /// for, in order. Counted at the real read, behind the section's drawable-only guard.
        /// </summary>
        public List<Guid> FrameRequests { get; } = [];

        /// <summary>What the frames read answers. Null by default. May block or throw.</summary>
        public Func<Guid, Phd2SessionFrames?> FramesResult { get; set; } = _ => null;

        /// <summary>Joins the guide graph's in-flight frames read. The post seam is synchronous
        /// here, so the publication has run when this returns.</summary>
        public Harness SettleFrames()
        {
            Card.Guiding?.Graph?.PendingLoad.Wait(Budget);
            return this;
        }

        public List<SessionDetail> FrameTableRequests { get; } = [];

        public List<SessionDetail> ChartRequests { get; } = [];

        /// <summary>What the frame table factory hands back. Null until Task 5 lands, which is
        /// also the production value.</summary>
        public object? FrameTableResult { get; set; }

        public object? ChartResult { get; set; }

        /// <summary>What the query returns. Null models a night a scan removed between the page
        /// load and the expansion (Task 2 handoff).</summary>
        public SessionDetail? Detail { get; set; }

        public Exception? Throws { get; set; }

        public SessionCardViewModel Card { get; internal set; } = null!;

        /// <summary>Joins the in-flight expansion query, so a test asserts against a settled card
        /// instead of sleeping. The blocking wait lives here rather than in a test method, which is
        /// what xunit's own analyzer asks for.</summary>
        public Harness Settle()
        {
            Card.PendingLoad?.Wait(Budget);
            return this;
        }

        /// <summary>Releases the autosave debounce window and joins the write.</summary>
        public void SettleNotes()
        {
            Delay.Release();
            Card.Notes?.PendingSave.Wait(Budget);
        }

        public bool WaitForQuery() => QueryEntered.Wait(Budget);

        public void Dispose() => Card.Dispose();
    }

    /// <param name="overview">Defaults to the shared single-rig session overview.</param>
    /// <param name="detail">What the expansion query returns. Defaults to the populated detail.
    /// </param>
    /// <param name="withNotes">False models an <c>obj:</c> group: no target id, so no notes
    /// writer and no notes box.</param>
    /// <param name="display">Defaults to spec 5.8.2's shipped document, which leaves weather and
    /// mount disabled. The card's body does not read it, so the default is the real one.</param>
    /// <param name="general">Defaults to UTC and 24 hour time, so the frame-time assertions are
    /// machine independent.</param>
    /// <param name="targetPage">The live <c>display.target_page</c> holder (P13 phase review
    /// P2-1). Null builds one per harness, seeded from <paramref name="display"/> and recording
    /// its writes into <see cref="Harness.DisplayWrites"/>. Two harnesses handed the same holder
    /// model two nights of one page, which is the shape the production host registers.</param>
    public static Harness Create(
        SessionOverview? overview = null,
        SessionDetail? detail = null,
        bool withNotes = true,
        DisplaySettings? display = null,
        GeneralSettings? general = null,
        string? groupKey = null,
        TargetPageState? targetPage = null,
        Func<string, ThumbnailSlotViewModel>? createFrameThumbnail = null,
        bool anyGuideLogs = false,
        IReadOnlyList<DateOnly>? nights = null,
        IReadOnlyList<SessionCardViewModel>? noteNights = null)
    {
        var harness = new Harness
        {
            Detail = detail ?? PopulatedDetail(),
        };

        harness.TargetPage = targetPage ?? new TargetPageState(
            (display ?? new DisplaySettings()).TargetPage,
            harness.DisplayWrites.Add);

        harness.Card = new SessionCardViewModel(
            overview ?? TargetDetailViewModelTestFactory.Session(TargetDetailViewModelTestFactory.LastSession),
            groupKey ?? TargetDetailViewModelTestFactory.ResolvedGroupKey,
            (key, date) =>
            {
                Interlocked.Increment(ref harness.Queries);
                harness.QueryThread = Environment.CurrentManagedThreadId;
                lock (harness.QueryArguments)
                {
                    harness.QueryArguments.Add((key, date));
                }

                harness.QueryEntered.Set();
                harness.QueryGate.Wait(Budget);

                if (harness.Throws is not null)
                {
                    throw harness.Throws;
                }

                return harness.Detail;
            },
            withNotes
                ? (date, notes) => harness.NoteWrites.Add((date, notes))
                : null,
            loaded =>
            {
                harness.FrameTableRequests.Add(loaded);
                return harness.FrameTableResult;
            },
            loaded =>
            {
                harness.ChartRequests.Add(loaded);
                return harness.ChartResult;
            },
            display ?? new DisplaySettings(),
            general ?? new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            delay: harness.Delay.Delay,
            post: action => action(),
            logger: harness.Logger,
            targetPage: harness.TargetPage,
            createFrameThumbnail: createFrameThumbnail is null
                ? null
                : path =>
                {
                    harness.ThumbnailRequests.Add(path);
                    return createFrameThumbnail(path);
                },
            // Phase 15B: the Guiding section's night query, always counting and never running
            // until the band is open on a shown card, which is what the staging cases assert.
            getGuiding: (night, telescope) =>
            {
                lock (harness.GuidingRequests)
                {
                    harness.GuidingRequests.Add((night, telescope));
                }

                harness.GuidingEntered.Set();
                harness.GuidingGate.Wait(Budget);

                if (harness.GuidingThrows is not null)
                {
                    throw harness.GuidingThrows;
                }

                return harness.GuidingResultFor is { } perRig
                    ? perRig(telescope)
                    : harness.GuidingResult;
            },
            // Spec 12.4's library-wide EXISTS. False by default, which is the library every case
            // written before this phase describes: no guide log, so no band.
            anyGuideLogs: () => anyGuideLogs,
            getFrames: id =>
            {
                lock (harness.FrameRequests)
                {
                    harness.FrameRequests.Add(id);
                }

                return harness.FramesResult(id);
            },
            // P25: a merged card over several member nights (core-shapes 1.4).
            nights: nights,
            noteNights: noteNights);

        return harness;
    }
}
