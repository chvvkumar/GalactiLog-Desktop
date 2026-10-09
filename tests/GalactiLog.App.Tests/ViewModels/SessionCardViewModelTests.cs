using System.Globalization;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Xunit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Page = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 18.3's view-model tests for spec 12.4's night, which since P12 R10 retired the
// session accordion card is the ledger row plus the session pane, and the project
// rule assigns the laziness assertions to: a query object cannot assert its own
// non-invocation, so "the session detail query is issued when a card expands, not up front" is
// proved here with a counting delegate.
public class SessionCardViewModelTests
{
    private static readonly DateTime FirstFrameUtc = new(2025, 12, 7, 21, 5, 0, DateTimeKind.Utc);

    [Fact]
    public void Collapsed_RendersEveryCollapsedField()
    {
        // The roadmap Verify line's first half: every field of spec 12.4's collapsed list, from
        // the SessionOverview alone.
        using var harness = Cards.Create();
        var card = harness.Card;

        Assert.Equal(Page.LastSession, card.SessionDate);
        Assert.Equal("2025-12-07", card.SessionDateText);
        Assert.Equal("6.2 h", card.IntegrationText);
        Assert.Equal(74, card.FrameCount);
        Assert.Equal("74", card.FrameCountText);
        Assert.Equal("2.30 px", card.MedianHfrText);
        Assert.Equal("1.80 arcsec", card.MedianHfrArcsecText);
        Assert.True(card.HasHfrArcsec);
        Assert.Equal("0.40", card.MedianEccentricityText);
        Assert.Equal("1.90 arcsec", card.MedianFwhmText);
        Assert.Equal("0.45 arcsec", card.MedianGuidingRmsText);
        Assert.Equal("1,490", card.MedianDetectedStarsText);

        // The joined filter names went with the round 2 sweep; the swatches are what the ledger
        // row and the pane render.
        Assert.Equal(["Ha"], card.FilterSwatches.Select(swatch => swatch.FilterName));
    }

    [Fact]
    public void Collapsed_AbsentMetrics_RenderEmptyRatherThanZero()
    {
        // Null means "no frame carried this metric", never zero (Task 1 handoff).
        var sparse = Page.Session(Page.LastSession) with
        {
            MedianHfrArcsec = null,
            MedianEccentricity = null,
            EccentricitySource = null,
            MedianGuidingRmsArcsec = null,
            Camera = null,
            Telescope = null,
            FiltersUsed = [],
            HasNotes = false,
        };

        using var harness = Cards.Create(overview: sparse);
        var card = harness.Card;

        Assert.Equal("", card.MedianHfrArcsecText);
        Assert.False(card.HasHfrArcsec);
        Assert.Equal("", card.MedianEccentricityText);
        Assert.Equal("", card.MedianGuidingRmsText);
        Assert.Empty(card.FilterSwatches);
    }

    [Fact]
    public void Collapsed_IssuesNoQuery()
    {
        // The assertion that actually proves spec 12.4's "not up front": there is
        // no code path from construction to a query.
        using var harness = Cards.Create();

        Assert.Equal(0, harness.Queries);
        Assert.Null(harness.Card.Detail);
        Assert.Null(harness.Card.PendingLoad);
        Assert.False(harness.Card.IsLoading);
        Assert.Empty(harness.Card.FilterRows);
        Assert.Empty(harness.Card.Ranges);
        Assert.Empty(harness.Card.OutlierPills);
    }

    [Fact]
    public void Expanding_IssuesSessionDetailQueryExactlyOnce()
    {
        // The roadmap Verify line's second half.
        using var harness = Cards.Create();
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Equal(1, harness.Queries);
        Assert.NotNull(harness.Card.Detail);
        Assert.False(harness.Card.IsLoading);

        // And it named the group and the night the page named, not a rebuilt key.
        Assert.Equal((Page.ResolvedGroupKey, Page.LastSession), Assert.Single(harness.QueryArguments));
    }

    [Fact]
    public void Expanding_Collapsing_AndExpandingAgain_StillIssuesOneQuery()
    {
        // Collapsing keeps the loaded detail, so re-expanding is
        // free: the user who toggles a card four times pays for one read.
        using var harness = Cards.Create();
        var card = harness.Card;

        card.IsExpanded = true;
        harness.Settle();
        card.IsExpanded = false;
        card.IsExpanded = true;
        harness.Settle();

        Assert.Equal(1, harness.Queries);
        Assert.NotNull(card.Detail);
    }

    [Fact]
    public void Expanding_Twice_Rapidly_IssuesOneQuery()
    {
        // The double-start guard. The query is parked inside the delegate, so the second
        // expansion arrives while the first read is genuinely in flight, which is the race a
        // Detail-is-null check alone would lose.
        using var harness = Cards.Create();
        harness.QueryGate.Reset();
        var card = harness.Card;

        card.IsExpanded = true;
        Assert.True(harness.WaitForQuery(), "the session query never ran");
        card.IsExpanded = false;
        card.IsExpanded = true;

        harness.QueryGate.Set();
        harness.Settle();

        Assert.Equal(1, harness.Queries);
        Assert.NotNull(card.Detail);
    }

