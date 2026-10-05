using System.Globalization;
using GalactiLog.Core.Metrics;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// The one stats card type (ruling A7), used by the Correlation tab for its two axes, by the
/// histogram and by Compare. It binds to the seam's <see cref="SummaryStats"/> plus a label and a
/// unit suffix and renders the six fields as N, Min, Max, Mean, Median and StDev in that order.
/// </summary>
/// <remarks>
/// <para>
/// The number format is <c>StatsCard.tsx</c> lines 12 to 16, ported exactly: no decimals at an
/// absolute value of 1000 and above, one decimal at 10 and above, two decimals below 10, with the
/// metric's unit suffix appended and no space before it. The threshold is on the ABSOLUTE value,
/// so -1000.4 formats as -1000. N is the integer count and takes neither the format nor the unit.
/// Invariant culture, because these are the web's own strings and not a localized figure.
/// </para>
/// <para>
/// Immutable and not an <c>ObservableObject</c>: a card is rebuilt when its tab maps a new result,
/// which is what the tab's own body raises. Absent stats hide the card rather than showing zeroes
/// (spec 12.14's Correlation subsection), which is why the constructor takes a nullable record
/// instead of the tab testing for null at each of three call sites.
/// </para>
/// </remarks>
/// <param name="stats">The summary, or null when the query could not compute one.</param>
/// <param name="label">The card's own heading, normally the axis and the metric label. Empty
/// renders no heading row.</param>
/// <param name="unit">The metric's unit suffix from <see cref="AnalysisMetricLabels"/>, appended
/// to the five formatted figures with no space of its own.</param>
public sealed class StatsCardViewModel(SummaryStats? stats, string label = "", string unit = "")
{
    /// <summary>Spec 12.14: absent stats hide the card rather than showing zeroes.</summary>
    public bool IsVisible { get; } = stats is not null;

    /// <summary>The card's heading. Empty renders no heading row, as the web's optional label
    /// does.</summary>
    public string Label { get; } = label;

    /// <summary>Whether the heading row is rendered at all.</summary>
    public bool HasLabel { get; } = !string.IsNullOrEmpty(label);

    /// <summary>The value count, the integer with no format and no unit.</summary>
    public string Count { get; } =
        stats is null ? string.Empty : stats.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>The smallest value.</summary>
    public string Min { get; } = stats is null ? string.Empty : Format(stats.Min, unit);

    /// <summary>The largest value.</summary>
    public string Max { get; } = stats is null ? string.Empty : Format(stats.Max, unit);

    /// <summary>The arithmetic mean.</summary>
    public string Mean { get; } = stats is null ? string.Empty : Format(stats.Mean, unit);

    /// <summary>The median.</summary>
    public string Median { get; } = stats is null ? string.Empty : Format(stats.Median, unit);

    /// <summary>The sample standard deviation.</summary>
    public string StdDev { get; } = stats is null ? string.Empty : Format(stats.StdDev, unit);

    /// <summary>
    /// <c>StatsCard.tsx</c> lines 12 to 16 verbatim: <c>toFixed(0)</c> at an absolute value of
    /// 1000 and above, <c>toFixed(1)</c> at 10 and above, <c>toFixed(2)</c> below 10, then the unit
    /// suffix with no separator. Public because the Compare tab prints the same figures outside a
    /// card and a second copy of the three bands would be the duplication design lesson 1 names.
    /// </summary>
    public static string Format(double value, string unit = "")
    {
        var magnitude = Math.Abs(value);
        var format = magnitude >= 1000 ? "F0" : magnitude >= 10 ? "F1" : "F2";
        return value.ToString(format, CultureInfo.InvariantCulture) + unit;
    }
}
