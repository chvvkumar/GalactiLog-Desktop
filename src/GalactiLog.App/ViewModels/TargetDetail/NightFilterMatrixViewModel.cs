using System.Globalization;
using GalactiLog.App.Controls.Table;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>One filter of one night in the Compare table: the five medians under the filter's
/// name. The night prints on its group's first row only; a blank there is a group label, not a
/// missing value, so it is not a dash.</summary>
public sealed record CompareRowViewModel(
    DateOnly Night, bool IsFirstOfNight, bool IsAltGroup, FilterSwatchViewModel? Filter, MetricRowViewModel Metrics)
{
    public string NightText => IsFirstOfNight ? MetricText.Date(Night) : "";
}

/// <summary>One filter's cell of a matrix row; <paramref name="ColumnId"/> names the filter's strip
/// column (<c>f0</c>, <c>f1</c>...), so the cells of one filter line up across rows and tables.</summary>
public sealed record MatrixCellViewModel(string ColumnId, string Text);

/// <summary>One row of the exposure or hours table: a label, one cell per filter in bar order and a total.</summary>
public sealed record MatrixRowViewModel(string Label, IReadOnlyList<MatrixCellViewModel> Cells, string Total);

/// <summary>
/// The spine of the three per-night, per-filter tables. Every figure is summed from the
/// rows, never from the ledger's night figures, because a frame with no filter is in no row.
/// </summary>
public sealed class NightFilterMatrixViewModel
{
    private readonly Dictionary<(DateOnly Night, string Filter), NightFilterOverview> _cells;
    private readonly Dictionary<DateOnly, SessionOverview> _overviews;

