using System.Diagnostics;
using System.Globalization;
using GalactiLog.Core.Metrics;
using Xunit;
using Xunit.Abstractions;

namespace GalactiLog.Core.Tests.Metrics;

/// <summary>
/// The distribution half of Task 2 (task2.md sections 5.1 to 5.4, 5.8, 5.9, 5.10 and 5.12):
/// quartiles, fences, the summary stats, the box plot, the histogram, skewness, the moving average
/// and the compare verdict, against <c>backend/app/api/analysis.py</c> at 591234b.
/// </summary>
/// <remarks>
/// Parity is EXACT and has no tolerance (ruling A4): every oracle comparison below is
/// <c>Assert.Equal(expected, actual)</c> on <c>double</c> and never the precision overload. A
/// tolerance is a choice not to see a class of defect.
/// <para>
/// Every expected figure that is not hand computed was produced by running the real functions out
/// of <c>analysis.py</c>; the values come from <c>realdata/inputs.json</c> and the figures from
/// <c>realdata/oracle.json</c>, both read through <see cref="AnalysisOracle"/>. Neither file is
/// written here. Every group label in the oracle is an ordinal or a filter name, so no rig, target
/// or site ever enters this source.
/// </para>
/// </remarks>
public class AnalysisDistributionTests(ITestOutputHelper output)
{
    // ---- 7.1 Every figure against the oracle, exact ----

    [Fact]
    public void Oracle_WasProducedByAnInterpreterWhoseBuiltinSumIsCompensated()
    {
        // The builtin sum()'s Neumaier compensation is a CPython 3.12 change and every figure in
        // the file depends on it, so the interpreter is checked rather than assumed.
        var version = AnalysisOracle.PythonVersion;
        output.WriteLine($"oracle.json python_version: {version}");

        var major = int.Parse(version.Split('.')[0], CultureInfo.InvariantCulture);
        var minor = int.Parse(version.Split('.')[1], CultureInfo.InvariantCulture);
        Assert.True(major > 3 || (major == 3 && minor >= 12), $"oracle produced by {version}");
    }

    [Fact]
    public void Summary_OverTheUsersOwnHfrFrames_MatchesTheOracleExactly()
    {
        var values = AnalysisOracle.Values("hfr");
        var expected = AnalysisOracle.Scenario("distribution_hfr_frame").GetProperty("stats");

        var stats = Analysis.Summary(values);

        Assert.NotNull(stats);
        Assert.Equal(expected.GetProperty("count").GetInt32(), stats.Count);
        Assert.Equal(expected.GetProperty("min").GetDouble(), stats.Min);
        Assert.Equal(expected.GetProperty("max").GetDouble(), stats.Max);
        Assert.Equal(expected.GetProperty("mean").GetDouble(), stats.Mean);
        Assert.Equal(expected.GetProperty("median").GetDouble(), stats.Median);
        Assert.Equal(expected.GetProperty("std_dev").GetDouble(), stats.StdDev);
    }

