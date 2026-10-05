using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.Views;
using GalactiLog.App.Views.Dashboard;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke tests for the filter panel: it parses, lays out, and binds
// against a populated view-model. Compiled bindings already turn a binding-path typo into a build
// error; these catch the rest (a missing resource, a template that cannot realize).
public class FilterPanelViewTests
{
    // post runs inline and the debounce completes immediately, so the only thing to wait for is
    // the filter panel's one background reload of its option lists.
    private static DashboardViewModel CreateDashboard()
    {
        var dashboard = new DashboardViewModel(
            _ => new TargetListingPage([], 3, 7200d, 120, 1, 50),
            () => new DashboardFacets(
                [new FilterFacet("Ha", "#FF0000", 12), new FilterFacet("Broken", "not-a-colour", 3)],
                ["ASI2600MM"],
                ["RC8"]),
            () => ["OBJECT", "GAIN"],
            DashboardViewModelTestFactory.EmptyAliasMap,
            new GeneralSettings(),
            (_, _) => Task.CompletedTask,
            post: action => action());
        dashboard.Filters.PendingReload!.GetAwaiter().GetResult();
        // The realisation cases render the open panel; a fresh profile is collapsed.
        dashboard.IsFilterPanelCollapsed = false;
        return dashboard;
    }

    [AvaloniaFact]
    public void FilterPanelView_Constructs_AndBindsToAPopulatedViewModel()
    {
        var dashboard = CreateDashboard();
        var panel = dashboard.Filters;

        // Every section expanded, so each template is actually realized rather than skipped.
        foreach (var section in panel.Sections)
        {
            section.IsExpanded = true;
        }

        panel.Filters[0].IsSelected = true;
        panel.ObjectTypes[0].IsSelected = true;
        panel.PinnedLabel = "M 31";
        panel.DraftHeaderKey = "OBJECT";
        panel.DraftHeaderValue = "NGC";
        panel.AddHeaderConditionCommand.Execute(null);

        var view = new FilterPanelView { DataContext = panel };
        var window = new Window { Width = 360, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
        Assert.Equal(7, view.GetVisualDescendants().OfType<Expander>().Count());
        Assert.Contains(
            view.GetVisualDescendants().OfType<Button>(),
            button => button.Content as string == "Reset Filters");
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "OBJECT contains NGC" || text.Text == "OBJECT = NGC");
    }

    [AvaloniaFact]
    public void DashboardView_ShowsTheSummaryStripAndTheFilterPanel()
    {
        var dashboard = CreateDashboard();
        var view = new DashboardView { DataContext = dashboard };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(view.GetVisualDescendants().OfType<FilterPanelView>());
        Assert.Contains(
            view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == dashboard.GroupsLabel);
    }

    [AvaloniaFact]
    public void State_SurvivesReconstructionOfTheView()
    {
        // The singleton view-model is the whole of spec 12.2's "persist
        // for the session". Rebuilding only the view restores every value.
        var dashboard = CreateDashboard();
        dashboard.Filters.SearchText = "andromeda";
        dashboard.Filters.Filters[0].IsSelected = true;
        dashboard.Filters.MetricsSection.IsExpanded = true;

        var first = new Window { Width = 1280, Height = 800, Content = new DashboardView { DataContext = dashboard } };
        first.Show();
        Dispatcher.UIThread.RunJobs();
        first.Close();

        var rebuilt = new DashboardView { DataContext = dashboard };
        var second = new Window { Width = 1280, Height = 800, Content = rebuilt };
        second.Show();
        Dispatcher.UIThread.RunJobs();

        var panel = Assert.Single(rebuilt.GetVisualDescendants().OfType<FilterPanelView>());
        var bound = Assert.IsType<FilterPanelViewModel>(panel.DataContext);
        Assert.Same(dashboard.Filters, bound);
        Assert.Equal("andromeda", bound.SearchText);
        Assert.True(bound.Filters[0].IsSelected);
        Assert.True(bound.MetricsSection.IsExpanded);
    }