    // F18 follow-up. SessionDetailQuery.Get is a synchronous SQLite read whose first call after a
    // scan also pays the rig baseline rebuild (Task 2 handoff), so it must not run on the thread
    // that expanded the card. Proven by parking the query and observing that the expanding thread
    // carried on, not by comparing thread ids: that comparison is only sound while the expanding
    // thread stays occupied, and occupying it means a blocking wait that needs the pool to make
    // progress at the same time (TRACKING section 2 item 8).
    [Fact]
    public async Task Expanding_RunsTheQueryOffTheCallingThread()
    {
        using var harness = Cards.Create();
        harness.QueryGate.Reset();

        harness.Card.IsExpanded = true;

        // Setting IsExpanded returned while the query is parked, and nothing has published.
        Assert.False(harness.Card.PendingLoad!.IsCompleted);
        Assert.Null(harness.Card.Detail);

        harness.QueryGate.Set();
        await harness.Card.PendingLoad!;

        Assert.Equal(1, harness.Queries);
        Assert.NotNull(harness.Card.Detail);
    }

    [Fact]
    public void Expanding_PublishesTheFilterRowsRangesAndOutlierPills()
    {
        using var harness = Cards.Create();
        harness.Card.IsExpanded = true;
        harness.Settle();
        var card = harness.Card;

        Assert.NotNull(card.Detail);

        // The merged filter table is what the pane renders; the two projected lists it replaced
        // (FilterMedians and FilterDetails, with their two view model types) were bound by no view
        // and went with the retired card (phase review P2-4).
        Assert.Equal(["Ha", "OIII"], card.FilterRows.Where(row => !row.IsSubRow).Select(row => row.FilterName));
        Assert.Equal("2.28", card.FilterRows[0].MedianHfrText);
        Assert.Equal("1,490", card.FilterRows[0].MedianDetectedStarsText);
        Assert.Equal("40", card.FilterRows[0].FrameCountText);
        Assert.Equal("3.3", card.FilterRows[0].IntegrationText);
        Assert.Equal("300", card.FilterRows[0].ExposureTimeText);

        // P24 R22: the insights reach no view; the outlier pills come from the frames' own values
        // and flags, and the populated detail carries no frame, so there is no pill.
        Assert.Empty(card.OutlierPills);

        Assert.Equal("1.95", card.Ranges[0].MinText);
        Assert.Equal("3.10", card.Ranges[0].MaxText);
        Assert.Equal("2.30", card.Ranges[0].MedianText);

        Assert.Equal("100", card.GainText);
        Assert.Equal("180 s, 300 s", card.ExposureTimesText);

        // The three weather medians of spec 12.4's fourth bullet.
        Assert.Equal("1.23", card.MedianAirmassText);
        Assert.Equal("4.5 C", card.MedianAmbientTempText);
        Assert.Equal("62 %", card.MedianHumidityText);

        // The two factory seams got the same loaded detail, once each.
        Assert.Same(card.Detail, Assert.Single(harness.FrameTableRequests));
        Assert.Same(card.Detail, Assert.Single(harness.ChartRequests));
    }

    [Fact]
    public void Expanding_AbsentRange_TakesItsCellOutOfTheList()
    {
        // Every MetricRangeSummary is all-null or all-populated (Task 2 handoff), so an unmeasured
        // metric is one absent cell rather than three empty figures.
        var detail = Cards.PopulatedDetail() with
        {
            GuidingRmsArcsec = new MetricRangeSummary(null, null, null),
        };

        using var harness = Cards.Create(detail: detail);
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.DoesNotContain("Guiding " + TableHeads.Rms, harness.Card.Ranges.Select(range => range.Label));
    }

    [Fact]
    public void Expanding_NullDetail_LeavesTheBodyEmptyWithoutThrowing()
    {
        // A scan removed the night between the page load and the expansion (Task 2 handoff): an
        // empty body, not an error page.
        using var harness = Cards.Create(detail: null);
        harness.Detail = null;
        harness.Card.IsExpanded = true;
        harness.Settle();
        var card = harness.Card;

        Assert.Equal(1, harness.Queries);
        Assert.Null(card.Detail);
        Assert.Null(card.LastFailure);
        Assert.False(card.IsLoading);
        Assert.Empty(card.FilterRows);
        Assert.Empty(card.OutlierPills);
        Assert.Empty(card.Ranges);
        Assert.Empty(harness.FrameTableRequests);
    }