    [Fact]
    public void Summary_OverOneToFour_MatchesTheHandComputedFigures()
    {
        // Hand computed beside the oracle rows: n 4, mean 2.5, median 2.5, and a SAMPLE variance of
        // (2.25 + 0.25 + 0.25 + 2.25) / 3, whose root is 1.2909944487358056 and rounds to 1.290994.
        // A population divisor of n would give 1.118034 and fail here.
        var stats = Analysis.Summary([1d, 2d, 3d, 4d]);

        Assert.NotNull(stats);
        Assert.Equal(4, stats.Count);
        Assert.Equal(1d, stats.Min);
        Assert.Equal(4d, stats.Max);
        Assert.Equal(2.5d, stats.Mean);
        Assert.Equal(2.5d, stats.Median);
        Assert.Equal(1.290994d, stats.StdDev);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Box_OverTheUsersOwnFilterGroups_MatchesTheOracleExactly(int groupIndex)
    {
        var scenario = AnalysisOracle.Scenario("boxplot_hfr_by_filter");
        var expected = scenario.GetProperty("groups")[groupIndex];
        var name = expected.GetProperty("group_name").GetString()!;
        var values = HfrByFilter(name);

        var box = Analysis.Box(values, name);

        Assert.NotNull(box);
        Assert.Equal(name, box.GroupName);
        Assert.Equal(expected.GetProperty("count").GetInt32(), box.Count);
        Assert.Equal(expected.GetProperty("min").GetDouble(), box.Min);
        Assert.Equal(expected.GetProperty("q1").GetDouble(), box.Q1);
        Assert.Equal(expected.GetProperty("median").GetDouble(), box.Median);
        Assert.Equal(expected.GetProperty("q3").GetDouble(), box.Q3);
        Assert.Equal(expected.GetProperty("max").GetDouble(), box.Max);
        Assert.Equal(AnalysisOracle.Doubles(expected.GetProperty("outliers")), box.Outliers);
    }

    [Fact]
    public void Box_EveryOracleGroupIsPresent_AndNoneWasDroppedUnderFour()
    {
        // The theory above indexes seven rows; if the oracle ever gains or loses a group the
        // indices would silently stop covering it, so the count is pinned here.
        var scenario = AnalysisOracle.Scenario("boxplot_hfr_by_filter");

        Assert.Equal(7, scenario.GetProperty("groups").GetArrayLength());
        Assert.Equal(0, scenario.GetProperty("dropped_under_4").GetArrayLength());
    }

    // ---- 7.2 The sorted-order mean is not the caller-order mean ----

    [Fact]
    public void Summary_SumsTheSortedCopy_AndNotTheCallersOwnOrder()
    {
        // Line 188 of the pinned analysis.py is `mean = sum(s) / n`, where `s` is the SORTED copy
        // built one line above, so the order the values are added in is part of the answer. This
        // four value list is the input on which the two orders part, run through the pinned
        // `_compute_summary_stats` itself:
        //
        //   sum(v)          = 4.156665784275441e+32   -> mean 1.0391664460688602e+32
        //   sum(sorted(v))  = 4.1566657842754416e+32  -> mean 1.0391664460688604e+32
        //
        // and the pinned function answers the second, because it sums `s`. Both survive
        // round(x, 6) unchanged at this magnitude, so the difference reaches the published figure.
        // `Mean` is the ONLY field that parts: the deviation is 1.2050131032020914e+32 under either
        // order, which is why a case on this list has to assert the mean.
        //
        // Two searches of over a million random lists missed this input, one by this implementer
        // and one by a reviewer. A failed search is not a proof of absence.
        double[] values =
        [
            1.9428667965184474e+32, -4.602283722369154, 2.213798987756994e+32, 1.4458726373252628,
        ];

        var callerOrderMean = PythonNumerics.CompensatedSum(values) / values.Length;
        var sortedOrderMean = PythonNumerics.CompensatedSum(values.Order()) / values.Length;
        output.WriteLine($"caller order: {callerOrderMean:R}");
        output.WriteLine($"sorted order: {sortedOrderMean:R}");

        // The C# compensated sum reproduces the pinned interpreter's two totals bit for bit.
        Assert.Equal(4.156665784275441e+32d, PythonNumerics.CompensatedSum(values));
        Assert.Equal(4.1566657842754416e+32d, PythonNumerics.CompensatedSum(values.Order()));
        Assert.NotEqual(callerOrderMean, sortedOrderMean);

        var stats = Analysis.Summary(values);

        Assert.NotNull(stats);
        Assert.Equal(4, stats.Count);
        Assert.Equal(-4.602284d, stats.Min);
        Assert.Equal(2.213798987756994e+32d, stats.Max);
        Assert.Equal(1.0391664460688604e+32d, stats.Mean);
        Assert.Equal(9.714333982592237e+31d, stats.Median);
        Assert.Equal(1.2050131032020914e+32d, stats.StdDev);

        // Said the other way round, so a reader sees which figure the defect would ship.
        Assert.NotEqual(PythonNumerics.RoundLikePython(callerOrderMean, 6), stats.Mean);
    }

    // ---- 7.3 CompensatedSum is not Enumerable.Sum ----

    [Fact]
    public void Summary_OverAMagnitudeSpread_TakesTheCompensatedSumAndNotEnumerableSum()
    {
        // Found by search against the real interpreter, ascending so the sort cannot be what makes
        // the difference: CPython's builtin sum() (Neumaier, which CompensatedSum ports) gives a
        // mean that rounds to -931659205.724467 while a naive left to right total rounds to
        // -931659205.724466. The eight PHD2 SNR values of PythonNumerics' own remark do NOT serve
        // here: once sorted, the naive total agrees with the compensated one to the last bit.
        double[] values =
        [
            -9799273004.279243, -329561199.5196677, -8193400.201880168, -73.34361095426432,
            -0.7259183434779448, -0.45222310460630144, -1.8375404415923715e-06,
            0.16586620074576675, 3691.8369094239843, 820431929.2751052,
        ];

        var naive = 0d;
        foreach (var value in values)
        {
            naive += value;
        }

        output.WriteLine($"compensated: {PythonNumerics.CompensatedSum(values) / values.Length:R}");
        output.WriteLine($"naive:       {naive / values.Length:R}");
        output.WriteLine($"Enumerable.Sum: {values.Sum() / values.Length:R}");

        var stats = Analysis.Summary(values);

        Assert.NotNull(stats);
        Assert.Equal(-931659205.724467d, stats.Mean);

        // Both of the shapes the defect would actually ship: a hand written left to right loop AND
        // Enumerable.Sum, which is the expression an implementer reaches for because it reads
        // better. Asserting only the loop would leave the case discriminating nothing on a runtime
        // where the two disagree.
        Assert.NotEqual(-931659205.724467d, PythonNumerics.RoundLikePython(naive / values.Length, 6));
        Assert.NotEqual(
            -931659205.724467d, PythonNumerics.RoundLikePython(values.Sum() / values.Length, 6));
    }

    // ---- 7.4 The fences correction changes no flag ----

    [Theory]
    [InlineData("even count")]
    [InlineData("odd count")]
    [InlineData("planted extreme")]
    public void Fences_ComputedOncePerAxis_FlagEveryPointAsTheLiteralPythonDoes(string shape)
    {
        // Each of the three was found by search so that the fences of the FULL set and the fences
        // of any prefix of it flag different points. A tamer set passes under either, which is
        // exactly the failure an identity case has to avoid: the defect hiding in a refactor of
        // this kind is a precomputation taken over the wrong set, not over no set at all.
        double[] values = shape switch
        {
            "even count" => [18.6, 268.2, 13.8, 14.3, 8.0, 13.4, 7.5, 18.0],
            "odd count" => [9.3, 14.6, 13.5, 19.7, 2.0, 8.1, 436.8, 17.2, 5.0],
            _ => [13.0, 12.2, 3.4, 14.6, 3.3, 87.0, 19.8, 12.8, 11.1, 13.7, 16.9, 15.5],
        };

        var fences = Analysis.Fences(values);
        var ported = values.Select(v => Analysis.IsOutlier(v, fences)).ToArray();
        var literal = values.Select(v => IsOutlierIqrLiteral(v, values)).ToArray();

        Assert.Equal(literal, ported);
        Assert.Contains(true, ported);
    }

    [Fact]
    public void Fences_OverTheUsersOwnPhd2AndHfrAxes_FlagEveryPointAsTheLiteralPythonDoes()
    {
        // The correction's real shape: 416 points, both axes, which is the call the Python makes
        // once per point. The flag vectors are compared element for element on the union, which is
        // what the endpoint's outlier flag is.
        var (xs, ys) = AnalysisOracle.Pairs("phd2_rms_total", "hfr");
        var xFences = Analysis.Fences(xs);
        var yFences = Analysis.Fences(ys);

        var ported = new bool[xs.Count];
        var literal = new bool[xs.Count];
        for (var i = 0; i < xs.Count; i++)
        {
            ported[i] = Analysis.IsOutlier(xs[i], xFences) || Analysis.IsOutlier(ys[i], yFences);
            literal[i] = IsOutlierIqrLiteral(xs[i], xs) || IsOutlierIqrLiteral(ys[i], ys);
        }

        Assert.Equal(literal, ported);
    }

    // ---- 7.5 Quartile halves on even and odd counts ----

    [Theory]
    [InlineData(4, 1.5, 3.5)]
    [InlineData(5, 1.5, 4.5)]
    [InlineData(6, 2.0, 5.0)]
    [InlineData(7, 2.0, 6.0)]
    public void QuartilesOf_UsesTheExclusiveMethod(int count, double q1, double q3)
    {
        var values = Enumerable.Range(1, count).Select(i => (double)i).ToArray();

        var quartiles = Analysis.QuartilesOf(values);

        Assert.Equal(q1, quartiles.Q1);
        Assert.Equal(q3, quartiles.Q3);
    }

    [Fact]
    public void QuartilesOf_OnAnOddCount_LeavesTheMiddleValueInNeitherHalf()
    {
        // The inclusive method, s[: (n + 1) // 2], is the other common convention and would give
        // 2 and 4 here. Only an odd count can tell them apart: integer division makes the two
        // slice expressions equal for an even n.
        var quartiles = Analysis.QuartilesOf([1d, 2d, 3d, 4d, 5d]);

        Assert.Equal(1.5d, quartiles.Q1);
        Assert.Equal(4.5d, quartiles.Q3);
    }

    // ---- 7.6 The histogram, defect D1, both figures asserted ----

    [Fact]
    public void Histogram_OverAnOrdinarySpread_RecomputesEveryEdgeFromItsOwnIndex()
    {
        // Ten values, five Sturges bins of width 1.8, every bin holding two. Bin 2's unrounded end
        // is 6.3999999999999995 while bin 3's unrounded start is 6.4, which is what "end is
        // start + width and start is recomputed from i" produces; six decimals hide the pair, so
        // this case pins the bin count, the edges as a reader sees them and the counts, and the
        // unrounded distinction is not observable through HistogramBin at all (seam review P2-4
        // says the same of the oracle's tenth edge).
        var bins = Analysis.Histogram([.. Enumerable.Range(1, 10).Select(i => (double)i)]);

        Assert.Equal(5, bins.Count);
        Assert.Equal(new[] { 1.0, 2.8, 4.6, 6.4, 8.2 }, bins.Select(b => b.BinStart));
        Assert.Equal(new[] { 2.8, 4.6, 6.4, 8.2, 10.0 }, bins.Select(b => b.BinEnd));
        Assert.Equal(new[] { 2, 2, 2, 2, 2 }, bins.Select(b => b.Count));
        Assert.Equal(10, bins.Sum(b => b.Count));
    }

    [Fact]
    public void Histogram_OnAConstantMetric_KeepsThePythonsOwnSingleBinShape()
    {
        // Ruling S8: v_max equals v_min, so the width is the Python's literal 1.0 and U1's
        // last-edge rule does NOT apply. Forcing the last edge to v_max here would publish an
        // INVERTED bin, BinStart 7.5 with BinEnd 3.5, and the counts would still sum to n, so only
        // the edge assertions below catch it.
        var scenario = AnalysisOracle.Scenario("distribution_constant_metric");
        var expected = scenario.GetProperty("u1_bins");
        var values = Enumerable.Repeat(scenario.GetProperty("v_min").GetDouble(), 10).ToArray();

        var bins = Analysis.Histogram(values);

        Assert.Equal(expected.GetArrayLength(), bins.Count);
        for (var i = 0; i < bins.Count; i++)
        {
            Assert.Equal(expected[i].GetProperty("bin_start").GetDouble(), bins[i].BinStart);
            Assert.Equal(expected[i].GetProperty("bin_end").GetDouble(), bins[i].BinEnd);
            Assert.Equal(expected[i].GetProperty("count").GetInt32(), bins[i].Count);
            Assert.True(bins[i].BinEnd > bins[i].BinStart, $"bin {i} is inverted");
        }

        Assert.Equal(1, bins.Count(b => b.Count > 0));
        Assert.Equal(1.0d, bins[0].BinEnd - bins[0].BinStart);
        Assert.Equal(values.Length, bins.Sum(b => b.Count));

        // "The rule does not apply on this branch", as a figure: the two sums are equal.
        Assert.Equal(
            scenario.GetProperty("bin_count_sum").GetInt32(),
            scenario.GetProperty("u1_bin_count_sum").GetInt32());
    }

    [Fact]
    public void Histogram_OverTheUsersOwn416Frames_CountsEveryValueWhereTheWebLosesTheMaximum()
    {
        // Defect D1, user ruling U1 (choice 19). The evidence the user ruled on is asserted here
        // with BOTH figures: the shipped rule's 416 and the web's own 415. Deleting the second is a
        // review finding.
        var scenario = AnalysisOracle.Scenario("distribution_hfr_frame");
        var values = AnalysisOracle.Values("hfr");

        var bins = Analysis.Histogram(values);

        // 1. The rounded edges. The oracle publishes bin_end 2.6245 for the tenth bin either way,
        // because round(2.6244999999999994, 6) and round(2.6245, 6) are the same double, so the
        // departure is not observable in the edges at all.
        var webBins = scenario.GetProperty("bins");
        var u1Bins = scenario.GetProperty("u1_bins");
        Assert.Equal(10, bins.Count);
        for (var i = 0; i < bins.Count; i++)
        {
            Assert.Equal(u1Bins[i].GetProperty("bin_start").GetDouble(), bins[i].BinStart);
            Assert.Equal(u1Bins[i].GetProperty("bin_end").GetDouble(), bins[i].BinEnd);
            Assert.Equal(webBins[i].GetProperty("bin_end").GetDouble(), bins[i].BinEnd);
            Assert.Equal(u1Bins[i].GetProperty("count").GetInt32(), bins[i].Count);
        }

        // 2. The shipped counts sum to every value, and the last bin reads 1 where the web's reads 0.
        Assert.Equal(scenario.GetProperty("u1_bin_count_sum").GetInt32(), bins.Sum(b => b.Count));
        Assert.Equal(values.Count, bins.Sum(b => b.Count));
        Assert.Equal(1, bins[^1].Count);
        Assert.Equal(0, webBins[9].GetProperty("count").GetInt32());

        // 3. The web's own counts sum to 415 of 416, which is the whole of D1, and the three
        // fields that say why.
        var webSum = scenario.GetProperty("bin_count_sum").GetInt32();
        Assert.Equal(415, webSum);
        Assert.Equal(416, scenario.GetProperty("value_count").GetInt32());
        Assert.False(
            scenario.GetProperty("last_edge_equals_v_max").GetBoolean(),
            $"the web's last edge {scenario.GetProperty("last_edge_full").GetDouble():R} is not "
            + $"v_max {scenario.GetProperty("v_max").GetDouble():R}, so its counts sum to {webSum} "
            + $"of {values.Count} and the maximum frame is admitted to no bin at all");

        output.WriteLine($"shipped (U1) bin counts sum to {bins.Sum(b => b.Count)}; the web's own sum to {webSum}");
    }

    // ---- 7.7 Skewness: rounded inputs, and the integer overflow ----

    [Fact]
    public void Skewness_ReadsTheAlreadyRoundedMeanAndDeviation()
    {
        var scenario = AnalysisOracle.Scenario("distribution_hfr_frame");
        var values = AnalysisOracle.Values("hfr");
        var stats = Analysis.Summary(values)!;

        var skewness = Analysis.Skewness(values, stats.Mean, stats.StdDev);

        Assert.Equal(scenario.GetProperty("skewness_full").GetDouble(), skewness);
        Assert.Equal(
            scenario.GetProperty("skewness").GetDouble(),
            PythonNumerics.RoundLikePython(skewness, 4));

        // The rounding dependency documented by a passing assertion rather than by a comment: the
        // full precision pair is a different figure and is NOT what the web returns.
        var sorted = values.Order().ToArray();
        var unroundedMean = PythonNumerics.CompensatedSum(sorted) / sorted.Length;
        var unroundedStdDev = Math.Sqrt(
            PythonNumerics.CompensatedSum(sorted.Select(v => (v - unroundedMean) * (v - unroundedMean)))
            / (sorted.Length - 1));
        var fromUnrounded = Analysis.Skewness(values, unroundedMean, unroundedStdDev);

        output.WriteLine($"from the rounded pair:   {skewness:R}");
        output.WriteLine($"from the unrounded pair: {fromUnrounded:R}");

        Assert.NotEqual(skewness, fromUnrounded);
        Assert.Equal(
            scenario.GetProperty("skewness_from_unrounded_mean_and_std").GetDouble(), fromUnrounded);
    }

    [Fact]
    public void Skewness_AboveTheIntegerOverflowCount_KeepsItsSign()
    {
        // The first n at which (n - 1) * (n - 2) overflows a signed 32 bit product is 46,343.
        // 50,000 is past it and is chosen because 49,999 * 49,998 is 2,499,850,002, which wraps to
        // -1,795,117,294: an int denominator there flips the sign silently.
        var values = new double[50000];
        values[^1] = 1000d;
        var stats = Analysis.Summary(values)!;

        var skewness = Analysis.Skewness(values, stats.Mean, stats.StdDev);

        output.WriteLine($"n {values.Length}, mean {stats.Mean:R}, std {stats.StdDev:R}, skewness {skewness:R}");

        Assert.True(skewness > 0d, $"a right-skewed set gave {skewness:R}");
        Assert.Equal(223.6068d, PythonNumerics.RoundLikePython(skewness, 4));
    }

    // ---- 7.8 Degenerate inputs answer, never throw ----

    [Theory]
    [InlineData("empty")]
    [InlineData("one value")]
    [InlineData("two values")]
    [InlineData("three values")]
    [InlineData("all identical")]
    [InlineData("two identical")]
    public void Degenerate_Inputs_Answer_AndNeverThrow(string shape)
    {
        double[] values = shape switch
        {
            "empty" => [],
            "one value" => [3.5],
            "two values" => [3.5, 4.5],
            "three values" => [3.5, 4.5, 5.5],
            "all identical" => [3.5, 3.5, 3.5, 3.5, 3.5],
            _ => [3.5, 3.5],
        };

        var stats = Analysis.Summary(values);
        var box = Analysis.Box(values, shape);
        var bins = Analysis.Histogram(values);
        var skewness = Analysis.Skewness(values, stats?.Mean ?? 0d, stats?.StdDev ?? 0d);
        var moving = Analysis.MovingAverage(values, Analysis.MovingAverageShortWindow);

        if (values.Length < 2)
        {
            Assert.Null(stats);
        }
        else
        {
            Assert.NotNull(stats);
        }

        // Box answers null below FOUR values and not below two: the five identical values are a
        // legal group whose quartiles, fences and whiskers all collapse onto the one value.
        if (values.Length < 4)
        {
            Assert.Null(box);
        }
        else
        {
            Assert.NotNull(box);
            Assert.Equal(values.Length, box.Count);
            Assert.Empty(box.Outliers);
            Assert.Equal(box.Min, box.Max);
        }

        // Every value lands in a bin on every one of these shapes, the constant branch included.
        Assert.Equal(values.Length, bins.Sum(b => b.Count));

        if (values.Length == 1)
        {
            Assert.Single(bins);
            Assert.Equal(1.0d, bins[0].BinEnd - bins[0].BinStart);
            Assert.Equal(1, bins[0].Count);
        }

        // Zero spread or fewer than three values: the Python answers 0.0 and never divides by zero.
        // The three-value row is the one that reaches the arithmetic, and its cubed residuals
        // cancel to 0.0 because the set is symmetric about its mean.
        Assert.Equal(0d, skewness);
        Assert.Empty(moving);
    }

    [Fact]
    public void QuartilesOf_BelowTwoValues_RefusesRatherThanAnsweringZero()
    {
        // Python would raise from statistics.median on the empty lower half. Every caller guards
        // at 2 or 4, so this is a programming error and a silent zero would hide one.
        Assert.Throws<ArgumentException>(() => Analysis.QuartilesOf([1d]));
        Assert.Throws<ArgumentException>(() => Analysis.QuartilesOf([]));
    }

    [Fact]
    public void MovingAverage_WithANonPositiveWindow_Refuses()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Analysis.MovingAverage([1d, 2d], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Analysis.MovingAverage([1d, 2d], -7));
    }

