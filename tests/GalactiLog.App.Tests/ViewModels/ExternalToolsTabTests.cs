using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Integrations;
using GalactiLog.Core.Settings;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Spec amendment 4c's External Tools tab (Phase 21 Task 4), task4-external-tools-tab.md section
// 4. Delegates throughout, so every case runs with lambdas and no database (design-spec 18.3).
public class ExternalToolsTabTests
{
    private sealed class FakeStore
    {
        public GeneralSettings Current { get; private set; } = new();

        public GeneralSettings Get() => Current;

        public GeneralSettings Mutate(Func<GeneralSettings, GeneralSettings> mutate)
        {
            Current = mutate(Current);
            return Current;
        }
    }

    private static async Task<(ExternalToolsTabViewModel Tab, FakeStore Store)> CreateAsync(
        GeneralSettings? seed = null, IReadOnlyList<string>? knownFilters = null)
    {
        var store = new FakeStore();
        if (seed is not null)
        {
            store.Mutate(_ => seed);
        }

        var tab = new ExternalToolsTabViewModel(
            store.Get,
            store.Mutate,
            () => knownFilters ?? [],
            post: action => action());

        if (tab.PendingLoad is { } load)
        {
            await load.ConfigureAwait(false);
        }

        return (tab, store);
    }

    private static Task Settle(ExternalToolsTabViewModel tab) => tab.PendingWrite;

    // ---- required case 1 (Tier 1): one edit rewrites one key --------------------------------

    [Fact]
    public async Task EditingTheFilterIdMap_LeavesTheOtherThreeKeysUntouched()
    {
        var seed = new GeneralSettings
        {
            AstroBinFilterIdsDocument = IntegrationSettings.WriteFilterId(null, "Ha", 5),
            AstroBinBortle = 4,
            NinaInstancesDocument = IntegrationSettings.WriteInstances([new IntegrationInstance("Obsy", "http://host:1888", true)]),
            StellariumInstancesDocument = IntegrationSettings.WriteInstances([new IntegrationInstance("Desk", "http://host:8090", true)]),
        };
        var (tab, store) = await CreateAsync(seed, ["Ha"]);
        var beforeBortle = store.Current.AstroBinBortle;
        var beforeNina = store.Current.NinaInstancesDocument!.Value.GetRawText();
        var beforeStellarium = store.Current.StellariumInstancesDocument!.Value.GetRawText();

        tab.FilterIdRows.Single().IdText = "9";
        await Settle(tab);

        Assert.Equal(9, IntegrationSettings.ReadFilterIds(store.Current.AstroBinFilterIdsDocument)["Ha"]);
        Assert.Equal(beforeBortle, store.Current.AstroBinBortle);
        Assert.Equal(beforeNina, store.Current.NinaInstancesDocument!.Value.GetRawText());
        Assert.Equal(beforeStellarium, store.Current.StellariumInstancesDocument!.Value.GetRawText());
    }

    [Fact]
    public async Task EditingTheBortleBox_LeavesTheOtherThreeKeysUntouched()
    {
        var seed = new GeneralSettings
        {
            AstroBinFilterIdsDocument = IntegrationSettings.WriteFilterId(null, "Ha", 5),
            AstroBinBortle = 4,
            NinaInstancesDocument = IntegrationSettings.WriteInstances([new IntegrationInstance("Obsy", "http://host:1888", true)]),
            StellariumInstancesDocument = IntegrationSettings.WriteInstances([new IntegrationInstance("Desk", "http://host:8090", true)]),
        };
        var (tab, store) = await CreateAsync(seed, ["Ha"]);
        var beforeFilterIds = store.Current.AstroBinFilterIdsDocument!.Value.GetRawText();
        var beforeNina = store.Current.NinaInstancesDocument!.Value.GetRawText();
        var beforeStellarium = store.Current.StellariumInstancesDocument!.Value.GetRawText();

        tab.BortleText = "7";
        await Settle(tab);

        Assert.Equal(7, store.Current.AstroBinBortle);
        Assert.Equal(beforeFilterIds, store.Current.AstroBinFilterIdsDocument!.Value.GetRawText());
        Assert.Equal(beforeNina, store.Current.NinaInstancesDocument!.Value.GetRawText());
        Assert.Equal(beforeStellarium, store.Current.StellariumInstancesDocument!.Value.GetRawText());
    }

