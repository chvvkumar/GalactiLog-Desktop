using System.Globalization;
using System.Text.Json;
using GalactiLog.Core.Metrics;
using Xunit;
using Xunit.Abstractions;

namespace GalactiLog.Core.Tests.Metrics;

/// <summary>
/// The correlation half of the Analysis statistics core: <c>PearsonR</c>, <c>PearsonQuality</c>,
/// <c>SpearmanRho</c>, <c>Trend</c>, <c>MatrixPearson</c> and <c>Downsample</c>. The distribution
/// half is <c>AnalysisDistributionTests</c>.
/// </summary>
/// <remarks>
/// Ruling A4, exact parity with no tolerance: every oracle comparison below is
/// <c>Assert.Equal(expected, actual)</c> on <c>double</c> and never the precision overload. A
/// tolerance is a choice not to see a class of defect.
/// <para>
/// Every real-data figure comes from <c>realdata/oracle.json</c>, produced by running the real
/// functions out of <c>analysis.py</c>, and every real-data value from <c>realdata/inputs.json</c>,
/// both read from the repository through <see cref="AnalysisOracle"/>. Every group label in the
/// oracle is an ordinal, so no rig, target or filter name enters this file.
/// </para>
/// </remarks>
public class AnalysisCorrelationTests(ITestOutputHelper output)
{
    // The two correlation scenarios this half reads, both in the port's own row order. The oracle's
    // order_agreement object reports that every _port_order pair agrees on every figure, so either
    // member serves; the port-order member is read because inputs.json publishes its rows in that
    // order and a case that reorders them would be asserting its own arithmetic.
    private const string HumidityHfr = "correlation_humidity_hfr_frame_port_order";
    private const string Phd2RmsHfr = "correlation_phd2_rms_total_hfr_frame_port_order";
    private const string Matrix = "matrix_10x10_port_order";

    // ---- The oracle itself ----

    [Fact]
    public void Oracle_WasProducedByAnInterpreterWhoseBuiltinSumIsCompensated()
    {
        // CPython gave the builtin sum() its Neumaier compensation in 3.12, and every figure in
        // the oracle depends on it, so the interpreter is checked rather than assumed
        // (task2.md section 8 item 3).
        var version = AnalysisOracle.PythonVersion;
        output.WriteLine($"oracle.json python_version: {version}");

        var parts = version.Split('.');
        Assert.Equal(3, int.Parse(parts[0], CultureInfo.InvariantCulture));
        Assert.True(
            int.Parse(parts[1], CultureInfo.InvariantCulture) >= 12,
            $"The oracle needs CPython 3.12 or later for a compensated builtin sum(); it reads {version}.");
    }

    // ---- 7.1 Every figure against the oracle, exact ----

    [Fact]
    public void Trend_OnTheUsersOwnHumidityAndHfr_MatchesTheOracleExactly()
    {
        var (xs, ys) = AnalysisOracle.Pairs("humidity", "hfr");
        var scenario = AnalysisOracle.Scenario(HumidityHfr);

        Assert.Equal(scenario.GetProperty("total_count").GetInt32(), xs.Count);

        var trend = Assert.IsType<TrendLine>(Analysis.Trend(xs, ys));
        AssertTrendMatches(scenario.GetProperty("trend"), trend);
    }

    [Fact]
    public void Trend_OnThePhd2NightStepFunction_MatchesTheOracleExactly()
    {
        // 7.12a row 3, the shape the user's own library actually has: the PHD2 night figure is a
        // step function, so 416 frames carry only 5 distinct X positions. The trend still answers a
        // line and a full band of ConfidenceBandMaxPoints points across the X range.
        var (xs, ys) = AnalysisOracle.Pairs("phd2_rms_total", "hfr");
        var scenario = AnalysisOracle.Scenario(Phd2RmsHfr);

        Assert.Equal(scenario.GetProperty("total_count").GetInt32(), xs.Count);
        Assert.Equal(416, xs.Count);
        Assert.Equal(5, xs.Distinct().Count());

        var trend = Assert.IsType<TrendLine>(Analysis.Trend(xs, ys));
        AssertTrendMatches(scenario.GetProperty("trend"), trend);

        Assert.Equal(Math.Min(Analysis.ConfidenceBandMaxPoints, xs.Count), trend.ConfidenceUpper.Count);
        Assert.Equal(50, trend.ConfidenceUpper.Count);
    }