    // ---- 7.11 CompareVerdict, and defect D2 with both figures ----

    [Fact]
    public void CompareVerdict_NamesTheLowerGroupFirst_AndSwapsBothCounts()
    {
        Assert.Equal(
            "A has 25% lower median than B (N=10 vs N=20)",
            Analysis.CompareVerdict("A", "B", 3d, 4d, 10, 20, string.Empty));

        // B lower: the names AND the two N values swap, so the sentence's own N comes first.
        Assert.Equal(
            "B has 25% lower median than A (N=20 vs N=10)",
            Analysis.CompareVerdict("A", "B", 4d, 3d, 10, 20, string.Empty));
    }

    [Fact]
    public void CompareVerdict_OnEqualMedians_SaysSo()
    {
        Assert.Equal(
            "Both groups have identical median values (N=10 vs N=20)",
            Analysis.CompareVerdict("A", "B", 4d, 4d, 10, 20, string.Empty));
    }

    [Fact]
    public void CompareVerdict_WithAZeroMedian_DividesByTheLargerOne()
    {
        // User ruling U2: the basis is the larger median. With medianA zero the basis is B, which
        // is what the web's own fallback branch also reaches, and the answer is a full 100 percent.
        Assert.Equal(
            "A has 100% lower median than B (N=4 vs N=5)",
            Analysis.CompareVerdict("A", "B", 0d, 4d, 4, 5, string.Empty));

        // Both zero: no basis, no percentage, and the medians are equal so the third branch wins.
        Assert.Equal(
            "Both groups have identical median values (N=4 vs N=5)",
            Analysis.CompareVerdict("A", "B", 0d, 0d, 4, 5, string.Empty));
    }

