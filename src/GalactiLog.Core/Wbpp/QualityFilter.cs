using System.Globalization;
using System.Numerics;

namespace GalactiLog.Core.Wbpp;

/// <summary>The WBPP export's quality verdict: absolute thresholds the user typed, judged per LIGHT
/// frame on the metrics that frame actually carries, with a per-row override that replaces the
/// answer outright. Port of the verdict half of <c>frontend/src/lib/wbppQualityFilter.ts</c> and of
/// <c>isIncluded</c> in <c>frontend/src/lib/frameListFormats.ts</c> (task3a.md sections 5.1 to 5.5).
///
/// <para>Pure: no Avalonia, no database, no file system, no clock. Every number that becomes a
/// string goes through <see cref="CultureInfo.InvariantCulture"/>, so a machine whose culture writes
/// a comma decimal mark cannot put one into a failure sentence.</para>
///
/// <para>This type reads no baseline and no grade. The two band ladders of
/// <c>GalactiLog.Core.Metrics.FrameQuality</c> belong to the panel's cell colours, which is a
/// separate task; the verdict compares a frame's own value against the user's own limit and nothing
/// else (task3a.md section 4). <c>suggestRelaxation</c> is not ported: nothing in the web calls
/// it.</para></summary>
public static class QualityFilter
{
    /// <summary>The quick-fill thresholds offered on the eccentricity limit, in order: strict,
    /// balanced, relaxed. Eccentricity is the one constrained metric whose meaning is rig
    /// independent, which is why it is the only one with presets. The labels are the panel's, not
    /// this file's.</summary>
    public static IReadOnlyList<double> EccentricityPresets { get; } = [0.55, 0.65, 0.75];

    /// <summary>How many decimals the metric is displayed and compared with in a failure sentence.
    /// Detected stars is a count and carries none; every other metric carries two.</summary>
    public static int Decimals(WbppMetric metric) => metric == WbppMetric.Stars ? 0 : 2;

    /// <summary>True when a higher value of the metric is the better frame. Only detected stars is
    /// higher is better; sharpness, roundness and guiding error are all lower is better.</summary>
    public static bool BetterWhenHigh(WbppMetric metric) => metric == WbppMetric.Stars;

    /// <summary>The terse name the failure sentence uses, the port of the web's
    /// <c>METRIC_SHORT</c>. Deliberately shorter than a column heading, because the sentence sits
    /// inside a table cell beside the verdict glyph.</summary>
    public static string ShortName(WbppMetric metric) => metric switch
    {
        WbppMetric.Hfr => "HFR",
        WbppMetric.Ecc => "ecc",
        WbppMetric.Fwhm => "FWHM",
        WbppMetric.Stars => "stars",
        WbppMetric.Rms => "RMS",
        _ => throw UnknownMetric(metric),
    };

