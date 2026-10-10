using System.Globalization;
using Avalonia.Media.Immutable;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// One run of the comp's log line: a figure or the words that connect two figures. The line is
/// built as runs rather than as one string so the figures can take the <c>figure</c> class
/// (weight 600, tabular, primary ink) while the connecting words stay secondary, which is what
/// the comp shows, and so the wording still lives in one place instead of as twelve literals in
/// markup.
/// </summary>
/// <param name="Text">The run's text, including any punctuation that belongs to it.</param>
/// <param name="IsFigure">True for a measured figure, false for the connecting words.</param>
public sealed record LogLineRunViewModel(string Text, bool IsFigure);

/// <summary>
/// One bar of the header's per-filter integration line: the filter's name, its swatch tint, its hours, and its length as a fraction of the longest filter's hours.
/// </summary>
/// <param name="Fraction">0 to 1 of the largest filter's hours; the largest is 1, all zero is 0.</param>
public sealed record IntegrationBarViewModel(string FilterName, ImmutableSolidColorBrush Brush, double Hours, double Fraction)
{
    /// <summary>One segment per night in ledger order, each a fraction of the longest filter's hours.</summary>
    public IReadOnlyList<IntegrationSegmentViewModel> Segments { get; init; } = [];

    /// <summary>The goal as a fraction of the longest filter's hours, null when the filter has none.</summary>
    public double? GoalFraction { get; init; }

    public double ShortByHours { get; init; }

    /// <summary>"short by 1.2 h", empty under 0.05 h.</summary>
    public string ShortByText => ShortByHours >= 0.05d
        ? string.Create(CultureInfo.InvariantCulture, $"short by {ShortByHours:0.0} h")
        : "";

    /// <summary>The longest bar's width in pixels, the view's bar column.</summary>
    public const double FullWidth = 160d;

    public string HoursText => MetricText.Format(Hours, "0.0") + " h";

    public double BarWidth => Fraction * FullWidth;
}

/// <summary>One night's share of a filter's bar.</summary>
public sealed record IntegrationSegmentViewModel(DateOnly Night, double Hours, double Fraction, ImmutableSolidColorBrush Brush)
{
    public string Tip => string.Create(CultureInfo.InvariantCulture, $"{MetricText.Date(Night)}: {Hours:0.0} h");
}