    /// <param name="rows">The query's rows.</param>
    /// <param name="filters">The filter columns, in bar order; a row of another filter is dropped.</param>
    /// <param name="compareNights">The Compare table's nights, any order; rows come out newest
    /// first, the night list's order. Null is <see cref="Nights"/>.</param>
    /// <param name="totals">The target's totals, for <see cref="Overall"/>; null, or a target with
    /// no frame, leaves it null.</param>
    /// <param name="overviews">The nights' overviews: a Compare night with no filter row reads the
    /// night's own medians, the figures the trend chart plots for it.</param>
    public NightFilterMatrixViewModel(
        IReadOnlyList<NightFilterOverview> rows,
        IReadOnlyList<FilterSwatchViewModel> filters,
        IEnumerable<DateOnly>? compareNights = null,
        TargetTotals? totals = null,
        IEnumerable<SessionOverview>? overviews = null)
    {
        Filters = filters;
        FilterHeads = Cells(filter => filter);
        Overall = totals is { FrameCount: > 0 } ? new OverallMetricsViewModel(totals, filters) : null;
        _overviews = (overviews ?? []).DistinctBy(overview => overview.SessionDate).ToDictionary(overview => overview.SessionDate);
        var known = filters.Select(filter => filter.FilterName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = rows.Where(row => known.Contains(row.Filter)).ToList();
        _cells = new(new CellKeyComparer());
        foreach (var row in kept)
        {
            _cells[(row.SessionDate, row.Filter)] = row;
        }

        Nights = [.. kept.Select(row => row.SessionDate).Distinct().OrderDescending()];

        CompareRows = [.. (compareNights ?? Nights).OrderDescending()
            .SelectMany((night, index) => CompareGroup(night, index % 2 == 1))];

        var lengths = kept.SelectMany(row => row.Exposures).Select(exposure => exposure.Seconds).Distinct().Order().ToList();
        // The unit is in the "Exp s" heading, so a label is the bare figure (ruling R12).
        ExposureRows = [.. lengths.Select(seconds => new MatrixRowViewModel(
            MetricText.ExposureFigure(seconds),
            Cells(filter => Frames(ExposureFrames(filter, seconds))),
            MetricText.Count(filters.Sum(filter => ExposureFrames(filter.FilterName, seconds)))))];
        ExposureTotalRow = new MatrixRowViewModel(
            TableHeads.Total,
            Cells(filter => MetricText.Count(FrameCount(filter))),
            MetricText.Count(kept.Sum(row => row.FrameCount)));

        HoursRows = [.. Nights.Select(night => new MatrixRowViewModel(
            MetricText.Date(night),
            Cells(filter => Hours(Cell(night, filter)?.IntegrationSeconds)),
            Hours(RowSeconds(night))))];
        HoursTotalRow = new MatrixRowViewModel(
            TableHeads.Total,
            Cells(filter => Hours(ColumnSeconds(filter))),
            Hours(TotalSeconds));
    }

    /// <summary>Nights with at least one row, newest first, which is the ledger's order.</summary>
    public IReadOnlyList<DateOnly> Nights { get; }

    /// <summary>The filter columns in bar order.</summary>
    public IReadOnlyList<FilterSwatchViewModel> Filters { get; }

    /// <summary>The matrices' filter headings, one strip cell per filter.</summary>
    public IReadOnlyList<MatrixCellViewModel> FilterHeads { get; }

    /// <summary>The Integration tab's Overall metrics; null when the page passed no totals or the
    /// target has no frame. Not gated by <see cref="HasRows"/>: a target whose frames carry no
    /// filter or no date has no matrix row and still has means.</summary>
    public OverallMetricsViewModel? Overall { get; }

    public bool HasOverall => Overall is not null;

    /// <summary>The Compare table: one group per night the trend chart plots, newest first, one
    /// row per filter the night shot, in bar order.</summary>
    public IReadOnlyList<CompareRowViewModel> CompareRows { get; }

    public bool HasCompareRows => CompareRows.Count > 0;

    /// <summary>One row per exposure length, ascending, cells are frame counts.</summary>
    public IReadOnlyList<MatrixRowViewModel> ExposureRows { get; }

    public MatrixRowViewModel ExposureTotalRow { get; }

    public IReadOnlyList<MatrixRowViewModel> HoursRows { get; }

    public MatrixRowViewModel HoursTotalRow { get; }

    public bool HasRows => Nights.Count > 0;

    public NightFilterOverview? Cell(DateOnly night, string filter)
        => _cells.GetValueOrDefault((night, filter));

    public double RowSeconds(DateOnly night)
        => _cells.Values.Where(row => row.SessionDate == night).Sum(row => row.IntegrationSeconds);

    public double ColumnSeconds(string filter)
        => _cells.Values.Where(row => string.Equals(row.Filter, filter, StringComparison.OrdinalIgnoreCase)).Sum(row => row.IntegrationSeconds);

    public double TotalSeconds => _cells.Values.Sum(row => row.IntegrationSeconds);

    private int FrameCount(string filter)
        => _cells.Values.Where(row => string.Equals(row.Filter, filter, StringComparison.OrdinalIgnoreCase)).Sum(row => row.FrameCount);

    private int ExposureFrames(string filter, double seconds)
        => _cells.Values
            .Where(row => string.Equals(row.Filter, filter, StringComparison.OrdinalIgnoreCase))
            .SelectMany(row => row.Exposures)
            .Where(exposure => exposure.Seconds == seconds)
            .Sum(exposure => exposure.Frames);

    /// <summary>One row's strip cells, one per filter in bar order, from each filter's text.</summary>
    private List<MatrixCellViewModel> Cells(Func<string, string> text)
        => [.. Filters.Select((filter, index) => new MatrixCellViewModel(
            string.Create(CultureInfo.InvariantCulture, $"f{index}"), text(filter.FilterName)))];

    private static string Frames(int frames) => frames == 0 ? MetricText.Missing : MetricText.Count(frames);

    private static string Hours(double? seconds)
        => seconds is { } present ? MetricText.HourFigure(present) : MetricText.Missing;

    // A night with no row of a known filter (its frames carry no filter) keeps one All frames row
    // of the night's own medians, the figures the chart plots, so a night the chart plots is never
    // silently absent from the table and never reads "no metrics" beside a plotted point.
    private IEnumerable<CompareRowViewModel> CompareGroup(DateOnly night, bool alt)
    {
        var shot = Filters
            .Select(filter => (Filter: filter, Row: Cell(night, filter.FilterName)))
            .Where(pair => pair.Row is not null)
            .Select(pair => (Filter: (FilterSwatchViewModel?)pair.Filter, Metrics: MetricRowViewModel.Of(
                pair.Filter.FilterName,
                pair.Row!.MedianHfr,
                pair.Row.MedianEccentricity,
                pair.Row.MedianFwhm,
                pair.Row.MedianGuidingRms,
                pair.Row.MedianDetectedStars)))
            .ToList();
        if (shot.Count == 0)
        {
            var overview = _overviews.GetValueOrDefault(night);
            shot.Add((null, MetricRowViewModel.Of(
                OverallMetricsViewModel.AllFramesLabel,
                overview?.MedianHfr,
                overview?.MedianEccentricity,
                overview?.MedianFwhm,
                overview?.MedianGuidingRmsArcsec,
                overview?.MedianDetectedStars)));
        }

        return shot.Select((pair, index) => new CompareRowViewModel(night, index == 0, alt, pair.Filter, pair.Metrics));
    }

    private sealed class CellKeyComparer : IEqualityComparer<(DateOnly Night, string Filter)>
    {
        public bool Equals((DateOnly Night, string Filter) x, (DateOnly Night, string Filter) y)
            => x.Night == y.Night && string.Equals(x.Filter, y.Filter, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((DateOnly Night, string Filter) key)
            => HashCode.Combine(key.Night, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Filter));
    }
}
