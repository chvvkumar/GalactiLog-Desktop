namespace GalactiLog.Core.Metrics;

/// <summary>One (telescope, camera, filter) group's robust baseline for one metric.</summary>
/// <param name="Median">Median of the non-null values, null when there are none.</param>
/// <param name="Mad">Median absolute deviation from that median. 0 for a uniform group.</param>
/// <param name="N">Count of non-null values, which is what gates <see cref="FrameQuality.MadZ"/>.
/// Per metric, never the group size: a group of 10 frames where only 3 carry a guiding RMS must
/// not grade the guiding RMS.</param>
public sealed record MetricBaseline(double? Median, double? Mad, int N)
{
    /// <summary>
    /// The baseline of a sample: the median of its non-null values, the median absolute deviation
    /// from that median, and the count of those non-null values. The web's <c>baselineOf</c>
    /// (<c>frontend/src/components/GuidingScorecard.tsx:72-77</c> and the identical body in
    /// <c>EquipmentPerformance.tsx</c>).
    /// </summary>
    /// <remarks>
    /// The one builder. It was written out three times before this member existed, identically in
    /// <c>EquipmentPerformanceViewModel</c> and in <c>GuidingStatsQuery</c>, which is design lesson
    /// 1's second occurrence and the review finding (Phase 15B, task5a-review P3-1) that folded
    /// them here. <see cref="FrameQuality.GroupBaselines"/> keeps its own inline construction
    /// deliberately: it counts the present values inside a loop it is already walking, so routing
    /// it through here would walk the bucket a second time and buy nothing.
    /// <para>
    /// <paramref name="values"/> is enumerated once. Null entries are dropped before any of the
    /// three figures is taken, so all three describe the same sample, which is what
    /// <see cref="N"/>'s own summary requires.
    /// </para>
    /// </remarks>
    public static MetricBaseline Of(IEnumerable<double?> values)
    {
        var present = values.Where(value => value is not null).ToList();
        return new MetricBaseline(Statistics.Median(present), Statistics.Mad(present), present.Count);
    }
}

/// <summary>
/// Spec 12.4's four grading bands. The band is carried by a cell's ink alone and never by a fill
/// (ruling C3), and <see cref="Neutral"/> is drawn as no mark at all.
/// </summary>
/// <remarks>
/// The one band enum of this application. The Statistics page carried a second, display-only copy
/// from Phase 11 until the Phase 14A fixer folded it onto this one (design lesson 1's second
/// occurrence); <c>EquipmentComboRowViewModel.BandForZ</c> went with it, leaving
/// <see cref="FrameQuality.BandForZ"/> as the only ladder.
/// <para>
/// The members are declared in spec 12.4's own band order, best to worst, which is not the order
/// the retired Statistics enum used. <c>default(QualityBand)</c> is therefore
/// <see cref="Better"/> and not <see cref="Neutral"/>: nothing may rely on the default, and a band
/// that is genuinely unknown is written as <see cref="Neutral"/> or as a null
/// <c>QualityBand?</c> in as many words.
/// </para>
/// </remarks>
public enum QualityBand
{
    /// <summary>Spec 12.4's better band. A deviation at or below -1.0, or a score of 60 and
    /// above.</summary>
    Better,

    /// <summary>Spec 12.4's neutral band, which every ungraded cell also takes. No mark.</summary>
    Neutral,

    /// <summary>Spec 12.4's watch band. A deviation at or above 1.5 and below 3.0, or a score from
    /// 30 up to but not including 45.</summary>
    Watch,

    /// <summary>Spec 12.4's reject band. A deviation at or above 3.0, or a score below 30.</summary>
    Reject,
}

/// <summary>The graded metrics of one frame, with its group identity already folded to canonical
/// names. Deliberately a small struct-shaped record rather than the images entity: the baselines
/// are computed over every LIGHT frame in the library and the entity carries raw_headers.</summary>
public sealed record GradedFrame(
    string? Telescope,
    string? Camera,
    string? FilterUsed,
    double? MedianHfr,
    double? Fwhm,
    double? Eccentricity,
    double? DetectedStars,
    double? AduMedian,
    double? GuidingRmsArcsec);

/// <summary>
/// Port of <c>backend/app/services/frame_quality.py</c>: MAD-based per-group baselines and the
/// signed robust z-score built on them. Pure functions, no database, no Avalonia. The session
/// insights of spec 12.4 and the statistics MADs of spec 12.5 read the same numbers from here,
/// so a sentence printed above a grading table can never contradict the table.
/// </summary>
public static class FrameQuality
{
    /// <summary>Minimum non-null values in a group before a z-score is computed at all. Below
    /// this the group is too sparse to grade and <see cref="MadZ"/> returns null, because staying
    /// silent beats a wrong claim. <c>frame_quality.MIN_GROUP</c>.</summary>
    public const int MinGroup = 8;

