using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Targets;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 14A Task 6: spec 12.4's object type edit (PAR-009). The pencil, the combo box and the
// write live on TargetHeaderViewModel, which the page builds, so these drive the page's own header
// and assert against the harness's recorded writes. No database and no window.
public class ObjectTypeEditTests
{
    private static TargetHeaderViewModel Header(Factory.Harness harness)
    {
        Assert.NotNull(harness.ViewModel.Header);
        return harness.ViewModel.Header!;
    }

    private static Factory.Harness Unresolved() => Factory.Create(
        get: _ => Factory.PopulatedDetail(header: Factory.UnresolvedHeader()),
        groupKey: Factory.UnresolvedGroupKey);

    [Fact]
    public void TheChoices_AreSpecNinePointEightsNineCategoriesPlusOther()
    {
        // Compared against the array the dashboard's Object Type pills and the Statistics
        // breakdowns read, not against a retyped list, so the two cannot drift.
        using var harness = Factory.Create().Settle();

        Assert.Equal(
            [.. ObjectTypeCategories.DisplayCategories, "Other"],
            Header(harness).ObjectTypeChoices);
    }

    [Fact]
    public void TheChoices_AreInTheTablesOrder()
    {
        using var harness = Factory.Create().Settle();

        Assert.Equal(
            [
                "Emission Nebula",
                "Reflection Nebula",
                "Dark Nebula",
                "Planetary Nebula",
                "Supernova Remnant",
                "Galaxy",
                "Open Cluster",
                "Globular Cluster",
                "Star",
                "Other",
            ],
            Header(harness).ObjectTypeChoices);
    }

    [Fact]
    public void TheChoices_WithholdUnresolved()
    {
        // Spec 12.4: Unresolved describes a frame with no target rather than a target, so it is
        // not something a target can be set to.
        using var harness = Factory.Create().Settle();

        Assert.DoesNotContain("Unresolved", Header(harness).ObjectTypeChoices);
    }

    [Fact]
    public void TheChoices_WithholdTheFiveSolarSystemCategories()
    {
        // task1-report open question 6, ruled as proposed: the five belong to user_defined targets
        // the name-pattern classifier of spec 9.2 created, which are already categorised.
        using var harness = Factory.Create().Settle();

        foreach (var category in ObjectTypeCategories.SolarSystemCategories)
        {
            Assert.DoesNotContain(category, Header(harness).ObjectTypeChoices);
        }
    }

    [Fact]
    public void AnUnresolvedGroup_CannotEdit()
    {
        // Spec 12.4: an obj: group has no targets row to write, so the pencil is absent rather
        // than disabled, and the row it would sit in is not drawn at all.
        using var harness = Unresolved().Settle();
        var header = Header(harness);

        Assert.False(header.CanEditObjectType);
        Assert.False(header.BeginEditObjectTypeCommand.CanExecute(null));
        Assert.False(header.ShowObjectTypeRow);
    }

    [Fact]
    public void BeginEdit_ShowsTheComboBoxAndHidesTheValue()
    {
        using var harness = Factory.Create().Settle();
        var header = Header(harness);

        Assert.False(header.IsEditingObjectType);

        header.BeginEditObjectTypeCommand.Execute(null);

        Assert.True(header.IsEditingObjectType);

        // Unseeded (Task 6 review P3), with the target's current category carried by the box's
        // PlaceholderText instead, so every entry in the list is a real selection and the entry
        // the target already has closes the editor rather than doing nothing.
        Assert.Null(header.SelectedObjectTypeChoice);
        Assert.Equal("Galaxy", header.ObjectCategory);
        Assert.Empty(harness.ObjectTypeWrites);
    }

    [Fact]
    public void ChoosingAnEntry_CommitsAtOnce_AndSwapsBack()
    {
        using var harness = Factory.Create().Settle();
        var header = Header(harness);
        header.BeginEditObjectTypeCommand.Execute(null);

        header.SelectedObjectTypeChoice = "Emission Nebula";

        var write = Assert.Single(harness.ObjectTypeWrites);
        Assert.Equal(Factory.TargetId, write.TargetId);
        Assert.Equal("Emission Nebula", write.Category);
        Assert.False(header.IsEditingObjectType);
    }

    [Fact]
    public void ChoosingAnEntry_UpdatesTheDisplayedValueWithoutAReload()
    {
        // Spec 12.4: the page reloads on a scan and not on a write, so a value that reverted on
        // screen until the next scan would read as a failed write.
        using var harness = Factory.Create().Settle();
        var loadsBefore = harness.Loads;
        var header = Header(harness);
        header.BeginEditObjectTypeCommand.Execute(null);

        header.SelectedObjectTypeChoice = "Emission Nebula";

        Assert.Equal("Emission Nebula", header.ObjectCategory);
        Assert.Equal("HII", header.ObjectType);
        Assert.True(header.HasObjectType);
        Assert.Equal(loadsBefore, harness.Loads);
    }

