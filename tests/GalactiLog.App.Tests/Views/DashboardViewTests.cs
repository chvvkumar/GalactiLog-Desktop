using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.Views;
using GalactiLog.App.Views.Dashboard;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Spec 12.2's scan filter notice (PAR-014) as it renders on the Dashboard, Phase 14B Task 5.
// DashboardView.axaml had no dedicated view test before this file, only DashboardEmptyStateViewTests.
public class DashboardViewTests
{
    private static Window Show(Control view)
    {
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static DashboardViewModel Dashboard(ScanFilterConfig filters)
        => Dashboard(filters, Expanded());

    /// <summary>A stored panel that is open at <paramref name="width"/>, which the layout cases
    /// exercise; a fresh profile is collapsed (polish 1 ruling 4).</summary>
    private static DisplaySettings Expanded(int width = DashboardDisplaySettings.DefaultPanelWidth)
        => new()
        {
            Dashboard = new DashboardDisplaySettings { FilterPanelExpanded = true, FilterPanelWidth = width },
        };

    // One representative row, so the layout cases below have an Expand button and a pager to
    // measure. The figures are the target list suite's own SampleRow, three badges and a rig.
    private static readonly TargetRow SampleRow = new(
        GroupKey: "10000000-0000-0000-0000-000000000000",
        TargetId: Guid.Parse("10000000-0000-0000-0000-000000000000"),
        Name: "M 31",
        CommonName: "Andromeda Galaxy",
        CatalogId: "M 31",
        ObjectType: "G",
        ObjectCategory: "Galaxy",
        IntegrationSeconds: 44_640d,
        FrameCount: 148,
        SessionCount: 2,
        FirstSession: new DateOnly(2024, 1, 5),
        LastSession: new DateOnly(2025, 12, 7),
        Palette:
        [
            new FilterBadge("Ha", "#FF0000", 5, 1_500d),
            new FilterBadge("L", "#FFFFFF", 900, 27_000d),
            new FilterBadge("B", "not-a-colour", 40, 12_000d),
        ],
        Equipment: ["RC8 / ASI2600MM"],
        Aliases: [],
        Sessions: [new SessionSummary(new DateOnly(2025, 12, 7), 88, 26_400d)]);

    private static TargetListingPage OneRowPage(TargetListingCriteria criteria)
        => new([SampleRow], 120, 44_640d, 148, 1, 25);

    private static DashboardViewModel Dashboard(
        ScanFilterConfig filters,
        DisplaySettings display,
        Action<DisplaySettings>? saveDisplay = null,
        Func<TargetListingCriteria, TargetListingPage>? list = null)
    {
        var dashboard = new DashboardViewModel(
            list ?? (_ => DashboardViewModelTestFactory.EmptyPage),
            DashboardViewModelTestFactory.EmptyFacets,
            () => [],
            DashboardViewModelTestFactory.EmptyAliasMap,
            new GeneralSettings { ScanFilters = filters },
            (_, _) => Task.CompletedTask,
            initialDisplay: display,
            getDisplay: () => display,
            saveDisplay: saveDisplay ?? (_ => { }),
            post: action => action());
        dashboard.Filters.PendingReload!.GetAwaiter().GetResult();
        DashboardViewModelTestFactory.Settle(dashboard);
        return dashboard;
    }

    // Spec 12 shell paragraph and the brief's section 8: every layout figure here is the page's own
    // allotment inside the shell, never the whole window. The shipped window is 1280 by 800
    // (MainWindow.axaml) with a 200 pixel expanded navigation rail
    // (MainWindowViewModel.NavRailExpandedWidth) and a 32 pixel status bar, so the content region
    // is 1080 by 768; DashboardView's own Margin="16" leaves 1048 by 736 for its grid. At the
    // 1024 by 700 floor the same arithmetic gives 824 by 668.
    private const double StatusBarHeight = 32d;

    private static readonly Size ShippedAllotment = new(
        1280d - MainWindowViewModel.NavRailExpandedWidth,
        800d - StatusBarHeight);

    private static readonly Size FloorAllotment = new(
        1024d - MainWindowViewModel.NavRailExpandedWidth,
        700d - StatusBarHeight);

    // A real 1600 by 900 window, the allotment the two splitter cases below are laid out at since
    // the fixer pass. The panel's rendered width is now bounded by what the target list needs
    // (fixer-list item 6), and at the shipped 1280 that bound is 304, below the 480 maximum those
    // two cases exist to exercise; at 1600 it is 624, so the 220 to 480 clamp is reachable end to
    // end and neither case had to give up anything it proved. Both figures are asserted, not
    // asserted about, in TheLayoutBound_WhenTheWindowNarrows_IsNotReadAsADrag_AndWritesNothing.
    private static readonly Size WideAllotment = new(
        1600d - MainWindowViewModel.NavRailExpandedWidth,
        900d - StatusBarHeight);

    private static Window ShowAt(Control view, Size allotment)
    {
        var window = new Window { Width = allotment.Width, Height = allotment.Height, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static ColumnDefinition PanelColumn(DashboardView view)
        => view.GetControl<Grid>("DashboardRoot").ColumnDefinitions[0];

    private static ScanFilterConfig Seeded() => new() { NameRules = ScanFilterConfig.SeededRules() };

    private static string ViewSource() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "GalactiLog.App", "Views", "DashboardView.axaml"));

    // Re-pointed in Phase 14C, and re-named with what it now proves. Spec 12.2's corrected
    // placement (fixer-list item 34) moves the notice out of the collapsible left column and to
    // the top of the target list column, which makes the two figures this case carried false by
    // design: it asserted row 1 of the left column's inner grid, and that grid's verbatim
    // RowDefinitions string. The notice is now row 0 of the list column, and the left column's own
    // grid has lost the notice row.
    [AvaloniaFact]
    public void View_TheNotice_SitsAtTheTopOfTheTargetListColumn()
    {
        using var dashboard = Dashboard(Seeded());
        var view = new DashboardView { DataContext = dashboard };
        Show(view);

        var notice = view.GetControl<Border>("ScanFilterNoticeBanner");
        Assert.True(notice.IsEffectivelyVisible);

        // Row 0 of the list column, above the toolbar row, so a smaller row index is literally
        // "above" as it was before the move.
        Assert.Equal(0, Grid.GetRow(notice));
        Assert.Equal(1, Grid.GetRow(view.GetControl<StackPanel>("DashboardToolbar")));

        var source = ViewSource();

        // The left column is now heading, summary strip, panel; the list column carries the four
        // rows, the notice first.
        Assert.Contains("RowDefinitions=\"Auto,Auto,*\"", source, StringComparison.Ordinal);
        Assert.Contains(
            "Grid Grid.Column=\"2\" RowDefinitions=\"Auto,Auto,Auto,*\"",
            source,
            StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("x:Name=\"ScanFilterNoticeBanner\"", StringComparison.Ordinal)
            < source.IndexOf("x:Name=\"DashboardToolbar\"", StringComparison.Ordinal),
            "the notice must be declared above the toolbar row");

        // And it is out of the filter panel's column for good: nothing about the panel governs it.
        Assert.True(
            source.IndexOf("x:Name=\"FilterPanelSplitter\"", StringComparison.Ordinal)
            < source.IndexOf("x:Name=\"ScanFilterNoticeBanner\"", StringComparison.Ordinal),
            "the notice must be declared after the panel column, in the list column");
    }

    [AvaloniaFact]
    public void View_TheNotice_IsTheSharedCallout()
    {
        using var dashboard = Dashboard(Seeded());
        var view = new DashboardView { DataContext = dashboard };
        Show(view);

        var notice = view.GetControl<Border>("ScanFilterNoticeBanner");
        Assert.Contains("callout", notice.Classes);

        // It informs rather than warns, so it takes neither semantic arm.
        Assert.DoesNotContain("warn", notice.Classes);
        Assert.DoesNotContain("error", notice.Classes);

        // And it is gone once the stored rules are no longer only the seeded five.
        using var edited = Dashboard(ScanFilterConfig.Empty);
        var second = new DashboardView { DataContext = edited };
        Show(second);
        Assert.False(second.GetControl<Border>("ScanFilterNoticeBanner").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void View_TheNotice_HasOneActionNamedReview()
    {
        using var dashboard = Dashboard(Seeded());
        var view = new DashboardView { DataContext = dashboard };
        Show(view);

        var notice = view.GetControl<Border>("ScanFilterNoticeBanner");
        var buttons = notice.GetVisualDescendants().OfType<Button>().ToList();

        var review = Assert.Single(buttons);
        Assert.Equal("Review", review.Content);
        Assert.Same(view.GetControl<Button>("ScanFilterNoticeReviewButton"), review);

        // Departure 10: no "use defaults" beside it.
        Assert.Same(dashboard.ReviewScanFiltersCommand, review.Command);
    }

    // ControlStyleScanTest is the choke point for this across every view; the case here is the
    // local proof for the one view this task added a callout to (questions.md Q2).
    [AvaloniaFact]
    public void View_DeclaresNoCalloutStyleOfItsOwn()
    {
        var source = ViewSource();

        Assert.DoesNotContain("<Style Selector=\"Border.callout", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<Style Selector=\"Button", source, StringComparison.Ordinal);

        // And it uses the shared class, so the rule above is not passing vacuously.
        Assert.Contains("Classes=\"callout\"", source, StringComparison.Ordinal);
    }


    // ---- Phase 14C, spec 12.2's two panel states -------------------------------------------

    [AvaloniaFact]
    public void ThePanelColumn_IsFortyEightWide_WhenCollapsed()
    {
        // The measured column off the laid-out grid, never FilterPanelColumnWidth's own value: a
        // case that reads the view-model property proves the property and not the column, which is
        // the proxy shape carried fixer-list item 37 records nine times.
        using var dashboard = Dashboard(Seeded());
        var view = new DashboardView { DataContext = dashboard };
        ShowAt(view, ShippedAllotment);

        dashboard.ToggleFilterPanelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(DashboardViewModel.CollapsedStripWidth, PanelColumn(view).ActualWidth, 3);
        Assert.Equal(48d, PanelColumn(view).ActualWidth, 3);
    }

    [AvaloniaFact]
    public void ThePanelColumn_TakesTheStoredWidth_WhenExpanded()
    {
        using var dashboard = Dashboard(
            Seeded(),
            Expanded(360));

        var view = new DashboardView { DataContext = dashboard };
        ShowAt(view, WideAllotment);

        Assert.False(dashboard.IsFilterPanelCollapsed);
        Assert.Equal(360d, PanelColumn(view).ActualWidth, 3);
    }

    // The other half of the case above, and the reason it moved to a wider allotment: the same
    // stored 360 renders bounded at the shipped window, because 360 would leave the target list
    // short of a row (fixer-list item 6). The stored figure is untouched either way.
    [AvaloniaFact]
    public void ThePanelColumn_TakesTheBound_WhenTheStoredWidthWouldStarveTheList()
    {
        using var dashboard = Dashboard(
            Seeded(),
            Expanded(360),
            list: OneRowPage);

        var view = new DashboardView { DataContext = dashboard };
        ShowAt(view, ShippedAllotment);

        Assert.Equal(360d, dashboard.FilterPanelWidth, 3);
        Assert.True(dashboard.MaxRenderedPanelWidth < 360d, $"The bound is {dashboard.MaxRenderedPanelWidth}.");
        Assert.Equal(dashboard.MaxRenderedPanelWidth, PanelColumn(view).ActualWidth, 3);
        Assert.True(
            ListView(view).Bounds.Width >= TargetListView.ListMinWidth,
            $"The list is {ListView(view).Bounds.Width}.");
    }

    [AvaloniaFact]
    public void TheSplitter_IsPresentWhenExpanded_AndHiddenWhenCollapsed()
    {
        using var dashboard = Dashboard(Seeded());
        var view = new DashboardView { DataContext = dashboard };
        ShowAt(view, ShippedAllotment);

        var splitter = view.GetControl<GridSplitter>("FilterPanelSplitter");
        Assert.True(splitter.IsEffectivelyVisible);

        // Ruling E1's proxy, natively (questions.md Q4): the preview adorner is what keeps the
        // target list's shared-size spine off the drag path.
        Assert.True(splitter.ShowsPreview);

        dashboard.ToggleFilterPanelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(splitter.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void TheSplitter_DragsAProxy_AndDoesNotCommitUntilRelease()
    {
        // The case ruling E1 exists for. A case that only asserts the post-release figure passes
        // against a splitter that commits on every tick, so the stored width is read after each of
        // the two moves as well.
        var stored = Expanded();
        using var dashboard = Dashboard(Seeded(), stored, saved => stored = saved);

        var view = new DashboardView { DataContext = dashboard };
        var window = ShowAt(view, WideAllotment);

        var before = dashboard.FilterPanelWidth;
        Assert.Equal(DashboardDisplaySettings.DefaultPanelWidth, before, 3);

        // Not vacuous at this allotment: the layout bound is clear of the 480 maximum, so every
        // figure below is the 220 to 480 clamp's and not the bound's (fixer-list item 6).
        Assert.True(
            dashboard.MaxRenderedPanelWidth > DashboardDisplaySettings.MaxPanelWidth,
            $"The layout bound is {dashboard.MaxRenderedPanelWidth}, inside the clamp this case exercises.");

        var splitter = view.GetControl<GridSplitter>("FilterPanelSplitter");
        var origin = splitter.TranslatePoint(
            new Point(splitter.Bounds.Width / 2d, splitter.Bounds.Height / 2d), window)!.Value;

        window.MouseDown(origin, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        foreach (var step in new[] { 30d, 60d })
        {
            window.MouseMove(origin.WithX(origin.X + step));
            Dispatcher.UIThread.RunJobs();
            dashboard.PendingPanelPersist.GetAwaiter().GetResult();

            Assert.Equal(before, dashboard.FilterPanelWidth, 3);
            Assert.Equal(
                DashboardDisplaySettings.DefaultPanelWidth,
                stored.Dashboard.FilterPanelWidth);
        }

        window.MouseUp(origin.WithX(origin.X + 60d), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        dashboard.PendingPanelPersist.GetAwaiter().GetResult();

        Assert.Equal(before + 60d, dashboard.FilterPanelWidth, 3);
        Assert.Equal((int)(before + 60d), stored.Dashboard.FilterPanelWidth);
        Assert.Equal(before + 60d, PanelColumn(view).ActualWidth, 3);

        // Review P2. Every assertion below is on the laid-out column, never on the view-model
        // property, because the escape this catches is precisely the two disagreeing: the
        // GridSplitter writes a local GridLength straight onto the ColumnDefinition, and the
        // clamped figure only travels back down the binding when FilterPanelColumnWidth is raised.
        // A repeated over-drag clamps to the same 480 twice, so the second one changes nothing in
        // the view-model, and without the raise the column keeps the dragged figure and widens
        // again on every further drag.
        Drag(window, splitter, 300d);
        Assert.Equal(480d, PanelColumn(view).ActualWidth, 3);

        Drag(window, splitter, 200d);
        Assert.Equal(480d, PanelColumn(view).ActualWidth, 3);

        // The mirror at the floor, which escapes the same way.
        Drag(window, splitter, -400d);
        Assert.Equal(220d, PanelColumn(view).ActualWidth, 3);

        Drag(window, splitter, -200d);
        Assert.Equal(220d, PanelColumn(view).ActualWidth, 3);

        // And the binding survives the splitter's own local write to ColumnDefinition.Width: every
        // other collapse case in this file collapses a column that has never been dragged, so if
        // the local write detached the binding, collapse would silently stop working after any
        // drag and the whole suite would stay green.
        dashboard.ToggleFilterPanelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(48d, PanelColumn(view).ActualWidth, 3);

        dashboard.ToggleFilterPanelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(220d, PanelColumn(view).ActualWidth, 3);
        Assert.Equal(220, stored.Dashboard.FilterPanelWidth);
    }

    private static void Drag(Window window, GridSplitter splitter, double delta)
    {
        // Recomputed from the splitter's live bounds each time: a clamped drag moves the splitter
        // back, so a cached origin would aim at empty space on the next one.
        var origin = splitter.TranslatePoint(
            new Point(splitter.Bounds.Width / 2d, splitter.Bounds.Height / 2d), window)!.Value;

        window.MouseDown(origin, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(origin.WithX(origin.X + delta));
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(origin.WithX(origin.X + delta), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TheScanFilterNotice_RendersInBothPanelStates()
    {
        // Fixer-list item 34's own defect is a parent whose IsVisible hid a child whose own
        // IsVisible was true, so this asserts effective visibility rather than IsVisible.
        using var dashboard = Dashboard(Seeded());
        var view = new DashboardView { DataContext = dashboard };
        ShowAt(view, ShippedAllotment);

        Assert.True(dashboard.ShowScanFilterNotice);
        Assert.True(view.GetControl<Border>("ScanFilterNoticeBanner").IsEffectivelyVisible);

        dashboard.ToggleFilterPanelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(dashboard.IsFilterPanelCollapsed);
        Assert.True(view.GetControl<Border>("ScanFilterNoticeBanner").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void TheScanFilterNotice_SitsAboveTheToolbarRow_InTheListColumn()
    {
        using var dashboard = Dashboard(Seeded());
        var view = new DashboardView { DataContext = dashboard };
        ShowAt(view, ShippedAllotment);

        var notice = view.GetControl<Border>("ScanFilterNoticeBanner");
        var toolbar = view.GetControl<StackPanel>("DashboardToolbar");

        // Same parent, and a smaller row index.
        Assert.Same(notice.Parent, toolbar.Parent);
        Assert.True(Grid.GetRow(notice) < Grid.GetRow(toolbar));

        // In the list column, not the panel's.
        Assert.Equal(2, Grid.GetColumn((Control)notice.Parent!));
    }

    [AvaloniaFact]
    public void ThePage_DeclaresNoStyleOfItsOwn()
    {
        // Stricter than ControlStyleScanTest's four needles: this page declares no Style element at
        // all, so the two new Button.strip-item rules had to go to Theme/Controls.axaml.
        var source = ViewSource();

        Assert.DoesNotContain("<Style ", source, StringComparison.Ordinal);
        Assert.Contains("Classes=\"icon-glyph\"", source, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheCollapseChevron_IsADrawnPath_AndNotAGlyph()
    {
        // DESIGN.md section 8 refuses a Unicode glyph standing in for an icon. This is
        // The retired ledger chevron's shape applied to the dashboard's own chevron, and the two
        // guillemets the old build carried are gone with it.
        using var dashboard = Dashboard(Seeded());
        var view = new DashboardView { DataContext = dashboard };
        ShowAt(view, ShippedAllotment);

        var collapse = view.GetControl<Button>("FilterPanelCollapse");
        var mark = Assert.Single(collapse.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());
        Assert.Empty(collapse.GetVisualDescendants().OfType<TextBlock>());
        Assert.NotNull(mark.Data);
        Assert.NotNull(mark.Stroke);

        var source = ViewSource();
        Assert.DoesNotContain("&#x00AB;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("&#x00BB;", source, StringComparison.Ordinal);
    }

    // ---- fixer-list item 6: the panel is bounded by what the list needs ------------------------

    // Re-pointed by the fixer pass (phase-review P3 on this file). The case this replaces hosted
    // the page at the right allotment and then asserted the region's width was greater than zero,
    // which is true of any layout that does not collapse entirely, this one included: at the floor
    // the list was handed 448 pixels with the panel at 300 and 268 at 480, against a row that needs
    // TargetListView.ListMinWidth, so Last Session, the Expand button and the pager's page-size
    // select clipped off the trailing edge with no horizontal scroller to reach them. It now
    // asserts the two trailing edges, which is what the shell paragraph's claim actually is.
    [AvaloniaTheory]
    [InlineData(DashboardDisplaySettings.DefaultPanelWidth)]
    [InlineData(DashboardDisplaySettings.MaxPanelWidth)]
    public void AtTheWindowMinimumAllotment_TheExpandButtonAndThePageSizeSelect_AreInsideTheList(int storedWidth)
    {
        using var dashboard = Dashboard(
            Seeded(),
            Expanded(storedWidth),
            list: OneRowPage);

        var view = new DashboardView { DataContext = dashboard };
        ShowAt(view, FloorAllotment);

        var list = ListView(view);
        var toggle = list.GetVisualDescendants().OfType<Button>().First(button => button.Name == "SessionsToggle");
        var select = list.GetVisualDescendants().OfType<ComboBox>().First();

        Assert.True(list.Bounds.Width >= TargetListView.ListMinWidth, $"The list is {list.Bounds.Width}.");
        Assert.True(
            TrailingEdge(toggle, list) <= list.Bounds.Width + 0.5d,
            $"The Expand button ends at {TrailingEdge(toggle, list)} in a {list.Bounds.Width} list.");
        Assert.True(
            TrailingEdge(select, list) <= list.Bounds.Width + 0.5d,
            $"The page-size select ends at {TrailingEdge(select, list)} in a {list.Bounds.Width} list.");

        // The bound is layout only. The stored width is untouched, the stored state is still
        // expanded, and nothing was written to the display document.
        Assert.Equal(storedWidth, dashboard.FilterPanelWidth, 3);
        Assert.False(dashboard.IsFilterPanelCollapsed);
        Assert.True(dashboard.IsFilterPanelShowingStrip);
        Assert.Equal(DashboardViewModel.CollapsedStripWidth, PanelColumn(view).ActualWidth, 3);
    }

    [AvaloniaFact]
    public void TheLayoutBound_GivesTheStoredWidthBack_WhenTheWindowWidens()
    {
        var saves = 0;
        var display = Expanded(DashboardDisplaySettings.MaxPanelWidth);
        using var dashboard = Dashboard(Seeded(), display, saveDisplay: _ => saves++, list: OneRowPage);

        var view = new DashboardView { DataContext = dashboard };
        var window = ShowAt(view, FloorAllotment);
        Assert.True(dashboard.IsFilterPanelShowingStrip);

        window.Width = ShippedAllotment.Width;
        Dispatcher.UIThread.RunJobs();

        // 1048 for the grid inside DashboardView's own 16 margin, less the splitter column, the
        // list column's margin, the region's padding and edge and the list's minimum: 304, so the
        // stored 480 renders bounded rather than as the strip, and the list still has its minimum.
        Assert.False(dashboard.IsFilterPanelShowingStrip);
        var rendered = PanelColumn(view).ActualWidth;
        Assert.True(rendered >= DashboardDisplaySettings.MinPanelWidth, $"The panel renders at {rendered}.");
        Assert.True(rendered <= DashboardDisplaySettings.MaxPanelWidth, $"The panel renders at {rendered}.");
        Assert.True(
            ListView(view).Bounds.Width >= TargetListView.ListMinWidth,
            $"the bound left the list at {ListView(view).Bounds.Width}, under its minimum");

        // The stored width came back untouched and the bound wrote nothing on the way.
        Assert.Equal(DashboardDisplaySettings.MaxPanelWidth, dashboard.FilterPanelWidth, 3);
        Assert.Equal(0, saves);
    }

    // The trap in DashboardView.axaml.cs. The splitter's commit handler fires on every write to the
    // panel column's Width, and the layout bound writes that column too: if the handler reads the
    // bounded figure as a user drag, narrowing the window persists it and the stored width shrinks
    // with the window, which is exactly what fixer-list item 6 forbids. This narrows a window that
    // rendered the stored 480 whole down to one that cannot, and asserts the document.
    [AvaloniaFact]
    public void TheLayoutBound_WhenTheWindowNarrows_IsNotReadAsADrag_AndWritesNothing()
    {
        var saves = 0;
        var stored = Expanded(DashboardDisplaySettings.MaxPanelWidth);

        using var dashboard = Dashboard(
            Seeded(),
            stored,
            saved => { saves++; stored = saved; },
            OneRowPage);

        var view = new DashboardView { DataContext = dashboard };
        var window = ShowAt(view, WideAllotment);

        // 1368 for the grid inside DashboardView's own 16 margin, less the 6 pixel splitter column,
        // the list column's 8 margin and the region's 34 of padding and edge, less the list's own
        // 696 minimum: 624, clear of the 480 maximum, so the stored width renders whole here.
        Assert.Equal(624d, dashboard.MaxRenderedPanelWidth, 3);
        Assert.Equal(DashboardDisplaySettings.MaxPanelWidth, PanelColumn(view).ActualWidth, 3);

        window.Width = ShippedAllotment.Width;
        Dispatcher.UIThread.RunJobs();
        dashboard.PendingPanelPersist.GetAwaiter().GetResult();

        // The same arithmetic over the shipped window's 1048: 304, below the stored 480, so the
        // bound is what renders.
        Assert.Equal(304d, dashboard.MaxRenderedPanelWidth, 3);
        Assert.Equal(304d, PanelColumn(view).ActualWidth, 3);

        // And nothing of it reached the view-model or the document.
        Assert.Equal(DashboardDisplaySettings.MaxPanelWidth, dashboard.FilterPanelWidth, 3);
        Assert.False(dashboard.IsFilterPanelCollapsed);
        Assert.Equal(DashboardDisplaySettings.MaxPanelWidth, stored.Dashboard.FilterPanelWidth);
        Assert.True(stored.Dashboard.FilterPanelExpanded);
        Assert.Equal(0, saves);
    }

    // ---- The coordinator's second-pass ruling: an explicit gesture beats the bound ---------------
    //
    // AtTheWindowMinimumAllotment_ExpandingByHand_LeavesTheStrip stood here for one pass, pinning
    // the refusal this ruling reversed: it asserted that neither gesture changed what was drawn at
    // the floor. The four cases below replace it, and everything it proved survives among them and
    // in AtTheWindowMinimumAllotment_TheExpandButtonAndThePageSizeSelect_AreInsideTheList, which
    // holds the list at its 696 minimum while the strip is what renders.

    // The bound is what the application decides by itself, on load and on resize. A gesture the
    // user makes in the window in front of them wins: below about 1196 the panel would otherwise be
    // unreachable, with a chevron that toggles a stored flag and changes nothing on screen, which
    // reads as a broken button. Forced open, the panel takes its 220 floor and the list is clipped
    // at its trailing edge exactly as it was before the bound existed.
    [AvaloniaFact]
    public void AtTheWindowMinimumAllotment_TheChevron_ForcesThePanelOpenAtTwoTwenty()
    {
        var saves = 0;
        var stored = Expanded(DashboardDisplaySettings.DefaultPanelWidth);

        using var dashboard = Dashboard(Seeded(), stored, saved => { saves++; stored = saved; }, OneRowPage);

        var view = new DashboardView { DataContext = dashboard };
        var window = ShowAt(view, FloorAllotment);

        // It loads as the strip although the stored flag says expanded: that decision is the
        // application's own and the bound still governs it.
        Assert.True(stored.Dashboard.FilterPanelExpanded);
        Assert.Equal(48d, dashboard.MaxRenderedPanelWidth, 3);
        Assert.Equal(DashboardViewModel.CollapsedStripWidth, PanelColumn(view).ActualWidth, 3);

        dashboard.ToggleFilterPanelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        dashboard.PendingPanelPersist.GetAwaiter().GetResult();

        // One press, not two: the gesture opens the panel at the floor and the body is on screen.
        Assert.Equal(DashboardDisplaySettings.MinPanelWidth, PanelColumn(view).ActualWidth, 3);
        Assert.False(dashboard.IsFilterPanelShowingStrip);
        Assert.False(dashboard.IsFilterPanelCollapsed);
        Assert.True(PanelBody(view).IsEffectivelyVisible);

        // The splitter is reachable again while forced open, and a drag cannot widen it past the
        // floor, nor commit a width.
        Drag(window, view.GetControl<GridSplitter>("FilterPanelSplitter"), 200d);
        dashboard.PendingPanelPersist.GetAwaiter().GetResult();
        Assert.Equal(DashboardDisplaySettings.MinPanelWidth, PanelColumn(view).ActualWidth, 3);

        // The stored width was never touched: the forced state is a view flag and the bound writes
        // nothing. The stored flag keeps its meaning and was already true, so nothing was written
        // at all.
        Assert.Equal(DashboardDisplaySettings.DefaultPanelWidth, dashboard.FilterPanelWidth, 3);
        Assert.Equal(DashboardDisplaySettings.DefaultPanelWidth, stored.Dashboard.FilterPanelWidth);
        Assert.True(stored.Dashboard.FilterPanelExpanded);
        Assert.Equal(0, saves);
    }

    [AvaloniaFact]
    public void AtTheWindowMinimumAllotment_CollapsingAForcedPanel_ReturnsToTheStrip()
    {
        var stored = Expanded();
        using var dashboard = Dashboard(Seeded(), stored, saved => stored = saved, OneRowPage);

        var view = new DashboardView { DataContext = dashboard };
        ShowAt(view, FloorAllotment);

        dashboard.ToggleFilterPanelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(DashboardDisplaySettings.MinPanelWidth, PanelColumn(view).ActualWidth, 3);

        dashboard.ToggleFilterPanelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        dashboard.PendingPanelPersist.GetAwaiter().GetResult();

        Assert.Equal(DashboardViewModel.CollapsedStripWidth, PanelColumn(view).ActualWidth, 3);
        Assert.True(dashboard.IsFilterPanelCollapsed);

        // A collapse is the user saying collapsed, and it still means that in the document.
        Assert.False(stored.Dashboard.FilterPanelExpanded);
        Assert.Equal(DashboardDisplaySettings.DefaultPanelWidth, stored.Dashboard.FilterPanelWidth);
    }

    [AvaloniaFact]
    public void AtTheWindowMinimumAllotment_AStripLabel_ForcesThePanelOpenOnThatSection()
    {
        var stored = Expanded();
        using var dashboard = Dashboard(Seeded(), stored, saved => stored = saved, OneRowPage);

        var view = new DashboardView { DataContext = dashboard };
        ShowAt(view, FloorAllotment);

        var section = dashboard.Filters.Sections[5];
        section.IsExpanded = false;
        var others = dashboard.Filters.Sections.Select(each => each.IsExpanded).ToList();

        dashboard.ExpandFilterPanelOnCommand.Execute(section.Key);
        Dispatcher.UIThread.RunJobs();
        dashboard.PendingPanelPersist.GetAwaiter().GetResult();

        Assert.Equal(DashboardDisplaySettings.MinPanelWidth, PanelColumn(view).ActualWidth, 3);
        Assert.True(PanelBody(view).IsEffectivelyVisible);
        Assert.True(section.IsExpanded);

        // The other six are as they were, the gesture's own contract since spec 12.2.
        for (var index = 0; index < dashboard.Filters.Sections.Count; index++)
        {
            if (index != 5)
            {
                Assert.Equal(others[index], dashboard.Filters.Sections[index].IsExpanded);
            }
        }

        Assert.Equal(DashboardDisplaySettings.DefaultPanelWidth, stored.Dashboard.FilterPanelWidth);
    }

    [AvaloniaFact]
    public void ResizingTheWindow_ClearsTheForcedPanel_AndReAppliesTheBound()
    {
        var saves = 0;
        var stored = Expanded();
        using var dashboard = Dashboard(Seeded(), stored, saved => { saves++; stored = saved; }, OneRowPage);

        var view = new DashboardView { DataContext = dashboard };
        var window = ShowAt(view, FloorAllotment);

        dashboard.ToggleFilterPanelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(DashboardDisplaySettings.MinPanelWidth, PanelColumn(view).ActualWidth, 3);

        // Wide enough for the stored 300: the forced state is gone and the stored width is what
        // renders, not the 220 the gesture forced.
        window.Width = ShippedAllotment.Width;
        Dispatcher.UIThread.RunJobs();
        dashboard.PendingPanelPersist.GetAwaiter().GetResult();

        Assert.Equal(304d, dashboard.MaxRenderedPanelWidth, 3);
        Assert.Equal(DashboardDisplaySettings.DefaultPanelWidth, PanelColumn(view).ActualWidth, 3);

        // And back to the floor, where the bound governs again and the strip returns.
        window.Width = FloorAllotment.Width;
        Dispatcher.UIThread.RunJobs();
        dashboard.PendingPanelPersist.GetAwaiter().GetResult();

        Assert.True(dashboard.IsFilterPanelShowingStrip);
        Assert.Equal(DashboardViewModel.CollapsedStripWidth, PanelColumn(view).ActualWidth, 3);

        Assert.Equal(DashboardDisplaySettings.DefaultPanelWidth, stored.Dashboard.FilterPanelWidth);
        Assert.Equal(0, saves);
    }

    // The panel's own body, the control that is on screen only in the expanded state.
    private static ScrollViewer PanelBody(DashboardView view)
        => view.GetVisualDescendants().OfType<FilterPanelView>().First().GetControl<ScrollViewer>("FilterPanelBody");

    // Fixer-list item 21. The panel's two chevrons are the only way between the states, and neither
    // carried a name, so a screen reader announced "button" and nothing outside the process could
    // find them: Task 3's implementer could not drive the collapse, which is how the 48 pixel strip
    // beside the new rows went unseen. The expand chevron's own case is in FilterPanelViewTests.
    [AvaloniaFact]
    public void TheCollapseChevron_CarriesItsAutomationName()
    {
        using var dashboard = Dashboard(Seeded());
        var view = new DashboardView { DataContext = dashboard };
        ShowAt(view, ShippedAllotment);

        var collapse = view.GetControl<Button>("FilterPanelCollapse");
        Assert.Equal("Collapse filters", AutomationProperties.GetName(collapse));
    }

    private static double TrailingEdge(Visual control, Visual root)
        => control.TranslatePoint(new Point(control.Bounds.Width, 0), root)!.Value.X;

    // The list control itself, not TargetListRegion: that Border adds its own 16 padding and 1
    // pixel edge on each side, which is exactly the chrome the layout bound subtracts.
    private static TargetListView ListView(DashboardView view)
        => view.GetControl<Border>("TargetListRegion").GetVisualDescendants().OfType<TargetListView>().First();

    [AvaloniaFact]
    public void AtTheWindowMinimumAllotment_TheCollapsedPanelIsExactlyFortyEight()
    {
        using var dashboard = Dashboard(Seeded());
        var view = new DashboardView { DataContext = dashboard };
        ShowAt(view, FloorAllotment);

        // On load the bound alone renders the strip at this allotment, with no gesture at all.
        Assert.Equal(48d, PanelColumn(view).ActualWidth, 3);

        // The first press forces the panel open under the second-pass ruling, where it used to be
        // the collapse; the second puts it away. What this case pins, the strip's width at the
        // floor, is what it always was.
        dashboard.ToggleFilterPanelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        dashboard.ToggleFilterPanelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(48d, PanelColumn(view).ActualWidth, 3);
        Assert.True(view.GetControl<Border>("TargetListRegion").Bounds.Width > 0d);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalactiLog.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