    [Fact]
    public async Task EditingANinaInstance_LeavesTheOtherThreeKeysUntouched()
    {
        var seed = new GeneralSettings
        {
            AstroBinFilterIdsDocument = IntegrationSettings.WriteFilterId(null, "Ha", 5),
            AstroBinBortle = 4,
            NinaInstancesDocument = IntegrationSettings.WriteInstances([new IntegrationInstance("Obsy", "http://host:1888", true)]),
            StellariumInstancesDocument = IntegrationSettings.WriteInstances([new IntegrationInstance("Desk", "http://host:8090", true)]),
        };
        var (tab, store) = await CreateAsync(seed, ["Ha"]);
        var beforeFilterIds = store.Current.AstroBinFilterIdsDocument!.Value.GetRawText();
        var beforeBortle = store.Current.AstroBinBortle;
        var beforeStellarium = store.Current.StellariumInstancesDocument!.Value.GetRawText();

        tab.NinaInstances.Single().Name = "Observatory";
        await Settle(tab);

        Assert.Equal("Observatory", IntegrationSettings.ReadInstances(store.Current.NinaInstancesDocument).Single().Name);
        Assert.Equal(beforeFilterIds, store.Current.AstroBinFilterIdsDocument!.Value.GetRawText());
        Assert.Equal(beforeBortle, store.Current.AstroBinBortle);
        Assert.Equal(beforeStellarium, store.Current.StellariumInstancesDocument!.Value.GetRawText());
    }

    [Fact]
    public async Task EditingAStellariumInstance_LeavesTheOtherThreeKeysUntouched()
    {
        var seed = new GeneralSettings
        {
            AstroBinFilterIdsDocument = IntegrationSettings.WriteFilterId(null, "Ha", 5),
            AstroBinBortle = 4,
            NinaInstancesDocument = IntegrationSettings.WriteInstances([new IntegrationInstance("Obsy", "http://host:1888", true)]),
            StellariumInstancesDocument = IntegrationSettings.WriteInstances([new IntegrationInstance("Desk", "http://host:8090", true)]),
        };
        var (tab, store) = await CreateAsync(seed, ["Ha"]);
        var beforeFilterIds = store.Current.AstroBinFilterIdsDocument!.Value.GetRawText();
        var beforeBortle = store.Current.AstroBinBortle;
        var beforeNina = store.Current.NinaInstancesDocument!.Value.GetRawText();

        tab.StellariumInstances.Single().Name = "Roof desk";
        await Settle(tab);

        Assert.Equal("Roof desk", IntegrationSettings.ReadInstances(store.Current.StellariumInstancesDocument).Single().Name);
        Assert.Equal(beforeFilterIds, store.Current.AstroBinFilterIdsDocument!.Value.GetRawText());
        Assert.Equal(beforeBortle, store.Current.AstroBinBortle);
        Assert.Equal(beforeNina, store.Current.NinaInstancesDocument!.Value.GetRawText());
    }

    // ---- required case 2 (Tier 1): clearing an id box removes the key -----------------------

    [Fact]
    public async Task ClearingAFilterIdBox_RemovesTheKeyRatherThanStoringZero()
    {
        var seed = new GeneralSettings { AstroBinFilterIdsDocument = IntegrationSettings.WriteFilterId(null, "Ha", 5) };
        var (tab, store) = await CreateAsync(seed, ["Ha"]);

        tab.FilterIdRows.Single().IdText = "";
        await Settle(tab);

        var ids = IntegrationSettings.ReadFilterIds(store.Current.AstroBinFilterIdsDocument);
        Assert.False(ids.ContainsKey("Ha"));
        Assert.Empty(ids);
        // The key itself is gone, not stored as 0: a stored 0 would still read as absent through
        // IntegrationSettings' own total reader (it drops non-positive values), so the raw
        // document is checked directly to catch a "store 0" regression the reader would hide.
        Assert.False(store.Current.AstroBinFilterIdsDocument!.Value.TryGetProperty("Ha", out _));
    }

    // ---- required case 3 (Tier 1): a stranded filter id keeps its key and is not listed -----

    [Fact]
    public async Task AStrandedFilterId_KeepsItsKeyAfterAnUnrelatedEditAndIsNotListed()
    {
        var seed = new GeneralSettings
        {
            AstroBinFilterIdsDocument = IntegrationSettings.WriteFilterId(
                IntegrationSettings.WriteFilterId(null, "Ha", 5), "Retired", 3),
        };
        var (tab, store) = await CreateAsync(seed, ["Ha"]);

        Assert.Single(tab.FilterIdRows);
        Assert.Equal("Ha", tab.FilterIdRows.Single().FilterName);

        // The unrelated edit is Ha's own id box, so this exercises the same CommitFilterId path
        // that "Retired" (a filter the library no longer carries, so it renders no row) must
        // survive: a rebuild-from-the-rendered-rows regression would drop it here.
        tab.FilterIdRows.Single().IdText = "9";
        await Settle(tab);

        var ids = IntegrationSettings.ReadFilterIds(store.Current.AstroBinFilterIdsDocument);
        Assert.Equal(3, ids["Retired"]);
        Assert.Equal(9, ids["Ha"]);
    }