    /// <summary>Deviation at which a frame is an outlier, in raw MAD units. There is no 1.4826
    /// consistency scaling anywhere in the web application either, so these are not sigma.
    /// <c>frame_quality.Z_REJECT</c>.</summary>
    public const double ZReject = 3.0;

    /// <summary>Minimum frames behind a session median before it is compared against the rig
    /// baseline. <c>target_helpers.MIN_SESSION_MEDIAN_FRAMES</c>.</summary>
    public const int MinSessionMedianFrames = 3;

    /// <summary>The eccentricity metric name, the one <c>SessionDetailQuery</c> grades. Named so a
    /// caller does not spell a metric key by hand and miss silently.</summary>
    public const string EccentricityMetric = "eccentricity";

    private const double Eps = 1e-9;

    /// <summary>The metric names <see cref="GroupBaselines"/> computes, in the web
    /// application's order: median_hfr, fwhm, eccentricity, detected_stars, adu_median,
    /// guiding_rms_arcsec. Spec 7.1.1: <c>fwhm</c>, never <c>median_fwhm</c>.</summary>
    public static readonly string[] Metrics =
    [
        "median_hfr",
        "fwhm",
        EccentricityMetric,
        "detected_stars",
        "adu_median",
        "guiding_rms_arcsec",
    ];

    /// <summary><c>"{telescope}|{camera}|{filter_used}"</c> with a null component rendered as an
    /// empty string. The literal format of <c>frame_quality.group_key</c>, kept because the
    /// session baseline and the rig baseline are dictionaries that must key identically.</summary>
    public static string GroupKey(GradedFrame frame)
        => $"{frame.Telescope ?? ""}|{frame.Camera ?? ""}|{frame.FilterUsed ?? ""}";

    /// <summary>Per-group, per-metric baselines.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, MetricBaseline>>
        GroupBaselines(IEnumerable<GradedFrame> frames)
    {
        var groups = new Dictionary<string, List<GradedFrame>>(StringComparer.Ordinal);
        foreach (var frame in frames)
        {
            var key = GroupKey(frame);
            if (!groups.TryGetValue(key, out var bucket))
            {
                groups[key] = bucket = [];
            }

            bucket.Add(frame);
        }

        var result = new Dictionary<string, IReadOnlyDictionary<string, MetricBaseline>>(
            groups.Count,
            StringComparer.Ordinal);
        foreach (var (key, bucket) in groups)
        {
            var perMetric = new Dictionary<string, MetricBaseline>(Metrics.Length, StringComparer.Ordinal);
            foreach (var metric in Metrics)
            {
                var values = new List<double?>(bucket.Count);
                var present = 0;
                foreach (var frame in bucket)
                {
                    var value = Value(frame, metric);
                    values.Add(value);
                    if (value is not null)
                    {
                        present++;
                    }
                }

                // N counts this metric's non-nulls, not the bucket size. Statistics.Median and
                // Statistics.Mad skip the nulls themselves, so all three figures describe the
                // same sample.
                perMetric[metric] = new MetricBaseline(
                    Statistics.Median(values),
                    Statistics.Mad(values),
                    present);
            }

            result[key] = perMetric;
        }

        return result;
    }

    /// <summary>Signed robust z-score, null when the value is missing, the baseline is missing,
    /// the group is sparse (<c>N &lt; MinGroup</c>) or the group is uniform (<c>Mad == 0</c>).
    /// <paramref name="higherIsBetter"/> flips the sign so "worse than baseline" is always
    /// positive. Units are raw MAD, not sigma.</summary>
    public static double? MadZ(double? value, MetricBaseline? baseline, bool higherIsBetter = false)
    {
        if (value is not { } measured || baseline is null)
        {
            return null;
        }

        if (baseline.Median is not { } median || baseline.Mad is not { } deviation)
        {
            return null;
        }

        if (baseline.N < MinGroup || deviation == 0)
        {
            return null;
        }

        var z = (measured - median) / Math.Max(deviation, Eps);
        return higherIsBetter ? -z : z;
    }

    /// <summary>Spec 12.4's band table over a signed MAD deviation: null is
    /// <see cref="QualityBand.Neutral"/>, because an ungraded cell is neutral and draws no mark;
    /// at or below -1.0 is better; below 1.5 is neutral; below 3.0 is watch; otherwise reject.
    /// The ladder is written in that order so a value exactly at -1.0 is better and a value
    /// exactly at 1.5 is watch, which is what the spec's "at or below" and "at or above" mean.
    /// <c>frameQuality.bandForZ</c>.</summary>
    public static QualityBand BandForZ(double? z)
    {
        if (z is not { } deviation)
        {
            return QualityBand.Neutral;
        }

        if (deviation <= -1.0)
        {
            return QualityBand.Better;
        }

        if (deviation < 1.5)
        {
            return QualityBand.Neutral;
        }

        return deviation < 3.0 ? QualityBand.Watch : QualityBand.Reject;
    }

