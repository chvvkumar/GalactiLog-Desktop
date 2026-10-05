using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Analysis;

// Spec 12.14's Matrix tab and spec 13's "Correlation matrix" row, task5.md section 2 and cases 15
// to 22. Nothing here re-proves a base rule: AnalysisTabBaseTests already covers lazy creation,
// stale marking, the kept result, the generation token, the per-tab failure, Retry, the reversed
// range and the plate scale callout, and a case here that asserted one of those would be a
// duplicate. What belongs here is the tab's own grid.
//
// AvaloniaFact throughout, and the post seam reaches the UI thread. Both are load bearing rather
// than habitual: the mapper resolves four theme tokens through ChartTheme.Read, which answers its
// documented neutral off the UI thread, so under the inline seam every assertion on a colour would
// compare the fallback with itself and pass with the ramp wired to nothing at all.
public class MatrixTabTests
{
    private static readonly AnalysisFilter AnyFilter =
        new(null, null, null, AnalysisGranularity.Frame, null, null);

    // Twelve pairs given no r, of which the FIRST THREE carry 400 paired points. The point count
    // is deliberately generous there: "no r" is MatrixCell.PearsonR being null and nothing else
    // (seam ruling S3), so a view-model that re-derived the gate from NPoints would draw those
    // three and the absent set below would not match.
    private static readonly (AnalysisMetric X, AnalysisMetric Y)[] Blanks =
    [
        (AnalysisMetric.SkyQuality, AnalysisMetric.Hfr),
        (AnalysisMetric.SkyQuality, AnalysisMetric.Fwhm),
        (AnalysisMetric.SkyQuality, AnalysisMetric.Eccentricity),
        (AnalysisMetric.Humidity, AnalysisMetric.AduStdev),
        (AnalysisMetric.WindSpeed, AnalysisMetric.AduMedian),
        (AnalysisMetric.AmbientTemp, AnalysisMetric.AduMean),
        (AnalysisMetric.DewPoint, AnalysisMetric.DetectedStars),
        (AnalysisMetric.Pressure, AnalysisMetric.GuidingRmsDec),
        (AnalysisMetric.CloudCover, AnalysisMetric.GuidingRmsRa),
        (AnalysisMetric.FocuserTemp, AnalysisMetric.GuidingRms),
        (AnalysisMetric.Airmass, AnalysisMetric.Eccentricity),
        (AnalysisMetric.SensorTemp, AnalysisMetric.Fwhm),
    ];

    // A cell whose column and row are neither equal nor at either end of either axis, which is the
    // only shape that catches a transposition AND a missing row reversal at once. Column 3 is
    // dew_point; drawn row 6 is AnalysisMetrics.Y[9 - 6], which is guiding_rms.
    private const int OffCentreColumn = 3;
    private const int OffCentreRow = 6;

    // ---- fixtures ---------------------------------------------------------------------------

    private static MatrixResult Grid(
        Func<AnalysisMetric, AnalysisMetric, (double? R, int Points)> cell, int plateScales = 1)
        => new(
            [
                .. AnalysisMetrics.X.SelectMany(x => AnalysisMetrics.Y.Select(y =>
                {
                    var (r, points) = cell(x, y);
                    return new MatrixCell(x, y, r, points);
                })),
            ],
            plateScales);

    private static MatrixResult Full(double r = 0.5d, int points = 40, int plateScales = 1)
        => Grid((_, _) => (r, points), plateScales);

    private static MatrixResult WithBlanks()
        => Grid((x, y) =>
        {
            var index = Array.IndexOf(Blanks, (x, y));
            return index < 0 ? (0.5d, 40) : (null, index < 3 ? 400 : 4);
        });

    private static MatrixTabViewModel Tab(
        Func<AnalysisFilter, MatrixResult> query,
        Action<AnalysisMetric, AnalysisMetric>? open = null,
        Action<Action>? post = null)
        => new(
            () => AnyFilter,
            query,
            open ?? ((_, _) => { }),
            post ?? (action => Dispatcher.UIThread.Post(action)));

    private static async Task<MatrixTabViewModel> Loaded(
        MatrixResult result, Action<AnalysisMetric, AnalysisMetric>? open = null)
    {
        var tab = Tab(_ => result, open);
        tab.IsVisible = true;

        // Section 0's first case rule: the await is what makes every assertion below read the
        // published grid rather than racing the background load.
        await tab.PendingLoad!;
        Dispatcher.UIThread.RunJobs();
        return tab;
    }

    // Fix pass 2: the grid is TWO HeatSeries over one ramp and one pair of axes, the drawn cells
    // partitioned by which of the two label inks reads better over them. Everything but Values and
    // DataLabelsPaint is identical between the halves, so a case that asserts the ramp asserts it
    // on both.
    private static IReadOnlyList<HeatSeries<WeightedPoint>> Halves(MatrixTabViewModel tab)
    {
        var halves = tab.Chart!.Series.Select(Assert.IsType<HeatSeries<WeightedPoint>>).ToList();
        Assert.Equal(2, halves.Count);
        return halves;
    }

