using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Settings;

// Spec 12.15's Custom Columns settings tab (Phase 20 Task 4). The repository's own validation
// (the slug rule, the duplicate checks, the option-still-used refusal) is
// CustomColumnRepositoryTests's territory; every case here drives CustomColumnsTabViewModel
// through a fake catalogue written as the same five delegates the tab itself takes (spec 18.3),
// so a case controls the exact CustomWriteResult the tab reacts to rather than re-deriving the
// repository's own rules.
public class CustomColumnsTabViewModelTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>A minimal in-memory stand-in for <c>CustomColumnRepository</c>: enough behaviour
    /// for a write to be visible on the next <see cref="Load"/>, plus an override seam on Create
    /// and Reorder for the two cases that need a specific refusal or a specific exception. The
    /// generic write-refusal path (<c>RunWriteAsync</c>) is one path for all four writes, so
    /// exercising it through Create and Reorder covers Update and Delete too.</summary>
    private sealed class FakeCatalogue
    {
        private readonly List<CustomColumnDefinition> _rows = [];
        private int _nextOrder;

        public Func<string, CustomColumnType, CustomColumnScope, IReadOnlyList<string>, CustomWriteResult>? CreateOverride;
        public Func<Guid, bool, CustomWriteResult>? ReorderOverride;

        /// <summary>What the last <see cref="Create"/> call received, for required case 1's
        /// mapping assertion.</summary>
        public (string Name, CustomColumnType Type, CustomColumnScope Scope, IReadOnlyList<string> Options)? LastCreateCall;

        public CustomColumnDefinition Seed(CustomColumnDefinition definition)
        {
            _rows.Add(definition with { DisplayOrder = _nextOrder++ });
            return definition;
        }

        public IReadOnlyList<CustomColumnDefinition> Load() => [.. _rows.OrderBy(row => row.DisplayOrder)];

        public CustomWriteResult Create(
            string name, CustomColumnType type, CustomColumnScope scope, IReadOnlyList<string> options)
        {
            LastCreateCall = (name, type, scope, options);
            if (CreateOverride is not null)
            {
                return CreateOverride(name, type, scope, options);
            }

            var definition = CustomColumnTestFactory.Define(name, type, scope, options, _nextOrder++);
            _rows.Add(definition);
            return new CustomWriteResult(CustomWriteStatus.Written, null, definition);
        }

        public CustomWriteResult Update(Guid columnId, string name, IReadOnlyList<string> options)
        {
            var index = _rows.FindIndex(row => row.Id == columnId);
            var updated = _rows[index] with
            {
                Name = name,
                Options = _rows[index].Type == CustomColumnType.Dropdown ? options : _rows[index].Options,
            };
            _rows[index] = updated;
            return new CustomWriteResult(CustomWriteStatus.Written, null, updated);
        }

        public CustomWriteResult Reorder(Guid columnId, bool up)
        {
            if (ReorderOverride is not null)
            {
                return ReorderOverride(columnId, up);
            }

            var ordered = Load();
            var index = ordered.ToList().FindIndex(row => row.Id == columnId);
            var neighbourIndex = index + (up ? -1 : 1);
            if (index < 0 || neighbourIndex < 0 || neighbourIndex >= ordered.Count)
            {
                return new CustomWriteResult(CustomWriteStatus.Written, null);
            }

            var a = ordered[index];
            var b = ordered[neighbourIndex];
            _rows[_rows.FindIndex(row => row.Id == a.Id)] = a with { DisplayOrder = b.DisplayOrder };
            _rows[_rows.FindIndex(row => row.Id == b.Id)] = b with { DisplayOrder = a.DisplayOrder };
            return new CustomWriteResult(CustomWriteStatus.Written, null);
        }

        public CustomWriteResult Delete(Guid columnId)
        {
            _rows.RemoveAll(row => row.Id == columnId);
            return new CustomWriteResult(CustomWriteStatus.Deleted, null);
        }
    }

    private static CustomColumnsTabViewModel Create(FakeCatalogue catalogue, ILogger? logger = null) => new(
        catalogue.Load,
        catalogue.Create,
        catalogue.Update,
        catalogue.Reorder,
        catalogue.Delete,
        post: action => action(),
        logger: logger);

    private static async Task<CustomColumnsTabViewModel> CreateAndLoadAsync(FakeCatalogue catalogue)
    {
        var tab = Create(catalogue);
        await tab.RefreshAsync().WaitAsync(Budget);
        return tab;
    }

    // ---- required case 1 and 2: the create form's mapping and defaults --------------------

    [Theory]
    [InlineData("Target", CustomColumnScope.Target)]
    [InlineData("Night", CustomColumnScope.Session)]
    [InlineData("Rig", CustomColumnScope.Rig)]
    public async Task Create_PassesTheThreeOnScreenWordsAsTheThreeStoredScopes(string onScreen, CustomColumnScope stored)
    {
        var catalogue = new FakeCatalogue();
        var tab = await CreateAndLoadAsync(catalogue);

        tab.NewName = "Priority";
        tab.SelectedScope = tab.ScopeOptions.Single(option => option.Label == onScreen);
        await tab.CreateCommand.ExecuteAsync(null).WaitAsync(Budget);

        Assert.Equal(stored, catalogue.LastCreateCall!.Value.Scope);
    }

    [Fact]
    public async Task Create_DefaultsToCheckboxAndTarget()
    {
        var catalogue = new FakeCatalogue();
        var tab = await CreateAndLoadAsync(catalogue);

        Assert.Equal(CustomColumnType.Boolean, tab.SelectedType.Value);
        Assert.Equal(CustomColumnScope.Target, tab.SelectedScope.Value);

        tab.NewName = "Done";
        await tab.CreateCommand.ExecuteAsync(null).WaitAsync(Budget);

        Assert.Equal(CustomColumnType.Boolean, catalogue.LastCreateCall!.Value.Type);
        Assert.Equal(CustomColumnScope.Target, catalogue.LastCreateCall!.Value.Scope);
    }

    // ---- required cases 3 and 4: the guard is repeated in the command body ----------------

    [Fact]
    public async Task Create_WithABlankName_IsRefusedInTheCommandBodyToo()
    {
        var catalogue = new FakeCatalogue();
        var tab = await CreateAndLoadAsync(catalogue);

        tab.NewName = "   ";
        Assert.False(tab.CreateCommand.CanExecute(null));

        // TRACKING section 6 item 13: RelayCommand.Execute ignores CanExecute, so calling it
        // directly is the red case: a guard that lived only in CanExecute would let this through
        // and LastCreateCall would be set.
        await tab.CreateCommand.ExecuteAsync(null).WaitAsync(Budget);

        Assert.Null(catalogue.LastCreateCall);
    }

    [Fact]
    public async Task Create_ADropdownWithNoOptions_CannotBeSubmitted_AndTheTooltipSaysWhy()
    {
        var catalogue = new FakeCatalogue();
        var tab = await CreateAndLoadAsync(catalogue);

        tab.NewName = "Priority";
        tab.SelectedType = tab.TypeOptions.Single(option => option.Label == "Dropdown");

        Assert.False(tab.CreateCommand.CanExecute(null));
        Assert.Equal(CustomColumnMessages.DropdownWithNoOptions, tab.CreateDisabledReason);

        await tab.CreateCommand.ExecuteAsync(null).WaitAsync(Budget);
        Assert.Null(catalogue.LastCreateCall);
    }

    [Fact]
    public void TheOptionsEditor_IsHiddenUntilTypeIsDropdown()
    {
        var catalogue = new FakeCatalogue();
        var tab = Create(catalogue);

        Assert.False(tab.ShowOptionsEditor);

        tab.SelectedType = tab.TypeOptions.Single(option => option.Label == "Dropdown");
        Assert.True(tab.ShowOptionsEditor);

        tab.SelectedType = tab.TypeOptions.Single(option => option.Label == "Text");
        Assert.False(tab.ShowOptionsEditor);
    }

    [Fact]
    public void AddingAnOption_TrimsIt_AndARepeatIsRefusedWithTheSentence()
    {
        var catalogue = new FakeCatalogue();
        var tab = Create(catalogue);

        tab.NewOptionText = "  High  ";
        tab.AddNewOptionCommand.Execute(null);
        Assert.Equal(["High"], tab.NewOptions);
        Assert.Null(tab.NewOptionError);

        tab.NewOptionText = "high";
        tab.AddNewOptionCommand.Execute(null);
        Assert.Equal(["High"], tab.NewOptions);
        Assert.Equal(CustomColumnMessages.DuplicateOption("high"), tab.NewOptionError);
    }

    // The repository refuses options on a column that is not a dropdown. The create form can never
    // reach that refusal: a chip typed while the type was Dropdown stays on the hidden editor and is
    // not submitted for a type that cannot hold it, so the user is never told about an option editor
    // that is no longer on screen. Red against a form that sends NewOptions whatever the type: the
    // options arrive with the create and are stored on a text column.
    [Fact]
    public async Task Create_AfterTheTypeLeavesDropdown_SendsNoOptions()
    {
        var catalogue = new FakeCatalogue();
        var tab = await CreateAndLoadAsync(catalogue);

        tab.NewName = "Processed";
        tab.SelectedType = tab.TypeOptions.Single(option => option.Label == "Dropdown");
        tab.NewOptionText = "High";
        tab.AddNewOptionCommand.Execute(null);
        Assert.Equal(["High"], tab.NewOptions);

        tab.SelectedType = tab.TypeOptions.Single(option => option.Label == "Text");
        Assert.False(tab.ShowOptionsEditor);

        await tab.CreateCommand.ExecuteAsync(null).WaitAsync(Budget);

        Assert.Empty(catalogue.LastCreateCall!.Value.Options);
        Assert.Empty(Assert.Single(catalogue.Load()).Options);
        Assert.Null(tab.ErrorMessage);
    }

    [Fact]
    public async Task AfterASuccessfulCreate_TheFormIsCleared_AndTheTableHoldsTheNewRow()
    {
        var catalogue = new FakeCatalogue();
        var tab = await CreateAndLoadAsync(catalogue);

        tab.NewName = "Processed";
        tab.SelectedType = tab.TypeOptions.Single(option => option.Label == "Text");
        await tab.CreateCommand.ExecuteAsync(null).WaitAsync(Budget);

        Assert.Equal("", tab.NewName);
        Assert.Same(tab.TypeOptions[0], tab.SelectedType);
        Assert.Same(tab.ScopeOptions[0], tab.SelectedScope);
        Assert.Null(tab.ErrorMessage);

        var row = Assert.Single(tab.Rows);
        Assert.Equal("Processed", row.Name);
    }

    [Fact]
    public async Task ARefusedCreate_ShowsTheRepositorysSentenceVerbatim_AndKeepsTheTypedForm()
    {
        var catalogue = new FakeCatalogue
        {
            CreateOverride = (_, _, _, _) => new CustomWriteResult(
                CustomWriteStatus.DuplicateName, CustomColumnMessages.DuplicateName("Priority")),
        };
        var tab = await CreateAndLoadAsync(catalogue);

        tab.NewName = "Priority";
        await tab.CreateCommand.ExecuteAsync(null).WaitAsync(Budget);

        Assert.Equal(CustomColumnMessages.DuplicateName("Priority"), tab.ErrorMessage);
        Assert.Equal("Priority", tab.NewName);
        Assert.Empty(tab.Rows);
    }

    // ---- the table: edit, cancel, arrows, delete -------------------------------------------

    [Fact]
    public async Task Edit_ChangesTheNameAndTheOptions_AndLeavesTypeAndScopeDisabled()
    {
        var catalogue = new FakeCatalogue();
        catalogue.Seed(CustomColumnTestFactory.Dropdown("Low", "High"));
        var tab = await CreateAndLoadAsync(catalogue);

        var row = Assert.Single(tab.Rows);
        row.BeginEditCommand.Execute(null);

        Assert.False(row.TypeIsEnabled);
        Assert.False(row.ScopeIsEnabled);
        Assert.Equal(
            "Type and scope are fixed when the column is created.",
            CustomColumnRowViewModel.TypeScopeTooltip);

        row.EditName = "Priority level";
        row.RemoveEditOptionCommand.Execute("Low");
        row.EditOptionText = "Medium";
        row.AddEditOptionCommand.Execute(null);

        await row.SaveCommand.ExecuteAsync(null).WaitAsync(Budget);

        var saved = Assert.Single(tab.Rows);
        Assert.Equal("Priority level", saved.Name);
        Assert.Equal(["High", "Medium"], saved.Options);
    }

    [Fact]
    public async Task Cancel_RestoresTheRowsNameAndOptions()
    {
        var catalogue = new FakeCatalogue();
        catalogue.Seed(CustomColumnTestFactory.Dropdown("Low", "High"));
        var tab = await CreateAndLoadAsync(catalogue);

        var row = Assert.Single(tab.Rows);
        row.BeginEditCommand.Execute(null);
        row.EditName = "Something else";
        row.RemoveEditOptionCommand.Execute("Low");

        row.CancelEditCommand.Execute(null);

        Assert.False(row.IsEditing);
        Assert.Equal("Priority", row.EditName);
        Assert.Equal(["Low", "High"], row.EditOptions);
    }

    [Fact]
    public async Task TheArrows_AreDisabledAtTheEnds()
    {
        var catalogue = new FakeCatalogue();
        catalogue.Seed(CustomColumnTestFactory.Boolean("First"));
        catalogue.Seed(CustomColumnTestFactory.Boolean("Second"));
        catalogue.Seed(CustomColumnTestFactory.Boolean("Third"));
        var tab = await CreateAndLoadAsync(catalogue);

        Assert.True(tab.Rows[0].IsFirst);
        Assert.False(tab.Rows[0].IsLast);
        Assert.False(tab.Rows[0].MoveUpCommand.CanExecute(null));

        Assert.False(tab.Rows[1].IsFirst);
        Assert.False(tab.Rows[1].IsLast);
        Assert.True(tab.Rows[1].MoveUpCommand.CanExecute(null));
        Assert.True(tab.Rows[1].MoveDownCommand.CanExecute(null));

        Assert.True(tab.Rows[2].IsLast);
        Assert.False(tab.Rows[2].MoveDownCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnArrow_CallsReorderAndThenReloads()
    {
        var catalogue = new FakeCatalogue();
        catalogue.Seed(CustomColumnTestFactory.Boolean("First"));
        catalogue.Seed(CustomColumnTestFactory.Boolean("Second"));
        var tab = await CreateAndLoadAsync(catalogue);

        await tab.Rows[1].MoveUpCommand.ExecuteAsync(null).WaitAsync(Budget);

        // The rebound order comes from the reload (Load(), which reads DisplayOrder), not from a
        // locally swapped Rows list: a fresh set of row view-models replaces the old ones.
        Assert.Equal("Second", tab.Rows[0].Name);
        Assert.Equal("First", tab.Rows[1].Name);

        // Red against an optimistic move that survives a failed write: a refusal must leave the
        // table exactly as it was.
        catalogue.ReorderOverride = (_, _) => new CustomWriteResult(CustomWriteStatus.ColumnNotFound, null);
        var before = tab.Rows.Select(row => row.Name).ToList();
        await tab.Rows[0].MoveDownCommand.ExecuteAsync(null).WaitAsync(Budget);
        Assert.Equal(before, tab.Rows.Select(row => row.Name));
    }

    [Fact]
    public async Task Delete_ArmsFirst_AndTheMessageStatesTheValueCount()
    {
        var catalogue = new FakeCatalogue();
        catalogue.Seed(CustomColumnTestFactory.Define(
            "Done", CustomColumnType.Boolean, CustomColumnScope.Target, []) with { ValueCount = 3 });
        var tab = await CreateAndLoadAsync(catalogue);

        var row = Assert.Single(tab.Rows);
        Assert.False(row.ConfirmPending);

        row.ArmDeleteCommand.Execute(null);

        Assert.True(row.ConfirmPending);
        Assert.Equal(
            "Delete this column and all 3 of its values? This cannot be undone.",
            row.DeleteConfirmMessage);

        // The first press only arms it: no delete has reached the catalogue.
        Assert.Single(catalogue.Load());
    }

    // Design-spec 12.15's three branches (fixer P3-1): at zero the sentence names no count, at
    // one it says "and its one value", and at two or more it stays the plural form the fact above
    // already covers.
    [Theory]
    [InlineData(0, "Delete this column? This cannot be undone.")]
    [InlineData(1, "Delete this column and its one value? This cannot be undone.")]
    [InlineData(2, "Delete this column and all 2 of its values? This cannot be undone.")]
    [InlineData(7, "Delete this column and all 7 of its values? This cannot be undone.")]
    public async Task DeleteConfirmMessage_HasThreeBranches(int valueCount, string expected)
    {
        var catalogue = new FakeCatalogue();
        catalogue.Seed(CustomColumnTestFactory.Define(
            "Done", CustomColumnType.Boolean, CustomColumnScope.Target, []) with { ValueCount = valueCount });
        var tab = await CreateAndLoadAsync(catalogue);

        var row = Assert.Single(tab.Rows);

        Assert.Equal(expected, row.DeleteConfirmMessage);
    }

    [Fact]
    public async Task ArmingASecondRow_DisarmsTheFirst()
    {
        var catalogue = new FakeCatalogue();
        catalogue.Seed(CustomColumnTestFactory.Boolean("First"));
        catalogue.Seed(CustomColumnTestFactory.Boolean("Second"));
        var tab = await CreateAndLoadAsync(catalogue);

        tab.Rows[0].ArmDeleteCommand.Execute(null);
        Assert.True(tab.Rows[0].ConfirmPending);

        tab.Rows[1].ArmDeleteCommand.Execute(null);
        Assert.False(tab.Rows[0].ConfirmPending);
        Assert.True(tab.Rows[1].ConfirmPending);
    }

    [Fact]
    public async Task TheEmptyTable_SaysNoCustomColumnsYet()
    {
        var catalogue = new FakeCatalogue();
        var tab = await CreateAndLoadAsync(catalogue);

        Assert.True(tab.ShowEmptyState);

        catalogue.Seed(CustomColumnTestFactory.Boolean("Done"));
        await tab.RefreshAsync().WaitAsync(Budget);

        Assert.False(tab.ShowEmptyState);
    }

    [Fact]
    public async Task TheValuesColumn_ShowsTheStoredValueCount()
    {
        var catalogue = new FakeCatalogue();
        catalogue.Seed(CustomColumnTestFactory.Define(
            "Done", CustomColumnType.Boolean, CustomColumnScope.Target, []) with { ValueCount = 7 });
        var tab = await CreateAndLoadAsync(catalogue);

        Assert.Equal(7, Assert.Single(tab.Rows).ValueCount);
    }

    [Fact]
    public async Task TheValuesColumn_SeparatesThousands()
    {
        var catalogue = new FakeCatalogue();
        catalogue.Seed(CustomColumnTestFactory.Define(
            "Done", CustomColumnType.Boolean, CustomColumnScope.Target, []) with { ValueCount = 1234 });
        var tab = await CreateAndLoadAsync(catalogue);

        Assert.Equal("1,234", Assert.Single(tab.Rows).ValueCountText);
    }

    // ---- required case 21 --------------------------------------------------------------

    [Fact]
    public async Task ARefusedWriteNeverReachesTheTabAsAnException()
    {
        var catalogue = new FakeCatalogue
        {
            CreateOverride = (_, _, _, _) => throw new InvalidOperationException("boom"),
        };
        var tab = await CreateAndLoadAsync(catalogue);

        tab.NewName = "Priority";

        // Red against an unguarded Task.Run: the exception would otherwise fault the command's
        // task and the awaiting caller would observe it, or, worse, an unobserved task exception
        // would tear the process down.
        await tab.CreateCommand.ExecuteAsync(null).WaitAsync(Budget);

        Assert.NotNull(tab.ErrorMessage);
        Assert.False(tab.IsBusy);

        // The tab is still usable: a second, valid create still reaches the catalogue.
        catalogue.CreateOverride = null;
        await tab.CreateCommand.ExecuteAsync(null).WaitAsync(Budget);
        Assert.Single(tab.Rows);
    }

    // ---- review P2-1: the generation guard ----------------------------------------------

    [Fact]
    public async Task ASlowLoad_FinishingAfterAWrite_DoesNotOverwriteTheWrittenState()
    {
        var done = CustomColumnTestFactory.Define("Done", CustomColumnType.Boolean, CustomColumnScope.Target, []);
        var loadCallCount = 0;
        var gate = new SemaphoreSlim(0);

        // A held-publish seam: the first Load() call blocks here until the test releases it, and
        // answers with a fixed (by then stale) empty list; every later call answers at once with
        // the row the write actually created. No sleep anywhere in this case.
        IReadOnlyList<CustomColumnDefinition> Load()
        {
            if (Interlocked.Increment(ref loadCallCount) == 1)
            {
                gate.Wait(Budget);
                return [];
            }

            return [done];
        }

        CustomWriteResult Create(string name, CustomColumnType type, CustomColumnScope scope, IReadOnlyList<string> options)
            => new(CustomWriteStatus.Written, null, done);

        var tab = new CustomColumnsTabViewModel(
            Load, Create, (_, _, _) => CustomColumnTestFactory.Written,
            (_, _) => CustomColumnTestFactory.Written, _ => CustomColumnTestFactory.Written,
            post: action => action());

        // IsLoading is set synchronously before Task.Run is scheduled, so by the time this call
        // returns its Task, the first (about to block) load is already under way.
        var slowLoad = tab.RefreshAsync();
        Assert.True(tab.IsLoading);

        tab.NewName = "Done";
        await tab.CreateCommand.ExecuteAsync(null).WaitAsync(Budget);
        Assert.Single(tab.Rows);

        // Release the stale load only now, so it publishes strictly after the write already has.
        gate.Release();
        await slowLoad.WaitAsync(Budget);

        // Red against the missing guard: the stale load's empty answer would land after the write
        // and silently wipe the row it just added.
        Assert.Single(tab.Rows);
    }

    [Fact]
    public async Task TwoOverlappingWrites_PublishTheLaterOne()
    {
        var alpha = CustomColumnTestFactory.Define("Alpha", CustomColumnType.Boolean, CustomColumnScope.Target, []);
        var loadCallCount = 0;

        // Three canned answers by call order: the initial refresh, the delete's own (correct,
        // current) reload, and the slow create's stale reload, which must never reach the table.
        IReadOnlyList<CustomColumnDefinition> Load() => Interlocked.Increment(ref loadCallCount) switch
        {
            1 => [CustomColumnTestFactory.Boolean("ToDelete")],
            2 => [],
            _ => [CustomColumnTestFactory.Boolean("ToDelete"), alpha],
        };

        var gate = new SemaphoreSlim(0);

        // A held-publish seam over the write delegate itself: this call does not return until the
        // test releases it, so its own publish is guaranteed to settle after the delete below.
        CustomWriteResult Create(string name, CustomColumnType type, CustomColumnScope scope, IReadOnlyList<string> options)
        {
            gate.Wait(Budget);
            return new CustomWriteResult(CustomWriteStatus.Written, null, alpha);
        }

        var tab = new CustomColumnsTabViewModel(
            Load, Create, (_, _, _) => CustomColumnTestFactory.Written,
            (_, _) => CustomColumnTestFactory.Written, id => new CustomWriteResult(CustomWriteStatus.Deleted, null),
            post: action => action());
        await tab.RefreshAsync().WaitAsync(Budget);
        var row = Assert.Single(tab.Rows);

        tab.NewName = "Alpha";
        var slowCreate = tab.CreateCommand.ExecuteAsync(null);
        Assert.True(tab.IsBusy);

        // Row commands do not check the tab's IsBusy (only the view disables the container), so
        // this second write, issued for a different row's own action, genuinely overlaps the
        // first rather than being refused by a guard of its own.
        row.ArmDeleteCommand.Execute(null);
        await row.ConfirmDeleteCommand.ExecuteAsync(null).WaitAsync(Budget);
        Assert.Empty(tab.Rows);

        gate.Release();
        await slowCreate.WaitAsync(Budget);

        // Red against the missing guard: the slow create's stale reload would land after the
        // delete and silently resurrect the row the delete just removed.
        Assert.Empty(tab.Rows);
    }
}