    [Fact]
    public void CompareVerdict_FormatsThePercentageHalfToEvenOnTheExactValue()
    {
        // 1 in 40 is exactly 2.5 percent and prints 2, and 3 in 200 is exactly 1.5 and prints 2:
        // half to even in both directions. Math.Round(pct, MidpointRounding.AwayFromZero) prints
        // 3 and 2 and fails the first.
        Assert.Equal(
            "A has 2% lower median than B (N=4 vs N=4)",
            Analysis.CompareVerdict("A", "B", 39d, 40d, 4, 4, string.Empty));
        Assert.Equal(
            "A has 2% lower median than B (N=4 vs N=4)",
            Analysis.CompareVerdict("A", "B", 197d, 200d, 4, 4, string.Empty));
    }

    [Fact]
    public void CompareVerdict_CarriesTheArcsecondUnitWithItsLeadingSpace()
    {
        Assert.Equal(
            "A has 25% lower median (arcsec) than B (N=10 vs N=20)",
            Analysis.CompareVerdict("A", "B", 3d, 4d, 10, 20, " (arcsec)"));
    }

    [Theory]
    [InlineData("compare_two_rigs_hfr")]
    [InlineData("compare_ha_oiii_hfr")]
    public void CompareVerdict_OnTheUsersOwnComparisons_DividesByTheLargerMedian(string scenarioName)
    {
        // Defect D2, user ruling U2 (choice 20). BOTH figures are asserted: the shipped percentage
        // and the web's own, which reads over 100 percent lower on a sentence that says "lower".
        // Deleting the second assertion is a review finding.
        var scenario = AnalysisOracle.Scenario(scenarioName);
        var groupA = scenario.GetProperty("group_a").GetProperty("name").GetString()!;
        var groupB = scenario.GetProperty("group_b").GetProperty("name").GetString()!;
        var medianA = scenario.GetProperty("median_hfr_arcsec_a").GetDouble();
        var medianB = scenario.GetProperty("median_hfr_arcsec_b").GetDouble();
        var countA = scenario.GetProperty("group_a").GetProperty("stats").GetProperty("count").GetInt32();
        var countB = scenario.GetProperty("group_b").GetProperty("stats").GetProperty("count").GetInt32();

        var shipped = Analysis.CompareVerdict(groupA, groupB, medianA, medianB, countA, countB, " (arcsec)");

        var webPercent = scenario.GetProperty("pct_web_rounded").GetDouble();
        var shippedPercent = scenario.GetProperty("pct_u2_rounded").GetDouble();
        var webVerdict = scenario.GetProperty("verdict").GetString()!;

        output.WriteLine($"shipped: {shipped}");
        output.WriteLine($"the web: {webVerdict}");

        // The sentence is the web's in every respect but the percentage, which is asserted by
        // rebuilding the web's own verdict with the corrected figure substituted.
        var expected = webVerdict.Replace(
            $"{webPercent.ToString("0", CultureInfo.InvariantCulture)}%",
            $"{shippedPercent.ToString("0", CultureInfo.InvariantCulture)}%",
            StringComparison.Ordinal);
        Assert.Equal(expected, shipped);

        // The web's own figure, recorded: a percentage the sentence cannot mean.
        Assert.Contains(
            $"{shippedPercent.ToString("0", CultureInfo.InvariantCulture)}% lower",
            shipped,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"{webPercent.ToString("0", CultureInfo.InvariantCulture)}% lower",
            shipped,
            StringComparison.Ordinal);
        Assert.Equal(
            webPercent,
            PythonNumerics.RoundLikePython(
                Math.Abs(medianA - medianB) / Math.Abs(medianA) * 100d, 0));
    }

