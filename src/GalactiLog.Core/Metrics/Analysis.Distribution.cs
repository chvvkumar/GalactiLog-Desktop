using System.Globalization;

namespace GalactiLog.Core.Metrics;

// The distribution half of the Analysis statistics core: quartiles, fences, the summary stats,
// the box plot, the histogram, skewness, the moving average and the compare verdict (task2.md
// sections 5.1 to 5.4, 5.8, 5.9, 5.10 and 5.12). The correlation half is Analysis.Correlation.cs,
// which also declares the constants. Port of backend/app/api/analysis.py at 591234b, and a line
// number in a summary below with no other file named is that file.
//
// Two rules bind every figure here. Every round(x, n) of the Python goes through
// PythonNumerics.RoundLikePython, and every sum() of the Python goes through
// PythonNumerics.CompensatedSum, which is what CPython's builtin sum() does over floats. The order
// in which values are summed is part of the answer, so a member that sorts before it sums says so
// and a member that does not never sorts.
public static partial class Analysis
{
    /// <summary>The first and third quartiles of <paramref name="values"/>, the EXCLUSIVE method:
    /// on an odd count the middle value belongs to neither half. Port of lines 205, 206, 231 and
    /// 232, which are the same two lines written twice in the Python. Not rounded: the quartiles
    /// feed <see cref="Fences"/> and <see cref="Box"/>, and only the box plot's own outputs
    /// round.</summary>
    /// <exception cref="ArgumentException"><paramref name="values"/> holds fewer than two values,
    /// which would leave the lower half empty. Python would raise from
    /// <c>statistics.median</c>; every caller here already guards at 2 or 4, so an input this
    /// small is a programming error and not a user path.</exception>
    public static Quartiles QuartilesOf(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count < 2)
        {
            throw new ArgumentException(
                $"Quartiles need at least 2 values; {values.Count} were given.", nameof(values));
        }

        var sorted = Sorted(values);