    [Fact]
    public void Expanding_Throwing_SetsLastFailure_AndKeepsTheCollapsedFields()
    {
        using var harness = Cards.Create();
        harness.Throws = new InvalidOperationException("the database is locked");
        harness.Card.IsExpanded = true;
        harness.Settle();
        var card = harness.Card;

        Assert.NotNull(card.LastFailure);
        Assert.True(card.HasFailure);
        Assert.False(card.IsLoading);

        // The collapsed field set came from the overview and never needed this query, so a failed
        // session does not blank the card, and it does not take the page down either.
        Assert.Equal("2025-12-07", card.SessionDateText);
        Assert.Equal("2.30 px", card.MedianHfrText);

        // Logged rather than swallowed.
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void Expanding_AfterAFailure_RetriesOnTheNextExpansion()
    {
        using var harness = Cards.Create();
        harness.Throws = new InvalidOperationException("the database is locked");
        harness.Card.IsExpanded = true;
        harness.Settle();

        harness.Throws = null;
        harness.Card.IsExpanded = false;
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Equal(2, harness.Queries);
        Assert.NotNull(harness.Card.Detail);
        Assert.Null(harness.Card.LastFailure);
    }

    [Fact]
    public void Invalidate_ThenExpanding_ReQueries()
    {
        // The page calls this after a scan finishes: the night may have gained frames.
        using var harness = Cards.Create();
        var card = harness.Card;

        card.IsExpanded = true;
        harness.Settle();
        card.IsExpanded = false;

        card.Invalidate();
        Assert.Equal(1, harness.Queries);
        Assert.Null(card.Detail);
        Assert.Empty(card.FilterRows);
        Assert.Equal("", card.MedianAirmassText);

        // Review finding 7: the indicator belongs to the load that was abandoned.
        Assert.False(card.IsLoading);

        card.IsExpanded = true;
        harness.Settle();

        Assert.Equal(2, harness.Queries);
        Assert.NotNull(card.Detail);
    }

    [Fact]
    public void Invalidate_WhileExpanded_ReloadsImmediately()
    {
        using var harness = Cards.Create();
        var card = harness.Card;

        card.IsExpanded = true;
        harness.Settle();

        card.Invalidate();
        harness.Settle();

        Assert.Equal(2, harness.Queries);
        Assert.NotNull(card.Detail);
        Assert.NotEmpty(card.FilterRows);
    }

    [Fact]
    public void Invalidate_DisposesTheFrameTableAndTheChart()
    {
        // The projections are rebuilt from the new read; the old children would otherwise leak a
        // query and a chart per scan.
        var frames = new Cards.DisposableStub();
        var chart = new Cards.DisposableStub();
        using var harness = Cards.Create();
        harness.FrameTableResult = frames;
        harness.ChartResult = chart;

        harness.Card.IsExpanded = true;
        harness.Settle();
        Assert.Same(frames, harness.Card.Frames);

        harness.FrameTableResult = null;
        harness.ChartResult = null;
        harness.Card.Invalidate();
        harness.Settle();

        Assert.Equal(1, frames.Disposals);
        Assert.Equal(1, chart.Disposals);
    }

    [Fact]
    public void Notes_AutosaveFiresOnceAfterTheDebounceWindow()
    {
        // Task 3's AutosaveField unchanged: the same one second window, asserted here so a second
        // debounce with a different figure cannot appear in this file (collision-map owners).
        using var harness = Cards.Create();
        var notes = Assert.IsType<AutosaveField>(harness.Card.Notes);

        notes.Text = "seeing dropped after midnight";
        notes.Text = "seeing dropped after midnight, guiding recovered";
        harness.SettleNotes();

        var write = Assert.Single(harness.NoteWrites);
        Assert.Equal("seeing dropped after midnight, guiding recovered", write.Notes);
        Assert.Contains(AutosaveField.IdleWindow, harness.Delay.Requested);
    }

    [Fact]
    public void Notes_SaveTargetsTheSessionDate()
    {
        // The writer is partially applied to the target id; the date is the card's own, so two
        // cards of one target cannot write each other's note.
        using var harness = Cards.Create(overview: Page.Session(Page.FirstSession));
        harness.Card.Notes!.Text = "a note";
        harness.SettleNotes();

        Assert.Equal(Page.FirstSession, Assert.Single(harness.NoteWrites).SessionDate);
    }

    [Fact]
    public void Notes_UnresolvedGroup_AreAbsent()
    {
        // An obj: group has no target id to key a session note on, so the box is absent rather
        // than silently discarding what is typed into it.
        using var harness = Cards.Create(withNotes: false);

        Assert.Null(harness.Card.Notes);

        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.NotNull(harness.Card.Detail);
        Assert.Empty(harness.NoteWrites);
    }

    [Fact]
    public void Notes_ReseededFromTheLoadedDetail()
    {
        // The box exists before the first expansion and holds nothing until the detail arrives.
        using var harness = Cards.Create();
        Assert.Equal("", harness.Card.Notes!.Text);

        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Equal("a session note", harness.Card.Notes.Text);
        Assert.False(harness.Card.Notes.IsDirty);
        Assert.Empty(harness.NoteWrites);
    }

    [Fact]
    public void Notes_ReseedNeverClobbersInProgressTyping()
    {
        using var harness = Cards.Create();
        harness.Card.Notes!.Text = "half typed";

        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Equal("half typed", harness.Card.Notes.Text);
    }

    [Fact]
    public void Ranges_AreInTheSpecifiedOrder()
    {
        // Spec 12.4's order for the session ranges block. Gain, the exposure times and the two
        // frame times follow as their own cells because they are not ranges.
        using var harness = Cards.Create();
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Equal(
            [TableHeads.Hfr, "Eccentricity", TableHeads.Fwhm, "Guiding " + TableHeads.Rms, "Sensor temp C"],
            harness.Card.Ranges.Select(range => range.Label));
    }

    [Fact]
    public void FrameTimes_HonourGeneralTimezoneAndUse24hTime()
    {
        // Spec 5.8.1's two keys, and the one helper Task 5's Time column reuses: two renderings
        // of the same instant on the same screen is the failure this owns.
        using var utcTwentyFour = Cards.Create(
            general: new GeneralSettings { Timezone = "UTC", Use24HTime = true });
        utcTwentyFour.Card.IsExpanded = true;
        utcTwentyFour.Settle();

        Assert.Equal("21:05", utcTwentyFour.Card.FirstFrameTimeText);
        Assert.Equal("03:40", utcTwentyFour.Card.LastFrameTimeText);

        using var utcTwelve = Cards.Create(
            general: new GeneralSettings { Timezone = "UTC", Use24HTime = false });
        utcTwelve.Card.IsExpanded = true;
        utcTwelve.Settle();

        Assert.Equal("9:05 PM", utcTwelve.Card.FirstFrameTimeText);
        Assert.Equal("3:40 AM", utcTwelve.Card.LastFrameTimeText);

        // And the configured zone is actually applied rather than ignored: any zone with a
        // non-zero fixed offset must move the clock off the UTC reading. Discovered from the
        // machine's own database so the assertion holds on any host.
        var offsetZone = TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(
            zone => zone.BaseUtcOffset != TimeSpan.Zero && !zone.SupportsDaylightSavingTime);
        Assert.NotNull(offsetZone);

        using var shifted = Cards.Create(
            general: new GeneralSettings { Timezone = offsetZone.Id, Use24HTime = true });
        shifted.Card.IsExpanded = true;
        shifted.Settle();

        Assert.NotEqual("21:05", shifted.Card.FirstFrameTimeText);
    }

    [Fact]
    public void FrameTimes_UnknownTimezoneId_FallsBackToLocal()
    {
        // A settings document carried over from another machine must not take a card down.
        var expected = TimeZoneInfo
            .ConvertTimeFromUtc(FirstFrameUtc, TimeZoneInfo.Local)
            .ToString("HH:mm", CultureInfo.InvariantCulture);

        using var unknown = Cards.Create(
            general: new GeneralSettings { Timezone = "Not/AZone", Use24HTime = true });
        unknown.Card.IsExpanded = true;
        unknown.Settle();

        Assert.Equal(expected, unknown.Card.FirstFrameTimeText);

        using var empty = Cards.Create(
            general: new GeneralSettings { Timezone = "", Use24HTime = true });
        empty.Card.IsExpanded = true;
        empty.Settle();

        Assert.Equal(expected, empty.Card.FirstFrameTimeText);
    }

    [Fact]
    public void FrameTimes_AbsentCaptureTime_RendersEmpty()
    {
        // FirstFrameTime and LastFrameTime are null independently of each other when a frame
        // carries no capture_date (Task 2 handoff).
        var detail = Cards.PopulatedDetail() with { LastFrameTime = null };

        using var harness = Cards.Create(detail: detail);
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Equal("21:05", harness.Card.FirstFrameTimeText);
        Assert.Equal("", harness.Card.LastFrameTimeText);
    }

    [Fact]
    public void DisabledMetricGroup_HidesNothingOnTheCardBody()
    {
        // Review findings 1 and 2. Spec 12.4 states "a group turned off hides its columns
        // everywhere" inside its Frame table paragraph, so the gate governs the frame table's
        // columns (Task 5) and nothing on this card: every range the session carries is built and
        // all three weather medians are shown.
        using var harness = Cards.Create(
            display: Cards.DisplayWithout("quality", "guiding", "weather", "mount"));
        harness.Card.IsExpanded = true;
        harness.Settle();
        var card = harness.Card;

        Assert.Equal(
            [TableHeads.Hfr, "Eccentricity", TableHeads.Fwhm, "Guiding " + TableHeads.Rms, "Sensor temp C"],
            card.Ranges.Select(range => range.Label));
        Assert.Equal("1.23", card.MedianAirmassText);
        Assert.Equal("4.5 C", card.MedianAmbientTempText);
        Assert.Equal("62 %", card.MedianHumidityText);
    }

    [Fact]
    public void ShippedDisplayDefaults_ShowTheWholeCardBody()
    {
        // The default document (spec 5.8.2) leaves weather and mount disabled, which is exactly
        // the case that used to blank spec 12.4's fourth expanded bullet on a fresh install.
        using var harness = Cards.Create(display: new DisplaySettings());
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Equal("1.23", harness.Card.MedianAirmassText);
        Assert.Equal("4.5 C", harness.Card.MedianAmbientTempText);
        Assert.Equal("62 %", harness.Card.MedianHumidityText);
        Assert.Equal(5, harness.Card.Ranges.Count);

        // And the document itself is still reachable, for Task 5's frame table columns.
        Assert.False(harness.Card.Display.Groups["weather"].Enabled);
    }

    [Fact]
    public void Refresh_KeepsTheCardAndRepublishesTheCollapsedFields()
    {
        // Review finding 3's card half: the page hands a kept card the fresh overview, and every
        // collapsed projection follows one property change rather than staying at the old figure.
        using var harness = Cards.Create();
        harness.Card.IsExpanded = true;
        harness.Settle();
        Assert.NotNull(harness.Card.Detail);

        var changes = new List<string>();
        harness.Card.PropertyChanged += (_, args) => changes.Add(args.PropertyName ?? "");

        harness.Card.Refresh(
            Page.Session(Page.LastSession) with { FrameCount = 91, MedianHfr = 2.5d, RigCount = 2 });
        harness.Settle();

        Assert.Equal("91", harness.Card.FrameCountText);
        Assert.Equal("2.50 px", harness.Card.MedianHfrText);
        Assert.Equal("2.50", harness.Card.LedgerHfrText);
        Assert.Contains(nameof(SessionCardViewModel.FrameCountText), changes);
        Assert.Contains(nameof(SessionCardViewModel.LedgerHfrText), changes);

        // Still expanded, so it re-read exactly once more.
        Assert.True(harness.Card.IsExpanded);
        Assert.Equal(2, harness.Queries);
        Assert.NotNull(harness.Card.Detail);
    }

    [Fact]
    public void Refresh_WhileCollapsed_IssuesNoQuery()
    {
        using var harness = Cards.Create();

        harness.Card.Refresh(Page.Session(Page.LastSession) with { FrameCount = 91 });

        Assert.Equal(0, harness.Queries);
        Assert.Null(harness.Card.Detail);
        Assert.False(harness.Card.IsLoading);
        Assert.Equal("91", harness.Card.FrameCountText);
    }

    [Fact]
    public void Dispose_DisposesTheNotesFieldAndTheFrameTable()
    {
        var frames = new Cards.DisposableStub();
        var chart = new Cards.DisposableStub();
        var harness = Cards.Create();
        harness.FrameTableResult = frames;
        harness.ChartResult = chart;

        harness.Card.IsExpanded = true;
        harness.Settle();

        var notes = harness.Card.Notes!;
        var windowsBefore = harness.Delay.Requested.Count;

        harness.Card.Dispose();

        Assert.True(harness.Card.IsDisposed);
        Assert.Equal(1, frames.Disposals);
        Assert.Equal(1, chart.Disposals);

        // A disposed AutosaveField opens no further debounce window, which is how its own
        // disposal is observable from outside.
        notes.Text = "typed after the card closed";
        Assert.Equal(windowsBefore, harness.Delay.Requested.Count);
        Assert.Empty(harness.NoteWrites);

        // Idempotent: the page disposes its cards on every load and again on close.
        harness.Card.Dispose();
        Assert.Equal(1, frames.Disposals);
    }

    [Fact]
    public void Dispose_FlushesAnUnsavedNote()
    {
        // A note typed and immediately navigated away from must not be lost. The page flushes its
        // own note and then disposes the cards, which is where this runs (Task 3 handoff).
        var harness = Cards.Create();
        harness.Card.Notes!.Text = "typed and left";

        harness.Card.Dispose();

        Assert.Equal("typed and left", Assert.Single(harness.NoteWrites).Notes);
    }

    [Fact]
    public void Dispose_WhileALoadIsInFlight_DropsThePublish()
    {
        // A card the page disposed must not write to its bindings afterwards: a leaked publish is
        // the defect FIXER LIST item 9 recorded against DashboardViewModel.
        var harness = Cards.Create();
        harness.QueryGate.Reset();

        harness.Card.IsExpanded = true;
        Assert.True(harness.WaitForQuery(), "the session query never ran");

        harness.Card.Dispose();
        harness.QueryGate.Set();
        harness.Settle();

        Assert.Null(harness.Card.Detail);
        Assert.Empty(harness.Card.FilterRows);
    }

    // ---- P12 Task 4: the ledger cells and the worse ink ---------------------------------------

    private static SessionOverview NightWith(string metric, double value)
    {
        var night = Page.Session(Page.LastSession);
        return metric switch
        {
            "Hfr" => night with { MedianHfr = value },
            "Eccentricity" => night with { MedianEccentricity = value },
            "Fwhm" => night with { MedianFwhm = value },
            "GuidingRms" => night with { MedianGuidingRmsArcsec = value },
            "Stars" => night with { MedianDetectedStars = value },
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "unknown metric"),
        };
    }