    [Fact]
    public void CompareVerdict_OnTheTwoRigs_ShipsFiftySixWhereTheWebShipsOneHundredAndTwentySix()
    {
        // The two literals the user ruled on, named rather than only derived, so a reader of this
        // file finds the evidence without opening the oracle.
        var scenario = AnalysisOracle.Scenario("compare_two_rigs_hfr");

        Assert.Equal(1.772d, scenario.GetProperty("median_hfr_arcsec_a").GetDouble());
        Assert.Equal(3.997d, scenario.GetProperty("median_hfr_arcsec_b").GetDouble());
        Assert.Equal(126d, scenario.GetProperty("pct_web_rounded").GetDouble());
        Assert.Equal(56d, scenario.GetProperty("pct_u2_rounded").GetDouble());
    }

    // ---- 7.12 The moving average window ----

    [Fact]
    public void MovingAverage_EmitsNothingUntilTheWindowIsFull()
    {
        var values = Enumerable.Range(1, 10).Select(i => (double)i).ToArray();

        // Window 7 over 1..10: the first six positions emit nothing, and the four that follow are
        // the means of 1..7, 2..8, 3..9 and 4..10. A partial window at the start would draw a line
        // from the first night and make the first week look calm.
        Assert.Empty(Analysis.MovingAverage(values[..6], 7));
        Assert.Equal(new[] { (6, 4d) }, Analysis.MovingAverage(values[..7], 7));
        Assert.Equal(
            new[] { (6, 4d), (7, 5d), (8, 6d), (9, 7d) },
            Analysis.MovingAverage(values, 7));
        Assert.Empty(Analysis.MovingAverage([.. Enumerable.Range(0, 29).Select(i => (double)i)], 30));
    }

