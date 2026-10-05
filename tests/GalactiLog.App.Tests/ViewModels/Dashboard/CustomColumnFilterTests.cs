using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Dashboard;

// Spec 12.15's eighth filter section, Phase 20 Task 5b. The section is absent while the library
// holds no custom column (user choice 19), which is what keeps every existing filter pin unmoved,
// and the values it holds are session state like every other filter value (spec 12.2): nothing
// here writes a settings document.
//
// No database: the panel takes its definition list as a delegate, the same seam every other list
// on this view-model takes, and the definitions come from the assembly's one factory.
public class CustomColumnFilterTests
{
    private static readonly TargetListingCriteria Seed = new();

    // post runs the closure inline; the panel's own reload is awaited, so the returned panel has
    // already published whatever the delegate answered.
    private static FilterPanelViewModel CreatePanel(params CustomColumnDefinition[] columns)
        => CreatePanel(() => columns);

    private static FilterPanelViewModel CreatePanel(Func<IReadOnlyList<CustomColumnDefinition>> load)
    {
        var panel = new FilterPanelViewModel(
            DashboardViewModelTestFactory.EmptyAliasMap,
            DashboardViewModelTestFactory.EmptyFacets,
            () => [],
            post: action => action(),
            loadCustomColumns: load);
        panel.Reload();
        panel.PendingReload!.GetAwaiter().GetResult();
        return panel;
    }

    private static void Reload(FilterPanelViewModel panel)
    {
        panel.Reload();
        panel.PendingReload!.GetAwaiter().GetResult();
    }

    // ---- 16 to 17: the section appears and disappears -----------------------------------

    [Fact]
    public void WithNoCustomColumn_ThePanelHasSevenSections()
    {
        var panel = CreatePanel();

        // User choice 19: "a user who never defines one sees seven sections exactly as today".
        // Red against a section that is always appended, which would move every existing figure
        // in FilterPanelViewModelTests and FilterPanelViewTests at once.
        Assert.Equal(7, panel.Sections.Count);
        Assert.DoesNotContain(panel.CustomSection, panel.Sections);
        Assert.False(panel.HasCustomColumns);
        Assert.Empty(panel.CustomFilters);
        Assert.Empty(panel.CustomFilterGroups);
    }

    [Fact]
    public void PublishingOneDefinition_AppendsTheEighthSectionAtTheEnd()
    {
        IReadOnlyList<CustomColumnDefinition> defined = [CustomColumnTestFactory.Boolean("Processed")];
        var panel = CreatePanel(() => defined);

        Assert.Equal(8, panel.Sections.Count);

        // Appended, never inserted: the seven existing keys are still at their own indexes, which
        // is what FilterPanelViewModelTests' panel.Sections[3] reads and what a design that
        // inserted the section would break.
        Assert.Equal(
            ["search", "object_type", "date_range", "filters", "equipment", "metrics", "header_query", "custom"],
            panel.Sections.Select(section => section.Key));
        Assert.True(panel.HasCustomColumns);

        // And the last definition going takes the section with it.
        defined = [];
        Reload(panel);

        Assert.Equal(7, panel.Sections.Count);
        Assert.DoesNotContain(panel.CustomSection, panel.Sections);
        Assert.False(panel.HasCustomColumns);
    }

    // ---- 19: the groups ------------------------------------------------------------------

    [Fact]
    public void TheGroups_AreTargetThenNightThenRig_AndAnEmptyGroupIsNotDrawn()
    {
        var panel = CreatePanel(
            CustomColumnTestFactory.Define("Rig note", CustomColumnType.Text, CustomColumnScope.Rig, [], order: 0),
            CustomColumnTestFactory.Define("Second tag", CustomColumnType.Text, CustomColumnScope.Target, [], order: 5),
            CustomColumnTestFactory.Define("First tag", CustomColumnType.Text, CustomColumnScope.Target, [], order: 1),
            CustomColumnTestFactory.Define("Seeing", CustomColumnType.Text, CustomColumnScope.Session, [], order: 9));

        // The fixed heading order, and "Night" for the session scope (user choice 17). Red
        // against the web's own caption, which is "Session" (CustomColumnFilters.tsx).
        Assert.Equal(["Target", "Night", "Rig"], panel.CustomFilterGroups.Select(group => group.Title));

        // Within a group, display_order, not the order the definitions arrived in.
        Assert.Equal(
            ["First tag", "Second tag"],
            panel.CustomFilterGroups[0].Filters.Select(filter => filter.Label));

        // A scope with no column produces no group at all, so no empty heading is drawn.
        var targetOnly = CreatePanel(CustomColumnTestFactory.Boolean("Processed"));
        Assert.Equal(["Target"], targetOnly.CustomFilterGroups.Select(group => group.Title));
    }

