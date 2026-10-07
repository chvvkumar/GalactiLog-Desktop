using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.Views;
using GalactiLog.App.Views.Analysis;
using GalactiLog.Core.Metrics;
using GalactiLog.Data.Queries;
using Xunit;
using static GalactiLog.App.Tests.TestSupport.AnalysisShellHarness;

namespace GalactiLog.App.Tests.Views.Analysis;

/// <summary>
/// Spec 12.14's Distributions tab body, shown in the page's REAL allotment (Task 6 cases 10, 29,
/// 30 and 31).
/// </summary>
/// <remarks>
/// The harness is the shell rather than a bare window, for the reason <c>AnalysisViewTests</c>
/// records: <c>MainWindowViewModel.NavRailExpandedWidth</c> is 200 and the status bar takes its
/// own row, so at 1280 by 800 the page has about 1080 by 720 and at the 1024 by 700 minimum about
/// 824 by 620, and a bare window would hand the view the full width and the layout case would
/// never fire. Every case here SHOWS a bound view, so the page takes <c>UiPost.Default</c> and
/// each settle goes through <c>AnalysisSettle.Page</c>, which drains the dispatcher.
/// </remarks>
public class DistributionsTabViewTests : IDisposable
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

    private (Window Window, AnalysisView View, DistributionsTabView Body, AnalysisViewModel Page) Show(
        double width = 1280d,
        double height = 800d,
        AnalysisViewModel? analysis = null)
    {
        // The Distributions tab is the second of the five, selected through the page's own command
        // rather than by assignment, which is the shipped route.
        var (window, view, page) = AnalysisShellHarness.Show(
            _disposables, width, height, analysis, shown => shown.Tabs[1]);

        return (window, view, Body<DistributionsTabView>(view), page);
    }

    // ---- case 29, the markup vocabulary -------------------------------------------------------

    // The same plain text scan shape ControlStyleScanTest and FontSizeTokenTest take, narrowed to
    // the two views this task fills so a failure names the file rather than the phase.
    [Theory]
    [InlineData("DistributionsTabView.axaml")]
    [InlineData("CompareTabView.axaml")]
    public void TheTabViews_DeclareXDataTypeAndNoLocalButtonTagOrCalloutStyle(string file)
    {
        var text = File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "Views", "Analysis", file));

        Assert.Contains("x:DataType=", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"<Style\s+Selector\s*=\s*""(?:Toggle)?Button"), text);
        Assert.DoesNotMatch(new Regex(@"<Style\s+Selector\s*=\s*""Border\.(?:tag|callout)"), text);

        // No heading row and no help glyph: all seven live in AnalysisView.axaml (ruling P1-4).
        Assert.DoesNotContain("t-label section", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HelpGlyph", text, StringComparison.Ordinal);

        // Ledger vocabulary: containment is alignment and the shared hairline, never a rounded
        // panel. The needle is the attribute, because the file's own comment names the rule.
        Assert.DoesNotMatch(new Regex(@"CornerRadius\s*="), text);
    }

    // Horizontal containment only. A control below the fold at the minimum height is correct: the
    // page scrolls vertically and spec 12.14 asks for no horizontal page scroll.
    private static bool FitsHorizontally(Control target, ScrollViewer scroller)
        => target.IsEffectivelyVisible
            && target.TranslatePoint(default, scroller) is { } point
            && point.X >= -0.5d
            && point.X + target.Bounds.Width <= scroller.Viewport.Width + 0.5d;

    // ---- case 30, layout at both sizes ---------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(1280d, 800d)]
    [InlineData(1024d, 700d)]
    public void TheTabFitsThePagesAllotmentAtBothSizes(double width, double height)
    {
        var (_, view, body, _) = Show(width, height);
        var scroller = ScrollAssertions.Scroller(view);

        Assert.True(body.IsEffectivelyVisible);
        Assert.True(
            scroller.Extent.Width <= scroller.Viewport.Width,
            $"Extent {scroller.Extent.Width} passed viewport {scroller.Viewport.Width} at {width} by {height}.");

        foreach (var name in new[] { "Controls", "HistogramMetricPicker" })
        {
            var control = body.GetVisualDescendants().OfType<Control>().Single(candidate => candidate.Name == name);
            Assert.True(ScrollAssertions.IsInView(control, scroller), $"{name} is outside the viewport.");
        }

        foreach (var name in new[] { "HistogramChart", "SkewnessCard", "PixelNote" })
        {
            var control = body.GetVisualDescendants().OfType<Control>().Single(candidate => candidate.Name == name);
            Assert.True(FitsHorizontally(control, scroller), $"{name} passes the viewport's width.");
        }
    }

    // The control row wraps rather than clipping at the minimum size. Red against a fixed-width
    // picker row: a clipped control hides an affordance the reader cannot then find.
    [AvaloniaFact]
    public void At1024By700_TheControlRowWrapsRatherThanClipping()
    {
        var (_, view, body, page) = Show(1024d, 700d);
        var tab = Assert.IsType<DistributionsTabViewModel>(page.Tabs[1]);

        tab.IsBoxPlot = true;
        AnalysisSettle.Page(page);

        var controls = Named<WrapPanel>(body, "Controls");
        var scroller = ScrollAssertions.Scroller(view);
        var picker = Named<ComboBox>(body, "GroupByPicker");

        Assert.True(picker.IsEffectivelyVisible);
        Assert.True(
            picker.TranslatePoint(default, controls)!.Value.X + picker.Bounds.Width
                <= controls.Bounds.Width + 0.5d,
            "The grouping picker is clipped by the control row.");
        Assert.True(scroller.Extent.Width <= scroller.Viewport.Width);
    }

    // ---- case 31 -------------------------------------------------------------------------------

    // Red against a filler that puts a picker inside ResultRegion: the reader cannot widen the
    // filters that emptied the chart.
    [AvaloniaFact]
    public void TheControlsSurviveANoRowsResult_AndTheResultRegionDoesNot()
    {
        var page = AnalysisShellHarness.Page(
            distribution: (_, _) => new DistributionResult([], AnalysisViewModelTestFactory.Stats(), 0d, 1));
        var (_, view, body, shown) = Show(analysis: page);

        Assert.Equal(AnalysisTabState.Empty, shown.Tabs[1].State);
        Assert.False(Named<StackPanel>(body, "ResultRegion").IsEffectivelyVisible);

        foreach (var name in new[] { "Controls", "HistogramButton", "BoxPlotButton", "HistogramMetricPicker" })
        {
            var control = body.GetVisualDescendants().OfType<Control>().Single(candidate => candidate.Name == name);
            Assert.True(control.IsEffectivelyVisible, $"{name} is hidden in the empty state.");
            Assert.True(control.IsEnabled, $"{name} is disabled in the empty state.");
        }

        // The grouping picker belongs to the box plot half and is reachable through the segment,
        // which is itself outside the region.
        var segment = Named<Button>(body, "BoxPlotButton");
        segment.Command!.Execute(segment.CommandParameter);
        AnalysisSettle.Page(shown);

        var grouping = Named<ComboBox>(body, "GroupByPicker");
        Assert.True(grouping.IsEffectivelyVisible);
        Assert.True(grouping.IsEnabled);

        // And the note is outside the region too, so it stands in every state.
        Assert.True(Named<TextBlock>(body, "PixelNote").IsEffectivelyVisible);
        Assert.True(view.IsEffectivelyVisible);
    }

    // ---- case 10, both statements on screen at once ---------------------------------------------

    // Spec 12.14: "The two are different statements and both are useful ... When both apply, both
    // are shown, the warning above the note." Red against an implementation that shows one or the
    // other, and red against a note drawn above the callout.
    [AvaloniaFact]
    public void ThePixelNoteAndThePlateScaleWarning_AreBothOnScreen_WarningAboveNote()
    {
        var page = AnalysisShellHarness.Page(
            boxPlot: (_, _, _) => new BoxPlotResult(
                [new BoxPlot("RC8 + ASI2600MM", 1d, 2d, 3d, 4d, 5d, [], 12)], 2, 12));
        var (_, view, body, shown) = Show(analysis: page);
        var tab = Assert.IsType<DistributionsTabViewModel>(shown.Tabs[1]);

        tab.IsBoxPlot = true;
        tab.GroupBy = BoxPlotGrouping.Equipment;
        AnalysisSettle.Page(page);

        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.True(tab.PlateScaleWarningVisible);

        var note = Named<TextBlock>(body, "PixelNote");
        Assert.True(note.IsEffectivelyVisible);
        Assert.Equal(DistributionsTabViewModel.PixelNoteText, note.Text);

        var callout = view.GetVisualDescendants().OfType<ContentControl>().Single(
            border => border.Classes.Contains("callout")
                && border.Classes.Contains("warn")
                && border.IsEffectivelyVisible);
        Assert.Equal(
            AnalysisTabViewModel.PlateScaleWarningText,
            Assert.IsType<TextBlock>(callout.Content).Text);

        // The warning is the base's row, drawn by AnalysisView above this body; the note is the
        // tab's own and is therefore below it.
        Assert.True(
            callout.TranslatePoint(default, view)!.Value.Y < note.TranslatePoint(default, view)!.Value.Y,
            "The pixel-domain note is drawn above the mixed plate scale warning.");
    }

    // ---- the two charts are one region apiece ---------------------------------------------------

    [AvaloniaFact]
    public void TheSegmentSwapsTheChartInsideTheOneResultRegion()
    {
        var (_, _, body, page) = Show();
        var tab = Assert.IsType<DistributionsTabViewModel>(page.Tabs[1]);

        var histogram = Named<Control>(body, "HistogramChart");
        var box = Named<Control>(body, "BoxPlotChart");

        Assert.True(histogram.IsEffectivelyVisible);
        Assert.False(box.IsEffectivelyVisible);

        tab.IsBoxPlot = true;
        AnalysisSettle.Page(page);

        Assert.False(histogram.IsEffectivelyVisible);
        Assert.True(box.IsEffectivelyVisible);
        Assert.False(Named<Control>(body, "SkewnessCard").IsEffectivelyVisible);
    }
}