    [Theory]
    [InlineData(HumidityHfr, "humidity", "hfr")]
    [InlineData(Phd2RmsHfr, "phd2_rms_total", "hfr")]
    public void PearsonR_AndSpearmanRho_Unrounded_MatchTheOracleExactly(
        string scenarioName, string xKey, string yKey)
    {
        var (xs, ys) = AnalysisOracle.Pairs(xKey, yKey);
        var scenario = AnalysisOracle.Scenario(scenarioName);

        Assert.Equal(scenario.GetProperty("pearson_r_full").GetDouble(), Analysis.PearsonR(xs, ys));
        Assert.Equal(scenario.GetProperty("spearman_rho_full").GetDouble(), Analysis.SpearmanRho(xs, ys));
    }

    [Fact]
    public void MatrixPearson_OverTheHundredCellsOfTheUsersOwnLibrary_MatchesTheOracleExactly()
    {
        // Both published figures: r_full, the unrounded value, and pearson_r, the rounded cell,
        // each exact. r_via_pearson_r_helper is deliberately NOT read here: it is the other
        // function, which answers 0.0 where the cell answers no value.
        var cells = AnalysisOracle.Scenario(Matrix).GetProperty("cells");
        var mismatches = new List<string>();
        var nullCells = 0;

        foreach (var cell in cells.EnumerateArray())
        {
            var xKey = cell.GetProperty("x_metric").GetString()!;
            var yKey = cell.GetProperty("y_metric").GetString()!;
            var (xs, ys) = AnalysisOracle.Pairs(xKey, yKey);

            var expectedFull = AnalysisOracle.Number(cell.GetProperty("r_full"));
            var expectedCell = AnalysisOracle.Number(cell.GetProperty("pearson_r"));
            var actual = Analysis.MatrixPearson(xs, ys);

            if (expectedFull is null)
            {
                nullCells++;
            }

            if (xs.Count != cell.GetProperty("n_points").GetInt32())
            {
                mismatches.Add($"{xKey}/{yKey}: paired {xs.Count}, oracle n_points {cell.GetProperty("n_points").GetInt32()}");
                continue;
            }

            if (actual != expectedFull)
            {
                mismatches.Add($"{xKey}/{yKey}: r_full {Show(actual)} against the oracle's {Show(expectedFull)}");
                continue;
            }

            var rounded = actual is { } value ? PythonNumerics.RoundLikePython(value, 4) : (double?)null;
            if (rounded != expectedCell)
            {
                mismatches.Add($"{xKey}/{yKey}: cell {Show(rounded)} against the oracle's {Show(expectedCell)}");
            }
        }

        Assert.Equal(100, cells.GetArrayLength());
        Assert.Equal(10, nullCells);
        Assert.Empty(mismatches);
    }

    [Fact]
    public void PearsonR_OnAPerfectlyLinearPair_IsExactlyOne()
    {
        // Hand computed, not from the oracle: y = 2x + 1 correlates perfectly, so r is 1.
        double[] xs = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        var ys = xs.Select(x => 2 * x + 1).ToArray();

        Assert.Equal(1.0, Analysis.PearsonR(xs, ys));
        Assert.Equal(1.0, Analysis.MatrixPearson(xs, ys));
        Assert.Equal(CorrelationQuality.Ok, Analysis.PearsonQuality(xs, ys));

        // And a perfectly inverse pair answers exactly -1.
        var inverse = xs.Select(x => 100 - 3 * x).ToArray();
        Assert.Equal(-1.0, Analysis.PearsonR(xs, inverse));
        Assert.Equal(-1.0, Analysis.MatrixPearson(xs, inverse));

        // SpearmanRho over a strictly increasing pair is its Pearson over the ranks 1..n, which is
        // 1 exactly, and the ranks are halves only where values tie.
        Assert.Equal(1.0, Analysis.SpearmanRho(xs, ys));
    }