    [Fact]
    public void EachKind_OffersItsOwnControlAndNothingElse()
    {
        var panel = CreatePanel(
            CustomColumnTestFactory.Boolean("Processed"),
            CustomColumnTestFactory.Text("Notes tag", order: 1),
            CustomColumnTestFactory.Dropdown("High", "Low"));

        var boolean = panel.CustomFilters.Single(filter => filter.Label == "Processed");
        var text = panel.CustomFilters.Single(filter => filter.Label == "Notes tag");
        var dropdown = panel.CustomFilters.Single(filter => filter.Label == "Priority");

        Assert.True(boolean.IsBoolean);
        Assert.False(boolean.IsText || boolean.IsDropdown);
        Assert.True(text.IsText);
        Assert.False(text.IsBoolean || text.IsDropdown);
        Assert.True(dropdown.IsDropdown);
        Assert.False(dropdown.IsBoolean || dropdown.IsText);

        // The combo box's first entry is the unset one, before the column's own options in the
        // user's order.
        Assert.Equal(["Any", "High", "Low"], dropdown.Choices);
    }

    // ---- 20 to 21: F3, the two unset shapes ----------------------------------------------

    [Fact]
    public void ABooleanFilterOnAny_ContributesNoCriteria()
    {
        var panel = CreatePanel(CustomColumnTestFactory.Boolean("Processed"));
        var filter = panel.CustomFilters.Single();

        Assert.Equal("Any", filter.BooleanChoice);
        Assert.False(filter.IsActive);

        var criteria = panel.BuildCriteria(Seed);

        // Red against a panel that projects every row: CustomFilterMode.Any would reach the
        // criteria, AnyFilterActive would report a filter active, and the query would contribute
        // no clause, so an empty library would show "No targets match these filters" with a Reset
        // button that clears nothing.
        Assert.Empty(criteria.CustomFilters);
        Assert.False(criteria.AnyFilterActive);
        Assert.False(panel.CustomSection.IsActive);

        // The other half: a chosen pill does reach it, so the case is not vacuous.
        filter.SetBooleanChoiceCommand.Execute("Yes");
        var active = panel.BuildCriteria(Seed);
        var projected = Assert.Single(active.CustomFilters);
        Assert.Equal(CustomFilterMode.Yes, projected.Mode);
        Assert.True(active.AnyFilterActive);
        Assert.True(panel.CustomSection.IsActive);
    }

    [Fact]
    public void AWhitespaceTextFilter_ContributesNoCriteria()
    {
        var panel = CreatePanel(CustomColumnTestFactory.Text("Notes tag"));
        var filter = panel.CustomFilters.Single();

        filter.Text = "   ";

        Assert.False(filter.IsActive);
        var blank = panel.BuildCriteria(Seed);
        Assert.Empty(blank.CustomFilters);
        Assert.False(blank.AnyFilterActive);

        // A real value reaches the criteria trimmed, so a Contains clause always carries a
        // non-empty text and the query never has to guard against one.
        filter.Text = "  wide field  ";
        var projected = Assert.Single(panel.BuildCriteria(Seed).CustomFilters);
        Assert.Equal(CustomFilterMode.Contains, projected.Mode);
        Assert.Equal("wide field", projected.Text);
    }

    [Fact]
    public void ADropdownOnAny_ContributesNoCriteria_AndAChosenOptionIsProjectedExactly()
    {
        var panel = CreatePanel(CustomColumnTestFactory.Dropdown("High", "Low"));
        var filter = panel.CustomFilters.Single();

        Assert.Equal("Any", filter.Selected);
        Assert.Empty(panel.BuildCriteria(Seed).CustomFilters);

        filter.Selected = "High";

        var projected = Assert.Single(panel.BuildCriteria(Seed).CustomFilters);
        Assert.Equal(CustomFilterMode.Equals, projected.Mode);
        Assert.Equal("High", projected.Text);
    }

    // ---- 22 to 23: the event count --------------------------------------------------------

