using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Stats;

/// <summary>One graded metric cell: its text, its band colour and its hover explanation.</summary>
public sealed record GradedCell(string Text, QualityBand Band, IImmutableSolidColorBrush Brush, string Tooltip);

/// <summary>One canonical filter inside an expanded equipment row.</summary>
public sealed record EquipmentFilterRow(
    string FilterName,
    string FrameCount,
    string Integration,
    string MedianHfr,
    string BestHfr,
    string MedianEccentricity,
    string MedianFwhm);

/// <summary>
/// One (telescope, camera) combination, expandable to its per-filter breakdown.
/// </summary>
/// <remarks>
/// Only <see cref="MedianEccentricity"/> and <see cref="MedianFwhm"/> are graded.
/// <see cref="MedianHfr"/> and <see cref="BestHfr"/> are deliberately never graded, because HFR is
/// in pixels and pixels are only comparable inside one optical train; the web source carries the
/// same rule and the same tooltip, which is ported verbatim onto those two cells.
/// </remarks>
public sealed partial class EquipmentComboRowViewModel : ObservableObject
{
    /// <summary>The web's hover text on the two pixel-HFR cells and on their column headers.
    /// </summary>
    public const string HfrNotGradedTooltip =
        "Pixel HFR; only comparable within this optical train, not graded across rigs";

    /// <summary>The web's hover text on the grouped marker.</summary>
    public const string GroupedTooltip =
        "Grouped: multiple equipment aliases are combined under this name";

    internal EquipmentComboRowViewModel(
        EquipmentComboMetrics combo,
        MetricBaseline eccentricityBaseline,
        MetricBaseline fwhmBaseline,
        BandBrushes brushes)
    {
        Name = $"{combo.Telescope} + {combo.Camera}";
        Grouped = combo.Grouped;
        FrameCount = MetricText.Count(combo.FrameCount);
        AvgSession = combo.AvgSessionSeconds is { } average
            ? MetricText.Integration(average)
            : MetricText.Missing;
        Integration = MetricText.Integration(combo.IntegrationSeconds);
        MedianHfr = Metric(combo.MedianHfr);
        BestHfr = Metric(combo.BestHfr);
        MedianEccentricity = MetricGrading.Grade(combo.MedianEccentricity, eccentricityBaseline, "Ecc", brushes);
        MedianFwhm = MetricGrading.Grade(combo.MedianFwhm, fwhmBaseline, "FWHM", brushes);
        FwhmFrameCount = combo.FwhmFrameCount > 0
            ? $"n={MetricText.Count(combo.FwhmFrameCount)}"
            : "";
        Filters = string.Join(", ", combo.FilterBreakdown.Select(filter => filter.FilterName));
        FilterRows =
        [
            .. combo.FilterBreakdown.Select(filter => new EquipmentFilterRow(
                filter.FilterName,
                MetricText.Count(filter.FrameCount),
                MetricText.Integration(filter.IntegrationSeconds),
                Metric(filter.MedianHfr),
                Metric(filter.BestHfr),
                Metric(filter.MedianEccentricity),
                Metric(filter.MedianFwhm))),
        ];
    }

    /// <summary><c>"{telescope} + {camera}"</c>, the web's own row label.</summary>
    public string Name { get; }

    /// <summary>True when more than one raw name folded onto this canonical pair. The view shows
    /// a marker carrying <see cref="GroupedTooltip"/>.</summary>
    public bool Grouped { get; }

    public string FrameCount { get; }

    public string AvgSession { get; }

    public string Integration { get; }

    /// <summary>Pixel HFR, never graded. See the type's remarks.</summary>
    public string MedianHfr { get; }

    /// <summary>Pixel HFR, never graded. See the type's remarks.</summary>
    public string BestHfr { get; }

    public GradedCell MedianEccentricity { get; }

    public GradedCell MedianFwhm { get; }

    /// <summary>How many frames carried an FWHM, rendered beside the median as <c>n=NNN</c>.
    /// Empty when none did.</summary>
    public string FwhmFrameCount { get; }

    /// <summary>The canonical filters of this combination, comma separated.</summary>
    public string Filters { get; }

    public IReadOnlyList<EquipmentFilterRow> FilterRows { get; }

    /// <summary>Whether the per-filter breakdown is showing. Session state: the web expands on a
    /// row click and persists nothing.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary><c>formatMetric</c>: two decimals, or the missing placeholder.</summary>
    internal static string Metric(double? value) => value is { } present && double.IsFinite(present)
        ? MetricText.Format(present, "0.00")
        : MetricText.Missing;

}

/// <summary>
/// The one graded-cell builder of the Statistics page: a figure, the band its deviation from a
/// baseline falls in, that band's brush and the hover text.
/// </summary>
/// <remarks>
/// Lifted out of <see cref="EquipmentComboRowViewModel"/>'s private static at the second
/// occurrence, which is spec 12.5's Guiding scorecard grading three columns against the cross-rig
/// baselines the same way Equipment performance grades two against the catalogue (design lesson 1).
/// The body is unchanged by the move, the word <c>sigma</c> in its tooltip included: that literal
/// is pinned by <c>StatisticsViewModelTests</c> and correcting the grading vocabulary is a change
/// to three surfaces at once, not a side effect of an extraction.
/// </remarks>
internal static class MetricGrading
{
    /// <summary>One graded cell. A null <paramref name="value"/> is neutral with no hover text at
    /// all, never a neutral cell whose tooltip reads NaN.</summary>
    /// <param name="baseline">The sample this value is compared against.
    /// <see cref="FrameQuality.MadZ"/> answers null, and so the cell is neutral, whenever that
    /// sample holds fewer than <see cref="FrameQuality.MinGroup"/> values or has a zero
    /// deviation.</param>
    /// <param name="label">The metric's short name, which opens the hover text.</param>
    public static GradedCell Grade(double? value, MetricBaseline baseline, string label, BandBrushes brushes)
    {
        var text = EquipmentComboRowViewModel.Metric(value);
        if (value is null)
        {
            return new GradedCell(text, QualityBand.Neutral, brushes.Neutral, "");
        }

        // Every graded metric here is higher-is-worse, so the sign is not flipped.
        var z = FrameQuality.MadZ(value, baseline);
        var band = FrameQuality.BandForZ(z);
        // The web writes the sigma glyph here; the word is used instead, for the same reason
        // MetricText.Missing is a hyphen.
        var tooltip = z is { } score
            ? string.Create(CultureInfo.InvariantCulture, $"{label} {score:0.0} sigma vs catalog median")
            : "";

        return new GradedCell(text, band, brushes.For(band), tooltip);
    }
}