    [Fact]
    public void PearsonR_AndMatrixPearson_AnswerDifferentDoublesOnOneAndTheSamePair()
    {
        // The two entry points are two different source functions and are not interchangeable
        // (ruling A19). _pearson_r divides by sqrt(Sxx) * sqrt(Syy) and _pg_corr by
        // sqrt(Sxx * Syy), which are different doubles; each figure is asserted against its own
        // published field, so a port that reused one for the other goes red here.
        var (xs, ys) = AnalysisOracle.Pairs("humidity", "hfr");

        var helper = Analysis.PearsonR(xs, ys);
        var cell = Analysis.MatrixPearson(xs, ys);
        output.WriteLine($"_pearson_r {helper:R}, _pg_corr {cell:R}");

        Assert.Equal(
            AnalysisOracle.Scenario(HumidityHfr).GetProperty("pearson_r_full").GetDouble(), helper);
        Assert.Equal(
            AnalysisOracle.Number(FirstCell().GetProperty("r_full")), cell);
        Assert.NotEqual(helper, cell);
    }

    // ---- 7.2 The caller's order is part of the answer ----

    [Fact]
    public void TheCallersOrder_IsTheAnswer_AndNothingInThisHalfSortsItsInput()
    {
        // _pearson_r and _compute_trend sum xs and ys as the caller hands them (lines 243 to 247
        // and 284 to 313), where _compute_summary_stats sums its SORTED copy. An implementer who
        // sorts here for tidiness does not move a last decimal: it computes a different statistic,
        // because sorting the two axes independently destroys the pairing.
        var (xs, ys) = AnalysisOracle.Pairs("humidity", "hfr");
        var scenario = AnalysisOracle.Scenario(HumidityHfr);

        var trend = Assert.IsType<TrendLine>(Analysis.Trend(xs, ys));
        Assert.Equal(scenario.GetProperty("trend").GetProperty("slope").GetDouble(), trend.Slope);

        var sortedXs = xs.Order().ToArray();
        var sortedYs = ys.Order().ToArray();
        var sortedTrend = Assert.IsType<TrendLine>(Analysis.Trend(sortedXs, sortedYs));
        output.WriteLine(
            $"caller order slope {trend.Slope:R}, each axis sorted {sortedTrend.Slope:R}");
        output.WriteLine(
            $"caller order r {Analysis.PearsonR(xs, ys):R}, each axis sorted {Analysis.PearsonR(sortedXs, sortedYs):R}");

        Assert.NotEqual(trend.Slope, sortedTrend.Slope);
        Assert.NotEqual(Analysis.PearsonR(xs, ys), Analysis.PearsonR(sortedXs, sortedYs));

        // A JOINT permutation keeps the pairing and is mathematically the same statistic. The
        // oracle's own order_agreement object reports that the export order and the port order
        // agree on every figure of this scenario, and the reversal below agrees too: the
        // compensated accumulation is robust enough that no permutation of this library's own
        // values moves a figure. The rule is pinned by construction, not by this library's luck.
        var reversedXs = xs.Reverse().ToArray();
        var reversedYs = ys.Reverse().ToArray();
        Assert.Equal(trend.Slope, Assert.IsType<TrendLine>(Analysis.Trend(reversedXs, reversedYs)).Slope);
        Assert.Equal(Analysis.PearsonR(xs, ys), Analysis.PearsonR(reversedXs, reversedYs));
    }

    // ---- 7.3 The accumulation is compensated, not a plain left-to-right sum ----