    private static TargetTotalsViewModel TargetWith(string metric, double value)
    {
        var totals = Page.PopulatedTotals();
        return new TargetTotalsViewModel(metric switch
        {
            "Hfr" => totals with { AvgHfr = value },
            "Eccentricity" => totals with { AvgEccentricity = value },
            "Fwhm" => totals with { AvgFwhm = value },
            "GuidingRms" => totals with { AvgGuidingRmsArcsec = value },
            "Stars" => totals with { AvgDetectedStars = value },
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "unknown metric"),
        });
    }

    private static bool FlagOf(SessionCardViewModel card, string metric) => metric switch
    {
        "Hfr" => card.IsWorseHfr,
        "Eccentricity" => card.IsWorseEccentricity,
        "Fwhm" => card.IsWorseFwhm,
        "GuidingRms" => card.IsWorseGuidingRms,
        "Stars" => card.IsWorseStars,
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "unknown metric"),
    };

    [Theory]
    // Worse by more than one unit of the last displayed decimal is the only case that speaks.
    // Better and exactly one unit both stay silent, because the job on this page is finding what
    // to reject and a difference inside the last decimal is noise rather than a signal.
    [InlineData("Hfr", 1.83d, 1.71d, true)]
    [InlineData("Hfr", 1.72d, 1.71d, false)]
    [InlineData("Hfr", 1.60d, 1.71d, false)]
    [InlineData("Eccentricity", 0.36d, 0.31d, true)]
    [InlineData("Eccentricity", 0.32d, 0.31d, false)]
    [InlineData("Eccentricity", 0.28d, 0.31d, false)]
    [InlineData("Fwhm", 3.40d, 3.10d, true)]
    [InlineData("Fwhm", 3.11d, 3.10d, false)]
    [InlineData("Fwhm", 2.90d, 3.10d, false)]
    [InlineData("GuidingRms", 0.70d, 0.55d, true)]
    [InlineData("GuidingRms", 0.56d, 0.55d, false)]
    [InlineData("GuidingRms", 0.40d, 0.55d, false)]
    // Stars is the one inverted metric: fewer stars is worse, and its unit is a whole star.
    [InlineData("Stars", 318d, 412d, true)]
    [InlineData("Stars", 411d, 412d, false)]
    [InlineData("Stars", 500d, 412d, false)]
    public void CompareWith_SetsTheWorseFlag(string metric, double night, double target, bool expected)
    {
        using var harness = Cards.Create(overview: NightWith(metric, night));

        harness.Card.CompareWith(TargetWith(metric, target));

        Assert.Equal(expected, FlagOf(harness.Card, metric));
    }

    [Fact]
    public void CompareWith_Null_ClearsEveryFlag()
    {
        // The missing-target case: no target row to compare against, so nothing reads worse.
        using var harness = Cards.Create(overview: NightWith("Hfr", 9.99d));
        harness.Card.CompareWith(TargetWith("Hfr", 1.71d));
        Assert.True(harness.Card.IsWorseHfr);

        harness.Card.CompareWith(null);

        Assert.False(harness.Card.IsWorseHfr);
        Assert.False(harness.Card.IsWorseEccentricity);
        Assert.False(harness.Card.IsWorseFwhm);
        Assert.False(harness.Card.IsWorseGuidingRms);
        Assert.False(harness.Card.IsWorseStars);
    }

    [Fact]
    public void CompareWith_AMissingNightMedian_LeavesTheFlagFalse()
    {
        // Null means "no frame carried this metric", never zero, so an absent median is silent
        // rather than reading as the worst night on the page.
        var night = Page.Session(Page.LastSession) with { MedianHfr = null, MedianDetectedStars = null };
        using var harness = Cards.Create(overview: night);

        harness.Card.CompareWith(new TargetTotalsViewModel(Page.PopulatedTotals()));

        Assert.False(harness.Card.IsWorseHfr);
        Assert.False(harness.Card.IsWorseStars);
    }

    [Fact]
    public void CompareWith_AMissingTargetMean_LeavesTheFlagFalse()
    {
        var totals = Page.PopulatedTotals() with { AvgHfr = null };
        using var harness = Cards.Create(overview: NightWith("Hfr", 9.99d));

        harness.Card.CompareWith(new TargetTotalsViewModel(totals));

        Assert.False(harness.Card.IsWorseHfr);
    }

    [Fact]
    public void LedgerCells_CarryNoUnits()
    {
        // The ledger's units live in its column headers, so these six are the unit-free twins of
        // the card's own figures, which keep theirs.
        using var harness = Cards.Create();
        var card = harness.Card;

        Assert.Equal("6.2", card.LedgerIntegrationText);
        Assert.Equal("2.30", card.LedgerHfrText);
        Assert.Equal("0.40", card.LedgerEccentricityText);
        Assert.Equal("1.90", card.LedgerFwhmText);
        Assert.Equal("0.45", card.LedgerGuidingRmsText);
        Assert.Equal("1,490", card.LedgerStarsText);

        // The unit-carrying originals are untouched.
        Assert.Equal("6.2 h", card.IntegrationText);
        Assert.Equal("2.30 px", card.MedianHfrText);
    }

    [Fact]
    public async Task FilterSwatches_CarryOneImmutableBrushPerFilter()
    {
        // Spec 14.5: a view model that holds a brush holds an ImmutableSolidColorBrush, because
        // these are built wherever the query result arrives.
        //
        // What is asserted is the rule rather than the record's own signature. Brush is a
        // non-nullable positional parameter, so Assert.NotNull could never fail, which is an
        // unfalsifiable assertion in the phase that added it (Task 4 review P3-4). An immutable
        // brush is one whose colour can be read off the UI thread without VerifyAccess throwing,
        // and that is what TRACKING section 6 item 24 exists for.
        using var harness = Cards.Create();

        var swatch = Assert.Single(harness.Card.FilterSwatches);
        Assert.Equal("Ha", swatch.FilterName);
        Assert.Equal(byte.MaxValue, swatch.Brush.Color.A);

        var offThread = await Task.Run(() => swatch.Brush.Color);
        Assert.Equal(swatch.Brush.Color, offThread);
    }

    [Fact]
    public void FilterSwatches_AreResolvedOncePerOverview()
    {
        // Phase review P3-3. The property used to rebuild the list, and call the tint resolver once
        // per filter, on every binding evaluation, which on the virtualised ledger is once per row
        // realisation during a scroll, and the resolver can fall through to a synchronous settings
        // read. One read per overview, and a fresh overview is what invalidates it.
        using var harness = Cards.Create();
        var card = harness.Card;

        var first = card.FilterSwatches;
        Assert.Same(first, card.FilterSwatches);
        Assert.Same(first, card.FilterSwatches);

        card.Refresh(Page.Session(Page.LastSession) with { FiltersUsed = ["Ha", "OIII"] });

        var second = card.FilterSwatches;
        Assert.NotSame(first, second);
        Assert.Equal(["Ha", "OIII"], second.Select(swatch => swatch.FilterName));
        Assert.Same(second, card.FilterSwatches);
    }

    [Fact]
    public void FilterSwatches_FollowFilterOrder_NotTheQueryOrder()
    {
        // The ledger row's dots follow the filter order. A failure is the swatches in the query's wire
        // order (OIII, Ha, L) instead of L, Ha, OIII.
        using var harness = Cards.Create();
        harness.Card.Refresh(Page.Session(Page.LastSession) with { FiltersUsed = ["OIII", "Ha", "L"] });

        Assert.Equal(["L", "Ha", "OIII"], harness.Card.FilterSwatches.Select(swatch => swatch.FilterName));
    }

    [Fact]
    public void TargetTotals_FilterSwatches_FollowFilterOrder_NotTheQueryOrder()
    {
        // The same rule on the ledger's target row. A failure is OIII, Ha, L in that order.
        var totals = new TargetTotalsViewModel(Page.PopulatedTotals() with { FiltersUsed = ["OIII", "Ha", "L"] });

        Assert.Equal(["L", "Ha", "OIII"], totals.FilterSwatches.Select(swatch => swatch.FilterName));
    }

    // ---- Construction and the findings summary ----------------------------------------------

    [Fact]
    public void Construction_QueuesNoWrite()
    {
        // The backing-field rule. Setting the properties in the constructor would queue a write of
        // the values just read back out of the document, which on a page with several nights is
        // one settings write per card built.
        var display = new DisplaySettings
        {
            TargetPage = new TargetPageSettings { GradingBaseline = "rig" },
        };

        using var harness = Cards.Create(display: display);

        Assert.Empty(harness.DisplayWrites);
    }

    [Fact]
    public void ShortSessionDateText_IsMonthDashDay()
    {
        // The collapsed ledger strip label. Invariant on purpose: a fixed-width two-by-two
        // label in a 48 px strip, not a localized date.
        using var harness = Cards.Create(overview: Page.Session(new DateOnly(2025, 9, 8)));

        Assert.Equal("09-08", harness.Card.ShortSessionDateText);
        Assert.Equal("2025-09-08", harness.Card.SessionDateText);
    }

    [Fact]
    public void StripLabel_IsTheFullDate()
    {
        // Polish wave 9 ruling 3: the collapsed strip draws the whole date, never the clipped
        // month-day form. A strip label of "09-08" fails here.
        using var harness = Cards.Create(overview: Page.Session(new DateOnly(2025, 9, 8)));

        Assert.Equal("2025-09-08", ((IStripItem)harness.Card).ShortLabel);
        Assert.True(((IStripItem)harness.Card).IsMonospace);
    }

    [Fact]
    public void FullLabel_IsTheDate_TheIntegration_AndTheFrameCount()
    {
        // The strip's hover label carries the night's three headline figures, joined by the
        // spec's middle dot. A label that is the date alone fails here.
        using var harness = Cards.Create();

        Assert.Equal("2025-12-07 · 6.2 h · 74 frames", ((IStripItem)harness.Card).FullLabel);
    }

    // ---- spec 12.4's "Compare to" segment (Phase 14A Task 3) --------------------------------

    /// <summary>The two projections the segment's buttons bind, read off the process-wide holder
    /// rather than a snapshot, so the segment shows the same choice on every night's pane.
    /// </summary>
    [Fact]
    public void CompareTo_ProjectsTheHoldersBaseline()
    {
        var harness = Cards.Create();

        Assert.True(harness.Card.IsSessionBaseline);
        Assert.False(harness.Card.IsRigBaseline);

        harness.TargetPage.GradingBaseline = GradingBaseline.Rig;

        Assert.False(harness.Card.IsSessionBaseline);
        Assert.True(harness.Card.IsRigBaseline);
    }

    /// <summary>The card forwards and the holder writes. There is no correctness rule in the
    /// command's body for <c>RelayCommand.Execute</c> ignoring <c>CanExecute</c> to skip
    /// (TRACKING item 13): either value is legal at any time.</summary>
    [Fact]
    public void SetGradingBaselineCommand_ForwardsToTheHolder()
    {
        var harness = Cards.Create();

        harness.Card.SetGradingBaselineCommand.Execute(GradingBaseline.Rig);
        Assert.Equal(GradingBaseline.Rig, harness.TargetPage.GradingBaseline);

        harness.Card.SetGradingBaselineCommand.Execute(GradingBaseline.Session);
        Assert.Equal(GradingBaseline.Session, harness.TargetPage.GradingBaseline);
    }

    /// <summary>The holder's flip raises the two projections, which is what repaints the segment
    /// on a card that is not the one that was clicked.</summary>
    [Fact]
    public void CompareTo_RaisesBothProjections_OnAFlip()
    {
        var harness = Cards.Create();
        var raised = new List<string>();
        harness.Card.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        harness.TargetPage.GradingBaseline = GradingBaseline.Rig;

        Assert.Contains(nameof(SessionCardViewModel.IsSessionBaseline), raised);
        Assert.Contains(nameof(SessionCardViewModel.IsRigBaseline), raised);
    }

    // ---- Phase 15A: guiding provenance (spec 12.4), the four-way truth table over
    // SessionOverview.GuidingProvenance --------------------------------------------------------
    //
    // The facts line clause needs a loaded detail (BuildFactsLine runs from Publish), so every
    // case expands and settles first. The ledger cell reads Overview alone and needs neither.
    // Both read the one member TargetDetailQuery computed, task7.md section 7.2 cases 4 to 8.

    [Fact]
    public void GuidingProvenance_CsvOnly_NoSourceClauseAndNoLedgerMark()
    {
        var overview = Page.Session(Page.LastSession) with { GuidingProvenance = GuidingRmsProvenance.Csv };
        using var harness = Cards.Create(overview: overview);
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Equal(
            "21:05 to 03:40, gain 100, 180 s, 300 s, airmass 1.23, 4.5 C, 62 %",
            harness.Card.FactsLineText);
        Assert.Equal("0.45", harness.Card.LedgerGuidingRmsText);
        Assert.Equal("", harness.Card.LedgerGuidingRmsMark);
    }

    [Fact]
    public void GuidingProvenance_Phd2Only_AppendsTheSentenceAndDrawsTheLedgerMark()
    {
        var overview = Page.Session(Page.LastSession) with { GuidingProvenance = GuidingRmsProvenance.Phd2 };
        using var harness = Cards.Create(overview: overview);
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Equal(
            "21:05 to 03:40, gain 100, 180 s, 300 s, airmass 1.23, 4.5 C, 62 %, from a PHD2 guide log",
            harness.Card.FactsLineText);
        // Review P2-2: the mark is its own property now, not concatenated into the value, so the
        // ledger's fixed-width value box is unaffected by whether a night is marked.
        Assert.Equal("0.45", harness.Card.LedgerGuidingRmsText);
        Assert.Equal("†", harness.Card.LedgerGuidingRmsMark);
    }

    // Phase review P3-10. The ledger's mark cell keeps its declared width on an unmarked night
    // (an invisible child would move every value box's right edge), so the view cannot hang a
    // literal tooltip on it: hovering an unmarked night's empty box offered "from a PHD2 guide
    // log". The tooltip has to come from the mark's own presence, which is here.
    [Fact]
    public void TheLedgerMarksTooltip_IsAbsentWithoutAMark_AndNamesTheGuideLogWithOne()
    {
        using var unmarked = Cards.Create(
            overview: Page.Session(Page.LastSession) with { GuidingProvenance = GuidingRmsProvenance.Csv });
        using var marked = Cards.Create(
            overview: Page.Session(Page.LastSession) with { GuidingProvenance = GuidingRmsProvenance.Phd2 });

        Assert.Equal("", unmarked.Card.LedgerGuidingRmsMark);
        Assert.Null(unmarked.Card.LedgerGuidingRmsMarkTip);
        Assert.Equal("†", marked.Card.LedgerGuidingRmsMark);
        Assert.Equal("from a PHD2 guide log", marked.Card.LedgerGuidingRmsMarkTip);
    }

    [Fact]
    public void GuidingProvenance_Mixed_AppendsTheForSomeFramesSentenceAndDrawsTheLedgerMark()
    {
        var overview = Page.Session(Page.LastSession) with { GuidingProvenance = GuidingRmsProvenance.Mixed };
        using var harness = Cards.Create(overview: overview);
        harness.Card.IsExpanded = true;
        harness.Settle();

        // A failure here reads the mixed night as wholly guide-log-derived, which sends a reader
        // troubleshooting one bad sub-exposure to look in the wrong place (task7.md 7.2 case 6).
        Assert.Equal(
            "21:05 to 03:40, gain 100, 180 s, 300 s, airmass 1.23, 4.5 C, 62 %, "
            + "from a PHD2 guide log for some frames",
            harness.Card.FactsLineText);
        Assert.Equal("0.45", harness.Card.LedgerGuidingRmsText);
        Assert.Equal("†", harness.Card.LedgerGuidingRmsMark);
    }

    [Fact]
    public void GuidingProvenance_None_DrawsNothingAtAll()
    {
        // Every value null: not an empty clause with a dangling separator, and not an empty
        // ledger badge, nothing at all (task7.md 7.2 case 7).
        var overview = Page.Session(Page.LastSession) with { GuidingProvenance = GuidingRmsProvenance.None };
        using var harness = Cards.Create(overview: overview);
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Equal(
            "21:05 to 03:40, gain 100, 180 s, 300 s, airmass 1.23, 4.5 C, 62 %",
            harness.Card.FactsLineText);
        Assert.Equal("0.45", harness.Card.LedgerGuidingRmsText);
        Assert.Equal("", harness.Card.LedgerGuidingRmsMark);
    }

    [Fact]
    public void GuidingProvenance_DefaultSingleRigNightWithNoGuidingMentioned_IsByteIdenticalToBeforeThisTask()
    {
        // task7.md 7.2 case 8: a clause added unconditionally would move every existing facts-line
        // case in the suite. Page.Session's default GuidingProvenance is None, unchanged by this
        // task, so this is the regression guard for that default rather than a duplicate of the
        // None case above, which sets the value explicitly.
        using var harness = Cards.Create();
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Equal(
            "21:05 to 03:40, gain 100, 180 s, 300 s, airmass 1.23, 4.5 C, 62 %",
            harness.Card.FactsLineText);
    }
}
