using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// The read and write seam over spec 5.8.2's <c>display.analysis</c> object (spec 12.14's
/// Persistence subsection, ruling A9): four tolerant reads and four writers, one per key.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.8.2 stores the four keys as plain strings and says "a stored value outside a key's
/// listed set reads as that key's default rather than throwing", which is a parse at the seam that
/// reads the key rather than a converter on the record. <c>TargetPageState.ParseBaseline</c>
/// is the shipped precedent and this type is the same shape, gathered into one static class
/// because the Analysis page has four such keys rather than one and a fifth reader would otherwise
/// copy the rule (design lesson 2: one choke point, not a convention).
/// </para>
/// <para>
/// Every writer is a nested <c>with</c> expression applied to the document as loaded INSIDE the
/// queued write, never to a snapshot taken at construction, which is what keeps one of these keys
/// from clobbering another, <c>target_page</c> or <c>columns</c> (ruling Q17). The delegate is
/// <c>DisplayColumnWriter.Write</c>, the one serialized chain over the display document.
/// </para>
/// <para>
/// The two metric reads take an already parsed <see cref="AnalysisMetric"/> rather than the stored
/// string, because the string to enum step is <c>AnalysisMetrics.Parse</c> and the two metric
/// writers take the stored key rather than the enum, because the enum to string step is
/// <c>AnalysisMetrics.Key</c>. Both members belong to <c>GalactiLog.Data.Queries</c> and a second
/// copy of either table here would be the duplication design lesson 1 names; the caller composes
/// the two halves.
/// </para>
/// </remarks>
public static class AnalysisDisplay
{
    /// <summary>Spec 12.14's first tab, and the default for <c>display.analysis.tab</c>.</summary>
    public const string CorrelationTab = "correlation";

    /// <summary>Spec 12.14's second tab.</summary>
    public const string DistributionsTab = "distributions";

    /// <summary>Spec 12.14's third tab.</summary>
    public const string TimeSeriesTab = "timeseries";

    /// <summary>Spec 12.14's fourth tab.</summary>
    public const string MatrixTab = "matrix";

    /// <summary>Spec 12.14's fifth tab.</summary>
    public const string CompareTab = "compare";

    /// <summary>
    /// The five tab keys in spec 12.14's strip order, spelled as <c>AnalysisPage.tsx</c>'s own
    /// <c>TABS</c> ids (lines 16 to 21). The stored spelling of each is also the suffix of its
    /// help topic id (<c>analysis.correlation</c> and its four siblings).
    /// </summary>
    /// <remarks>
    /// This is a closed set of five and is NOT <c>AnalysisCacheKey.Tab</c>'s query vocabulary,
    /// which splits the Distributions tab into <c>distribution</c> and <c>boxplot</c> and carries
    /// a <c>filters</c> entry that is no tab at all (<c>core-shapes.md</c> section 5.5). The two
    /// stay separate so that a query word cannot be parsed as a tab: <see cref="ParseTab"/> reads
    /// <c>boxplot</c> as Correlation, and its own case says so.
    /// </remarks>
    public static readonly IReadOnlyList<string> TabKeys =
        [CorrelationTab, DistributionsTab, TimeSeriesTab, MatrixTab, CompareTab];

    /// <summary>The stored tab matched against <see cref="TabKeys"/> ordinally. Anything else,
    /// junk and a null included, selects Correlation (spec 5.8.2, ruling A9).</summary>
    public static string ParseTab(string? stored)
    {
        foreach (var key in TabKeys)
        {
            if (string.Equals(key, stored, StringComparison.Ordinal))
            {
                return key;
            }
        }

        return CorrelationTab;
    }

    /// <summary>The stored X metric, already parsed through <c>AnalysisMetrics.Parse</c>. Null, or
    /// a metric that is in neither <c>AnalysisMetrics.X</c> nor <c>AnalysisMetrics.Phd2X</c>, reads
    /// as <see cref="AnalysisMetric.Humidity"/>.</summary>
    public static AnalysisMetric XMetric(string? stored) => XMetric(AnalysisMetrics.Parse(stored));

