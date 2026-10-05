using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

/// <summary>
/// Spec 12.4's per-frame quality grading on the view-model side: the six graded cells, their
/// tooltips, the row score, the tally and the "Compare to" toggle. Plain xunit facts: no window,
/// no dispatcher, no database. The grading arrives on the read model, exactly as
/// <c>SessionDetailQuery</c> hands it over.
/// </summary>
public class FrameGradingViewModelTests
{
    private static MetricGrade Grade(double? z, double? median) => new(z, median);

    private static readonly MetricGrade None = new(null, null);

    /// <summary>A grading with every member ungraded, so a test names only the ones it
    /// asserts.</summary>
    private static FrameGrading Grading(
        MetricGrade? sessionHfr = null,
        MetricGrade? rigHfr = null,
        MetricGrade? sessionEccentricity = null,
        MetricGrade? rigEccentricity = null,
        MetricGrade? sessionFwhm = null,
        MetricGrade? rigFwhm = null,
        MetricGrade? detectedStars = null,
        MetricGrade? aduMedian = null,
        MetricGrade? guidingRms = null)
        => new(
            sessionHfr ?? None, rigHfr ?? None,
            sessionEccentricity ?? None, rigEccentricity ?? None,
            sessionFwhm ?? None, rigFwhm ?? None,
            detectedStars ?? None,
            aduMedian ?? None,
            guidingRms ?? None);

    private static FrameRow Frame(
        double? medianHfr = null,
        double? eccentricity = null,
        double? fwhm = null,
        int? detectedStars = null,
        double? guidingRmsArcsec = null,
        double? aduMedian = null,
        bool isHfrOutlier = false,
        bool isEccentricityOutlier = false,
        FrameGrading? grading = null,
        string fileName = "frame.fits")
        => FrameTableViewModelTests.Frame(
            fileName: fileName,
            medianHfr: medianHfr,
            eccentricity: eccentricity,
            fwhm: fwhm,
            detectedStars: detectedStars,
            guidingRmsArcsec: guidingRmsArcsec,
            aduMedian: aduMedian,
            isHfrOutlier: isHfrOutlier,
            isEccentricityOutlier: isEccentricityOutlier) with
        { Grading = grading };

    private static FrameRowViewModel Row(FrameRow row, GradingBaseline baseline = GradingBaseline.Session)
        => new(row, Columns(), TimeZoneInfo.Utc, use24Hour: true, baseline);

    private static IReadOnlyList<ColumnViewModel> Columns()
        => [.. FrameColumns.All.Select(column => new ColumnViewModel(
            column.Key, column.Title, isVisible: true, canHide: true, isGroupEnabled: true,
            isNumeric: column.IsNumeric))];

    private sealed class TableHarness
    {
        public DisplaySettings Display { get; private set; } = new();

        public TargetPageState TargetPage { get; private set; } = null!;

        public FrameTableViewModel Table { get; private set; } = null!;

        public List<Func<DisplaySettings, DisplaySettings>> Writes { get; } = [];

        public static TableHarness Create(
            IReadOnlyList<FrameRow> frames,
            TargetPageSettings? stored = null)
        {
            var harness = new TableHarness();
            var writer = new DisplayColumnWriter(
                () => harness.Display,
                value => harness.Display = value);

            harness.TargetPage = new TargetPageState(
                stored,
                mutate =>
                {
                    harness.Writes.Add(mutate);
                    harness.Display = mutate(harness.Display);
                });

            harness.Table = new FrameTableViewModel(
                frames,
                harness.Display,
                writer,
                new ShellIntegration(
                    copyText: _ => Task.CompletedTask,
                    start: _ => null),
                openPreview: null,
                new GeneralSettings { Timezone = "UTC", Use24HTime = true },
                getHeaders: _ => null,
                targetPage: harness.TargetPage);

            return harness;
        }
    }

    // ---- the cell --------------------------------------------------------------------

    [Fact]
    public void ACell_CarriesItsBandAndItsText()
    {
        var row = Row(Frame(medianHfr: 2.80d, grading: Grading(sessionHfr: Grade(1.9d, 2.10d))));

        Assert.Equal("2.80", row.HfrCell.Text);
        Assert.Equal(QualityBand.Watch, row.HfrCell.Band);
        Assert.False(row.HfrCell.IsBetter);
        Assert.True(row.HfrCell.IsWatch);
        Assert.False(row.HfrCell.IsReject);
    }

