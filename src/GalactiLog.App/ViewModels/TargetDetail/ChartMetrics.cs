using System.Collections.Immutable;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// One chartable metric: its persisted key, its label, which column it reads, which theme token
/// colours it, and the four accessors that read the column out of every read model a chart plots.
/// </summary>
/// <param name="Key">The key stored in <c>graph.enabled_metrics</c> (spec 5.8.3). These are the
/// web application's <c>GraphSettings</c> keys, not the dashboard's metric-filter keys: the two
/// documents are different namespaces, and the dashboard's <c>MetricColumns</c> spells detected
/// stars <c>stars</c> while this document spells it <c>detected_stars</c>. Ruling Q16.</param>
/// <param name="Label">What the legend shows.</param>
/// <param name="Column">The frames column the series reads, verbatim from spec 13's two Target
/// detail chart rows. <c>fwhm</c>, never <c>median_fwhm</c>: spec 7.1.1 says the header FWHM is
/// never charted.</param>
/// <param name="TokenKey">The theme resource key, from spec 14.1. The mapping is fixed across
/// every chart and every theme (spec 13, 14.5): never reassign a hue per chart. Resolve it through
/// <c>ChartTheme.Palette.Metrics[TokenKey]</c>.</param>
/// <param name="Decimals">Digits after the point in a tooltip or an axis label.</param>
/// <param name="Unit">The suffix a formatted value carries, empty for a dimensionless one. This
/// is also what decides which Y axis a series lands on, and there is deliberately no per-metric
/// axis member: review finding 1 and the coordinator ruling on it put the rule in one place, at
/// series-build time. The primary metric (ruling Q22) and every enabled metric sharing its Unit go
/// on the left axis, enabled metrics of any other Unit go on the right, and a chart showing one
/// Unit has no right axis at all. Task 8 owns that function, for both charts.</param>
/// <param name="SessionValue">This metric's value for one collapsed session, the cross-session
/// chart's point. Null means the night carried no frame with the metric, which is a gap in the
/// line and never a zero (spec 13).</param>
/// <param name="FrameValue">This metric's value for one frame, the per-session chart's point.
/// Null on the same rule.</param>
/// <param name="FilterValue">This metric's median for one filter of one session, which is what a
/// per-filter split of the cross-session chart plots (spec 12.4 puts per-filter medians on the
/// expanded session detail).</param>
/// <param name="SessionRange">The precomputed session range this metric belongs to, or null for a
/// metric <c>SessionDetail</c> carries no <c>MetricRangeSummary</c> for. The per-session chart's
/// median reference line reads <c>Median</c> from here rather than recomputing it: two medians
/// that can disagree is a defect waiting to happen (Task 2 owns the figure).</param>
/// <param name="NightFrameValue">This metric's value for one frame as the Compare nights lanes plot
/// it.</param>
/// <remarks>
/// The four accessors live on the record rather than in a switch inside a chart view-model
/// (coordinator ruling on Task 7 review deviation 6): the spine is the table, so adding a sixth
/// metric is one row here and no edit in either chart.
/// </remarks>
public sealed record ChartMetric(
    string Key,
    string Label,
    string Column,
    string TokenKey,
    int Decimals,
    string Unit,
    Func<SessionOverview, double?> SessionValue,
    Func<FrameRow, double?> FrameValue,
    Func<FilterMedians, double?> FilterValue,
    Func<SessionDetail, MetricRangeSummary?> SessionRange,
    Func<NightFramePoint, double?> NightFrameValue);

/// <summary>
/// Spec 13's fixed <c>metric-*</c> series mapping, as data. The one table both charts read, so
/// neither writes its own switch over metric keys.
/// </summary>
public static class ChartMetrics
{
    /// <summary>Spec 13's five Target detail series, in the order the legend shows them. Ruling
    /// Q17: five and no more. The web application also charts <c>sensor_temp</c>,
    /// <c>ambient_temp</c>, <c>humidity</c>, <c>cloud_cover</c> and <c>airmass</c>, all on
    /// <c>metric-temp</c>; spec 13 lists five series, so the port ships five.
    /// <para>
    /// Ruling Q22: the primary metric is the first entry here that is enabled and has data, so the
    /// order is load-bearing and not just cosmetic.
    /// </para>
    /// <para>
    /// Every FWHM accessor reads the <c>fwhm</c> column's projection, never a header FWHM: there
    /// is no accessor for <c>median_fwhm</c> anywhere in this table, which is spec 7.1.1 enforced
    /// by absence rather than by a comment.
    /// </para></summary>
    public static readonly ImmutableArray<ChartMetric> All =
    [
        new("hfr", "HFR (px)", "median_hfr", "ColorMetricHfr", 2, " px",
            session => session.MedianHfr,
            frame => frame.MedianHfr,
            filter => filter.MedianHfr,
            detail => detail.Hfr,
            frame => frame.Hfr),
        new("eccentricity", "Ecc", "eccentricity", "ColorMetricEccentricity", 2, "",
            session => session.MedianEccentricity,
            frame => frame.Eccentricity,
            filter => filter.MedianEccentricity,
            detail => detail.Eccentricity,
            frame => frame.Eccentricity),
        new("fwhm", "FWHM", "fwhm", "ColorMetricFwhm", 2, " arcsec",
            session => session.MedianFwhm,
            frame => frame.Fwhm,
            filter => filter.MedianFwhm,
            detail => detail.Fwhm,
            frame => frame.Fwhm),
        new("guiding_rms", "RMS", "guiding_rms_arcsec", "ColorMetricGuiding", 2, " arcsec",
            session => session.MedianGuidingRmsArcsec,
            frame => frame.GuidingRmsArcsec,
            filter => filter.MedianGuidingRmsArcsec,
            detail => detail.GuidingRmsArcsec,
            frame => frame.GuidingRms),
        // Unit " count" rather than the dimensionless "" eccentricity carries (review ruling on
        // the axis-grouping escalation): a star count of 1,490 and an eccentricity of 0.4 are not
        // comparable magnitudes, and sharing one axis flattens the eccentricity line into the
        // floor. The leading space matches the other units, so a tick reads "1,490 count".
        //
        // SessionDetail carries no MetricRangeSummary for detected stars, so the accessor returns
        // null and the per-session chart falls back to Statistics.Median over the frames it is
        // already plotting, which is the same function Task 2 built the other four summaries with.
        new("detected_stars", "Stars", "detected_stars", "ColorMetricStars", 0, " count",
            session => session.MedianDetectedStars,
            frame => frame.DetectedStars,
            filter => filter.MedianDetectedStars,
            _ => null,
            frame => frame.DetectedStars),
    ];

    /// <summary>The metric with this <c>graph.enabled_metrics</c> key, or null for a key this
    /// build does not chart. A stored unknown key is ignored, not rendered.</summary>
    public static ChartMetric? ByKey(string key)
        => All.FirstOrDefault(metric => string.Equals(metric.Key, key, StringComparison.Ordinal));
}
