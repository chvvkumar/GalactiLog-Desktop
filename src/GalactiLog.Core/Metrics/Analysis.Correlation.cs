using static GalactiLog.Core.Metrics.PythonNumerics;

namespace GalactiLog.Core.Metrics;

// The correlation half of the Analysis statistics core: Pearson, Spearman, the least-squares
// trend with its confidence band, the scatter point cap and the matrix cell (task2.md sections
// 5.5 to 5.7, 5.11 and 5.13). The distribution half is Analysis.Distribution.cs. Both are one
// partial class so the two halves could be written at once; the records they answer with are in
// Analysis.cs. Port of backend/app/api/analysis.py at 591234b, and a line number in a summary
// below with no other file named is that file.
//
// Nothing here sorts its input before summing, deliberately. Floating point addition is not
// associative, so the order in which the values arrive is part of the answer, and these helpers
// sum in the caller's order because their Python counterparts do (task2.md section 4). The
// distribution half's Summary sorts first, because ITS counterpart does.
public static partial class Analysis
{
    /// <summary>Most scatter points the Correlation tab returns. Port of
    /// <c>_CORRELATION_POINT_CAP</c>, line 47: the trend and the summary stats are computed over
    /// the full point set and only the returned points are capped.</summary>
    public const int CorrelationPointCap = 5000;

    /// <summary>Fewest paired values a matrix cell needs before it shows a figure (line 810). The
    /// gate lives in <see cref="MatrixPearson"/>, not in the query, so no caller can forget
    /// it.</summary>
    public const int MatrixMinimumPoints = 10;

    /// <summary>The short moving-average window, in POINTS and not in days (line 735).</summary>
    public const int MovingAverageShortWindow = 7;

    /// <summary>The long moving-average window, in POINTS and not in days (line 736).</summary>
    public const int MovingAverageLongWindow = 30;

    /// <summary>Most points a confidence band carries (line 309).</summary>
    public const int ConfidenceBandMaxPoints = 50;

    // The spread below which _pearson_r calls an axis constant (line 248). On the ROOTS, not on
    // the sums under them, which is what the Python tests.
    private const double ConstantAxisSpread = 1e-12;