    [Fact]
    public void TheAccumulation_IsCompensated_AndNotEnumerableSum()
    {
        // Enumerable.Sum is a plain left-to-right loop. Measured over the user's own library, it
        // moves 86 of the matrix's 90 non-null cells; one of them is asserted here both ways so
        // the rule is proven by a figure rather than by a comment.
        var (xs, ys) = AnalysisOracle.Pairs("humidity", "hfr");
        var expected = AnalysisOracle.Number(FirstCell().GetProperty("r_full"))!.Value;

        var plain = PlainSumCorrelation(xs, ys);
        output.WriteLine($"compensated {Analysis.MatrixPearson(xs, ys):R}, plain left to right {plain:R}");
        output.WriteLine($"oracle r_full {expected:R}");

        Assert.Equal(expected, Analysis.MatrixPearson(xs, ys));
        Assert.NotEqual(expected, plain);

        // The eight SNR values of PythonNumerics.ExactSum's own remark, summed both ways, so the
        // two accumulation rules are visible side by side in the output.
        double[] eight = [324.52, 324.61, 324.48, 324.71, 324.59, 324.63, 324.57, 324.58];
        output.WriteLine(
            $"eight values: compensated {PythonNumerics.CompensatedSum(eight):R}, Enumerable.Sum {eight.Sum():R}");
    }

    // ---- 7.8 Degenerate inputs answer, never throw ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DegenerateCounts_Answer_NeverThrow(int count)
    {
        var xs = Enumerable.Range(0, count).Select(i => (double)i).ToArray();
        var ys = Enumerable.Range(0, count).Select(i => i * 2.0).ToArray();

        // Below 3 points every correlation figure is exactly 0.0 and there is no trend line at all
        // (lines 241, 255 and 280). At 3 the pair is perfectly linear, and the figure is a real
        // quotient rather than the 0.0 the guards hand back: 0.9999999999999998 and not 1.0,
        // because the two roots multiply to just over the numerator. It is asserted as a real
        // answer rather than as a literal, since the literal would be asserting the arithmetic
        // against itself.
        Assert.Equal(count < 3 ? 0.0 : 0.9999999999999998, Analysis.PearsonR(xs, ys));
        Assert.Equal(count < 3 ? 0.0 : 0.9999999999999998, Analysis.SpearmanRho(xs, ys));
        Assert.Equal(
            count < 3 ? CorrelationQuality.TooFewPoints : CorrelationQuality.Ok,
            Analysis.PearsonQuality(xs, ys));

        if (count < 3)
        {
            Assert.Null(Analysis.Trend(xs, ys));
        }
        else
        {
            Assert.NotNull(Analysis.Trend(xs, ys));
        }

        // Below the ten point minimum the matrix cell shows nothing.
        Assert.Null(Analysis.MatrixPearson(xs, ys));

        // Downsample hands back the input instance itself while it fits the cap.
        Assert.Same(xs, Analysis.Downsample(xs, Analysis.CorrelationPointCap));
    }

    [Fact]
    public void AnAxisThatNeverVaries_AnswersZeroAndNoTrend_NeverNaN()
    {
        var constant = Enumerable.Repeat(7.5, 12).ToArray();
        var varying = Enumerable.Range(0, 12).Select(i => i + 0.5).ToArray();

        Assert.Equal(0.0, Analysis.PearsonR(constant, varying));
        Assert.Equal(0.0, Analysis.PearsonR(varying, constant));
        Assert.Equal(0.0, Analysis.SpearmanRho(constant, varying));

        // A constant X gives a least-squares denominator of zero, which is line 290's null.
        Assert.Null(Analysis.Trend(constant, varying));

        // A constant Y still gives a line: a flat one, with r squared zero.
        var flat = Assert.IsType<TrendLine>(Analysis.Trend(varying, constant));
        Assert.Equal(0.0, flat.Slope);
        Assert.Equal(0.0, flat.RSquared);
        Assert.All(flat.ConfidenceUpper, point => Assert.True(double.IsFinite(point.Y)));
    }