/// <summary>
/// Spec 12.4's totals row as a display projection of <see cref="TargetTotals"/>. Formatting only,
/// like <see cref="TargetHeaderViewModel"/>: every figure is a string, and a string the view
/// hides when it is empty has a <c>Has*</c> companion, so the view binds <c>IsVisible</c> and
/// holds no conversion logic.
/// </summary>
/// <remarks>
/// <para>
/// These are means over every LIGHT frame of the group. The session cards show medians of the
/// same metrics, and the two are deliberately different figures (Task 1 handoff).
/// </para>
/// <para>
/// P12 moved the collapsed field set off this type. The log line reads <c>LogLineRuns</c>, and
/// the drawer reads the arcsecond HFR and its disclosure. The unit-carrying twins the retired card bound, the five
/// <c>Avg*Text</c> and their <c>Has*</c> companions, the eccentricity disclosure, the joined log
/// line and the joined filter names, were bound by no view after that and are gone with their
/// cases (phase review P2-5, coordinator ruling (3)). The round 2 sweep took the six that survived
/// the first list, <c>IntegrationText</c>, <c>SessionCountText</c>, <c>FirstSessionText</c>,
/// <c>LastSessionText</c>, <c>FiltersText</c> and <c>EquipmentText</c>, with
/// <c>HasFirstSession</c> and <c>HasLastSession</c>: the log line is the only reader the five
/// figures had left, so they are constructor locals now. Nothing here is kept as a read surface.
/// The table overhaul removed the Nights list's totals row, and its seven <c>Ledger*Text</c>
/// figures and <c>FrameCountText</c> went with it (ruling R13); the Integration tab's Overall
/// metrics reads the means from <see cref="Totals"/>.
/// </para>
/// </remarks>
public sealed class TargetTotalsViewModel
{
    /// <param name="totals">The loaded read model.</param>
    /// <param name="filterTint">Resolves a canonical filter name to its configured colour,
    /// normally <c>ChartSelectionViewModel.FilterTint</c>, which is the same resolution the chart
    /// pills use. Optional and trailing, so no existing construction site changes; null falls back
    /// to spec 5.8.4's grey, which since P13 R2a is what <c>FilterColor.Resolve</c> reaches only
    /// after the stored colour and the seeded palette have both missed, never to a theme token (a
    /// token belongs in XAML, not in a view model).</param>
    /// <param name="aliases">Normally <c>AliasMapCache.Current</c>, for the swatch order's alias
    /// fallback (polish wave 2 ruling 1). Null folds the canonical names alone.</param>
    public TargetTotalsViewModel(
        TargetTotals totals,
        Func<string, ImmutableSolidColorBrush>? filterTint = null,
        Func<AliasMap>? aliases = null,
        IReadOnlyList<NightFilterOverview>? nightFilters = null)
    {
        Totals = totals;

        // Locals rather than properties: the log line is the only reader left, so they are the
        // words it is built from and not a surface (phase review escalation 4, coordinator round 2).
        var integrationText = MetricText.Hours(totals.IntegrationSeconds);
        var frameCountText = MetricText.Count(totals.FrameCount);
        var sessionCountText = MetricText.Count(totals.SessionCount);
        var firstSessionText = FormatDate(totals.FirstSessionDate);
        var lastSessionText = FormatDate(totals.LastSessionDate);

        AvgHfrArcsecText = MetricText.Format(totals.AvgHfrArcsec, "0.00", " arcsec");
        HfrArcsecDisclosure = totals.HfrArcsecExcludedCount > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{totals.HfrArcsecExcludedCount} frames without a plate scale")
            : "";
        var equipmentText = string.Join(", ", totals.Equipment);

        var tint = filterTint ?? (_ => ChartSelectionViewModel.FallbackFilterTint);
        FilterSwatches = [.. totals.FiltersUsed.Order(FilterOrder.Comparer(aliases?.Invoke())).Select(name => new FilterSwatchViewModel(name, tint(name)))];

        var hoursByFilter = FilterSwatches
            .Select(swatch => (Swatch: swatch, Hours: totals.IntegrationSecondsByFilter.GetValueOrDefault(swatch.FilterName) / 3600d))
            .ToList();
        var longest = hoursByFilter.Count == 0 ? 0d : hoursByFilter.Max(entry => entry.Hours);
        var goals = IntegrationGoal.For(totals.IntegrationSecondsByFilter);
        var nights = (nightFilters ?? []).OrderByDescending(row => row.SessionDate).ToList();
        IntegrationBars = [.. hoursByFilter.Select(entry =>
        {
            var goal = goals.GetValueOrDefault(entry.Swatch.FilterName);
            return new IntegrationBarViewModel(
                entry.Swatch.FilterName,
                entry.Swatch.Brush,
                entry.Hours,
                longest > 0d ? entry.Hours / longest : 0d)
            {
                // Segments come from the dated rows, so frames with no date sum under the bar.
                Segments = [.. nights
                    .Where(row => string.Equals(row.Filter, entry.Swatch.FilterName, StringComparison.OrdinalIgnoreCase))
                    .Select(row => new IntegrationSegmentViewModel(
                        row.SessionDate,
                        row.IntegrationSeconds / 3600d,
                        longest > 0d ? row.IntegrationSeconds / 3600d / longest : 0d,
                        entry.Swatch.Brush))],
                GoalFraction = goal.GoalSeconds is { } goalSeconds && longest > 0d ? goalSeconds / 3600d / longest : null,
                ShortByHours = (goal.ShortBySeconds ?? 0d) / 3600d,
            };
        })];

        LogLineRuns = BuildLogLine(totals, integrationText, sessionCountText, frameCountText, firstSessionText, lastSessionText);
        LogLineEquipmentText = equipmentText.Length > 0
            ? string.Create(CultureInfo.InvariantCulture, $"on {equipmentText}.")
            : "";
        LogLineDisclosureText = BuildDisclosure(totals);
    }

    /// <summary>The read model behind the row.</summary>
    public TargetTotals Totals { get; }

    public string AvgHfrArcsecText { get; }

    public bool HasAvgHfrArcsec => AvgHfrArcsecText.Length > 0;

    /// <summary>Spec 12.4's "the count of frames excluded for lack of one is shown". Empty when
    /// nothing was excluded.</summary>
    public string HfrArcsecDisclosure { get; }

    public bool HasHfrArcsecDisclosure => HfrArcsecDisclosure.Length > 0;

