using GalactiLog.App.Tests.TestSupport;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Page = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 15B Task 3, brief sections 9.4 to 9.6. Spec 12.4's Guiding section: the staging rule that
// makes "nothing loads until it opens" true rather than aspirational, the seven summary readouts
// and the web's compact duration form, and the calibration chips.
public class GuidingSectionViewModelTests
{
    private static readonly DateOnly Night = Page.LastSession;

    /// <summary>One stored session row, with everything the state table decides on settable.
    /// </summary>
    private static Phd2SessionSummary Session(
        int frameCount = 400,
        double? pixelScale = 1.5d,
        string? telescope = "RC8",
        int minutes = 0,
        string profile = "TestScope_TestCam")
        => new(
            Guid.NewGuid(),
            new DateTime(Night.Year, Night.Month, Night.Day, 21, 0, 0, DateTimeKind.Utc).AddMinutes(minutes),
            null,
            300d,
            frameCount,
            profile,
            telescope,
            pixelScale,
            0.42d,
            0.51d,
            0.66d,
            null,
            null,
            0,
            0,
            0d,
            0,
            0,
            0,
            null,
            null,
            null,
            null,
            null);

    private static Phd2NightGuiding Guiding(Phd2NightSummary summary, params Phd2SessionSummary[] sessions)
        => new(summary, sessions);

    // ---- 9.4, the night the page shows is the night that loads -----------------------------

    [Fact]
    public void ANightSelected_IssuesTheQueryForThatNight()
    {
        var holder = new TargetPageState();
        using var shown = Cards.Create(targetPage: holder, anyGuideLogs: true);
        using var next = Cards.Create(targetPage: holder, anyGuideLogs: true);

        shown.Card.IsExpanded = true;
        shown.Settle();
        shown.SettleGuiding();

        Assert.Single(shown.GuidingRequests);
        Assert.Empty(next.GuidingRequests);

        // The ledger moves to another night: that night's section reads once and the night the
        // reader left is not read again.
        shown.Card.IsExpanded = false;
        next.Card.IsExpanded = true;
        next.SettleGuiding();

        Assert.Single(shown.GuidingRequests);
        Assert.Single(next.GuidingRequests);
    }

    // ---- 9.5, the summary figures and the compact duration ---------------------------------

    [Fact]
    public void TheSummaryFigures_RenderSpec124sSevenReadouts()
    {
        using var harness = Cards.Create(anyGuideLogs: true);
        harness.GuidingResult = Guiding(
            new Phd2NightSummary
            {
                SessionCount = 5,
                GatedSessionCount = 2,
                FrameCount = 1400,
                RmsRaArcsec = 0.42d,
                RmsDecArcsec = 0.51d,
                RmsTotalArcsec = 0.66d,
                DropCount = 7,
                MaxDropRun = 3,
                UnguidedSeconds = 95d,
                DitherCount = 12,
                SettleFailedCount = 2,
                SettleMedianS = 4.25d,
            },
            Session());

        Open(harness);
        var guiding = harness.Card.Guiding!;

        Assert.Equal("0.66 arcsec", guiding.RmsText);
        Assert.Equal("0.42 arcsec", guiding.RmsRaText);
        Assert.Equal("0.51 arcsec", guiding.RmsDecText);
        Assert.Equal("5 (2 too short to grade)", guiding.SessionsText);
        Assert.Equal("7 (1m 35s unguided, longest run 3)", guiding.StarLostText);
        Assert.Equal("12", guiding.DithersText);
        Assert.Equal("4.3s", guiding.SettleText);
        Assert.Equal("2 failed", guiding.SettleFailedText);
    }

