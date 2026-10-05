using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.Views;
using GalactiLog.App.Views.Analysis;
using GalactiLog.Data.Queries;
using LiveChartsCore.SkiaSharpView.Avalonia;
using Xunit;
using static GalactiLog.App.Tests.TestSupport.AnalysisShellHarness;

namespace GalactiLog.App.Tests.Views.Analysis;

/// <summary>
/// Spec 12.14's Matrix tab markup, measured in the page's real allotment (task5.md cases 23 to 27
/// as they touch this tab).
/// </summary>
/// <remarks>
/// <para>
/// The harness is the shell and not a bare window, for the reason <c>task4.md</c> section 10.1
/// gives: <c>MainWindowViewModel.NavRailExpandedWidth</c> is 200 and the status bar takes its own
/// row, so at 1280 by 800 the page has about 1080 by 720 and at the 1280 by 720 minimum about 1080
/// by 640. A view put straight into a <c>new Window { Width = 1280 }</c> would be handed the full
/// width and the fixed-width red proof would never fire.
/// </para>
/// <para>
/// Every page here is built with <c>UiPost.Default</c>, which is the ruling this task inherits:
/// these cases SHOW a bound view, the markup binds <c>State</c>, <c>StatusLine</c> and
/// <c>PlateScaleWarningVisible</c>, and a publish raised from the query's own thread-pool thread
/// is an order the application can never produce.
/// </para>
/// </remarks>
public class MatrixTabViewTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var page in _disposables)
        {
            page.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // ---- harness ------------------------------------------------------------------------------

    private (Window Window, MatrixTabView Body, AnalysisViewModel Page) ShowMatrix(
        double width = 1280d,
        double height = 800d,
        Func<AnalysisFilter, MatrixResult>? matrix = null)
    {
        // The tab strip's own route, so the case exercises the shipped selection and not a setter
        // only a test knows about.
        var (window, view, page) = AnalysisShellHarness.Show(
            _disposables,
            width,
            height,
            AnalysisShellHarness.Page(matrix: matrix),
            shown => shown.Tabs.Single(tab => tab.Key == "matrix"));

        return (window, Body<MatrixTabView>(view), page);
    }

    // ---- case 23 --------------------------------------------------------------------------------

    [Fact]
    public void TheMarkup_DeclaresItsDataType_AndAddsNoHeadingGlyphOrCalloutOfItsOwn()
    {
        // Case 23. A plain text scan, the shape ControlStyleScanTest and the result region census
        // already take. ControlStyleScanTest fails the build on a local Button, Border.tag or
        // Border.callout style, so what is asserted here is the half it does not cover: a compiled
        // binding needs x:DataType, and ruling P1-4 puts all seven help placements and all five
        // heading rows in AnalysisView.axaml, which this file may not duplicate.
        var text = File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "Views", "Analysis", "MatrixTabView.axaml"));

        Assert.Contains("x:DataType=\"analysis:MatrixTabViewModel\"", text, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"analysis:MatrixCellViewModel\"", text, StringComparison.Ordinal);

        // Task 7's glyphs and the base's three shared rows are elsewhere: a copy here would put
        // two of the seven placements in a file Task 7 may not open, and would draw the callout
        // and the state sentence a second time.
        Assert.DoesNotContain("HelpButton", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Classes=\"callout", text, StringComparison.Ordinal);
        Assert.DoesNotContain("t-label section", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RetryCommand", text, StringComparison.Ordinal);
    }

    // ---- case 24 --------------------------------------------------------------------------------

    [AvaloniaFact]
    public void TheLegendsTwoSwatches_TakeTheRampsOwnThemeTokens()
    {
        // Case 24. Red against a hex literal: ThemeResourceTest's own idiom is the precedent, and
        // a literal would not follow a theme swap while the ramp it names does.
        var (_, body, _) = ShowMatrix();

        var negative = Named<Border>(body, "NegativeSwatch");
        var positive = Named<Border>(body, "PositiveSwatch");

        Assert.Equal(ChartTheme.Read("ColorInfo", ChartTheme.Fallback), Resolved(negative));
        Assert.Equal(ChartTheme.Read("ColorMetricWorst", ChartTheme.Fallback), Resolved(positive));

        try
        {
            ThemeManager.Apply("deep-sky");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ChartTheme.Read("ColorInfo", ChartTheme.Fallback), Resolved(negative));
            Assert.Equal(ChartTheme.Read("ColorMetricWorst", ChartTheme.Fallback), Resolved(positive));
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static SkiaSharp.SKColor Resolved(Border swatch)
    {
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(swatch.Background);
        return new SkiaSharp.SKColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A);
    }

    // ---- case 25 --------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(1280d, 800d)]
    [InlineData(1280d, 720d)]
    public void TheGridItsLabelsAndItsLegend_FitWithNoHorizontalPageScroll(double width, double height)
    {
        // Case 25, the Matrix half. The grid draws its own axis labels inside the chart control, so
        // the control fitting is the labels fitting. Red against a fixed chart width: the extent
        // passes the viewport and the legend or the chart lands outside.
        var (window, body, _) = ShowMatrix(width, height);
        var region = window.GetControl<ContentControl>("ContentRegion");
        var scroller = ScrollAssertions.Scroller(
            region.GetVisualDescendants().OfType<AnalysisView>().Single());

        // The allotment really is the page's and not the window's, or the case would be measuring
        // something the reader never sees.
        Assert.True(region.Bounds.Width > 0d && region.Bounds.Width < width);

        Assert.True(
            IsHorizontallyInView(Named<CartesianChart>(body, "MatrixChart"), scroller),
            $"The grid is outside the viewport at {width} by {height}.");
        Assert.True(
            IsHorizontallyInView(Named<WrapPanel>(body, "MatrixLegend"), scroller),
            $"The legend is outside the viewport at {width} by {height}.");
        Assert.True(
            IsHorizontallyInView(Named<TextBlock>(body, "MatrixCaption"), scroller),
            $"The caption is outside the viewport at {width} by {height}.");

        Assert.True(
            scroller.Extent.Width <= scroller.Viewport.Width,
            $"Extent {scroller.Extent.Width} passed viewport {scroller.Viewport.Width} at {width} by {height}.");
    }

    // ---- case 27 --------------------------------------------------------------------------------

    [AvaloniaFact]
    public void TheCaptionAndTheLegend_SurviveANoRowsResult_AndTheResultRegionDoesNot()
    {
        // Case 27. Red against a legend or a caption drawn inside the region: a reader whose
        // filters emptied the grid would lose the one sentence that tells them why a cell is blank
        // and what a click would do.
        var cells = 0;
        var (_, body, page) = ShowMatrix(
            matrix: _ => cells == 0
                ? new MatrixResult([], 0)
                : AnalysisViewModelTestFactory.Matrix());

        var region = Named<Grid>(body, "ResultRegion");

        Assert.Equal(AnalysisTabState.Empty, page.SelectedTab.State);
        Assert.False(region.IsEffectivelyVisible);

        Assert.True(Named<TextBlock>(body, "MatrixCaption").IsEffectivelyVisible);
        Assert.True(Named<WrapPanel>(body, "MatrixLegend").IsEffectivelyVisible);
        Assert.True(Named<TextBlock>(body, "MatrixCaption").IsEffectivelyEnabled);
        Assert.True(Named<WrapPanel>(body, "MatrixLegend").IsEffectivelyEnabled);
        Assert.Equal(MatrixTabViewModel.CaptionText, Named<TextBlock>(body, "MatrixCaption").Text);

        // And the region comes back with a result, so the gate is a gate and not a permanent hide.
        cells = 1;
        page.SharedFilter.SelectedFilter = "Ha";
        AnalysisSettle.Page(page);

        Assert.Equal(AnalysisTabState.Ready, page.SelectedTab.State);
        Assert.True(region.IsEffectivelyVisible);
    }

    // ---- the keyboard route -----------------------------------------------------------------------

    private static List<Button> CellButtons(MatrixTabView body)
        => [.. Named<ItemsControl>(body, "MatrixCellKeys").GetVisualDescendants().OfType<Button>()];

    // The real Tab walk, which is the only thing that answers "is this reachable by keyboard".
    // Programmatic Focus() succeeds on elements Tab can never reach, so it answers nothing.
    // The idiom is KeyboardNavigationHandler.GetNext, walked until it returns
    // to where it started or runs out.
    private static List<IInputElement> TabOrder(Window window)
    {
        var stops = new List<IInputElement>();
        IInputElement? current = window;
        for (var step = 0; step < 500; step++)
        {
            current = KeyboardNavigationHandler.GetNext(current!, NavigationDirection.Next);
            if (current is null || stops.Contains(current))
            {
                break;
            }

            stops.Add(current);
        }

        return stops;
    }

    [AvaloniaFact]
    public void TheWholeGrid_IsOneTabStop_AndTheWalkLeavesItOnTheNextTab()
    {
        // Measured before this fix: the page offered 118 tab stops and 100 of them
        // were matrix cells, so a reader who visited the Matrix and then wanted the tab strip or
        // the filter bar pressed Tab a hundred times, and a screen reader announced a hundred
        // cells to cross one chart. Controls/AltitudeArc.cs already ships the answer for a drawn
        // control with many addressable parts: one stop, arrows inside.
        //
        // Red against the layer with no TabNavigation="Once": the count reads 100.
        var (window, body, _) = ShowMatrix();

        var cells = CellButtons(body);
        Assert.Equal(100, cells.Count);

        var stops = TabOrder(window);
        var inGrid = stops.OfType<Button>().Where(cells.Contains).ToList();

        Assert.Single(inGrid);

        // And it really is in the walk, so "one stop" is not "no stop": the grid is reachable.
        Assert.Contains(cells[0], stops.OfType<Button>());

        // The stop after the grid's one is not another cell, which is the half that says the walk
        // LEAVES rather than being trapped.
        var index = stops.IndexOf(inGrid[0]);
        Assert.True(index >= 0);
        if (index + 1 < stops.Count)
        {
            Assert.DoesNotContain(stops[index + 1], cells);
        }
    }

    [AvaloniaTheory]
    [InlineData(Key.Right, 1)]
    [InlineData(Key.Left, -1)]
    [InlineData(Key.Down, 10)]
    [InlineData(Key.Up, -10)]
    public void AnArrow_MovesFocusOneCell(Key key, int delta)
    {
        // The arrow handler, driven through the real key pipeline. Left and right
        // step one column, up and down one row, which is AltitudeArc's own shape widened to two
        // dimensions. Red against a handler that steps by the wrong stride: Down lands on the
        // neighbouring column instead of the row below.
        var (window, body, _) = ShowMatrix();
        var cells = CellButtons(body);

        // Start in the middle, so every one of the four directions has somewhere to go.
        const int Start = 55;
        cells[Start].Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        Assert.True(cells[Start].IsFocused);

        window.KeyPressQwerty(PhysicalKey(key), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(cells[Start + delta].IsFocused, $"{key} did not move focus by {delta}.");
    }

    [AvaloniaTheory]
    [InlineData(Key.Left, 40)]
    [InlineData(Key.Right, 49)]
    [InlineData(Key.Up, 4)]
    [InlineData(Key.Down, 94)]
    public void AnArrowAtAnEdge_StopsRatherThanWrapping(Key key, int start)
    {
        // The edges stop: a left arrow in column 0 must not land on the previous row's last cell,
        // which would read as a jump across the grid, and a down arrow in the bottom row must not
        // leave the grid. Red against an unclamped index: focus moves and the assertion names the
        // direction.
        var (window, body, _) = ShowMatrix();
        var cells = CellButtons(body);

        cells[start].Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey(key), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(cells[start].IsFocused, $"{key} moved focus off the edge at index {start}.");
    }

    private static Avalonia.Input.PhysicalKey PhysicalKey(Key key) => key switch
    {
        Key.Left => Avalonia.Input.PhysicalKey.ArrowLeft,
        Key.Right => Avalonia.Input.PhysicalKey.ArrowRight,
        Key.Up => Avalonia.Input.PhysicalKey.ArrowUp,
        _ => Avalonia.Input.PhysicalKey.ArrowDown,
    };

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnterAndSpace_OnAFocusedCell_OpenItsPair(bool space)
    {
        // The old case called Command.Execute, which bypasses the Button entirely
        // and takes the same route the view-model case already takes, so it stayed green against a
        // button that handled neither key. This drives the real key pipeline, the idiom at
        // HelpButtonTests.cs:113 to :127.
        //
        // Red against IsTabStop="False" on the layer, which takes the button out of the walk the
        // case above asserts, and red against a button that swallows Enter: the page stays on the
        // Matrix.
        var (window, body, page) = ShowMatrix();
        var cells = CellButtons(body);
        var tab = Assert.IsType<MatrixTabViewModel>(page.SelectedTab);

        // The top left button is the grid's top left cell: the entry list is built rows descending
        // so the layer, which fills left to right and top to bottom, sits square with the drawn
        // grid rather than upside down.
        var topLeft = tab.Chart!.Cells[0];
        Assert.Equal(AnalysisMetrics.X[0], topLeft.X);
        Assert.Equal(AnalysisMetrics.Y[0], topLeft.Y);
        Assert.Equal(
            topLeft.AccessibleName,
            Avalonia.Automation.AutomationProperties.GetName(cells[0]));

        cells[0].Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();

        // The focus box follows the focused cell, drawn in the chart's own coordinate space.
        Assert.Same(topLeft, tab.Chart.FocusedCell);
        Assert.Single(tab.Chart.Sections);

        var key = space ? Avalonia.Input.PhysicalKey.Space : Avalonia.Input.PhysicalKey.Enter;
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
        AnalysisSettle.Page(page);

        Assert.Equal("correlation", page.SelectedTab.Key);
    }

    [AvaloniaFact]
    public void AThemeSwap_LeavesTheFocusedCellFocused_AndItsBoxDrawn()
    {
        // A swap moves no data, so it must not move the reader. It used to run the whole data
        // rebuild: the hundred entries were replaced, this ItemsControl regenerated the hundred
        // buttons, and the focused button went away, so focus fell back to the page root and a
        // reader three arrow presses into the grid was put back at the top of the page with no
        // announcement.
        //
        // Red against a theme handler that rebuilds the cells: the button instance is gone, nothing
        // in the grid is focused, and the chart's box is empty.
        var (_, body, page) = ShowMatrix();
        var tab = Assert.IsType<MatrixTabViewModel>(page.SelectedTab);

        const int Middle = 55;
        var before = CellButtons(body);
        before[Middle].Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        Assert.True(before[Middle].IsFocused);
        Assert.Single(tab.Chart!.Sections);

        try
        {
            ThemeManager.Apply("deep-sky");
            Dispatcher.UIThread.RunJobs();

            // The same hundred buttons, not a fresh hundred: this is what the reader's focus is
            // attached to.
            Assert.Equal(before, CellButtons(body));
            Assert.True(before[Middle].IsFocused, "The theme swap took keyboard focus off the cell.");
            Assert.Same(tab.Chart.Cells[Middle], tab.Chart.FocusedCell);
            Assert.Single(tab.Chart.Sections);
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void TheLayerIsNotHitTestVisible_SoThePointerStillReachesTheChart()
    {
        // Red against a layer that is hit-test visible: it would swallow the pointer press the
        // chart's own DataPointerDownCommand needs and the pointer route would go dead.
        var (_, body, _) = ShowMatrix();

        Assert.False(Named<ItemsControl>(body, "MatrixCellKeys").IsHitTestVisible);
    }

    // ---- the layer sits on the painting ----------------------------------------------------------

    // The rectangle the chart actually paints its cells in, which is what the layer must cover.
    private static (double X, double Y, double Width, double Height) DrawMargin(CartesianChart chart)
    {
        var core = (LiveChartsCore.Chart)chart.CoreChart;
        return (core.DrawMarginLocation.X, core.DrawMarginLocation.Y,
            core.DrawMarginSize.Width, core.DrawMarginSize.Height);
    }

    /// <summary>
    /// Waits until the LIBRARY has measured to a draw margin <paramref name="reached"/> accepts,
    /// then drains the dispatcher once more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// rc5.4 measures on its own throttle rather than in the layout pass, and any property change
    /// arriving at the chart restarts that throttle: the application-wide <c>Foreground</c> the
    /// Window style now sets is inherited by the chart and does exactly that, so the harness can
    /// return before the first measure has run at all. That made this case pass alone and fail
    /// about one run in two among the other chart classes.
    /// </para>
    /// <para>
    /// The condition is on the library's own state and NEVER on the padding the assertions read, so
    /// nothing here can settle on a stale value or make the assertion vacuous: the case still fails
    /// if the view writes the wrong inset or none. The extra drain is what delivers the view's
    /// write, which is posted from inside the measure that raised the margin.
    /// </para>
    /// </remarks>
    private static async Task Measured(CartesianChart chart, Func<double, bool> reached, string what)
    {
        for (var step = 0; step < 120 && !reached(DrawMargin(chart).Width); step++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(reached(DrawMargin(chart).Width), what);
    }

    [AvaloniaFact]
    public async Task TheKeyboardLayer_CoversTheChartsDrawMargin_AndFollowsAResize()
    {
        // The launched look measured a focused cell painted at screen x 660 to 832 while the layer
        // reported it at 554 to 722, a whole column to the left, because the layer was spread over
        // the control while the painting starts a row-label gutter in. Pointer clicks were right
        // and only the accessibility layer and its adorner were displaced, so a screen reader or a
        // magnifier was pointed at the wrong cell.
        //
        // Red against a layer with no inset: the padding reads 0,0,0,0 while the draw margin is
        // inset by about a hundred pixels on the left.
        var (window, body, _) = ShowMatrix();
        var chart = Named<CartesianChart>(body, "MatrixChart");
        var layer = Named<ItemsControl>(body, "MatrixCellKeys");

        void AssertCovers(string when)
        {
            var margin = DrawMargin(chart);
            var first = CellButtons(body)[0];

            // Not vacuous: the gutters really are there, so an inset of nothing would be wrong,
            // and it is the LEFT one the look measured a cell against.
            Assert.True(margin.X > 1d, $"{when}: the draw margin starts at {margin.X}.");
            Assert.True(margin.Width > 1d && margin.Height > 1d, $"{when}: the draw margin is empty.");

            // The inset itself, as the view writes it.
            Assert.Equal(margin.X, layer.Padding.Left, 1);
            Assert.Equal(margin.Y, layer.Padding.Top, 1);
            Assert.Equal(chart.Bounds.Width - margin.X - margin.Width, layer.Padding.Right, 1);
            Assert.Equal(chart.Bounds.Height - margin.Y - margin.Height, layer.Padding.Bottom, 1);

            // And what it is for: the top left cell button starts where the painting starts and
            // steps at the painting's own pitch, which is what a screen reader and a magnifier
            // read off it. Within a pixel, because Avalonia rounds a layout to whole device pixels
            // while the library's margin is a float; the defect this case exists for was a
            // HUNDRED pixels.
            var at = first.TranslatePoint(default, chart)!.Value;
            Assert.InRange(at.X, margin.X - 1d, margin.X + 1d);
            Assert.InRange(at.Y, margin.Y - 1d, margin.Y + 1d);
            Assert.InRange(
                first.Bounds.Width,
                (margin.Width / MatrixChartViewModel.Columns.Count) - 1d,
                (margin.Width / MatrixChartViewModel.Columns.Count) + 1d);
            Assert.InRange(
                first.Bounds.Height,
                (margin.Height / MatrixChartViewModel.Rows.Count) - 1d,
                (margin.Height / MatrixChartViewModel.Rows.Count) + 1d);
        }

        await Measured(chart, width => width > 0d, "The chart never measured at 1280 by 800.");
        AssertCovers("at 1280 by 800");

        var beforeWidth = DrawMargin(chart).Width;
        window.Width = 1560d;

        await Measured(
            chart,
            width => width > beforeWidth,
            $"The draw margin stayed {beforeWidth} wide after the resize.");
        AssertCovers("after the resize");
    }

    [AvaloniaFact]
    public void ACellButton_DrawsNoFocusAdornerOfItsOwn()
    {
        // The chart's RectangularSection is the grid's one focus affordance. The platform's default
        // adorner is a plain white rectangle in all three themes, so a focused cell drew two boxes
        // and on red-light one of them was the brightest thing on the page.
        //
        // The adorner is a live control in the window's adorner layer rather than a property
        // reading, and it exists only while a cell is focused, so the case reads it there. An
        // earlier version asserted a null FocusAdorner and was VACUOUS: a null is exactly what
        // makes the platform reach for its own default, which is the white Border.
        //
        // Red against the button with no FocusAdorner of its own, or with one set to {x:Null}: the
        // adorner layer holds a Border sized to the cell.
        var (_, body, _) = ShowMatrix();
        var cells = CellButtons(body);

        const int Middle = 55;
        cells[Middle].Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        Assert.True(cells[Middle].IsFocused);

        var layer = AdornerLayer.GetAdornerLayer(cells[Middle]);
        Assert.NotNull(layer);

        // Not vacuous: focus really did adorn the cell, so "nothing is drawn" is a statement about
        // what the adorner is and not about there being none.
        Assert.NotEmpty(layer.Children);
        Assert.All(layer.Children, child =>
        {
            var empty = Assert.IsType<Panel>(child);
            Assert.Null(empty.Background);
            Assert.Empty(empty.Children);
        });
    }

    [AvaloniaFact]
    public void ACellWithNoValue_TakesFocusAndCarriesItsName_AndEnterOpensNothing()
    {
        // The ruling on the blank cell: it is FOCUSABLE, so the arrows reach it and a
        // screen reader announces "no data" rather than skipping the position in silence, and it
        // is INERT, which is spec 12.14's "a cell with no r is not clickable".
        //
        // Red against IsEnabled bound to HasValue, which is what shipped: the button is disabled,
        // Focus does not take, and the name is never announced.
        var (window, body, page) = ShowMatrix(
            matrix: _ => new MatrixResult(
                [
                    .. AnalysisMetrics.X.SelectMany(x => AnalysisMetrics.Y.Select(
                        y => new MatrixCell(x, y, null, 400))),
                ],
                1));

        var tab = Assert.IsType<MatrixTabViewModel>(page.SelectedTab);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.DoesNotContain(tab.Chart!.Cells, cell => cell.HasValue);

        var cells = CellButtons(body);
        Assert.Equal(100, cells.Count);
        Assert.All(cells, button => Assert.True(button.IsEffectivelyEnabled));

        cells[0].Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        Assert.True(cells[0].IsFocused);

        Assert.EndsWith(
            ", no data",
            Avalonia.Automation.AutomationProperties.GetName(cells[0]),
            StringComparison.Ordinal);

        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, RawInputModifiers.None);
        AnalysisSettle.Page(page);

        Assert.Equal("matrix", page.SelectedTab.Key);
    }
}
