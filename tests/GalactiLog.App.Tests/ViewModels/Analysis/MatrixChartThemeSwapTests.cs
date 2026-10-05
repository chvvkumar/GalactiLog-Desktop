using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.Data.Queries;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Analysis;

// What a theme swap does to the Matrix grid, and what it deliberately does NOT do.
//
// A swap used to run the whole data rebuild: all hundred entries were replaced, the view's keyboard
// layer regenerated all hundred buttons, and the focused button went away, so a reader three arrow
// presses into the grid was put back at the top of the page with no announcement over a change that
// moved no data. The inks are re-read and the two halves and the focus box are republished; the
// entries, which depend on no token, are left alone. A DATA rebuild still replaces them.
//
// The cases that the swap still re-partitions the cells and still re-reads both label paints and
// the ramp live in MatrixTabTests; this file is the half about what survives. The view's own half,
// that the focused BUTTON keeps keyboard focus, is in Views/Analysis/MatrixTabViewTests.
//
// AvaloniaFact throughout, for MatrixTabTests' own reason: ChartTheme.Read answers its documented
// neutral off the UI thread, so every assertion on a colour would compare the fallback with itself.
public class MatrixChartThemeSwapTests : IDisposable
{
    private readonly List<MatrixChartViewModel> _charts = [];

    public void Dispose()
    {
        foreach (var chart in _charts)
        {
            chart.Dispose();
        }

        // The static theme outlives every case, so it is put back whatever the case did.
        ThemeManager.Apply(ThemeManager.Available[0]);
        Dispatcher.UIThread.RunJobs();
        GC.SuppressFinalize(this);
    }

    // One of each half: r = 0 is the ramp's neutral stop, where the light ink wins in every theme,
    // and r = 1 is the positive end, where the dark ink does.
    private static IReadOnlyList<MatrixCell> Grid(double r = 0d, int points = 40)
        =>
        [
            .. AnalysisMetrics.X.SelectMany(x => AnalysisMetrics.Y.Select(
                y => new MatrixCell(x, y, y == AnalysisMetric.Hfr ? 1d : r, points))),
        ];

    private MatrixChartViewModel Loaded(IReadOnlyList<MatrixCell> cells)
    {
        var chart = new MatrixChartViewModel((_, _) => { });
        _charts.Add(chart);
        chart.Update(cells);
        return chart;
    }

    private static SKColor Stroke(MatrixChartViewModel chart)
        => Assert.IsType<SolidColorPaint>(Assert.Single(chart.Sections).Stroke).Color;

    [AvaloniaFact]
    public void AThemeSwap_KeepsTheCellInstances_AndRaisesNoCellsChange()
    {
        // The entries carry no token: a column, a row, two metrics, an r and a frame count. So
        // there is nothing in them for a swap to re-resolve, and replacing them costs the reader
        // their place in the grid. Reference equality is the whole claim, and the silent Cells
        // notification is the half the view acts on: an ItemsControl regenerates its items when the
        // bound collection is replaced, focused button included.
        //
        // Red against the shipped OnThemeChanged, which called Rebuild: both assertions fail.
        ThemeManager.Apply("luminance");
        Dispatcher.UIThread.RunJobs();

        var chart = Loaded(Grid());
        var before = chart.Cells;
        Assert.Equal(100, before.Count);

        var changed = new List<string?>();
        chart.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        ThemeManager.Apply("deep-sky");
        Dispatcher.UIThread.RunJobs();

        Assert.Same(before, chart.Cells);
        Assert.DoesNotContain(nameof(MatrixChartViewModel.Cells), changed);

        // Not vacuous: the swap really did reach this grid and republish what a token paints.
        Assert.Contains(nameof(MatrixChartViewModel.Series), changed);
    }

    [AvaloniaFact]
    public void AThemeSwap_KeepsTheFocusedCell_AndRepaintsItsBoxFromTheNewToken()
    {
        // The box has to survive with the cell under it, and it has to be painted from the theme
        // that is now current rather than from the ink it was built with. Red against the shipped
        // OnThemeChanged: FocusedCell is null after the swap and Sections is empty.
        ThemeManager.Apply("luminance");
        Dispatcher.UIThread.RunJobs();

        var chart = Loaded(Grid());
        var focused = chart.Cells[13];
        chart.SetFocus(focused);

        var before = ChartTheme.Read("ColorAccent", ChartTheme.Fallback);
        Assert.Equal(before, Stroke(chart));

        ThemeManager.Apply("red-light");
        Dispatcher.UIThread.RunJobs();

        Assert.Same(focused, chart.FocusedCell);

        // Still the same cell's box, in the series' own coordinate space.
        var section = Assert.Single(chart.Sections);
        Assert.Equal(focused.Column - 0.5d, section.Xi);
        Assert.Equal(focused.Row - 0.5d, section.Yi);

        // And painted from a live read: the two themes carry different accents, so the assertion
        // cannot pass against a box that kept its old paint.
        var after = ChartTheme.Read("ColorAccent", ChartTheme.Fallback);
        Assert.NotEqual(before, after);
        Assert.NotEqual(ChartTheme.Fallback, after);
        Assert.Equal(after, Stroke(chart));
    }