    /// <summary>Pearson's r over the pair. Port of <c>_pearson_r</c>, lines 239 to 250. Not
    /// rounded: <see cref="Trend"/> rounds it to 4 (line 326).</summary>
    /// <remarks>
    /// Answers exactly <c>0.0</c> for three different reasons, which is the Python's own behaviour
    /// and is kept: fewer than 3 points (line 241), a constant X and a constant Y (line 248). A
    /// genuinely uncorrelated pair answers <c>0.0</c> too, so nothing downstream can tell the four
    /// apart from the figure alone. <see cref="PearsonQuality"/> publishes the reason beside it
    /// (defect D6); this member's answer is unchanged by that.
    /// <para>
    /// The values are summed in the CALLER'S order, as lines 243 to 247 sum them, and that order is
    /// part of the answer: nothing here may sort for tidiness.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="xs"/> and <paramref name="ys"/> are
    /// different lengths. Python's <c>zip</c> would truncate silently and hide the caller's
    /// bug.</exception>
    public static double PearsonR(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
        => PearsonCore(xs, ys).Value;

    /// <summary>Why <see cref="PearsonR"/> answered exactly <c>0.0</c>, when it did. Has no web
    /// counterpart: it is the port's answer to defect D6, and spec 12.14's states table needs to
    /// tell "no relationship" from "not enough data" and from "this column never varies".</summary>
    /// <remarks><see cref="CorrelationQuality.TooFewPoints"/> wins over the two constant answers
    /// and <see cref="CorrelationQuality.ConstantX"/> over
    /// <see cref="CorrelationQuality.ConstantY"/>, which is the order the Python's own guards
    /// evaluate in. <see cref="CorrelationQuality.Ok"/> accompanies nothing but a real
    /// quotient.</remarks>
    public static CorrelationQuality PearsonQuality(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
        => PearsonCore(xs, ys).Quality;

    /// <summary>Spearman's rank correlation over the pair, which is <see cref="PearsonR"/> over the
    /// two rank vectors. Port of <c>_spearman_rho</c>, lines 253 to 274. <c>0.0</c> below 3 points
    /// (line 255). Not rounded: <see cref="Trend"/> rounds it to 4 (line 327).</summary>
    /// <exception cref="ArgumentException"><paramref name="xs"/> and <paramref name="ys"/> are
    /// different lengths.</exception>
    public static double SpearmanRho(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        RequireSameLength(xs, ys);

        return xs.Count < 3 ? 0.0 : PearsonR(Rank(xs), Rank(ys));
    }

    /// <summary>The least-squares line, its correlation figures and its confidence band. Port of
    /// <c>_compute_trend</c>, lines 277 to 330.</summary>
    /// <remarks>
    /// Null means the tab draws no line at all and says so itself: below 3 points (line 280), or a
    /// least-squares denominator below <c>1e-12</c> in absolute value (line 290), which is what an
    /// X that never varies produces.
    /// <para>
    /// The band's <c>t</c> is <c>1.96</c> above 30 points and <c>2.0</c> at or below (line 305). It
    /// is NOT a t distribution quantile, so the band is narrower than a true 95 percent interval at
    /// small n. Spec 12.14 says the port reproduces that exactly and why; it is not a defect to
    /// correct here.
    /// </para>
    /// <para>
    /// Every sum is taken in the CALLER'S order (lines 284 to 313), which is part of the answer.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="xs"/> and <paramref name="ys"/> are
    /// different lengths.</exception>
    public static TrendLine? Trend(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        RequireSameLength(xs, ys);

        var n = xs.Count;
        if (n < 3)
        {
            return null;
        }

        var sumX = CompensatedSum(xs);
        var sumY = CompensatedSum(ys);
        var sumXy = CompensatedSum(xs.Zip(ys, (x, y) => x * y));
        var sumX2 = CompensatedSum(xs.Select(x => x * x));

        var denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) < 1e-12)
        {
            return null;
        }

        var slope = (n * sumXy - sumX * sumY) / denom;
        var intercept = (sumY - slope * sumX) / n;

        var meanY = sumY / n;
        var ssTot = CompensatedSum(ys.Select(y => (y - meanY) * (y - meanY)));
        var ssRes = CompensatedSum(xs.Zip(ys, (x, y) =>
        {
            var residual = y - (slope * x + intercept);
            return residual * residual;
        }));
        var rSquared = ssTot > 0 ? 1 - ssRes / ssTot : 0.0;

        var meanX = sumX / n;
        var standardError = n > 2 && ssRes > 0 ? Math.Sqrt(ssRes / (n - 2)) : 0.0;
        var t = n > 30 ? 1.96 : 2.0;

        // sorted(xs)[0] and [-1], line 308. The sort is the Python's own and costs one copy on a
        // path that runs once per tab open.
        var sortedXs = new List<double>(xs);
        sortedXs.Sort();
        var xMin = sortedXs[0];
        var xMax = sortedXs[n - 1];

        var bandPoints = Math.Min(ConfidenceBandMaxPoints, n);
        var xStep = (xMax - xMin) / Math.Max(bandPoints - 1, 1);
        var sx2 = CompensatedSum(xs.Select(x => (x - meanX) * (x - meanX)));

        var upper = new List<BandPoint>(bandPoints);
        var lower = new List<BandPoint>(bandPoints);
        for (var i = 0; i < bandPoints; i++)
        {
            // Recomputed from i, never accumulated by repeated addition of the step, which would
            // drift and move the sixth decimal the band's x is rounded to.
            var bx = xMin + i * xStep;
            var yHat = slope * bx + intercept;

            // Line 317's conditional covers the WHOLE expression in Python, so the false branch is
            // 1 / n alone. 1.0 / n, because 1 / n with an int n is integer division here and would
            // silently give a band of zero width.
            var h = sx2 > 0 ? 1.0 / n + (bx - meanX) * (bx - meanX) / sx2 : 1.0 / n;
            var margin = t * standardError * Math.Sqrt(h);

            upper.Add(new BandPoint(RoundLikePython(bx, 6), RoundLikePython(yHat + margin, 6)));
            lower.Add(new BandPoint(RoundLikePython(bx, 6), RoundLikePython(yHat - margin, 6)));
        }

        // Lines 322 to 329. The two correlation figures run over the SAME full xs and ys the sums
        // used, not over the band's points.
        return new TrendLine(
            RoundLikePython(slope, 6),
            RoundLikePython(intercept, 6),
            RoundLikePython(rSquared, 4),
            RoundLikePython(PearsonR(xs, ys), 4),
            RoundLikePython(SpearmanRho(xs, ys), 4),
            upper,
            lower);
    }

