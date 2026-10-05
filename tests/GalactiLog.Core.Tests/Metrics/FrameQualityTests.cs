using GalactiLog.Core.Metrics;
using Xunit;

namespace GalactiLog.Core.Tests.Metrics;

public class FrameQualityTests
{
    // Every fixture below uses values that are exact in binary (whole numbers and halves), so a
    // z-score at the threshold really is 3.0 and not 2.9999999999999987. A baseline built from
    // 0.30 and 0.40 puts the "at the threshold" assertions a rounding error under the bar, which
    // would make the boundary test pass or fail for a reason that has nothing to do with the rule.
    private static GradedFrame Frame(
        string? telescope = "T",
        string? camera = "C",
        string? filter = "L",
        double? hfr = null,
        double? fwhm = null,
        double? eccentricity = null,
        double? stars = null,
        double? aduMedian = null,
        double? guiding = null)
        => new(telescope, camera, filter, hfr, fwhm, eccentricity, stars, aduMedian, guiding);

    /// <summary>Eight frames at 10 and 20, so the eccentricity baseline is median 15, MAD 5 and
    /// N 8: exactly at <see cref="FrameQuality.MinGroup"/>, with a value at 30 landing on
    /// <see cref="FrameQuality.ZReject"/> exactly.</summary>
    private static List<GradedFrame> EccentricityBaselineGroup(string filter = "L")
        => [.. Enumerable.Range(0, 8).Select(i => Frame(filter: filter, eccentricity: i < 4 ? 10d : 20d))];

    // ---- Statistics.Mad, the dispersion every baseline rests on ------------------------

    [Fact]
    public void Mad_UniformGroup_IsZero()
    {
        Assert.Equal(0d, Statistics.Mad([4d, 4d, 4d, 4d]));
    }

    [Fact]
    public void Mad_IgnoresNulls()
    {
        // Median of 10, 20, 30, 40 is 25; deviations 15, 5, 5, 15; their median is 10.
        Assert.Equal(10d, Statistics.Mad([10d, null, 20d, 30d, null, 40d]));
    }

    [Fact]
    public void Mad_Empty_ReturnsNull()
    {
        Assert.Null(Statistics.Mad([]));
        Assert.Null(Statistics.Mad([null, null]));
    }

    // ---- group keys --------------------------------------------------------------------

    [Fact]
    public void GroupKey_NullComponents_RenderAsEmptyStrings()
    {
        Assert.Equal("||", FrameQuality.GroupKey(Frame(null, null, null)));
        Assert.Equal("|ASI2600MM|", FrameQuality.GroupKey(Frame(null, "ASI2600MM", null)));
    }

    [Fact]
    public void GroupKey_Format_IsTelescopePipeCameraPipeFilter()
    {
        Assert.Equal("RC8|ASI2600MM|Ha", FrameQuality.GroupKey(Frame("RC8", "ASI2600MM", "Ha")));
    }

    // ---- baselines ---------------------------------------------------------------------

    [Fact]
    public void GroupBaselines_ComputesMedianMadAndNPerMetricPerGroup()
    {
        var baselines = FrameQuality.GroupBaselines(
        [
            Frame(filter: "L", hfr: 2d, fwhm: 3d),
            Frame(filter: "L", hfr: 4d, fwhm: 3d),
            Frame(filter: "L", hfr: 6d, fwhm: 3d),
            Frame(filter: "Ha", hfr: 100d),
        ]);

        Assert.Equal(["T|C|Ha", "T|C|L"], baselines.Keys.Order());

        var luminance = baselines["T|C|L"]["median_hfr"];
        Assert.Equal(4d, luminance.Median);
        Assert.Equal(2d, luminance.Mad);
        Assert.Equal(3, luminance.N);

        // A uniform metric reports a MAD of 0, which is what makes MadZ decline to grade it.
        var uniform = baselines["T|C|L"]["fwhm"];
        Assert.Equal(3d, uniform.Median);
        Assert.Equal(0d, uniform.Mad);

        // A metric no frame in the group carries is all null with N 0, not a zero baseline.
        var absent = baselines["T|C|L"]["guiding_rms_arcsec"];
        Assert.Null(absent.Median);
        Assert.Null(absent.Mad);
        Assert.Equal(0, absent.N);

        var narrowband = baselines["T|C|Ha"]["median_hfr"];
        Assert.Equal(100d, narrowband.Median);
        Assert.Equal(1, narrowband.N);
    }