    // ---- required case 4: a refused filter id reverts the box and stores nothing ------------

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1.5")]
    [InlineData("abc")]
    public async Task ARefusedFilterId_RevertsTheBoxAndStoresNothing(string badValue)
    {
        var seed = new GeneralSettings { AstroBinFilterIdsDocument = IntegrationSettings.WriteFilterId(null, "Ha", 5) };
        var (tab, store) = await CreateAsync(seed, ["Ha"]);
        var row = tab.FilterIdRows.Single();

        row.IdText = badValue;
        await Settle(tab);

        Assert.Equal("5", row.IdText);
        Assert.NotNull(row.Error);
        Assert.Equal(5, IntegrationSettings.ReadFilterIds(store.Current.AstroBinFilterIdsDocument)["Ha"]);
    }

    // ---- required case 5: a refused URL reverts the box and shows IntegrationMessages.BadUrl -

    [Fact]
    public async Task ARefusedUrl_RevertsTheBoxAndShowsTheOneBadUrlSentence()
    {
        var seed = new GeneralSettings
        {
            NinaInstancesDocument = IntegrationSettings.WriteInstances([new IntegrationInstance("Obsy", "http://host:1888", true)]),
        };
        var (tab, store) = await CreateAsync(seed);
        var row = tab.NinaInstances.Single();

        row.Url = "not a url";
        await Settle(tab);

        Assert.Equal("http://host:1888", row.Url);
        Assert.Equal(IntegrationMessages.BadUrl, row.UrlError);
        Assert.Equal(
            "http://host:1888",
            IntegrationSettings.ReadInstances(store.Current.NinaInstancesDocument).Single().Url);
    }

    // ---- required case 6: a blank name or URL is stored and the row carries the caption -----

    [Fact]
    public async Task ABlankNameOrUrl_IsStoredAndTheRowIsNotOffered()
    {
        var (tab, store) = await CreateAsync();

        tab.AddNinaInstanceCommand.Execute(null);
        await Settle(tab);

        var stored = IntegrationSettings.ReadInstances(store.Current.NinaInstancesDocument).Single();
        Assert.Equal("", stored.Name);
        Assert.Equal("", stored.Url);
        Assert.False(tab.NinaInstances.Single().IsOffered);
    }

    // ---- required case 7: IsOffered drives the caption, not a local predicate ---------------

    [Fact]
    public async Task ANamedEnabledInstanceWithAGoodUrl_IsOffered()
    {
        var (tab, _) = await CreateAsync();
        tab.AddNinaInstanceCommand.Execute(null);
        await Settle(tab);
        var row = tab.NinaInstances.Single();

        row.Name = "Observatory";
        await Settle(tab);
        row.Url = "http://host:1888";
        await Settle(tab);

        Assert.True(row.IsOffered);
        Assert.True(IntegrationInstance.IsOffered(new IntegrationInstance(row.Name, row.Url, row.Enabled)));
    }

    // ---- required case 8: Add appends a row; Remove removes exactly that row ---------------

    [Fact]
    public async Task AddInstance_AppendsAnEmptyEnabledRow()
    {
        var (tab, store) = await CreateAsync();

        tab.AddStellariumInstanceCommand.Execute(null);
        await Settle(tab);

        var row = tab.StellariumInstances.Single();
        Assert.Equal("", row.Name);
        Assert.Equal("", row.Url);
        Assert.True(row.Enabled);
        Assert.Single(IntegrationSettings.ReadInstances(store.Current.StellariumInstancesDocument));
    }

    [Fact]
    public async Task RemoveInstance_RemovesOnlyThatRowAndKeepsTheOthersInOrder()
    {
        var seed = new GeneralSettings
        {
            NinaInstancesDocument = IntegrationSettings.WriteInstances(
            [
                new IntegrationInstance("First", "http://host1:1888", true),
                new IntegrationInstance("Second", "http://host2:1888", true),
            ]),
        };
        var (tab, store) = await CreateAsync(seed);
        var second = tab.NinaInstances[1];

        tab.RemoveInstanceCommand.Execute(second);
        await Settle(tab);

        var remaining = IntegrationSettings.ReadInstances(store.Current.NinaInstancesDocument);
        Assert.Single(remaining);
        Assert.Equal("First", remaining[0].Name);
        Assert.Equal("First", tab.NinaInstances.Single().Name);
    }

    // ---- wave2-review P3 (:160): the 1-9 bound is pinned, not merely correct ----------------

