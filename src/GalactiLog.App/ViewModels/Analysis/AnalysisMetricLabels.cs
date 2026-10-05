using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>The three display names and the unit suffix one Analysis metric is rendered with.</summary>
/// <param name="Label">The full label, spec 12.14's metric table and <c>metricLabels.ts</c>'s
/// <c>METRIC_LABELS</c>. Picker entries, axis titles and stats card headings all use it.</param>
/// <param name="Short">The Correlation verdict sentence's name, <c>CorrelationChart.tsx</c>'s
/// <c>METRIC_SHORT</c> (lines 11 to 31).</param>
/// <param name="Matrix">The Matrix axis label, <c>MatrixTab.tsx</c>'s <c>X_LABELS</c> and
/// <c>Y_LABELS</c> (lines 9 to 19).</param>
/// <param name="Unit">The suffix appended directly after a formatted value, spec 12.14's third
/// column. Empty for a metric with no unit. The leading space in <c>" px"</c> and <c>" hPa"</c> is
/// part of the suffix and its absence in <c>"%"</c> is too.</param>
public sealed record AnalysisMetricLabel(string Label, string Short, string Matrix, string Unit);

/// <summary>
/// One entry of an Analysis metric picker: either a metric, or a non-selectable group header.
/// </summary>
/// <remarks>
/// <para>
/// The one shape all five metric pickers on the page bind, so that a picker's selection is carried
/// by the metric itself and never by a label string or a list position. The four tabs had each
/// solved this differently (a per-tab option class, a list of labels mapped back through
/// <c>Array.IndexOf</c>, and two bound as <c>SelectedIndex</c>), which is the second-occurrence
/// duplication design lesson 1 names.
/// </para>
/// <para>
/// <b>The header entry is load bearing.</b> Avalonia's <c>ComboBox</c> has no counterpart to the
/// HTML <c>optgroup</c> the web's Correlation X picker uses (<c>CorrelationTab.tsx</c> lines 166 to
/// 169), and a grouped collection view is ruled out because an option list may never be replaced
/// under a live two-way <c>SelectedItem</c> (the Phase 15B lesson: a rebuilt <c>ItemsSource</c>
/// renders the control empty). The shape is therefore one flat list with a header entry in it,
/// marked here and refused by each picker's own setter, so no unparseable key can reach a query.
/// </para>
/// </remarks>
/// <param name="Metric">The metric this entry stands for, null on a group header.</param>
/// <param name="Label">What the entry draws: the metric's full label from the table below, or the
/// group's own name.</param>
public sealed record AnalysisMetricChoice(AnalysisMetric? Metric, string Label)
{
    /// <summary>Whether the entry names a metric. False on a group header, which is what a
    /// picker's container binds <c>IsEnabled</c> to.</summary>
    public bool IsSelectable => Metric is not null;

    /// <summary>Whether the entry is a group header, which draws in the caption face.</summary>
    public bool IsHeader => Metric is null;

    /// <summary>
    /// The one selection rule every metric picker on the page follows, written once because five
    /// pickers followed it by convention and the one that also has to refuse a group header
    /// already differed in kind (design lesson 1).
    /// </summary>
    /// <remarks>
    /// A group header or a cleared selection restores: <paramref name="restore"/> raises the bound
    /// property, which puts the entry the picker had back under the selection, so nothing moves
    /// and no query fires. The metric already chosen does nothing at all. Anything else is
    /// assigned, and what an assignment costs, a stored key, a refresh or neither, is the caller's
    /// own <paramref name="assign"/>.
    /// </remarks>
    /// <param name="choice">The entry the picker was set to, null when the selection was cleared.</param>
    /// <param name="current">The metric the picker is showing now.</param>
    /// <param name="assign">Takes the chosen metric, which is the caller's own setter.</param>
    /// <param name="restore">Raises the bound property to put the previous entry back.</param>
    public static void Apply(
        AnalysisMetricChoice? choice,
        AnalysisMetric current,
        Action<AnalysisMetric> assign,
        Action restore)
    {
        if (choice?.Metric is not { } metric)
        {
            restore();
            return;
        }

        if (metric == current)
        {
            return;
        }

        assign(metric);
    }
}

