using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>One row of five metric figures under a label. The one place the five-metric row
/// format lives: Overall metrics (means), Compare nights and the per-filter table (medians) all
/// print through it.</summary>
public sealed record MetricRowViewModel(
    string Label, string HfrText, string EccentricityText, string FwhmText, string GuidingRmsText, string DetectedStarsText)
{
    public static MetricRowViewModel Of(
        string label, double? hfr, double? eccentricity, double? fwhm, double? guidingRms, double? stars) => new(
        label,
        MetricText.Cell(hfr, "0.00"),
        MetricText.Cell(eccentricity, "0.00"),
        MetricText.Cell(fwhm, "0.00"),
        MetricText.Cell(guidingRms, "0.00"),
        MetricText.Cell(stars, "N0"));
}

/// <summary>
/// The Integration tab's Overall metrics: an All frames row, then one row per filter in bar order.
/// Means over every light frame of the target (ruling R11), unlike Compare nights' medians.
/// </summary>
public sealed class OverallMetricsViewModel(TargetTotals totals, IReadOnlyList<FilterSwatchViewModel> filters)
{
    /// <summary>The label of a row over every frame, whatever its filter.</summary>
    public const string AllFramesLabel = "All frames";

    public MetricRowViewModel AllFrames { get; } = MetricRowViewModel.Of(
        AllFramesLabel, totals.AvgHfr, totals.AvgEccentricity, totals.AvgFwhm, totals.AvgGuidingRmsArcsec, totals.AvgDetectedStars);

    /// <summary>A filter with no means entry is a row of dashes.</summary>
    public IReadOnlyList<MetricRowViewModel> Filters { get; } = [.. filters.Select(filter =>
    {
        var means = totals.MeansByFilter.GetValueOrDefault(filter.FilterName);
        return MetricRowViewModel.Of(
            filter.FilterName, means?.Hfr, means?.Eccentricity, means?.Fwhm, means?.GuidingRmsArcsec, means?.DetectedStars);
    })];
}
