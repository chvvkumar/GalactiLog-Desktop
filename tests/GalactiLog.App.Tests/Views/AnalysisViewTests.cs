using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.Views;
using GalactiLog.App.Views.Analysis;
using GalactiLog.Data.Queries;
using Xunit;
using static GalactiLog.App.Tests.TestSupport.AnalysisShellHarness;

namespace GalactiLog.App.Tests.Views;

/// <summary>
/// Spec 12.14's markup, measured at the page's REAL allotment.
/// </summary>
/// <remarks>
/// <para>
/// The harness is the shell, not a bare window. <c>MainWindowViewModel.NavRailExpandedWidth</c> is
/// 200 and the status bar takes its own row, so at 1280 by 800 the page has about 1080 by 720 and
/// at the 1280 by 720 minimum about 1080 by 640. Putting the view straight into a
/// <c>new Window { Width = 1280 }</c>, which is what every other view layout case in this project
/// does, would hand it the full 1280 and case 1's red proof would never fire.
/// </para>
/// <para>
/// It is also the only harness that exercises the seam in the shipped order: the shell
/// builds the page, the content region binds it, and <c>AnalysisView</c> activates it.
/// </para>
/// </remarks>
public class AnalysisViewTests : IDisposable
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

    private (Window Window, AnalysisView View, AnalysisViewModel Page) ShowAnalysis(
        double width = 1280d,
        double height = 800d,
        AnalysisViewModel? analysis = null)
        => AnalysisShellHarness.Show(_disposables, width, height, analysis);

    private static ScrollViewer Scroller(AnalysisView view) => ScrollAssertions.Scroller(view);

    // ---- the seam the view assigns, in the shipped order ---------------------------------------

    [AvaloniaFact]
    public void TheShippedView_ActivatesThePage_SoTheSelectedTabIssuesExactlyOneQuery()
    {
        var queries = 0;
        var page = AnalysisShellHarness.Page(
            correlation: (_, _, _) => { queries++; return AnalysisViewModelTestFactory.Correlation(); });

        // Nothing has been queried yet: the page is built by the shell's lazy factory and the first
        // query is the view's own activation.
        Assert.Equal(0, queries);

        var (_, _, shown) = ShowAnalysis(analysis: page);

        Assert.Equal(1, queries);
        Assert.Equal(AnalysisTabState.Ready, shown.Tabs[0].State);
        Assert.NotNull(shown.Tabs[0].Body);
        Assert.All(shown.Tabs.Skip(1), tab => Assert.Null(tab.Body));
    }

    [AvaloniaFact]
    public void TheTabStrip_MarksTheCurrentTab_AndAClickMovesBothTheMarkerAndTheBody()
    {
        var (_, view, page) = ShowAnalysis();
        var strip = Named<ItemsControl>(view, "TabStrip");

        var buttons = strip.GetVisualDescendants().OfType<Button>().ToList();
        Assert.Equal(5, buttons.Count);
        Assert.Contains("current", buttons[0].Classes);
        Assert.All(buttons.Skip(1), button => Assert.DoesNotContain("current", button.Classes));

        // Only the shipped vocabulary: Button.sm plus page, with current on the selected one.
        Assert.All(buttons, button => Assert.Contains("sm", button.Classes));
        Assert.All(buttons, button => Assert.Contains("page", button.Classes));

        buttons[3].Command!.Execute(buttons[3].CommandParameter);
        AnalysisSettle.Page(page);

        Assert.Equal("matrix", page.SelectedTab.Key);
        Assert.DoesNotContain("current", buttons[0].Classes);
        Assert.Contains("current", buttons[3].Classes);
        Assert.True(Named<StackPanel>(view, "MatrixSection").IsVisible);
        Assert.False(Named<StackPanel>(view, "CorrelationSection").IsVisible);
        Assert.Contains(view.GetVisualDescendants(), visual => visual is MatrixTabView);
    }

    [AvaloniaFact]
    public void TheMixedPlateScaleCallout_IsShownOverTheTabBody_OnlyWhenTheCountAndTheMetricBothSaySo()
    {
        var scales = 1;
        var page = AnalysisShellHarness.Page(
            correlation: (_, _, _) => AnalysisViewModelTestFactory.Correlation(plateScales: scales));
        var (_, view, shown) = ShowAnalysis(analysis: page);

        // The callout's element is materialised either way; what one plate scale must not do is
        // SHOW it.
        Assert.False(shown.Tabs[0].PlateScaleWarningVisible);
        Assert.DoesNotContain(
            view.GetVisualDescendants().OfType<ContentControl>(),
            border => border.Classes.Contains("callout") && border.Classes.Contains("warn")
                && border.IsEffectivelyVisible);

        scales = 2;
        shown.SharedFilter.SelectedFilter = "Ha";
        AnalysisSettle.Page(shown);

        // Y is hfr by default, so the pixel-metric half is satisfied and the callout is drawn in
        // the warning composite of the control vocabulary.
        Assert.True(shown.Tabs[0].PlateScaleWarningVisible);
        var callout = view.GetVisualDescendants().OfType<ContentControl>()
            .Single(border => border.Classes.Contains("callout") && border.Classes.Contains("warn")
                && border.IsEffectivelyVisible);
        var sentence = Assert.IsType<TextBlock>(callout.Content);
        Assert.Equal(AnalysisTabViewModel.PlateScaleWarningText, sentence.Text);

        // The warn arm marks itself with a label row, not with a tinted fill or amber ink.
        Assert.Contains(
            callout.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "Warning" && block.IsEffectivelyVisible);
        Assert.Equal(view.FindResource("ColorTextPrimary"), sentence.Foreground);
    }

    [AvaloniaFact]
    public void TheTabsResultRegion_IsDrawnOnlyWhileAResultIs_AndTheTabsOwnControlsStayOnScreen()
    {
        // Spec 12.14's States table:
        // "No row matches the filters ... No chart is drawn", while the sentence IS drawn. The gate
        // is the tab's own one ResultRegion over the base's ShowsResult, not the whole tab body:
        // hiding the body would take the pickers with it, and a reader who filtered a chart into
        // emptiness would have nothing left to filter back out with.
        var points = 0;
        var page = AnalysisShellHarness.Page(
            correlation: (_, _, _) => AnalysisViewModelTestFactory.Correlation(points: points));
        var (_, view, shown) = ShowAnalysis(analysis: page);

        var body = view.GetVisualDescendants().OfType<CorrelationTabView>().Single();
        var region = Named<StackPanel>(body, "ResultRegion");

        Assert.Equal(AnalysisTabState.Empty, shown.Tabs[0].State);
        Assert.False(region.IsEffectivelyVisible);
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == shown.Tabs[0].StatusLine && text.IsEffectivelyVisible);

        // The tab's view itself is on screen in the empty state, which is what carries the controls
        // each tab's own view puts outside the region.
        Assert.True(body.IsEffectivelyVisible);

        points = 3;
        shown.SharedFilter.SelectedFilter = "Ha";
        AnalysisSettle.Page(shown);

        Assert.Equal(AnalysisTabState.Ready, shown.Tabs[0].State);
        Assert.Equal(string.Empty, shown.Tabs[0].StatusLine);
        Assert.True(region.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void TheTwoDateClearButtons_EachCarryTheirOwnAutomationName()
    {
        var (_, view, _) = ShowAnalysis();

        var clearButtons = view.GetVisualDescendants().OfType<Button>()
            .Where(button => Equals(button.Content, "x"))
            .ToList();

        Assert.Equal(2, clearButtons.Count);
        var names = clearButtons.Select(Avalonia.Automation.AutomationProperties.GetName).ToList();
        Assert.Contains("Clear date from", names);
        Assert.Contains("Clear date to", names);
    }

    /// <summary>
    /// The bar's selection across a re-read, measured where it happens: under a LIVE two-way
    /// <c>SelectedItem</c> binding on the shipped <c>ComboBox</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The windowless cases in <c>SharedFilterTests</c> cannot see this half at all: they run with
    /// no bound control, so nothing writes back while the rows move. Measured here on the real
    /// picker, with a non-first combination chosen through the control itself: when the row at the
    /// selected index is REPLACED, Avalonia writes NULL back through the binding, synchronously,
    /// inside the publish, and the bar's own null guard turns that into the "All equipment" row. It
    /// does not write the replacement back and it does not leave the selection alone. An INSERT
    /// ahead of the selection is safe, the control following the item, but the bar's publish never
    /// inserts: it is an index-compare-and-write, so an inserted row reaches the live collection as
    /// a chain of replaces over every later index, the reader's own among them.
    /// </para>
    /// <para>
    /// Seen red against the shipped publish, which corrected the selection only where the chosen row
    /// was absent: the picker reads "All equipment" and the assertion on the control's own
    /// <c>SelectedItem</c> fails. The <c>Changed</c> count is the second half: the write-back raises
    /// it from inside the publish, so a page built on the shipped code requeries every tab for a
    /// filter the reader never chose.
    /// </para>
    /// <para>
    /// The page is built through <c>AnalysisShellHarness.Page</c>, which takes the bar's
    /// <c>loadEquipment</c> delegate and fixes the shown seam, so no case spells that seam by hand.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public void AReReadUnderTheBoundPicker_LeavesTheChosenCombinationWhereTheReaderPutIt()
    {
        var equipment = new List<EquipmentCombination>
        {
            new("RC8", "ASI2600MM", false),
            new("Askar FMA180", "ASI533MC", false),
        };
        var page = AnalysisShellHarness.Page(loadEquipment: () => [.. equipment]);
        var (_, view, shown) = ShowAnalysis(analysis: page);

        // Chosen on the CONTROL, through its own two-way binding, and not on the view-model: the
        // binding is the whole subject of this case.
        var picker = Named<ComboBox>(view, "EquipmentPicker");
        picker.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Askar FMA180 + ASI533MC", shown.SharedFilter.SelectedEquipment.Label);

        var raised = 0;
        shown.SharedFilter.Changed += (_, _) => raised++;

        // The scan that catalogues frames for the other rig and carries it up the frame-count sort,
        // which is what moves a row under a reader who is not touching the page.
        equipment.Reverse();
        shown.SharedFilter.Reload();
        AnalysisSettle.Page(shown);

        Assert.Equal(
            new[] { "All equipment", "Askar FMA180 + ASI533MC", "RC8 + ASI2600MM" },
            shown.SharedFilter.EquipmentChoices.Select(choice => choice.Label));
        Assert.Equal("Askar FMA180 + ASI533MC", shown.SharedFilter.SelectedEquipment.Label);
        Assert.Equal("Askar FMA180", shown.SharedFilter.Current!.Telescope);

        // The control agrees with the bar, at the row's NEW index, so the reader sees no movement.
        Assert.Equal("Askar FMA180 + ASI533MC", Assert.IsType<EquipmentChoice>(picker.SelectedItem).Label);
        Assert.Equal(1, picker.SelectedIndex);

        // And no tab was marked stale for a selection nobody moved.
        Assert.Equal(0, raised);
    }

    // ---- section 10.1's four layout cases, at the page's real allotment -----------------------

    [AvaloniaFact]
    public void At1280By800_EveryPartOfThePageIsInsideTheViewport()
    {
        var (window, view, _) = ShowAnalysis();
        var region = window.GetControl<ContentControl>("ContentRegion");
        var scroller = Scroller(view);

        Assert.True(region.Bounds.Width > 0d && region.Bounds.Width < 1280d);
        AssertInView(view, scroller, "SharedFilters", "FilterControls", "TabStrip", "CorrelationSection");

        // Both date fields, on BOTH axes: this is the assertion a filter card laid out in one
        // non-wrapping row fails.
        Assert.True(IsFullyInView(Named<DatePicker>(view, "DateFromPicker"), scroller), "Date from is outside.");
        Assert.True(IsFullyInView(Named<DatePicker>(view, "DateToPicker"), scroller), "Date to is outside.");
        Assert.True(IsFullyInView(Named<ComboBox>(view, "EquipmentPicker"), scroller), "The equipment picker is outside.");
        Assert.True(scroller.Extent.Width <= scroller.Viewport.Width);
    }

    [AvaloniaFact]
    public void At1280By720_TheFilterControlsWrapRatherThanClip()
    {
        var (window, view, _) = ShowAnalysis(1280d, 720d);
        var region = window.GetControl<ContentControl>("ContentRegion");
        var scroller = Scroller(view);

        Assert.True(region.Bounds.Width > 0d && region.Bounds.Width < 1280d);

        var controls = Named<WrapPanel>(view, "FilterControls");
        var equipment = Named<ComboBox>(view, "EquipmentPicker");
        var dateTo = Named<DatePicker>(view, "DateToPicker");

        // Wrapped, not clipped: the last control is on a later row than the first and both are
        // inside the panel's own width. A clipped control hides an affordance the reader cannot
        // then find.
        Assert.True(dateTo.TranslatePoint(default, controls)!.Value.Y > equipment.TranslatePoint(default, controls)!.Value.Y);
        Assert.True(IsFullyInView(dateTo, scroller), "Date to is outside at the minimum size.");
        Assert.True(IsFullyInView(equipment, scroller), "The equipment picker is outside at the minimum size.");
        Assert.True(scroller.Extent.Width <= scroller.Viewport.Width);

        AssertInView(view, scroller, "SharedFilters", "FilterControls", "TabStrip");
    }

    [AvaloniaFact]
    public void AReversedRange_ShowsTheSentenceUnderTheFields_AndLeavesTheStripOnScreen()
    {
        var (_, view, page) = ShowAnalysis(1280d, 720d);

        page.SharedFilter.DateFrom = new DateTimeOffset(new DateTime(2025, 6, 10), TimeSpan.Zero);
        page.SharedFilter.DateTo = new DateTimeOffset(new DateTime(2025, 6, 1), TimeSpan.Zero);
        AnalysisSettle.Page(page);

        var error = Named<ContentControl>(view, "DateRangeError");
        var controls = Named<WrapPanel>(view, "FilterControls");
        var strip = Named<ItemsControl>(view, "TabStrip");
        var scroller = Scroller(view);

        Assert.True(error.IsEffectivelyVisible);
        Assert.Equal(
            SharedFilterViewModel.RangeErrorText,
            Assert.IsType<TextBlock>(error.Content).Text);
        Assert.Contains(
            error.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text == "Error" && block.IsEffectivelyVisible);

        // Under the two fields, and the strip is still reachable below it.
        Assert.True(error.TranslatePoint(default, view)!.Value.Y
            >= controls.TranslatePoint(default, view)!.Value.Y + controls.Bounds.Height - 0.5d);
        Assert.True(ScrollAssertions.IsInView(strip, scroller));
        Assert.True(scroller.Extent.Width <= scroller.Viewport.Width);
    }

    [AvaloniaTheory]
    [InlineData(1280d, 800d)]
    [InlineData(1280d, 720d)]
    public void TheExtentNeverPassesTheViewportWidth(double width, double height)
    {
        var (_, view, _) = ShowAnalysis(width, height);
        var scroller = Scroller(view);

        // The one assertion that catches a fixed Width somebody adds later.
        Assert.True(
            scroller.Extent.Width <= scroller.Viewport.Width,
            $"Extent {scroller.Extent.Width} passed viewport {scroller.Viewport.Width} at {width} by {height}.");
    }

    private static void AssertInView(AnalysisView view, ScrollViewer scroller, params string[] names)
    {
        foreach (var name in names)
        {
            var control = view.GetVisualDescendants().OfType<Control>().Single(candidate => candidate.Name == name);
            Assert.True(ScrollAssertions.IsInView(control, scroller), $"{name} is outside the viewport.");
        }
    }
}