    [Fact]
    public void Reset_ClearsEveryCustomFilterInOneChanged()
    {
        var panel = CreatePanel(
            CustomColumnTestFactory.Boolean("Processed"),
            CustomColumnTestFactory.Text("Notes tag", order: 1),
            CustomColumnTestFactory.Dropdown("High", "Low"));

        panel.CustomFilters.Single(filter => filter.IsBoolean).BooleanChoice = "Yes";
        panel.CustomFilters.Single(filter => filter.IsText).Text = "wide field";
        panel.CustomFilters.Single(filter => filter.IsDropdown).Selected = "High";
        Assert.True(panel.CustomSection.IsActive);

        var changed = 0;
        panel.Changed += (_, _) => changed++;

        panel.ResetCommand.Execute(null);

        // Red against a clear outside the suspend block, which would raise one Changed per
        // control and open the query window three times over for one press.
        Assert.Equal(1, changed);
        Assert.All(panel.CustomFilters, filter => Assert.False(filter.IsActive));
        Assert.False(panel.CustomSection.IsActive);
        Assert.False(panel.BuildCriteria(Seed).AnyFilterActive);
    }

    [Theory]
    [InlineData("boolean")]
    [InlineData("text")]
    [InlineData("dropdown")]
    public void AChangeInACustomFilter_RaisesChangedExactlyOnce(string kind)
    {
        var panel = CreatePanel(
            CustomColumnTestFactory.Boolean("Processed"),
            CustomColumnTestFactory.Text("Notes tag", order: 1),
            CustomColumnTestFactory.Dropdown("High", "Low"));

        var changed = 0;
        panel.Changed += (_, _) => changed++;

        switch (kind)
        {
            case "boolean":
                panel.CustomFilters.Single(filter => filter.IsBoolean).SetBooleanChoiceCommand.Execute("No");
                break;
            case "text":
                panel.CustomFilters.Single(filter => filter.IsText).Text = "clouds";
                break;
            default:
                panel.CustomFilters.Single(filter => filter.IsDropdown).Selected = "Low";
                break;
        }

        // One PropertyChanged per user change, which the panel's own OnChildChanged turns into
        // one Changed. Red against a NotifyPropertyChangedFor on a derived member (an IsActive or
        // a per-pill IsSelected), which would raise two or four and open the query window that
        // many times for one press.
        Assert.Equal(1, changed);
    }

    // ---- a refresh keeps what the user set ------------------------------------------------

    [Fact]
    public void ARefresh_KeepsEveryChosenValueBySlug()
    {
        IReadOnlyList<CustomColumnDefinition> defined =
        [
            CustomColumnTestFactory.Boolean("Processed"),
            CustomColumnTestFactory.Text("Notes tag", order: 1),
            CustomColumnTestFactory.Dropdown("High", "Low"),
        ];
        var panel = CreatePanel(() => defined);

        panel.CustomFilters.Single(filter => filter.IsBoolean).BooleanChoice = "Yes";
        panel.CustomFilters.Single(filter => filter.IsText).Text = "wide field";
        panel.CustomFilters.Single(filter => filter.IsDropdown).Selected = "High";

        // A rename keeps the slug (spec 12.15 never re-slugs), so the same three values survive a
        // post-scan reload. Without this every scan would silently clear the section.
        defined =
        [
            CustomColumnTestFactory.Boolean("Processed"),
            CustomColumnTestFactory.Text("Notes tag", order: 1),
            CustomColumnTestFactory.Dropdown("High", "Low"),
        ];
        Reload(panel);

        Assert.Equal("Yes", panel.CustomFilters.Single(filter => filter.IsBoolean).BooleanChoice);
        Assert.Equal("wide field", panel.CustomFilters.Single(filter => filter.IsText).Text);
        Assert.Equal("High", panel.CustomFilters.Single(filter => filter.IsDropdown).Selected);
        Assert.Equal(3, panel.BuildCriteria(Seed).CustomFilters.Count);
    }

    [Fact]
    public void ARefresh_KeepsAChosenOptionTheColumnNoLongerOffers()
    {
        IReadOnlyList<CustomColumnDefinition> defined = [CustomColumnTestFactory.Dropdown("High", "Low")];
        var panel = CreatePanel(() => defined);
        panel.CustomFilters.Single().Selected = "High";

        defined = [CustomColumnTestFactory.Dropdown("Low")];
        Reload(panel);

        // Spec 12.15: the filter is kept and applied, and matches nothing. Dropping it because
        // the option is gone would silently widen the result, and clearing the combo box because
        // its selection is not in the item source would do the same thing by accident.
        var filter = panel.CustomFilters.Single();
        Assert.Equal("High", filter.Selected);
        Assert.Contains("High", filter.Choices);
        Assert.True(filter.IsActive);
        Assert.Equal("High", Assert.Single(panel.BuildCriteria(Seed).CustomFilters).Text);
    }