    [AvaloniaFact]
    public void AThemeSwap_RepublishesBothAxes_AndKeepsTheCellsAndTheFocus()
    {
        // The two halves of the ruling, pinned against each other in one case so neither can be
        // undone by the other: an axis instance the chart has already measured is never re-styled,
        // so the two axes must be REPLACED for their label and separator ink to follow the swap,
        // while Cells and the focused cell must SURVIVE it.
        //
        // Measured under rc5.4 rather than inferred: one hosted chart read #ffa8a49c off its X axis
        // under luminance and the same instance still read #ffa8a49c after a swap to
        // deep-sky, whose axis ink is #ff94a3b8. The library guards its axis styling with a theme
        // id and ChartTheme.Apply installs no new theme after the first call, so the id never moves.
        // That an axis built AFTER a swap takes the new ink is ChartPaintCensusTests' own case.
        //
        // Red two ways: against an OnThemeChanged that leaves the axes alone (the four axis
        // assertions), and against one that calls Rebuild (the cells and focus assertions).
        ThemeManager.Apply("luminance");
        Dispatcher.UIThread.RunJobs();

        var chart = Loaded(Grid());
        var focused = chart.Cells[13];
        chart.SetFocus(focused);

        var cells = chart.Cells;
        var x = Assert.IsType<Axis>(Assert.Single(chart.XAxes));
        var y = Assert.IsType<Axis>(Assert.Single(chart.YAxes));

        ThemeManager.Apply("deep-sky");
        Dispatcher.UIThread.RunJobs();

        // New instances, both sides.
        var swappedX = Assert.IsType<Axis>(Assert.Single(chart.XAxes));
        var swappedY = Assert.IsType<Axis>(Assert.Single(chart.YAxes));
        Assert.NotSame(x, swappedX);
        Assert.NotSame(y, swappedY);

        // Carrying the same labels in the same order, the row axis still over the REVERSED Y list.
        // Read off AnalysisMetrics rather than off the view-model's own Rows, so a build that
        // stopped reversing or that transposed the two axes fails here.
        Assert.Equal(
            AnalysisMetrics.X.Select(metric => AnalysisMetricLabels.For(metric).Matrix),
            swappedX.Labels);
        Assert.Equal(
            AnalysisMetrics.Y.Reverse().Select(metric => AnalysisMetricLabels.For(metric).Matrix),
            swappedY.Labels);

        // And the other half: the entries and the reader's place in the grid are untouched.
        Assert.Same(cells, chart.Cells);
        Assert.Same(focused, chart.FocusedCell);
        Assert.Single(chart.Sections);
    }

    [AvaloniaFact]
    public void AThemeSwapOnTheEmptyGrid_StillPublishesOneBareAxisPerSide()
    {
        // rc5.4's CartesianChartEngine.Measure throws "XAxes and YAxes must contain at least one
        // element", and the control measures while the result region is closed, so the empty state
        // publishes one BARE axis per side. A swap leaves that shape alone: nothing of the empty
        // state is on the page to take the new ink, and a labelled axis there would draw ten row
        // and column labels over an empty grid. Red against a republish placed ahead of the empty
        // check: both axes come back carrying the ten labels.
        ThemeManager.Apply("luminance");
        Dispatcher.UIThread.RunJobs();

        var chart = Loaded([]);
        Assert.Empty(chart.Cells);

        ThemeManager.Apply("deep-sky");
        Dispatcher.UIThread.RunJobs();

        foreach (var axis in new[]
                 {
                     Assert.IsType<Axis>(Assert.Single(chart.XAxes)),
                     Assert.IsType<Axis>(Assert.Single(chart.YAxes)),
                 })
        {
            Assert.True(
                axis.Labels is null || axis.Labels.Count == 0,
                $"The empty state's axis carries labels: {string.Join(", ", axis.Labels ?? [])}");
        }
    }

    [AvaloniaFact]
    public void ANewResult_StillReplacesTheCells_AndDropsTheFocus()
    {
        // The other half of the ruling, and the one a fix that went too far would break: on a DATA
        // rebuild the entries really are stale, because each one holds its own r and frame count,
        // so they are replaced and the focus goes with them. Red against a Rebuild that reuses the
        // entries when the shape matches: the same instances come back carrying the old figures.
        var chart = Loaded(Grid(r: 0d, points: 40));
        var before = chart.Cells;
        chart.SetFocus(before[13]);
        Assert.Single(chart.Sections);

        chart.Update(Grid(r: -0.4d, points: 61));

        Assert.NotSame(before, chart.Cells);
        Assert.All(chart.Cells, cell => Assert.DoesNotContain(cell, before));
        Assert.Null(chart.FocusedCell);
        Assert.Empty(chart.Sections);

        // The new figures really are the ones on the page, so "replaced" is not "re-listed".
        Assert.Equal(61, chart.Cells[13].NPoints);
    }
}
