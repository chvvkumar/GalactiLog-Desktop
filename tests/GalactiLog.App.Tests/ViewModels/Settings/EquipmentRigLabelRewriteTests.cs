using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Settings;

// Phase 20 Task 2, spec 12.15's "What happens to values". A rig label is
// "{telescope} / {camera}" over the canonical names (SessionDetailQuery line 418), so a rename of
// either half has to move every stored rig-scope custom value keyed to it. That is the THIRD write
// of the Equipment tab's save, after the equipment document and the PHD2 profile map, and the
// three cannot be made one.
public class EquipmentRigLabelRewriteTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly TempDatabase _db = new("galactilog-rig-label-rewrite");
    private readonly SettingsStore _store;
    private readonly CustomColumnRepository _columns;

    private Func<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, int>? _rewrite;

    // The three writes of one save, in the order the tab issued them.
    private readonly List<string> _writes = [];

    // The discovered telescope names of the library, which is what the editor's left column offers
    // and what a grouping is made out of. Empty unless a case sets it.
    private IReadOnlyList<(string Name, int Count)> _discoveredTelescopes = [];

    public EquipmentRigLabelRewriteTests()
    {
        _store = new SettingsStore(new SettingsRepository(_db.ConnectionString));
        _columns = new CustomColumnRepository(new DatabaseConnectionString(_db.ConnectionString));
        _rewrite = _columns.RewriteRigLabels;
    }

    public void Dispose() => _db.Dispose();

    // Case 29, the telescope half. Red against a save that rewrites the profile map alone, which is
    // exactly what ships today: the stored label keeps the old canonical name, so the value renders
    // under a rig row the session pane no longer draws.
    [Fact]
    public async Task SavingATelescopeRename_RewritesTheStoredRigLabels()
    {
        SaveEquipment(telescopes: ["Askar 120", "RedCat 51"], cameras: ["ASI2600MC"]);
        var column = RigColumn("Rig note");
        var target = NewTarget("NGC 7000");
        Set(column, target, "Askar 120 / ASI2600MC", "kept through the rename");
        Set(column, target, "RedCat 51 / ASI2600MC", "untouched");

        using var tab = Create();
        Rename(tab.TelescopesEditor, "Askar 120", "Askar FMA180");
        tab.SaveCommand.Execute(null);
        await (tab.PendingSave ?? Task.CompletedTask);

        Assert.Null(tab.ErrorMessage);
        Assert.Equal("kept through the rename", ValueOf(column, target, "Askar FMA180 / ASI2600MC"));
        Assert.Equal("untouched", ValueOf(column, target, "RedCat 51 / ASI2600MC"));
        Assert.Equal(2, Labels().Count);
    }

    // Case 29's camera twin. Red against a rewrite built from the telescope rename map alone, which
    // is the only map the tab kept before this phase, because the PHD2 profile map holds telescopes
    // and nothing else.
    [Fact]
    public async Task SavingACameraRename_RewritesTheStoredRigLabels()
    {
        SaveEquipment(telescopes: ["Askar 120"], cameras: ["ASI2600MC", "ASI533MC"]);
        var column = RigColumn("Rig note");
        var target = NewTarget("NGC 7000");
        Set(column, target, "Askar 120 / ASI2600MC", "kept through the rename");
        Set(column, target, "Askar 120 / ASI533MC", "untouched");

        using var tab = Create();
        Rename(tab.CamerasEditor, "ASI2600MC", "ASI2600MC Pro");
        tab.SaveCommand.Execute(null);
        await (tab.PendingSave ?? Task.CompletedTask);

        Assert.Null(tab.ErrorMessage);
        Assert.Equal("kept through the rename", ValueOf(column, target, "Askar 120 / ASI2600MC Pro"));
        Assert.Equal("untouched", ValueOf(column, target, "Askar 120 / ASI533MC"));
    }

    // Both halves renamed in one save, which is the cross product the view-model deliberately does
    // not build: the repository reads the stored labels and splits them itself (ruling C9).
    [Fact]
    public async Task SavingBothHalvesRenamed_MovesTheLabelOnce()
    {
        SaveEquipment(telescopes: ["Askar 120"], cameras: ["ASI2600MC"]);
        var column = RigColumn("Rig note");
        var target = NewTarget("NGC 7000");
        Set(column, target, "Askar 120 / ASI2600MC", "moved");

        using var tab = Create();
        Rename(tab.TelescopesEditor, "Askar 120", "Askar FMA180");
        Rename(tab.CamerasEditor, "ASI2600MC", "ASI533MC");
        tab.SaveCommand.Execute(null);
        await (tab.PendingSave ?? Task.CompletedTask);

        Assert.Equal(new[] { "Askar FMA180 / ASI533MC" }, Labels());
    }

    // A save with no rename at all must not reach the catalogue.
    [Fact]
    public async Task SavingWithNoRename_DoesNotCallTheRewrite()
    {
        SaveEquipment(telescopes: ["Askar 120"], cameras: ["ASI2600MC"]);
        var calls = 0;
        _rewrite = (telescopes, cameras) =>
        {
            calls++;
            return _columns.RewriteRigLabels(telescopes, cameras);
        };

        using var tab = Create();
        tab.SaveCommand.Execute(null);
        await (tab.PendingSave ?? Task.CompletedTask);

        // The save must have RUN and found nothing to rewrite, not have been refused before it got
        // there: a refusal by CanSave, or a throw ahead of the third write, would also leave calls
        // at zero (review P3-3).
        Assert.Null(tab.ErrorMessage);
        Assert.Equal("Equipment settings saved", tab.StatusMessage);
        Assert.Equal(new[] { "Askar 120" }, _store.GetEquipment().Telescopes.Keys.ToList());
        Assert.Equal(0, calls);
    }

    // Case 30, the accepted third-write shape spec 12.15 states. Red against a rewrite presented as
    // atomic: the equipment document IS saved when the third write throws, and the repair is the
    // next save of the same rename, because the rename pairs are rebuilt from the loaded names each
    // time and the loaded names are only refreshed on a save that reached the end.
    [Fact]
    public async Task ARewriteThatThrows_LeavesTheEquipmentSaveLanded_AndTheNextSaveRepairsIt()
    {
        SaveEquipment(telescopes: ["Askar 120"], cameras: ["ASI2600MC"]);
        var column = RigColumn("Rig note");
        var target = NewTarget("NGC 7000");
        Set(column, target, "Askar 120 / ASI2600MC", "stranded, then repaired");

        var seen = new List<IReadOnlyDictionary<string, string>>();
        var fail = true;
        _rewrite = (telescopes, cameras) =>
        {
            seen.Add(telescopes);
            if (fail)
            {
                throw new InvalidOperationException("the catalogue write failed");
            }

            return _columns.RewriteRigLabels(telescopes, cameras);
        };

        using var tab = Create();
        Rename(tab.TelescopesEditor, "Askar 120", "Askar FMA180");
        tab.SaveCommand.Execute(null);
        await (tab.PendingSave ?? Task.CompletedTask);

        // The first write landed and is not rolled back; the label is left on the old canonical
        // name, so the value renders under a rig row the pane no longer draws rather than under the
        // wrong rig.
        Assert.NotNull(tab.ErrorMessage);
        Assert.Equal(new[] { "Askar FMA180" }, _store.GetEquipment().Telescopes.Keys.ToList());
        Assert.Equal(new[] { "Askar 120 / ASI2600MC" }, Labels());

        // The next save of the same rename repairs it, with no second rename typed: the pairs are
        // rebuilt from the loaded names, which the failed save left alone.
        fail = false;
        tab.SaveCommand.Execute(null);
        await (tab.PendingSave ?? Task.CompletedTask);

        Assert.Null(tab.ErrorMessage);
        Assert.Equal(2, seen.Count);
        Assert.Equal("Askar FMA180", seen[1]["Askar 120"]);
        Assert.Equal(new[] { "Askar FMA180 / ASI2600MC" }, Labels());
        Assert.Equal("stranded, then repaired", ValueOf(column, target, "Askar FMA180 / ASI2600MC"));
    }

    // A save that reached the end refreshes the loaded names, so a third save carries no rename and
    // the catalogue is not rewritten again. The other half of case 30's repair rule.
    [Fact]
    public async Task ASaveThatLanded_CarriesNoRenameOnTheNextSave()
    {
        SaveEquipment(telescopes: ["Askar 120"], cameras: ["ASI2600MC"]);
        var seen = new List<int>();
        _rewrite = (telescopes, cameras) =>
        {
            seen.Add(telescopes.Count + cameras.Count);
            return _columns.RewriteRigLabels(telescopes, cameras);
        };

        using var tab = Create();
        Rename(tab.TelescopesEditor, "Askar 120", "Askar FMA180");
        tab.SaveCommand.Execute(null);
        await (tab.PendingSave ?? Task.CompletedTask);

        tab.SaveCommand.Execute(null);
        await (tab.PendingSave ?? Task.CompletedTask);

        // Both saves must have RUN: the second one carrying no rename is the point, and a second
        // save that was refused or that threw early would leave the same single entry (P3-3).
        Assert.Null(tab.ErrorMessage);
        Assert.Equal("Equipment settings saved", tab.StatusMessage);
        Assert.Equal(new[] { "Askar FMA180" }, _store.GetEquipment().Telescopes.Keys.ToList());
        Assert.Equal(new[] { 1 }, seen);
    }

    // A regroup with no canonical name renamed: two discovered spellings of one telescope checked
    // and grouped, which names the more frequent spelling canonical and makes the other its alias.
    // Every frame of the second spelling now resolves to the first, so the label the session pane
    // draws moves and the stored value has to move with it. Red against a rewrite fed the rename
    // pairs alone: nothing was renamed, so both maps are empty, the rewrite is never called and the
    // value keeps a label no pane draws.
    [Fact]
    public async Task GroupingTwoSpellingsUnderOneName_MovesTheStoredRigLabels()
    {
        SaveEquipment(telescopes: [], cameras: ["ASI2600MC"]);
        _discoveredTelescopes = [("Askar 120", 40), ("askar 120", 3)];
        var column = RigColumn("Rig note");
        var target = NewTarget("NGC 7000");
        Set(column, target, "askar 120 / ASI2600MC", "written under the second spelling");

        using var tab = Create();
        Check(tab.TelescopesEditor, "Askar 120", "askar 120");
        tab.TelescopesEditor.GroupSelectedCommand.Execute(null);
        tab.SaveCommand.Execute(null);
        await (tab.PendingSave ?? Task.CompletedTask);

        Assert.Null(tab.ErrorMessage);
        Assert.Equal("Equipment settings saved", tab.StatusMessage);
        Assert.Equal(new[] { "Askar 120 / ASI2600MC" }, Labels());
        Assert.Equal("written under the second spelling", ValueOf(column, target, "Askar 120 / ASI2600MC"));
    }

    // The same move in reverse, and the other operation that carries no rename: a group whose
    // canonical name is a name the user typed, with the library's one raw spelling as its alias, is
    // deleted. The frames go back to resolving to the raw spelling, so the value goes with them.
    // Red the same way: no canonical name moved, so the shipped tab hands the rewrite two empty
    // maps and the value keeps the group's name for good, since no later save carries a rename
    // either.
    [Fact]
    public async Task RemovingAGroup_MovesTheStoredRigLabelsBack()
    {
        SaveEquipment(
            telescopes: ["Askar"],
            cameras: ["ASI2600MC"],
            telescopeAliases: new Dictionary<string, string[]> { ["Askar"] = ["Askar 120"] });
        var column = RigColumn("Rig note");
        var target = NewTarget("NGC 7000");
        Set(column, target, "Askar / ASI2600MC", "written while the group folded it");

        using var tab = Create();
        tab.TelescopesEditor.RemoveGroup(tab.TelescopesEditor.Groups.Single(group => group.Canonical == "Askar"));
        tab.SaveCommand.Execute(null);
        await (tab.PendingSave ?? Task.CompletedTask);

        Assert.Null(tab.ErrorMessage);
        Assert.Equal("Equipment settings saved", tab.StatusMessage);
        Assert.Equal(new[] { "Askar 120 / ASI2600MC" }, Labels());
        Assert.Equal("written while the group folded it", ValueOf(column, target, "Askar 120 / ASI2600MC"));
    }

    // The other side of the same rule, now that the alias table takes part in composing the moves:
    // a save that changes no grouping at all must still reach the catalogue with nothing, even
    // though both alias tables are full. An alias that folded a spelling before the save and folds
    // it after is not a move.
    [Fact]
    public async Task ASaveThatChangesNoGrouping_DoesNotCallTheRewrite()
    {
        SaveEquipment(
            telescopes: ["Askar"],
            cameras: ["ASI2600MC"],
            telescopeAliases: new Dictionary<string, string[]> { ["Askar"] = ["Askar 120"] });
        var column = RigColumn("Rig note");
        var target = NewTarget("NGC 7000");
        Set(column, target, "Askar / ASI2600MC", "untouched");

        var calls = 0;
        _rewrite = (telescopes, cameras) =>
        {
            calls++;
            return _columns.RewriteRigLabels(telescopes, cameras);
        };

        using var tab = Create();
        tab.SaveCommand.Execute(null);
        await (tab.PendingSave ?? Task.CompletedTask);

        // The save must have RUN and found nothing to move, not have been refused before it got
        // there (review P3-3).
        Assert.Null(tab.ErrorMessage);
        Assert.Equal("Equipment settings saved", tab.StatusMessage);
        Assert.Equal(0, calls);
        Assert.Equal(new[] { "Askar / ASI2600MC" }, Labels());
    }

    // Review P3-3: the catalogue write is the last of the three, so a catalogue failure costs the
    // dismissed suggestions of that save nothing. Red against the shipped order, which issues it
    // between the profile map rewrite and the dismissed list.
    [Fact]
    public async Task TheCatalogueWrite_IsIssuedAfterTheDismissedSuggestions()
    {
        SaveEquipment(telescopes: ["Askar 120"], cameras: ["ASI2600MC"]);
        var column = RigColumn("Rig note");
        var target = NewTarget("NGC 7000");
        Set(column, target, "Askar 120 / ASI2600MC", "moved");

        using var tab = Create();
        Rename(tab.TelescopesEditor, "Askar 120", "Askar FMA180");
        tab.SaveCommand.Execute(null);
        await (tab.PendingSave ?? Task.CompletedTask);

        Assert.Null(tab.ErrorMessage);
        Assert.Equal(new[] { "equipment", "dismissed", "rig" }, _writes);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private EquipmentTabViewModel Create()
    {
        var tab = new EquipmentTabViewModel(
            _store.GetEquipment,
            document =>
            {
                _writes.Add("equipment");
                _store.SaveEquipment(document);
            },
            _store.GetDismissedSuggestions,
            dismissed =>
            {
                _writes.Add("dismissed");
                _store.SaveDismissedSuggestions(dismissed);
            },
            () => [],
            () => _discoveredTelescopes,
            post: action => action(),
            rewriteRigLabels: (telescopes, cameras) =>
            {
                _writes.Add("rig");
                return _rewrite!(telescopes, cameras);
            });
        tab.PendingLoad?.Wait(Budget);
        return tab;
    }

    // The rename the editor itself performs: BeginRename, RenameText, CommitRename, which raises
    // RenameRequested and lands in GroupingEditorViewModel.OnRenameRequested, which assigns
    // Canonical and adds no alias. Driven through the shipped path, not by assigning Canonical.
    private static void Rename(GroupingEditorViewModel editor, string from, string to)
    {
        var group = editor.Groups.Single(row => row.Canonical == from);
        group.BeginRenameCommand.Execute(null);
        group.RenameText = to;
        group.CommitRenameCommand.Execute(null);
    }

    // The checked rows a grouping is made from, driven through the row's own property as the left
    // column's check box does.
    private static void Check(GroupingEditorViewModel editor, params string[] names)
    {
        foreach (var name in names)
        {
            editor.Ungrouped.Single(row => row.Name == name).IsChecked = true;
        }
    }

    private void SaveEquipment(
        string[] telescopes,
        string[] cameras,
        IReadOnlyDictionary<string, string[]>? telescopeAliases = null)
        => _store.SaveEquipment(new EquipmentSettings
        {
            Telescopes = telescopes.ToDictionary(
                name => name,
                name => new EquipmentItemSettings { Aliases = [.. telescopeAliases?.GetValueOrDefault(name) ?? []] },
                StringComparer.Ordinal),
            Cameras = cameras.ToDictionary(name => name, _ => new EquipmentItemSettings(), StringComparer.Ordinal),
        });

    private Guid RigColumn(string name)
    {
        var result = _columns.Create(name, CustomColumnType.Text, CustomColumnScope.Rig, []);
        Assert.Equal(CustomWriteStatus.Written, result.Status);
        return result.Column!.Id;
    }

    private Guid NewTarget(string primaryName)
    {
        var id = Guid.NewGuid();
        _db.Seed(context => context.Targets.Add(new Target { Id = id, PrimaryName = primaryName }));
        return id;
    }

    private void Set(Guid columnId, Guid targetId, string label, string value)
        => Assert.Equal(
            CustomWriteStatus.Written,
            _columns.SetValue(columnId, CustomValueKey.ForRig(targetId, new DateOnly(2026, 1, 4), label), value).Status);

    private string ValueOf(Guid columnId, Guid targetId, string label)
        => _columns.ValuesForTarget(targetId)
            .Single(row => row.ColumnId == columnId && row.Key.RigLabel == label)
            .Value;

    private List<string> Labels()
        => _db.Read(context => context.CustomColumnValues
            .Where(row => row.RigLabel != null)
            .Select(row => row.RigLabel!)
            .ToList()
            .Order(StringComparer.Ordinal)
            .ToList());
}
