using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.Help;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.Views;
using GalactiLog.App.Views.Dashboard;
using GalactiLog.Core.Help;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Spec 12.15's eighth filter section as it renders, plus the dashboard page's card chrome
// (DESIGN.md section 4's carried amendment, fixer-list item 27). Phase 20 Task 5b.
//
// The chrome cases live here rather than in DashboardViewTests so the change is checked by a case
// that names it, not by a case about something else.
public class FilterPanelCustomSectionTests
{
    private static FilterPanelViewModel Panel(params CustomColumnDefinition[] columns)
    {
        var panel = new FilterPanelViewModel(
            DashboardViewModelTestFactory.EmptyAliasMap,
            DashboardViewModelTestFactory.EmptyFacets,
            () => [],
            post: action => action(),
            loadCustomColumns: () => columns);
        panel.Reload();
        panel.PendingReload!.GetAwaiter().GetResult();
        return panel;
    }

    private static FilterPanelView Show(FilterPanelViewModel panel, bool collapsed)
    {
        panel.IsPanelCollapsed = collapsed;
        var view = new FilterPanelView { DataContext = panel };
        var window = new Window { Width = 360, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static List<Button> StripItems(FilterPanelView view)
        => [.. view.GetControl<ItemsControl>("FilterStripSections").GetVisualDescendants().OfType<Button>()];

    private static string SourceOf(params string[] parts)
        => File.ReadAllText(Path.Combine([SourceScan.SrcRoot(), "GalactiLog.App", .. parts]));

    // ---- 24: the two states cannot disagree ---------------------------------------------

    private static ItemsControl Slot(FilterPanelView view)
        => view.GetControl<ItemsControl>("CustomFilterSectionSlot");

    [AvaloniaFact]
    public void TheStripAndTheBodyAgreeOnWhetherTheCustomSectionExists()
    {
        // With no custom column the body realizes no eighth Expander at all, not merely a hidden
        // one, and the strip draws seven items: both read the same one fact.
        var bare = Panel();
        var bareBody = Show(bare, collapsed: false);
        Assert.Empty(Slot(bareBody).GetVisualDescendants().OfType<Expander>());
        Assert.Equal(7, bareBody.GetVisualDescendants().OfType<Expander>().Count());
        Assert.Equal(7, StripItems(Show(bare, collapsed: true)).Count);

        var defined = Panel(CustomColumnTestFactory.Boolean("Processed"));
        var body = Show(defined, collapsed: false);
        Assert.Single(Slot(body).GetVisualDescendants().OfType<Expander>());
        Assert.Equal(8, body.GetVisualDescendants().OfType<Expander>().Count());

        var strip = StripItems(Show(defined, collapsed: true));
        Assert.Equal(defined.Sections.Count, strip.Count);
        Assert.Equal(8, strip.Count);

        // Cust is last, which is what "appended, never inserted" looks like on screen.
        Assert.Equal("custom", defined.Sections[^1].Key);
        Assert.Equal("Cust", defined.Sections[^1].ShortLabel);
    }

    [AvaloniaFact]
    public void TheSection_RealizesTheControlItsColumnTypeChooses()
    {
        var panel = Panel(
            CustomColumnTestFactory.Boolean("Processed"),
            CustomColumnTestFactory.Text("Notes tag", order: 1),
            CustomColumnTestFactory.Dropdown("High", "Low"));
        panel.CustomSection.IsExpanded = true;

        var view = Show(panel, collapsed: false);
        var section = Assert.Single(Slot(view).GetVisualDescendants().OfType<Expander>());

        Assert.True(section.IsEffectivelyVisible);

        // The three pills of the checkbox column, the Contains box and the combo box, all
        // realized rather than skipped: a template that cannot realize fails here.
        // Effectively visible, not merely realized: one row template serves all three kinds and
        // hides the two its column does not use, the same IsVisible idiom the Custom Columns tab
        // uses for its own options editor. A row that showed two controls at once would fail
        // here; a row that realized three but showed one is what the markup does on purpose.
        var buttons = section.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("pill") && button.IsEffectivelyVisible)
            .Select(button => (string)button.Content!)
            .ToList();
        Assert.Equal(["Any", "Yes", "No"], buttons);

        var box = Assert.Single(
            section.GetVisualDescendants().OfType<TextBox>(), candidate => candidate.IsEffectivelyVisible);
        Assert.Equal("Contains", box.Watermark);

        var combo = Assert.Single(
            section.GetVisualDescendants().OfType<ComboBox>(), candidate => candidate.IsEffectivelyVisible);
        Assert.Equal(["Any", "High", "Low"], combo.ItemsSource!.Cast<string>());

        // The active dot is the shared one, drawn from the section's own predicate.
        Assert.Single(section.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>());
    }

    // ---- 25: the shipped vocabulary, and no style of its own ----------------------------

    [Fact]
    public void TheSectionUsesTheShippedPillVocabularyAndDeclaresNoStyle()
    {
        var source = SourceOf("Views", "Dashboard", "FilterPanelView.axaml");

        // Button.pill, Border-free group headings in TextBlock.t-caption, and not one Style
        // element: the pill and the caption both come from Theme/Controls.axaml, which Task 3
        // owns this phase and which this unit does not open.
        Assert.Contains("Classes=\"pill\"", source, StringComparison.Ordinal);
        Assert.Contains("Classes=\"t-caption\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<Style ", source, StringComparison.Ordinal);

        // The unset choice is the section's own vocabulary and the reason a row can contribute no
        // clause at all; a template that offered only Yes and No would fail here.
        Assert.Contains("CommandParameter=\"Any\"", source, StringComparison.Ordinal);
    }

    // ---- 26: the help glyph -------------------------------------------------------------

    [Fact]
    public void ThePanel_CarriesTheDashboardCustomHelpGlyph()
    {
        var source = SourceOf("Views", "Dashboard", "FilterPanelView.axaml");

        var placed = HelpPlacementCensusTest.PlacedIn(source, "FilterPanelView.axaml");

        // Task 7 landed the record; this task places the glyph, once, in the section's own
        // heading row. A commented-out glyph is invisible to the census, so this is the same
        // reading HelpPlacementCensusTest takes.
        Assert.Single(placed, id => id == "dashboard.custom");
        Assert.True(HelpTopics.Ids.Contains("dashboard.custom"));
    }

    // ---- the eighth header shares the template -------------------------------------------

    [Fact]
    public void TheEighthHeader_ReusesTheSharedSectionHeaderTemplate()
    {
        // The eighth section's header draws through the same ContentControl and SectionHeader
        // template the other seven use, with the help glyph beside it, rather than a second copy
        // of the dot and the title. Red against that second copy: the accent dot's fill is
        // declared exactly once in the whole file, inside SectionHeader itself.
        var source = SourceOf("Views", "Dashboard", "FilterPanelView.axaml");

        Assert.Single(Regex.Matches(source, Regex.Escape("Fill=\"{DynamicResource ColorAccent}\"")));

        var slotStart = source.IndexOf("x:Name=\"CustomFilterSectionSlot\"", StringComparison.Ordinal);
        var headerEnd = source.IndexOf("</Expander.Header>", slotStart, StringComparison.Ordinal);
        var slotHeader = source[slotStart..headerEnd];
        Assert.Contains("ContentTemplate=\"{StaticResource SectionHeader}\"", slotHeader, StringComparison.Ordinal);
        Assert.Contains("controls:HelpButton", slotHeader, StringComparison.Ordinal);
    }

    // ---- 27: the card chrome ------------------------------------------------------------

    [Fact]
    public void DashboardView_CarriesNoNonZeroCornerRadius()
    {
        var source = SourceOf("Views", "DashboardView.axaml");

        // DESIGN.md section 4's carried amendment: the page shell around the moved rows was still
        // card chrome, six RadiusLg, RadiusMd and RadiusSm containers byte identical to their
        // pre-14C form. Red against the shipped file before this phase, which is the whole point:
        // without this case the next phase inherits the same finding again.
        Assert.DoesNotContain("CornerRadius=\"{DynamicResource Radius", source, StringComparison.Ordinal);

        // And no literal non-zero radius slipped in behind it.
        foreach (Match match in Regex.Matches(source, "CornerRadius=\"([^\"]*)\""))
        {
            Assert.Equal("0", match.Groups[1].Value);
        }

        // The vocabulary it moved into, named so a later rewrite back to a card fails here too.
        Assert.Contains("Classes=\"rule\"", source, StringComparison.Ordinal);
        Assert.Contains("Classes=\"tag\"", source, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(source, "Classes=\"callout\"").Count);
        Assert.DoesNotContain("<Style ", source, StringComparison.Ordinal);
    }

    // ---- 28: the four panel width pins, re-run against the edited page -------------------

    [AvaloniaFact]
    public void DashboardView_KeepsEveryPanelWidthPin()
    {
        // The same four figures DashboardViewTests pins at lines 239, 253, 354 and 361, measured
        // off the laid-out column rather than off the view-model property, and re-run here so the
        // chrome change is checked by a case that names it. Section 5 changes no padding, no
        // margin and no width; a figure that moves is a question for the coordinator, never an
        // expected value to edit.
        Assert.Equal(48d, MeasuredPanelWidth(stored: 300, collapse: true), 3);
        Assert.Equal(360d, MeasuredPanelWidth(stored: 360, collapse: false), 3);

        // The clamp's ceiling and floor, reached through the stored figure rather than through a
        // drag: DashboardDisplaySettings clamps on read, so both arrive at the column the same
        // way a clamped drag does.
        Assert.Equal(480d, MeasuredPanelWidth(stored: 999, collapse: false), 3);
        Assert.Equal(220d, MeasuredPanelWidth(stored: 100, collapse: false), 3);
    }

    private static double MeasuredPanelWidth(int stored, bool collapse)
    {
        using var dashboard = new DashboardViewModel(
            _ => DashboardViewModelTestFactory.EmptyPage,
            DashboardViewModelTestFactory.EmptyFacets,
            () => [],
            DashboardViewModelTestFactory.EmptyAliasMap,
            new GeneralSettings(),
            (_, _) => Task.CompletedTask,
            initialDisplay: new DisplaySettings
            {
                Dashboard = new DashboardDisplaySettings { FilterPanelExpanded = true, FilterPanelWidth = stored },
            },
            post: action => action());
        dashboard.Filters.PendingReload!.GetAwaiter().GetResult();
        DashboardViewModelTestFactory.Settle(dashboard);

        var view = new DashboardView { DataContext = dashboard };

        // The 1600 by 900 allotment DashboardViewTests' two clamp cases use, so the layout bound
        // (624 there) is outside the 220 to 480 clamp and the clamp is what the column shows.
        var window = new Window
        {
            Width = 1600d - MainWindowViewModel.NavRailExpandedWidth,
            Height = 900d - 32d,
            Content = view,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        if (collapse)
        {
            dashboard.ToggleFilterPanelCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
        }

        return view.GetControl<Grid>("DashboardRoot").ColumnDefinitions[0].ActualWidth;
    }
}