    /// <inheritdoc cref="XMetric(string)"/>
    public static AnalysisMetric XMetric(AnalysisMetric? parsed)
        => parsed is { } metric
            && (AnalysisMetrics.X.Contains(metric) || AnalysisMetrics.Phd2X.Contains(metric))
            ? metric
            : AnalysisMetric.Humidity;

    /// <summary>The stored Y metric, already parsed through <c>AnalysisMetrics.Parse</c>. Null, or
    /// a metric that is not in <c>AnalysisMetrics.Y</c>, reads as <see cref="AnalysisMetric.Hfr"/>.
    /// Spec 5.8.2 names this one: a PHD2 key stored in <c>y_metric</c> is a value the Correlation
    /// tab would reject at query time, and reading it as <c>hfr</c> is what keeps a hand-edited
    /// document from opening a page that cannot draw.</summary>
    public static AnalysisMetric YMetric(string? stored) => YMetric(AnalysisMetrics.Parse(stored));

    /// <inheritdoc cref="YMetric(string)"/>
    public static AnalysisMetric YMetric(AnalysisMetric? parsed)
        => parsed is { } metric && AnalysisMetrics.Y.Contains(metric)
            ? metric
            : AnalysisMetric.Hfr;

    /// <summary>The stored granularity. Only <c>session</c>, ordinal and case insensitive, is
    /// <see cref="AnalysisGranularity.Session"/>; everything else, junk included, is the
    /// default.</summary>
    public static AnalysisGranularity ParseGranularity(string? stored)
        => string.Equals(stored, "session", StringComparison.OrdinalIgnoreCase)
            ? AnalysisGranularity.Session
            : AnalysisGranularity.Frame;

    /// <summary>The stored form of <paramref name="granularity"/>, the lower-case literal spec
    /// 5.8.2 lists.</summary>
    public static string GranularityToStored(AnalysisGranularity granularity)
        => granularity == AnalysisGranularity.Session ? "session" : "frame";

    /// <summary>Writes <c>display.analysis.tab</c> and nothing else.</summary>
    public static void WriteTab(Action<Func<DisplaySettings, DisplaySettings>>? write, string tab)
        => write?.Invoke(document => document with
        {
            Analysis = document.Analysis with { Tab = tab },
        });

    /// <summary>Writes <c>display.analysis.x_metric</c> and nothing else. The key is
    /// <c>AnalysisMetrics.Key</c>'s answer for the chosen metric.</summary>
    public static void WriteXMetric(Action<Func<DisplaySettings, DisplaySettings>>? write, string metricKey)
        => write?.Invoke(document => document with
        {
            Analysis = document.Analysis with { XMetric = metricKey },
        });

    /// <inheritdoc cref="WriteXMetric(Action{Func{DisplaySettings, DisplaySettings}}, string)"/>
    public static void WriteXMetric(Action<Func<DisplaySettings, DisplaySettings>>? write, AnalysisMetric metric)
        => WriteXMetric(write, AnalysisMetrics.Key(metric));

    /// <summary>Writes <c>display.analysis.y_metric</c> and nothing else.</summary>
    public static void WriteYMetric(Action<Func<DisplaySettings, DisplaySettings>>? write, string metricKey)
        => write?.Invoke(document => document with
        {
            Analysis = document.Analysis with { YMetric = metricKey },
        });

    /// <inheritdoc cref="WriteYMetric(Action{Func{DisplaySettings, DisplaySettings}}, string)"/>
    public static void WriteYMetric(Action<Func<DisplaySettings, DisplaySettings>>? write, AnalysisMetric metric)
        => WriteYMetric(write, AnalysisMetrics.Key(metric));

    /// <summary>Writes <c>display.analysis.granularity</c> and nothing else.</summary>
    public static void WriteGranularity(
        Action<Func<DisplaySettings, DisplaySettings>>? write, AnalysisGranularity granularity)
        => write?.Invoke(document => document with
        {
            Analysis = document.Analysis with { Granularity = GranularityToStored(granularity) },
        });
}