    [Fact]
    public void ANeutralCell_CarriesNoBandClass()
    {
        // Spec 12.4's band table gives neutral ColorTextPrimary, "which is to say no mark", so the
        // absence of the three classes is the neutral ink and there is no fourth flag to set.
        var row = Row(Frame(medianHfr: 2.20d, grading: Grading(sessionHfr: Grade(0.4d, 2.10d))));

        Assert.Equal(QualityBand.Neutral, row.HfrCell.Band);
        Assert.False(row.HfrCell.IsBetter);
        Assert.False(row.HfrCell.IsWatch);
        Assert.False(row.HfrCell.IsReject);
    }

    [Theory]
    [InlineData(1.9d, 2.10d, "HFR 2.80, 1.9 MAD units worse than the session median 2.10")]
    [InlineData(-1.9d, 2.10d, "HFR 2.80, 1.9 MAD units better than the session median 2.10")]
    [InlineData(3.44d, 2.00d, "HFR 2.80, 3.4 MAD units worse than the session median 2.00")]
    // Task 3 review P3: exactly zero is not an improvement. The direction word was z > 0, so a
    // frame carrying its own group's median rendered "0.0 MAD units better", which claims an
    // improvement of nothing. At or above the median is worse and only below it is better.
    [InlineData(0d, 2.10d, "HFR 2.80, 0.0 MAD units worse than the session median 2.10")]
    [InlineData(-0.04d, 2.10d, "HFR 2.80, 0.0 MAD units better than the session median 2.10")]
    public void TheTooltip_MatchesTheSpecifiedFormat(double z, double median, string expected)
    {
        var row = Row(Frame(medianHfr: 2.80d, grading: Grading(sessionHfr: Grade(z, median))));

        Assert.Equal(expected, row.HfrCell.Tooltip);
        Assert.True(row.HfrCell.HasTooltip);
    }

    [Fact]
    public void AnUngradedCell_CarriesNoTooltip()
    {
        // Spec 12.4: "an empty claim reads as a claim". The tooltip is null, not empty, so the
        // markup's ToolTip.Tip binds a null and Avalonia renders no tooltip at all.
        var row = Row(Frame(medianHfr: 2.80d, grading: Grading(sessionHfr: Grade(null, 2.10d))));

        Assert.Null(row.HfrCell.Tooltip);
        Assert.False(row.HfrCell.HasTooltip);
    }

    [Fact]
    public void ACellWithNoValue_CarriesNoTooltip()
    {
        var row = Row(Frame(medianHfr: null, grading: Grading(sessionHfr: Grade(1.9d, 2.10d))));

        Assert.Equal("", row.HfrCell.Text);
        Assert.Null(row.HfrCell.Tooltip);
    }

    [Fact]
    public void ACellWhoseBaselineHasNoMedian_CarriesNoTooltip()
    {
        var row = Row(Frame(medianHfr: 2.80d, grading: Grading(sessionHfr: Grade(1.9d, null))));

        Assert.Null(row.HfrCell.Tooltip);
    }