    private static IReadOnlyList<WeightedPoint> Points(HeatSeries<WeightedPoint> half)
        => [.. (IEnumerable<WeightedPoint>)half.Values!];

    private static IReadOnlyList<WeightedPoint> Drawn(MatrixTabViewModel tab)
        => [.. Halves(tab).SelectMany(Points)];

    private static (int Column, int Row) Position(AnalysisMetric x, AnalysisMetric y)
        => (AnalysisMetrics.X.ToList().IndexOf(x), MatrixChartViewModel.Rows.ToList().IndexOf(y));

    // ---- case 15 ----------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task EveryPairIsOffered_AndOnlyThePairsWithAnR_AreDrawn()
    {
        // Case 15. Spike trap 7 and spec 12.14 departure 3: a WeightedPoint with a null weight is
        // DRAWN, takes the ramp's midpoint ink and prints 0.00, which a reader cannot tell from a
        // measured zero, so a pair with no r is omitted from Values entirely.
        //
        // The exact set and not the count alone. Red against a view-model that gives a blank pair
        // a null weight: Values reads 100. Red against one that keys the omission on NPoints < 10
        // instead of on the null: the three blanks carrying 400 points are drawn and the absent
        // set is short by three.
        var tab = await Loaded(WithBlanks());

        Assert.Equal(100, tab.Chart!.Cells.Count);
        Assert.Equal(100 - Blanks.Length, Drawn(tab).Count);

        var drawn = Drawn(tab)
            .Select(point => ((int)point.X!.Value, (int)point.Y!.Value))
            .ToHashSet();
        var everyPosition = Enumerable.Range(0, AnalysisMetrics.X.Count)
            .SelectMany(column => Enumerable.Range(0, MatrixChartViewModel.Rows.Count)
                .Select(row => (column, row)))
            .ToHashSet();
        var expectedAbsent = Blanks.Select(pair => Position(pair.X, pair.Y)).ToHashSet();

        everyPosition.ExceptWith(drawn);
        Assert.Equal(expectedAbsent, everyPosition);

        // Recorded so nobody later treats this case as the row reversal's guard:
        // Position() derives its expected row from MatrixChartViewModel.Rows, the list under test,
        // so the absent-set assertion moves with a reversal defect instead of catching it. Case 21
        // pins the reversal independently, against the spec, with Rows[^1] being Hfr.

        // The other half of the same rule, on the accessible layer: the twelve are present as
        // positions and answer HasValue false, which is what makes their buttons inert.
        var blankEntries = tab.Chart.Cells.Where(cell => !cell.HasValue).Select(cell => (cell.Column, cell.Row));
        Assert.Equal(expectedAbsent, blankEntries.ToHashSet());
    }

    // ---- case 16 ----------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task TheRamp_IsPinnedToTheRangeMinusOneToOne()
    {
        // Case 16, the line the spike calls the most important in the block (trap 8). Red against
        // the library defaults: the ramp rescales to the data, and a matrix whose strongest r is
        // 0.3 paints that cell fully saturated.
        // Both halves, because a ramp pinned on one and left to rescale on the other would paint
        // the same r two different colours depending on which ink its label happened to take.
        var tab = await Loaded(Full(r: 0.3d));

        Assert.All(Halves(tab), half =>
        {
            Assert.Equal(-1d, half.MinValue);
            Assert.Equal(1d, half.MaxValue);
        });
    }