    /// <summary>Spec 12.4's row score: the three axes weighted 0.5 signal, 0.25 sharpness and
    /// 0.25 roundness, an axis whose deviation is null dropped and the surviving weights
    /// renormalised by their own sum, all three null giving null, and the weighted mean clamped
    /// to the range -3 to 3 and mapped to <c>50 - 16.7 * clamped</c>, a 0 to 100 figure where
    /// higher is better. <c>frameQuality.combinedScore</c>.</summary>
    /// <remarks>The constant is the literal 16.7, deliberately, and not <c>100.0 / 6.0</c>: the
    /// web application's number is 16.7, the two differ in the third decimal, and at the clamp
    /// that difference is enough to move a score across a band boundary. A tidy-up to the exact
    /// fraction is a behaviour change, not a simplification.</remarks>
    public static double? CombinedScore(double? zSignal, double? zSharp, double? zRound)
    {
        var weighted = 0.0;
        var weightSum = 0.0;

        Accumulate(zSignal, 0.5, ref weighted, ref weightSum);
        Accumulate(zSharp, 0.25, ref weighted, ref weightSum);
        Accumulate(zRound, 0.25, ref weighted, ref weightSum);

        if (weightSum == 0)
        {
            return null;
        }

        var mean = weighted / weightSum;
        var clamped = Math.Max(-3.0, Math.Min(3.0, mean));
        return 50 - (16.7 * clamped);

        static void Accumulate(double? z, double weight, ref double weighted, ref double weightSum)
        {
            if (z is not { } deviation)
            {
                return;
            }

            weighted += deviation * weight;
            weightSum += weight;
        }
    }

    /// <summary>Spec 12.4's band for a row score: 60 and above better, 45 up to 60 neutral, 30 up
    /// to 45 watch, below 30 reject. Null score, null band, which is the shape
    /// <c>FrameRowViewModel.ScoreBand</c> carries.</summary>
    /// <remarks>These are the score's own thresholds and they are not the z thresholds of
    /// <see cref="BandForZ"/>. The two ladders share four band names and nothing else; an
    /// implementer reading "the four bands" and applying the wrong ladder is the mistake this
    /// sentence exists to prevent.</remarks>
    public static QualityBand? BandForScore(double? score)
    {
        if (score is not { } value)
        {
            return null;
        }

        if (value >= 60)
        {
            return QualityBand.Better;
        }

        if (value >= 45)
        {
            return QualityBand.Neutral;
        }

        return value >= 30 ? QualityBand.Watch : QualityBand.Reject;
    }

    /// <summary>Frames at or beyond <paramref name="zThreshold"/> on the bad side of the baseline
    /// for their own group. Returns the outlier count and the count that had a usable baseline,
    /// so a caller can tell "nothing was bad" from "nothing could be judged" and stay silent in
    /// the second case.</summary>
    public static (int Outliers, int Graded) CountOutliers(
        IEnumerable<GradedFrame> frames,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, MetricBaseline>> baselines,
        string metric,
        double zThreshold = ZReject,
        bool higherIsBetter = false)
    {
        var outliers = 0;
        var graded = 0;
        foreach (var frame in frames)
        {
            var baseline = baselines.TryGetValue(GroupKey(frame), out var group)
                ? group.GetValueOrDefault(metric)
                : null;
            if (MadZ(Value(frame, metric), baseline, higherIsBetter) is not { } z)
            {
                continue;
            }

            graded++;
            if (z >= zThreshold)
            {
                outliers++;
            }
        }

        return (outliers, graded);
    }

    /// <summary>Turns a group key back into prose: <c>"RC8 / ASI2600MM / Ha"</c>, or
    /// <c>"Unknown rig"</c> when every component is empty. <c>target_helpers._group_label</c>.</summary>
    public static string GroupLabel(string groupKey)
    {
        var label = string.Join(" / ", groupKey.Split('|').Where(part => part.Length > 0));
        return label.Length == 0 ? "Unknown rig" : label;
    }

    /// <summary>The one place a metric name becomes a field. A name outside <see cref="Metrics"/>
    /// is a programming error, not a data condition, so it throws rather than reading as null and
    /// grading nothing.</summary>
    public static double? Value(GradedFrame frame, string metric) => metric switch
    {
        "median_hfr" => frame.MedianHfr,
        "fwhm" => frame.Fwhm,
        EccentricityMetric => frame.Eccentricity,
        "detected_stars" => frame.DetectedStars,
        "adu_median" => frame.AduMedian,
        "guiding_rms_arcsec" => frame.GuidingRmsArcsec,
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Not a graded metric."),
    };
}
