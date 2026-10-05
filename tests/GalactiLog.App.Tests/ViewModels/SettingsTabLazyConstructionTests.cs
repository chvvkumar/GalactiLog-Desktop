using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using Xunit;
using LibraryFactory = GalactiLog.App.Tests.TestSupport.LibraryTabViewModelTestFactory;
using TargetsFactory = GalactiLog.App.Tests.TestSupport.TargetsTabViewModelTestFactory;
using PreferenceTabViewModelTestFactory = GalactiLog.App.Tests.TestSupport.PreferenceTabViewModelTestFactory;
using DiagnosticsFactory = GalactiLog.App.Tests.TestSupport.DiagnosticsViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

// TRACKING.md section 6 item 16. SettingsViewModel used to construct all ten tabs eagerly, so
// resolving the main window cost four SQLite reads for a Targets tab nobody had opened, and
// Phase 9 adds up to nine more tabs. The mechanism is on NavigationItem, so Tasks 6, 7 and 8 each
// add one factory entry and nothing else. Phase 9 Task 7: filters and equipment are now two more
// factory entries, over minimal in-memory view-models this file otherwise has no interest in.
public class SettingsTabLazyConstructionTests
{
    private sealed class Counter
    {
        public int Library;

        public int Targets;

        public int Filters;

        public int Equipment;

        public int Maintenance;

        public int Location;

        public int Display;

        public int Storage;

        public int General;

        public int CustomColumns;

        public int ExternalTools;
    }

    private static (SettingsViewModel Page, Counter Count, LibraryFactory.Harness Library, TargetsFactory.Harness Targets) Build()
    {
        var library = LibraryFactory.Create().Settle();
        var targets = TargetsFactory.Create().Settle();
        var count = new Counter();

        var page = new SettingsViewModel(
            () =>
            {
                count.Library++;
                return library.ViewModel;
            },
            () =>
            {
                count.Targets++;
                return targets.ViewModel;
            },
            () =>
            {
                count.Filters++;
                return NewFiltersTab();
            },
            () =>
            {
                count.Equipment++;
                return NewEquipmentTab();
            },
            () =>
            {
                count.Maintenance++;
                return NewMaintenanceTab();
            },

            // Phase 9 Task 6: three more factory entries, over the shared preference-tab factory
            // for the reason the filters and equipment helpers above exist. This file asserts that
            // a tab is not constructed until it is selected, not what any tab does.
            () =>
            {
                count.Location++;
                return PreferenceTabViewModelTestFactory.NewLocationTab();
            },
            () =>
            {
                count.Display++;
                return PreferenceTabViewModelTestFactory.NewDisplayTab();
            },
            () =>
            {
                count.Storage++;
                return PreferenceTabViewModelTestFactory.NewStorageTab();
            },
            diagnostics: null,
            about: null,

            // Phase 11 Task 2: the eleventh tab is one more factory entry, and it sits at
            // Tabs[1] (ruling Q1), which is the position most likely to be built eagerly by
            // mistake because it is next to the initial selection.
            general: () =>
            {
                count.General++;
                return PreferenceTabViewModelTestFactory.NewGeneralTab();
            },

            // Phase 20 Task 4: the twelfth entry, over a minimal in-memory tab this file has no
            // further interest in, exactly the shape the ten factories above already take.
            customColumns: () =>
            {
                count.CustomColumns++;
                return NewCustomColumnsTab();
            },

            // Phase 21 Task 4: the thirteenth entry, over a minimal in-memory tab this file has
            // no further interest in, exactly the shape the twelve factories above already take.
            externalTools: () =>
            {
                count.ExternalTools++;
                return NewExternalToolsTab();
            });

        return (page, count, library, targets);
    }

    // Neither this file nor its assertions care about filters or equipment behaviour; these
    // exist only so SettingsViewModel's primary constructor, which now takes one factory per
    // real tab, has something to invoke on the rare assertion that does select one of them.
    private static FiltersTabViewModel NewFiltersTab() => new(
        () => new Dictionary<string, FilterSetting>(),
        _ => { },
        () => [],
        _ => { },
        () => [],
        post: action => action());