    // ---- case 17 ----------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task TheThreeRampStops_AreTheirTokens_BeforeAndAfterAThemeSwap()
    {
        // Case 17. Red against ColorMetricGuiding at the negative end, which is ruling A25's
        // reversal: in deep-sky metric-guiding and metric-worst are deltaE 10.5 apart at
        // contrast 1.03, so an r of -1 and an r of +1 rendered the same. Red against a grid with
        // no ChartTheme.Subscribe: the after-values keep the first theme's ink. Red against a read
        // through ChartTheme.Palette: MetricTokenOrder carries neither ColorInfo nor
        // ColorMetricWorst and this case would not compile.
        string[] keys = ["ColorInfo", "ColorBgElevated", "ColorMetricWorst"];

        try
        {
            ThemeManager.Apply("luminance");
            var tab = await Loaded(Full());

            var before = keys.Select(key => ChartTheme.Read(key, ChartTheme.Fallback).AsLvcColor()).ToArray();
            Assert.All(Halves(tab), half => Assert.Equal(before, half.HeatMap!));

            ThemeManager.Apply("deep-sky");
            Dispatcher.UIThread.RunJobs();

            var after = keys.Select(key => ChartTheme.Read(key, ChartTheme.Fallback).AsLvcColor()).ToArray();

            // Not vacuous twice over: the two themes really do paint this ramp differently, so the
            // assertion below is a re-read rather than a value compared with itself, and neither
            // set is the documented neutral a token read off the UI thread would have returned.
            Assert.NotEqual(before, after);
            Assert.DoesNotContain(ChartTheme.Fallback.AsLvcColor(), after);
            Assert.All(Halves(tab), half => Assert.Equal(after, half.HeatMap!));
        }
        finally
        {
            // Application.Current is process-wide under the harness, so the default theme goes
            // back rather than being left for whatever runs next.
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    // ---- case 18 ----------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task ACellClick_OpensTheDrawnPair_ThroughTheInjectedRoute()
    {
        // Case 18 and section 2.3. The route is the constructor delegate and nothing else: this
        // tab holds no page reference. Red against a transposition, which brings the pair back
        // swapped, and red against a missing row reversal, which brings it back mirrored; only an
        // off-diagonal, off-end coordinate catches both.
        var opened = new List<(AnalysisMetric X, AnalysisMetric Y)>();
        var tab = await Loaded(Full(), (x, y) => opened.Add((x, y)));

        tab.Chart!.OpenCell(OffCentreColumn, OffCentreRow);

        var expected = (
            AnalysisMetrics.X[OffCentreColumn],
            AnalysisMetrics.Y[AnalysisMetrics.Y.Count - 1 - OffCentreRow]);
        Assert.Equal(expected, Assert.Single(opened));

        // And the keyboard's route ends at the same delegate with the same pair, which is what
        // keeps the accessible layer from drifting into a second mapping.
        opened.Clear();
        var entry = tab.Chart.Resolve(OffCentreColumn, OffCentreRow)!;
        entry.ActivateCommand.Execute(null);
        Assert.Equal(expected, Assert.Single(opened));
    }

    // ---- case 19 ----------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task ABlankPosition_OpensNothing_OnEitherRoute()
    {
        // Case 19. Red against a view-model that maps a click by coordinate arithmetic instead of
        // by the drawn point: a press in the gap a blank cell leaves opens a pair.
        var blankX = AnalysisMetrics.X[OffCentreColumn];
        var blankY = AnalysisMetrics.Y[AnalysisMetrics.Y.Count - 1 - OffCentreRow];

        var opened = new List<(AnalysisMetric X, AnalysisMetric Y)>();
        var tab = await Loaded(
            Grid((x, y) => x == blankX && y == blankY ? (null, 400) : (0.5d, 40)),
            (x, y) => opened.Add((x, y)));

        Assert.Null(tab.Chart!.Resolve(OffCentreColumn, OffCentreRow));
        tab.Chart.OpenCell(OffCentreColumn, OffCentreRow);
        Assert.Empty(opened);

        // The keyboard's own gate. The entry exists so the layer stays square with the grid, its
        // command stays EXECUTABLE so the button keeps its place in the arrow walk and its "no
        // data" name is announced, and the body refuses to open a pair.
        // Red against a CanExecute gate: the button would be disabled and the name unreachable.
        var entry = tab.Chart.Cells.Single(
            cell => cell.Column == OffCentreColumn && cell.Row == OffCentreRow);
        Assert.False(entry.HasValue);
        Assert.True(entry.ActivateCommand.CanExecute(null));
        entry.ActivateCommand.Execute(null);
        Assert.Empty(opened);
    }

    // ---- a non-finite coordinate resolves to nothing -------------------------------------------

    [AvaloniaTheory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task ANonFiniteCoordinate_ResolvesToNothing(double coordinate)
    {
        // (int)Math.Round(double.NaN) is unspecified and keys (0, 0) on this
        // runtime, so without the guard a malformed point out of the chart library would open the
        // top-left pair. Red against the unguarded cast: Resolve answers the (0, 0) entry and the
        // recorder holds one pair.
        var opened = new List<(AnalysisMetric X, AnalysisMetric Y)>();
        var tab = await Loaded(Full(), (x, y) => opened.Add((x, y)));

        Assert.Null(tab.Chart!.Resolve(coordinate, 0d));
        Assert.Null(tab.Chart.Resolve(0d, coordinate));
        tab.Chart.OpenCell(coordinate, coordinate);
        Assert.Empty(opened);
    }

    // ---- a duplicate pair draws the first and does not fail the tab ----------------------------

    [AvaloniaFact]
    public async Task ADuplicatePair_DrawsTheFirstAndDoesNotFailTheTab()
    {
        // A ToDictionary over the cells throws on a duplicate (X, Y), the base
        // turns that into Failed plus Retry, and Retry re-runs the same query and fails
        // identically, so one malformed result locks the tab in a loop rather than degrading the
        // grid. Red against ToDictionary: the state reads Failed and the grid holds no series.
        //
        // The FIRST entry wins, which is the ruled behaviour: last-one-wins would pick a figure by
        // row order with nothing said about it.
        var duplicated = Full().Cells.ToList();
        var first = duplicated[0];
        duplicated.Add(first with { PearsonR = -0.99d, NPoints = 7 });

        var tab = await Loaded(new MatrixResult(duplicated, 1));

        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.Equal(100, tab.Chart!.Cells.Count);
        Assert.Equal(100, Drawn(tab).Count);

        var kept = tab.Chart.Resolve(
            Position(first.X, first.Y).Column, Position(first.X, first.Y).Row);
        Assert.Equal(first.PearsonR, kept!.PearsonR);
        Assert.Equal(first.NPoints, kept.NPoints);
    }

    // ---- case 20 ----------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task TheLoadingLine_IsThisTabsOwn()
    {
        // Case 20, spec 12.14's States table: the Matrix is the one tab whose loading line differs.
        // Red against the shared default, which reads "Loading...".
        //
        // The post seam parks the publish, so the tab is observed IN Loading rather than after it.
        var parked = new List<Action>();
        var tab = Tab(_ => Full(), post: parked.Add);

        tab.IsVisible = true;
        await tab.PendingLoad!;

        Assert.Equal(AnalysisTabState.Loading, tab.State);
        Assert.Equal("Computing correlations...", tab.StatusLine);
        Assert.NotEqual(AnalysisTabViewModel.DefaultLoadingText, tab.StatusLine);

        foreach (var action in parked)
        {
            action();
        }

        Assert.Equal(AnalysisTabState.Ready, tab.State);
    }

    // ---- case 21 ----------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task EveryCellBelowTheMinimum_DrawsTwoAxesAndTheLabelsAndNoCell()
    {
        // Case 21, spec 12.14's "Matrix with every cell below 10 points" row. Red against an empty
        // axis array: rc5.4's CartesianChartEngine.Measure throws "XAxes and YAxes must contain at
        // least one element" for a heat series as for every other kind (spike trap 11).
        var tab = await Loaded(Grid((_, _) => (null, 4)));

        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.True(tab.ShowsResult);
        Assert.Empty(Drawn(tab));

        var columns = Assert.Single(tab.Chart!.XAxes);
        var rows = Assert.Single(tab.Chart.YAxes);
        Assert.Equal(
            AnalysisMetrics.X.Select(metric => AnalysisMetricLabels.For(metric).Matrix),
            columns.Labels!);
        Assert.Equal(
            MatrixChartViewModel.Rows.Select(metric => AnalysisMetricLabels.For(metric).Matrix),
            rows.Labels!);

        // Spec 12.14: "Y metrics down the rows" with HFR first. Index 0 of a LiveCharts category
        // axis is the BOTTOM, so the reversed list is what puts HFR at the top (ruling P3-5).
        Assert.Equal(AnalysisMetric.Hfr, MatrixChartViewModel.Rows[^1]);
        Assert.Equal(100, tab.Chart.Cells.Count);
        Assert.DoesNotContain(tab.Chart.Cells, cell => cell.HasValue);
    }

    [AvaloniaFact]
    public async Task NoCellAtAll_IsTheEmptyState_WithOneBareAxisPerSide()
    {
        // The row above case 21's in the same table: the filters matched no frame, so the query
        // answered no cell and the grid is not drawn at all. The bare axes still ship, for the
        // reason above: the control measures while it is collapsed.
        var tab = await Loaded(new MatrixResult([], 0));

        Assert.Equal(AnalysisTabState.Empty, tab.State);
        Assert.False(tab.ShowsResult);
        Assert.Empty(tab.Chart!.Series);
        Assert.Empty(tab.Chart.Cells);
        Assert.Null(Assert.Single(tab.Chart.XAxes).Labels);
        Assert.Null(Assert.Single(tab.Chart.YAxes).Labels);
    }

    // ---- the two axes are pinned to all ten categories -----------------------------------------

    public static TheoryData<string> WhollyBlankShapes => ["last column", "first column", "last row", "everything"];

    [AvaloniaTheory]
    [MemberData(nameof(WhollyBlankShapes))]
    public async Task ACategoryNoCellFills_StillGetsItsColumnAndItsLabel(string blank)
    {
        // The launched look found the grid drawing NINE of its ten columns: the fixture's Sensor
        // Temp never varies, so every pair in that column has no r, a pair with no r is omitted
        // from the series, and an axis with no limits fits itself to the points it was given. The
        // tenth column and its label fell off the end while the keyboard still walked into it and
        // announced it, so a reader could reach a column nobody could see.
        //
        // Measured on a hosted grid before the fix: with the tenth column wholly blank the X axis
        // answered data bounds -0.5 to 8.5, against -0.5 to 9.5 on the full grid.
        //
        // Red against the axes as they stood: both limits read null on all four shapes.
        var lastColumn = AnalysisMetrics.X[^1];
        var firstColumn = AnalysisMetrics.X[0];
        var lastRow = MatrixChartViewModel.Rows[^1];

        var tab = await Loaded(Grid((x, y) => blank switch
        {
            "last column" => x == lastColumn ? (null, 40) : (0.5d, 40),
            "first column" => x == firstColumn ? (null, 40) : (0.5d, 40),
            "last row" => y == lastRow ? (null, 40) : (0.5d, 40),
            _ => (null, 40),
        }));

        var columns = Assert.Single(tab.Chart!.XAxes);
        var rows = Assert.Single(tab.Chart.YAxes);

        // Not vacuous: the shape really did leave a whole category with nothing drawn in it.
        var drawn = Drawn(tab);
        Assert.Equal(blank == "everything" ? 0 : 90, drawn.Count);

        foreach (var (axis, metrics) in new (ICartesianAxis Axis, IReadOnlyList<AnalysisMetric> Metrics)[]
                 {
                     (columns, AnalysisMetrics.X), (rows, MatrixChartViewModel.Rows),
                 })
        {
            Assert.Equal(
                metrics.Select(metric => AnalysisMetricLabels.For(metric).Matrix), axis.Labels!);

            // Half a cell either way, which is the span a full grid produces of its own accord and
            // the one the focus box already assumes.
            Assert.Equal(-0.5d, axis.MinLimit);
            Assert.Equal(metrics.Count - 0.5d, axis.MaxLimit);
        }
    }

    // ---- case 22 ----------------------------------------------------------------------------

    [Theory]
    [InlineData(-0.55d, "-0.55")]
    [InlineData(0.55d, "0.55")]
    [InlineData(-1d, "-1.00")]
    [InlineData(0d, "0.00")]
    public void TheCellLabel_PrintsTwoDecimalsWithTheSign(double r, string expected)
    {
        // Case 22. Red against Math.Abs anywhere in the formatter, which is the mistake ruling
        // A25's "the printed r carries the sign" exists to prevent: on red-light every ink on this
        // page is a shade of one red and the sign has nowhere else to go.
        Assert.Equal(expected, MatrixChartViewModel.CellLabel(r));
    }

    [AvaloniaFact]
    public async Task TheHoverLine_ReadsRToThreeDecimalsWithThePairsPointCount()
    {
        // Spec 12.14: "Hovering a cell reads r=x.xxx (N=n)". The sign is carried here too, and the
        // count is the pair's own NPoints rather than anything the drawn point holds.
        var tab = await Loaded(Full(r: -0.5555d, points: 137));

        Assert.Equal(
            "r=-0.556 (N=137)",
            tab.Chart!.Hover(new Coordinate(OffCentreColumn, OffCentreRow, -0.5555d)));
    }

    // ---- the accessible name ------------------------------------------------------------------

    [AvaloniaFact]
    public async Task EveryCellCarriesAnAccessibleName_NamingBothMetricsAndTheFigure()
    {
        // The keyboard layer's half of section 2.3. A position with an r names both metrics by
        // their full labels and prints the figure; one without says so in words rather than
        // offering a coloured 0.00. Every name comes from AnalysisMetricLabels and none is spelled
        // in the grid (ruling B15).
        var blankX = AnalysisMetrics.X[OffCentreColumn];
        var blankY = AnalysisMetrics.Y[AnalysisMetrics.Y.Count - 1 - OffCentreRow];
        var tab = await Loaded(Grid((x, y) => x == blankX && y == blankY ? (null, 4) : (-0.55d, 40)));

        var blank = tab.Chart!.Resolve(OffCentreColumn, OffCentreRow);
        Assert.Null(blank);

        var entries = tab.Chart.Cells.ToDictionary(cell => (cell.Column, cell.Row));
        Assert.Equal(
            $"{AnalysisMetricLabels.Label(blankX)} against {AnalysisMetricLabels.Label(blankY)}, no data",
            entries[(OffCentreColumn, OffCentreRow)].AccessibleName);

        var filled = entries[(0, 0)];
        Assert.Equal(
            $"{AnalysisMetricLabels.Label(filled.X)} against {AnalysisMetricLabels.Label(filled.Y)}, "
            + "r -0.55, 40 frames",
            filled.AccessibleName);
    }

    // ---- the label's ink is chosen per cell by contrast ---------------------------------------------

    // The three r values the rule is asserted at, which are the ramp's own three stops: at -1, 0
    // and +1 the interpolation returns the stop colour exactly, so the expected figures below are
    // the tokens themselves and no interpolation rounding rides on them.
    //
    // Each row is (theme, r, the label ink expected to win, the contrast ratio it achieves). The
    // ratios were computed from the shipped token values with the WCAG 2.x formula and are
    // asserted to two decimals, so this table and the report's table cannot drift apart: if the
    // shipped tokens move, this case fails by name rather than the report going quietly stale.
    public static TheoryData<string, double, bool, double> LabelInkRows => new()
    {
        { "civil-dusk", -1d, true, 6.28d },
        { "civil-dusk", 0d, false, 11.07d },
        { "civil-dusk", 1d, true, 4.58d },
        { "luminance", -1d, true, 6.59d },
        { "luminance", 0d, false, 12.03d },
        { "luminance", 1d, true, 4.80d },
        { "red-light", -1d, true, 3.86d },
        { "red-light", 0d, false, 4.49d },
        { "red-light", 1d, true, 8.14d },
        { "deep-sky", -1d, true, 6.75d },
        { "deep-sky", 0d, false, 13.92d },
        { "deep-sky", 1d, true, 6.20d },
        { "atlas", -1d, true, 5.42d },
        { "atlas", 0d, false, 17.24d },
        { "atlas", 1d, true, 6.02d },
        { "logbook", -1d, true, 6.08d },
        { "logbook", 0d, false, 16.03d },
        { "logbook", 1d, true, 6.51d },
    };

    [AvaloniaTheory]
    [MemberData(nameof(LabelInkRows))]
    public void TheLabelInk_IsTheHigherContrastOfTheTwo_InEveryTheme(
        string theme, double r, bool expectDark, double expectedRatio)
    {
        // Fix pass 2. Spec 13's Matrix row put every label in text-primary. On red-light that is a
        // contrast of 1.08 against the negative end of the ramp, in the one theme where ruling
        // A25 makes the printed r the ONLY carrier of the sign; on luminance the negative
        // end is a pale blue under near-white text, which is the same defect in the other
        // direction. The rule is therefore general and not one theme's patch.
        //
        // Red against a view-model that keeps one ink for every cell: twelve of these eighteen rows pick
        // the other one, and the ratios the single ink achieves are 1.16 to 2.50 where the rule
        // achieves 3.86 to 13.92.
        try
        {
            ThemeManager.Apply(theme);

            using var grid = new MatrixChartViewModel((_, _) => { });
            var cell = grid.RampColourAt(r);
            var light = ChartTheme.Read("ColorTextPrimary", ChartTheme.Fallback);
            var dark = ChartTheme.Read("ColorBgElevated", ChartTheme.Fallback).WithAlpha(0xFF);

            // Not vacuous: the ramp really did resolve, rather than falling back to the neutral
            // grey a token read off the UI thread would have given.
            Assert.NotEqual(ChartTheme.Fallback, cell);

            var chosen = InkContrast.Choose(cell, light, dark);
            Assert.Equal(expectDark ? dark : light, chosen);

            // The chosen ink is the better of the two, always.
            var lightRatio = InkContrast.Contrast(light, cell);
            var darkRatio = InkContrast.Contrast(dark, cell);
            var best = Math.Max(lightRatio, darkRatio);
            Assert.Equal(best, InkContrast.Contrast(chosen, cell), 6);
            Assert.Equal(expectedRatio, best, 2);

            // And it clears WCAG AA wherever either ink can reach it. Two of the eighteen rows cannot,
            // both on red-light, where every ink in the theme is a shade of one red; there the
            // rule promises only the better of the two, which the assertion above already pinned.
            if (best >= 4.5d)
            {
                Assert.True(
                    InkContrast.Contrast(chosen, cell) >= 4.5d,
                    $"{theme} at r={r} reached only {best:F2} to 1.");
            }
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    [AvaloniaFact]
    public async Task EveryDrawnCell_IsInExactlyOneHalf_AndTheBlanksAreInNeither()
    {
        // Fix pass 2, the partition. A cell drawn twice would paint over itself in two label inks
        // and a cell drawn in neither half would vanish, so this asserts the exact sets rather
        // than the two counts.
        //
        // Red against a partition that adds every cell to both halves, and red against one that
        // drops the losing half.
        var tab = await Loaded(WithBlanks());
        var halves = Halves(tab);

        var light = Points(halves[0]).Select(p => ((int)p.X!.Value, (int)p.Y!.Value)).ToHashSet();
        var dark = Points(halves[1]).Select(p => ((int)p.X!.Value, (int)p.Y!.Value)).ToHashSet();

        Assert.Empty(light.Intersect(dark));
        Assert.Equal(100 - Blanks.Length, light.Count + dark.Count);

        var union = light.Union(dark).ToHashSet();
        var expectedDrawn = tab.Chart!.Cells
            .Where(cell => cell.HasValue)
            .Select(cell => (cell.Column, cell.Row))
            .ToHashSet();
        Assert.Equal(expectedDrawn, union);

        // The blanks are in neither, which is spec 12.14 departure 3 unchanged by the partition.
        foreach (var pair in Blanks)
        {
            Assert.DoesNotContain(Position(pair.X, pair.Y), union);
        }

        // The two halves carry DIFFERENT label paints and everything else the same, which is the
        // whole reason there are two of them.
        Assert.NotEqual(Ink(halves[0]), Ink(halves[1]));
        Assert.Equal(halves[0].DataLabelsSize, halves[1].DataLabelsSize);
        Assert.Equal(halves[0].ColorStops, halves[1].ColorStops);

        // Load bearing, and pinned because nothing else would notice if it changed: the cartesian
        // engine divides a category slot only among BAR series, so two heat series share one slot
        // rather than being offset against each other into half-width cells.
        Assert.IsNotAssignableFrom<IBarSeries>(halves[0]);
    }

    private static SKColor Ink(HeatSeries<WeightedPoint> half)
        => Assert.IsType<SolidColorPaint>(half.DataLabelsPaint).Color;

    [AvaloniaFact]
    public async Task ACellOfTheSecondHalf_ResolvesForAPressAndForAHover()
    {
        // Fix pass 2. The partition is the one thing that could have broken the click and the
        // hover: both resolve by the DRAWN COORDINATE the series build wrote, never by a point's
        // index within its series, so a cell in the second half maps exactly as one in the first.
        //
        // Red against a Resolve rewritten to index into a single Values list: a second-half cell
        // resolves to the wrong pair or to nothing.
        var opened = new List<(AnalysisMetric X, AnalysisMetric Y)>();
        var tab = await Loaded(Full(r: 0.9d, points: 61), (x, y) => opened.Add((x, y)));

        // At r = 0.9 the cells sit near the positive end of the ramp, which is where the dark ink
        // wins in all three themes, so the whole grid is in the SECOND half.
        var halves = Halves(tab);
        Assert.Empty(Points(halves[0]));
        Assert.Equal(100, Points(halves[1]).Count);

        var cell = tab.Chart!.Resolve(OffCentreColumn, OffCentreRow);
        Assert.NotNull(cell);

        tab.Chart.OpenCell(OffCentreColumn, OffCentreRow);
        Assert.Equal(
            (AnalysisMetrics.X[OffCentreColumn],
                AnalysisMetrics.Y[AnalysisMetrics.Y.Count - 1 - OffCentreRow]),
            Assert.Single(opened));

        Assert.Equal(
            "r=0.900 (N=61)",
            tab.Chart.Hover(new Coordinate(OffCentreColumn, OffCentreRow, 0.9d)));
    }

    [AvaloniaFact]
    public async Task AThemeSwap_RePartitionsTheCells_AndBothLabelPaintsReRead()
    {
        // Fix pass 2. The winning ink is a property of the theme as much as of the r, so a swap
        // can move a cell from one half to the other, and both paints have to come from the new
        // theme's tokens. Red against a partition computed once and kept, and red against label
        // paints built from a captured ink.
        try
        {
            ThemeManager.Apply("luminance");

            // r = 0 is the ramp's neutral stop, where the LIGHT ink wins in every theme, and
            // r = 1 is the positive end, where the DARK ink does. One of each, so the case sees a
            // real partition rather than an all-or-nothing one.
            var tab = await Loaded(Grid((_, y) => (y == AnalysisMetric.Hfr ? 1d : 0d, 40)));

            var before = Halves(tab);
            Assert.Equal(90, Points(before[0]).Count);
            Assert.Equal(10, Points(before[1]).Count);

            var lightBefore = ChartTheme.Read("ColorTextPrimary", ChartTheme.Fallback);
            var darkBefore = ChartTheme.Read("ColorBgElevated", ChartTheme.Fallback).WithAlpha(0xFF);
            Assert.Equal(lightBefore, Ink(before[0]));
            Assert.Equal(darkBefore, Ink(before[1]));

            ThemeManager.Apply("deep-sky");
            Dispatcher.UIThread.RunJobs();

            var after = Halves(tab);
            var lightAfter = ChartTheme.Read("ColorTextPrimary", ChartTheme.Fallback);
            var darkAfter = ChartTheme.Read("ColorBgElevated", ChartTheme.Fallback).WithAlpha(0xFF);

            // Not vacuous: the two themes really do carry different inks here.
            Assert.NotEqual(lightBefore, lightAfter);
            Assert.NotEqual(darkBefore, darkAfter);

            Assert.Equal(lightAfter, Ink(after[0]));
            Assert.Equal(darkAfter, Ink(after[1]));

            // The partition is recomputed rather than carried over, and the hundred cells are
            // still hundred: every one is in exactly one half after the swap too.
            Assert.Equal(100, Points(after[0]).Count + Points(after[1]).Count);
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    // ---- a focused cell draws its box, taken as one change ---------------------------------------

    [AvaloniaFact]
    public async Task FocusingACell_PublishesOneSectionOverThatCell_AndLosingFocusRemovesIt()
    {
        // Before this the focused cell drew nothing at all: the cell
        // button carries Classes="quiet", which sets BorderThickness to 0, and a zero-thickness
        // border paints nothing whatever brush it holds, so one of a hundred stops was
        // indistinguishable from the other ninety-nine.
        //
        // The box is a RectangularSection half a cell either way, which puts it in SERIES space.
        // That is why there is no second case at 1280 by 800 and at 1024 by 700: a section is
        // positioned by the axes and not by pixels, so it lands on the drawn cell at every window
        // size and whatever the axis label gutters do. The overlay the buttons live on is a
        // different rectangle at a different origin and pitch, which is what made a ring drawn
        // there land on a neighbouring cell.
        //
        // Red against a view-model that records focus and publishes nothing: Sections stays empty.
        var tab = await Loaded(Full());
        var grid = tab.Chart!;

        Assert.Empty(grid.Sections);

        var cell = grid.Cells.Single(
            entry => entry.Column == OffCentreColumn && entry.Row == OffCentreRow);
        grid.SetFocus(cell);

        var section = Assert.Single(grid.Sections);
        Assert.Equal(OffCentreColumn - 0.5d, section.Xi);
        Assert.Equal(OffCentreColumn + 0.5d, section.Xj);
        Assert.Equal(OffCentreRow - 0.5d, section.Yi);
        Assert.Equal(OffCentreRow + 0.5d, section.Yj);

        // A box and not a wash: a fill would tint the cell and change the r a reader sees.
        Assert.Null(section.Fill);
        Assert.NotNull(section.Stroke);

        grid.SetFocus(null);
        Assert.Empty(grid.Sections);
        Assert.Null(grid.FocusedCell);
    }

    [AvaloniaFact]
    public async Task TheFocusBox_IsPaintedFromItsToken_BeforeAndAfterAThemeSwap()
    {
        // The box is ColorAccent, the application's own focus and selection ink. On red-light the
        // ramp runs continuously from ColorInfo through ColorBgElevated to ColorMetricWorst, which
        // is the whole of that theme's lightness range, so no opaque token separates from every
        // cell; what makes the box read is that PointPadding leaves a four pixel gutter between
        // neighbours and a two pixel stroke centred on the boundary sits in it, over the chart
        // background rather than over a cell's ink.
        //
        // Red against a literal colour, and red against a grid that does not re-read on a swap.
        try
        {
            ThemeManager.Apply("luminance");
            var tab = await Loaded(Full());
            var grid = tab.Chart!;
            grid.SetFocus(grid.Cells[0]);

            var before = ChartTheme.Read("ColorAccent", ChartTheme.Fallback);
            Assert.Equal(before, Stroke(grid));

            ThemeManager.Apply("red-light");
            Dispatcher.UIThread.RunJobs();

            // The box is not set again: a theme swap re-reads the inks and republishes the series
            // and the focus section without rebuilding the entries, so the focused cell and its box
            // both survive and the box is repainted from a live read of the token.
            var after = ChartTheme.Read("ColorAccent", ChartTheme.Fallback);
            Assert.NotEqual(before, after);
            Assert.NotEqual(ChartTheme.Fallback, after);
            Assert.Equal(after, Stroke(grid));
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    private static SKColor Stroke(MatrixChartViewModel grid)
        => Assert.IsType<SolidColorPaint>(Assert.Single(grid.Sections).Stroke).Color;

    [AvaloniaFact]
    public async Task ANewResult_DropsTheFocusBox()
    {
        // The entries a rebuild publishes are new objects, so a box left over from the old ones
        // would sit on a cell that no longer exists. Red against a Rebuild that keeps FocusedCell:
        // the section survives a result that replaced every cell under it.
        var result = Full();
        var tab = Tab(_ => result);
        tab.IsVisible = true;
        await tab.PendingLoad!;
        Dispatcher.UIThread.RunJobs();

        tab.Chart!.SetFocus(tab.Chart.Cells[0]);
        Assert.Single(tab.Chart.Sections);

        result = Full(r: -0.2d);
        tab.MarkStale();
        tab.Refresh();
        await tab.PendingLoad!;
        Dispatcher.UIThread.RunJobs();

        Assert.Null(tab.Chart.FocusedCell);
        Assert.Empty(tab.Chart.Sections);
    }

    // ---- the tab's own members ------------------------------------------------------------------

    [AvaloniaFact]
    public async Task ThePlateScaleCount_RidesTheResult_AndTheWarningIsTheBasesOwn()
    {
        // User ruling U3 and section 0: this tab answers IsPixelMetric true, because the grid's
        // hfr row is present whenever the grid is, and copies the result's count onto the base.
        // The sentence itself is never respelled here.
        var tab = await Loaded(Full(plateScales: 2));

        Assert.Equal(2, tab.DistinctPlateScales);
        Assert.True(tab.PlateScaleWarningVisible);
    }

    [AvaloniaFact]
    public async Task ARefresh_ReusesTheOneGrid_AndDisposingTheTabDisposesIt()
    {
        // The grid is the tab's body, so it is built once and kept, and its ChartTheme.Changed
        // token goes with it: ChartTheme.Changed is static and a handler left attached pins the
        // view-model for the life of the process (ChartTheme.cs:117).
        var tab = await Loaded(Full());
        var grid = tab.Chart!;

        tab.MarkStale();
        tab.Refresh();
        await tab.PendingLoad!;
        Dispatcher.UIThread.RunJobs();

        Assert.Same(grid, tab.Chart);

        tab.Dispose();
        var before = grid.Series;
        ThemeManager.Apply("deep-sky");
        ThemeManager.Apply(ThemeManager.Available[0]);
        Assert.Same(before, grid.Series);
    }
}