    [Fact]
    public void AZeroFigure_RendersNoParentheticalAndTheDitherCountStillRendersZero()
    {
        using var harness = Cards.Create(anyGuideLogs: true);
        harness.GuidingResult = Guiding(
            new Phd2NightSummary
            {
                SessionCount = 3,
                GatedSessionCount = 0,
                DropCount = 0,
                MaxDropRun = 0,
                UnguidedSeconds = 0d,
                DitherCount = 0,
                SettleFailedCount = 0,
                SettleMedianS = null,
            },
            Session());

        Open(harness);
        var guiding = harness.Card.Guiding!;

        // "The parenthetical is absent at zero, never '0 too short'."
        Assert.Equal("3", guiding.SessionsText);
        Assert.Equal("0", guiding.StarLostText);

        // "Always drawn, including at zero."
        Assert.Equal("0", guiding.DithersText);

        // "A null median draws nothing where the figure would be."
        Assert.Equal("", guiding.SettleText);
        Assert.Equal("", guiding.SettleFailedText);
        Assert.False(guiding.HasSettleFailed);
    }

    [Fact]
    public void ANightWhereNoSessionClearedTheGate_DrawsNoRmsFiguresAtAll()
    {
        // Spec 12.4's seventh state row: every session short, so the weighted RMS is null and a
        // null figure draws nothing, its label included.
        using var harness = Cards.Create(anyGuideLogs: true);
        harness.GuidingResult = Guiding(
            new Phd2NightSummary { SessionCount = 2, GatedSessionCount = 2 },
            Session(frameCount: 40));

        Open(harness);
        var guiding = harness.Card.Guiding!;

        Assert.Equal("", guiding.RmsText);
        Assert.False(guiding.HasRms);
        Assert.False(guiding.HasRmsRa);
        Assert.False(guiding.HasRmsDec);
        Assert.Equal("2 (2 too short to grade)", guiding.SessionsText);

        // A night of short sessions is still plotted: the default selection is the first session
        // when every one of them is gated.
        Assert.NotNull(guiding.SelectedSession);
        Assert.Equal(GuidingSectionViewModel.SectionState.Drawable, guiding.State);
    }

    [Theory]
    [InlineData(0d, "0s")]
    [InlineData(45d, "45s")]
    [InlineData(59.4d, "59s")]
    [InlineData(59.6d, "60s")]
    [InlineData(60d, "1m")]
    [InlineData(61d, "1m 1s")]
    [InlineData(3599d, "59m 59s")]
    [InlineData(3600d, "1h")]
    [InlineData(3661d, "1h 1m")]
    // Task 3 review P2-1: the web rounds the total once and branches on the rounded figure.
    [InlineData(90.6d, "1m 31s")]
    [InlineData(119.5d, "2m")]
    [InlineData(3599.6d, "1h")]
    [InlineData(3659.6d, "1h 1m")]
    [InlineData(86399.6d, "24h")]
    // task4b-review.md P3-5: the web's guard answers "0s" for a non-finite or negative input, and
    // the port answered the empty string, which no caller reaches today and the next one would.
    [InlineData(double.NaN, "0s")]
    [InlineData(-1d, "0s")]
    public void TheCompactDuration_BranchesOnTheRawValueAndNotTheRoundedOne(double seconds, string expected)
        // The web's formatSecondsShort (frontend/src/utils/phd2Format.ts lines 9 to 21). A failure
        // looks like 59.6 reading "1m", which is the rounded-value branch and which every other
        // figure in this table passes.
        => Assert.Equal(expected, GuidingSectionViewModel.CompactDuration(seconds));

    // ---- 9.6, the calibration issues -------------------------------------------------------
    // Phase 24 R3 removed the chips with the Guiding section; the section still carries the
    // list unfiltered, so the one exclusion stays in Phd2Metrics.AggregateNight. The literal
    // "none" is deliberately perverse (questions.md section 3): a section that filtered it a
    // second time would pass every ordinary case and silently be a second answer to the same question.

    [Theory]
    [InlineData("Orthogonality", "Rates")]
    [InlineData]
    [InlineData("none")]
    public void TheCalIssues_PassThroughUnfiltered(params string[] issues)
    {
        using var harness = Cards.Create(anyGuideLogs: true);
        harness.GuidingResult = Guiding(new Phd2NightSummary { SessionCount = 1, CalIssues = issues }, Session());

        Open(harness);

        Assert.Equal(issues, harness.Card.Guiding!.CalIssues);
        Assert.Equal(issues.Length > 0, harness.Card.Guiding.HasCalIssues);
    }

