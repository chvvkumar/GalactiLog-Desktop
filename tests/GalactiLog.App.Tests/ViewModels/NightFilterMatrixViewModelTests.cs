using Avalonia.Media;
using Avalonia.Media.Immutable;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// The night by filter spine. Every total is summed from the rows.
public class NightFilterMatrixViewModelTests
{
    private static readonly DateOnly Newer = new(2025, 3, 9);
    private static readonly DateOnly Older = new(2025, 3, 2);

    internal static NightFilterOverview Row(
        DateOnly night, string filter, double seconds, int frames, (double Seconds, int Frames)[] exposures,
        double? hfr = null, double? stars = null)
        => new(night, filter, seconds, frames, [.. exposures.Select(e => new ExposureCount(e.Seconds, e.Frames))], hfr, null, null, null, stars);

    internal static IReadOnlyList<FilterSwatchViewModel> Swatches(params string[] names)
        => [.. names.Select(name => new FilterSwatchViewModel(name, new ImmutableSolidColorBrush(Colors.Gray)))];

    // Older night listed first, so a result in input order instead of newest first is visible.
    internal static IReadOnlyList<NightFilterOverview> ThreeRows() =>
    [
        Row(Older, "Ha", 3_600d, 12, [(300d, 12)]),
        Row(Newer, "Ha", 1_800d, 6, [(300d, 3), (600d, 3)], hfr: 2.346d, stars: 1_234d),
        Row(Newer, "OIII", 7_200d, 24, [(300d, 24)]),
    ];

    [Fact]
    public void NightsAreNewestFirst_AndFiltersKeepTheBarOrder()
    {
        // A failure is the older night first, or OIII before Ha.
        var matrix = new NightFilterMatrixViewModel(ThreeRows(), Swatches("OIII", "Ha"));

        Assert.Equal([Newer, Older], matrix.Nights);
        Assert.Equal(["OIII", "Ha"], matrix.Filters.Select(filter => filter.FilterName));
    }

    [Fact]
    public void Cell_IsTheRowOfThatNightAndFilter_AndNullWhereThereIsNone()
    {
        var matrix = new NightFilterMatrixViewModel(ThreeRows(), Swatches("Ha", "OIII"));

        Assert.Equal(1_800d, matrix.Cell(Newer, "Ha")!.IntegrationSeconds);
        Assert.Equal(1_800d, matrix.Cell(Newer, "ha")!.IntegrationSeconds);
        Assert.Null(matrix.Cell(Older, "OIII"));
    }

    [Fact]
    public void Totals_AreSummedFromTheRows()
    {
        // Tier 1. A failure is a row total that misses a filter, a column total that adds another
        // filter's rows, or a grand total that is not the sum of the rows.
        var matrix = new NightFilterMatrixViewModel(ThreeRows(), Swatches("Ha", "OIII"));

        Assert.Equal(9_000d, matrix.RowSeconds(Newer));
        Assert.Equal(3_600d, matrix.RowSeconds(Older));
        Assert.Equal(5_400d, matrix.ColumnSeconds("Ha"));
        Assert.Equal(7_200d, matrix.ColumnSeconds("OIII"));
        Assert.Equal(12_600d, matrix.TotalSeconds);

        Assert.Equal(["0.5", "2.0"], Texts(matrix.HoursRows[0]));
        Assert.Equal("2.5", matrix.HoursRows[0].Total);
        Assert.Equal(["1.0", "-"], Texts(matrix.HoursRows[1]));
        Assert.Equal("1.0", matrix.HoursRows[1].Total);
        Assert.Equal(["1.5", "2.0"], Texts(matrix.HoursTotalRow));
        Assert.Equal("3.5", matrix.HoursTotalRow.Total);
    }

    [Fact]
    public void ExposureRows_AreOneRowPerLengthAscending_WithFrameCounts()
    {
        // Tier 1. A failure is a length listed twice, a count that drops a night, or a total that
        // is not the frames of the rows.
        var matrix = new NightFilterMatrixViewModel(ThreeRows(), Swatches("Ha", "OIII"));

        Assert.Equal(["300", "600"], matrix.ExposureRows.Select(row => row.Label));
        Assert.Equal(["15", "24"], Texts(matrix.ExposureRows[0]));
        Assert.Equal("39", matrix.ExposureRows[0].Total);
        Assert.Equal(["3", "-"], Texts(matrix.ExposureRows[1]));
        Assert.Equal(["18", "24"], Texts(matrix.ExposureTotalRow));
        Assert.Equal("42", matrix.ExposureTotalRow.Total);
    }

    [Fact]
    public void MatrixCells_CarryOneColumnIdPerFilter_InBarOrder()
    {
        // A failure is a strip cell in another filter's column group, which misaligns the matrices.
        var matrix = new NightFilterMatrixViewModel(ThreeRows(), Swatches("Ha", "OIII"));

        Assert.Equal(
            [new MatrixCellViewModel("f0", "Ha"), new MatrixCellViewModel("f1", "OIII")],
            matrix.FilterHeads);
        var rows = matrix.ExposureRows.Concat(matrix.HoursRows).Append(matrix.ExposureTotalRow).Append(matrix.HoursTotalRow);
        Assert.All(rows, row => Assert.Equal(["f0", "f1"], row.Cells.Select(cell => cell.ColumnId)));
    }