    [Fact]
    public void GroupBaselines_NPerMetric_CountsThatMetricsNonNulls_NotTheGroupSize()
    {
        // Ten frames, all carrying an HFR, only three carrying a guiding RMS. If N were the group
        // size the guiding baseline would clear MinGroup on the strength of the HFR column and
        // grade a rig on three measurements.
        var frames = Enumerable.Range(0, 10)
            .Select(i => Frame(hfr: 2d + i, guiding: i < 3 ? 0.5d + i : null))
            .ToList();

        var group = FrameQuality.GroupBaselines(frames)["T|C|L"];

        Assert.Equal(10, group["median_hfr"].N);
        Assert.Equal(3, group["guiding_rms_arcsec"].N);
        Assert.NotNull(FrameQuality.MadZ(20d, group["median_hfr"]));
        Assert.Null(FrameQuality.MadZ(20d, group["guiding_rms_arcsec"]));
    }

    // ---- MadZ --------------------------------------------------------------------------

    [Fact]
    public void MadZ_NullValue_ReturnsNull()
    {
        Assert.Null(FrameQuality.MadZ(null, new MetricBaseline(10d, 2d, 8)));
    }

    [Fact]
    public void MadZ_NullBaseline_ReturnsNull()
    {
        Assert.Null(FrameQuality.MadZ(16d, null));
        // A baseline row can exist with no median or no MAD when the metric was never measured.
        Assert.Null(FrameQuality.MadZ(16d, new MetricBaseline(null, 2d, 8)));
        Assert.Null(FrameQuality.MadZ(16d, new MetricBaseline(10d, null, 8)));
    }

    [Fact]
    public void MadZ_SparseGroup_ReturnsNull()
    {
        Assert.Equal(8, FrameQuality.MinGroup);
        Assert.Null(FrameQuality.MadZ(16d, new MetricBaseline(10d, 2d, 7)));
        Assert.NotNull(FrameQuality.MadZ(16d, new MetricBaseline(10d, 2d, 8)));
    }

    [Fact]
    public void MadZ_UniformGroup_ReturnsNull()
    {
        // A MAD of 0 would divide by the epsilon and report an astronomical z for a frame that
        // differs from a perfectly uniform group by a hair.
        Assert.Null(FrameQuality.MadZ(16d, new MetricBaseline(10d, 0d, 40)));
    }

    [Fact]
    public void MadZ_KnownValues_MatchTheHandComputedScore()
    {
        // (16 - 10) / 2 = 3. Raw MAD units, with no 1.4826 scaling: with it this would be 2.02.
        Assert.Equal(3d, FrameQuality.MadZ(16d, new MetricBaseline(10d, 2d, 8)));
        Assert.Equal(-2.5d, FrameQuality.MadZ(5d, new MetricBaseline(10d, 2d, 8)));
        Assert.Equal(0d, FrameQuality.MadZ(10d, new MetricBaseline(10d, 2d, 8)));
    }

    [Fact]
    public void MadZ_HigherIsBetter_FlipsTheSign()
    {
        var baseline = new MetricBaseline(10d, 2d, 8);

        // Detected stars: more is better, so 16 stars against a median of 10 is good news and
        // must read as a negative z so that "worse than baseline" is always positive.
        Assert.Equal(-3d, FrameQuality.MadZ(16d, baseline, higherIsBetter: true));
        Assert.Equal(3d, FrameQuality.MadZ(4d, baseline, higherIsBetter: true));
    }

    // ---- CountOutliers -----------------------------------------------------------------