    [Fact]
    public void Cancel_LeavesTheStoredValueAlone()
    {
        using var harness = Factory.Create().Settle();
        var header = Header(harness);
        header.BeginEditObjectTypeCommand.Execute(null);

        header.CancelObjectTypeCommand.Execute(null);

        Assert.False(header.IsEditingObjectType);
        Assert.Empty(harness.ObjectTypeWrites);
        Assert.Equal("G", header.ObjectType);
        Assert.Equal("Galaxy", header.ObjectCategory);

        // Clearing the selection after the box closed must not commit an empty choice.
        Assert.Null(header.SelectedObjectTypeChoice);
        Assert.Empty(harness.ObjectTypeWrites);
    }

    [Fact]
    public void BeginEdit_ExecutedDirectlyOnAnUnresolvedGroup_DoesNothing()
    {
        // TRACKING item 13: RelayCommand.Execute ignores CanExecute, so the guard is in the body
        // as well and this case runs the command directly to prove it.
        using var harness = Unresolved().Settle();
        var header = Header(harness);

        header.BeginEditObjectTypeCommand.Execute(null);

        Assert.False(header.IsEditingObjectType);
        Assert.Null(header.SelectedObjectTypeChoice);
        Assert.Empty(harness.ObjectTypeWrites);
    }

    [Fact]
    public void Commit_ExecutedDirectlyWithNoSelection_WritesNothing()
    {
        using var harness = Factory.Create().Settle();
        var header = Header(harness);

        Assert.False(header.CommitObjectTypeCommand.CanExecute(null));
        header.CommitObjectTypeCommand.Execute(null);

        Assert.Empty(harness.ObjectTypeWrites);
        Assert.Equal("G", header.ObjectType);
        Assert.Equal("Galaxy", header.ObjectCategory);
    }

    [Fact]
    public void Other_LeavesTheRowAndThePencilOnScreen()
    {
        // Other stores the empty string (spec 9.8), which is what the Details panel would have
        // read as "no object type" and hidden, taking the pencil with it. An edit the user cannot
        // undo is worse than an empty value beside a label.
        using var harness = Factory.Create().Settle();
        var header = Header(harness);
        header.BeginEditObjectTypeCommand.Execute(null);

        header.SelectedObjectTypeChoice = "Other";

        Assert.Equal("", header.ObjectType);
        Assert.False(header.HasObjectType);
        Assert.True(header.ShowObjectTypeRow);
        Assert.True(header.CanEditObjectType);
    }

    [Fact]
    public void ANullWriteDelegate_CommitsNothingAndThrowsNothing()
    {
        // The header is constructible with no writer at all, which is what every test that is not
        // about the edit gets, and the CLI has no pages.
        //
        // Phase review P3-5: the display follows the write and never leads it. The commit used to
        // set ObjectType and ObjectCategory unconditionally after a null-conditional invoke, so
        // with no delegate the page reported a change the database did not have until the next
        // reload. The editor still closes, because the edit is over.
        var header = new TargetHeaderViewModel(Factory.PopulatedHeader());
        header.BeginEditObjectTypeCommand.Execute(null);

        header.SelectedObjectTypeChoice = "Star";

        Assert.False(header.IsEditingObjectType);
        Assert.Equal("G", header.ObjectType);
        Assert.Equal("Galaxy", header.ObjectCategory);
    }

    [Fact]
    public void ChoosingTheCategoryTheTargetAlreadyHas_ClosesTheEditorAndWritesNothing()
    {
        // Task 6 review P3. The box used to open seeded on the current category, so choosing that
        // entry was not a change, nothing committed, and the editor stayed on screen looking
        // stuck. It opens unseeded now, with the current category as its placeholder, so that
        // entry is a selection like any other and closes the editor. It is still not a write: the
        // stored code would not change and an activity event saying a type changed to itself would
        // be false.
        using var harness = Factory.Create().Settle();
        var header = Header(harness);

        header.BeginEditObjectTypeCommand.Execute(null);
        Assert.True(header.IsEditingObjectType);
        Assert.Null(header.SelectedObjectTypeChoice);

        header.SelectedObjectTypeChoice = "Galaxy";

        Assert.False(header.IsEditingObjectType);
        Assert.Empty(harness.ObjectTypeWrites);
        Assert.Equal("G", header.ObjectType);
        Assert.Equal("Galaxy", header.ObjectCategory);
    }

    [Fact]
    public void ThePencil_LeavesTheScreenWhileItsOwnEditorIsOpen()
    {
        // Task 6 review P3: the pencil bound CanEditObjectType alone, so it stayed beside the
        // combo box it had just opened, offering to open an editor that was already there.
        using var harness = Factory.Create().Settle();
        var header = Header(harness);

        Assert.True(header.CanBeginEditObjectType);

        header.BeginEditObjectTypeCommand.Execute(null);
        Assert.False(header.CanBeginEditObjectType);

        header.CancelObjectTypeCommand.Execute(null);
        Assert.True(header.CanBeginEditObjectType);
    }
}