        // Neither slice is re-sorted by hand. Statistics.Median sorts what it is given (ruling A5:
        // one median), and a slice of an ascending list is already ascending, so that sort is a
        // no-op that costs one copy.
        return QuartilesOfSorted(sorted);
    }

    /// <summary>The 1.5 times interquartile range outlier fences of <paramref name="values"/>.
    /// Port of lines 209, 210, 233 and 234. Not rounded.</summary>
    public static OutlierFences Fences(IReadOnlyList<double> values)
        => FencesFrom(QuartilesOf(values));

    /// <summary>Whether <paramref name="value"/> lies outside <paramref name="fences"/>, strictly
    /// on both sides. Port of line 234.</summary>
    /// <remarks>
    /// Defect D5, the one correction to the Python's SHAPE in this file, and it changes no answer.
    /// Lines 226 to 236 recompute both axes' quartiles inside a call made once per point (line
    /// 446), which sorts both axes once per point and is quadratic in the point count on a page
    /// whose frame granularity reaches six figures. The quartiles do not depend on the point being
    /// tested, so the caller computes <see cref="Fences"/> once per axis and tests every point
    /// against them, which gives bit-identical flags. Spec 12.14's IQR paragraph states the
    /// correction and <c>AnalysisDistributionTests</c> proves the identity against a literal
    /// transcription of <c>_is_outlier_iqr</c>.
    /// </remarks>
    public static bool IsOutlier(double value, OutlierFences fences)
    {
        ArgumentNullException.ThrowIfNull(fences);

        return value < fences.Low || value > fences.High;
    }

    /// <summary>The five-figure summary of <paramref name="values"/>, or null when fewer than two
    /// values were given, which is the Python's own answer (line 184) and never a throw. Port of
    /// <c>_compute_summary_stats</c>, lines 183 to 197.</summary>
    /// <remarks>
    /// The mean and the variance are summed over the SORTED copy, because lines 188 and 189 sum
    /// <c>s</c> and not <c>values</c>. Floating point addition is not associative, so the order is
    /// part of the answer: summing the caller's order here moves the sixth decimal on real data.
    /// The deviation is the SAMPLE deviation, divisor <c>n - 1</c>, so a count of 2 is legal and
    /// divides by 1.
    /// </remarks>
    public static SummaryStats? Summary(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count < 2)
        {
            return null;
        }

        var s = Sorted(values);
        var n = s.Count;
        var mean = PythonNumerics.CompensatedSum(s) / n;

        // (v - mean) * (v - mean), and not Math.Pow(v - mean, 2). CPython's float_pow does NOT
        // special-case an integral exponent; it calls the platform pow, so line 189's ** 2 is
        // whatever that platform's pow returns. Measured against exact rational arithmetic over
        // 200,000 doubles: z * z is wrong 0 times, so the multiplication IS the correctly rounded
        // square, while the oracle's own Windows CPython has z ** 2 wrong 90 times. glibc's pow has
        // been correctly rounded since 2.28 and the web runs python:3.12-slim, so on the web's own
        // platform z ** 2 and z * z are the same double and this square is bit identical to what
        // the user's web application computes. Math.Pow is not the remedy: it is .NET's own libm, a
        // THIRD rule that reproduces neither glibc nor MSVC.
        var variance = PythonNumerics.CompensatedSum(s.Select(v => (v - mean) * (v - mean))) / (n - 1);

        return new SummaryStats(
            n,
            PythonNumerics.RoundLikePython(s[0], 6),
            PythonNumerics.RoundLikePython(s[n - 1], 6),
            PythonNumerics.RoundLikePython(mean, 6),
            PythonNumerics.RoundLikePython(Statistics.Median(s)!.Value, 6),
            PythonNumerics.RoundLikePython(Math.Sqrt(variance), 6));
    }

    /// <summary>One group's box plot, or null when fewer than four values were given, which is how
    /// a group DISAPPEARS from the box plot (lines 201 and 650 to 652); the drop is silent and the
    /// help topic explains it. Port of <c>_compute_box_plot</c>, lines 200 to 223.</summary>
    /// <remarks>
    /// <see cref="BoxPlot.Min"/> and <see cref="BoxPlot.Max"/> are the WHISKER ends and not the
    /// group's extremes (lines 211 and 212), while <see cref="BoxPlot.Count"/> is the whole group
    /// including the outliers (line 222).
    /// <para>
    /// Line 211's <c>min(v for v in s if v >= lower_fence)</c> would raise on an empty sequence and,
    /// for any FINITE input, cannot: <c>Low &lt;= q1 &lt;= q3 &lt;= s[n - 1]</c>, so the LARGEST
    /// value always qualifies. Line 212's <c>max(v for v in s if v &lt;= upper_fence)</c> is the
    /// mirror and cannot be empty for the mirror reason, <c>s[0] &lt;= q1 &lt;= q3 &lt;= High</c>,
    /// so the SMALLEST value always qualifies. Neither reason is that <c>q1</c> is a member of
    /// <c>s</c>, because on an even lower half it is the mean of two values and need not be.
    /// </para>
    /// <para>
    /// The finite qualifier is not decoration: a NaN among the values makes every
    /// <c>v &gt;= Low</c> false and this throws, which is parity, because line 211's own
    /// <c>min()</c> over the same empty generator raises <c>ValueError</c>. It is unreachable from
    /// the database, where SQLite stores a NaN as NULL and a NULL is skipped before the values
    /// reach here. An infinity answers rather than throwing: the fences become
    /// <c>[-inf, inf]</c> and both predicates match.
    /// </para>
    /// <para>
    /// ponytail: three sorts of one list, here, in <see cref="QuartilesOf"/> and inside
    /// <see cref="Statistics.Median(IEnumerable{double})"/>, holding three n-element copies at once.
    /// <see cref="Summary"/> has the same ceiling at two sorts and two copies, and at 200,000
    /// values it is the larger of the two allocations because it runs over the whole selection
    /// where this runs per GROUP. Neither is worth an optimisation: the ceiling is a handful of
    /// sorts per tab load (task2.md section 9). The upgrade path, if a measurement ever asks for
    /// it, is an internal median overload that takes an already sorted list, for both members; a
    /// second median of its own is what ruling A5 forbids.
    /// </para>
    /// </remarks>
    public static BoxPlot? Box(IReadOnlyList<double> values, string groupName)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count < 4)
        {
            return null;
        }

        var s = Sorted(values);
        var n = s.Count;
        var quartiles = QuartilesOfSorted(s);
        var fences = FencesFrom(quartiles);

        // s is ascending, so the first value at or above the low fence IS the smallest such value
        // and the last at or below the high fence IS the largest, which is what lines 211 and 212
        // take with min() and max() over the same filtered sequences.
        var whiskerLow = s.First(v => v >= fences.Low);
        var whiskerHigh = s.Last(v => v <= fences.High);

        // Line 213 iterates the sorted list, so the outliers come back ascending.
        var outliers = s
            .Where(v => IsOutlier(v, fences))
            .Select(v => PythonNumerics.RoundLikePython(v, 6))
            .ToArray();

        return new BoxPlot(
            groupName,
            PythonNumerics.RoundLikePython(whiskerLow, 6),
            PythonNumerics.RoundLikePython(quartiles.Q1, 6),
            PythonNumerics.RoundLikePython(Statistics.Median(s)!.Value, 6),
            PythonNumerics.RoundLikePython(quartiles.Q3, 6),
            PythonNumerics.RoundLikePython(whiskerHigh, 6),
            outliers,
            n);
    }

    /// <summary>Equal-width Sturges bins over <paramref name="values"/>, empty for an empty input.
    /// Port of lines 533 to 543, with user ruling U1's count correction on the spread branch. Both
    /// edges are rounded to six decimals (line 543) and the count is an exact integer.</summary>
    /// <remarks>
    /// <b>User ruling U1 (choice 19), a recorded departure from line 542.</b> The last bin's upper
    /// edge is <c>vMax</c> and admits it, so every value is counted and the counts sum to the value
    /// count. The Python's own last bin ends at the accumulated
    /// <c>vMin + (nBins - 1) * width + width</c>, which is not exactly <c>vMax</c> in binary for
    /// most inputs; when it lands below <c>vMax</c> the <c>v == b_end</c> equality fails and the
    /// maximum value is admitted to no bin at all. Measured on the user's own 416 LIGHT frames:
    /// <c>vMin</c> 0.0, <c>vMax</c> 2.6245, 10 bins, width 0.26244999999999996, last edge
    /// 2.6244999999999994, and the web's bin counts sum to 415 of 416.
    /// <para>
    /// <b>The rule applies ONLY when <c>vMax &gt; vMin</c></b> (ruling S8). When every value is
    /// equal the width is the Python's literal <c>1.0</c> and the last bin's start is already above
    /// <c>vMax</c>, so forcing the edge down to <c>vMax</c> would publish an INVERTED bin whose end
    /// is below its start. The constant branch therefore keeps the Python's own shape, and the
    /// counts sum to the value count there too.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<HistogramBin> Histogram(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return [];
        }

        // Math.Log2, not Math.Log(n, 2): the two give different doubles and the Ceiling can then
        // differ by a whole bin.
        var nBins = Math.Max(1, (int)Math.Ceiling(Math.Log2(values.Count) + 1));
        var vMin = values.Min();
        var vMax = values.Max();
        var width = vMax > vMin ? (vMax - vMin) / nBins : 1.0;

        var bins = new List<HistogramBin>(nBins);
        for (var i = 0; i < nBins; i++)
        {
            // start is recomputed from i and never accumulated, and end is start + width and not
            // vMin + (i + 1) * width: those are different doubles and both edges are shown.
            var start = vMin + i * width;
            var last = i == nBins - 1;
            var end = last && vMax > vMin ? vMax : start + width;

            var count = last
                ? values.Count(v => start <= v && v <= end)
                : values.Count(v => start <= v && v < end);

            // What the web does on the last bin, kept here so the departure is readable:
            //     : values.Count(v => (start <= v && v < end) || v == end);
            bins.Add(new HistogramBin(
                PythonNumerics.RoundLikePython(start, 6),
                PythonNumerics.RoundLikePython(end, 6),
                count));
        }

        return bins;
    }

    /// <summary>The Fisher skewness of <paramref name="values"/>, <c>0.0</c> when the deviation is
    /// not above zero or fewer than three values were given. Port of lines 545 to 552. Rounded to
    /// four decimals by the caller (line 558), not here.</summary>
    /// <param name="values">The values, in the caller's own order: line 550 sums them as given and
    /// never sorts, so the order is part of the answer.</param>
    /// <param name="roundedMean">The mean ALREADY ROUNDED to six decimals.</param>
    /// <param name="roundedStdDev">The standard deviation ALREADY ROUNDED to six decimals.</param>
    /// <remarks>
    /// The two parameters are the rounded <see cref="SummaryStats"/> fields and not the full
    /// precision figures. Lines 546 and 547 read <c>stats.mean</c> and <c>stats.std_dev</c> off the
    /// object line 190 built, whose fields lines 193 and 196 already rounded. This reads like a
    /// mistake in the source and it is not one to fix: on the user's own 416 frames the rounded
    /// pair gives -1.9676561028705923 and the unrounded pair -1.9676532549512464, which part at the
    /// fifth decimal and would part at the fourth on other data.
    /// <para>
    /// The cast has to sit INSIDE the denominator. <c>(double)n / ((n - 1) * (n - 2))</c> would
    /// evaluate an unchecked <c>int</c> product before the double numerator is involved, and the
    /// first <c>n</c> at which that overflows is 46,343: 46,342 times 46,341 is 2,147,534,622,
    /// above <c>int.MaxValue</c>. The overflow gives a negative denominator and a skewness with the
    /// wrong sign, silently, only on large libraries.
    /// </para>
    /// </remarks>
    public static double Skewness(IReadOnlyList<double> values, double roundedMean, double roundedStdDev)
    {
        ArgumentNullException.ThrowIfNull(values);

        var n = values.Count;
        if (!(roundedStdDev > 0) || n <= 2)
        {
            return 0.0;
        }

        // z * z * z, as task2.md 5.9 briefs it, and this is a RECORDED DEPARTURE rather than the
        // faithful form: the cube goes the opposite way to Summary's square. Two multiplications
        // carry two roundings, so measured against exact rational arithmetic over 200,000 doubles
        // z * z * z differs from the correctly rounded cube 51,434 times, about one unit in the
        // last place on a quarter of terms, where line 550's ** 3 differs 98 times, on every
        // platform including the web's. The effect on the PUBLISHED figure was measured at zero:
        // over 40,000 whole-set trials at HFR and ADU magnitudes, with the mean and deviation
        // rounded to 6 and the skewness to 4 exactly as the page shows them, the two forms never
        // parted, and on the user's own 416 frames both give -1.9677. The per-term ulp is absorbed
        // by the sum over hundreds of terms and then by the rounding. Math.Pow is not the remedy
        // for the same reason Summary gives: it is a third rule.
        return (double)n / ((double)(n - 1) * (n - 2))
            * PythonNumerics.CompensatedSum(values.Select(v =>
            {
                var z = (v - roundedMean) / roundedStdDev;
                return z * z * z;
            }));
    }

    /// <summary>The trailing moving average of <paramref name="values"/> over a window of
    /// <paramref name="window"/>, one entry per position where the window is FULL, carrying the
    /// index into <paramref name="values"/> so the caller attaches that point's date (line 730).
    /// Port of <c>_moving_avg</c>, lines 723 to 733. Each value is rounded to six decimals (line
    /// 731).</summary>
    /// <param name="values">The nightly medians ALREADY ROUNDED to six decimals. Line 721 builds
    /// <c>raw_values</c> from <c>p.value</c>, which line 716 already rounded, and <c>_moving_avg</c>
    /// sums those. A caller that passes the unrounded medians produces a different sixth decimal on
    /// every point.</param>
    /// <param name="window">How many POINTS the window spans, that is nights with a frame and not
    /// calendar days: a month long gap does not widen it.</param>
    /// <returns>An empty list when <paramref name="window"/> exceeds the value count, because the
    /// first <c>window - 1</c> positions emit nothing.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> is zero or negative.
    /// The two call sites pass the constants 7 and 30.</exception>
    public static IReadOnlyList<(int Index, double Value)> MovingAverage(
        IReadOnlyList<double> values, int window)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(window);

        var result = new List<(int Index, double Value)>();
        for (var i = 0; i < values.Count; i++)
        {
            var start = Math.Max(0, i - window + 1);
            var length = i + 1 - start;
            if (length < window)
            {
                continue;
            }

            var chunk = Enumerable.Range(start, length).Select(j => values[j]);
            result.Add((i, PythonNumerics.RoundLikePython(PythonNumerics.CompensatedSum(chunk) / length, 6)));
        }

        return result;
    }

    /// <summary>The Compare tab's verdict sentence, naming the group with the LOWER median first.
    /// Port of <c>_pct_verdict</c>, lines 890 to 901, with user ruling U2's denominator
    /// correction.</summary>
    /// <param name="countA">The value count of group A, which is
    /// <see cref="SummaryStats.Count"/> of its PIXEL stats even when the verdict ran on arcsecond
    /// medians: lines 898 and 900 read <c>stats_a.count</c> either way. The web's own mismatch, kept
    /// so the sentence is the web's.</param>
    /// <param name="unit">Empty for every metric but <c>hfr</c>, and <c>" (arcsec)"</c> for
    /// <c>hfr</c> when the arcsecond comparison ran (line 913). The leading space is part of the
    /// string.</param>
    /// <remarks>
    /// <b>User ruling U2 (choice 20), a recorded departure from lines 891 to 896.</b> The web
    /// divides by <c>med_a</c> whatever the sentence says, but the sentence always names the SMALLER
    /// median first and phrases it as lower THAN the larger, so when the smaller is less than half
    /// the larger the sentence reads over 100 percent lower, which is not a quantity that exists.
    /// Three of three comparisons on the user's own data print one: the two rigs read "126% lower
    /// median (arcsec)" at 1.772 against 3.997, where the honest figure is 56. The denominator here
    /// is the LARGER median, which is the one the sentence says the smaller is lower than.
    /// <para>
    /// Everything else is the web's and is unchanged: the three branches, the wording, the
    /// <c>(N=a vs N=b)</c> tail and the half-to-even formatting of the percentage.
    /// </para>
    /// </remarks>
    public static string CompareVerdict(
        string groupA, string groupB,
        double medianA, double medianB,
        int countA, int countB,
        string unit)
    {
        // What the web does, kept here so the departure is readable:
        //   pct = medianA != 0 ? Math.Abs(medianA - medianB) / Math.Abs(medianA) * 100
        //       : medianB != 0 ? Math.Abs(medianA - medianB) / Math.Abs(medianB) * 100
        //       : 0;
        var basis = Math.Max(Math.Abs(medianA), Math.Abs(medianB));
        var pct = basis != 0 ? Math.Abs(medianA - medianB) / basis * 100 : 0;

        if (medianA < medianB)
        {
            return $"{groupA} has {Pct(pct)}% lower median{unit} than {groupB} (N={countA} vs N={countB})";
        }

        if (medianB < medianA)
        {
            return $"{groupB} has {Pct(pct)}% lower median{unit} than {groupA} (N={countB} vs N={countA})";
        }

        return $"Both groups have identical median values (N={countA} vs N={countB})";
    }

    // Python's f"{x:.0f}", which rounds half to even on the EXACT binary value. ToString("F0")
    // rounds the shortest round-trippable form and Math.Round is a third rule, so either would
    // print 3 where the Python prints 2 for 2.5. InvariantCulture explicitly: a comma decimal
    // separator or a group separator would ship in another locale.
    private static string Pct(double value)
        => PythonNumerics.RoundLikePython(value, 0).ToString("0", CultureInfo.InvariantCulture);

    // One ascending copy. The caller's sequence is never reordered, so passing a live list is safe.
    private static List<double> Sorted(IReadOnlyList<double> values)
    {
        var sorted = new List<double>(values);
        sorted.Sort();
        return sorted;
    }

    // Lines 205 and 206 over an already ascending list: the lower half is the first n / 2 values
    // and the upper half runs from (n + 1) / 2 to the end, both integer divisions, so on an odd n
    // the middle value is in neither.
    private static Quartiles QuartilesOfSorted(List<double> sorted)
    {
        var n = sorted.Count;
        return new Quartiles(
            Statistics.Median(sorted.GetRange(0, n / 2))!.Value,
            Statistics.Median(sorted.GetRange((n + 1) / 2, n - ((n + 1) / 2)))!.Value);
    }

    // Lines 209 and 210, written in the Python's own shape: q1 - 1.5 * iqr, not q1 - iqr * 1.5 and
    // not q1 - (q3 - q1) * 1.5, because each is a different rounding path. The one home for the
    // expression, so Box and Fences cannot drift apart.
    private static OutlierFences FencesFrom(Quartiles quartiles)
    {
        var iqr = quartiles.Q3 - quartiles.Q1;
        return new OutlierFences(quartiles.Q1 - 1.5 * iqr, quartiles.Q3 + 1.5 * iqr);
    }
}
