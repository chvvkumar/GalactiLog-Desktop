using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.Views;
using GalactiLog.App.Views.Analysis;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Avalonia;
using LiveChartsCore.SkiaSharpView.Painting;
using Xunit;
using static GalactiLog.App.Tests.TestSupport.AnalysisShellHarness;

namespace GalactiLog.App.Tests.Views.Analysis;

/// <summary>
/// Task 5 section 3's markup cases for the Correlation tab: the mask against the chart's own
/// background (case 3), the vocabulary scan (case 23), the layout at both window sizes (case 25)
/// and the controls surviving an empty result (case 26).
/// </summary>
/// <remarks>
/// <para>
/// Every case here SHOWS a bound view, so every one of them takes the UI-thread post seam and
/// drains it with <c>Dispatcher.UIThread.RunJobs()</c>. The inline seam is for windowless cases, and
/// a publish raised from the query's own thread-pool thread into a live binding is the false green
/// the wiring half already shipped once.
/// </para>
/// <para>
/// Case 23 is scoped to <c>CorrelationTabView.axaml</c>. The Matrix half of the same rule belongs
/// to <c>MatrixTabViewTests.cs</c>, whose file lands with the Matrix tab; asserting it from here
/// would couple two implementers' green runs in one shared tree.
/// </para>
/// </remarks>
public class CorrelationTabViewTests : IDisposable
{
    private static readonly AnalysisFilter AnyFilter =
        new(null, null, null, AnalysisGranularity.Frame, null, null);

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var item in _disposables)
        {
            item.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // ---- harnesses ----------------------------------------------------------------------------

    private static CorrelationResult Result(bool rows)
        => new(
            rows
                ?
                [
                    new CorrelationPoint(1d, 2d, new DateOnly(2026, 3, 1), null, false),
                    new CorrelationPoint(3d, 6d, new DateOnly(2026, 3, 2), null, false),
                    new CorrelationPoint(5d, 11d, new DateOnly(2026, 3, 3), null, false),
                    new CorrelationPoint(9d, 40d, new DateOnly(2026, 3, 4), null, true),
                ]
                : [],
            rows
                ? new TrendLine(
                    2d, 0.5d, 0.6d, 0.77d, 0.71d,
                    [new BandPoint(1d, 3d), new BandPoint(9d, 23d)],
                    [new BandPoint(1d, 1d), new BandPoint(9d, 19d)])
                : null,
            rows ? new SummaryStats(4, 1d, 9d, 4.5d, 4d, 3.4d) : null,
            rows ? new SummaryStats(4, 2d, 40d, 14.75d, 8.5d, 17.5d) : null,
            new Dictionary<Guid, string>(),
            rows ? CorrelationQuality.Ok : CorrelationQuality.TooFewPoints,
            1,
            rows ? 4 : 0,
            rows ? 4 : 0);

    /// <summary>The tab alone in a window, which is all cases 3 and 26 need: neither asks anything
    /// about the page's allotment.</summary>
    private (Window Window, CorrelationTabView View, CorrelationTabViewModel Tab) ShowTab(bool rows)
    {
        var tab = new CorrelationTabViewModel(
            () => AnyFilter,
            (_, _, _) => Result(rows),
            post: AnalysisShellHarness.Post);
        _disposables.Add(tab);

        var view = new CorrelationTabView { DataContext = tab };
        var window = new Window { Width = 1000, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        tab.IsVisible = true;
        tab.PendingLoad?.Wait(TimeSpan.FromSeconds(30));
        Dispatcher.UIThread.RunJobs();

        return (window, view, tab);
    }

    /// <summary>
    /// The page in the real shell, which is the only harness that gives it the allotment case 25
    /// measures against: the shell's rail is 200 wide and the status bar takes its own row, so a
    /// bare window would hand the page the full width and the case would never fire.
    /// </summary>
    private (Window Window, AnalysisView View, AnalysisViewModel Page) ShowPage(double width, double height)
        => AnalysisShellHarness.Show(
            _disposables,
            width,
            height,
            AnalysisShellHarness.Page(correlation: (_, _, _) => Result(rows: true)));

    // ---- case 3: the mask and the chart's own background are the same colour --------------------

    // Ruling A23 names the bg-elevated token twice, in the mask series' fill and in the chart
    // control's Background, and the two must agree or the band renders against a colour it is not
    // masking. Read off the LOADED view's resolved brush, never by reading ChartTheme.Read twice,
    // which would assert that a value equals itself and could never go red.
    //
    // The guarantee is COLOUR equality plus a pinned alpha, not ARGB equality.
    // A23's other word is "opaquely", and deep-sky declares ColorBgElevated as
    // #E6181B26: a mask painted at the token's own alpha there passes about a tenth of the band's
    // fill through the half it exists to hide. Pinning the alpha is one channel of one colour, so
    // the mask and the card still cannot name two colours, which is what the three channels below
    // assert. Both themes are driven, because the pin is only observable in the second.
    [AvaloniaTheory]
    [InlineData("luminance")]
    [InlineData("deep-sky")]
    public void TheMaskFill_CarriesTheChartsResolvedBackgroundColour_AtFullAlpha(string themeId)
    {
        try
        {
            ThemeManager.Apply(themeId);

            var (_, view, tab) = ShowTab(rows: true);

            var chart = Named<CartesianChart>(view, "Chart");
            var background = Assert.IsAssignableFrom<ISolidColorBrush>(chart.Background);
            var resolved = ChartTheme.ToSkColor(background.Color);

            // Not the documented fallback: the token really resolved out of the merged dictionary.
            Assert.NotEqual(ChartTheme.Fallback, resolved);

            var mask = Assert.IsType<LineSeries<ObservablePoint>>(tab.Chart.Series[1]);
            var fill = Assert.IsType<SolidColorPaint>(mask.Fill).Color;

            Assert.Equal(
                (resolved.Red, resolved.Green, resolved.Blue),
                (fill.Red, fill.Green, fill.Blue));
            Assert.Equal(0xFF, fill.Alpha);
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    // ---- case 23: the shipped vocabulary --------------------------------------------------------

    // ControlStyleScanTest already fails the build on a local Button, Border.tag or Border.callout
    // style; this asserts the two halves it does not, the compiled binding context and the class
    // names, and repeats the style rule over this one file so a reader of this case sees all three.
    [Fact]
    public void TheView_DeclaresItsDataType_AndUsesOnlyTheSharedClassVocabulary()
    {
        var markup = File.ReadAllText(
            Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Views", "Analysis", "CorrelationTabView.axaml"));

        Assert.Contains(
            "x:DataType=\"analysis:CorrelationTabViewModel\"", markup, StringComparison.Ordinal);

        foreach (var forbidden in new[]
                 {
                     "<Style Selector=\"Button",
                     "<Style Selector=\"ToggleButton",
                     "<Style Selector=\"Border.tag",
                     "<Style Selector=\"Border.callout",
                     "<Style Selector=\"lvc|",
                 })
        {
            Assert.DoesNotContain(forbidden, markup, StringComparison.Ordinal);
        }

        var vocabulary = File.ReadAllText(
            Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Theme", "Controls.axaml"));

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(vocabulary, @"Selector=""[^""]*"""))
        {
            foreach (Match name in Regex.Matches(match.Value, @"\.([A-Za-z][\w-]*)"))
            {
                declared.Add(name.Groups[1].Value);
            }
        }

        var used = new List<string>();
        foreach (Match match in Regex.Matches(markup, @"\bClasses=""([^""]+)"""))
        {
            used.AddRange(match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (Match match in Regex.Matches(markup, @"\bClasses\.([A-Za-z][\w-]*)\s*="))
        {
            used.Add(match.Groups[1].Value);
        }

        Assert.NotEmpty(used);

        var strangers = used.Distinct(StringComparer.Ordinal)
            .Where(name => !declared.Contains(name))
            .ToArray();

        Assert.True(
            strangers.Length == 0,
            "Every class this view names is declared in Theme/Controls.axaml. Strangers: "
            + string.Join(", ", strangers));
    }

    // ---- case 25: the layout at the two window sizes --------------------------------------------

    // Red against a fixed chart width: the page's own ScrollViewer starts scrolling horizontally,
    // which its Disabled default would otherwise make impossible, so the assertion is taken on the
    // extent against the viewport.
    [AvaloniaTheory]
    [InlineData(1280d, 800d)]
    [InlineData(1024d, 700d)]
    public void TheTab_FitsThePagesAllotment_WithNoHorizontalScroll(double width, double height)
    {
        var (_, view, _) = ShowPage(width, height);
        var scroller = ScrollAssertions.Scroller(view);
        var tab = view.GetVisualDescendants().OfType<CorrelationTabView>().Single();

        Assert.True(
            scroller.Extent.Width <= scroller.Viewport.Width + 0.5d,
            $"At {width} by {height} the page extends to {scroller.Extent.Width} across a "
            + $"{scroller.Viewport.Width} viewport.");

        // The chart is in the list because the page's own ScrollViewer disables horizontal
        // scrolling, so a control given a fixed width wider than the viewport is CLIPPED rather
        // than scrolled: the extent assertion above stays green and the reader loses the right
        // hand side of the chart. Its own measured width is what catches that.
        foreach (var name in new[] { "Presets", "MetricControls", "ResultRegion", "Chart" })
        {
            var control = Named<Control>(tab, name);
            Assert.True(control.IsEffectivelyVisible, $"{name} is not visible at {width} by {height}.");
            Assert.True(
                control.Bounds.Width <= scroller.Viewport.Width + 0.5d,
                $"{name} is {control.Bounds.Width} wide inside a {scroller.Viewport.Width} viewport.");
        }
    }

    // Case 25's second half, "the Correlation chart plus both stats cards fit above the fold at
    // 1280 by 800", was WITHDRAWN: the rule it asserted was never in the approved spec, the page
    // scrolls, and holding the cards above the fold is what held the chart at 180 pixels, which a
    // launched look read as a decorative strip with two tick labels and no distinguishable
    // confidence band. The floor is now 280, the smallest of the page's other charts, and the case
    // below measures that instead. The first half, the horizontal fit at both sizes, is unedited
    // above.

    // The chart is the viewport less the page's fixed spend and never below the floor, at every
    // window size. Red against the shipped floor of 180 at the two smaller sizes.
    [AvaloniaTheory]
    [InlineData(1024d, 700d)]
    [InlineData(1280d, 800d)]
    [InlineData(1920d, 1080d)]
    public void TheChart_IsTheViewportLessThePagesSpend_AndNeverBelowTheFloor(
        double width, double height)
    {
        var (_, view, _) = ShowPage(width, height);
        var scroller = ScrollAssertions.Scroller(view);
        var tab = view.GetVisualDescendants().OfType<CorrelationTabView>().Single();
        var chart = Named<Control>(tab, "Chart");

        var expected = Math.Max(
            CorrelationTabViewModel.ChartMinimumHeight,
            scroller.Viewport.Height - CorrelationTabViewModel.ChartFixedSpend);

        Assert.True(
            Math.Abs(chart.Bounds.Height - expected) <= 0.5d,
            $"At {width} by {height} the viewport is {scroller.Viewport.Height} tall and the chart "
            + $"is {chart.Bounds.Height}, not the {expected} the floor and the spend give.");
    }

    // ---- the chart grows with the window -----------------------------------------------------------

    // The height was a literal, so the scatter sat in a 180 pixel letterbox at every window size,
    // the smallest chart on its own page by a factor of 2.5. It is now the viewport less the page's
    // measured fixed spend, floored at 280, which is what the case above measures at the two
    // smaller sizes while a larger window gives the plot its area back.
    //
    // Red against the shipped literal on the first assertion. Red against a converter with no floor
    // on the two smaller sizes, which the case above already covers.
    [AvaloniaFact]
    public void TheChart_GrowsWithTheWindow_AndTheStatsCardsStayAboveTheFold()
    {
        var (_, view, _) = ShowPage(1920d, 1080d);
        var scroller = ScrollAssertions.Scroller(view);
        var tab = view.GetVisualDescendants().OfType<CorrelationTabView>().Single();

        var chart = Named<Control>(tab, "Chart");
        var cards = Named<Control>(tab, "StatsCards");

        Assert.True(
            chart.Bounds.Height > CorrelationTabViewModel.ChartMinimumHeight,
            $"At 1920 by 1080 the chart is still {chart.Bounds.Height} tall inside a "
            + $"{scroller.Viewport.Height} viewport, so it is not following the window.");

        var top = cards.TranslatePoint(default, scroller)?.Y ?? double.NaN;
        Assert.True(
            top >= -0.5d && top + cards.Bounds.Height <= scroller.Viewport.Height + 0.5d,
            $"At 1920 by 1080 the stats cards run from {top} for {cards.Bounds.Height} inside a "
            + $"{scroller.Viewport.Height} viewport, so growing the chart pushed them past the fold.");
    }

    // ---- the verdict's ink -----------------------------------------------------------------------

    // This application declares no ink for a bare TextBlock: there is no Selector="TextBlock" rule
    // anywhere, and the one Window style sets FontFamily alone. Every other sentence on the page
    // carries one of Controls.axaml's text classes; the verdict carried neither a class nor a
    // Foreground, so it inherited FluentTheme's Dark Window default, pure white, which is in no
    // GalactiLog palette. On red-light a launched look measured 1,998 white pixels in that block,
    // the brightest thing on a night-vision page.
    //
    // Red against the shipped markup in both themes: the block answers white, not the token.
    // Driven in two themes because a single theme could not tell a token apart from a constant.
    [AvaloniaTheory]
    [InlineData("red-light")]
    [InlineData("luminance")]
    public void TheVerdict_TakesThePrimaryInk_AndItsCountStaysTertiary(string themeId)
    {
        try
        {
            ThemeManager.Apply(themeId);

            var (_, view, _) = ShowTab(rows: true);
            var verdict = Named<TextBlock>(view, "Verdict");

            Assert.True(view.TryFindResource("ColorTextPrimary", out var primary));
            var expected = ((ISolidColorBrush)primary!).Color;
            var actual = ((ISolidColorBrush)verdict.Foreground!).Color;

            Assert.Equal(expected, actual);

            // The inherited default this replaces. No theme declares a pure white primary ink, so
            // this stays a real assertion rather than a restatement of the one above.
            Assert.NotEqual(Colors.White, actual);

            // The count beside it keeps its own, tertiary, which the block's ink must not have
            // taken over: it is a local value on the run and outranks the block's.
            Assert.True(view.TryFindResource("ColorTextTertiary", out var tertiary));
            var count = verdict.Inlines!.OfType<Run>().Single(run => run.Name == "PointCount");
            Assert.Equal(
                ((ISolidColorBrush)tertiary!).Color,
                ((ISolidColorBrush)count.Foreground!).Color);
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    // ---- the hover tooltip ------------------------------------------------------------------------

    // The tooltip strings were built and asserted from the first pass, and the running application
    // still showed no tooltip at all, because nothing in those cases reads the two properties that
    // decide whether a point can be FOUND under the pointer.
    //
    // rc5.4 resolves FindingStrategy.Automatic through VisibleSeries.GetFindingStrategy(), which
    // answers CompareOnlyXTakeClosest only when every series prefers the X strategy. A scatter
    // series declares PrefersXYStrategyTooltips, so this chart resolved to CompareAllTakeClosest,
    // and Series.FindPointsInPosition then requires the pointer INSIDE the hover area in both
    // axes. A scatter's hover area is its geometry box, 4 pixels square at frame granularity. The
    // declared X strategy asks the same question of one axis, so a point is findable anywhere
    // above or below it.
    //
    // Red against the shipped markup, which left the property at Automatic.
    [AvaloniaFact]
    public void TheChart_DeclaresTheXFindingStrategy_AndOnlyThePointSeriesAreHoverable()
    {
        var (_, view, tab) = ShowTab(rows: true);
        var chart = Named<CartesianChart>(view, "Chart");

        Assert.Equal(FindingStrategy.CompareOnlyXTakeClosest, chart.FindingStrategy);

        // Hidden is the one TooltipPosition that suppresses the tooltip outright (Chart.cs line
        // 680), so the control's own position is pinned here beside the strategy.
        Assert.NotEqual(TooltipPosition.Hidden, chart.TooltipPosition);

        // The band, the mask and the trend answer nothing; the two point series answer the
        // spec's string. A line series' hover area is a full axis unit wide, so a hoverable trend
        // would take every hover on the X strategy.
        var series = tab.Chart.Series;
        Assert.Equal(5, series.Count);
        Assert.False(series[0].IsHoverable);
        Assert.False(series[1].IsHoverable);
        Assert.True(series[2].IsHoverable);
        Assert.True(series[3].IsHoverable);
        Assert.False(series[4].IsHoverable);
    }

    // ---- spine B escalation 2: a SECOND query into the realised view ------------------------------

    // Every other case in this file takes ONE load, during the harness's own settle, and an inline
    // post seam survives that first publish, so before this case the file held nothing that could
    // detect a seam mistake at all. A later publish is the one that throws: it is raised from the
    // query's own thread-pool thread into a live binding, and the load loop's catch turns it into
    // Failed, a state nothing chose.
    //
    // The first query answers NO rows and the second answers four, so every assertion below reads
    // the second result positively and a swallowed publish cannot satisfy it. Seen red with
    // AnalysisShellHarness.Post mutated to the inline action => action().
    [AvaloniaFact]
    public void ASecondQueryIntoTheDrawnView_PublishesItsOwnResult()
    {
        // The stored default X metric is humidity (spec 5.8.2), which is what the page opens on.
        var (_, view, page) = AnalysisShellHarness.Show(
            _disposables,
            page: AnalysisShellHarness.Page(
                correlation: (x, _, _) => Result(rows: x != AnalysisMetric.Humidity)));

        var tab = view.GetVisualDescendants().OfType<CorrelationTabView>().Single();
        var region = Named<Control>(tab, "ResultRegion");
        Assert.False(region.IsEffectivelyVisible);

        // Moved on the DRAWN control through its own two-way binding, which is the reader's own
        // gesture and not a view-model assignment.
        var picker = Named<ComboBox>(tab, "XPicker");
        picker.SelectedItem = picker.Items
            .OfType<AnalysisMetricChoice>()
            .First(choice => choice.Metric == AnalysisMetric.Airmass);

        AnalysisSettle.Page(page);

        var correlation = Assert.IsType<CorrelationTabViewModel>(page.Tabs[0]);
        Assert.Equal(AnalysisMetric.Airmass, correlation.XMetric);
        Assert.Equal(AnalysisTabState.Ready, correlation.State);
        Assert.True(region.IsEffectivelyVisible);

        // The second result's own row count, read off the drawn runs rather than off the
        // view-model. The markup's two runs are separated by a whitespace run, so the count is
        // found by value and not by index.
        Assert.Contains(
            "(4 points)",
            Named<TextBlock>(tab, "Verdict").Inlines!.OfType<Run>().Select(run => run.Text));
    }

    // ---- case 26: the controls survive an empty result and the region does not --------------------

    // Red against a filler that puts a picker inside ResultRegion: the reader cannot widen the
    // filters that emptied the chart.
    [AvaloniaFact]
    public void TheControls_SurviveANoRowsResult_AndTheResultRegionDoesNot()
    {
        var (_, view, tab) = ShowTab(rows: false);

        Assert.Equal(AnalysisTabState.Empty, tab.State);
        Assert.False(tab.ShowsResult);
        Assert.False(Named<Control>(view, "ResultRegion").IsEffectivelyVisible);

        foreach (var name in new[] { "Presets", "XPicker", "YPicker", "OutlierToggle" })
        {
            var control = Named<Control>(view, name);
            Assert.True(control.IsEffectivelyVisible, $"{name} is not visible in the empty state.");
            Assert.True(control.IsEnabled, $"{name} is disabled in the empty state.");
        }

        // All six preset buttons are live, not only the panel that holds them.
        var buttons = Named<ItemsControl>(view, "Presets")
            .GetVisualDescendants()
            .OfType<Button>()
            .ToList();

        Assert.Equal(6, buttons.Count);
        Assert.All(buttons, button => Assert.True(button.IsEffectivelyVisible && button.IsEnabled));
    }
}