    private static EquipmentTabViewModel NewEquipmentTab() => new(
        () => new EquipmentSettings(),
        _ => { },
        () => [],
        _ => { },
        () => [],
        () => [],
        post: action => action());

    // Phase 9 Task 8: the Maintenance tab is a fifth factory entry, over delegates that do
    // nothing. This file asserts when a tab is built, never what it does.
    private static MaintenanceTabViewModel NewMaintenanceTab() => new(
        (_, _) => new TargetRebuild.RebuildOutcome(0, 0, 0, 0, 0, 0),
        (_, _) => new UnresolvedRetry.RetryOutcome(0, 0, 0, 0, 0, false),
        (_, _) => new SmartRebuild.SmartRebuildOutcome(0, 0, 0, 0, 0, 0, 0),
        (_, _) => new CatalogIdentityBackfill.BackfillOutcome(0, 0, 0),
        (_, _, _) => Task.FromResult<ReferenceThumbnailPass.ReferenceThumbnailOutcome?>(
            new ReferenceThumbnailPass.ReferenceThumbnailOutcome(0, 0, 0, false)),
        _ => 0,
        () => 90,
        _ => 0,
        post: action => action());

    // Phase 20 Task 4: the twelfth factory entry, over delegates that do nothing. This file
    // asserts when a tab is built, never what it does.
    private static CustomColumnsTabViewModel NewCustomColumnsTab() => new(
        () => [],
        (_, _, _, _) => CustomColumnTestFactory.Written,
        (_, _, _) => CustomColumnTestFactory.Written,
        (_, _) => CustomColumnTestFactory.Written,
        _ => CustomColumnTestFactory.Written,
        post: action => action());

    // Phase 21 Task 4: the thirteenth factory entry, over a general document with no reads beyond
    // its own construction. This file asserts when a tab is built, never what it does.
    private static ExternalToolsTabViewModel NewExternalToolsTab() => new(
        () => new GeneralSettings(),
        mutate => mutate(new GeneralSettings()),
        () => [],
        post: action => action());

    [Fact]
    public void Tabs_AreNotConstructedUntilTheyAreSelected()
    {
        var (page, count, library, targets) = Build();
        using var pageDisposal = page;
        using var libraryHarness = library;
        using var targetsHarness = targets;

        // Thirteen tabs exist as entries; none of their pages has been asked for. Assigning the
        // initial selection does not read Selected.Page.
        Assert.Equal(13, page.Tabs.Count);
        Assert.Equal(0, count.Library);
        Assert.Equal(0, count.General);
        Assert.Equal(0, count.Targets);
        Assert.Equal(0, count.CustomColumns);
        Assert.Equal(0, count.Filters);
        Assert.Equal(0, count.Equipment);
        Assert.Equal(0, count.ExternalTools);
        Assert.All(
            page.Tabs.Where(tab =>
                tab.Key is "library" or "general" or "targets" or "filters" or "equipment"
                    or "custom-columns" or "external-tools"),
            tab => Assert.False(tab.IsConstructed));
    }

    [Fact]
    public void SelectingATab_ConstructsItOnce()
    {
        var (page, count, library, targets) = Build();
        using var pageDisposal = page;
        using var libraryHarness = library;
        using var targetsHarness = targets;

        page.Selected = page.Tabs.Single(tab => tab.Key == "targets");
        var shown = page.CurrentTab;

        Assert.Same(targets.ViewModel, shown);
        Assert.Equal(1, count.Targets);
        Assert.Equal(0, count.Library);
    }

    [Fact]
    public void SelectingATabTwice_DoesNotConstructItAgain()
    {
        var (page, count, library, targets) = Build();
        using var pageDisposal = page;
        using var libraryHarness = library;
        using var targetsHarness = targets;
        var targetsTab = page.Tabs.Single(tab => tab.Key == "targets");

        page.Selected = targetsTab;
        _ = page.CurrentTab;
        page.Selected = page.Tabs.Single(tab => tab.Key == "about");
        _ = page.CurrentTab;
        page.Selected = targetsTab;
        _ = page.CurrentTab;

        Assert.Equal(1, count.Targets);
    }

