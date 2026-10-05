using System.Globalization;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// Spec 12.14's Correlation verdict sentence, the port of <c>describeCorrelation</c>
/// (<c>CorrelationChart.tsx</c> lines 34 to 67) with that section's one correction.
/// </summary>
/// <remarks>
/// <para>
/// A pure static with no state and no view-model of its own: it takes a trend, a point count and
/// the two metrics and answers one string. The four band names, the three figures and their two
/// decimals are the web's; the trailing clause is not, and spec 12.14 departure 7 is why.
/// </para>
/// <para>
/// <b>The clause is direction neutral.</b> The web's two stronger bands assume a lower Y is better:
/// a rising slope is a "negative impact" and a falling one "improves". That reading is wrong for
/// <c>detected_stars</c>, where more is better, and it is a judgement about the reader's data
/// rather than a description of it. The seven clauses below name which way Y moves with X and stop
/// there, so none of them carries "negative impact", "improves", "worse" or "better".
/// </para>
/// <para>
/// The figures are written <c>R2</c> and <c>rho</c> rather than with U+00B2 and U+03C1, for the
/// reason ruling B15 gives for the arcsecond mark: a glyph ships only where the application's own
/// embedded faces draw it, and spec 12.14 already spells this line in ASCII.
/// </para>
/// </remarks>
public static class CorrelationVerdict
{
    /// <summary>Spec 12.14's fixed disclaimer, drawn under the sentence.</summary>
    public const string Disclaimer =
        "Correlations show statistical associations, not causation. Many factors affect image "
        + "quality simultaneously.";

    /// <summary>The <c>r_squared</c> band names, in the order the thresholds below take them.</summary>
    public const string NoMeaningfulBand = "No meaningful correlation";

    /// <inheritdoc cref="NoMeaningfulBand"/>
    public const string WeakBand = "Weak correlation";

    /// <inheritdoc cref="NoMeaningfulBand"/>
    public const string ModerateBand = "Moderate correlation";

    /// <inheritdoc cref="NoMeaningfulBand"/>
    public const string StrongBand = "Strong correlation";

    // The three thresholds of CorrelationChart.tsx lines 43, 46 and 51, each an exclusive upper
    // bound: below 0.05, below 0.15, below 0.4, and everything else.
    private const double WeakFloor = 0.05d;
    private const double ModerateFloor = 0.15d;
    private const double StrongFloor = 0.4d;

    // The web's own gate, and Analysis.Trend answers null below the same count anyway.
    private const int MinimumPoints = 3;

    /// <summary>
    /// The sentence for one result: a band name, the three figures and a direction-neutral clause,
    /// or the too-few sentence when there is no trend or fewer than three points.
    /// </summary>
    /// <param name="trend">The result's trend, null when the least squares fit had no answer.</param>
    /// <param name="pointCount">The result's full point count, before any outlier hiding.</param>
    /// <param name="x">The X metric, named by its <c>METRIC_SHORT</c> name.</param>
    /// <param name="y">The Y metric, named by its <c>METRIC_SHORT</c> name.</param>
    public static string Describe(TrendLine? trend, int pointCount, AnalysisMetric x, AnalysisMetric y)
    {
        if (trend is null || pointCount < MinimumPoints)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Not enough data to determine a pattern ({0} points).",
                pointCount);
        }

        var xName = AnalysisMetricLabels.For(x).Short;
        var yName = AnalysisMetricLabels.For(y).Short;

        // CorrelationChart.tsx line 41 is trend.slope > 0, so a slope of exactly zero takes the
        // falling arm. Kept, because the two arms differ only in which way they say Y moves and a
        // third arm for a flat fit would be a sentence the web never shows.
        var rising = trend.Slope > 0d;

        var (band, clause) = trend.RSquared switch
        {
            < WeakFloor => (
                NoMeaningfulBand,
                $"{xName} does not appear to move your {yName}."),
            < ModerateFloor => (
                WeakBand,
                rising
                    ? $"{yName} tends to be slightly higher at higher {xName}, but the effect is minor."
                    : $"{yName} tends to be slightly lower at higher {xName}, but the effect is minor."),
            < StrongFloor => (
                ModerateBand,
                rising
                    ? $"Higher {xName} goes with higher {yName} in your data."
                    : $"Higher {xName} goes with lower {yName} in your data."),
            _ => (
                StrongBand,
                rising
                    ? $"{yName} rises with {xName} in your data. This is a strong pattern at your site."
                    : $"{yName} falls as {xName} rises in your data. This is a strong pattern at your site."),
        };

        var figures = string.Format(
            CultureInfo.InvariantCulture,
            "(R2={0:F2}, Pearson r={1:F2}, Spearman rho={2:F2})",
            trend.RSquared,
            trend.PearsonR,
            trend.SpearmanRho);

        return $"{band} {figures}. {clause}";
    }
}