    // ---- a republish that drops an ACTIVE row re-queries ----------------------------------
    //
    // Publish makes this comparison for the three library-derived lists; PublishCustomColumns did
    // not. Without it, a column deleted on the Custom Columns tab while a dashboard filter on it is
    // active leaves the rows on screen narrowed by a filter that has no control any more, every
    // section marker reading inactive, and Reset Filters clearing nothing that brings the hidden
    // targets back.
    //
    // Every count below is taken after the panel's own PendingReload has been awaited, so the
    // figure is bounded by that task rather than by a delay: a republish that reaches the query
    // has already raised Changed by the time Reload returns, and one that does not never will.

    [Fact]
    public void ADeletedColumn_WhoseTextFilterWasActive_ReQueriesOnce_AndClearsItsMarker()
    {
        IReadOnlyList<CustomColumnDefinition> defined = [CustomColumnTestFactory.Text("Notes tag")];
        var panel = CreatePanel(() => defined);
        panel.CustomFilters.Single().Text = "wide field";
        Assert.True(panel.BuildCriteria(Seed).AnyFilterActive);

        var changed = 0;
        panel.Changed += (_, _) => changed++;

        defined = [];
        Reload(panel);

        // Red against the landed PublishCustomColumns, which ended at RefreshSectionMarkers and
        // raised nothing: changed was 0, so the dashboard kept the narrowed page of rows while the
        // panel showed no filter at all. Exactly one, not two: the drop costs one query window, the
        // same as a user clearing the box by hand.
        Assert.Equal(1, changed);
        Assert.Empty(panel.CustomFilters);
        Assert.Empty(panel.BuildCriteria(Seed).CustomFilters);
        Assert.False(panel.BuildCriteria(Seed).AnyFilterActive);
        Assert.False(panel.CustomSection.IsActive);
        Assert.DoesNotContain(panel.CustomSection, panel.Sections);
    }

    [Fact]
    public void ADeletedColumn_WhoseDropdownFilterWasActive_ReQueriesOnce_AndDropsTheClause()
    {
        IReadOnlyList<CustomColumnDefinition> defined = [CustomColumnTestFactory.Dropdown("High", "Low")];
        var panel = CreatePanel(() => defined);
        panel.CustomFilters.Single().Selected = "High";

        var changed = 0;
        panel.Changed += (_, _) => changed++;

        defined = [];
        Reload(panel);

        // The same defect on the kind whose chosen value survives an option edit: it is the column
        // going, not the option going, that drops the clause.
        Assert.Equal(1, changed);
        Assert.Empty(panel.BuildCriteria(Seed).CustomFilters);
        Assert.False(panel.CustomSection.IsActive);
    }

    [Fact]
    public void ADropdownWhoseChosenOptionWasRemoved_KeepsItsClause_AndRaisesNoQuery()
    {
        IReadOnlyList<CustomColumnDefinition> defined = [CustomColumnTestFactory.Dropdown("High", "Low")];
        var panel = CreatePanel(() => defined);
        panel.CustomFilters.Single().Selected = "High";

        var changed = 0;
        panel.Changed += (_, _) => changed++;

        defined = [CustomColumnTestFactory.Dropdown("Low")];
        Reload(panel);

        // Spec 12.15: the filter is kept, applied, and matches nothing until the reader changes it,
        // so the clause that reaches the query is the one that was already there and the republish
        // is not a filter change. The marker stays active, which is what lets the reader see why
        // the list is empty. Red against a comparison that raises Changed on every republish, which
        // would cost a redundant query on the startup reload and on every finished scan.
        Assert.Equal(0, changed);
        Assert.Equal("High", Assert.Single(panel.BuildCriteria(Seed).CustomFilters).Text);
        Assert.True(panel.CustomSection.IsActive);
    }

    [Fact]
    public void ADeletedColumn_WhoseFilterWasInactive_RaisesNoQuery()
    {
        IReadOnlyList<CustomColumnDefinition> defined = [CustomColumnTestFactory.Boolean("Processed")];
        var panel = CreatePanel(() => defined);
        Assert.False(panel.CustomFilters.Single().IsActive);

        var changed = 0;
        panel.Changed += (_, _) => changed++;

        defined = [];
        Reload(panel);

        // The row contributed no clause, so its going alters nothing the query reads. Red against a
        // comparison over the ROWS rather than over the clauses they contribute, which would treat
        // every definition change as a filter change.
        Assert.Equal(0, changed);
        Assert.False(panel.HasCustomColumns);
    }