    // ---- the state table --------------------------------------------------------------------

    [Fact]
    public void ANightWithNoGuidingSession_SaysSoWhereThePlotWouldBe()
    {
        using var harness = Cards.Create(anyGuideLogs: true);
        harness.GuidingResult = Guiding(new Phd2NightSummary());

        Open(harness);
        var guiding = harness.Card.Guiding!;

        Assert.Equal(GuidingSectionViewModel.SectionState.NoSessions, guiding.State);
        Assert.Equal("No PHD2 guide logs for this night.", guiding.PlotNoticeText);
        Assert.False(guiding.HasSummary);
        Assert.False(guiding.HasPlot);
        Assert.Equal("", guiding.SessionCountText);
        Assert.False(guiding.HasSessionCount);
    }

    [Fact]
    public void ASelectedSessionWithNoPixelScale_SaysSoWithoutAskingForFrames()
    {
        using var harness = Cards.Create(anyGuideLogs: true);
        harness.GuidingResult = Guiding(
            new Phd2NightSummary { SessionCount = 1 },
            Session(pixelScale: null));

        Open(harness);
        var guiding = harness.Card.Guiding!;

        Assert.Equal(GuidingSectionViewModel.SectionState.NoPixelScale, guiding.State);
        Assert.Equal(
            "No pixel scale in this session's log header, so guiding error cannot be shown in arcseconds.",
            guiding.PlotNoticeText);

        // The summary, the chips and the selectors are still drawn; only the plot is replaced.
        Assert.True(guiding.HasSummary);
        Assert.False(guiding.HasPlot);
    }

    [Fact]
    public void ASelectedSessionWithNoFrame_SaysSoWhereThePlotWouldBe()
    {
        using var harness = Cards.Create(anyGuideLogs: true);
        harness.GuidingResult = Guiding(
            new Phd2NightSummary { SessionCount = 1 },
            Session(frameCount: 0));

        Open(harness);

        Assert.Equal(GuidingSectionViewModel.SectionState.NoFrames, harness.Card.Guiding!.State);
        Assert.Equal("No guide frames for this session.", harness.Card.Guiding!.PlotNoticeText);
    }

    [Fact]
    public void AFailedNightQuery_IsTheSectionsOwnFailureAndNotThePanes()
    {
        using var harness = Cards.Create(anyGuideLogs: true);
        harness.GuidingThrows = new InvalidOperationException("the guide log read failed");

        Open(harness);

        Assert.Equal(GuidingSectionViewModel.SectionState.Failed, harness.Card.Guiding!.State);
        Assert.True(harness.Card.Guiding!.HasFailure);

        // The rest of the pane is unaffected: a guide-log failure never takes the night down.
        Assert.False(harness.Card.HasFailure);
        Assert.NotNull(harness.Card.Detail);
    }

    [Fact]
    public void TheDefaultSelection_IsTheFirstSessionThatIsNotGated()
    {
        using var harness = Cards.Create(anyGuideLogs: true);
        var gated = Session(frameCount: 40, minutes: 0);
        var full = Session(frameCount: 400, minutes: 1);
        harness.GuidingResult = Guiding(new Phd2NightSummary { SessionCount = 2 }, gated, full);

        Open(harness);

        Assert.Same(full, harness.Card.Guiding!.SelectedSession);
    }

    [Fact]
    public void TheGraphSlot_IsNullUntilANightIsPublished()
    {
        using var harness = Cards.Create(anyGuideLogs: true);

        Assert.Null(harness.Card.Guiding!.Graph);
    }