    [Fact]
    public void MovingAverage_OnTheUsersOwnThreeNights_EmitsNothingForEitherWindow()
    {
        // The real shape of this library: three nights, so neither the 7 nor the 30 point window
        // ever fills and both oracle arrays are empty. The window counts POINTS and not calendar
        // days, so the gap between the July and September nights does not fill one either.
        var scenario = AnalysisOracle.Scenario("timeseries_hfr");
        var medians = scenario.GetProperty("points").EnumerateArray()
            .Select(point => point.GetProperty("value").GetDouble())
            .ToArray();

        Assert.Equal(3, medians.Length);
        Assert.Equal(0, scenario.GetProperty("ma_7").GetArrayLength());
        Assert.Equal(0, scenario.GetProperty("ma_30").GetArrayLength());
        Assert.Empty(Analysis.MovingAverage(medians, Analysis.MovingAverageShortWindow));
        Assert.Empty(Analysis.MovingAverage(medians, Analysis.MovingAverageLongWindow));
    }

    // ---- 7.12a The shapes the user's own library actually has ----

    [Fact]
    public void AMetricWithNoValuesAtAll_AnswersTotally_AndNeverApproximately()
    {
        // sky_quality is null on all 416 frames (realdata-prep-report.md S1), so the empty list is
        // the common path on this page rather than the edge.
        var values = AnalysisOracle.Values("sky_quality");

        Assert.Empty(values);
        Assert.Null(Analysis.Summary(values));
        Assert.Null(Analysis.Box(values, "1"));
        Assert.Empty(Analysis.Histogram(values));
        Assert.Equal(0d, Analysis.Skewness(values, 0d, 0d));
        Assert.Empty(Analysis.MovingAverage(values, Analysis.MovingAverageShortWindow));
    }