    [Fact]
    public void MismatchedAxisLengths_AreRefused_RatherThanTruncatedTheWayPythonsZipWould()
    {
        double[] xs = [1, 2, 3, 4];
        double[] ys = [1, 2, 3];

        Assert.Throws<ArgumentException>(() => Analysis.PearsonR(xs, ys));
        Assert.Throws<ArgumentException>(() => Analysis.PearsonQuality(xs, ys));
        Assert.Throws<ArgumentException>(() => Analysis.SpearmanRho(xs, ys));
        Assert.Throws<ArgumentException>(() => Analysis.Trend(xs, ys));
        Assert.Throws<ArgumentException>(() => Analysis.MatrixPearson(xs, ys));
    }

    // ---- 7.9 The cap boundary at 5,000 and 5,001 ----

    [Theory]
    [InlineData(4999)]
    [InlineData(5000)]
    public void Downsample_AtOrBelowTheCap_ReturnsTheSameInstance(int count)
    {
        var points = Enumerable.Range(0, count).ToArray();

        Assert.Same(points, Analysis.Downsample(points, Analysis.CorrelationPointCap));
    }

    [Fact]
    public void Downsample_OnePointAboveTheCap_TakesExactlyTheCapAtAnEvenStride()
    {
        var points = Enumerable.Range(0, 5001).ToArray();

        var sampled = Analysis.Downsample(points, Analysis.CorrelationPointCap);

        Assert.NotSame(points, sampled);
        Assert.Equal(Analysis.CorrelationPointCap, sampled.Count);

        // The whole index vector, computed the way lines 466 and 467 compute it, so an accumulated
        // step that drifts and misses the tail goes red rather than an off-by-one at the ends only.
        var step = 5001d / Analysis.CorrelationPointCap;
        var expected = Enumerable.Range(0, Analysis.CorrelationPointCap).Select(i => points[(int)(i * step)]);
        Assert.Equal(expected, sampled);

        // The first point is index 0 and the last is index 4999, not 5000: the stride is
        // 5001 / 5000, so the highest index it reaches is int(4999 * 1.0002) = 4999 and the
        // original tail point is not among the returned ones. That is line 467's own behaviour and
        // it is asserted rather than corrected.
        Assert.Equal(0, sampled[0]);
        Assert.Equal(4999, sampled[^1]);
        Assert.DoesNotContain(5000, sampled);
    }

    [Fact]
    public void Downsample_WithANonPositiveCap_IsRefused()
    {
        var points = Enumerable.Range(0, 10).ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => Analysis.Downsample(points, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Analysis.Downsample(points, -1));
    }

    // ---- 7.10 MatrixPearson gates, by rule and not by literal ----

    [Fact]
    public void MatrixPearson_AtTheTenPointMinimum_FollowsTheOracle()
    {
        // matrix_below_minimum_points: nine paired points answer no value and ten answer 1.0.
        var scenario = AnalysisOracle.Scenario("matrix_below_minimum_points");

        var nine = Enumerable.Range(0, 9).Select(i => (double)i).ToArray();
        var ten = Enumerable.Range(0, 10).Select(i => (double)i).ToArray();

        Assert.Null(AnalysisOracle.Number(scenario.GetProperty("n_9")));
        Assert.Null(Analysis.MatrixPearson(nine, nine.Select(x => x * 2).ToArray()));

        Assert.Equal(
            AnalysisOracle.Number(scenario.GetProperty("n_10")),
            Analysis.MatrixPearson(ten, ten.Select(x => x * 2).ToArray()));
        Assert.Equal(10, Analysis.MatrixMinimumPoints);
    }