    [Fact]
    public async Task ASlowNightRead_ArrivingAfterANewerOne_PublishesNothing()
    {
        // Task 3 review P3-6, the section's generation guard. A failure looks like the figures of
        // before a rescan overwriting the figures of after it.
        //
        // The two reads are told apart by call order, and nothing but this handshake orders the
        // two Task.Run bodies: the second can reach the delegate first on a loaded machine, and
        // then it is the SECOND read that blocks and the await below that times out at ten
        // seconds. That is the failure Task 5c saw on the shared tree. `entered` makes the order a
        // fact rather than a hope; it is a synchronisation primitive and not a sleep.
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var calls = 0;
        using var section = new GuidingSectionViewModel(
            Night,
            (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.Set();
                    gate.Wait(TimeSpan.FromSeconds(10));
                    return Guiding(new Phd2NightSummary { SessionCount = 1 }, Session());
                }

                return Guiding(new Phd2NightSummary { SessionCount = 2 }, Session(), Session(minutes: 10));
            },
            post: action => action());

        section.IsShown = true;
        var slow = section.PendingLoad!;
        entered.Wait();

        section.Invalidate();
        await section.PendingLoad!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, section.Sessions.Count);

        gate.Set();
        await slow.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, calls);
        Assert.Equal(2, section.Sessions.Count);
        Assert.Equal("2", section.SessionCountText);
    }

    // ---- Task 4b 7.1, the two stages, counted ------------------------------------------------

    private static Phd2SessionFrames FramesOf(int count = 3)
        => new(
            1.5d,
            null,
            [.. Enumerable.Range(0, count).Select(i => new Phd2FramePoint(i * 2d, 0.1d, -0.1d, 0, "", 0, "", null, null, false))],
            []);

    [Fact]
    public void TheTwoStages_Counted_AndTheRigFilterIssuesNoNightQuery()
    {
        // A failure looks like a second frames read for one session or, for the rig filter, a night query carrying the label: the night query answers an
        // unmapped rig's label with an empty list and the plot goes blank with no notice.
        using var harness = Cards.Create(anyGuideLogs: true);
        var first = Session(minutes: 0);
        var second = Session(minutes: 10);
        var unmapped = Session(minutes: 20, telescope: null, profile: "Spare_Guider");
        harness.GuidingResult = Guiding(new Phd2NightSummary { SessionCount = 3 }, first, second, unmapped);
        harness.FramesResult = _ => FramesOf();

        harness.Card.IsExpanded = true;
        harness.Settle();
        harness.SettleGuiding().SettleFrames();
        var graph = harness.Card.Guiding!.Graph!;
        Assert.Single(harness.GuidingRequests);
        Assert.Equal([first.Id], harness.FrameRequests);

        graph.SelectedSession = graph.SessionOptions[1];
        harness.SettleFrames();
        Assert.Equal([first.Id, second.Id], harness.FrameRequests);
        Assert.Same(second, harness.Card.Guiding.SelectedSession);

        // The required case: one mapped rig and one unmapped, the filter set to the unmapped
        // label. The session stays on the panel, and the night query is not asked again.
        Assert.True(graph.ShowRigFilter);
        graph.SelectedRigOption = "Spare_Guider";
        harness.SettleFrames();

        Assert.Equal([unmapped.Id], graph.SessionOptions.Select(option => option.Id));
        Assert.Equal([first.Id, second.Id, unmapped.Id], harness.FrameRequests);
        Assert.Single(harness.GuidingRequests);

        // The one night query carried the CARD's rig, which the harness overview says is "RC8",
        // and never the panel's filter label.
        Assert.Equal("RC8", harness.GuidingRequests[0].Telescope);
        Assert.Equal(3, graph.RigOptions.Count);
        Assert.Same(unmapped, harness.Card.Guiding.SelectedSession);

        // Back to the whole night.
        graph.SelectedRigOption = GuideGraphViewModel.AllRigs;
        Assert.Equal(3, graph.SessionOptions.Count);
        Assert.Single(harness.GuidingRequests);
    }

    [Fact]
    public void ANightPublishedWhileTheCardIsHidden_BuildsItsGraphWhenTheCardComesBack()
    {
        // task4b-review.md P3-6, the one reachable state of EnsureLoaded's `_loaded` re-entry
        // branch: the night read is in flight, the reader leaves the card, and the answer lands
        // with nobody looking, so Publish's EnsureGraph returns without building. A failure looks
        // like a band that is permanently blank after one card collapse. The gate makes the
        // ordering a fact rather than a hope.
        using var harness = Cards.Create(anyGuideLogs: true);
        harness.GuidingResult = Guiding(new Phd2NightSummary { SessionCount = 1 }, Session());
        harness.FramesResult = _ => FramesOf();

        harness.GuidingGate.Reset();
        harness.Card.IsExpanded = true;
        harness.Settle();
        harness.GuidingEntered.Wait(TimeSpan.FromSeconds(30));

        // The card stops being the shown night while its read is parked.
        harness.Card.IsExpanded = false;
        harness.GuidingGate.Set();
        harness.SettleGuiding();

        var section = harness.Card.Guiding!;
        Assert.Single(harness.GuidingRequests);
        Assert.Null(section.Graph);
        Assert.Empty(harness.FrameRequests);

        // Back to the card: no second night query, and the graph is built now.
        harness.Card.IsExpanded = true;
        harness.Settle();
        harness.SettleFrames();

        Assert.Single(harness.GuidingRequests);
        Assert.NotNull(section.Graph);
        Assert.Single(harness.FrameRequests);
    }

    // ---- Phase review F1, the band is the card's rig's ----------------------------------------

    private const string RigA = "Askar_140APO";
    private const string RigB = "Askar_SQA55";

    /// <summary>A night the two rigs guided separately, with figures that differ per rig, so a
    /// case that reads the wrong rig's rollup cannot pass by coincidence. This is the user's own
    /// 2026-07-13 and 2026-07-14 shape: two rigs, two targets, one night.</summary>
    private static void TwoRigNight(TestSupport.SessionCardViewModelTestFactory.Harness harness)
    {
        var a = Session(telescope: RigA, profile: "AM5n_OAG_ASI174M");
        var b = Session(telescope: RigB, profile: "ASI220-AM5-30F5", minutes: 10);
        harness.GuidingResultFor = rig => rig switch
        {
            RigA => Guiding(new Phd2NightSummary { SessionCount = 1, RmsTotalArcsec = 0.42d }, a),
            RigB => Guiding(new Phd2NightSummary { SessionCount = 1, RmsTotalArcsec = 0.91d }, b),
            _ => Guiding(new Phd2NightSummary { SessionCount = 2, RmsTotalArcsec = 0.66d }, a, b),
        };
    }

    [Fact]
    public void ASingleRigCard_OnATwoRigNight_ReadsItsOwnRigsSessionsAndRollup()
    {
        // Phase review F1. A session card is one target's night, not the night, and the web
        // narrows on exactly this seam twice (SessionAccordionCard.tsx:1240 for the session list,
        // target_detail.py:563-571 for the rollup). A failure looks like the Crescent Nebula card
        // and the Sh2 129 card on one night printing the same seven figures, frame-count weighted
        // across both rigs. Red against the null argument: the rollup reads 0.66 arcsec and the
        // list holds both sessions.
        using var harness = Cards.Create(
            overview: Page.Session(Night) with { Telescope = RigA, RigCount = 1 },
            anyGuideLogs: true);
        TwoRigNight(harness);

        Open(harness);
        var guiding = harness.Card.Guiding!;

        Assert.Equal(RigA, Assert.Single(harness.GuidingRequests).Telescope);
        Assert.Equal("0.42 arcsec", guiding.RmsText);
        Assert.Equal("1", guiding.SessionCountText);
        Assert.Equal([RigA], guiding.Sessions.Select(session => session.Telescope));

        // One rig came back, so there is nothing for the panel's own filter to partition.
        Assert.False(guiding.Graph!.ShowRigFilter);
    }

    [Fact]
    public void ACardWhoseOwnNightIsMultiRig_ReadsTheWholeNight_AndIsOfferedTheFilter()
    {
        // The other side of F1's branch, which is where the web stops narrowing: isMultiRig()
        // passes null. Red against a fix that always passes Overview.Telescope, where the rollup
        // would read 0.42 and the rig filter would never be offered.
        using var harness = Cards.Create(
            overview: Page.Session(Night) with { Telescope = RigA, RigCount = 2 },
            anyGuideLogs: true);
        TwoRigNight(harness);

        Open(harness);
        var guiding = harness.Card.Guiding!;

        Assert.Null(Assert.Single(harness.GuidingRequests).Telescope);
        Assert.Equal("0.66 arcsec", guiding.RmsText);
        Assert.Equal("2", guiding.SessionCountText);
        Assert.Equal([RigA, RigB], guiding.Sessions.Select(session => session.Telescope));

        // And the panel's own rig filter is what partitions it from here, locally.
        Assert.True(guiding.Graph!.ShowRigFilter);
        Assert.Equal([GuideGraphViewModel.AllRigs, RigA, RigB], guiding.Graph.RigOptions);
    }

    // ---- Fixer item 34, no section at all on a library with no guide log ----------------------

    [Fact]
    public void ACardOnALibraryWithNoGuideLog_BuildsNoSection_AndCanIssueNoQuery()
    {
        // Item 34's P3-3. The band is not drawn, so nothing about it may reach the night query;
        // a section that exists is a section a later caller can open. A failure looks like a
        // Guiding section on a card whose band the reader cannot see.
        using var harness = Cards.Create(anyGuideLogs: false);
        harness.GuidingResult = Guiding(new Phd2NightSummary { SessionCount = 1 }, Session());

        Assert.False(harness.Card.HasGuidingBand);
        Assert.Null(harness.Card.Guiding);

        // Everything the reader can do to a band that is not there, done anyway.
        harness.Card.IsExpanded = true;
        harness.Settle();
        harness.SettleGuiding();

        Assert.Empty(harness.GuidingRequests);
        Assert.Empty(harness.FrameRequests);
    }

    // ---- Task 4b 7.2, no read for a session the graph cannot draw ----------------------------

    [Theory]
    [InlineData(100, null, "No pixel scale in this session's log header, so guiding error cannot be shown in arcseconds.")]
    [InlineData(0, 1.5d, "No guide frames for this session.")]
    public void ASessionTheGraphCannotDraw_CostsNoFramesRead(int frameCount, double? pixelScale, string notice)
    {
        // A failure looks like the read being issued and the notice derived from its answer, which
        // is the web's shape and the one spec 12.4 does not port.
        using var harness = Cards.Create(anyGuideLogs: true);
        var undrawable = Session(frameCount: frameCount, pixelScale: pixelScale, minutes: 0);
        var drawable = Session(frameCount: frameCount == 0 ? 40 : 400, minutes: 10);
        harness.GuidingResult = Guiding(new Phd2NightSummary { SessionCount = 2 }, drawable, undrawable);
        harness.FramesResult = _ => FramesOf();

        // Spec 12.4's default lands on the first session that is not gated. The zero-frame row is
        // gated, so its case reaches it through the selector instead.
        Open(harness);
        harness.SettleFrames();
        var section = harness.Card.Guiding!;
        var graph = section.Graph!;
        Assert.True(graph.ShowSessionSelector);

        graph.SelectedSession = graph.SessionOptions.Single(option => option.Id == drawable.Id);
        harness.SettleFrames();
        Assert.True(section.HasPlot);
        Assert.False(section.HasPlotNotice);
        var reads = harness.FrameRequests.Count;

        graph.SelectedSession = graph.SessionOptions.Single(option => option.Id == undrawable.Id);
        harness.SettleFrames();

        Assert.Same(undrawable, section.SelectedSession);
        Assert.Equal(reads, harness.FrameRequests.Count);
        Assert.DoesNotContain(undrawable.Id, harness.FrameRequests);
        Assert.Equal(notice, section.PlotNoticeText);
        Assert.True(section.HasPlotNotice);
        Assert.False(section.HasPlot);
    }

    [Fact]
    public void ANightWhoseOnlySessionHasNoPixelScale_ReadsNoFramesAtAll()
    {
        // The ASIAIR night, as the band opens on it: the graph exists, because the selectors are
        // its own, and the read is withheld.
        using var harness = Cards.Create(anyGuideLogs: true);
        harness.GuidingResult = Guiding(new Phd2NightSummary { SessionCount = 1 }, Session(pixelScale: null));
        harness.FramesResult = _ => FramesOf();

        Open(harness);
        harness.SettleFrames();

        Assert.NotNull(harness.Card.Guiding!.Graph);
        Assert.Empty(harness.FrameRequests);
        Assert.Equal(GuidingSectionViewModel.SectionState.NoPixelScale, harness.Card.Guiding.State);
    }

    [Fact]
    public async Task ASelectionChangedWhileAReadIsInFlight_LastWins()
    {
        using var harness = Cards.Create(anyGuideLogs: true);
        var slow = Session(minutes: 0);
        var fast = Session(minutes: 10);
        harness.GuidingResult = Guiding(new Phd2NightSummary { SessionCount = 2 }, slow, fast);
        using var gate = new ManualResetEventSlim();
        harness.FramesResult = id =>
        {
            if (id == slow.Id)
            {
                gate.Wait(TimeSpan.FromSeconds(10));
                return FramesOf(50);
            }

            return FramesOf(5);
        };

        Open(harness);
        var graph = harness.Card.Guiding!.Graph!;
        var slowRead = graph.PendingLoad;

        graph.SelectedSession = graph.SessionOptions[1];
        harness.SettleFrames();
        gate.Set();
        await slowRead.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(8d, graph.TimeRange.Max);
        Assert.Equal([slow.Id, fast.Id], harness.FrameRequests);
    }

    [Fact]
    public void AFailedFramesRead_IsLogged()
    {
        // The graph holds no logger by design, so without this the failure is silent.
        using var harness = Cards.Create(anyGuideLogs: true);
        harness.GuidingResult = Guiding(new Phd2NightSummary { SessionCount = 1 }, Session());
        harness.FramesResult = _ => throw new InvalidOperationException("frames read failed");

        Open(harness);
        harness.SettleFrames();

        Assert.Contains(harness.Logger.Entries, entry => entry.Exception?.Message == "frames read failed");
        Assert.False(harness.Card.Guiding!.HasFailure);
    }

    [Fact]
    public void AnInvalidatedSection_DropsItsGraph_AndRebuildsItOnTheNextPublish()
    {
        using var harness = Cards.Create(anyGuideLogs: true);
        harness.GuidingResult = Guiding(new Phd2NightSummary { SessionCount = 1 }, Session());
        Open(harness);
        var before = harness.Card.Guiding!.Graph;
        Assert.NotNull(before);

        harness.Card.Guiding.Invalidate();
        harness.SettleGuiding().SettleFrames();

        Assert.NotNull(harness.Card.Guiding.Graph);
        Assert.NotSame(before, harness.Card.Guiding.Graph);
    }

    // ---- Task 4b, one default-selection rule and no rig rule ---------------------------------

    [Fact]
    public void TheSectionSource_HoldsNoSelectionRuleAndNoRigRule()
    {
        var code = TestSupport.SourceScan.StripComments(File.ReadAllText(Path.Combine(
            TestSupport.SourceScan.SrcRoot(), "GalactiLog.App", "ViewModels", "TargetDetail", "GuidingSectionViewModel.cs")));

        // Spec 12.4's default selection has one implementation, the graph's.
        Assert.DoesNotMatch(@"\.Gated\b", code);

        // The rig filter is one RigLabel comparison over the cached night: no rig rule, no second
        // night query, and never a label in the night query's telescope argument.
        Assert.DoesNotContain("SelectNightRows", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".Telescope", code, StringComparison.Ordinal);

        // The one night query, carrying the card's own rig and never the panel's filter (F1).
        Assert.Contains("_getGuiding(_night, _rig)", code, StringComparison.Ordinal);
        Assert.Equal(1, code.Split("_getGuiding(").Length - 1);

        Assert.DoesNotContain("ConfigureAwait", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Clamp", code, StringComparison.Ordinal);
    }

    private static void Open(TestSupport.SessionCardViewModelTestFactory.Harness harness)
    {
        harness.Card.IsExpanded = true;
        harness.Settle();
        harness.SettleGuiding();
    }

}