    [Fact]
    public void TheFirstTabIsConstructedWhenTheSettingsPageIsShown()
    {
        var (page, count, library, targets) = Build();
        using var pageDisposal = page;
        using var libraryHarness = library;
        using var targetsHarness = targets;

        // Library is Tabs[0] and the initial selection, so the content region's first read of
        // CurrentTab builds exactly one tab.
        Assert.Equal("library", page.Selected.Key);
        var shown = page.CurrentTab;

        Assert.Same(library.ViewModel, shown);
        Assert.Equal(1, count.Library);
        Assert.Equal(0, count.Targets);

        // Ruling Q1 put General beside Library in the strip, and it still has to wait for its
        // own first visit (TRACKING item 16).
        Assert.Equal(0, count.General);
    }

    [Fact]
    public void TheGeneralTab_IsConstructedOnFirstVisitOnly()
    {
        var (page, count, library, targets) = Build();
        using var pageDisposal = page;
        using var libraryHarness = library;
        using var targetsHarness = targets;

        var general = page.Tabs.Single(tab => tab.Key == "general");
        Assert.Equal("general", page.Tabs[1].Key);

        page.Selected = general;
        _ = page.CurrentTab;
        page.Selected = page.Tabs.Single(tab => tab.Key == "library");
        _ = page.CurrentTab;
        page.Selected = general;
        _ = page.CurrentTab;

        Assert.Equal(1, count.General);
        Assert.True(general.IsConstructed);
    }

    // Phase 20 Task 4, required case 19: the same "first visit only" shape the ten tabs above
    // already take.
    [Fact]
    public void TheCustomColumnsTab_IsBuiltOnItsFirstVisitAndNotBefore()
    {
        var (page, count, library, targets) = Build();
        using var pageDisposal = page;
        using var libraryHarness = library;
        using var targetsHarness = targets;

        var customColumns = page.Tabs.Single(tab => tab.Key == "custom-columns");
        Assert.False(customColumns.IsConstructed);
        Assert.Equal(0, count.CustomColumns);

        page.Selected = customColumns;
        _ = page.CurrentTab;
        page.Selected = page.Tabs.Single(tab => tab.Key == "library");
        _ = page.CurrentTab;
        page.Selected = customColumns;
        _ = page.CurrentTab;

        Assert.Equal(1, count.CustomColumns);
        Assert.True(customColumns.IsConstructed);
    }

    // Phase 21 Task 4, required case 11: the same "first visit only" shape the twelve tabs above
    // already take.
    [Fact]
    public void TheExternalToolsTab_IsBuiltOnItsFirstVisitAndNotBefore()
    {
        var (page, count, library, targets) = Build();
        using var pageDisposal = page;
        using var libraryHarness = library;
        using var targetsHarness = targets;

        var externalTools = page.Tabs.Single(tab => tab.Key == "external-tools");
        Assert.False(externalTools.IsConstructed);
        Assert.Equal(0, count.ExternalTools);

        page.Selected = externalTools;
        _ = page.CurrentTab;
        page.Selected = page.Tabs.Single(tab => tab.Key == "library");
        _ = page.CurrentTab;
        page.Selected = externalTools;
        _ = page.CurrentTab;

        Assert.Equal(1, count.ExternalTools);
        Assert.True(externalTools.IsConstructed);
    }

    [Fact]
    public void Dispose_DisposesOnlyTheTabsThatWereConstructed()
    {
        var (page, count, library, targets) = Build();
        using var libraryHarness = library;
        using var targetsHarness = targets;

        _ = page.CurrentTab;
        page.Dispose();

        Assert.True(library.ViewModel.IsDisposed);
        Assert.Equal(0, count.Targets);
    }

    [Fact]
    public void Dispose_DoesNotConstructAnUnvisitedTab()
    {
        var (page, count, library, targets) = Build();
        using var libraryHarness = library;
        using var targetsHarness = targets;

        // The defect this design exists to avoid: walking Tabs and reading Page in Dispose built
        // every tab in order to dispose it.
        page.Dispose();

        Assert.Equal(0, count.Library);
        Assert.Equal(0, count.Targets);
        Assert.Equal(0, count.Filters);
        Assert.Equal(0, count.Equipment);
        Assert.Equal(0, count.General);
        Assert.Equal(0, count.CustomColumns);
        Assert.Equal(0, count.ExternalTools);
        // Phase 9 Tasks 6 and 8 turned location, display, storage and maintenance into lazy
        // factories too, and Phase 11 Task 2's General tab is a lazy factory here as well, so the
        // only entries still constructed are the two that are still eager placeholder pages:
        // diagnostics (Phase 10) and about.
        Assert.All(
            page.Tabs,
            tab => Assert.Equal(tab.Key is "diagnostics" or "about", tab.IsConstructed));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var (page, count, library, targets) = Build();
        using var libraryHarness = library;
        using var targetsHarness = targets;

        _ = page.CurrentTab;
        page.Dispose();
        page.Dispose();

        Assert.Equal(1, count.Library);
    }