    [Fact]
    public void ARepublishOfTheSameColumns_RaisesNoQuery_AndKeepsTheTypedText()
    {
        IReadOnlyList<CustomColumnDefinition> defined = [CustomColumnTestFactory.Text("Notes tag")];
        var panel = CreatePanel(() => defined);
        panel.CustomFilters.Single().Text = "wide field";

        var changed = 0;
        panel.Changed += (_, _) => changed++;

        Reload(panel);

        // The ordinary case: a finished scan republishes the same definitions. Red against a
        // comparison that raises Changed unconditionally, which would make every scan cost a
        // second query for nothing.
        Assert.Equal(0, changed);
        Assert.Equal("wide field", panel.CustomFilters.Single().Text);
        Assert.Equal("wide field", Assert.Single(panel.BuildCriteria(Seed).CustomFilters).Text);
    }

    [Fact]
    public void AReorder_RaisesNoQuery()
    {
        IReadOnlyList<CustomColumnDefinition> defined =
        [
            CustomColumnTestFactory.Text("First tag", order: 1),
            CustomColumnTestFactory.Text("Second tag", order: 5),
        ];
        var panel = CreatePanel(() => defined);
        panel.CustomFilters.Single(filter => filter.Label == "First tag").Text = "one";
        panel.CustomFilters.Single(filter => filter.Label == "Second tag").Text = "two";

        var changed = 0;
        panel.Changed += (_, _) => changed++;

        defined =
        [
            CustomColumnTestFactory.Text("First tag", order: 5),
            CustomColumnTestFactory.Text("Second tag", order: 1),
        ];
        Reload(panel);

        // The clauses are ANDed, so their order reaches no result. This is why the comparison is a
        // SET, exactly as Publish compares its selected filter keys. Red against a sequence
        // comparison, which sees the two swapped projections as a change and re-queries for a drag
        // on the Custom Columns tab that cannot alter one row of the answer.
        Assert.Equal(0, changed);
        Assert.Equal(["Second tag", "First tag"], panel.CustomFilters.Select(filter => filter.Label));
        Assert.Equal(2, panel.BuildCriteria(Seed).CustomFilters.Count);
    }

    [Fact]
    public void ASlugWhoseTypeChanged_DropsTheClause_AndReQueriesOnce()
    {
        IReadOnlyList<CustomColumnDefinition> defined = [CustomColumnTestFactory.Text("Notes tag")];
        var panel = CreatePanel(() => defined);
        panel.CustomFilters.Single().Text = "wide field";

        var changed = 0;
        panel.Changed += (_, _) => changed++;

        // The same slug carrying another kind, which is AdoptFrom's early return: the new row is
        // unset, so the clause the criteria carried is gone. Reachable only from a hand-edited
        // catalogue (spec 12.15 never changes a column's type), and it is the second half of the
        // same defect: the panel must not keep a query narrowed by a value no control now holds.
        defined = [CustomColumnTestFactory.Boolean("Notes tag")];
        Reload(panel);

        Assert.Equal(1, changed);
        Assert.True(panel.CustomFilters.Single().IsBoolean);
        Assert.False(panel.CustomFilters.Single().IsActive);
        Assert.Empty(panel.BuildCriteria(Seed).CustomFilters);
        Assert.False(panel.CustomSection.IsActive);
    }

    [Fact]
    public void ResetFilters_AfterAnActiveColumnWasDeleted_LeavesNoFilterActive()
    {
        IReadOnlyList<CustomColumnDefinition> defined = [CustomColumnTestFactory.Text("Notes tag")];
        var panel = CreatePanel(() => defined);
        panel.CustomFilters.Single().Text = "wide field";

        var changed = 0;
        panel.Changed += (_, _) => changed++;

        defined = [];
        Reload(panel);
        panel.ResetCommand.Execute(null);

        // The whole journey of P1-1, end to end: one query for the drop and one for the press. Red
        // against the landed PublishCustomColumns at 1 rather than 2, which is the user-visible
        // symptom: the drop issued nothing, so the rows stayed narrowed, and Reset Filters was the
        // first thing that widened them again instead of the second.
        Assert.Equal(2, changed);
        Assert.False(panel.BuildCriteria(Seed).AnyFilterActive);
        Assert.Empty(panel.BuildCriteria(Seed).CustomFilters);
        Assert.All(panel.Sections, section => Assert.False(section.IsActive));
    }
}