    [Fact]
    public void AnXTakingFiveDistinctValuesOver416Points_FlagsTheOraclesSevenOutliers()
    {
        // The PHD2 step function (realdata-prep-report.md S8): five night rows joined to 416
        // frames. The endpoint's outlier flag is the union over the two axes and the oracle counts
        // seven.
        var scenario = AnalysisOracle.Scenario("correlation_phd2_rms_total_hfr_frame");
        var (xs, ys) = AnalysisOracle.Pairs("phd2_rms_total", "hfr");
        var xFences = Analysis.Fences(xs);
        var yFences = Analysis.Fences(ys);

        var flagged = 0;
        for (var i = 0; i < xs.Count; i++)
        {
            if (Analysis.IsOutlier(xs[i], xFences) || Analysis.IsOutlier(ys[i], yFences))
            {
                flagged++;
            }
        }

        Assert.Equal(scenario.GetProperty("total_count").GetInt32(), xs.Count);
        Assert.Equal(5, xs.Distinct().Count());
        Assert.Equal(scenario.GetProperty("outlier_count").GetInt32(), flagged);
    }

    [Fact]
    public void AZeroHfrIsARealValue_AndNothingHereFiltersIt()
    {
        // Six of the user's 416 frames carry an HFR of exactly 0.0, every one with 0 or 1 stars
        // (realdata-prep-report.md S10). A reader will want to drop them as failed frames. NOTHING
        // in analysis.py does, so nothing here may: they survive into the minimum, into bin 0 and
        // into the skewness, which they drive to the oracle's -1.9677.
        var scenario = AnalysisOracle.Scenario("distribution_hfr_frame");
        var values = AnalysisOracle.Values("hfr");
        var stats = Analysis.Summary(values)!;
        var bins = Analysis.Histogram(values);

        Assert.Equal(6, values.Count(v => v == 0d));
        Assert.Equal(0d, stats.Min);
        Assert.Equal(6, bins[0].Count);
        Assert.Equal(
            scenario.GetProperty("skewness").GetDouble(),
            PythonNumerics.RoundLikePython(Analysis.Skewness(values, stats.Mean, stats.StdDev), 4));
    }

    // ---- 7.13 Two hundred thousand values, timed ----

    [Fact]
    public void TheHelpers_OverTwoHundredThousandValues_CompleteUnderABudget()
    {
        const int Points = 200_000;
        var xs = new double[Points];
        var ys = new double[Points];
        for (var i = 0; i < Points; i++)
        {
            xs[i] = (i % 977) * 0.37 + (i % 13) * 0.011;
            ys[i] = (i % 613) * 0.53 - (i % 7) * 0.043;
        }

        // A handful of planted extremes so the flag loop has something to flag and the figure is
        // not taken over a set where every point takes the same branch.
        for (var i = 0; i < Points; i += 40_000)
        {
            xs[i] = 5_000d;
            ys[i] = -5_000d;
        }

        // Warm the code paths so the figures measure the work and not the first call's jitting.
        _ = Analysis.Summary(xs[..1000]);
        _ = Analysis.Histogram(xs[..1000]);

        var best = double.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var watch = Stopwatch.StartNew();

            var xFences = Analysis.Fences(xs);
            var yFences = Analysis.Fences(ys);
            var flagged = 0;
            for (var i = 0; i < Points; i++)
            {
                if (Analysis.IsOutlier(xs[i], xFences) || Analysis.IsOutlier(ys[i], yFences))
                {
                    flagged++;
                }
            }

            _ = Analysis.Trend(xs, ys);
            _ = Analysis.Summary(xs);
            _ = Analysis.Summary(ys);
            _ = Analysis.Histogram(ys);

            watch.Stop();
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
            output.WriteLine($"attempt {attempt + 1}: {watch.Elapsed.TotalMilliseconds:0.0} ms, {flagged} flagged");
        }

