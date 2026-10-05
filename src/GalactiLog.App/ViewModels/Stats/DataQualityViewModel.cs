using System.Globalization;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Stats;

/// <summary>One labelled figure of spec 12.5's Data quality row.</summary>
public sealed record DataQualityFigure(string Label, string Value, string Tooltip = "");

/// <summary>
/// Spec 12.5's Data quality row: the average and best HFR in both domains, the average
/// eccentricity, the count of frames with no derivable plate scale, and the eccentricity exclusion
/// count. The two histograms beside it are <see cref="StatsBarChartViewModel"/> instances owned by
/// the page, because they carry chart state and this is a projection.
/// </summary>
/// <remarks>
/// The web has no data quality section at all; spec 12.5's table lists one, so the port adds it
/// from <c>schemas/stats.py::DataQualityStats</c>, which is what that table cites as its source.
/// </remarks>
public sealed class DataQualityViewModel
{
    /// <summary>The empty shape, so the page has something to bind before its first load.
    /// </summary>
    public DataQualityViewModel()
        : this(new DataQualityStats(null, null, null, null, null, 0, null, null, [], []))
    {
    }

    public DataQualityViewModel(DataQualityStats quality)
    {
        Figures =
        [
            // Review finding M3: the unit is in the label, not only inside the value. A WrapPanel
            // reflows by width, so two figures called "Avg HFR" are not reliably adjacent and the
            // reader has no other way to tell them apart. The qualifiers match the equipment
            // table's own headers.
            new DataQualityFigure("Avg HFR (px)", Pixels(quality.AvgHfr)),
            new DataQualityFigure("Avg HFR (arcsec)", Arcsec(quality.AvgHfrArcsec)),
            new DataQualityFigure("Best HFR (px)", Pixels(quality.BestHfr)),
            new DataQualityFigure("Best HFR (arcsec)", Arcsec(quality.BestHfrArcsec)),
            new DataQualityFigure("Avg Eccentricity", EquipmentComboRowViewModel.Metric(quality.AvgEccentricity)),
            new DataQualityFigure(
                "No plate scale",
                MetricText.Count(quality.UnscaledFrameCount),
                "Frames carrying an HFR with no derivable arcseconds per pixel. The arcsecond "
                    + "figures above skip exactly these."),
            new DataQualityFigure(
                "Eccentricity excluded",
                quality.EccentricityExcludedCount is { } excluded
                    ? MetricText.Count(excluded)
                    : MetricText.Missing,
                quality.EccentricitySource is { } source
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"Frames whose eccentricity came from a source other than {source}, which is the one pooled (spec 7.2).")
                    : "No frame carries an eccentricity, so there is nothing to pool."),
        ];

        EccentricitySource = quality.EccentricitySource ?? MetricText.Missing;
        IsEmpty = quality.HfrPixelHistogram.Count == 0 && quality.HfrArcsecHistogram.Count == 0;
    }

    /// <summary>The seven figures, in the order spec 12.5's table names them.</summary>
    public IReadOnlyList<DataQualityFigure> Figures { get; }

    /// <summary>Spec 7.2's pooled eccentricity source, shown so the exclusion count above it means
    /// something.</summary>
    public string EccentricitySource { get; }

    public bool IsEmpty { get; }

    public string EmptyMessage => "No frame metrics recorded.";

    private static string Pixels(double? value)
        => value is null ? MetricText.Missing : MetricText.Format(value, "0.00", " px");

    private static string Arcsec(double? value)
        => value is null ? MetricText.Missing : MetricText.Format(value, "0.00", " arcsec");
}