    public static TheoryData<double, bool> ConstantColumnLiterals()
    {
        var data = new TheoryData<double, bool>();

        // The six the oracle publishes, each with a pg_corr of null.
        foreach (var cell in AnalysisOracle.Scenario("matrix_constant_column").GetProperty("cells").EnumerateArray())
        {
            data.Add(cell.GetProperty("constant").GetDouble(), true);
        }

        // The four further literals the seam reviewer measured. The withdrawn nx <= 0 residue gate
        // answers null for these and a spurious near-zero for most of the six above, so a theory
        // that named only one side would have passed or failed on that choice.
        foreach (var literal in new[] { 0.1, 1.0, 20.5, 1024.0 })
        {
            data.Add(literal, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ConstantColumnLiterals))]
    public void MatrixPearson_OnAColumnThatNeverVaries_ShowsNoValue(double constant, bool fromTheOracle)
    {
        // 40 points, the y column 1.0 stepping by 0.01, which is the scenario's own construction.
        var constantColumn = Enumerable.Repeat(constant, 40).ToArray();
        var varying = Enumerable.Range(0, 40).Select(i => 1.0 + 0.01 * i).ToArray();

        Assert.Null(Analysis.MatrixPearson(constantColumn, varying));
        Assert.Null(Analysis.MatrixPearson(varying, constantColumn));

        if (fromTheOracle)
        {
            var cell = AnalysisOracle.Scenario("matrix_constant_column").GetProperty("cells")
                .EnumerateArray()
                .Single(c => c.GetProperty("constant").GetDouble() == constant);

            Assert.Null(AnalysisOracle.Number(cell.GetProperty("pg_corr")));
            Assert.Null(AnalysisOracle.Number(cell.GetProperty("pg_corr_axis_swapped")));

            // The gate is min equals max on the observed values, not a zero test on an
            // accumulation: the oracle's own two Sxx fields are why. The mean centred form recovers
            // exactly 0.0 for every literal while the computational form does not.
            Assert.Equal(0.0, cell.GetProperty("sxx_mean_centred").GetDouble());
            output.WriteLine(
                $"constant {constant:R}: Sxx centred {cell.GetProperty("sxx_mean_centred").GetDouble():R}, "
                + $"computational {cell.GetProperty("sxx_computational_form").GetDouble():R}");

            // _pearson_r on the same pair answers 0.0, which is the divergence the second entry
            // point exists for: a coloured square reading 0.00 where the user must see no data.
            Assert.Equal(0.0, cell.GetProperty("pearson_r_helper").GetDouble());
            Assert.Equal(0.0, Analysis.PearsonR(constantColumn, varying));
        }
    }

    [Fact]
    public void MatrixPearson_OnASpreadWhoseCentredSquaresUnderflow_ShowsNoValue_NeverAnInfinity()
    {
        // The reference's own last gate, oracle.py:600, which the min equals max test cannot stand
        // in for. Twelve values around 1e-170 spaced by one part in 1e6 are twelve DISTINCT
        // doubles, so that gate passes, and every centred square underflows, so the sum of them is
        // exactly 0.0. _pg_corr answers None here; without the gate the port divides by zero and
        // hands an infinity to a coloured matrix square, which is the one shape case 7.8's rule
        // that the degenerate answers are total does not otherwise cover.
        var underflowing = Enumerable.Range(0, 12).Select(i => 1e-170 * (1.0 + i / 1e6)).ToArray();
        var varying = Enumerable.Range(0, 12).Select(i => 1.0 + 0.01 * i).ToArray();

        Assert.Equal(12, underflowing.Distinct().Count());
        Assert.NotEqual(underflowing.Min(), underflowing.Max());

        var centred = PythonNumerics.CompensatedSum(
            underflowing.Select(x => (x - PythonNumerics.CompensatedSum(underflowing) / 12)
                * (x - PythonNumerics.CompensatedSum(underflowing) / 12)));
        output.WriteLine($"twelve values from {underflowing[0]:R} to {underflowing[^1]:R}, Sxx {centred:R}");
        Assert.Equal(0.0, centred);

        Assert.Null(Analysis.MatrixPearson(underflowing, varying));
        Assert.Null(Analysis.MatrixPearson(varying, underflowing));
    }