/// <summary>
/// The four band colours and the missing-value ink, resolved once from the theme rather than per
/// cell.
/// </summary>
/// <remarks>
/// Spec 14.5: a view-model that holds a brush holds an <see cref="ImmutableSolidColorBrush"/>.
/// Resolved on the thread that constructs this type, which is the UI thread (the page builds it
/// inside its post callback), because the resolution reads
/// <c>Application.Current</c>'s merged dictionary through <c>ChartTheme.Read</c>.
/// </remarks>
internal sealed class BandBrushes
{
    /// <summary>The four spec 14.1 keys this type resolves, in band order. Named so a test can
    /// assert against the dictionary rather than against a hard-coded hex string.</summary>
    internal static readonly string[] TokenKeys = ["ColorTextPrimary", "ColorSuccess", "ColorWarning", "ColorError"];

    /// <summary>The faint ink of a missing value (spec.md item 6). A graded cell binds its brush
    /// locally, which outranks the table's faint-dash style, so an absent figure takes this instead.
    /// Not one of <see cref="TokenKeys"/>, which are the bands in order.</summary>
    internal const string MissingKey = "ColorTextTertiary";

    public BandBrushes()
    {
        // Review finding I1: the first implementation named the ColorSuccessValue, ColorWarningValue
        // and ColorErrorValue keys, which DeepSky.axaml declares as Color rather than
        // SolidColorBrush, so all three resolved to ChartTheme's neutral grey and the whole grading
        // palette rendered in one colour. Both halves are fixed: these are the brush keys, and
        // ChartTheme.Read now accepts either declaration form so the same silence cannot recur.
        Neutral = Resolve(TokenKeys[0]);
        Better = Resolve(TokenKeys[1]);
        Watch = Resolve(TokenKeys[2]);
        Reject = Resolve(TokenKeys[3]);
        Missing = Resolve(MissingKey);
    }

    public IImmutableSolidColorBrush Neutral { get; }

    public IImmutableSolidColorBrush Better { get; }

    public IImmutableSolidColorBrush Watch { get; }

    public IImmutableSolidColorBrush Reject { get; }

    public IImmutableSolidColorBrush Missing { get; }

    public IImmutableSolidColorBrush For(QualityBand band) => band switch
    {
        QualityBand.Better => Better,
        QualityBand.Watch => Watch,
        QualityBand.Reject => Reject,
        _ => Neutral,
    };

    // ChartTheme.Read is the application's one token-to-colour reader, with the documented
    // neutral-grey fallback for a key that is missing from the dictionary. Going through it rather
    // than through TryFindResource here keeps that fallback in one place.
    private static IImmutableSolidColorBrush Resolve(string key)
    {
        var colour = ChartTheme.Read(key, ChartTheme.Fallback);
        return new ImmutableSolidColorBrush(
            Color.FromArgb(colour.Alpha, colour.Red, colour.Green, colour.Blue));
    }
}

/// <summary>
/// Spec 12.5's Equipment performance row: one row per canonical (telescope, camera) pair, each
/// expandable to its per-filter breakdown, with eccentricity and FWHM graded against the
/// catalogue.
/// </summary>
/// <remarks>
/// The baselines are the median and MAD of the <em>per-combination medians</em>, which is
/// <c>EquipmentPerformance.tsx::buildComboBaselines</c>, not a pass over frames.
/// <see cref="FrameQuality.MadZ"/> then applies the same <c>N &lt; 8</c> and <c>MAD == 0</c> gates
/// every other grading surface uses, so a library with seven rigs grades nothing at all rather
/// than grading each rig against six others.
/// </remarks>
public sealed class EquipmentPerformanceViewModel
{
    /// <summary>The empty shape, so the page has something to bind before its first load.
    /// </summary>
    public EquipmentPerformanceViewModel()
        : this([], new BandBrushes())
    {
    }

    internal EquipmentPerformanceViewModel(IReadOnlyList<EquipmentComboMetrics> combos, BandBrushes brushes)
    {
        EccentricityBaseline = MetricBaseline.Of(combos.Select(combo => combo.MedianEccentricity));
        FwhmBaseline = MetricBaseline.Of(combos.Select(combo => combo.MedianFwhm));
        Rows =
        [
            .. combos.Select(combo => new EquipmentComboRowViewModel(
                combo, EccentricityBaseline, FwhmBaseline, brushes)),
        ];
    }

    /// <summary>Server order, preserved: the query already ordered these and a second sort here
    /// would be a second answer to the same question.</summary>
    public IReadOnlyList<EquipmentComboRowViewModel> Rows { get; }

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Spec 12.10's shape for a section with nothing in it. The web's own wording.
    /// </summary>
    public string EmptyMessage => "No equipment data available";

    internal MetricBaseline EccentricityBaseline { get; }

    internal MetricBaseline FwhmBaseline { get; }
}