    [Fact]
    public void TheTooltip_SaysMadUnits_AndNeverSigma()
    {
        // Ruling C2 and spec 12.4: there is no 1.4826 consistency scaling anywhere in this port or
        // in the web application, and every label, tooltip and help paragraph says "MAD units".
        var row = Row(Frame(
            medianHfr: 2.80d,
            eccentricity: 0.55d,
            fwhm: 3.10d,
            detectedStars: 900,
            guidingRmsArcsec: 0.62d,
            aduMedian: 1800d,
            grading: Grading(
                sessionHfr: Grade(1.9d, 2.10d),
                sessionEccentricity: Grade(2.0d, 0.40d),
                sessionFwhm: Grade(1.6d, 2.90d),
                detectedStars: Grade(1.2d, 1100d),
                guidingRms: Grade(2.4d, 0.48d),
                aduMedian: Grade(1.7d, 1500d))));

        foreach (var tooltip in new[]
        {
            row.HfrCell.Tooltip, row.EccentricityCell.Tooltip, row.FwhmCell.Tooltip,
            row.DetectedStarsCell.Tooltip, row.GuidingRmsCell.Tooltip, row.AduMedianCell.Tooltip,
        })
        {
            Assert.NotNull(tooltip);
            Assert.Contains("MAD units", tooltip, StringComparison.Ordinal);
            Assert.DoesNotContain("sigma", tooltip, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheTooltip_NamesTheActiveBaseline()
    {
        var frame = Frame(
            medianHfr: 2.80d,
            grading: Grading(
                sessionHfr: Grade(1.9d, 2.10d),
                rigHfr: Grade(0.5d, 2.50d)));

        Assert.Contains("session median 2.10", Row(frame).HfrCell.Tooltip!, StringComparison.Ordinal);
        Assert.Contains(
            "rig median 2.50",
            Row(frame, GradingBaseline.Rig).HfrCell.Tooltip!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheThreeSignalMetrics_AlwaysSaySession()
    {
        // Spec 12.4's Compare to block: the toggle governs the sharpness and roundness metrics
        // only, so the baseline word on the three signal cells is the one their number was
        // actually measured against whatever the toggle says.
        var row = Row(
            Frame(
                detectedStars: 900,
                guidingRmsArcsec: 0.62d,
                aduMedian: 1800d,
                grading: Grading(
                    detectedStars: Grade(1.2d, 1100d),
                    guidingRms: Grade(2.4d, 0.48d),
                    aduMedian: Grade(1.7d, 1500d))),
            GradingBaseline.Rig);

        Assert.Contains("session median", row.DetectedStarsCell.Tooltip!, StringComparison.Ordinal);
        Assert.Contains("session median", row.GuidingRmsCell.Tooltip!, StringComparison.Ordinal);
        Assert.Contains("session median", row.AduMedianCell.Tooltip!, StringComparison.Ordinal);
        Assert.DoesNotContain("rig median", row.DetectedStarsCell.Tooltip!, StringComparison.Ordinal);
    }

    // ---- the row score ---------------------------------------------------------------

    [Fact]
    public void TheRowScore_IsTheWeightedMeanOfTheThreeAxes()
    {
        var row = Row(Frame(
            medianHfr: 2.80d,
            eccentricity: 0.55d,
            detectedStars: 900,
            grading: Grading(
                sessionHfr: Grade(2.0d, 2.10d),
                sessionEccentricity: Grade(2.0d, 0.40d),
                detectedStars: Grade(2.0d, 1100d))));

        Assert.Equal(FrameQuality.CombinedScore(2.0d, 2.0d, 2.0d), row.RowScore);
        Assert.Equal(50d - (16.7d * 2d), row.RowScore!.Value, 9);
        Assert.Equal(QualityBand.Reject, row.ScoreBand);
        Assert.True(row.IsScoreReject);

        // The signal axis is session graded whichever way the toggle is set, and sharpness and
        // roundness follow it, which is the web's own composition in SessionAccordionCard.tsx.
        var rig = Row(
            Frame(
                medianHfr: 2.80d,
                eccentricity: 0.55d,
                detectedStars: 900,
                grading: Grading(
                    sessionHfr: Grade(2.0d, 2.10d),
                    rigHfr: Grade(0d, 2.80d),
                    sessionEccentricity: Grade(2.0d, 0.40d),
                    rigEccentricity: Grade(0d, 0.55d),
                    detectedStars: Grade(2.0d, 1100d))),
            GradingBaseline.Rig);

        Assert.Equal(FrameQuality.CombinedScore(2.0d, 0d, 0d), rig.RowScore);
    }

    [Fact]
    public void ARowWithNoAxis_HasNoScore()
    {
        var row = Row(Frame(medianHfr: 2.80d, grading: Grading(sessionFwhm: Grade(1.0d, 2.90d))));

        Assert.Null(row.RowScore);
        Assert.Null(row.ScoreBand);
        Assert.False(row.IsScoreWatch);
        Assert.False(row.IsScoreReject);
    }

    [Fact]
    public void ApplyBaseline_RebuildsTheSixCellsAndTheScore()
    {
        var row = Row(Frame(
            medianHfr: 2.80d,
            eccentricity: 0.55d,
            fwhm: 3.10d,
            detectedStars: 900,
            grading: Grading(
                sessionHfr: Grade(3.2d, 2.10d),
                rigHfr: Grade(-1.4d, 2.95d),
                sessionEccentricity: Grade(3.2d, 0.40d),
                rigEccentricity: Grade(-1.4d, 0.60d),
                sessionFwhm: Grade(3.2d, 2.50d),
                rigFwhm: Grade(-1.4d, 3.30d),
                detectedStars: Grade(0d, 900d))));

        Assert.Equal(QualityBand.Reject, row.HfrCell.Band);
        Assert.Equal(QualityBand.Reject, row.EccentricityCell.Band);
        Assert.Equal(QualityBand.Reject, row.FwhmCell.Band);
        var before = row.RowScore;

        var raised = new List<string>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        row.ApplyBaseline(GradingBaseline.Rig);

        Assert.Equal(QualityBand.Better, row.HfrCell.Band);
        Assert.Equal(QualityBand.Better, row.EccentricityCell.Band);
        Assert.Equal(QualityBand.Better, row.FwhmCell.Band);
        Assert.NotEqual(before, row.RowScore);

        Assert.Contains(nameof(FrameRowViewModel.HfrCell), raised);
        Assert.Contains(nameof(FrameRowViewModel.EccentricityCell), raised);
        Assert.Contains(nameof(FrameRowViewModel.FwhmCell), raised);
        Assert.Contains(nameof(FrameRowViewModel.DetectedStarsCell), raised);
        Assert.Contains(nameof(FrameRowViewModel.GuidingRmsCell), raised);
        Assert.Contains(nameof(FrameRowViewModel.AduMedianCell), raised);
        Assert.Contains(nameof(FrameRowViewModel.RowScore), raised);
    }

    // ---- the tally -------------------------------------------------------------------

    private static FrameRow Scored(double axisZ, string fileName)
        => Frame(
            fileName: fileName,
            medianHfr: 2.80d,
            eccentricity: 0.55d,
            detectedStars: 900,
            grading: Grading(
                sessionHfr: Grade(axisZ, 2.10d),
                sessionEccentricity: Grade(axisZ, 0.40d),
                detectedStars: Grade(axisZ, 1100d)));

    [Fact]
    public void TheTally_CountsBetterAndNeutralAsGood()
    {
        // Scores: -2 gives 83.4 (better), 0 gives 50 (neutral), 1 gives 33.3 (watch), 2 gives 16.6
        // (reject). Better and neutral are one count, which is what makes three counts out of four
        // bands: the watch band exists to be looked at, not to be excluded.
        var harness = TableHarness.Create([
            Scored(-2d, "better.fits"),
            Scored(0d, "neutral.fits"),
            Scored(1d, "watch.fits"),
            Scored(2d, "reject.fits"),
        ]);

        Assert.Equal(2, harness.Table.GoodCount);
        Assert.Equal(1, harness.Table.WatchCount);
        Assert.Equal(1, harness.Table.RejectCount);
        Assert.Equal(0, harness.Table.UngradedCount);
        Assert.True(harness.Table.HasTally);
        Assert.Contains("Good 2", harness.Table.TallyText, StringComparison.Ordinal);
        Assert.Contains("Watch 1", harness.Table.TallyText, StringComparison.Ordinal);
        Assert.Contains("Reject 1", harness.Table.TallyText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTally_IsOverTheLoadedNight_NotTheFilteredRows()
    {
        // Spec 12.4 says "the loaded night's frames". A tally that moved with the outlier filter
        // would make Task 4's Copy Frame List dialog disagree with the table.
        var harness = TableHarness.Create([
            Scored(-2d, "good.fits"),
            Scored(2d, "reject.fits") with { IsHfrOutlier = true },
        ]);

        Assert.Equal(1, harness.Table.GoodCount);
        Assert.Equal(1, harness.Table.RejectCount);

        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);

        Assert.Single(harness.Table.Rows);
        Assert.Equal(1, harness.Table.GoodCount);
        Assert.Equal(1, harness.Table.RejectCount);
    }

    [Fact]
    public void TheTally_ReportsTheUngradedCount_OnlyWhenThereIsOne()
    {
        var graded = TableHarness.Create([Scored(0d, "a.fits")]);
        Assert.Equal(0, graded.Table.UngradedCount);
        Assert.DoesNotContain("ungraded", graded.Table.TallyText, StringComparison.Ordinal);

        var mixed = TableHarness.Create([Scored(0d, "a.fits"), Frame(medianHfr: 2.8d, fileName: "b.fits")]);
        Assert.Equal(1, mixed.Table.UngradedCount);
        Assert.Contains("1 ungraded", mixed.Table.TallyText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTally_RecomputesOnABaselineFlip()
    {
        var frame = Frame(
            medianHfr: 2.80d,
            eccentricity: 0.55d,
            detectedStars: 900,
            grading: Grading(
                sessionHfr: Grade(3d, 2.10d),
                rigHfr: Grade(-3d, 3.40d),
                sessionEccentricity: Grade(3d, 0.40d),
                rigEccentricity: Grade(-3d, 0.70d),
                detectedStars: Grade(0d, 900d)));
        var harness = TableHarness.Create([frame]);

        Assert.Equal(1, harness.Table.RejectCount);
        Assert.Equal(0, harness.Table.GoodCount);

        harness.TargetPage.GradingBaseline = GradingBaseline.Rig;

        Assert.Equal(0, harness.Table.RejectCount);
        Assert.Equal(1, harness.Table.GoodCount);
    }

    /// <summary>The roadmap's own Verify clause: a frame in the reject band that carries no flag
    /// is not what the outlier filter shows. The flags and the bands are two different marks, and
    /// only the flags feed the filter, the findings' counts and the night strip's tall ticks.
    /// </summary>
    [Fact]
    public void TheOutlierFilter_StillEqualsTheFlags_AndNotTheBands()
    {
        var harness = TableHarness.Create([
            // Reject band, no flag.
            Scored(3d, "banded.fits"),
            // Flagged, and its own score is comfortably good.
            Scored(-3d, "flagged.fits") with { IsHfrOutlier = true },
        ]);

        Assert.Equal(1, harness.Table.RejectCount);

        harness.Table.SetOutlierFilter(FrameOutlierFilter.Hfr);

        var shown = Assert.Single(harness.Table.Rows);
        Assert.Equal("flagged.fits", shown.FileName);
        Assert.True(shown.IsHfrOutlier);
        Assert.Equal(QualityBand.Better, shown.ScoreBand);

        // The banded frame is out of the filtered view although it is the reject one.
        Assert.DoesNotContain(harness.Table.Rows, row => row.FileName == "banded.fits");
    }

    // ---- the toggle and its persistence -----------------------------------------------

    [Fact]
    public void SetGradingBaseline_WritesTheDisplayDocument()
    {
        var harness = TableHarness.Create([Scored(0d, "a.fits")]);

        harness.TargetPage.GradingBaseline = GradingBaseline.Rig;

        Assert.Single(harness.Writes);
        Assert.Equal("rig", harness.Display.TargetPage.GradingBaseline);

        harness.TargetPage.GradingBaseline = GradingBaseline.Session;

        Assert.Equal("session", harness.Display.TargetPage.GradingBaseline);
    }

    /// <summary>P13 phase review P2-1: the holder is process wide, so a table built after a flip
    /// takes the flipped value. A per-card snapshot passes every other case in this file and fails
    /// this one.</summary>
    [Fact]
    public void TheBaseline_SurvivesMovingToAnotherNight()
    {
        var frame = Frame(
            medianHfr: 2.80d,
            eccentricity: 0.55d,
            detectedStars: 900,
            grading: Grading(
                sessionHfr: Grade(3d, 2.10d),
                rigHfr: Grade(-3d, 3.40d),
                sessionEccentricity: Grade(3d, 0.40d),
                rigEccentricity: Grade(-3d, 0.70d),
                detectedStars: Grade(0d, 900d)));

        var harness = TableHarness.Create([frame]);
        harness.TargetPage.GradingBaseline = GradingBaseline.Rig;

        // The next night's table, on the same holder.
        var next = new FrameTableViewModel(
            [frame],
            harness.Display,
            new DisplayColumnWriter(() => harness.Display, _ => { }),
            new ShellIntegration(copyText: _ => Task.CompletedTask, start: _ => null),
            openPreview: null,
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            getHeaders: _ => null,
            targetPage: harness.TargetPage);

        Assert.Equal(QualityBand.Better, next.Rows[0].HfrCell.Band);
        Assert.Equal(1, next.GoodCount);
    }

    [Fact]
    public void AFreshProfile_ComparesToTheSession()
    {
        var holder = new TargetPageState();

        Assert.Equal(GradingBaseline.Session, holder.GradingBaseline);
        Assert.Equal("session", new TargetPageSettings().GradingBaseline);
    }

    /// <summary>Spec 5.8.2's closing sentence: a stored value outside a key's listed set reads as
    /// that key's default rather than throwing.</summary>
    [Fact]
    public void AStoredJunkBaseline_ReadsAsSession()
    {
        Assert.Equal(
            GradingBaseline.Session,
            new TargetPageState(new TargetPageSettings { GradingBaseline = "sideways" }).GradingBaseline);
        Assert.Equal(
            GradingBaseline.Session,
            new TargetPageState(new TargetPageSettings { GradingBaseline = "" }).GradingBaseline);

        // And the one value that is not the default parses, case insensitively and ordinally.
        Assert.Equal(
            GradingBaseline.Rig,
            new TargetPageState(new TargetPageSettings { GradingBaseline = "RIG" }).GradingBaseline);
    }
}