    [Fact]
    public void CountOutliers_ReportsGradedZero_WhenNoBaselineIsUsable()
    {
        // Three frames is below MinGroup, so nothing can be judged. Graded 0 is what lets a
        // caller stay silent instead of announcing "no bad frames" about an ungradeable night.
        var sparse = new List<GradedFrame>
        {
            Frame(eccentricity: 10d),
            Frame(eccentricity: 20d),
            Frame(eccentricity: 90d),
        };

        var (outliers, graded) = FrameQuality.CountOutliers(
            sparse,
            FrameQuality.GroupBaselines(sparse),
            FrameQuality.EccentricityMetric);

        Assert.Equal(0, outliers);
        Assert.Equal(0, graded);
    }

    [Fact]
    public void CountOutliers_CountsOnlyFramesAtOrBeyondTheThreshold()
    {
        // Baseline: median 15, MAD 5, N 8. Thresholds are exact: 25 is z 2, 30 is z 3, 35 is z 4.
        var baselines = FrameQuality.GroupBaselines(EccentricityBaselineGroup());

        var (outliers, graded) = FrameQuality.CountOutliers(
            [
                Frame(eccentricity: 25d),
                Frame(eccentricity: 30d),
                Frame(eccentricity: 35d),
                Frame(eccentricity: 0d),
            ],
            baselines,
            FrameQuality.EccentricityMetric);

        // At the threshold counts (the rule is ">= ZReject"), below it does not, and a frame far
        // on the good side is graded but not an outlier.
        Assert.Equal(2, outliers);
        Assert.Equal(4, graded);
    }

    [Fact]
    public void CountOutliers_GradesEachFrameAgainstItsOwnGroup()
    {
        // L: median 15, MAD 5. Ha: median 150, MAD 50. Spec 7.2's whole point: the same number
        // means different things in two groups, so 100 is a disaster in L and unremarkable in Ha.
        var baselines = FrameQuality.GroupBaselines(
        [
            .. EccentricityBaselineGroup("L"),
            .. Enumerable.Range(0, 8).Select(i => Frame(filter: "Ha", eccentricity: i < 4 ? 100d : 200d)),
        ]);

        var (outliers, graded) = FrameQuality.CountOutliers(
            [
                Frame(filter: "L", eccentricity: 100d),
                Frame(filter: "Ha", eccentricity: 100d),
                Frame(filter: "L", eccentricity: 15d),
                Frame(filter: "Ha", eccentricity: 400d),
            ],
            baselines,
            FrameQuality.EccentricityMetric);

        Assert.Equal(2, outliers);
        Assert.Equal(4, graded);
    }

    // ---- MetricBaseline.Of, the one builder ---------------------------------------------

    [Fact]
    public void MetricBaselineOf_DropsTheNulls_SoAllThreeFiguresDescribeTheSameSample()
    {
        // The builder the Phase 15B review folded three copies onto (task5a-review P3-1). N counts
        // the present values and never the length of the list: a table of nine rigs where two carry
        // no figure grades against seven values or not at all, which is what MetricBaseline.N's own
        // summary requires and what FrameQuality.MadZ's MinGroup gate then tests.
        var baseline = MetricBaseline.Of([10d, null, 20d, null, 10d, 20d]);

        Assert.Equal(4, baseline.N);
        Assert.Equal(15d, baseline.Median);
        Assert.Equal(5d, baseline.Mad);
    }

    [Fact]
    public void MetricBaselineOf_AnEmptySample_GradesNothing()
    {
        var baseline = MetricBaseline.Of([null, null]);

        Assert.Equal(0, baseline.N);
        Assert.Null(baseline.Median);
        Assert.Null(baseline.Mad);
        Assert.Null(FrameQuality.MadZ(1d, baseline));
    }

    // ---- GroupLabel --------------------------------------------------------------------

    [Fact]
    public void GroupLabel_AllComponentsEmpty_IsUnknownRig()
    {
        Assert.Equal("Unknown rig", FrameQuality.GroupLabel("||"));
    }

    [Fact]
    public void GroupLabel_JoinsPresentComponentsWithSlashes()
    {
        Assert.Equal("RC8 / ASI2600MM / Ha", FrameQuality.GroupLabel("RC8|ASI2600MM|Ha"));
        Assert.Equal("RC8 / Ha", FrameQuality.GroupLabel("RC8||Ha"));
        Assert.Equal("ASI2600MM", FrameQuality.GroupLabel("|ASI2600MM|"));
    }
}