    // Task 8, spec 12.2's results dropdown: primary name, object type category, the matched
    // alias, and an unresolved row's frame count.
    [AvaloniaFact]
    public void SearchResultsDropdown_RendersEachRow_AndSelectingOnePins()
    {
        var dashboard = CreateDashboard();
        var panel = dashboard.Filters;
        panel.SearchSection.IsExpanded = true;
        panel.PublishSearchResults(
        [
            new SearchResultViewModel(new TargetSearchResult(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                null, "M 31", "G", "Andromeda Galaxy", 0, 0.83)),
            new SearchResultViewModel(new TargetSearchResult(
                null, "Sh2-155", "Sh2-155", null, "Sh2-155", 3, 1.0)),
        ]);

        var view = new FilterPanelView { DataContext = panel };
        var window = new Window { Width = 360, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var dropdown = view.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SearchResultsDropdown");
        Assert.True(dropdown.IsEffectivelyVisible);
        var texts = dropdown.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains("M 31", texts);
        Assert.Contains("Galaxy", texts);
        Assert.Contains("matched Andromeda Galaxy", texts);
        Assert.Contains("Sh2-155", texts);
        Assert.Contains("3 frames", texts);

        // Review item 2. The three secondary lines used to bind FontSizeCaption, which is a ratio
        // (0.714), not a point size, and rendered at well under a pixel.
        var blocks = dropdown.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.True(
            block.FontSize >= 8,
            $"TextBlock '{block.Text}' renders at {block.FontSize}."));

        // The row button carries the panel's own command, not a second selection path.
        var row = dropdown.GetVisualDescendants().OfType<Button>().First();
        row.Command!.Execute(row.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), panel.PinnedTargetId);
        Assert.False(panel.IsSearchDropdownOpen);
    }

    // ---- Phase 14C, spec 12.2's collapsed 48 pixel strip -------------------------------------

    // IsVisible="False" keeps a subtree in the visual tree but never measures it, so the honest
    // question is which state is on screen rather than which objects exist. It is still
    // falsifiable in the direction that matters: the strip's items are realized by a measure, so a
    // strip that renders beside the expanded body puts seven buttons and seven expanders on screen
    // at once and every case below fails.
    private static FilterPanelView Collapsed(DashboardViewModel dashboard)
    {
        dashboard.IsFilterPanelCollapsed = true;

        var view = new FilterPanelView { DataContext = dashboard.Filters };
        var window = new Window { Width = 48, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static List<Button> StripItems(FilterPanelView view)
        => view.GetControl<ItemsControl>("FilterStripSections")
            .GetVisualDescendants()
            .OfType<Button>()
            .ToList();

    private static void Click(FilterPanelView view, Button button)
    {
        button.Command!.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TheCollapsedStrip_ShowsOneItemPerFilterSection()
    {
        // Asserted against Sections.Count rather than the literal seven, so an eighth section later
        // moves one figure and not two.
        var dashboard = CreateDashboard();
        var view = Collapsed(dashboard);

        Assert.Equal(dashboard.Filters.Sections.Count, StripItems(view).Count);
    }

    [AvaloniaFact]
    public void TheCollapsedStrip_ShowsADrawnIcon_AndTheFullLabelOnItsTooltip()
    {
        // The strip draws an icon per section, never the four-letter
        // label. A template that still shows the TextBlock, or draws a Path with no geometry,
        // fails here.
        var dashboard = CreateDashboard();
        var view = Collapsed(dashboard);

        var items = StripItems(view);

        foreach (var (section, button) in dashboard.Filters.Sections.Zip(items))
        {
            var icon = Assert.Single(button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(), p => p.IsEffectivelyVisible);
            Assert.NotNull(icon.Data);
            var panel = button.GetVisualDescendants().OfType<Panel>().First(p => p.GetType() == typeof(Panel));
            Assert.Equal(section.Title, ToolTip.GetTip(panel));
            Assert.DoesNotContain(button.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible);
        }
    }

    [AvaloniaFact]
    public void TheCollapsedStrip_MarksAnActiveSection()
    {
        // DESIGN.md section 6: "the open or active item in the accent ink". The requirement is the
        // ink, not the class, so this asserts the label's resolved Foreground as well. The class
        // alone passes against a Button.strip-item.active style that was never declared, that
        // carries no setter, or whose descendant selector fails to reach the label through the
        // ContentControl and its two content presenters (review P3). Same shape as the ledger
        // strip's own two-ink case.
        var dashboard = CreateDashboard();
        dashboard.Filters.SearchText = "andromeda";
        Dispatcher.UIThread.RunJobs();

        var view = Collapsed(dashboard);
        var items = StripItems(view);

        Assert.True(dashboard.Filters.SearchSection.IsActive);
        Assert.False(dashboard.Filters.ObjectTypeSection.IsActive);
        Assert.Contains("active", items[0].Classes);
        Assert.DoesNotContain("active", items[1].Classes);

        var accent = TokenColor(view, "ColorAccent");
        Assert.Equal(accent, LabelInk(items[0]));
        Assert.NotEqual(accent, LabelInk(items[1]));
    }

    private static Color TokenColor(FilterPanelView view, string key)
    {
        Assert.True(view.TryFindResource(key, out var resource));
        return ((ISolidColorBrush)resource!).Color;
    }

    private static Color LabelInk(Button item)
    {
        var icon = Assert.Single(item.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(), p => p.IsEffectivelyVisible);
        return ((ISolidColorBrush)icon.Stroke!).Color;
    }

    [AvaloniaFact]
    public void TheCollapsedStrip_RealizesNoExpander()
    {
        var dashboard = CreateDashboard();
        var view = Collapsed(dashboard);

        Assert.DoesNotContain(
            view.GetVisualDescendants().OfType<Expander>(),
            section => section.IsEffectivelyVisible);
        Assert.False(view.GetControl<ScrollViewer>("FilterPanelBody").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void ClickingAStripItem_ExpandsThePanel_OnThatSection()
    {
        var dashboard = CreateDashboard();
        var view = Collapsed(dashboard);

        var metrics = StripItems(view)[5];
        Assert.Equal("metrics", metrics.CommandParameter);

        Click(view, metrics);

        Assert.False(dashboard.IsFilterPanelCollapsed);
        Assert.True(dashboard.Filters.MetricsSection.IsExpanded);
        Assert.True(view.GetControl<ScrollViewer>("FilterPanelBody").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void ClickingAStripItem_LeavesTheOtherSixSectionsAsTheyWere()
    {
        var dashboard = CreateDashboard();
        dashboard.Filters.DateRangeSection.IsExpanded = true;

        var before = dashboard.Filters.Sections.Select(section => section.IsExpanded).ToList();
        var view = Collapsed(dashboard);

        Click(view, StripItems(view)[5]);

        for (var index = 0; index < dashboard.Filters.Sections.Count; index++)
        {
            var expected = index == 5 || before[index];
            Assert.Equal(expected, dashboard.Filters.Sections[index].IsExpanded);
        }
    }

    [AvaloniaFact]
    public void TheStripChevron_ReopensThePanel_WithNoSectionChange()
    {
        var dashboard = CreateDashboard();
        dashboard.Filters.DateRangeSection.IsExpanded = true;

        var before = dashboard.Filters.Sections.Select(section => section.IsExpanded).ToList();
        var view = Collapsed(dashboard);

        var chevron = view.GetControl<Button>("FilterStripToggle");

        // A drawn Path and no text, the same convention the ledger strip's own toggle follows.
        Assert.Single(chevron.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());
        Assert.Empty(chevron.GetVisualDescendants().OfType<TextBlock>());
        Assert.Equal("Show the filter panel", ToolTip.GetTip(chevron));

        Click(view, chevron);

        Assert.False(dashboard.IsFilterPanelCollapsed);
        Assert.Equal(before, dashboard.Filters.Sections.Select(section => section.IsExpanded).ToList());
    }

    // Fixer-list item 21, the strip's half. The tooltip is a hover affordance and nothing reads it
    // as a name; without this the one control that brings the filter panel back announces "button"
    // and cannot be found from outside the process.
    [AvaloniaFact]
    public void TheStripChevron_CarriesItsAutomationName()
    {
        var dashboard = CreateDashboard();
        var view = Collapsed(dashboard);

        Assert.Equal(
            "Expand filters",
            AutomationProperties.GetName(view.GetControl<Button>("FilterStripToggle")));
    }

    [AvaloniaFact]
    public void OnlyOneStateIsRealizedAtATime()
    {
        // The falsifiable companion to the seven-Expander count above: a panel that renders both
        // states puts seven expanders and seven strip items on screen together.
        var dashboard = CreateDashboard();

        var expanded = new FilterPanelView { DataContext = dashboard.Filters };
        var window = new Window { Width = 360, Height = 900, Content = expanded };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(expanded.GetControl<ScrollViewer>("FilterPanelBody").IsEffectivelyVisible);
        Assert.False(expanded.GetControl<Grid>("FilterStrip").IsEffectivelyVisible);
        Assert.Empty(StripItems(expanded));

        var collapsed = Collapsed(dashboard);

        Assert.False(collapsed.GetControl<ScrollViewer>("FilterPanelBody").IsEffectivelyVisible);
        Assert.True(collapsed.GetControl<Grid>("FilterStrip").IsEffectivelyVisible);
        Assert.Equal(7, StripItems(collapsed).Count);
        Assert.DoesNotContain(
            collapsed.GetVisualDescendants().OfType<Expander>(),
            section => section.IsEffectivelyVisible);
    }
}
