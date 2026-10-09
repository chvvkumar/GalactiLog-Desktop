using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Design-spec 12.7's Equipment tab (Phase 9 Task 7). Same list as FiltersTabViewModelTests minus
// the colour cases, plus the two-section cases the Filters tab has none of.
public class EquipmentTabViewModelTests : IDisposable
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly TempDatabase _db = new("galactilog-equipment-tab");
    private readonly SettingsStore _store;
    private readonly AliasMapCache _cache;

    private IReadOnlyList<(string Name, int Count)> _discoveredCameras = [];
    private IReadOnlyList<(string Name, int Count)> _discoveredTelescopes = [];

    public EquipmentTabViewModelTests()
    {
        _store = new SettingsStore(new SettingsRepository(_db.ConnectionString));
        _cache = new AliasMapCache(_store);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
    }

    private EquipmentTabViewModel Create()
    {
        var vm = new EquipmentTabViewModel(
            _store.GetEquipment,
            _store.SaveEquipment,
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discoveredCameras,
            () => _discoveredTelescopes,
            post: action => action());
        vm.PendingLoad?.Wait(Budget);
        return vm;
    }

    [Fact]
    public void Load_PopulatesGroupsDiscoveredNamesAndSuggestions()
    {
        _store.SaveEquipment(new EquipmentSettings
        {
            Cameras = new Dictionary<string, EquipmentItemSettings>
            {
                ["ASI2600MM"] = new EquipmentItemSettings { Aliases = ["ZWO ASI2600MM"] },
            },
        });
        _discoveredCameras = [("ASI2600MM", 10), ("ZWO ASI2600MM", 4), ("ASI294MC", 6)];
        _discoveredTelescopes = [("RC8", 8), ("rc8", 2)];

        using var vm = Create();

        var camera = Assert.Single(vm.CamerasEditor.Groups);
        Assert.Equal("ASI2600MM", camera.Canonical);
        Assert.Equal(["ZWO ASI2600MM"], camera.Aliases);

        var suggestion = Assert.Single(vm.Suggestions);
        Assert.Equal("telescopes", suggestion.Section);
        Assert.Equal(new[] { "RC8", "rc8" }, suggestion.Names);
    }

    [Fact]
    public void EquipmentGroups_AreBornWithNoStoredColour()
    {
        // Phase review P3-5. A camera or telescope group has no swatch (ShowColorPicker is false
        // on both editors) and nothing saves a colour for one, so handing the grey to the row was
        // a literal with no consumer. Null is what the three filter creation sites pass and what
        // "no colour stored" means everywhere else since Task 3.
        _store.SaveEquipment(new EquipmentSettings
        {
            Cameras = new Dictionary<string, EquipmentItemSettings>
            {
                ["ASI2600MM"] = new EquipmentItemSettings { Aliases = ["ZWO ASI2600MM"] },
            },
            Telescopes = new Dictionary<string, EquipmentItemSettings>
            {
                ["RC8"] = new EquipmentItemSettings { Aliases = ["rc8"] },
            },
        });
        _discoveredCameras = [("ASI2600MM", 10), ("ASI533MC", 6), ("asi533mc", 2)];
        _discoveredTelescopes = [("RC8", 8)];

        using var vm = Create();

        Assert.Null(Assert.Single(vm.CamerasEditor.Groups).Color);
        Assert.Null(Assert.Single(vm.TelescopesEditor.Groups).Color);

        var suggestion = Assert.Single(vm.Suggestions);
        vm.AcceptSuggestionCommand.Execute(suggestion);

        Assert.All(vm.CamerasEditor.Groups, group => Assert.Null(group.Color));
    }

    [Fact]
    public void CamerasAndTelescopes_AreTwoIndependentSections()
    {
        _discoveredCameras = [("ASI2600MM", 10)];
        _discoveredTelescopes = [("RC8", 8)];
        using var vm = Create();

        Assert.Equal("ASI2600MM", Assert.Single(vm.CamerasEditor.Ungrouped).Name);
        Assert.Equal("RC8", Assert.Single(vm.TelescopesEditor.Ungrouped).Name);
        Assert.NotSame(vm.CamerasEditor, vm.TelescopesEditor);
    }

    [Fact]
    public void ASuggestionTaggedTelescopes_IsAcceptedIntoTheTelescopeMap()
    {
        _discoveredTelescopes = [("RC8", 8), ("rc8", 2)];
        using var vm = Create();
        var suggestion = Assert.Single(vm.Suggestions);

        vm.AcceptSuggestionCommand.Execute(suggestion);

        var group = Assert.Single(vm.TelescopesEditor.Groups);
        Assert.Equal("RC8", group.Canonical);
        Assert.Equal(["rc8"], group.Aliases);
        Assert.Empty(vm.CamerasEditor.Groups);
    }

    [Fact]
    public async Task Save_WritesBothCamerasAndTelescopesInOneEquipmentDocument()
    {
        using var vm = Create();
        vm.CamerasEditor.AddGroup(new AliasGroupViewModel("ASI2600MM", "#808080", []));
        vm.TelescopesEditor.AddGroup(new AliasGroupViewModel("RC8", "#808080", []));

        vm.SaveCommand.Execute(null);
        await (vm.PendingSave ?? Task.CompletedTask);

        var stored = _store.GetEquipment();
        Assert.Contains("ASI2600MM", stored.Cameras.Keys);
        Assert.Contains("RC8", stored.Telescopes.Keys);
    }

    [Fact]
    public async Task Save_RaisesAliasSourcesChangedExactlyOnce()
    {
        using var vm = Create();
        var raised = 0;
        _store.AliasSourcesChanged += (_, _) => raised++;

        vm.CamerasEditor.AddGroup(new AliasGroupViewModel("ASI2600MM", "#808080", []));
        vm.SaveCommand.Execute(null);
        await (vm.PendingSave ?? Task.CompletedTask);

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task AcceptingASuggestion_WritesTheAliasList()
    {
        _discoveredCameras = [("ASI2600MM", 10), ("asi2600mm", 4)];
        using var vm = Create();
        var suggestion = Assert.Single(vm.Suggestions);

        vm.AcceptSuggestionCommand.Execute(suggestion);
        vm.SaveCommand.Execute(null);
        await (vm.PendingSave ?? Task.CompletedTask);

        var stored = _store.GetEquipment();
        var entry = Assert.Single(stored.Cameras);
        Assert.Equal("ASI2600MM", entry.Key);
        Assert.Equal(["asi2600mm"], entry.Value.Aliases);
    }

    [Fact]
    public async Task AcceptingASuggestion_InvalidatesTheAliasMapCache()
    {
        _discoveredCameras = [("ASI2600MM", 10), ("asi2600mm", 4)];
        using var vm = Create();
        var before = _cache.Current;

        var suggestion = Assert.Single(vm.Suggestions);
        vm.AcceptSuggestionCommand.Execute(suggestion);
        vm.SaveCommand.Execute(null);
        await (vm.PendingSave ?? Task.CompletedTask);

        var after = _cache.Current;
        Assert.NotSame(before, after);
        Assert.Equal("ASI2600MM", after.CanonicalCamera("asi2600mm"));
    }

    [Fact]
    public void AcceptingASuggestion_ChoosesTheHighestCountNameAsCanonical()
    {
        _discoveredCameras = [("asi2600mm", 2), ("ASI2600MM", 9)];
        using var vm = Create();

        var suggestion = Assert.Single(vm.Suggestions);

        Assert.Equal("ASI2600MM", suggestion.SelectedName);
    }

    [Fact]
    public async Task AcceptingASuggestion_NeverLeavesADocumentWhereANameIsBothACanonicalKeyAndAnAlias()
    {
        // "asi2600mm" starts as its own canonical camera; the discovered pair is similar
        // (case-insensitive exact match) and "ASI2600MM" has the higher count, so accepting must
        // absorb the existing "asi2600mm" group rather than leave it standing as both a canonical
        // key and "ASI2600MM"'s alias (review I2).
        _store.SaveEquipment(new EquipmentSettings
        {
            Cameras = new Dictionary<string, EquipmentItemSettings> { ["asi2600mm"] = new EquipmentItemSettings() },
        });
        _discoveredCameras = [("ASI2600MM", 9), ("asi2600mm", 2)];
        using var vm = Create();

        var suggestion = Assert.Single(vm.Suggestions);
        Assert.Equal("ASI2600MM", suggestion.SelectedName);

        vm.AcceptSuggestionCommand.Execute(suggestion);
        vm.SaveCommand.Execute(null);
        await (vm.PendingSave ?? Task.CompletedTask);

        var stored = _store.GetEquipment();
        var canonicalNames = new HashSet<string>(stored.Cameras.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain(
            stored.Cameras.Values.SelectMany(entry => entry.Aliases),
            alias => canonicalNames.Contains(alias));

        var camera = Assert.Single(stored.Cameras);
        Assert.Equal("ASI2600MM", camera.Key);
        Assert.Equal(["asi2600mm"], camera.Value.Aliases);
    }

    [Fact]
    public void DismissingASuggestion_AppendsTheSortedNameListToDismissed()
    {
        _discoveredCameras = [("asi2600mm", 2), ("ASI2600MM", 9)];
        using var vm = Create();
        var suggestion = Assert.Single(vm.Suggestions);

        vm.DismissSuggestionCommand.Execute(suggestion);
        vm.SaveCommand.Execute(null);
        vm.PendingSave?.Wait(Budget);

        var dismissed = _store.GetDismissedSuggestions();
        var group = Assert.Single(dismissed);
        Assert.Equal(new[] { "ASI2600MM", "asi2600mm" }, group);
    }

    [Fact]
    public void DismissedSuggestion_DoesNotReappearAfterAReload()
    {
        _discoveredCameras = [("asi2600mm", 2), ("ASI2600MM", 9)];
        using var vm = Create();
        var suggestion = Assert.Single(vm.Suggestions);
        vm.DismissSuggestionCommand.Execute(suggestion);

        vm.Reload();

        Assert.Empty(vm.Suggestions);
    }

    [Fact]
    public void DismissedSuggestion_IsNotPersistedUntilSave()
    {
        _discoveredCameras = [("asi2600mm", 2), ("ASI2600MM", 9)];
        using var vm = Create();
        var suggestion = Assert.Single(vm.Suggestions);

        vm.DismissSuggestionCommand.Execute(suggestion);

        Assert.Empty(_store.GetDismissedSuggestions());
    }

    [Fact]
    public async Task Save_WritesEquipmentThenDismissedSuggestions_InThatOrder()
    {
        var order = new List<string>();
        var vm = new EquipmentTabViewModel(
            _store.GetEquipment,
            equipment =>
            {
                order.Add("equipment");
                _store.SaveEquipment(equipment);
            },
            _store.GetDismissedSuggestions,
            dismissed =>
            {
                order.Add("dismissed");
                _store.SaveDismissedSuggestions(dismissed);
            },
            () => _discoveredCameras,
            () => _discoveredTelescopes,
            post: action => action());
        vm.PendingLoad?.Wait(Budget);

        vm.CamerasEditor.AddGroup(new AliasGroupViewModel("ASI2600MM", "#808080", []));
        vm.SaveCommand.Execute(null);
        await (vm.PendingSave ?? Task.CompletedTask);

        Assert.Equal(["equipment", "dismissed"], order);
        vm.Dispose();
    }

    [Fact]
    public void RenamingAGroup_MovesTheEntry_KeepingTheColourAndAliases()
    {
        using var vm = Create();
        var group = new AliasGroupViewModel("RC8", "#808080", ["RC-8"]);
        vm.TelescopesEditor.AddGroup(group);

        group.RenameText = "RC 8 inch";
        group.CommitRenameCommand.Execute(null);

        Assert.Equal("RC 8 inch", group.Canonical);
        Assert.Equal(["RC-8"], group.Aliases);
    }

    [Fact]
    public void RenamingAGroup_RefusesAnExistingName()
    {
        using var vm = Create();
        vm.TelescopesEditor.AddGroup(new AliasGroupViewModel("RC8", "#808080", []));
        var other = new AliasGroupViewModel("FRA600", "#808080", []);
        vm.TelescopesEditor.AddGroup(other);

        other.RenameText = "RC8";
        other.CommitRenameCommand.Execute(null);

        Assert.Equal("FRA600", other.Canonical);
        Assert.True(vm.HasErrorMessage);
    }

    [Fact]
    public void RemovingTheLastAlias_DeletesTheGroup()
    {
        using var vm = Create();
        var group = new AliasGroupViewModel("RC8", "#808080", ["RC-8"]);
        vm.TelescopesEditor.AddGroup(group);

        group.RemoveAliasCommand.Execute("RC-8");

        Assert.Empty(vm.TelescopesEditor.Groups);
    }

    [Fact]
    public void DiscoveredNames_KeepTheServerOrder()
    {
        // Already count-descending; see FiltersTabViewModelTests's identical case for why.
        _discoveredCameras = [("ASI2600MM", 40), ("ASI533MC", 12), ("ASI294MC", 4)];
        using var vm = Create();

        Assert.Equal(new[] { "ASI2600MM", "ASI533MC", "ASI294MC" }, vm.CamerasEditor.Ungrouped.Select(row => row.Name));
    }

    [Fact]
    public void AGroupWhoseMembersAreAllKnown_IsNotSuggested()
    {
        _store.SaveEquipment(new EquipmentSettings
        {
            Cameras = new Dictionary<string, EquipmentItemSettings>
            {
                ["ASI2600MM"] = new EquipmentItemSettings { Aliases = ["asi2600mm"] },
            },
        });
        _discoveredCameras = [("ASI2600MM", 10), ("asi2600mm", 4)];

        using var vm = Create();

        Assert.Empty(vm.Suggestions);
    }

    [Fact]
    public async Task Load_RunsOffTheUiThread()
    {
        var callingThread = Environment.CurrentManagedThreadId;
        int? loadThread = null;
        var vm = new EquipmentTabViewModel(
            () =>
            {
                loadThread = Environment.CurrentManagedThreadId;
                return _store.GetEquipment();
            },
            _store.SaveEquipment,
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discoveredCameras,
            () => _discoveredTelescopes,
            post: action => action());

        Assert.NotNull(vm.PendingLoad);
        await vm.PendingLoad!;

        Assert.NotEqual(callingThread, loadThread);
        vm.Dispose();
    }

    [Fact]
    public void LoadFailure_ShowsAFailureLine_NotAnEmptyState()
    {
        var vm = new EquipmentTabViewModel(
            () => throw new InvalidOperationException("boom"),
            _store.SaveEquipment,
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discoveredCameras,
            () => _discoveredTelescopes,
            post: action => action());
        vm.PendingLoad?.Wait(Budget);

        Assert.True(vm.LoadFailed);
        Assert.False(vm.IsLoading);
        Assert.Equal("The discovered equipment names could not be loaded.", vm.LoadFailedMessage);
        vm.Dispose();
    }

    [Fact]
    public void Dispose_ReleasesItsSubscriptions()
    {
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var published = false;
        var vm = new EquipmentTabViewModel(
            () =>
            {
                started.Set();
                release.Wait(Budget);
                return _store.GetEquipment();
            },
            _store.SaveEquipment,
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discoveredCameras,
            () => _discoveredTelescopes,
            post: action =>
            {
                published = true;
                action();
            });

        // Deterministic: wait for the load to actually be in flight before disposing, so this
        // proves the tab-lifetime token stops a load that is still running.
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

    // ---- Phase 15B Task 5c: the PHD2 panel request and carried item 64 --------------------------

    [Fact]
    public void ThePhd2ProfilesRequest_IsPendingUntilItIsConsumed_AndThenOnce()
    {
        using var vm = Create();

        Assert.False(vm.ConsumePhd2ProfilesInViewRequest());

        vm.RequestPhd2ProfilesInView();

        Assert.True(vm.ConsumePhd2ProfilesInViewRequest());

        // One route scrolls once: the second visit to the tab is an ordinary visit.
        Assert.False(vm.ConsumePhd2ProfilesInViewRequest());
    }

    // The panel over a counted row read, so a row rebuild is observable.
    private Phd2ProfilesViewModel CreatePanel(Action onLoad, params Phd2ProfileRow[] rows)
        => new(
            _store.GetGeneral,
            () =>
            {
                onLoad();
                return rows;
            },
            _store.MutateGeneral,
            () => [],
            systemTimezones: () => ["UTC"],
            post: action => action());

    private static readonly DateTime Seen = new(2026, 3, 1, 20, 5, 0, DateTimeKind.Utc);

    // One profile as Phd2ProfilesQuery returns it, with both "seen" values resolved instants, so
    // the rendered seen text follows the display preferences on the general document.
    private static Phd2ProfileRow Row(string profile = "Rig A")
        => new(profile, "ZWO ASI294MM Mini", 500, 1.23, 5, Seen.AddDays(-10), Seen);

    private EquipmentTabViewModel CreateWithPanel(Phd2ProfilesViewModel panel)
    {
        var vm = new EquipmentTabViewModel(
            _store.GetEquipment,
            _store.SaveEquipment,
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discoveredCameras,
            () => _discoveredTelescopes,
            post: action => action(),
            phd2Profiles: panel);
        vm.PendingLoad?.Wait(Budget);
        return vm;
    }

    // Joins the panel's read, and the write a row edit starts, so a case asserts against a settled
    // panel instead of sleeping. Both, in this order, because a handler that reloads starts its
    // read from inside the write.
    private static void SettlePanel(Phd2ProfilesViewModel panel)
    {
        panel.PendingWrite.Wait(Budget);
        panel.PendingLoad?.Wait(Budget);
    }

    private EquipmentTabViewModel FollowingTab(Phd2ProfilesViewModel panel)
    {
        var vm = CreateWithPanel(panel);
        vm.FollowGeneralChanges(
            handler => _store.GeneralChanged += handler,
            handler => _store.GeneralChanged -= handler);
        SettlePanel(panel);
        return vm;
    }

    // Carried item 64, as the Task 5c review re-scoped it. What goes stale is spec 12.7's "a re-run
    // is still owed" sentence, which the panel reads only at load; the rows are not stale and must
    // not be rebuilt, because the panel commits every row edit through the same MutateGeneral that
    // raises this event.
    //
    // The clear is NOT a MutateGeneral: that writer preserves the flag monotonically
    // (SettingsStore.WithCorrelationPending). ClearCorrelationPendingIfUnchanged is the one door
    // that clears it and it is what a completed correlation pass calls, so it is what this case
    // drives.
    [Fact]
    public void AReRunCompleting_TakesTheOwedSentenceDown_WithoutReadingOrRebuildingTheRows()
    {
        _store.MutateGeneral(general => general with { Phd2CorrelationPending = true });
        var loads = 0;
        var panel = CreatePanel(() => Interlocked.Increment(ref loads), Row());
        SettlePanel(panel);
        using var vm = FollowingTab(panel);
        Assert.Equal(Phd2ProfilesViewModel.ReRunOwedMessage, panel.PendingMessage);
        var row = Assert.Single(panel.Rows);
        var before = Volatile.Read(ref loads);

        Assert.True(_store.ClearCorrelationPendingIfUnchanged(_store.GetGeneral()));
        SettlePanel(panel);

        Assert.Null(panel.PendingMessage);
        Assert.Same(row, Assert.Single(panel.Rows));
        Assert.Equal(before, Volatile.Read(ref loads));
    }

    // Review P2-1, the defect this handler shipped with: a row edit commits through MutateGeneral,
    // the store raises, and a handler that reloads clears Rows and rebuilds them, destroying the
    // TextBox the user is typing into on the first keystroke that parses.
    [Fact]
    public void TypingIntoARow_WhenItsOwnCommitRaisesTheStoresEvent_KeepsTheSameRowAndTheTypedText()
    {
        var panel = CreatePanel(() => { }, Row());
        SettlePanel(panel);
        using var vm = FollowingTab(panel);
        var row = Assert.Single(panel.Rows);

        row.LatitudeText = "45.5";
        SettlePanel(panel);

        Assert.Same(row, Assert.Single(panel.Rows));
        Assert.Equal("45.5", row.LatitudeText);
    }

    // The same defect from the other side: every general save anywhere in the application reaches
    // this handler, not only the panel's own.
    [Fact]
    public void AGeneralSaveMadeElsewhere_LeavesTheRowInstancesAlone()
    {
        var loads = 0;
        var panel = CreatePanel(() => Interlocked.Increment(ref loads), Row());
        SettlePanel(panel);
        using var vm = FollowingTab(panel);
        var row = Assert.Single(panel.Rows);
        var before = Volatile.Read(ref loads);

        _store.MutateGeneral(general => general with { Use24HTime = !general.Use24HTime });
        SettlePanel(panel);

        Assert.Same(row, Assert.Single(panel.Rows));
        Assert.Equal(before, Volatile.Read(ref loads));
    }

    // And what the handler does do: the derived text on each existing row, which is what carries
    // the seen times and the zone and site resolution, is recomputed from the new document.
    [Fact]
    public void AGeneralSaveMadeElsewhere_RefreshesEachRowsDerivedText_OnTheSameInstance()
    {
        var panel = CreatePanel(() => { }, Row());
        SettlePanel(panel);
        using var vm = FollowingTab(panel);
        var row = Assert.Single(panel.Rows);
        var before = row.LastSeenText;
        Assert.NotEmpty(before);

        _store.MutateGeneral(general => general with { Use24HTime = !general.Use24HTime });
        SettlePanel(panel);

        Assert.NotEqual(before, row.LastSeenText);
    }

    // The item 56 boundary, which is a statement about the constructor rather than about a
    // behaviour: carried item 56 owns that constructor (it calls Load() on a pool thread and the
    // structural fix is moving that call out), so Task 5c's follower is a member the host calls
    // afterwards and the constructor gained nothing at all.
    //
    // A source scan rather than a behavioural case deliberately. "A tab nobody asked to follow
    // answers no general change" is true by construction: this tab holds no store and no
    // subscribe delegate, so no defect short of adding one can make such a case fail, and a case
    // that cannot fail is the shape this task's review rejected twice.
    [Fact]
    public void TheConstructor_NamesNoSubscription_SoCarriedItemFiftySixsBoundaryHolds()
    {
        var source = SourceScan.StripComments(File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "ViewModels", "Settings", "EquipmentTabViewModel.cs")));
        var start = source.IndexOf("public EquipmentTabViewModel(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var constructor = source[start..end];

        Assert.DoesNotContain("FollowGeneralChanges", constructor, StringComparison.Ordinal);
        Assert.DoesNotContain("GeneralChanged", constructor, StringComparison.Ordinal);
        Assert.DoesNotContain("subscribe", constructor, StringComparison.OrdinalIgnoreCase);
    }

    // The subscriber side of the real store, which exposes no invocation list to read. The tab's
    // own guard would hide a leaked handler behind a quiet reload, so the count is what this case
    // asserts rather than the effect.
    private sealed class GeneralChangedRelay
    {
        private EventHandler<GeneralSettings>? _changed;

        public int Handlers { get; private set; }

        public void Subscribe(EventHandler<GeneralSettings> handler)
        {
            _changed += handler;
            Handlers++;
        }

        public void Unsubscribe(EventHandler<GeneralSettings> handler)
        {
            _changed -= handler;
            Handlers--;
        }

        public void Raise() => _changed?.Invoke(this, new GeneralSettings());
    }

    [Fact]
    public void ADisposedTab_LeavesNoGeneralChangedHandlerBehind()
    {
        var panel = CreatePanel(() => { }, Row());
        SettlePanel(panel);
        var vm = CreateWithPanel(panel);
        var relay = new GeneralChangedRelay();
        vm.FollowGeneralChanges(relay.Subscribe, relay.Unsubscribe);
        SettlePanel(panel);
        Assert.Equal(1, relay.Handlers);

        vm.Dispose();
        var message = panel.PendingMessage;
        relay.Raise();
        SettlePanel(panel);

        // The store is a process singleton and outlives every tab built over it, so a handler left
        // on it would hold a disposed tab and its panel alive for the life of the process.
        Assert.Equal(0, relay.Handlers);
        Assert.Equal(message, panel.PendingMessage);
    }

    // Review P3-2: the member is public, so a second call would subscribe a second handler and
    // overwrite the unfollow that drops the first.
    [Fact]
    public void FollowGeneralChanges_CalledTwice_SubscribesOnce_AndDisposeDropsThatOne()
    {
        var panel = CreatePanel(() => { }, Row());
        SettlePanel(panel);
        var vm = CreateWithPanel(panel);
        var relay = new GeneralChangedRelay();

        vm.FollowGeneralChanges(relay.Subscribe, relay.Unsubscribe);
        vm.FollowGeneralChanges(relay.Subscribe, relay.Unsubscribe);

        Assert.Equal(1, relay.Handlers);

        vm.Dispose();

        Assert.Equal(0, relay.Handlers);
    }

    // ---- pending-edits spine: dirty tracking ---------------------------------------------------
    // Before this the Save button was always enabled and nothing knew an edit was staged, so the
    // app-wide save bar had nothing to read. An edit in either editor, or a dismissal, marks the
    // tab dirty; a load does not; a save that reached the end clears it.

    private static IPendingEdits Pending(EquipmentTabViewModel vm) => vm;

    [Fact]
    public void PendingEdits_LabelAndNavigationKey()
    {
        using var vm = Create();

        Assert.Equal("Equipment names", Pending(vm).Label);
        Assert.Equal("equipment", Pending(vm).NavigationKey);
    }

    [Fact]
    public void Load_IsNotDirty_AndSaveIsDisabled()
    {
        _store.SaveEquipment(new EquipmentSettings
        {
            Cameras = new Dictionary<string, EquipmentItemSettings>
            {
                ["ASI2600MM"] = new EquipmentItemSettings { Aliases = ["ZWO ASI2600MM"] },
            },
            Telescopes = new Dictionary<string, EquipmentItemSettings>
            {
                ["RC8"] = new EquipmentItemSettings { Aliases = ["rc8"] },
            },
        });
        _discoveredCameras = [("ASI294MC", 6), ("asi294mc", 2)];

        using var vm = Create();

        Assert.False(vm.IsDirty);
        Assert.False(Pending(vm).HasPendingEdits);
        Assert.Null(Pending(vm).SaveRefusal);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void TelescopeRename_MarksDirty_AndRaisesHasPendingEdits()
    {
        _store.SaveEquipment(new EquipmentSettings
        {
            Telescopes = new Dictionary<string, EquipmentItemSettings> { ["RC8"] = new EquipmentItemSettings() },
        });
        using var vm = Create();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var group = Assert.Single(vm.TelescopesEditor.Groups);

        group.RenameText = "RC 8 inch";
        group.CommitRenameCommand.Execute(null);

        Assert.True(Pending(vm).HasPendingEdits);
        Assert.Contains(nameof(IPendingEdits.HasPendingEdits), raised);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    // One case per user mutation path the tab reaches, in both editors, so a path that forgets
    // the signal shows up here by name.
    [Theory]
    [InlineData("camera rename")]
    [InlineData("camera remove alias")]
    [InlineData("camera remove last alias")]
    [InlineData("camera group selected")]
    [InlineData("camera add to group")]
    [InlineData("telescope remove alias")]
    [InlineData("telescope group selected")]
    [InlineData("accept camera suggestion")]
    [InlineData("accept telescope suggestion")]
    [InlineData("dismiss suggestion")]
    public void EveryUserMutation_MarksDirty(string path)
    {
        _store.SaveEquipment(new EquipmentSettings
        {
            Cameras = new Dictionary<string, EquipmentItemSettings>
            {
                ["ASI2600MM"] = new EquipmentItemSettings { Aliases = ["ZWO ASI2600MM", "asi2600"] },
                ["ASI533MC"] = new EquipmentItemSettings { Aliases = ["533"] },
            },
            Telescopes = new Dictionary<string, EquipmentItemSettings>
            {
                ["RC8"] = new EquipmentItemSettings { Aliases = ["rc8", "RC-8"] },
            },
        });
        _discoveredCameras = [("ASI294MC", 6), ("asi294mc", 2), ("QHY268M", 1)];
        _discoveredTelescopes = [("FSQ106", 5), ("fsq106", 2)];
        using var vm = Create();
        Assert.False(vm.IsDirty);
        var asi2600 = vm.CamerasEditor.Groups.Single(group => group.Canonical == "ASI2600MM");
        var asi533 = vm.CamerasEditor.Groups.Single(group => group.Canonical == "ASI533MC");
        var rc8 = vm.TelescopesEditor.Groups.Single(group => group.Canonical == "RC8");

        switch (path)
        {
            case "camera rename":
                asi2600.RenameText = "ASI 2600";
                asi2600.CommitRenameCommand.Execute(null);
                break;
            case "camera remove alias":
                asi2600.RemoveAliasCommand.Execute("asi2600");
                break;
            case "camera remove last alias":
                asi533.RemoveAliasCommand.Execute("533");
                Assert.DoesNotContain(asi533, vm.CamerasEditor.Groups);
                break;
            case "camera group selected":
                vm.CamerasEditor.Ungrouped.Single(row => row.Name == "ASI294MC").IsChecked = true;
                vm.CamerasEditor.Ungrouped.Single(row => row.Name == "asi294mc").IsChecked = true;
                Assert.False(vm.IsDirty);
                vm.CamerasEditor.GroupSelectedCommand.Execute(null);
                break;
            case "camera add to group":
                vm.CamerasEditor.Ungrouped.Single(row => row.Name == "QHY268M").IsChecked = true;
                vm.CamerasEditor.AddToGroupCommand.Execute(asi2600);
                break;
            case "telescope remove alias":
                rc8.RemoveAliasCommand.Execute("RC-8");
                break;
            case "telescope group selected":
                vm.TelescopesEditor.Ungrouped.Single(row => row.Name == "FSQ106").IsChecked = true;
                vm.TelescopesEditor.Ungrouped.Single(row => row.Name == "fsq106").IsChecked = true;
                vm.TelescopesEditor.GroupSelectedCommand.Execute(null);
                break;
            case "accept camera suggestion":
                vm.AcceptSuggestionCommand.Execute(vm.Suggestions.Single(s => s.Section == "cameras"));
                break;
            case "accept telescope suggestion":
                vm.AcceptSuggestionCommand.Execute(vm.Suggestions.Single(s => s.Section == "telescopes"));
                break;
            case "dismiss suggestion":
                vm.DismissSuggestionCommand.Execute(vm.Suggestions.Single(s => s.Section == "cameras"));
                break;
        }

        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task Save_ClearsDirty()
    {
        using var vm = Create();
        vm.CamerasEditor.AddGroup(new AliasGroupViewModel("ASI2600MM", null, []));
        Assert.True(vm.IsDirty);

        await Pending(vm).SaveAsync();
        await (vm.PendingSave ?? Task.CompletedTask);

        Assert.False(vm.IsDirty);
        Assert.Equal(["ASI2600MM"], _store.GetEquipment().Cameras.Keys);
    }

    [Fact]
    public async Task AFailedSave_StaysDirty()
    {
        var vm = new EquipmentTabViewModel(
            _store.GetEquipment,
            _ => throw new InvalidOperationException("disk full"),
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discoveredCameras,
            () => _discoveredTelescopes,
            post: action => action());
        vm.PendingLoad?.Wait(Budget);
        vm.TelescopesEditor.AddGroup(new AliasGroupViewModel("RC8", null, []));

        await Pending(vm).SaveAsync();
        await (vm.PendingSave ?? Task.CompletedTask);

        Assert.True(vm.IsDirty);
        Assert.True(vm.HasErrorMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task AFailedRigLabelRewrite_StaysDirty()
    {
        // The rig-label rewrite is the last write of a save. The equipment document has landed by
        // then, but the loaded names are not remembered, so the next save repeats the rewrite; the
        // edit has to stay pending for that next save to be offered at all.
        _store.SaveEquipment(new EquipmentSettings
        {
            Telescopes = new Dictionary<string, EquipmentItemSettings> { ["RC8"] = new EquipmentItemSettings() },
        });
        var vm = new EquipmentTabViewModel(
            _store.GetEquipment,
            _store.SaveEquipment,
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discoveredCameras,
            () => _discoveredTelescopes,
            post: action => action(),
            rewriteRigLabels: (_, _) => throw new InvalidOperationException("catalogue locked"));
        vm.PendingLoad?.Wait(Budget);
        var group = Assert.Single(vm.TelescopesEditor.Groups);
        group.RenameText = "RC 8 inch";
        group.CommitRenameCommand.Execute(null);

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
        var vm = new EquipmentTabViewModel(
            _store.GetEquipment,
            equipment =>
            {
                started.Set();
                release.Wait(Budget);
                _store.SaveEquipment(equipment);
            },
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discoveredCameras,
            () => _discoveredTelescopes,
            post: action => action());
        vm.PendingLoad?.Wait(Budget);
        vm.CamerasEditor.AddGroup(new AliasGroupViewModel("ASI2600MM", null, []));

        var save = Pending(vm).SaveAsync();
        Assert.True(started.Wait(Budget));

        Assert.Equal("Equipment is still saving.", Pending(vm).SaveRefusal);
        Assert.False(vm.SaveCommand.CanExecute(null));

        // An edit landing while the first save is in flight is not in that save's snapshot, so the
        // save completing must not clear it.
        vm.TelescopesEditor.AddGroup(new AliasGroupViewModel("RC8", null, []));

        release.Set();
        await save;
        await (vm.PendingSave ?? Task.CompletedTask);

        Assert.True(vm.IsDirty);
        Assert.Null(Pending(vm).SaveRefusal);
        Assert.Equal(["ASI2600MM"], _store.GetEquipment().Cameras.Keys);
        Assert.Empty(_store.GetEquipment().Telescopes);
        vm.Dispose();
    }

    [Fact]
    public async Task Discard_RestoresTheLoadedGroups_AndClearsDirty()
    {
        _store.SaveEquipment(new EquipmentSettings
        {
            Telescopes = new Dictionary<string, EquipmentItemSettings>
            {
                ["RC8"] = new EquipmentItemSettings { Aliases = ["rc8"] },
            },
        });
        using var vm = Create();
        var group = Assert.Single(vm.TelescopesEditor.Groups);
        group.RenameText = "RC 8 inch";
        group.CommitRenameCommand.Execute(null);
        vm.CamerasEditor.AddGroup(new AliasGroupViewModel("ASI2600MM", null, []));
        Assert.True(vm.IsDirty);

        Pending(vm).Discard();
        await (vm.PendingLoad ?? Task.CompletedTask);

        Assert.False(vm.IsDirty);
        Assert.Empty(vm.CamerasEditor.Groups);
        var restored = Assert.Single(vm.TelescopesEditor.Groups);
        Assert.Equal("RC8", restored.Canonical);
        Assert.Equal(["rc8"], restored.Aliases);
    }

    [Fact]
    public async Task Discard_RestoresADismissedSuggestion()
    {
        _discoveredCameras = [("asi2600mm", 2), ("ASI2600MM", 9)];
        using var vm = Create();
        vm.DismissSuggestionCommand.Execute(Assert.Single(vm.Suggestions));
        Assert.True(vm.IsDirty);

        Pending(vm).Discard();
        await (vm.PendingLoad ?? Task.CompletedTask);

        Assert.False(vm.IsDirty);
        Assert.Single(vm.Suggestions);
    }

    [Fact]
    public async Task Discard_DuringAnInFlightSave_ReloadsOnlyAfterTheSaveHasWritten()
    {
        // A reload started while the save is still writing could read the pre-save document,
        // leave the editors on it with IsDirty false, and let the next save silently revert this
        // one. The reload waits for the save instead.
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var loads = 0;
        var vm = new EquipmentTabViewModel(
            () =>
            {
                loads++;
                return _store.GetEquipment();
            },
            equipment =>
            {
                started.Set();
                release.Wait(Budget);
                _store.SaveEquipment(equipment);
            },
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discoveredCameras,
            () => _discoveredTelescopes,
            post: action => action());
        vm.PendingLoad?.Wait(Budget);
        vm.CamerasEditor.AddGroup(new AliasGroupViewModel("ASI2600MM", null, []));

        var save = Pending(vm).SaveAsync();
        Assert.True(started.Wait(Budget));
        Pending(vm).Discard();
        Assert.Equal(1, loads);

        release.Set();
        await save;
        await (vm.PendingLoad ?? Task.CompletedTask);

        Assert.Equal(2, loads);
        Assert.False(vm.IsDirty);
        Assert.Equal("ASI2600MM", Assert.Single(vm.CamerasEditor.Groups).Canonical);
        vm.Dispose();
    }

    [Fact]
    public async Task Discard_WhoseReloadFails_StaysDirty()
    {
        var loads = 0;
        var vm = new EquipmentTabViewModel(
            () => ++loads == 1 ? _store.GetEquipment() : throw new InvalidOperationException("locked"),
            _store.SaveEquipment,
            _store.GetDismissedSuggestions,
            _store.SaveDismissedSuggestions,
            () => _discoveredCameras,
            () => _discoveredTelescopes,
            post: action => action());
        vm.PendingLoad?.Wait(Budget);
        vm.CamerasEditor.AddGroup(new AliasGroupViewModel("ASI2600MM", null, []));

        Pending(vm).Discard();
        await (vm.PendingLoad ?? Task.CompletedTask);

        // The failed read left the edit on screen, so the save bar has to stay up for it.
        Assert.True(vm.LoadFailed);
        Assert.Single(vm.CamerasEditor.Groups);
        Assert.True(vm.IsDirty);
        Assert.True(Pending(vm).HasPendingEdits);
        vm.Dispose();
    }
}