    [Fact]
    public void AnEagerNavigationItem_StillWorks()
    {
        // The rail's own call site is unchanged: the dashboard is a singleton that must exist
        // before the window is shown, so it hands NavigationItem a constructed page.
        var dashboard = new PlaceholderPageViewModel("Dashboard", "Body");
        var item = new NavigationItem("dashboard", "Dashboard", dashboard);

        Assert.True(item.IsConstructed);
        Assert.Same(dashboard, item.Page);
        Assert.Same(dashboard, item.Page);
    }

    [Fact]
    public void AFactoryNavigationItem_MemoizesTheFirstRead()
    {
        var invocations = 0;
        var page = new PlaceholderPageViewModel("Library", "Body");
        var item = new NavigationItem("library", "Library", () =>
        {
            invocations++;
            return page;
        });

        Assert.False(item.IsConstructed);
        Assert.Equal(0, invocations);

        Assert.Same(page, item.Page);
        Assert.Same(page, item.Page);

        Assert.Equal(1, invocations);
        Assert.True(item.IsConstructed);
    }

    // ---- Phase 10 Task 1, coordinator ruling Q3 ------------------------------------------------

    [Fact]
    public async Task Dispose_DoesNotDisposeTheHostOwnedDiagnosticsTab()
    {
        var library = LibraryFactory.Create().Settle();
        var targets = TargetsFactory.Create().Settle();
        using var libraryHarness = library;
        using var targetsHarness = targets;
        var snapshots = 0;
        using var diagnostics = DiagnosticsFactory.Create(
            () => { Interlocked.Increment(ref snapshots); return DiagnosticsFactory.Snapshot(); });
        Assert.Equal(1, snapshots);

        var page = new SettingsViewModel(
            () => library.ViewModel,
            () => targets.ViewModel,
            NewFiltersTab,
            NewEquipmentTab,
            NewMaintenanceTab,
            () => PreferenceTabViewModelTestFactory.NewLocationTab(),
            () => PreferenceTabViewModelTestFactory.NewDisplayTab(),
            () => PreferenceTabViewModelTestFactory.NewStorageTab(),
            () => diagnostics);

        // Visited, so the tab is constructed and would otherwise be disposed with the page.
        page.Selected = page.Tabs.Single(tab => tab.Key == "diagnostics");
        Assert.Same(diagnostics, page.CurrentTab);

        page.Dispose();

        // One page in two places (ruling Q3): the rail still shows this instance and the DI
        // container owns its lifetime. A disposed page drops its scan subscription and its
        // Refresh command returns without reading anything, so a second snapshot is the proof it
        // is still alive.
        // Awaited, never fired and forgotten (TRACKING section 2 item 8).
        await diagnostics.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, snapshots);
        Assert.False(diagnostics.LoadFailed);
        Assert.NotEmpty(diagnostics.Groups[0].Fields);

        // And nothing else was built: the page's Dispose is what would otherwise have to walk the
        // tabs, so a tab that was never visited must still report unconstructed afterwards. The
        // about, general, custom-columns and external-tools entries are the exceptions here,
        // placeholders built eagerly because this page is built without their factories (phase
        // review minor P10: this assertion is about construction, and the comment used to claim
        // disposal; Phase 20 Task 4 added custom-columns and Phase 21 Task 4 adds external-tools
        // to the same exception for the same reason).
        Assert.All(
            page.Tabs.Where(tab => tab.Key != "diagnostics"),
            tab => Assert.Equal(
                tab.Key is "about" or "general" or "custom-columns" or "external-tools",
                tab.IsConstructed));
    }
}