/// <summary>
/// The one display table for spec 12.14's twenty-five metrics: three names and a unit suffix each.
/// Owned by the page shell because the Correlation, Distributions, Time Series, Matrix and Compare
/// tabs all read it and a second copy is the duplication design lesson 1 names.
/// </summary>
/// <remarks>
/// <para>
/// This file adds no metric list. The three picker lists are <c>AnalysisMetrics.X</c>, <c>.Y</c>
/// and <c>.Phd2X</c> from the seam, and the Distributions histogram and the Time Series picker
/// offer all twenty in the table's own order, which is <c>X</c> then <c>Y</c>.
/// </para>
/// <para>
/// Three names per metric in one table beats three tables, which is what the web has: the labels
/// live in <c>metricLabels.ts</c>, the verdict's short names in <c>CorrelationChart.tsx</c> and the
/// matrix's shorter ones in <c>MatrixTab.tsx</c>, and a metric added to one of the three there is
/// silently missing from the other two.
/// </para>
/// <para>
/// <b>Glyphs.</b> A glyph ships only where the application's own embedded faces draw
/// it. The cmaps of all six files under <c>Assets/Fonts</c> (Atkinson Hyperlegible Next Regular,
/// Medium, SemiBold and Bold, Atkinson Hyperlegible Mono Regular and Medium) were read with
/// <c>fontTools.ttLib</c>: U+00B0 DEGREE SIGN and U+03BC GREEK SMALL LETTER MU are in all six, and
/// <b>U+03C3 GREEK SMALL LETTER SIGMA and U+2033 DOUBLE PRIME are in none of them</b>. The degree
/// sign and the mu therefore ship as the web writes them; the sigma and the arcsecond double prime
/// are replaced by spec 12.14's own ASCII words for those labels, <c>ADU sigma</c> and
/// <c>arcsec</c>. The arcsecond word is also how every other arcsecond figure in this application
/// is rendered, which <c>EquipmentInventoryViewModel</c>'s own comment records as settled in Phase
/// 6.
/// </para>
/// <para>
/// The five PHD2 metrics have no row in either of the web's two short tables, so the web's own
/// lookups fall through to the raw key and its Correlation verdict prints
/// <c>phd2_rms_total</c> in a sentence. The port takes the full label for both short names
/// instead. The Matrix name is never drawn for these five, because the grid is ten by ten and no
/// PHD2 metric is in <c>AnalysisMetrics.X</c>.
/// </para>
/// </remarks>
public static class AnalysisMetricLabels
{
    // Spec 12.14's unit column, and the word the label's parenthetical carries. The web writes
    // U+2033 in both places; no embedded face draws it (the cmap reading above).
    private const string ArcsecWord = "arcsec";
    private const string ArcsecUnit = " arcsec";

    // U+00B0 DEGREE SIGN followed by C, as metricLabels.ts writes it. In all six faces.
    private const string DegreesC = "\u00b0C";

    // U+03BC GREEK SMALL LETTER MU, MatrixTab.tsx line 18. In all six faces, so it ships.
    private const string MatrixAduMean = "ADU \u03bc";

    // MatrixTab.tsx line 18 writes U+03C3 GREEK SMALL LETTER SIGMA here. No embedded face draws
    // it, so spec 12.14's Matrix transcription word ships instead.
    private const string MatrixAduStdev = "ADU sigma";