    /// <summary>The frame's own figure for the metric, or null when the frame does not carry it.
    /// <see cref="WbppMetric.Fwhm"/> reads <see cref="WbppFrame.Fwhm"/> and never a median form of
    /// it; <see cref="WbppMetric.Rms"/> reads <see cref="WbppFrame.GuidingRmsArcsec"/>, the one
    /// column the PHD2 correlation and the sidecar importer both fill, so the filter is provenance
    /// blind and carries no source field at all.</summary>
    public static double? ValueOf(WbppFrame frame, WbppMetric metric)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return metric switch
        {
            WbppMetric.Hfr => frame.MedianHfr,
            WbppMetric.Ecc => frame.Eccentricity,
            WbppMetric.Fwhm => frame.Fwhm,
            WbppMetric.Stars => frame.DetectedStars,
            WbppMetric.Rms => frame.GuidingRmsArcsec,
            _ => throw UnknownMetric(metric),
        };
    }

    /// <summary>The metric's value as a failure sentence prints it: fixed point at the metric's own
    /// <see cref="Decimals"/>, invariant culture, no unit and no padding.
    ///
    /// <para>This is the web's <c>Number.prototype.toFixed</c> to the last printed digit, which the
    /// framework's own <c>"F"</c> format is not: <c>toFixed</c> rounds the double's exact binary
    /// value half away from zero, while <c>"F"</c> rounds that same exact value half to even, so the
    /// two disagree on every binary exact tie (<c>0.125</c> prints <c>0.13</c> here and would print
    /// <c>0.12</c> there, <c>2.5</c> at no decimals prints <c>3</c> and would print <c>2</c>). The
    /// rounding is taken from the exact decimal expansion of the double and never from a scaled
    /// double, so a value that only looks like a tie, such as <c>1.005</c>, whose exact value sits
    /// below one, prints <c>1.00</c> as the web does.</para>
    ///
    /// <para>A value at or above <c>1e21</c> is the one known divergence, and is unreachable: the
    /// web switches to exponential notation there and this prints the digits in full. No metric a
    /// frame carries comes near it.</para></summary>
    public static string Format(WbppMetric metric, double value) => ToFixed(value, Decimals(metric));

    /// <summary>The constraint a freshly added chip carries: enabled, with no value, and with the
    /// comparison the metric's polarity implies. It gates nothing until the user types a number,
    /// and the threshold is deliberately not seeded from the data under judgment, which would
    /// always pass most of that data and read as authority it does not have.</summary>
    public static RawConstraint EmptyConstraintFor(WbppMetric metric)
        => new(metric, BetterWhenHigh(metric) ? ConstraintOp.AtLeast : ConstraintOp.AtMost, null, true);

    /// <summary>True when the constraint can actually judge a frame: it is enabled and holds a
    /// number. A disabled constraint does not exist for the verdict, and a valueless one is an
    /// empty input rather than a gate.</summary>
    public static bool IsActive(RawConstraint constraint)
    {
        ArgumentNullException.ThrowIfNull(constraint);

        return constraint.Enabled && constraint.Value is not null;
    }

    /// <summary>Whether the value satisfies the constraint. A value exactly on the threshold passes
    /// in both directions. A constraint with no value passes everything.</summary>
    public static bool Passes(RawConstraint constraint, double value)
    {
        ArgumentNullException.ThrowIfNull(constraint);

        if (constraint.Value is not double limit)
        {
            return true;
        }

        return constraint.Op == ConstraintOp.AtLeast ? value >= limit : value <= limit;
    }

    /// <summary>One frame's verdict: an AND over the active constraints, judged only on the metrics
    /// the frame carries. Port of <c>evaluateRawDetailed</c>.
    ///
    /// <para>Zero active constraints answers <see cref="Verdict.Copy"/>: an empty filter is no
    /// filter, not a filter that quarantines the whole library as unmeasured.</para>
    ///
    /// <para>A missing metric is skipped, not failed. That is a decision rather than an oversight: a
    /// limit the user added for their guided nights must not silently delete every unguided frame,
    /// and metric coverage across a library is uneven, so a frame recorded before a field existed is
    /// not a bad frame. A frame carrying none of the active constraints' metrics lands in
    /// <see cref="Verdict.Unmeasured"/>, which the page counts and labels separately, so the skip is
    /// visible rather than a quiet pass.</para>
    ///
    /// <para><see cref="FrameVerdict.Failures"/> holds every violated gate in the user's own
    /// constraint order and <see cref="FrameVerdict.FailedBy"/> is the first of them. Both are empty
    /// and null respectively on a <see cref="Verdict.Copy"/> or <see cref="Verdict.Unmeasured"/>
    /// verdict, because there is no gate to name.</para>
    ///
    /// <para>The caller hands this LIGHT frames only, and the rule is the caller's because
    /// <see cref="WbppFrame"/> carries no frame type at all: a calibration frame handed in here
    /// would be graded exactly like a light. There is one mapping site, the export page's
    /// projection from <c>SessionDetailQuery</c>'s <c>FrameRow</c> to <see cref="WbppFrame"/> in
    /// <c>WbppExportViewModel</c>, which filters to LIGHT once, so the rule has a choke point rather
    /// than a convention repeated at each call site. Every other file the export copies is copied
    /// unchanged and never reaches a gate.</para></summary>
    public static FrameVerdict Evaluate(WbppFrame frame, IReadOnlyList<RawConstraint> constraints)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(constraints);

        var anyActive = false;
        var present = 0;
        List<MetricFailure>? failures = null;

        for (var i = 0; i < constraints.Count; i++)
        {
            var constraint = constraints[i];
            if (!IsActive(constraint))
            {
                continue;
            }

            anyActive = true;
            if (ValueOf(frame, constraint.Metric) is not double value)
            {
                continue;
            }

            present++;
            if (!Passes(constraint, value))
            {
                failures ??= [];
                failures.Add(new MetricFailure(constraint.Metric, FailureText(constraint, value)));
            }
        }

        if (!anyActive)
        {
            return new FrameVerdict(frame, Verdict.Copy, null, []);
        }

        if (failures is not null)
        {
            return new FrameVerdict(frame, Verdict.Exclude, failures[0].Text, failures);
        }

        return new FrameVerdict(frame, present == 0 ? Verdict.Unmeasured : Verdict.Copy, null, []);
    }

    /// <summary>One verdict per frame, in the order the frames arrive. Port of
    /// <c>computeVerdicts</c>: nothing is grouped and nothing is sorted, which is the page's
    /// business. The LIGHT only rule is <see cref="Evaluate"/>'s remark and is the caller's to
    /// keep.</summary>
    public static IReadOnlyList<FrameVerdict> EvaluateAll(
        IEnumerable<WbppFrame> frames,
        IReadOnlyList<RawConstraint> constraints)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(constraints);

        var verdicts = new List<FrameVerdict>();
        foreach (var frame in frames)
        {
            verdicts.Add(Evaluate(frame, constraints));
        }

        return verdicts;
    }

    /// <summary>Whether the export copies this frame. The computed answer is every frame when
    /// <paramref name="filterEnabled"/> is false, and <see cref="Verdict.Copy"/> alone when it is
    /// true: an <see cref="Verdict.Unmeasured"/> frame is not copied, which is what makes the
    /// page's third verdict glyph mean something.
    ///
    /// <para>An override for the frame's <see cref="WbppFrame.ImageId"/> replaces that computed
    /// answer entirely, in both directions, and is how a user rescues an unmeasured row or drops a
    /// passing one. It is a replacement and never an "or": an override of false still excludes with
    /// the filter switched off. The key is the image id rather than the web's date and relative path
    /// pair, because the port has an id that is exactly unique (task3a.md section 5.4).</para></summary>
    public static bool IsIncluded(
        FrameVerdict verdict,
        bool filterEnabled,
        IReadOnlyDictionary<Guid, bool> overrides)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(overrides);

        return overrides.TryGetValue(verdict.Frame.ImageId, out var forced)
            ? forced
            : Computed(verdict, filterEnabled);
    }

    /// <summary>The selection's tally. <see cref="QualityTotals.Copy"/> is what
    /// <see cref="IsIncluded"/> says yes to; <see cref="QualityTotals.Exclude"/> and
    /// <see cref="QualityTotals.Unmeasured"/> split what it says no to by the frame's own verdict, a
    /// <see cref="Verdict.Copy"/> verdict that an override excludes counting under
    /// <see cref="QualityTotals.Exclude"/>. Those three always sum to
    /// <see cref="QualityTotals.Total"/>.
    ///
    /// <para><see cref="QualityTotals.Overridden"/> sits outside that sum: it counts the verdicts
    /// whose image id carries an override differing from the computed answer, which the page appends
    /// to the tally only when it is non-zero. It lives on the record rather than being recomputed
    /// beside it so the two figures cannot disagree.</para></summary>
    public static QualityTotals Totals(
        IReadOnlyList<FrameVerdict> verdicts,
        bool filterEnabled,
        IReadOnlyDictionary<Guid, bool> overrides)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        ArgumentNullException.ThrowIfNull(overrides);

        var copy = 0;
        var exclude = 0;
        var unmeasured = 0;
        var overridden = 0;

        for (var i = 0; i < verdicts.Count; i++)
        {
            var verdict = verdicts[i];
            var computed = Computed(verdict, filterEnabled);
            var hasOverride = overrides.TryGetValue(verdict.Frame.ImageId, out var forced);
            if (hasOverride && forced != computed)
            {
                overridden++;
            }

            if (hasOverride ? forced : computed)
            {
                copy++;
            }
            else if (verdict.Verdict == Verdict.Unmeasured)
            {
                unmeasured++;
            }
            else
            {
                exclude++;
            }
        }

        return new QualityTotals(verdicts.Count, copy, exclude, unmeasured, overridden);
    }

    // The inclusion answer before any override: the master switch, then the verdict.
    private static bool Computed(FrameVerdict verdict, bool filterEnabled)
        => !filterEnabled || verdict.Verdict == Verdict.Copy;

    private static ArgumentOutOfRangeException UnknownMetric(WbppMetric metric)
        => new(nameof(metric), metric, "Not one of the five WBPP quality metrics.");

    // Number.prototype.toFixed, reproduced exactly.
    //
    // The framework's "F" format rounds the double's exact binary value half to EVEN; toFixed
    // rounds that same exact value half AWAY FROM ZERO. Everything here works from the exact
    // decimal expansion of the double, built from its IEEE mantissa and exponent as an integer
    // ratio, so no scaled double is ever rounded: scaling in binary floating point is what turns
    // 1.005, whose exact value sits just below the tie, into a tie it is not.
    //
    // The sign follows the standard rather than the printed digits: toFixed puts a minus in front
    // of any value strictly below zero, so -0.001 at two decimals prints "-0.00", while negative
    // zero, which is not below zero, prints "0.00".
    private static string ToFixed(double value, int decimals)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return double.IsNegative(value) ? "-Infinity" : "Infinity";
        }

        var negative = value < 0;
        var bits = BitConverter.DoubleToInt64Bits(Math.Abs(value));
        var biasedExponent = (int)((bits >> 52) & 0x7FF);
        var fraction = bits & 0xF_FFFF_FFFF_FFFF;

        // A subnormal has no implicit leading bit and a fixed exponent; a normal carries both.
        var mantissa = biasedExponent == 0
            ? new BigInteger(fraction)
            : new BigInteger(fraction | (1L << 52));
        var exponent = biasedExponent == 0 ? -1074 : biasedExponent - 1075;

        var scaled = mantissa * BigInteger.Pow(10, decimals);
        BigInteger rounded;
        if (exponent >= 0)
        {
            // An exact integer once scaled: there is nothing to round.
            rounded = scaled << exponent;
        }
        else
        {
            var denominator = BigInteger.One << -exponent;
            rounded = BigInteger.DivRem(scaled, denominator, out var remainder);
            if (remainder * 2 >= denominator)
            {
                rounded += BigInteger.One;
            }
        }

        var digits = rounded.ToString(CultureInfo.InvariantCulture);
        if (decimals > 0)
        {
            digits = digits.PadLeft(decimals + 1, '0');
            digits = string.Concat(
                digits.AsSpan(0, digits.Length - decimals),
                ".",
                digits.AsSpan(digits.Length - decimals));
        }

        return negative ? "-" + digits : digits;
    }

    // The web's rawFailureText, literally: short name, the frame's value, the glyph of the
    // direction the frame violated, the limit. An AtMost limit prints ">" and an AtLeast limit
    // prints "<", which is the violated direction and not the constraint's own.
    private static string FailureText(RawConstraint constraint, double actual)
    {
        var glyph = constraint.Op == ConstraintOp.AtLeast ? "<" : ">";
        return string.Concat(
            ShortName(constraint.Metric),
            " ",
            Format(constraint.Metric, actual),
            " ",
            glyph,
            " ",
            Format(constraint.Metric, constraint.Value!.Value));
    }
}