    /// <summary>The correlation matrix cell's figure, or null when the cell shows none. Port of
    /// PostgreSQL's <c>corr(Y, X)</c> aggregate, which line 791 calls and which SQLite does not
    /// have, so the hundred cells are computed here.</summary>
    /// <remarks>
    /// Deliberately a SECOND correlation entry point beside <see cref="PearsonR"/>, and ruling A19
    /// is why: they are two different source functions with two different published contracts. This
    /// one answers NO VALUE where <see cref="PearsonR"/> answers <c>0.0</c>, which is what keeps
    /// <c>0.00</c> out of a coloured square on a column that never varies.
    /// <para>
    /// Null for four reasons: fewer than <see cref="MatrixMinimumPoints"/> paired values (line
    /// 810), a constant X, a constant Y, or a spread whose centred squares underflow to a zero sum,
    /// which is the reference's own last gate at <c>oracle.py:600</c>. The two constant gates are
    /// tested as min equals max on the observed values BEFORE any arithmetic runs, which is the
    /// whole point of them: a gate built
    /// on a cancellation residue answers differently depending on which literal the constant column
    /// holds, and the oracle's <c>matrix_constant_column</c> scenario measures exactly that.
    /// </para>
    /// <para>
    /// The reference is the oracle's <c>_pg_corr</c>, which is the mean centred sample Pearson and
    /// is what current PostgreSQL computes. The quotient is written <c>sxy / sqrt(sxx * syy)</c>,
    /// with ONE square root over the product, because that is the expression <c>_pg_corr</c>
    /// evaluates. <c>sqrt(sxx) * sqrt(syy)</c> is a different double: measured over the user's own
    /// 416 frames it moves 30 of the 90 non-null cells of scenario <c>matrix_10x10</c>, by at most
    /// 1.11e-16, which is invisible at the four decimals the cell prints and is an exact-parity
    /// failure against the published <c>r_full</c> that ruling A4 forbids.
    /// </para>
    /// <para>Not rounded: the caller rounds to 4 (line 811).</para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="xs"/> and <paramref name="ys"/> are
    /// different lengths.</exception>
    public static double? MatrixPearson(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        RequireSameLength(xs, ys);

        var n = xs.Count;
        if (n < MatrixMinimumPoints)
        {
            return null;
        }

        // Two more linear passes over lists this member is about to sum, and they are not folded
        // into the accumulation on purpose: the gate has to answer before any arithmetic runs.
        if (xs.Min() == xs.Max() || ys.Min() == ys.Max())
        {
            return null;
        }

        var mx = CompensatedSum(xs) / n;
        var my = CompensatedSum(ys) / n;
        var sxy = CompensatedSum(xs.Zip(ys, (x, y) => (x - mx) * (y - my)));
        var sxx = CompensatedSum(xs.Select(x => (x - mx) * (x - mx)));
        var syy = CompensatedSum(ys.Select(y => (y - my) * (y - my)));

        // The reference's own gate, oracle.py:600. The min equals max test above covers every input
        // this page can reach, and this covers the one it cannot: a spread whose CENTRED SQUARES
        // underflow. Twelve values around 1e-170 spaced by one part in 1e6 are twelve distinct
        // doubles, so min does not equal max, and every (x - mx) * (x - mx) underflows to zero, so
        // sxx is exactly 0.0. Without this line the quotient divides by zero and hands an infinity
        // to a coloured matrix square, where _pg_corr answers None.
        if (sxx == 0.0 || syy == 0.0)
        {
            return null;
        }

        return sxy / Math.Sqrt(sxx * syy);
    }