    private static readonly IReadOnlyDictionary<AnalysisMetric, AnalysisMetricLabel> Table =
        new Dictionary<AnalysisMetric, AnalysisMetricLabel>
        {
            // The ten X metrics, spec 12.14's metric table rows 1 to 10.
            [AnalysisMetric.Humidity] = new("Humidity (%)", "humidity", "Humid.", "%"),
            [AnalysisMetric.WindSpeed] = new("Wind Speed", "wind", "Wind", ""),
            [AnalysisMetric.AmbientTemp] = new($"Ambient Temp ({DegreesC})", "temperature", "Temp", DegreesC),
            [AnalysisMetric.DewPoint] = new($"Dew Point ({DegreesC})", "dew point", "Dew Pt", DegreesC),
            [AnalysisMetric.Pressure] = new("Pressure (hPa)", "pressure", "Press.", " hPa"),
            [AnalysisMetric.CloudCover] = new("Cloud Cover (%)", "cloud cover", "Cloud", "%"),
            [AnalysisMetric.SkyQuality] = new("Sky Quality (SQM)", "sky quality", "SQM", ""),
            [AnalysisMetric.FocuserTemp] = new($"Focuser Temp ({DegreesC})", "focuser temp", "Focus T", DegreesC),
            [AnalysisMetric.Airmass] = new("Airmass", "airmass", "Airm.", ""),
            [AnalysisMetric.SensorTemp] = new($"Sensor Temp ({DegreesC})", "sensor temp", "Sensor T", DegreesC),

            // The ten Y metrics, spec 12.14's metric table rows 11 to 20.
            [AnalysisMetric.Hfr] = new("HFR (px)", "HFR", "HFR", " px"),
            [AnalysisMetric.Fwhm] = new(
                $"FWHM ({ArcsecWord})", $"FWHM ({ArcsecWord})", "FWHM", ArcsecUnit),
            [AnalysisMetric.Eccentricity] = new("Eccentricity", "eccentricity", "Ecc.", ""),
            [AnalysisMetric.GuidingRms] = new($"Guiding RMS ({ArcsecWord})", "guiding RMS", "Guide", ArcsecUnit),
            [AnalysisMetric.GuidingRmsRa] = new($"Guiding RA RMS ({ArcsecWord})", "RA guiding", "Guide RA", ArcsecUnit),
            [AnalysisMetric.GuidingRmsDec] = new($"Guiding DEC RMS ({ArcsecWord})", "DEC guiding", "Guide DEC", ArcsecUnit),
            [AnalysisMetric.DetectedStars] = new("Detected Stars", "star count", "Stars", ""),
            [AnalysisMetric.AduMean] = new("ADU Mean", "ADU mean", MatrixAduMean, ""),
            [AnalysisMetric.AduMedian] = new("ADU Median", "ADU median", "ADU med", ""),
            [AnalysisMetric.AduStdev] = new("ADU StDev", "ADU noise", MatrixAduStdev, ""),

            // The five PHD2 night metrics, spec 12.14's second table. Both short names are the
            // label: neither of the web's short tables carries a row for them.
            [AnalysisMetric.Phd2RmsTotal] = Phd2($"PHD2 RMS Total ({ArcsecWord})", ArcsecUnit),
            [AnalysisMetric.Phd2RmsRa] = Phd2($"PHD2 RMS RA ({ArcsecWord})", ArcsecUnit),
            [AnalysisMetric.Phd2RmsDec] = Phd2($"PHD2 RMS Dec ({ArcsecWord})", ArcsecUnit),
            [AnalysisMetric.Phd2StarLostPct] = Phd2("PHD2 Star Lost (%)", "%"),
            [AnalysisMetric.Phd2SnrMean] = Phd2("PHD2 Guide SNR", ""),
        };

    /// <summary>The three names and the unit suffix for one metric.</summary>
    /// <exception cref="KeyNotFoundException">A member of <see cref="AnalysisMetric"/> with no row,
    /// which the table's own case fails by name before it can reach a reader.</exception>
    public static AnalysisMetricLabel For(AnalysisMetric metric) => Table[metric];

    /// <summary>The full label, for a picker entry, an axis title or a card heading.</summary>
    public static string Label(AnalysisMetric metric) => Table[metric].Label;

    /// <summary>The unit suffix, appended directly after a formatted value with no separator.</summary>
    public static string Unit(AnalysisMetric metric) => Table[metric].Unit;

    /// <summary>
    /// One picker entry per metric, in the order given, each labelled from the table above and from
    /// nowhere else. The answer is a fixed list, which is what a picker holds for its whole life: a
    /// bound option list replaced under a two-way selection drops the selection (Phase 15B).
    /// </summary>
    /// <param name="metrics">The picker's own metric list, normally one of the seam's
    /// <c>AnalysisMetrics</c> lists or a concatenation of two of them.</param>
    public static IReadOnlyList<AnalysisMetricChoice> Choices(IEnumerable<AnalysisMetric> metrics)
        => [.. metrics.Select(metric => new AnalysisMetricChoice(metric, Label(metric)))];

    /// <summary>A non-selectable group header entry, for a picker that offers its metrics in more
    /// than one group. The only one on the page today is Correlation's "Guiding (PHD2)".</summary>
    public static AnalysisMetricChoice Header(string label) => new(null, label);

    private static AnalysisMetricLabel Phd2(string label, string unit) => new(label, label, label, unit);
}