    [Fact]
    public void MatrixPearson_OnAConstantColumnBelowTheMinimum_ShowsNoValueForBothReasonsAtOnce()
    {
        var constantColumn = Enumerable.Repeat(12.7, 9).ToArray();
        var varying = Enumerable.Range(0, 9).Select(i => 1.0 + 0.01 * i).ToArray();

        Assert.Null(Analysis.MatrixPearson(constantColumn, varying));
    }

    // ---- 7.11a PearsonQuality separates the three zeroes ----

    [Fact]
    public void PearsonQuality_SeparatesTheThreeReasonsForAZero()
    {
        double[] twoXs = [1, 2];
        double[] twoYs = [3, 4];
        Assert.Equal(CorrelationQuality.TooFewPoints, Analysis.PearsonQuality(twoXs, twoYs));
        Assert.Equal(0.0, Analysis.PearsonR(twoXs, twoYs));

        var constant = Enumerable.Repeat(4.25, 10).ToArray();
        var varying = Enumerable.Range(0, 10).Select(i => i * 1.5).ToArray();

        Assert.Equal(CorrelationQuality.ConstantX, Analysis.PearsonQuality(constant, varying));
        Assert.Equal(0.0, Analysis.PearsonR(constant, varying));

        Assert.Equal(CorrelationQuality.ConstantY, Analysis.PearsonQuality(varying, constant));
        Assert.Equal(0.0, Analysis.PearsonR(varying, constant));

        // TooFewPoints wins over both constant answers, which is the order the Python's own guards
        // evaluate in: line 241 before line 248.
        Assert.Equal(
            CorrelationQuality.TooFewPoints,
            Analysis.PearsonQuality([4.25, 4.25], [4.25, 4.25]));

        // Both axes constant answers ConstantX, the X reason first.
        Assert.Equal(
            CorrelationQuality.ConstantX,
            Analysis.PearsonQuality(constant, Enumerable.Repeat(9.0, 10).ToArray()));
    }

    [Fact]
    public void PearsonQuality_OnAGenuinelyUncorrelatedPair_IsOk()
    {
        // A real pair whose r IS 0.0 is still Ok, which is the whole point of the separation: the
        // page must not read "not enough data" over a real answer of no relationship. This
        // permutation of 1..8 against 1..8 correlates at exactly zero, so the figure is
        // indistinguishable from the three guarded zeroes and the quality is not.
        double[] xs = [1, 2, 3, 4, 5, 6, 7, 8];
        double[] ys = [1, 4, 6, 7, 8, 5, 3, 2];

        var r = Analysis.PearsonR(xs, ys);
        output.WriteLine($"uncorrelated pair r {r:R}, rounded {PythonNumerics.RoundLikePython(r, 4):R}");

        Assert.Equal(0.0, r);
        Assert.Equal(CorrelationQuality.Ok, Analysis.PearsonQuality(xs, ys));
    }

    [Fact]
    public void PearsonQuality_OnTheUsersOwnSensorTemperature_IsOk_AlthoughItTakesOnlyFiveValues()
    {
        // realdata-prep-report.md D6's near miss: 416 frames carry only 5 distinct sensor set
        // points, which reads like a constant column and is not one.
        var (xs, ys) = AnalysisOracle.Pairs("sensor_temp", "hfr");

        Assert.Equal(416, xs.Count);
        Assert.Equal(5, xs.Distinct().Count());
        Assert.Equal(CorrelationQuality.Ok, Analysis.PearsonQuality(xs, ys));
        Assert.NotEqual(0.0, Analysis.PearsonR(xs, ys));
    }

    // ---- 7.12a The shapes the user's own library actually has ----