    [Theory]
    [InlineData("0")]
    [InlineData("10")]
    [InlineData("abc")]
    public async Task ARefusedBortleValue_RevertsTheBoxAndStoresNothing(string badValue)
    {
        var seed = new GeneralSettings { AstroBinBortle = 5 };
        var (tab, store) = await CreateAsync(seed);

        tab.BortleText = badValue;
        await Settle(tab);

        Assert.Equal("5", tab.BortleText);
        Assert.Equal("Enter a whole number from 1 to 9.", tab.BortleError);
        Assert.Equal(5, store.Current.AstroBinBortle);
    }

    // ---- wave2-review P3 (:218): the refusal sentence is spelled once, in IntegrationMessages -

    [Fact]
    public void TheBadUrlSentence_IsSpelledNowhereInThisUnitsSourceExceptTheConstantReference()
    {
        var path = Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "ViewModels", "Settings", "ExternalToolsTabViewModel.cs");
        var source = SourceScan.StripComments(File.ReadAllText(path));

        Assert.DoesNotContain("\"" + IntegrationMessages.BadUrl + "\"", source, StringComparison.Ordinal);
        Assert.Contains("IntegrationMessages.BadUrl", source, StringComparison.Ordinal);
    }

    // ---- ruling B32: the filter id map's union follows a rename or a scan discovery ---------

    [Fact]
    public async Task AliasSourcesChanged_RebuildsTheFilterIdRowsFromTheCurrentUnion()
    {
        var store = new FakeStore();
        var knownFilters = new List<string> { "Ha" };
        EventHandler? handler = null;
        var tab = new ExternalToolsTabViewModel(
            store.Get,
            store.Mutate,
            () => knownFilters,
            post: action => action(),
            subscribeAliasSourcesChanged: h => handler = h,
            unsubscribeAliasSourcesChanged: _ => { });
        await tab.PendingLoad!;

        knownFilters.Add("OIII");
        handler!.Invoke(null, EventArgs.Empty);

        Assert.Equal(["Ha", "OIII"], tab.FilterIdRows.Select(row => row.FilterName));
    }

    [Fact]
    public async Task ThePostScanRefreshSignal_RebuildsTheFilterIdRowsFromTheCurrentUnion()
    {
        var store = new FakeStore();
        var knownFilters = new List<string> { "Ha" };
        EventHandler? handler = null;
        var tab = new ExternalToolsTabViewModel(
            store.Get,
            store.Mutate,
            () => knownFilters,
            post: action => action(),
            subscribeFilterUnionRefresh: h => handler = h,
            unsubscribeFilterUnionRefresh: _ => { });
        await tab.PendingLoad!;

        knownFilters.Add("OIII");
        handler!.Invoke(null, EventArgs.Empty);

        Assert.Equal(["Ha", "OIII"], tab.FilterIdRows.Select(row => row.FilterName));
    }

    // ---- required case 10: the tab is thirteenth, keyed external-tools ---------------------

    [Fact]
    public void TheTab_IsThirteenthKeyedExternalToolsBetweenCustomColumnsAndStorage()
    {
        var page = new SettingsViewModel(
            () => throw new InvalidOperationException("library"),
            () => throw new InvalidOperationException("targets"),
            () => throw new InvalidOperationException("filters"),
            () => throw new InvalidOperationException("equipment"),
            () => throw new InvalidOperationException("maintenance"),
            () => throw new InvalidOperationException("location"),
            () => throw new InvalidOperationException("display"),
            () => throw new InvalidOperationException("storage"));

        Assert.Equal(13, page.Tabs.Count);
        var keys = page.Tabs.Select(tab => tab.Key).ToList();
        var externalToolsIndex = keys.IndexOf("external-tools");
        Assert.True(externalToolsIndex > 0);
        Assert.Equal("custom-columns", keys[externalToolsIndex - 1]);
        Assert.Equal("storage", keys[externalToolsIndex + 1]);
    }
}

// ---- required case 9: the union lists canonical and ungrouped discovered names ---------------
public class FilterNameUnionTests
{
    [Fact]
    public void OnALibraryWithNoFilterGroup_TheUnionListsTheDiscoveredNamesSortedCaseInsensitively()
    {
        var editor = new GroupingEditorViewModel(showColorPicker: true);
        editor.SetDiscovered([("sii", 4), ("Ha", 10), ("OIII", 2)]);

        var union = FilterNameUnion.From(editor);

        Assert.Equal(["Ha", "OIII", "sii"], union);
    }

    [Fact]
    public void TheUnion_CombinesCanonicalGroupsAndUngroupedNames()
    {
        var editor = new GroupingEditorViewModel(showColorPicker: true);
        editor.SetGroups([new AliasGroupViewModel("Ha", null, []), new AliasGroupViewModel("OIII", null, [])]);
        editor.SetDiscovered([("Lum", 6), ("Ha", 10)]);

        var union = FilterNameUnion.From(editor);

        Assert.Equal(["Ha", "Lum", "OIII"], union);
    }
}
