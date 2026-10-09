using GalactiLog.App.ViewModels.Settings;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Port of the web's GroupingEditor.tsx (Phase 9 Task 7). One shared view-model, exercised here
// with no owning tab, the way the collision map's owner table describes it: "the grouping editor
// (GroupingEditorViewModel), owner Task 7, reused by Task 7 three times".
public class GroupingEditorViewModelTests
{
    private static GroupingEditorViewModel Create(bool showColorPicker, params (string Name, int Count)[] discovered)
    {
        var vm = new GroupingEditorViewModel(showColorPicker);
        vm.SetDiscovered(discovered);
        return vm;
    }

    [Fact]
    public void GroupSelected_IsDisabledBelowTwoChecked()
    {
        var vm = Create(true, ("Ha", 5), ("ha", 3), ("OIII", 2));

        Assert.False(vm.GroupSelectedCommand.CanExecute(null));

        vm.Ungrouped.Single(row => row.Name == "Ha").IsChecked = true;

        Assert.False(vm.GroupSelectedCommand.CanExecute(null));

        vm.Ungrouped.Single(row => row.Name == "ha").IsChecked = true;

        Assert.True(vm.GroupSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void GroupSelected_PicksTheHighestCountNameAsCanonical()
    {
        var vm = Create(true, ("Ha", 5), ("ha", 30), ("H-alpha", 2));
        vm.Ungrouped.Single(row => row.Name == "Ha").IsChecked = true;
        vm.Ungrouped.Single(row => row.Name == "ha").IsChecked = true;

        vm.GroupSelectedCommand.Execute(null);

        var group = Assert.Single(vm.Groups);
        Assert.Equal("ha", group.Canonical);
    }

    [Fact]
    public void GroupSelected_PutsTheRestInTheAliasList()
    {
        var vm = Create(true, ("Ha", 5), ("ha", 30), ("H-alpha", 2));
        vm.Ungrouped.Single(row => row.Name == "Ha").IsChecked = true;
        vm.Ungrouped.Single(row => row.Name == "ha").IsChecked = true;
        vm.Ungrouped.Single(row => row.Name == "H-alpha").IsChecked = true;

        vm.GroupSelectedCommand.Execute(null);

        var group = Assert.Single(vm.Groups);
        Assert.Equal("ha", group.Canonical);
        Assert.Equal(new[] { "H-alpha", "Ha" }, group.Aliases.OrderBy(a => a, StringComparer.Ordinal));
    }

    [Fact]
    public void AddToExistingGroup_AppendsTheCheckedNames()
    {
        var vm = Create(false, ("ASI2600MM", 10), ("ZWO ASI2600MM", 4), ("ASI2600MM Pro", 1));
        var group = new AliasGroupViewModel("ASI2600MM", "#808080", []);
        vm.AddGroup(group);

        vm.Ungrouped.Single(row => row.Name == "ZWO ASI2600MM").IsChecked = true;
        vm.Ungrouped.Single(row => row.Name == "ASI2600MM Pro").IsChecked = true;
        vm.AddToGroupCommand.Execute(group);

        Assert.Equal(
            new[] { "ASI2600MM Pro", "ZWO ASI2600MM" },
            group.Aliases.OrderBy(a => a, StringComparer.Ordinal));
    }

    [Fact]
    public void Ungrouped_ExcludesNamesAlreadyInAGroup()
    {
        var vm = Create(true, ("Ha", 5), ("ha", 3), ("OIII", 2));
        vm.AddGroup(new AliasGroupViewModel("Ha", "#808080", ["ha"]));

        Assert.Equal(new[] { "OIII" }, vm.Ungrouped.Select(row => row.Name));
    }

    [Fact]
    public void ShowColorPicker_IsFalseForEquipment_AndTrueForFilters()
    {
        var filters = new GroupingEditorViewModel(showColorPicker: true);
        var equipment = new GroupingEditorViewModel(showColorPicker: false);

        Assert.True(filters.ShowColorPicker);
        Assert.False(equipment.ShowColorPicker);
    }

    [Fact]
    public void IncidentalRefresh_PreservesACheckedRowByName()
    {
        // review minor 6: an alias added to an unrelated group must not silently clear a
        // checkbox the user has not acted on yet.
        var vm = Create(true, ("Ha", 5), ("ha", 3), ("OIII", 2));
        var unrelated = new AliasGroupViewModel("Clear", "#808080", []);
        vm.AddGroup(unrelated);
        vm.Ungrouped.Single(row => row.Name == "OIII").IsChecked = true;

        unrelated.Aliases.Add("L");

        Assert.True(vm.Ungrouped.Single(row => row.Name == "OIII").IsChecked);
    }

    [Fact]
    public void GroupSelected_ClearsCheckedCount_TheWebsExplicitSetCheckedNewSet()
    {
        // review minor 6, the web's own setChecked(new Set()): GroupSelected clears its checked
        // rows explicitly rather than relying only on the preserve-by-name refresh, matching the
        // web's own explicit reset (both consumed rows leave Ungrouped anyway once covered, and
        // this pins that no checked state is left dangling either way).
        var vm = Create(true, ("Ha", 5), ("ha", 3), ("OIII", 2));
        vm.Ungrouped.Single(row => row.Name == "Ha").IsChecked = true;
        vm.Ungrouped.Single(row => row.Name == "ha").IsChecked = true;

        vm.GroupSelectedCommand.Execute(null);

        Assert.Equal(0, vm.CheckedCount);
        Assert.False(vm.Ungrouped.Single(row => row.Name == "OIII").IsChecked);
    }

    [Fact]
    public void CanAddToExistingGroup_RequiresBothACheckedRowAndAGroup()
    {
        // review minor 4: the web shows "Add to..." only while at least one row is checked and
        // at least one group exists.
        var vm = Create(false, ("ASI2600MM", 10), ("ASI294MC", 4));

        Assert.False(vm.CanAddToExistingGroup);

        var group = new AliasGroupViewModel("ASI2600MM", "#808080", []);
        vm.AddGroup(group);
        Assert.False(vm.CanAddToExistingGroup);

        vm.Ungrouped.Single(row => row.Name == "ASI294MC").IsChecked = true;
        Assert.True(vm.CanAddToExistingGroup);
    }

    [Fact]
    public void RemoveGroup_DetachesAndDropsIt_AndItsNamesReturnToUngrouped()
    {
        var vm = Create(true, ("Ha", 5), ("ha", 3));
        var group = new AliasGroupViewModel("Ha", "#808080", ["ha"]);
        vm.AddGroup(group);

        vm.RemoveGroup(group);

        Assert.Empty(vm.Groups);
        Assert.Equal(new[] { "Ha", "ha" }, vm.Ungrouped.Select(row => row.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    // ---- P13 R2(c): the ungrouped swatch and the one-name group it writes ----------------------

    [Fact]
    public void SetUngroupedColor_PromotesTheNameToAOneNameGroup()
    {
        var vm = Create(true, ("Ha", 5), ("OIII", 2));

        vm.SetUngroupedColor("Ha", "#123456");

        var group = Assert.Single(vm.Groups);
        Assert.Equal("Ha", group.Canonical);
        Assert.Equal("#123456", group.Color);
        Assert.Empty(group.Aliases);
        // The promoted name leaves the ungrouped column, because a group now covers it.
        Assert.Equal(["OIII"], vm.Ungrouped.Select(row => row.Name));
    }

    [Fact]
    public void SetUngroupedColor_OnAGroupedName_IsANoOp()
    {
        var vm = Create(true, ("Ha", 5), ("ha", 3));
        vm.AddGroup(new AliasGroupViewModel("Ha", "#ff0000", []));

        vm.SetUngroupedColor("Ha", "#123456");

        var group = Assert.Single(vm.Groups);
        Assert.Equal("#ff0000", group.Color);
    }

    [Fact]
    public void SetUngroupedColor_OnAnAliasOfAGroup_IsANoOp()
    {
        var vm = Create(true, ("Ha", 5), ("ha", 3));
        vm.AddGroup(new AliasGroupViewModel("Ha", "#ff0000", ["ha"]));

        vm.SetUngroupedColor("ha", "#123456");

        var group = Assert.Single(vm.Groups);
        Assert.Equal("Ha", group.Canonical);
        Assert.Equal("#ff0000", group.Color);
    }

    // Ruling Q4: the port seeds the ungrouped swatch through the one spine, so "lum" shows L's
    // white. The web seeds by exact name only and would leave it blank.
    [Fact]
    public void AnUngroupedRow_CarriesItsSeededColour()
    {
        var vm = Create(true, ("Ha", 5), ("lum", 3));

        Assert.Equal("#c44040", vm.Ungrouped.Single(row => row.Name == "Ha").Color);
        Assert.Equal("#e0e0e0", vm.Ungrouped.Single(row => row.Name == "lum").Color);
    }

    // Review P2-1: the Settings swatch resolves through the same four steps AliasMap.FilterColor
    // does, so a group whose alias carries the category is not grey here while the ledger dots,
    // the night strip ticks and the chart pills render it red. GroupSelected produces exactly
    // this shape, because it takes the highest-count name as the canonical.
    [Fact]
    public void AGroupWhoseAliasCarriesTheCategory_ResolvesThroughIt()
    {
        var group = new AliasGroupViewModel("L-Pro", null, ["Ha"]);

        Assert.Equal("#c44040", group.ResolvedColor);
        Assert.Equal(Avalonia.Media.Color.Parse("#c44040"), group.SwatchBrush.Color);

        // And the map the rest of the application reads agrees, which is the point.
        var map = new GalactiLog.Core.Aliases.AliasMap(
            new Dictionary<string, GalactiLog.Core.Settings.FilterSetting>
            {
                ["L-Pro"] = new GalactiLog.Core.Settings.FilterSetting { Aliases = ["Ha"] },
            },
            new GalactiLog.Core.Settings.EquipmentSettings());
        Assert.Equal(group.ResolvedColor, map.FilterColor("L-Pro"));
    }

    [Fact]
    public void AGroupSwatch_FollowsAnAliasAddedAfterConstruction()
    {
        var group = new AliasGroupViewModel("L-Pro", null, []);
        var changed = new List<string?>();
        group.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("#808080", group.ResolvedColor);

        group.Aliases.Add("OIII");

        Assert.Equal("#3a8fd4", group.ResolvedColor);
        Assert.Equal(Avalonia.Media.Color.Parse("#3a8fd4"), group.SwatchBrush.Color);
        Assert.Contains(nameof(AliasGroupViewModel.ResolvedColor), changed);
        Assert.Contains(nameof(AliasGroupViewModel.SwatchBrush), changed);
    }

    // Review P3-5: a stored value equal to the grey fallback is what every pre-Phase-13 build
    // wrote by itself, so the swatch treats it as unstored too and shows the seeded colour.
    [Fact]
    public void AGroupStoringTheGreyFallback_ResolvesThroughTheFold()
    {
        Assert.Equal("#c44040", new AliasGroupViewModel("Ha", "#808080", []).ResolvedColor);
        Assert.Equal("#7f7f7f", new AliasGroupViewModel("Ha", "#7f7f7f", []).ResolvedColor);
    }

    // Phase review P3-6. HANDOFF 5.2 item 12 makes FilterColor.Resolve the one resolution and
    // forbids a second entry point to the palette; the seed used to call FilterCategory.DefaultFor
    // directly. The observable difference is here, a name that folds to no category: DefaultFor
    // hands back null and Resolve hands back spec 5.8.4's grey. The swatch does not move, because
    // ParseTint drew the null grey too, and nothing promotes the seed: SetUngroupedColor is
    // reached only after a genuine pick and GroupSelected stores null.
    [Fact]
    public void AnUngroupedRowWithNoCategory_SeedsTheGreyThroughTheOneResolution()
    {
        var vm = Create(true, ("Duoband", 5));

        var row = Assert.Single(vm.Ungrouped);
        Assert.Equal(GalactiLog.Core.Aliases.FilterColor.Fallback, row.Color);
        Assert.Equal(Avalonia.Media.Color.Parse("#808080"), row.SwatchBrush.Color);
    }

    // ---- the Edited signal (pending-edits spine) ------------------------------------------------
    // One signal for every user mutation, so an owning tab marks itself dirty from one handler and
    // cannot miss a path. A load (SetGroups, SetDiscovered) must stay silent, or every tab would
    // open already dirty.

    private static (GroupingEditorViewModel Editor, Func<int> Count) Watched(params (string Name, int Count)[] discovered)
    {
        var vm = Create(true, discovered);
        var raised = 0;
        vm.Edited += (_, _) => raised++;
        return (vm, () => raised);
    }

    [Fact]
    public void Edited_IsNotRaisedByALoad()
    {
        var (vm, count) = Watched(("Ha", 5), ("ha", 3));

        vm.SetGroups([new AliasGroupViewModel("OIII", "#00ff00", ["O3"])]);
        vm.SetDiscovered([("Ha", 5), ("SII", 2)]);
        vm.SetGroups([new AliasGroupViewModel("Ha", null, ["ha"])]);

        Assert.Equal(0, count());
    }

    [Fact]
    public void Edited_IsRaisedByARename()
    {
        var (vm, count) = Watched();
        vm.SetGroups([new AliasGroupViewModel("Ha", null, ["H-alpha"])]);
        var group = vm.Groups[0];

        group.RenameText = "Halpha";
        group.CommitRenameCommand.Execute(null);

        Assert.True(count() > 0);
    }

    [Fact]
    public void Edited_IsNotRaisedByARefusedRename()
    {
        var (vm, count) = Watched();
        vm.SetGroups([new AliasGroupViewModel("Ha", null, []), new AliasGroupViewModel("OIII", null, [])]);
        var group = vm.Groups[1];

        group.RenameText = "Ha";
        group.CommitRenameCommand.Execute(null);

        Assert.Equal(0, count());
    }

    [Fact]
    public void Edited_IsRaisedByAColourChange()
    {
        var (vm, count) = Watched();
        vm.SetGroups([new AliasGroupViewModel("Ha", null, [])]);

        Assert.True(vm.Groups[0].TrySetColor("#123456"));

        Assert.True(count() > 0);
    }

    [Fact]
    public void Edited_IsRaisedByAnAliasRemoval_AndByTheGroupItEmpties()
    {
        var (vm, count) = Watched();
        vm.SetGroups([new AliasGroupViewModel("Ha", null, ["H-alpha", "ha"])]);
        var group = vm.Groups[0];

        group.RemoveAliasCommand.Execute("ha");
        Assert.True(count() > 0);

        var before = count();
        group.RemoveAliasCommand.Execute("H-alpha");
        Assert.Empty(vm.Groups);
        Assert.True(count() > before);
    }

    [Fact]
    public void Edited_IsRaisedByGroupSelected_AddToGroup_AndAnUngroupedColour()
    {
        var (vm, count) = Watched(("Ha", 5), ("ha", 3), ("OIII", 2), ("SII", 1));

        vm.Ungrouped.Single(row => row.Name == "Ha").IsChecked = true;
        Assert.Equal(0, count());
        vm.Ungrouped.Single(row => row.Name == "ha").IsChecked = true;
        vm.GroupSelectedCommand.Execute(null);
        var afterGroup = count();
        Assert.True(afterGroup > 0);

        vm.Ungrouped.Single(row => row.Name == "OIII").IsChecked = true;
        vm.AddToGroupCommand.Execute(vm.Groups[0]);
        var afterAdd = count();
        Assert.True(afterAdd > afterGroup);

        vm.SetUngroupedColor("SII", "#123456");
        Assert.True(count() > afterAdd);
    }

    [Fact]
    public void Edited_IsRaisedByAddGroupAndRemoveGroup()
    {
        var (vm, count) = Watched();
        var group = new AliasGroupViewModel("Ha", null, []);

        vm.AddGroup(group);
        var afterAdd = count();
        Assert.True(afterAdd > 0);

        vm.RemoveGroup(group);
        Assert.True(count() > afterAdd);
    }

    [Fact]
    public void Edited_IsNotRaisedByAGroupAfterALoadReplacedIt()
    {
        var (vm, count) = Watched();
        vm.SetGroups([new AliasGroupViewModel("Ha", null, ["ha"])]);
        var stale = vm.Groups[0];
        vm.SetGroups([new AliasGroupViewModel("OIII", null, [])]);

        Assert.True(stale.TrySetColor("#123456"));
        stale.Aliases.Add("H-alpha");

        Assert.Equal(0, count());
    }
}
