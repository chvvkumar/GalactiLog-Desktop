using System.Globalization;

namespace GalactiLog.Core.Metrics;

/// <summary>Numeric accumulation and rounding rules ported from Python, moved out of
/// <see cref="GalactiLog.Core.Phd2.Phd2Metrics"/> because a rounding rule that lives in the PHD2
/// namespace invites the next consumer to copy it rather than ask (ruling A4). Bodies and remarks
/// are unchanged from where they were cut.</summary>
public static class PythonNumerics
{
    /// <summary>Round <paramref name="value"/> to <paramref name="digits"/> decimal places exactly
    /// as Python 3's <c>round(x, n)</c> does. Every stored figure that the web rounds is rounded
    /// here, and this is the only rounding idiom in the PHD2 code.</summary>
    /// <remarks>
    /// Not <see cref="Math.Round(double, int)"/>, which is a different function. .NET rounds the
    /// SHORTEST round-trippable decimal form of the double, so a value whose shortest form carries
    /// a 5 at position n+1 is treated as a tie and taken to the even digit even when the exact
    /// binary value is not at the midpoint at all. Python rounds the EXACT binary value, and
    /// applies round-half-to-even only to a true tie of that exact value, which needs the value to
    /// be exactly representable and is therefore rare.
    /// <para>
    /// Measured, not assumed: over the user's own 60 PHD2 guide logs, 937 guiding sections and
    /// 1,229,517 frames, the two functions disagreed on 7 sections, six in <c>settle_median_s</c>
    /// and one in <c>snr_mean</c>, each by one unit in the last stored decimal. A settle median of
    /// an even count is the mean of two PHD2 timestamps that each carry 3 decimals, which lands on
    /// an exact 4-decimal midpoint whenever the two differ in the last digit by an odd amount.
    /// </para>
    /// <para>
    /// The implementation is a round trip through "F{digits}". Since .NET Core 3.0 that formatting
    /// is exact and correctly rounded on the true binary value, and its tie rule was CHECKED
    /// against Python's on exactly representable ties rather than believed: 0.5 and 2.5 to 0
    /// digits give 0 and 2, 0.125 and 0.375 to 2 digits give 0.12 and 0.38, and 2.675 to 2 digits
    /// gives 2.67, which is the exact value's answer and not the shortest form's. An exact
    /// expansion of the mantissa and exponent through <c>System.Numerics.BigInteger</c> was
    /// written and measured beside it; it agrees on every row of the theory in
    /// <c>Phd2MetricsTests</c> and is thirty lines against these two, so it was not kept.
    /// </para>
    /// <para>
    /// Non-finite values pass through unchanged, negative zero is preserved, and a magnitude with
    /// no fractional part left to round is returned unchanged by the formatting itself, so there
    /// is no overflow path. <paramref name="digits"/> is zero or more: Python accepts a negative n
    /// to round to tens and no call site here wants one, and "F-1" is not a standard format string
    /// but a custom one, which would silently produce a different number.
    /// </para>
    /// </remarks>
    public static double RoundLikePython(double value, int digits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(digits);

        return double.IsFinite(value)
            ? double.Parse(
                value.ToString(
                    "F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                NumberStyles.Float,
                CultureInfo.InvariantCulture)
            : value;
    }

    /// <summary>The sum of <paramref name="values"/> rounded exactly once, as Python's
    /// <c>math.fsum</c> does. Zero for an empty sequence, as <c>fsum</c> gives.</summary>
    /// <remarks>
    /// Shewchuk's algorithm, ported from CPython's <c>Modules/mathmodule.c math_fsum</c>: a list of
    /// non-overlapping partial sums, each new value folded in with a two-sum, then the partials
    /// added from the largest down with a half-even correction on the last bit. The result is the
    /// double nearest the exact mathematical sum, so it does not depend on the order of the input.
    /// <para>
    /// Not Kahan and not Neumaier (<see cref="CompensatedSum"/>). A compensated sum is far more
    /// accurate than a naive one and is still not correctly rounded, so it can disagree with
    /// <c>fsum</c> in the last bit, and the bar for this port is that a stored figure matches the
    /// web to the last stored decimal, not that it is close.
    /// </para>
    /// <para>
    /// Measured, not assumed: this is the fix for the one parity difference found over the user's
    /// own 60 guide logs. Eight SNR values of
    /// <c>PHD2_GuideLog_2026-02-19_172102.txt</c> run 1 section 26 sum to 2596.69 exactly here and
    /// to 2596.6899999999996 left to right, which the mean and <see cref="RoundLikePython"/> then
    /// take to 324.5863 and 324.5862 respectively. One session of 937, one unit in the last stored
    /// decimal, and the whole reason the member exists.
    /// </para>
    /// <para>
    /// No non-finite handling, deliberately: <c>Phd2LogParser.ParseDouble</c> returns null for a
    /// value that parses to infinity or NaN, so no caller in this file can hand a non-finite value
    /// in, and CPython's branches for one would be unreachable code. Ceiling: CPython raises
    /// OverflowError when a partial overflows on finite input and this returns an infinity instead.
    /// That needs summands near <c>double.MaxValue</c>, which a PHD2 SNR or star mass column cannot
    /// reach.
    /// </para>
    /// </remarks>
    public static double ExactSum(IEnumerable<double> values)
    {
        var partials = new List<double>();

        foreach (var value in values)
        {
            var x = value;
            var kept = 0;

            for (var j = 0; j < partials.Count; j++)
            {
                var y = partials[j];
                if (Math.Abs(x) < Math.Abs(y))
                {
                    (x, y) = (y, x);
                }

                // Two-sum: hi is the rounded sum and residual is exactly what the rounding lost,
                // so hi + residual equals x + y with no error at all.
                var hi = x + y;
                var residual = y - (hi - x);
                if (residual != 0.0)
                {
                    partials[kept++] = residual;
                }

                x = hi;
            }

            partials.RemoveRange(kept, partials.Count - kept);

            // A zero carries no information and CPython drops it here too; keeping it would put a
            // 0.0 at the top of the list and defeat the half-even correction below.
            if (x != 0.0)
            {
                partials.Add(x);
            }
        }

        if (partials.Count == 0)
        {
            return 0.0;
        }

        var n = partials.Count - 1;
        var total = partials[n];
        var lost = 0.0;

        // Add from the top until the addition first becomes inexact. Everything below that point
        // is too small to move the result by itself.
        while (n > 0)
        {
            var x = total;
            var y = partials[--n];
            total = x + y;
            lost = y - (total - x);
            if (lost != 0.0)
            {
                break;
            }
        }

        // Half-even across several partials: when the next partial down has the same sign as what
        // the last addition lost, the exact sum is past the midpoint and the result steps one unit
        // in the last place. Without this, fsum([1e-16, 1, 1e16]) rounds down where the exact sum
        // rounds up.
        if (n > 0 && ((lost < 0.0 && partials[n - 1] < 0.0) || (lost > 0.0 && partials[n - 1] > 0.0)))
        {
            var doubled = lost * 2.0;
            var stepped = total + doubled;
            if (doubled == stepped - total)
            {
                total = stepped;
            }
        }

        return total;
    }

    /// <summary>The arithmetic mean of <paramref name="values"/> as Python's
    /// <c>statistics.fmean</c> computes it, which is <c>math.fsum(data) / n</c>. The callers guard
    /// on a non-empty list, as the Python's <c>if snr_vals</c> does, so an empty input here is a
    /// caller bug and gives NaN rather than a silent zero.</summary>
    public static double ExactMean(IReadOnlyCollection<double> values)
        => ExactSum(values) / values.Count;

    /// <summary>The sum of <paramref name="values"/> as CPython's builtin <c>sum()</c> computes it
    /// over floats: improved Kahan-Babuska compensated summation, due to Neumaier. Zero for an
    /// empty sequence.</summary>
    /// <remarks>
    /// Deliberately a second, weaker summation beside <see cref="ExactSum"/>, because the web is
    /// not consistent and the port matches what the web does site by site. CPython 3.12 gave the
    /// builtin <c>sum()</c> this compensation (the backend's <c>pyproject.toml</c> requires 3.12
    /// and its image is <c>python:3.12-slim</c>), so a bare <c>sum(...)</c> over a float column is
    /// compensated while an explicit <c>total += x</c> loop in the same file is not. Neumaier is
    /// not correctly rounded and does disagree with <see cref="ExactSum"/> on some input, so the
    /// two are not interchangeable, and this is the port's third accumulation rule beside that one
    /// and the plain left-to-right loops that match the Python's own <c>+=</c> sites.
    /// <para>
    /// The non-finite guard on the return is CPython's, not an invention: <c>Python/bltinmodule.c</c>
    /// adds the compensation only when the running total is still finite, because an infinite
    /// total makes every subsequent compensation term a NaN and a bare <c>total + compensation</c>
    /// would turn <c>sum([inf, 1.0])</c> into NaN where Python gives infinity. Checked against the
    /// interpreter on this machine over a table that includes both infinities, a NaN summand, an
    /// intermediate overflow of finite summands and both signed zeros, and over 300,000 random
    /// lists spanning 600 decades: no disagreement. It is not reachable from this port's callers,
    /// because <c>Phd2LogParser.ParseDouble</c> gives null for anything non-finite, but three
    /// lines that match the source are cheaper than a reader wondering.
    /// </para>
    /// </remarks>
    public static double CompensatedSum(IEnumerable<double> values)
    {
        var total = 0.0;
        var compensation = 0.0;

        foreach (var value in values)
        {
            var stepped = total + value;

            // The larger magnitude keeps its bits; the smaller is the one whose low bits the
            // addition drops, and those dropped bits are what accumulates in the compensation.
            compensation += Math.Abs(total) >= Math.Abs(value)
                ? (total - stepped) + value
                : (value - stepped) + total;

            total = stepped;
        }

        return double.IsFinite(total) ? total + compensation : total;
    }
}
