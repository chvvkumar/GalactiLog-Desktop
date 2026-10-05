using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.Views.Analysis;
using GalactiLog.Data.Queries;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Avalonia;
using LiveChartsCore.SkiaSharpView.Painting;
using Xunit;

namespace GalactiLog.App.Tests.Views.Analysis;

/// <summary>
/// Spec 12.14's Time Series tab body as markup (<c>task6.md</c> required cases 20, 29 and 32).
/// </summary>
/// <remarks>
/// Every case here SHOWS a bound view, so every one of them takes the post seam that reaches the
/// UI thread and drains it with <c>RunJobs</c> (the harness ruling in <c>task6.md</c> section 4):
/// the markup binds <c>ShowsResult</c> and the chart's three collections, and a publish raised
/// from the query's own thread-pool thread throws inside the binding, which the load loop turns
/// into a state nothing chose. The windowless cases live in
/// <c>ViewModels/Analysis/TimeSeriesTabTests</c>.
/// </remarks>
public class TimeSeriesTabViewTests
{
    private static readonly AnalysisFilter AnyFilter =
        new(null, null, null, AnalysisGranularity.Frame, null, null);

    // ---- case 20, the chart's background is the mask's own token -----------------------------

    [AvaloniaFact]
    public async Task TheChartsOwnBackground_IsTheTokenTheMaskIsPaintedIn()
    {
        var (_, view, tab) = await Show(Nights(9));

        Assert.Equal(AnalysisTabState.Ready, tab.State);

        var chart = view.GetControl<CartesianChart>("TimeSeriesChart");

        Assert.Equal(LegendPosition.Hidden, chart.LegendPosition);

        // Not Transparent: this chart is ruling A23's exception to spec 13's transparent rule,
        // because the mask under the band is opaque.
        Assert.NotEqual(Colors.Transparent, Background(chart));

        try
        {
            // The pair is asserted in TWO themes on purpose. One theme cannot tell a token from a
            // literal: luminance declares ColorBgElevated as #FF262626, so a Background
            // hard-coded to that hex agrees with the mask there and the case reads green with the
            // defect present. It was seen doing exactly that before this arm existed. A swap moves
            // the token and leaves a literal where it was.
            foreach (var themeId in new[] { "luminance", "deep-sky" })
            {
                ThemeManager.Apply(themeId);
                Dispatcher.UIThread.RunJobs();

                // The mask is painted at full alpha (ruling A23's "opaquely"), so the comparison
                // is over the colour the token declares. This side comes off the CONTROL, never
                // from a second ChartTheme.Read, which would assert a value equals itself.
                var mask = Assert.IsType<SolidColorPaint>(
                    tab.Chart!.Series.OfType<LineSeries<DateTimePoint>>()
                        .Single(series => series.Name == "Baseline mask").Fill);

                Assert.Equal(ChartTheme.ToSkColor(Background(chart)).WithAlpha(0xFF), mask.Color);
            }
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Color Background(CartesianChart chart)
        => Assert.IsAssignableFrom<ISolidColorBrush>(chart.Background).Color;

    // ---- case 20d, the empty state measures ---------------------------------------------------

    [AvaloniaFact]
    public async Task TheChart_WithNoNights_LaysOutWithoutThrowingOnItsAxes()
    {
        // rc5.4's chart engine throws "XAxes and YAxes must contain at least one element" from a
        // dispatcher post rather than inline, so a missing axis takes the page down where nothing
        // can catch it (spike trap 11). The chart is put in the tree with no data at all.
        var chart = new TimeSeriesChartViewModel();
        var control = new CartesianChart
        {
            Series = chart.Series,
            XAxes = chart.XAxes,
            YAxes = chart.YAxes,
            Width = 400,
            Height = 200,
        };

        var window = new Window { Width = 600, Height = 400, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(chart.XAxes);
        Assert.Single(chart.YAxes);
        Assert.Empty(chart.Series);

        chart.Dispose();
        window.Close();
    }

    // ---- case 32, the controls survive a no-rows result ---------------------------------------

    [AvaloniaFact]
    public async Task TheControls_SurviveANoRowsResult_AndTheResultRegionDoesNot()
    {
        var (_, view, tab) = await Show(new TimeSeriesResult([], [], [], [], 0));

        Assert.Equal(AnalysisTabState.Empty, tab.State);

        // Red against a filler that puts a picker inside ResultRegion: the reader who filtered the
        // chart into emptiness then has nothing left to filter back out with.
        Assert.False(view.GetControl<StackPanel>("ResultRegion").IsEffectivelyVisible);

        foreach (var name in new[] { "RawButton", "Ma7Button", "Ma30Button" })
        {
            var button = view.GetControl<Button>(name);
            Assert.True(button.IsEffectivelyVisible, name);
            Assert.True(button.IsEnabled, name);
        }

        var picker = view.GetControl<ComboBox>("MetricPicker");
        Assert.True(picker.IsEffectivelyVisible);
        Assert.True(picker.IsEnabled);
        Assert.Equal(20, picker.ItemCount);

        // And they still work from that state, which is the whole point of their being outside.
        view.GetControl<Button>("Ma7Button").Command!.Execute(
            view.GetControl<Button>("Ma7Button").CommandParameter);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(TimeSeriesSmoothing.Ma7, tab.Smoothing);
    }

    [AvaloniaFact]
    public async Task TheSmoothingSegment_MarksTheCurrentButton_WithTheShippedClassesOnly()
    {
        var (_, view, tab) = await Show(Nights(9));

        var raw = view.GetControl<Button>("RawButton");
        var ma7 = view.GetControl<Button>("Ma7Button");
        var ma30 = view.GetControl<Button>("Ma30Button");

        foreach (var button in new[] { raw, ma7, ma30 })
        {
            // The same shipped pair the filter bar's granularity segment and the tab strip take.
            // No new class and no local style; ControlStyleScanTest fails the build on one.
            Assert.Contains("sm", button.Classes);
            Assert.Contains("page", button.Classes);
        }

        Assert.Contains("current", raw.Classes);
        Assert.DoesNotContain("current", ma7.Classes);

        ma30.Command!.Execute(ma30.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TimeSeriesSmoothing.Ma30, tab.Smoothing);
        Assert.DoesNotContain("current", raw.Classes);
        Assert.Contains("current", ma30.Classes);
    }

    // ---- spine B escalation 2: a SECOND query into the realised view ---------------------------

    // Every other case in this file takes ONE load, during its own harness's settle, and an inline
    // post seam survives that first publish, so before this case the file held nothing that could
    // detect a seam mistake at all. A later publish is the one that throws: raised from the query's
    // own thread-pool thread into a live binding, with the load loop's catch turning it into
    // Failed, a state nothing chose.
    //
    // The first query answers NO nights and the second answers four, so every assertion below reads
    // the second result positively and a swallowed publish cannot satisfy it. Seen red with
    // AnalysisShellHarness.Post mutated to the inline action => action().
    //
    // Shown through AnalysisShellHarness and not through this file's own bare window, which was
    // measured rather than assumed: with the tab alone in a Window the inline seam survives a
    // second query too, because this view's chart view-model is replaced wholesale rather than
    // having an observed collection mutated under it. The shell is what draws the base's State,
    // StatusLine and plate scale callout around the body, and those are the bindings a publish
    // raised off the UI thread breaks. The page is built through AnalysisShellHarness.Page, which
    // carries its own timeSeries delegate; the seam is still named as AnalysisShellHarness.Post
    // and is still the one the mutation reaches.
    [AvaloniaFact]
    public void ASecondQueryIntoTheDrawnView_PublishesItsOwnResult()
    {
        List<IDisposable> disposables = [];

        // The tab opens on hfr, which is TimeSeriesTabViewModel's own default.
        var page = AnalysisShellHarness.Page(
            timeSeries: (metric, _) => metric == AnalysisMetric.Hfr
                ? new TimeSeriesResult([], [], [], [], 0)
                : Nights(4));

        var (window, shown, _) = AnalysisShellHarness.Show(
            disposables, page: page, tab: analysis => analysis.Tabs[2]);

        var view = shown.GetVisualDescendants().OfType<TimeSeriesTabView>().Single();
        var tab = Assert.IsType<TimeSeriesTabViewModel>(page.Tabs[2]);

        Assert.Equal(AnalysisTabState.Empty, tab.State);
        Assert.False(view.GetControl<StackPanel>("ResultRegion").IsEffectivelyVisible);

        // Moved on the DRAWN control through its own two-way binding, which is the reader's own
        // gesture and not a view-model assignment.
        var picker = view.GetControl<ComboBox>("MetricPicker");
        picker.SelectedItem = picker.Items
            .OfType<AnalysisMetricChoice>()
            .First(choice => choice.Metric == AnalysisMetric.Eccentricity);

        AnalysisSettle.Page(page);

        Assert.Equal(AnalysisMetric.Eccentricity, tab.Metric);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.True(view.GetControl<StackPanel>("ResultRegion").IsEffectivelyVisible);

        // The second result's own night count, read off the drawn chart rather than off the
        // view-model's copy of it. Summed over the three point series, which is how the chart
        // splits the nights by quality band.
        var nightly = view.GetControl<CartesianChart>("TimeSeriesChart").Series!
            .Where(series => series.Name == "Nightly median")
            .Sum(series => series.Values!.Cast<DateTimePoint>().Count());
        Assert.Equal(4, nightly);

        window.Close();
        foreach (var item in disposables)
        {
            item.Dispose();
        }
    }

    // ---- case 29, the markup vocabulary -------------------------------------------------------

    [Fact]
    public void TheView_DeclaresItsDataType_AndAddsNoLocalStyleAndNoneOfTheBasesFiveRows()
    {
        var markup = File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "Views", "Analysis", "TimeSeriesTabView.axaml"));

        Assert.Contains("x:DataType=\"analysis:TimeSeriesTabViewModel\"", markup, StringComparison.Ordinal);

        // No local style of any kind: the vocabulary is Theme/Controls.axaml's.
        Assert.DoesNotContain("<Style", markup, StringComparison.Ordinal);

        // The five things AnalysisView.axaml draws once for every tab (ruling P1-3, ruling Q10): a
        // second callout or a second state sentence here would draw the same words twice.
        Assert.DoesNotContain("PlateScaleWarning", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("StatusLine", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("RetryCommand", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Classes=\"callout", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("t-label section", markup, StringComparison.Ordinal);

        // No FontSize setter: Scales.axaml's FontSize* keys are ratios, not point sizes, so a
        // token bound to one renders sub-pixel text (FontSizeTokenTest).
        Assert.DoesNotContain("FontSize=", markup, StringComparison.Ordinal);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static TimeSeriesResult Nights(int count, int plateScales = 1) => new(
        [.. Enumerable.Range(0, count).Select(index => new TimeSeriesPoint(
            new DateOnly(2025, 6, 1).AddDays(index), (index % 4) + 1d, "M 31", 1, 10))],
        [],
        [],
        [],
        plateScales);

    // The tab shown in a window through its own view, with the post seam that reaches the UI
    // thread. The window is kept alive by the returned tuple for the length of the case.
    private static async Task<(Window Window, TimeSeriesTabView View, TimeSeriesTabViewModel Tab)> Show(
        TimeSeriesResult result)
    {
        var tab = new TimeSeriesTabViewModel(
            () => AnyFilter,
            (_, _) => result,
            AnalysisShellHarness.Post);

        var view = new TimeSeriesTabView { DataContext = tab };
        var window = new Window { Width = 1024, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Assigning IsVisible is the whole of how a tab is selected, and the publish it starts is
        // POSTED, so the await returns before it has run: RunJobs drains it.
        tab.IsVisible = true;
        await tab.PendingLoad!;
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(view.GetVisualRoot());
        return (window, view, tab);
    }
}
