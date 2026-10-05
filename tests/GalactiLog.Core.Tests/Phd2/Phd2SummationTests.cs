using System.Reflection;
using System.Text.Json;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Phd2;
using Xunit;

namespace GalactiLog.Core.Tests.Phd2;

// The summation members of Phd2Metrics, against the Python they port. Every expected value below
// was produced by running the real interpreter on this machine
// (python -c "import math; print(repr(math.fsum([...])))"), not by reading this port's output, and
// every case is stated so that the accumulation it names as wrong fails it.
//
// Phd2MetricsTests is deliberately not touched by this file: those cases pin the stored figures of
// the generated fixtures, and their staying green is the evidence that no figure other than the
// two means moved.
public class Phd2SummationTests
{
    // ---- The real section that found the defect ----

    // PHD2_GuideLog_2026-02-19_172102.txt run 1 section 26, the one session of 937 where the port
    // and the web parted. Literals, not the file: the corpus lives on the user's own drive and no
    // test reads it. Left to right these sum to 2596.6899999999996 and the mean rounds to 324.5862;
    // exactly they sum to 2596.69 and the mean rounds to 324.5863, which is what the web stores.
    private static readonly double[] RealSnrValues =
        [294.32, 325.94, 366.55, 345.13, 336.93, 322.53, 307.26, 298.03];

    [Fact]
    public void ExactMean_OfTheRealSnrSection_RoundsAsTheWebDoes()
    {
        Assert.Equal(2596.69, PythonNumerics.ExactSum(RealSnrValues));
        Assert.Equal(324.58625, PythonNumerics.ExactMean(RealSnrValues));
        Assert.Equal(324.5863, PythonNumerics.RoundLikePython(PythonNumerics.ExactMean(RealSnrValues), 4));
    }

    [Fact]
    public void SessionMetrics_SnrMeanAndStarMassMean_UseTheExactMean()
    {
        // Both stored means, through ComputeSessionMetrics, so the case fails if the members exist
        // but either call site still calls Average. Star mass carries the same eight values, which
        // are not a plausible star mass but are the one sample known to part the two sums; scaling
        // them to a plausible magnitude was tried first and the scaled sum happens to agree, which
        // would have left the star mass site untested.
        var section = Phd2MetricsFixtures.Base(8);
        for (var i = 0; i < section.Frames.Count; i++)
        {
            section.Frames[i] = section.Frames[i] with
            {
                Snr = RealSnrValues[i],
                StarMass = RealSnrValues[i],
            };
        }

        var metrics = Phd2Metrics.ComputeSessionMetrics(
            section, Phd2MetricsFixtures.Zone, Phd2MetricsFixtures.ObserverLongitude,
            useImagingNight: true);

        Assert.Equal(324.5863, metrics.SnrMean);
        Assert.Equal(324.5863, metrics.StarMassMean);
    }

    // ---- math.fsum, the stress table ----

    public static TheoryData<string, double[], double> FsumRows() => new()
    {
        // Total cancellation. Left to right this is 0.0, because 1e100 + 1.0 is 1e100.
        { "cancellation", [1e100, 1.0, -1e100], 1.0 },
        // Ten tenths. Left to right this is 0.9999999999999999.
        { "ten tenths", [0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1], 1.0 },
        // Two ones swallowed by a large partial and returned by the exact sum. Naive gives 0.0.
        { "swallowed ones", [1e16, 1.0, 1.0, -1e16], 2.0 },
        // Naive gives 0.6000000000000001.
        { "tenths and fifths", [0.1, 0.2, 0.3], 0.6 },
        // Ten values far below the unit in the last place of 1.0, which naive addition drops one
        // at a time and never recovers: naive gives exactly 1.0.
        {
            "sub-ulp tail",
            [1.0, 1e-16, 1e-16, 1e-16, 1e-16, 1e-16, 1e-16, 1e-16, 1e-16, 1e-16, 1e-16],
            1.000000000000001
        },
        // A single value is returned unchanged, and the mean of a one-element list is that element.
        { "single value", [3.5], 3.5 },
        // Empty gives 0.0, as math.fsum([]) does.
        { "empty", [], 0.0 },
        // Four values whose exact sum is one unit in the last place above what Neumaier
        // compensated summation gives (9.678491490568681e+17), so this row fails a Kahan or
        // Neumaier implementation while passing a correctly rounded one. Found by searching random
        // magnitude-spread quadruples against math.fsum.
        {
            "beyond compensated",
            [1.805493378223425e+18, 6.764108344655672e-16, -5.639614103655359e+17, -2.73682818801021e+17],
            9.678491490568682e+17
        },
    };

    [Theory]
    [MemberData(nameof(FsumRows))]
    public void ExactSum_MatchesMathFsum(string row, double[] values, double expected)
    {
        Assert.True(expected.Equals(PythonNumerics.ExactSum(values)), row);

        // fsum is correctly rounded and therefore independent of the order of its input, which a
        // compensated sum is not. Reversing every row is a free second assertion on that property.
        Assert.True(expected.Equals(PythonNumerics.ExactSum(values.Reverse().ToArray())), row + ", reversed");
    }