    /// <summary>The filters this target was shot through, each with its configured colour, for
    /// the 7 px dots in the log line.</summary>
    public IReadOnlyList<FilterSwatchViewModel> FilterSwatches { get; }

    /// <summary>The per-filter integration line under the log line, one bar per swatch in the
    /// swatch order, the longest bar at the full width.</summary>
    public IReadOnlyList<IntegrationBarViewModel> IntegrationBars { get; }

    public bool HasIntegrationBars => IntegrationBars.Count > 0;

    /// <summary>The bars' labels as one line, filters in the bars' order.</summary>
    public string IntegrationFiguresText
        => string.Join("  ", IntegrationBars.Select(bar => $"{bar.FilterName} {bar.HoursText}"));

    // ---- P12: the comp's log line ------------------------------------------------------------

    /// <summary>The log line as its runs, so the figures render at weight 600 in primary ink and
    /// the connecting words stay secondary. The filters and the equipment follow as their own
    /// runs, because the filters carry their colour dots.</summary>
    public IReadOnlyList<LogLineRunViewModel> LogLineRuns { get; }

    /// <summary>"on ASI2600MM, RC8." Empty when the group has no equipment.</summary>
    public string LogLineEquipmentText { get; }

    public bool HasLogLineEquipment => LogLineEquipmentText.Length > 0;

    /// <summary>
    /// Spec 12.4's two disclosures joined into the comp's tertiary sentence: "Averages below pool
    /// all 148 frames. Arcsecond figures leave out 23 frames without a plate scale; eccentricity
    /// pools the 143 frames measured by header." Empty when neither disclosure applies, which
    /// hides the line entirely.
    /// </summary>
    /// <remarks><see cref="HfrArcsecDisclosure"/> is unchanged and still carries the plate-scale
    /// count on its own; this is the wording the redesigned page renders.</remarks>
    public string LogLineDisclosureText { get; }

    public bool HasLogLineDisclosure => LogLineDisclosureText.Length > 0;

    // MetricText.Date takes a DateOnly; the totals row's two dates are nullable.
    private static string FormatDate(DateOnly? date)
        => date is { } present ? MetricText.Date(present) : "";

    private static string Frames(int count) => count == 1 ? "frame" : "frames";

    private static IReadOnlyList<LogLineRunViewModel> BuildLogLine(
        TargetTotals totals,
        string integrationText,
        string sessionCountText,
        string frameCountText,
        string firstSessionText,
        string lastSessionText)
    {
        List<LogLineRunViewModel> runs =
        [
            new(integrationText, true),
            new("over", false),
            new(sessionCountText, true),
            new(totals.SessionCount == 1 ? "night," : "nights,", false),
            new(frameCountText, true),
        ];

        // A group with no dated session ends the sentence at the frame count rather than
        // rendering an empty date span.
        if (firstSessionText.Length > 0 && lastSessionText.Length > 0)
        {
            runs.Add(new LogLineRunViewModel($"light {Frames(totals.FrameCount)},", false));
            runs.Add(new LogLineRunViewModel(firstSessionText, true));
            runs.Add(new LogLineRunViewModel("to", false));
            runs.Add(new LogLineRunViewModel($"{lastSessionText}.", true));
        }
        else
        {
            runs.Add(new LogLineRunViewModel($"light {Frames(totals.FrameCount)}.", false));
        }

        return runs;
    }

    private static string BuildDisclosure(TargetTotals totals)
    {
        List<string> parts = [];

        if (totals.HfrArcsecExcludedCount > 0)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Arcsecond figures leave out {totals.HfrArcsecExcludedCount} {Frames(totals.HfrArcsecExcludedCount)} without a plate scale"));
        }

        if (totals.EccentricityExcludedCount > 0)
        {
            // A null source is not an error: a frame can carry an eccentricity with no recorded
            // source at all, and that source can win the modal vote (Task 1 handoff).
            var pooled = Math.Max(0, totals.FrameCount - totals.EccentricityExcludedCount);
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"eccentricity pools the {MetricText.Count(pooled)} {Frames(pooled)} measured by {totals.EccentricityModalSource ?? "an unrecorded source"}"));
        }

        return parts.Count == 0
            ? ""
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Averages below pool all {MetricText.Count(totals.FrameCount)} {Frames(totals.FrameCount)}. {string.Join("; ", parts)}.");
    }
}