    [Fact]
    public void AMetricWithNoValuesAtAll_AnswersEmptily_AndNothingThrows()
    {
        // sky_quality, 0 of 416 rows. The empty answers have to be total rather than approximate,
        // because this is the common path on a real library and not an edge.
        var values = AnalysisOracle.Values("sky_quality");
        Assert.Empty(values);

        var (xs, ys) = AnalysisOracle.Pairs("sky_quality", "hfr");
        Assert.Empty(xs);
        Assert.Empty(ys);

        Assert.Null(Analysis.Trend(xs, ys));
        Assert.Equal(0.0, Analysis.PearsonR(xs, ys));
        Assert.Equal(0.0, Analysis.SpearmanRho(xs, ys));
        Assert.Equal(CorrelationQuality.TooFewPoints, Analysis.PearsonQuality(xs, ys));
        Assert.Null(Analysis.MatrixPearson(xs, ys));

        // And the matrix's own cells for that row read no value over zero points.
        foreach (var cell in AnalysisOracle.Scenario(Matrix).GetProperty("cells").EnumerateArray())
        {
            if (cell.GetProperty("x_metric").GetString() == "sky_quality")
            {
                Assert.Equal(0, cell.GetProperty("n_points").GetInt32());
                Assert.Null(AnalysisOracle.Number(cell.GetProperty("pearson_r")));
            }
        }
    }

    // ---- helpers ----

    private static JsonElement FirstCell()
        => AnalysisOracle.Scenario(Matrix).GetProperty("cells")
            .EnumerateArray()
            .Single(cell => cell.GetProperty("x_metric").GetString() == "humidity"
                && cell.GetProperty("y_metric").GetString() == "hfr");

    private void AssertTrendMatches(JsonElement expected, TrendLine actual)
    {
        Assert.Equal(expected.GetProperty("slope").GetDouble(), actual.Slope);
        Assert.Equal(expected.GetProperty("intercept").GetDouble(), actual.Intercept);
        Assert.Equal(expected.GetProperty("r_squared").GetDouble(), actual.RSquared);
        Assert.Equal(expected.GetProperty("pearson_r").GetDouble(), actual.PearsonR);
        Assert.Equal(expected.GetProperty("spearman_rho").GetDouble(), actual.SpearmanRho);

        AssertBandMatches(expected.GetProperty("confidence_upper"), actual.ConfidenceUpper, "upper");
        AssertBandMatches(expected.GetProperty("confidence_lower"), actual.ConfidenceLower, "lower");
    }

    private void AssertBandMatches(JsonElement expected, IReadOnlyList<BandPoint> actual, string which)
    {
        Assert.Equal(expected.GetArrayLength(), actual.Count);

        var i = 0;
        foreach (var point in expected.EnumerateArray())
        {
            var x = point.GetProperty("x").GetDouble();
            var y = point.GetProperty("y").GetDouble();

            if (x != actual[i].X || y != actual[i].Y)
            {
                output.WriteLine(
                    $"{which} band point {i}: expected ({x:R}, {y:R}), got ({actual[i].X:R}, {actual[i].Y:R})");
            }

            Assert.Equal(x, actual[i].X);
            Assert.Equal(y, actual[i].Y);
            i++;
        }
    }

    // _pg_corr's arithmetic with a plain left-to-right accumulation in place of the compensated
    // one, which is what Enumerable.Sum does. Present so the accumulation rule is proven by a
    // figure; never a second implementation of the member under test.
    private static double PlainSumCorrelation(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        var n = xs.Count;
        var mx = xs.Sum() / n;
        var my = ys.Sum() / n;
        var sxy = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum();
        var sxx = xs.Select(x => (x - mx) * (x - mx)).Sum();
        var syy = ys.Select(y => (y - my) * (y - my)).Sum();

        return sxy / Math.Sqrt(sxx * syy);
    }

    private static string Show(double? value)
        => value is { } number ? number.ToString("R", CultureInfo.InvariantCulture) : "no value";
}