    [Fact]
    public void ExactSum_OfALongAlternatingSeries_MatchesMathFsum()
    {
        // The alternating harmonic series to 1000 terms, which naive addition takes to
        // 0.6926474305598223, nineteen units in the last place away from the exact answer. Built
        // by a rule rather than written out because 1.0 / i is the same double in both languages.
        var values = new double[1000];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (i % 2 == 0 ? 1.0 : -1.0) / (i + 1);
        }

        Assert.Equal(0.6926474305598203, PythonNumerics.ExactSum(values));
    }

    // ---- The builtin sum(), which is a different function ----

    // Every expected value is what the interpreter on this machine, Python 3.14.7, printed for
    // sum(row). 3.14 is the oracle for 3.12 here because the compensation landed in 3.12 and has
    // not changed since; the backend requires ">=3.12" and ships on python:3.12-slim.
    public static TheoryData<string, double[], double> BuiltinSumRows() => new()
    {
        { "empty", [], 0.0 },
        { "cancellation", [1e100, 1.0, -1e100], 1.0 },
        { "ten tenths", [0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1], 1.0 },
        { "swallowed ones", [1e16, 1.0, 1.0, -1e16], 2.0 },
        { "tenths and fifths", [0.1, 0.2, 0.3], 0.6 },
        // Both signed zeros, which Python's sum() returns as a positive zero because it starts
        // from the integer 0. Kept as a regression fence on the compensation terms rather than on
        // the sign, which IEEE equality does not see either way.
        { "negative zeros", [-0.0, -0.0], 0.0 },
        { "mixed zeros", [0.0, -0.0], 0.0 },
        // The non-finite rows. Without the guard on the return each of these is a NaN, because
        // the compensation term of an infinite running total is (inf - inf).
        { "an infinity", [double.PositiveInfinity, 1.0], double.PositiveInfinity },
        { "a negative infinity", [double.NegativeInfinity, 1.0], double.NegativeInfinity },
        { "two infinities", [double.PositiveInfinity, double.NegativeInfinity], double.NaN },
        { "a NaN summand", [double.NaN, 1.0], double.NaN },
        // Finite summands whose partial sum overflows, and then comes back. Python gives the
        // infinity rather than recovering, and rather than a NaN.
        { "intermediate overflow", [1e308, 1e308, -1e308], double.PositiveInfinity },
        // The row the two summations part on: math.fsum gives 9.678491490568682e+17 here, one
        // unit in the last place higher, because Neumaier is not correctly rounded.
        {
            "beyond compensated",
            [1.805493378223425e+18, 6.764108344655672e-16, -5.639614103655359e+17, -2.73682818801021e+17],
            9.678491490568681e+17
        },
    };

    [Theory]
    [MemberData(nameof(BuiltinSumRows))]
    public void CompensatedSum_MatchesTheBuiltinSum(string row, double[] values, double expected)
    {
        Assert.True(expected.Equals(PythonNumerics.CompensatedSum(values)), row);
    }

    [Fact]
    public void AggregateNight_UnguidedSeconds_UsesTheCompensatedSum()
    {
        // phd2_metrics.py:545 is round(sum(r.unguided_seconds for r in rows), 3) over a float
        // column, so the night rollup accumulates compensated while _drop_metrics, which fills the
        // column with an explicit += loop, accumulates left to right. The port keeps both.
        //
        // These five values have a fourth decimal and an exact decimal total of 194.2035, which is
        // the midpoint the third decimal rounds at; compensated they total that in all 120 orders
        // and round down to 194.203, left to right they total 194.20350000000002 in this order,
        // and in 56 of the 120, and round up to 194.204.
        //
        // A fourth decimal, deliberately: ComputeSessionMetrics rounds this column to three before
        // it is ever stored, and a sum of three-decimal values lands on the three-decimal grid and
        // never on its midpoint, so the rounding defends the site on production data. The case
        // pins the accumulation rule rather than a figure a reader could reach today.
        double[] unguided = [26.4945, 14.0059, 38.2212, 69.0902, 46.3917];

        var rows = unguided
            .Select(seconds => new Phd2SessionMetrics { UnguidedSeconds = seconds, FrameCount = 200 })
            .ToList();

        Assert.Equal(194.203, Phd2Metrics.AggregateNight(rows).UnguidedSeconds);
    }

    // ---- The move itself, phase 17 core-shapes.md section 9.1: reflection rather than a source
    // scan, because GalactiLog.Core.Tests carries no SourceScan helper (that is
    // GalactiLog.Data.Tests and GalactiLog.App.Tests TestSupport). Seen red against the
    // pre-move tree, where every one of these four members was still declared on Phd2Metrics and
    // this test's second assertion in each case would have failed.

    public static TheoryData<string> MovedMemberNames() => new()
    {
        nameof(PythonNumerics.RoundLikePython),
        nameof(PythonNumerics.ExactSum),
        nameof(PythonNumerics.ExactMean),
        nameof(PythonNumerics.CompensatedSum),
    };

    [Theory]
    [MemberData(nameof(MovedMemberNames))]
    public void MovedMember_IsReachableOnPythonNumerics_AndAbsentFromPhd2Metrics(string member)
    {
        Assert.NotNull(typeof(PythonNumerics).GetMethod(
            member, BindingFlags.Public | BindingFlags.Static));

        Assert.Null(typeof(Phd2Metrics).GetMethod(
            member, BindingFlags.Public | BindingFlags.Static));
    }

    // ---- WeightedRmsUnrounded, phase 17 records fix pass, ruling S11. Seen red because the
    // member did not exist before this pass: every one of these calls was a compile error.

    [Fact]
    public void WeightedRmsUnrounded_UsesTheSqlTermOrder_NotWeightedRmsTermOrder()
    {
        // Two sessions chosen (by search, verified independently with an IEEE-double calculator
        // before landing) so the two groupings disagree in the last bit: double multiplication is
        // not associative, so analysis.py's phd2_night_subquery (`col * col * fc`, value squared
        // first) and phd2_stats.py's own accumulation (frame count first) are not interchangeable.
        (int FrameCount, double Value)[] rows =
        [
            (3032, 1.826012506471536),
            (29458, 2.0139708232056219),
        ];

        // Hand rolled, not read off either member, so the fixture proves the two groupings really
        // do differ here rather than assuming it.
        var sqlOrder = Math.Sqrt(
            (rows[0].Value * rows[0].Value * rows[0].FrameCount
                + rows[1].Value * rows[1].Value * rows[1].FrameCount)
            / (rows[0].FrameCount + rows[1].FrameCount));
        var statsOrder = Math.Sqrt(
            (rows[0].FrameCount * rows[0].Value * rows[0].Value
                + rows[1].FrameCount * rows[1].Value * rows[1].Value)
            / (rows[0].FrameCount + rows[1].FrameCount));
        Assert.NotEqual(sqlOrder, statsOrder);

        var unrounded = Phd2Metrics.WeightedRmsUnrounded(rows, r => (double?)r.Value, r => r.FrameCount);
        var rounded = Phd2Metrics.WeightedRms(rows, r => (double?)r.Value, r => r.FrameCount);

        Assert.Equal(sqlOrder, unrounded);
        Assert.NotEqual(statsOrder, unrounded);
        Assert.Equal(PythonNumerics.RoundLikePython(statsOrder, 6), rounded);
    }

    // ---- WeightedRmsUnrounded against the five real PHD2 night rows realdata-p17-prep published,
    // read from the checked-in repository file and never from a private copy under C:\tmp. Each
    // night is folded to one effective session carrying its own published frame count and figure:
    // WeightedRmsUnrounded over it is sqrt((v*v*f)/f), which round-trips v exactly for these five
    // rows' real magnitudes (checked before landing) and fails if the port squares, weights or
    // divides differently from analysis.py's own `weighted_rms(col)`. This does not by itself
    // distinguish the two term orders, which the case above does with two rows instead of one;
    // this one instead pins the formula itself against real, published figures.

    public static TheoryData<int, double> Phd2NightRmsFigures()
    {
        var data = new TheoryData<int, double>();
        foreach (var (frameCount, rmsTotal, rmsRa, rmsDec) in ReadPhd2Nights())
        {
            data.Add(frameCount, rmsTotal);
            data.Add(frameCount, rmsRa);
            data.Add(frameCount, rmsDec);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Phd2NightRmsFigures))]
    public void WeightedRmsUnrounded_ReproducesAPublishedPhd2NightFigure_Exactly(
        int frameCount, double publishedRms)
    {
        var rows = new[] { (Value: (double?)publishedRms, FrameCount: frameCount) };

        var result = Phd2Metrics.WeightedRmsUnrounded(rows, r => r.Value, r => r.FrameCount);

        Assert.Equal(publishedRms, result);
    }

    private static IEnumerable<(int FrameCount, double RmsTotal, double RmsRa, double RmsDec)> ReadPhd2Nights()
    {
        var path = Path.Combine(
            RepositoryRoot(), "docs", "superpowers", "work", "phase17", "realdata", "inputs.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var night in document.RootElement.GetProperty("phd2_nights").EnumerateArray())
        {
            yield return (
                night.GetProperty("frame_count").GetInt32(),
                night.GetProperty("phd2_rms_total").GetDouble(),
                night.GetProperty("phd2_rms_ra").GetDouble(),
                night.GetProperty("phd2_rms_dec").GetDouble());
        }
    }

    // Modelled on GalactiLog.Data.Tests.TestSupport.SourceScan.RepositoryRoot: walk up from the
    // test output directory until the solution file is found. Not extracted to a shared helper
    // for one call site in a project that carries no SourceScan today (P3-15).
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalactiLog.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("GalactiLog.sln not found above the test output directory.");
    }
}
