using System.Globalization;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>One night of the Compare table's column groups.</summary>
public sealed record CompareNightViewModel(DateOnly Night)
{
    public string Label => MetricText.Date(Night);
}

/// <summary>One night and filter of the Compare table: the five medians, empty text for a null.</summary>
public sealed record CompareCellViewModel(string Hfr, string Eccentricity, string Fwhm, string GuidingRms, string DetectedStars);

/// <summary>One filter of the Compare table, one cell per night in <see cref="NightFilterMatrixViewModel.CompareNights"/>.</summary>
public sealed record CompareRowViewModel(FilterSwatchViewModel Filter, IReadOnlyList<CompareCellViewModel> Cells);

/// <summary>One row of the exposure or hours table: a label, one cell per filter in bar order and a total.</summary>
public sealed record MatrixRowViewModel(string Label, IReadOnlyList<string> Cells, string Total);

/// <summary>
/// The spine of the three per-night, per-filter tables. Every figure is summed from the
/// rows, never from the ledger's night figures, because a frame with no filter is in no row.
/// </summary>
public sealed class NightFilterMatrixViewModel
{
    private readonly Dictionary<(DateOnly Night, string Filter), NightFilterOverview> _cells;

    /// <param name="rows">The query's rows.</param>
    /// <param name="filters">The filter columns, in bar order; a row of another filter is dropped.</param>
    /// <param name="compareNights">The Compare table's nights in their column order; null is
    /// <see cref="Nights"/>.</param>
    public NightFilterMatrixViewModel(
        IReadOnlyList<NightFilterOverview> rows,
        IReadOnlyList<FilterSwatchViewModel> filters,
        IEnumerable<DateOnly>? compareNights = null)
    {
        Filters = filters;
        var known = filters.Select(filter => filter.FilterName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = rows.Where(row => known.Contains(row.Filter)).ToList();
        _cells = new(new CellKeyComparer());
        foreach (var row in kept)
        {
            _cells[(row.SessionDate, row.Filter)] = row;
        }

        Nights = [.. kept.Select(row => row.SessionDate).Distinct().OrderDescending()];

        CompareNights = [.. (compareNights ?? Nights).Select(night => new CompareNightViewModel(night))];
        CompareRows = [.. filters.Select(filter => new CompareRowViewModel(
            filter,
            [.. CompareNights.Select(night => CompareCell(Cell(night.Night, filter.FilterName)))]))];

        var lengths = kept.SelectMany(row => row.Exposures).Select(exposure => exposure.Seconds).Distinct().Order().ToList();
        ExposureRows = [.. lengths.Select(seconds =>
        {
            var byFilter = filters.Select(filter => ExposureFrames(filter.FilterName, seconds)).ToList();
            return new MatrixRowViewModel(
                string.Create(CultureInfo.InvariantCulture, $"{seconds:0.##} s"),
                [.. byFilter.Select(Frames)],
                MetricText.Count(byFilter.Sum()));
        })];
        ExposureTotalRow = new MatrixRowViewModel(
            "Total",
            [.. filters.Select(filter => MetricText.Count(FrameCount(filter.FilterName)))],
            MetricText.Count(kept.Sum(row => row.FrameCount)));

        HoursRows = [.. Nights.Select(night => new MatrixRowViewModel(
            MetricText.Date(night),
            [.. filters.Select(filter => Hours(Cell(night, filter.FilterName)?.IntegrationSeconds))],
            Hours(RowSeconds(night))))];
        HoursTotalRow = new MatrixRowViewModel(
            "Total",
            [.. filters.Select(filter => Hours(ColumnSeconds(filter.FilterName)))],
            Hours(TotalSeconds));
    }

    /// <summary>Nights with at least one row, newest first, which is the ledger's order.</summary>
    public IReadOnlyList<DateOnly> Nights { get; }

    /// <summary>The filter columns in bar order.</summary>
    public IReadOnlyList<FilterSwatchViewModel> Filters { get; }

    /// <summary>The column groups: the nights the trend chart plots, in its order, oldest first,
    /// so each group sits under its night's band.</summary>
    public IReadOnlyList<CompareNightViewModel> CompareNights { get; }

    public IReadOnlyList<CompareRowViewModel> CompareRows { get; }

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

    private static string Frames(int frames) => frames == 0 ? "" : MetricText.Count(frames);

    private static string Hours(double? seconds)
        => seconds is { } present ? MetricText.Format(present / 3600d, "0.0") : "";

    private static CompareCellViewModel CompareCell(NightFilterOverview? row) => new(
        MetricText.Format(row?.MedianHfr, "0.00"),
        MetricText.Format(row?.MedianEccentricity, "0.00"),
        MetricText.Format(row?.MedianFwhm, "0.00"),
        MetricText.Format(row?.MedianGuidingRms, "0.00"),
        MetricText.Format(row?.MedianDetectedStars, "N0"));

    private sealed class CellKeyComparer : IEqualityComparer<(DateOnly Night, string Filter)>
    {
        public bool Equals((DateOnly Night, string Filter) x, (DateOnly Night, string Filter) y)
            => x.Night == y.Night && string.Equals(x.Filter, y.Filter, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((DateOnly Night, string Filter) key)
            => HashCode.Combine(key.Night, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Filter));
    }
}
