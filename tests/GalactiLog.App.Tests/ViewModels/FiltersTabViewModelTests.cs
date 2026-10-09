using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 12.7's Filters tab (Phase 9 Task 7). Built over a real SettingsStore and a real
// AliasMapCache, both over a TempDatabase, because the roadmap's Verify line and the reviewer
// focus both ask for the cache to really drop its memo, which only a real cache can prove
// (design-lessons rule 2: no direct cache poke anywhere in the tab under test).
public class FiltersTabViewModelTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly TempDatabase _db = new("galactilog-filters-tab");
    private readonly SettingsStore _store;
    private readonly AliasMapCache _cache;

    private IReadOnlyList<(string Name, int Count)> _discovered = [];

    public FiltersTabViewModelTests()
    {
        _store = new SettingsStore(new SettingsRepository(_db.ConnectionString));
        _cache = new AliasMapCache(_store);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
    }

    private FiltersTabViewModel Create(Action<Action>? post = null)
    {
        var vm = new FiltersTabViewModel(
            _store.GetFilters,
            _store.SaveFilters,
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discovered,
            post: post ?? (action => action()));
        vm.PendingLoad?.Wait(Budget);
        return vm;
    }

    private static async Task SettleAsync(FiltersTabViewModel vm)
    {
        if (vm.PendingLoad is { } load)
        {
            await load;
        }
    }

    [Fact]
    public void Load_PopulatesGroupsDiscoveredNamesAndSuggestions()
    {
        _store.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Color = "#ff0000", Aliases = ["H-alpha"] },
        });
        // "OIII" and "oiii" are discovered, similar (rule 1, case-insensitive exact match), and
        // not yet grouped: a suggestion. "Ha" and "H-alpha" are unrelated to that check; they are
        // already grouped by the seeded filter above and are not similar to one another under any
        // of the three rules ("Ha" is two characters, too short for the containment rule).
        _discovered = [("Ha", 5), ("H-alpha", 3), ("OIII", 2), ("oiii", 2)];

        using var vm = Create();

        var group = Assert.Single(vm.Editor.Groups);
        Assert.Equal("Ha", group.Canonical);
        Assert.Equal("#ff0000", group.Color);
        Assert.Equal(["H-alpha"], group.Aliases);

        var suggestion = Assert.Single(vm.Suggestions);
        Assert.Equal(new[] { "OIII", "oiii" }, suggestion.Names);
    }

    // P13 R2a re-pointed this case: an added filter no longer stores grey at birth, because a
    // stored grey would outrank the seeded palette for ever. It stores nothing, and "Lum" folds
    // to L. The name it was given, AddCanonicalFilter_DefaultsToTheNeutralGrey, asserted the
    // behaviour this phase deliberately removed.
    [Fact]
    public void AddFilter_StoresNoColour_SoTheCategoryDefaultResolves()
    {
        using var vm = Create();

        vm.NewFilterName = "Lum";
        vm.AddFilterCommand.Execute(null);

        var group = Assert.Single(vm.Editor.Groups);
        Assert.Null(group.Color);
        Assert.Equal("#e0e0e0", group.ResolvedColor);
    }

    [Fact]
    public void AddCanonicalFilter_DefaultsToAnEmptyAliasList()
    {
        using var vm = Create();

        vm.NewFilterName = "Lum";
        vm.AddFilterCommand.Execute(null);

        var group = Assert.Single(vm.Editor.Groups);
        Assert.Empty(group.Aliases);
    }

    [Fact]
    public void AddCanonicalFilter_RejectsABlankName()
    {
        using var vm = Create();

        vm.NewFilterName = "   ";
        vm.AddFilterCommand.Execute(null);

        Assert.Empty(vm.Editor.Groups);
    }

    [Fact]
    public void AddCanonicalFilter_RejectsAnExistingCanonicalName()
    {
        _store.SaveFilters(new Dictionary<string, FilterSetting> { ["Ha"] = new FilterSetting() });
        using var vm = Create();

        vm.NewFilterName = "Ha";
        vm.AddFilterCommand.Execute(null);

        Assert.Single(vm.Editor.Groups);
        Assert.True(vm.HasErrorMessage);
    }

    [Fact]
    public async Task AcceptingASuggestion_WritesTheAliasList()
    {
        _discovered = [("Ha", 5), ("ha", 3)];
        using var vm = Create();

        var suggestion = Assert.Single(vm.Suggestions);
        Assert.Equal("Ha", suggestion.SelectedName);
        vm.AcceptSuggestionCommand.Execute(suggestion);
        vm.SaveCommand.Execute(null);
        await SettleAsync(vm);
        await (vm.PendingSave ?? Task.CompletedTask);

        var stored = _store.GetFilters();
        var entry = Assert.Single(stored);
        Assert.Equal("Ha", entry.Key);
        Assert.Equal(["ha"], entry.Value.Aliases);
    }

    [Fact]
    public async Task AcceptingASuggestion_InvalidatesTheAliasMapCache()
    {
        _discovered = [("Ha", 5), ("ha", 3)];
        using var vm = Create();

        var before = _cache.Current;
        Assert.Equal("ha", before.CanonicalFilter("ha"));

        var suggestion = Assert.Single(vm.Suggestions);
        vm.AcceptSuggestionCommand.Execute(suggestion);
        vm.SaveCommand.Execute(null);
        await (vm.PendingSave ?? Task.CompletedTask);

        // Reflects the new mapping immediately: no 30 second TTL wait.
        var after = _cache.Current;
        Assert.NotSame(before, after);
        Assert.Equal("Ha", after.CanonicalFilter("ha"));
    }

    [Fact]
    public void AcceptingASuggestion_ChoosesTheHighestCountNameAsCanonical()
    {
        _discovered = [("ha", 2), ("Ha", 9)];
        using var vm = Create();

        var suggestion = Assert.Single(vm.Suggestions);

        Assert.Equal("Ha", suggestion.SelectedName);
    }

    [Fact]
    public async Task AcceptingASuggestion_NeverLeavesADocumentWhereANameIsBothACanonicalKeyAndAnAlias()
    {
        // "ha" starts as its own canonical filter; the discovered pair is similar (rule 1,
        // case-insensitive exact match) and "Ha" has the higher count, so accepting the
        // suggestion must absorb the existing "ha" group into "Ha"'s alias list rather than
        // leaving "ha" standing as both a canonical key and "Ha"'s alias (review I2).
        _store.SaveFilters(new Dictionary<string, FilterSetting> { ["ha"] = new FilterSetting() });
        _discovered = [("Ha", 9), ("ha", 2)];
        using var vm = Create();

        var suggestion = Assert.Single(vm.Suggestions);
        Assert.Equal("Ha", suggestion.SelectedName);

        vm.AcceptSuggestionCommand.Execute(suggestion);
        vm.SaveCommand.Execute(null);
        await (vm.PendingSave ?? Task.CompletedTask);

        var stored = _store.GetFilters();
        var canonicalNames = new HashSet<string>(stored.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain(
            stored.Values.SelectMany(entry => entry.Aliases),
            alias => canonicalNames.Contains(alias));

        var group = Assert.Single(stored);
        Assert.Equal("Ha", group.Key);
        Assert.Equal(["ha"], group.Value.Aliases);
    }

    [Fact]
    public void DismissingASuggestion_AppendsTheSortedNameListToDismissed()
    {
        _discovered = [("ha", 2), ("Ha", 9)];
        using var vm = Create();
        var suggestion = Assert.Single(vm.Suggestions);

        vm.DismissSuggestionCommand.Execute(suggestion);

        Assert.Empty(vm.Suggestions);
        // The dismissed list is private; SaveAsync is what makes it observable, so save it and
        // read the stored document back.
        vm.SaveCommand.Execute(null);
        vm.PendingSave?.Wait(Budget);
        var dismissed = _store.GetDismissedSuggestions();
        var group = Assert.Single(dismissed);
        Assert.Equal(new[] { "Ha", "ha" }, group);
    }

    [Fact]
    public void DismissedSuggestion_DoesNotReappearAfterAReload()
    {
        _discovered = [("ha", 2), ("Ha", 9)];
        using var vm = Create();
        var suggestion = Assert.Single(vm.Suggestions);
        vm.DismissSuggestionCommand.Execute(suggestion);

        vm.Reload();

        Assert.Empty(vm.Suggestions);
    }

    [Fact]
    public void DismissedSuggestion_IsNotPersistedUntilSave()
    {
        _discovered = [("ha", 2), ("Ha", 9)];
        using var vm = Create();
        var suggestion = Assert.Single(vm.Suggestions);

        vm.DismissSuggestionCommand.Execute(suggestion);

        Assert.Empty(_store.GetDismissedSuggestions());
    }

    [Fact]
    public async Task Save_WritesFiltersThenDismissedSuggestions_InThatOrder()
    {
        var order = new List<string>();
        var vm = new FiltersTabViewModel(
            _store.GetFilters,
            filters =>
            {
                order.Add("filters");
                _store.SaveFilters(filters);
            },
            _store.GetDismissedSuggestions,
            dismissed =>
            {
                order.Add("dismissed");
                _store.SaveDismissedSuggestions(dismissed);
            },
            () => _discovered,
            post: action => action());
        vm.PendingLoad?.Wait(Budget);

        vm.NewFilterName = "Lum";
        vm.AddFilterCommand.Execute(null);
        vm.SaveCommand.Execute(null);
        await (vm.PendingSave ?? Task.CompletedTask);

        Assert.Equal(["filters", "dismissed"], order);
        vm.Dispose();
    }

    [Fact]
    public async Task Save_RaisesAliasSourcesChangedExactlyOnce()
    {
        using var vm = Create();
        var raised = 0;
        _store.AliasSourcesChanged += (_, _) => raised++;

        vm.NewFilterName = "Lum";
        vm.AddFilterCommand.Execute(null);
        vm.SaveCommand.Execute(null);
        await (vm.PendingSave ?? Task.CompletedTask);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void RenamingAGroup_MovesTheEntry_KeepingTheColourAndAliases()
    {
        using var vm = Create();
        var group = new AliasGroupViewModel("Ha", "#112233", ["H-alpha"]);
        vm.Editor.AddGroup(group);

        group.RenameText = "Halpha";
        group.CommitRenameCommand.Execute(null);

        Assert.Equal("Halpha", group.Canonical);
        Assert.Equal("#112233", group.Color);
        Assert.Equal(["H-alpha"], group.Aliases);
        Assert.Single(vm.Editor.Groups);
    }

    [Fact]
    public void RenamingAGroup_RefusesAnExistingName()
    {
        using var vm = Create();
        vm.Editor.AddGroup(new AliasGroupViewModel("Ha", "#808080", []));
        var oiii = new AliasGroupViewModel("OIII", "#808080", []);
        vm.Editor.AddGroup(oiii);

        oiii.RenameText = "Ha";
        oiii.CommitRenameCommand.Execute(null);

        Assert.Equal("OIII", oiii.Canonical);
        Assert.True(vm.HasErrorMessage);
    }

    [Fact]
    public void RemovingTheLastAlias_DeletesTheGroup()
    {
        using var vm = Create();
        var group = new AliasGroupViewModel("Ha", "#808080", ["H-alpha"]);
        vm.Editor.AddGroup(group);

        group.RemoveAliasCommand.Execute("H-alpha");

        Assert.Empty(vm.Editor.Groups);
    }

    [Fact]
    public void ColourMustBeSixDigitHex_AndIsOtherwiseRefused()
    {
        var group = new AliasGroupViewModel("Ha", "#808080", []);
        var refused = new List<string>();
        group.ColorRefused += (_, value) => refused.Add(value);

        Assert.True(group.TrySetColor("#112233"));
        Assert.Equal("#112233", group.Color);

        Assert.False(group.TrySetColor("red"));
        Assert.False(group.TrySetColor("#12345"));
        Assert.False(group.TrySetColor("#zzzzzz"));
        Assert.Equal("#112233", group.Color);
        Assert.Equal(3, refused.Count);
    }

    [Fact]
    public void DiscoveredNames_KeepTheServerOrder()
    {
        // Already count-descending, as DiscoveredNamesQuery's own ORDER BY produces: with no
        // configured filters the fold (review minor 3) merges nothing and the re-sort by count
        // descending is a no-op, so this is a genuine "no extra client-side reordering" case
        // rather than one the fold happens to leave alone by chance.
        _discovered = [("Ha", 40), ("OIII", 12), ("Clear", 4)];
        using var vm = Create();

        Assert.Equal(new[] { "Ha", "OIII", "Clear" }, vm.Editor.Ungrouped.Select(row => row.Name));
    }

    [Fact]
    public void AGroupWhoseMembersAreAllKnown_IsNotSuggested()
    {
        _store.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Aliases = ["ha"] },
        });
        _discovered = [("Ha", 5), ("ha", 3)];

        using var vm = Create();

        Assert.Empty(vm.Suggestions);
    }

    [Fact]
    public async Task Load_RunsOffTheUiThread()
    {
        var callingThread = Environment.CurrentManagedThreadId;
        int? loadThread = null;
        var vm = new FiltersTabViewModel(
            () =>
            {
                loadThread = Environment.CurrentManagedThreadId;
                return _store.GetFilters();
            },
            _store.SaveFilters,
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discovered,
            post: action => action());

        Assert.NotNull(vm.PendingLoad);
        await vm.PendingLoad!;

        Assert.NotEqual(callingThread, loadThread);
        vm.Dispose();
    }

    [Fact]
    public void LoadFailure_ShowsAFailureLine_NotAnEmptyState()
    {
        var vm = new FiltersTabViewModel(
            () => throw new InvalidOperationException("boom"),
            _store.SaveFilters,
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discovered,
            post: action => action());
        vm.PendingLoad?.Wait(Budget);

        Assert.True(vm.LoadFailed);
        Assert.False(vm.IsLoading);
        Assert.Equal("The discovered filter names could not be loaded.", vm.LoadFailedMessage);
        vm.Dispose();
    }

    [Fact]
    public void Dispose_ReleasesItsSubscriptions()
    {
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var published = false;
        var vm = new FiltersTabViewModel(
            () =>
            {
                started.Set();
                release.Wait(Budget);
                return _store.GetFilters();
            },
            _store.SaveFilters,
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discovered,
            post: action =>
            {
                published = true;
                action();
            });

        // Deterministic: wait for the load to actually be in flight (parked on the gate) before
        // disposing, so this proves the tab-lifetime token really does stop a load that is still
        // running, rather than racing Task.Run's own before-the-fact cancellation.
        Assert.True(started.Wait(Budget));
        vm.Dispose();
        release.Set();

        // Poll rather than Wait(): under a heavily loaded thread pool (many parallel test
        // collections), Task.Run(action, token) can settle into either Canceled or
        // RanToCompletion once the token is already cancelled, and Wait() throws for the former.
        // Reading .Exception observes it either way (never throws) without caring which.
        var load = vm.PendingLoad;
        if (load is not null)
        {
            SpinWait.SpinUntil(() => load.IsCompleted, Budget);
            _ = load.Exception;
        }

        Assert.True(vm.IsDisposed);
        Assert.False(published);
    }

    // ---- P13 R2: the colour spine on the Filters tab -------------------------------------------

    [Fact]
    public async Task SettingAnUngroupedColour_SavesAOneNameGroup()
    {
        _discovered = [("Ha", 40), ("OIII", 12)];
        using var vm = Create();

        vm.Editor.SetUngroupedColor("Ha", "#123456");
        vm.SaveCommand.Execute(null);
        await (vm.PendingSave ?? Task.CompletedTask);

        var saved = _store.GetFilters();
        var entry = Assert.Single(saved);
        Assert.Equal("Ha", entry.Key);
        Assert.Equal("#123456", entry.Value.Color);
        Assert.Empty(entry.Value.Aliases);
    }

    [Fact]
    public async Task SettingAnUngroupedColour_LeavesTheOtherUngroupedNamesUnwritten()
    {
        _discovered = [("Ha", 40), ("OIII", 12), ("Duoband", 3)];
        using var vm = Create();

        vm.Editor.SetUngroupedColor("Ha", "#123456");
        vm.SaveCommand.Execute(null);
        await (vm.PendingSave ?? Task.CompletedTask);

        // Only the name the user coloured becomes a document entry. A discovered name that is
        // merely seeded from the palette is not stored: it resolves through the spine every time.
        Assert.Equal(["Ha"], _store.GetFilters().Keys);
    }

    [Fact]
    public void GroupSelected_StoresNoColour()
    {
        _discovered = [("Ha", 40), ("ha", 12)];
        using var vm = Create();

        vm.Editor.Ungrouped.Single(row => row.Name == "Ha").IsChecked = true;
        vm.Editor.Ungrouped.Single(row => row.Name == "ha").IsChecked = true;
        vm.Editor.GroupSelectedCommand.Execute(null);

        var group = Assert.Single(vm.Editor.Groups);
        Assert.Null(group.Color);
        Assert.Equal("#c44040", group.ResolvedColor);
    }

    // 7.6's rule: SaveAsync writes the stored colour, never the resolved one. Writing the resolved
    // value back would pin every seeded colour into the document on the first unrelated save and
    // make a later palette change invisible to the user.
    [Fact]
    public async Task Save_WritesTheStoredColourAndNotTheResolvedOne()
    {
        using var vm = Create();
        vm.NewFilterName = "Ha";
        vm.AddFilterCommand.Execute(null);

        var group = Assert.Single(vm.Editor.Groups);
        Assert.Equal("#c44040", group.ResolvedColor);

        vm.SaveCommand.Execute(null);
        await (vm.PendingSave ?? Task.CompletedTask);

        var entry = Assert.Single(_store.GetFilters());
        Assert.Null(entry.Value.Color);
        Assert.NotEqual("#c44040", entry.Value.Color);
    }

    // Review P3-5: the grey fallback is never written back as a stored value, because every build
    // before Phase 13 wrote it by itself and a document holding it therefore records an old
    // default rather than a choice. A group resolving to the grey stores null.
    [Fact]
    public async Task Save_NeverWritesTheGreyFallbackAsAStoredColour()
    {
        using var vm = Create();
        vm.NewFilterName = "Duoband";
        vm.AddFilterCommand.Execute(null);

        var group = Assert.Single(vm.Editor.Groups);
        Assert.True(group.TrySetColor("#808080"));
        Assert.Equal("#808080", group.ResolvedColor);

        vm.SaveCommand.Execute(null);
        await (vm.PendingSave ?? Task.CompletedTask);

        Assert.Null(Assert.Single(_store.GetFilters()).Value.Color);
    }

    [Fact]
    public async Task Save_WritesAGreyThatIsNotTheFallback()
    {
        using var vm = Create();
        vm.NewFilterName = "Ha";
        vm.AddFilterCommand.Execute(null);

        var group = Assert.Single(vm.Editor.Groups);
        Assert.True(group.TrySetColor("#7f7f7f"));

        vm.SaveCommand.Execute(null);
        await (vm.PendingSave ?? Task.CompletedTask);

        Assert.Equal("#7f7f7f", Assert.Single(_store.GetFilters()).Value.Color);
    }

    // ---- pending-edits spine: dirty tracking ---------------------------------------------------
    // Before this the Save button was always enabled and nothing knew an edit was staged, so the
    // app-wide save bar had nothing to read. Every user mutation marks the tab dirty; a load does
    // not; a successful save clears it.

    private static IPendingEdits Pending(FiltersTabViewModel vm) => vm;

    [Fact]
    public void PendingEdits_LabelAndNavigationKey()
    {
        using var vm = Create();

        Assert.Equal("Filter names and colours", Pending(vm).Label);
        Assert.Equal("filters", Pending(vm).NavigationKey);
    }

    [Fact]
    public void Load_IsNotDirty_AndSaveIsDisabled()
    {
        _store.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Color = "#ff0000", Aliases = ["H-alpha"] },
        });
        _discovered = [("Ha", 5), ("H-alpha", 3), ("OIII", 2), ("oiii", 2)];

        using var vm = Create();

        Assert.False(vm.IsDirty);
        Assert.False(Pending(vm).HasPendingEdits);
        Assert.Null(Pending(vm).SaveRefusal);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Rename_MarksDirty_AndRaisesHasPendingEdits()
    {
        _store.SaveFilters(new Dictionary<string, FilterSetting> { ["Ha"] = new FilterSetting() });
        using var vm = Create();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var group = Assert.Single(vm.Editor.Groups);

        group.RenameText = "Halpha";
        group.CommitRenameCommand.Execute(null);

        Assert.True(Pending(vm).HasPendingEdits);
        Assert.Contains(nameof(IPendingEdits.HasPendingEdits), raised);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    // One case per user mutation path the tab reaches, so a path that forgets the signal shows up
    // here by name.
    [Theory]
    [InlineData("add filter")]
    [InlineData("remove alias")]
    [InlineData("remove last alias")]
    [InlineData("colour")]
    [InlineData("accept suggestion")]
    [InlineData("dismiss suggestion")]
    [InlineData("group selected")]
    [InlineData("add to group")]
    [InlineData("ungrouped colour")]
    public void EveryUserMutation_MarksDirty(string path)
    {
        _store.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Aliases = ["H-alpha", "Halpha"] },
            ["Lum"] = new FilterSetting { Aliases = ["L"] },
        });
        _discovered = [("OIII", 4), ("oiii", 2), ("SII", 1)];
        using var vm = Create();
        Assert.False(vm.IsDirty);
        var ha = vm.Editor.Groups.Single(group => group.Canonical == "Ha");
        var lum = vm.Editor.Groups.Single(group => group.Canonical == "Lum");

        switch (path)
        {
            case "add filter":
                vm.NewFilterName = "SII";
                vm.AddFilterCommand.Execute(null);
                break;
            case "remove alias":
                ha.RemoveAliasCommand.Execute("Halpha");
                break;
            case "remove last alias":
                lum.RemoveAliasCommand.Execute("L");
                Assert.DoesNotContain(lum, vm.Editor.Groups);
                break;
            case "colour":
                Assert.True(ha.TrySetColor("#123456"));
                break;
            case "accept suggestion":
                vm.AcceptSuggestionCommand.Execute(Assert.Single(vm.Suggestions));
                break;
            case "dismiss suggestion":
                vm.DismissSuggestionCommand.Execute(Assert.Single(vm.Suggestions));
                break;
            case "group selected":
                vm.Editor.Ungrouped.Single(row => row.Name == "OIII").IsChecked = true;
                vm.Editor.Ungrouped.Single(row => row.Name == "oiii").IsChecked = true;
                Assert.False(vm.IsDirty);
                vm.Editor.GroupSelectedCommand.Execute(null);
                break;
            case "add to group":
                vm.Editor.Ungrouped.Single(row => row.Name == "SII").IsChecked = true;
                vm.Editor.AddToGroupCommand.Execute(ha);
                break;
            case "ungrouped colour":
                vm.Editor.SetUngroupedColor("SII", "#123456");
                break;
        }

        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task Save_ClearsDirty()
    {
        using var vm = Create();
        vm.NewFilterName = "Lum";
        vm.AddFilterCommand.Execute(null);
        Assert.True(vm.IsDirty);

        await Pending(vm).SaveAsync();
        await (vm.PendingSave ?? Task.CompletedTask);

        Assert.False(vm.IsDirty);
        Assert.Equal(["Lum"], _store.GetFilters().Keys);
    }

    [Fact]
    public async Task AFailedSave_StaysDirty()
    {
        var vm = new FiltersTabViewModel(
            _store.GetFilters,
            _ => throw new InvalidOperationException("disk full"),
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discovered,
            post: action => action());
        vm.PendingLoad?.Wait(Budget);
        vm.NewFilterName = "Lum";
        vm.AddFilterCommand.Execute(null);

        await Pending(vm).SaveAsync();
        await (vm.PendingSave ?? Task.CompletedTask);

        Assert.True(vm.IsDirty);
        Assert.True(vm.HasErrorMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task SaveRefusal_WhileSaving_AndAnEditDuringTheSaveStaysDirty()
    {
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var vm = new FiltersTabViewModel(
            _store.GetFilters,
            filters =>
            {
                started.Set();
                release.Wait(Budget);
                _store.SaveFilters(filters);
            },
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discovered,
            post: action => action());
        vm.PendingLoad?.Wait(Budget);
        vm.NewFilterName = "Lum";
        vm.AddFilterCommand.Execute(null);

        var save = Pending(vm).SaveAsync();
        Assert.True(started.Wait(Budget));

        Assert.Equal("Filters are still saving.", Pending(vm).SaveRefusal);
        Assert.False(vm.SaveCommand.CanExecute(null));

        // An edit landing while the first save is in flight is not in that save's snapshot, so the
        // save completing must not clear it.
        vm.NewFilterName = "OIII";
        vm.AddFilterCommand.Execute(null);

        release.Set();
        await save;
        await (vm.PendingSave ?? Task.CompletedTask);

        Assert.True(vm.IsDirty);
        Assert.Null(Pending(vm).SaveRefusal);
        Assert.Equal(["Lum"], _store.GetFilters().Keys);
        vm.Dispose();
    }

    [Fact]
    public async Task Discard_RestoresTheLoadedGroups_AndClearsDirty()
    {
        _store.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Color = "#ff0000", Aliases = ["H-alpha"] },
        });
        using var vm = Create();
        var group = Assert.Single(vm.Editor.Groups);
        group.RenameText = "Halpha";
        group.CommitRenameCommand.Execute(null);
        vm.NewFilterName = "Lum";
        vm.AddFilterCommand.Execute(null);
        Assert.True(vm.IsDirty);

        Pending(vm).Discard();
        await SettleAsync(vm);

        Assert.False(vm.IsDirty);
        var restored = Assert.Single(vm.Editor.Groups);
        Assert.Equal("Ha", restored.Canonical);
        Assert.Equal("#ff0000", restored.Color);
        Assert.Equal(["H-alpha"], restored.Aliases);
    }

    [Fact]
    public async Task Discard_RestoresADismissedSuggestion()
    {
        _discovered = [("ha", 2), ("Ha", 9)];
        using var vm = Create();
        vm.DismissSuggestionCommand.Execute(Assert.Single(vm.Suggestions));
        Assert.True(vm.IsDirty);

        Pending(vm).Discard();
        await SettleAsync(vm);

        Assert.False(vm.IsDirty);
        Assert.Single(vm.Suggestions);
    }
}