        output.WriteLine($"fences, flags, the trend, two summaries and a histogram over {Points} values, best of 3: {best:0.0} ms");

        // The wall clock figures above are printed EVIDENCE and nothing is asserted against them.
        // An absolute budget here is decoration by this project's own rule (TRACKING section 5: a
        // performance budget is proven against the defect it names, not set from a comfortable
        // margin). Best of three has read between 210 and 590 ms on this machine depending on how
        // many other builds were running, so any figure loose enough to survive contention is far
        // too loose to fail for anything but a timeout. The gate below is the real one, and it is
        // load invariant because both of its sides are measured in the same process at the same
        // moment.

        // The budget is proven to FAIL against the defect it names: the Python's own shape, which
        // sorts both axes once per point. The axes carry 20,000 points, so every literal call sorts
        // 20,000 values, exactly as the Python's would; only a SAMPLE of the points is probed,
        // because all 20,000 of them take four and a half minutes and the figure compared is the
        // PER POINT cost either way. At 200,000 the literal shape is hours, so it is never run there.
        const int QuadraticPoints = 20_000;
        const int ProbedPoints = 200;
        var smallXs = xs[..QuadraticPoints];
        var smallYs = ys[..QuadraticPoints];

        const int Repeats = 20;
        var portWatch = Stopwatch.StartNew();
        for (var repeat = 0; repeat < Repeats; repeat++)
        {
            var xFences = Analysis.Fences(smallXs);
            var yFences = Analysis.Fences(smallYs);
            for (var i = 0; i < QuadraticPoints; i++)
            {
                _ = Analysis.IsOutlier(smallXs[i], xFences) || Analysis.IsOutlier(smallYs[i], yFences);
            }
        }

        portWatch.Stop();
        var portPerPoint = portWatch.Elapsed.TotalMilliseconds / Repeats / QuadraticPoints;

        var literalWatch = Stopwatch.StartNew();
        for (var i = 0; i < ProbedPoints; i++)
        {
            _ = IsOutlierIqrLiteral(smallXs[i], smallXs) || IsOutlierIqrLiteral(smallYs[i], smallYs);
        }

        literalWatch.Stop();
        var literalPerPoint = literalWatch.Elapsed.TotalMilliseconds / ProbedPoints;

        output.WriteLine(
            $"per point over {QuadraticPoints} point axes: precomputed fences {portPerPoint:0.00000} ms "
            + $"(over {Repeats} passes), the Python's per point shape {literalPerPoint:0.000} ms "
            + $"(over {ProbedPoints} probed points), ratio {literalPerPoint / portPerPoint:0}x");

        // The floor is set from measurement and not from comfort: two independent readings of this
        // ratio, 41,158x by the implementer and 38,718x by the reviewer on the same machine, leave
        // about 38x of headroom above 1,000. If Fences regressed to a per-point sort the port's own
        // per point cost would rise to the literal shape's magnitude and the ratio would collapse
        // towards 1, which is what this floor catches; it was proved red that way.
        Assert.True(
            literalPerPoint >= 1000d * portPerPoint,
            $"the quadratic shape cost {literalPerPoint:0.000} ms per point against the port's "
            + $"{portPerPoint:0.00000} ms, a ratio of {literalPerPoint / portPerPoint:0}x, which is "
            + "under 1000x and means the port has itself grown super-linear in the point count");
    }

    // ---- Helpers ----

    // A literal transcription of _is_outlier_iqr (lines 226 to 236), quartiles re-derived for every
    // point on both axes. It exists only so the corrected shape can be proved identical to it and
    // measured against it, and nothing in src/ looks like this.
    private static bool IsOutlierIqrLiteral(double value, IReadOnlyList<double> values)
    {
        var s = values.Order().ToList();
        var n = s.Count;
        var q1 = Statistics.Median(s.GetRange(0, n / 2))!.Value;
        var q3 = Statistics.Median(s.GetRange((n + 1) / 2, n - ((n + 1) / 2)))!.Value;
        var iqr = q3 - q1;
        return value < q1 - 1.5 * iqr || value > q3 + 1.5 * iqr;
    }

    // One filter group's HFR values in the file's own row order, which is the order the box plot's
    // grouping hands the helper. The filter name comes from the oracle, so none is typed here.
    private static IReadOnlyList<double> HfrByFilter(string filterName)
    {
        var values = new List<double>();
        foreach (var row in AnalysisOracle.Rows.EnumerateArray())
        {
            if (row.GetProperty("filter").GetString() == filterName
                && AnalysisOracle.Number(row.GetProperty("median_hfr")) is { } value)
            {
                values.Add(value);
            }
        }

        return values;
    }
}