    [Fact]
    public void Overall_IsBuiltFromTheTotals_AndNullWithout()
    {
        var totals = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory.PopulatedTotals();

        var matrix = new NightFilterMatrixViewModel(ThreeRows(), Swatches("OIII", "Ha"), totals: totals);

        Assert.Equal("2.34", matrix.Overall!.AllFrames.HfrText);
        Assert.Equal(["OIII", "Ha"], matrix.Overall.Filters.Select(row => row.Label));
        Assert.Null(new NightFilterMatrixViewModel(ThreeRows(), Swatches("Ha")).Overall);
    }

    [Fact]
    public void ARowOfAFilterNotInTheColumns_IsLeftOutOfEveryTotal()
    {
        var matrix = new NightFilterMatrixViewModel(ThreeRows(), Swatches("Ha"));

        Assert.Equal(5_400d, matrix.TotalSeconds);
        Assert.Equal("1.5", matrix.HoursTotalRow.Total);
    }

    [Fact]
    public void CompareRows_HoldTheFiveMedians_AndAnEmptyCellForANull()
    {
        var matrix = new NightFilterMatrixViewModel(ThreeRows(), Swatches("Ha", "OIII"));

        Assert.Equal([Newer, Older], matrix.CompareNights.Select(night => night.Night));
        var ha = matrix.CompareRows[0].Cells[0];
        Assert.Equal("2.35", ha.Hfr);
        Assert.Equal("", ha.Eccentricity);
        Assert.Equal("1,234", ha.DetectedStars);
        Assert.Equal(new CompareCellViewModel("", "", "", "", ""), matrix.CompareRows[1].Cells[1]);
    }

    [Fact]
    public void CheckedNights_LimitOnlyTheCompareNights()
    {
        // A failure is the hours table losing its unchecked night.
        var matrix = new NightFilterMatrixViewModel(ThreeRows(), Swatches("Ha", "OIII"), new HashSet<DateOnly> { Older });

        Assert.Equal([Older], matrix.CompareNights.Select(night => night.Night));
        Assert.All(matrix.CompareRows, row => Assert.Single(row.Cells));
        Assert.Equal(2, matrix.HoursRows.Count);
        Assert.Equal([Newer, Older], matrix.Nights);
    }

    private static GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory.Harness Page()
        => GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory.Create(
            get: _ => GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory.PopulatedDetail() with
            {
                NightFilters =
                [
                    Row(GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory.LastSession, "Ha", 3_600d, 12, [(300d, 12)]),
                    Row(GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory.FirstSession, "Ha", 1_800d, 6, [(300d, 6)]),
                ],
            }).Settle().SettleCards();

    [Fact]
    public void TickingACard_WithCheckedOnlyOn_PutsItsNightInTheCompareGroups()
    {
        // A failure is a matrix still holding no night after the tick, because the IsChecked
        // rebuild is missing. All sessions are shown, as Compare nights opens.
        using var harness = Page();
        var page = harness.ViewModel;
        page.TargetChart.ShowAllSessions = true;
        page.Sessions[0].IsChecked = true;
        page.TargetChart.CheckedOnly = true;
        Assert.Equal([page.Sessions[0].SessionDate], page.NightFilterMatrix!.CompareNights.Select(night => night.Night));

        page.Sessions[1].IsChecked = true;

        Assert.Equal(2, page.NightFilterMatrix!.CompareNights.Count);
    }

    [Fact]
    public void TheCompareNights_AreTheChartsPlottedNights_InTheChartsOrder()
    {
        // A failure is the first column group under another night than the first lane band, or a
        // group for a night the chart does not plot.
        using var harness = Page();
        var page = harness.ViewModel;
        var first = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory.FirstSession;
        var last = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory.LastSession;

        page.TargetChart.ShowAllSessions = true;
        Assert.Equal([first, last], page.NightFilterMatrix!.CompareNights.Select(night => night.Night));

        page.TargetChart.ShowAllSessions = false;
        Assert.Equal([last], page.NightFilterMatrix!.CompareNights.Select(night => night.Night));
        Assert.Equal(2, page.NightFilterMatrix.HoursRows.Count);
    }

    [Fact]
    public void ThePagesMatrix_CarriesTheOverallMetrics()
    {
        using var harness = Page();

        Assert.Equal("All frames", harness.ViewModel.NightFilterMatrix!.Overall!.AllFrames.Label);
    }

    [Fact]
    public void TheMatrix_IsNotRebuilt_ByAnUnrelatedChange_OrByATickWithCheckedOnlyOff()
    {
        // A failure is a new matrix reference on either change.
        using var harness = Page();
        var page = harness.ViewModel;
        var rebuilds = 0;
        page.PropertyChanged += (_, e) => rebuilds += e.PropertyName == nameof(page.NightFilterMatrix) ? 1 : 0;

        page.RenameText = "another name";
        page.Sessions[0].IsChecked = true;
        page.Sessions[0].IsChecked = false;

        Assert.Equal(0, rebuilds);
    }

    private static IEnumerable<string> Texts(MatrixRowViewModel row) => row.Cells.Select(cell => cell.Text);

    [Fact]
    public void NoRows_IsEmpty()
    {
        var matrix = new NightFilterMatrixViewModel([], Swatches("Ha"));

        Assert.False(matrix.HasRows);
        Assert.Equal(0d, matrix.TotalSeconds);
        Assert.Empty(matrix.ExposureRows);
    }
}