    /// <summary>At most <paramref name="cap"/> of <paramref name="points"/>, taken at an even
    /// stride. Port of lines 463 to 468.</summary>
    /// <remarks>Returns <paramref name="points"/> ITSELF, not a copy, when it already fits, which
    /// is what line 468 does. The boundary is strict: <paramref name="cap"/> points come back
    /// whole and one more is downsampled to exactly <paramref name="cap"/>.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cap"/> is zero or
    /// negative.</exception>
    public static IReadOnlyList<T> Downsample<T>(IReadOnlyList<T> points, int cap)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cap);

        if (points.Count <= cap)
        {
            return points;
        }

        // (int) truncates toward zero, which is Python's int() on a non-negative float, and
        // i * step is computed from i every time rather than accumulated.
        var step = (double)points.Count / cap;
        return [.. Enumerable.Range(0, cap).Select(i => points[(int)(i * step)])];
    }

    // The one body behind PearsonR and PearsonQuality: n, the two roots and the quotient are
    // computed once and both answers fall out of it, so the figure and its reason can never
    // disagree.
    private static (double Value, CorrelationQuality Quality) PearsonCore(
        IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        RequireSameLength(xs, ys);

        var n = xs.Count;
        if (n < 3)
        {
            return (0.0, CorrelationQuality.TooFewPoints);
        }

        var mx = CompensatedSum(xs) / n;
        var my = CompensatedSum(ys) / n;
        var num = CompensatedSum(xs.Zip(ys, (x, y) => (x - mx) * (y - my)));
        var dx = Math.Sqrt(CompensatedSum(xs.Select(x => (x - mx) * (x - mx))));
        var dy = Math.Sqrt(CompensatedSum(ys.Select(y => (y - my) * (y - my))));

        // Line 248 guards the ROOTS, not the sums under them, and answers the X reason first.
        return dx < ConstantAxisSpread
            ? (0.0, CorrelationQuality.ConstantX)
            : dy < ConstantAxisSpread
                ? (0.0, CorrelationQuality.ConstantY)
                : (num / (dx * dy), CorrelationQuality.Ok);
    }

    // The rank walk of lines 258 to 270, exactly: the indices ordered by value, then one average
    // rank per run of equal values. OrderBy is the shape sorted(range(n), key=lambda i: vals[i])
    // has and costs nothing here. Its stability is NOT what makes the answer right: the walk gives
    // the one average rank (i + j) / 2.0 + 1 to every member of a run, so the order of indices
    // within a tie run cannot move a rank. Re-derived over [3, 1, 3, 2, 3, 1], which ranks
    // [5.0, 1.5, 5.0, 3.0, 5.0, 1.5] under a stable tie order and under a deliberately unstable
    // one alike.
    private static double[] Rank(IReadOnlyList<double> values)
    {
        var n = values.Count;
        var indexed = Enumerable.Range(0, n).OrderBy(i => values[i]).ToArray();
        var ranks = new double[n];

        var i = 0;
        while (i < n)
        {
            // An exact double equality, deliberately, because that is what line 264 writes.
            var j = i;
            while (j < n - 1 && values[indexed[j]] == values[indexed[j + 1]])
            {
                j++;
            }

            // / 2.0, so the division is floating point and a run of even length takes a half rank.
            var averageRank = (i + j) / 2.0 + 1;
            for (var k = i; k <= j; k++)
            {
                ranks[indexed[k]] = averageRank;
            }

            i = j + 1;
        }

        return ranks;
    }

    private static void RequireSameLength(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        if (xs.Count != ys.Count)
        {
            throw new ArgumentException(
                $"The two axes must hold the same number of values: {xs.Count} and {ys.Count}.",
                nameof(ys));
        }
    }
}
