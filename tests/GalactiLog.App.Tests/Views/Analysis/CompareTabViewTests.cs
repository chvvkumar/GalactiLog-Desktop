using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.Views;
using GalactiLog.App.Views.Analysis;
using GalactiLog.Data.Queries;
using Xunit;
using static GalactiLog.App.Tests.TestSupport.AnalysisShellHarness;

namespace GalactiLog.App.Tests.Views.Analysis;

/// <summary>
/// Spec 12.14's Compare tab body, shown in the page's REAL allotment (Task 6 cases 30 and 33).
/// </summary>
/// <remarks>
/// This is the tab the result-region ruling matters most on: it publishes a non-queryable state
/// before anything is chosen, so the two group pickers are the only thing that can ever make it
/// queryable. It is also where ruling A12's view-assigned seam is exercised in the shipped order,
/// because <c>CompareTabView</c> hands the tab the filter bar's two lists on its own attach.
/// </remarks>
public class CompareTabViewTests : IDisposable
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

    private (Window Window, AnalysisView View, CompareTabView Body, AnalysisViewModel Page) Show(
        double width = 1280d,
        double height = 800d,
        AnalysisViewModel? analysis = null)
    {
        var (window, view, page) = AnalysisShellHarness.Show(
            _disposables, width, height, analysis, shown => shown.Tabs[4]);

        return (window, view, Body<CompareTabView>(view), page);
    }

    // ---- case 33 -------------------------------------------------------------------------------

    // Red against a view that hides the pickers in the not-yet-queryable state: nothing on the tab
    // could then ever make it queryable again.
    [AvaloniaFact]
    public void TheControlsSurviveTheNotYetQueryableState_AndTheResultRegionDoesNot()
    {
        var (_, _, body, page) = Show();
        var tab = Assert.IsType<CompareTabViewModel>(page.Tabs[4]);

        Assert.Equal(CompareTabViewModel.ChooseGroupsText, tab.StatusLine);
        Assert.False(tab.ShowsResult);
        Assert.False(Named<StackPanel>(body, "ResultRegion").IsEffectivelyVisible);

        foreach (var name in new[] { "Controls", "EquipmentButton", "FilterButton", "MetricPicker", "GroupAPicker", "GroupBPicker" })
        {
            var control = body.GetVisualDescendants().OfType<Control>().Single(candidate => candidate.Name == name);
            Assert.True(control.IsEffectivelyVisible, $"{name} is hidden while the tab is not queryable.");
            Assert.True(control.IsEnabled, $"{name} is disabled while the tab is not queryable.");
        }
    }

    // ---- the two group pickers, against the real shell -----------------------------------------

    // The tab's constructor takes the filter bar's own two collections, so by the time the shell
    // has built and shown the page both pickers carry the bar's rows with neither sentinel. Red
    // against a constructor that snapshots them: the bar's list read is asynchronous and lands
    // after the page is built, so both pickers would be empty and the tab could never become
    // queryable.
    [AvaloniaFact]
    public void TheTabTakesTheFilterBarsOwnLists_WithNeitherSentinelRow()
    {
        var (_, _, body, page) = Show();
        var tab = Assert.IsType<CompareTabViewModel>(page.Tabs[4]);

        Assert.NotEmpty(tab.GroupChoices);
        Assert.DoesNotContain(
            SharedFilterViewModel.AllEquipmentLabel,
            tab.GroupChoices.Select(choice => choice.Label));

        var picker = Named<ComboBox>(body, "GroupAPicker");
        Assert.Same(tab.GroupChoices, picker.ItemsSource);

        // Choosing two different groups through the pickers is what leaves the state.
        picker.SelectedIndex = 0;
        Named<ComboBox>(body, "GroupBPicker").SelectedIndex = 1;
        AnalysisSettle.Page(page);

        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.True(Named<StackPanel>(body, "ResultRegion").IsEffectivelyVisible);
        Assert.Equal(string.Empty, tab.StatusLine);
        Assert.NotEqual(string.Empty, tab.GroupA);
        Assert.NotEqual(tab.GroupA, tab.GroupB);
    }

    // Leaving the Analysis page and coming back gives the SAME AnalysisViewModel and the same
    // SharedFilterViewModel, because MainWindowViewModel memoises the page factory, under a NEW
    // AnalysisView and a new CompareTabView. Both chosen groups and the drawn result have to
    // survive that. Red against an option list that is cleared and refilled under the two live
    // two-way SelectedItem bindings: the controls write null back and the tab falls out of Ready
    // into its not-yet-queryable sentence.
    [AvaloniaFact]
    public void TheChosenGroupsSurviveLeavingThePageAndComingBack()
    {
        var (window, _, body, page) = Show();
        var tab = Assert.IsType<CompareTabViewModel>(page.Tabs[4]);
        var shell = Assert.IsType<MainWindowViewModel>(window.DataContext);

        Named<ComboBox>(body, "GroupAPicker").SelectedIndex = 0;
        Named<ComboBox>(body, "GroupBPicker").SelectedIndex = 1;
        AnalysisSettle.Page(page);

        var groupA = tab.GroupA;
        var groupB = tab.GroupB;
        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.NotEqual(string.Empty, groupA);

        // Away, and back.
        shell.Selected = shell.Items[0];
        Dispatcher.UIThread.RunJobs();
        shell.Selected = shell.Items[2];
        AnalysisSettle.Page(page);

        var region = window.GetControl<ContentControl>("ContentRegion");
        var reborn = region.GetVisualDescendants().OfType<CompareTabView>().Single();
        Assert.NotSame(body, reborn);
        Assert.Same(tab, page.Tabs[4]);

        Assert.Equal(groupA, tab.GroupA);
        Assert.Equal(groupB, tab.GroupB);
        Assert.NotNull(tab.SelectedGroupA);
        Assert.NotNull(tab.SelectedGroupB);
        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.Equal(string.Empty, tab.StatusLine);
        Assert.True(Named<StackPanel>(reborn, "ResultRegion").IsEffectivelyVisible);
    }

    /// <summary>
    /// Both group pickers across a bar re-read that REORDERS the equipment rows, measured on the
    /// two real <c>ComboBox</c>es with both groups chosen through the controls themselves and
    /// neither of them the first row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The equipment list is ordered by frame count first, so a scan that catalogues frames for one
    /// rig carries it past another with no row added and none removed. The tab's rebuild applies
    /// the desired rows to the bound collection by index, so such a reorder reaches both pickers as
    /// a REPLACE at their own selected index, and Avalonia answers a replace under a live two-way
    /// <c>SelectedItem</c> by writing null back, synchronously.
    /// </para>
    /// <para>
    /// Seen red against the rebuild that reassigned nothing: both pickers read null, the tab held
    /// no group at all, its state left <see cref="AnalysisTabState.Ready"/> for
    /// <see cref="AnalysisTabState.Empty"/> and its status line became
    /// "Select two different groups to compare." with the reader's own two choices gone. The query
    /// count is the second half: the write-back runs inside the rebuild, so the tab requeried for
    /// groups nobody chose.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public void AReReadThatReordersTheRigs_LeavesBothGroupPickersWhereTheReaderPutThem()
    {
        var equipment = new List<EquipmentCombination>
        {
            new("RC8", "ASI2600MM", false),
            new("Askar FMA180", "ASI533MC", false),
            new("Esprit 100", "ASI294MC", false),
        };
        var queries = 0;
        var page = AnalysisShellHarness.Page(
            loadEquipment: () => [.. equipment],
            compare: (_, _, _, _, _, _) =>
            {
                queries++;
                return AnalysisViewModelTestFactory.Compare();
            });

        var (_, _, body, shown) = Show(analysis: page);
        var tab = Assert.IsType<CompareTabViewModel>(shown.Tabs[4]);

        // Chosen on the CONTROLS, through their own two-way bindings, and neither on the first row.
        var pickerA = Named<ComboBox>(body, "GroupAPicker");
        var pickerB = Named<ComboBox>(body, "GroupBPicker");
        pickerA.SelectedIndex = 1;
        pickerB.SelectedIndex = 2;
        AnalysisSettle.Page(shown);

        var groupA = tab.GroupA;
        var groupB = tab.GroupB;
        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.Contains("Askar FMA180", groupA);
        Assert.Contains("Esprit 100", groupB);

        var before = queries;

        // The scan that carries the last rig up the frame-count sort, which moves a row under a
        // reader who is not touching the page.
        equipment.Reverse();
        shown.SharedFilter.Reload();
        AnalysisSettle.Page(shown);

        Assert.Equal(
            new[] { "Esprit 100 + ASI294MC", "Askar FMA180 + ASI533MC", "RC8 + ASI2600MM" },
            tab.GroupChoices.Select(choice => choice.Label));

        // Both groups are still the reader's own, at the rows' NEW indices.
        Assert.Equal(groupA, tab.GroupA);
        Assert.Equal(groupB, tab.GroupB);
        Assert.Equal(groupA, Assert.IsType<CompareGroupChoice>(pickerA.SelectedItem).Value);
        Assert.Equal(groupB, Assert.IsType<CompareGroupChoice>(pickerB.SelectedItem).Value);
        Assert.Equal(1, pickerA.SelectedIndex);
        Assert.Equal(0, pickerB.SelectedIndex);

        Assert.Equal(AnalysisTabState.Ready, tab.State);
        Assert.Equal(string.Empty, tab.StatusLine);

        // And nothing was requeried for a selection nobody moved.
        Assert.Equal(before, queries);
    }

    // ---- case 30, layout at both sizes ----------------------------------------------------------

    // The segment, the metric picker and both group pickers fit on at most two rows at the
    // minimum size. Red against a fixed-width picker row.
    [AvaloniaTheory]
    [InlineData(1280d, 800d)]
    [InlineData(1024d, 700d)]
    public void TheControlsFitThePagesAllotmentOnAtMostTwoRows(double width, double height)
    {
        var (_, view, body, _) = Show(width, height);
        var scroller = ScrollAssertions.Scroller(view);
        var controls = Named<WrapPanel>(body, "Controls");

        Assert.True(
            scroller.Extent.Width <= scroller.Viewport.Width,
            $"Extent {scroller.Extent.Width} passed viewport {scroller.Viewport.Width} at {width} by {height}.");

        var rows = controls.Children
            .OfType<Control>()
            .Select(child => Math.Round(child.TranslatePoint(default, controls)!.Value.Y, 1))
            .Distinct()
            .Count();
        Assert.True(rows <= 2, $"The Compare controls wrapped onto {rows} rows at {width} by {height}.");

        foreach (var name in new[] { "EquipmentButton", "MetricPicker", "GroupAPicker", "GroupBPicker" })
        {
            var control = body.GetVisualDescendants().OfType<Control>().Single(candidate => candidate.Name == name);
            Assert.True(ScrollAssertions.IsInView(control, scroller), $"{name} is outside the viewport.");
        }
    }

    // ---- the result region's own contents --------------------------------------------------------

    [AvaloniaFact]
    public void TheVerdictAndTheTwoCardsAreInsideTheResultRegion()
    {
        var (_, _, body, page) = Show();
        var tab = Assert.IsType<CompareTabViewModel>(page.Tabs[4]);

        tab.SelectedGroupA = tab.GroupChoices[0];
        tab.SelectedGroupB = tab.GroupChoices[1];
        AnalysisSettle.Page(page);

        var region = Named<StackPanel>(body, "ResultRegion");
        Assert.True(region.IsEffectivelyVisible);

        foreach (var name in new[] { "CompareChart", "Verdict", "CardA", "CardB" })
        {
            var control = region.GetVisualDescendants().OfType<Control>().Single(candidate => candidate.Name == name);
            Assert.True(control.IsEffectivelyVisible, $"{name} is not drawn on a result.");
        }

        Assert.Equal(tab.Verdict, Named<TextBlock>(body, "Verdict").Text);

        // The not-comparable sentence is not on screen on a comparable result.
        Assert.False(Named<TextBlock>(body, "NotComparableNote").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void ANotComparableResult_DrawsTheSentenceAndNoVerdict()
    {
        var page = AnalysisShellHarness.Page(
            compare: (_, _, _, _, _, _) => new CompareResult(
                CompareState.Ok,
                "RC8 + ASI2600MM",
                "Askar FMA180 + ASI533MC",
                40,
                38,
                new CompareGroup("RC8 + ASI2600MM", AnalysisViewModelTestFactory.Boxes().Groups[0], AnalysisViewModelTestFactory.Stats()),
                new CompareGroup("Askar FMA180 + ASI533MC", AnalysisViewModelTestFactory.Boxes().Groups[1], AnalysisViewModelTestFactory.Stats()),
                null,
                false,
                null,
                null));

        var (_, _, body, shown) = Show(analysis: page);
        var tab = Assert.IsType<CompareTabViewModel>(shown.Tabs[4]);

        tab.SelectedGroupA = tab.GroupChoices[0];
        tab.SelectedGroupB = tab.GroupChoices[1];
        AnalysisSettle.Page(shown);

        var sentence = Named<TextBlock>(body, "NotComparableNote");
        Assert.True(sentence.IsEffectivelyVisible);
        Assert.Equal(CompareTabViewModel.NotComparableText, sentence.Text);
        Assert.False(Named<TextBlock>(body, "Verdict").IsEffectivelyVisible);
    }
}
