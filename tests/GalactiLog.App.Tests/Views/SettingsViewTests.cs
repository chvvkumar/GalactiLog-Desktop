using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views;
using GalactiLog.App.Views.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetsTabViewModelTestFactory;
using LibraryFactory = GalactiLog.App.Tests.TestSupport.LibraryTabViewModelTestFactory;
using DiagnosticsFactory = GalactiLog.App.Tests.TestSupport.DiagnosticsViewModelTestFactory;
using AboutFactory = GalactiLog.App.Tests.TestSupport.AboutTabViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke tests for spec 12.7's Settings page and its one real tab: they
// parse, lay out, and bind against a populated view-model. Compiled bindings already turn a
// binding-path typo into a build error; these catch the rest (a missing resource, a template that
// cannot realize, an empty state that never renders).
public class SettingsViewTests
{
    private static Window Show(Control view)
    {
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static IReadOnlyList<string> VisibleTexts(Control view)
        => [.. view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    // Phase 9 Task 7: SettingsViewModel's primary constructor now takes one factory per real
    // tab, so every existing call site in this file needs a Filters and an Equipment factory
    // too, even the tests that are not about either tab. Minimal in-memory view-models, matching
    // SettingsTabLazyConstructionTests.cs's own NewFiltersTab/NewEquipmentTab.
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

    // Phase 9 Task 8: the same shape again for the Maintenance tab, whose factory the primary
    // constructor now takes too. Delegates that do nothing, because these cases are about the
    // tab strip and the templates, not about the actions.
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

    // Phase 9 Task 6: three more factory entries, from the shared preference-tab factory, for the
    // reason the filters and equipment helpers above exist. Their behaviour is asserted in their
    // own suites; here they only have to exist so the page can be built.
    private static LocationTabViewModel NewLocationTab()
        => TestSupport.PreferenceTabViewModelTestFactory.NewLocationTab();

    private static DisplayTabViewModel NewDisplayTab()
        => TestSupport.PreferenceTabViewModelTestFactory.NewDisplayTab();

    private static StorageTabViewModel NewStorageTab()
        => TestSupport.PreferenceTabViewModelTestFactory.NewStorageTab();

    [AvaloniaFact]
    public void SettingsView_Constructs_AndLaysOut()
    {
        using var harness = Factory.Create().Settle();
        var page = new SettingsViewModel(harness.ViewModel);
        var view = new SettingsView { DataContext = page };
        Show(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);

        var strip = view.GetControl<ListBox>("SettingsTabStrip");
        Assert.Equal(page.Tabs, strip.ItemsSource);
        Assert.Same(page.Selected, strip.SelectedItem);

        // Spec 12.7's twelve tabs, in table order, with Library selected first. Ruling Q1 puts
        // General immediately after Library, and Tabs[0] stays Library. Spec amendment 2.8 and
        // user choice 18 (Phase 20 Task 4) put Custom Columns between Display and Storage.
        Assert.Equal(
            new[]
            {
                "library", "general", "filters", "equipment", "location", "display",
                "custom-columns", "external-tools", "storage", "targets", "maintenance", "diagnostics", "about",
            },
            page.Tabs.Select(tab => tab.Key));
        Assert.Equal("library", page.Selected.Key);
    }

    [AvaloniaFact]
    public void SettingsView_SelectingTheTargetsTab_ShowsTheCandidateList()
    {
        using var harness = Factory.Create([Factory.Candidate()]).Settle();
        var page = new SettingsViewModel(harness.ViewModel);
        var view = new SettingsView { DataContext = page };
        Show(view);

        page.Selected = page.Tabs.Single(tab => tab.Key == "targets");
        Dispatcher.UIThread.RunJobs();

        var region = view.GetControl<ContentControl>("SettingsTabRegion");
        Assert.Same(harness.ViewModel, region.Content);
        Assert.Contains(region.GetVisualDescendants(), visual => visual is TargetsTabView);
    }

    [AvaloniaFact]
    public void TargetsTabView_RendersEveryFieldOfACandidateRow()
    {
        using var harness = Factory.Create([Factory.Candidate()]).Settle();
        var view = new TargetsTabView { DataContext = harness.ViewModel };
        Show(view);

        var texts = VisibleTexts(view);
        Assert.Contains("NGC7331 field", texts);
        Assert.Contains("12 frames", texts);
        Assert.Contains("NGC 7331", texts);
        Assert.Contains("87%", texts);
        Assert.Contains("trigram", texts);
        Assert.Contains("Name is 87% similar to \"NGC 7331\"", texts);
        Assert.Contains("Accept", ButtonTexts(view));
        Assert.Contains("Dismiss", ButtonTexts(view));
        Assert.Contains("Edit target", ButtonTexts(view));
        Assert.DoesNotContain("No duplicate suggestions.", texts);
    }

    [AvaloniaFact]
    public void TargetsTabView_EmptyList_RendersTheSpecEmptyStateText()
    {
        using var harness = Factory.Create().Settle();
        var view = new TargetsTabView { DataContext = harness.ViewModel };
        Show(view);

        // Spec 12.10, verbatim, including the full stop.
        Assert.Contains("No duplicate suggestions.", VisibleTexts(view));
    }

    // ---- Phase 7 Task 6's two regions ----------------------------------------------------

    [AvaloniaFact]
    public void TargetsTabView_RendersTheUnresolvedNamesSection()
    {
        using var harness = Factory
            .Create(unresolvedNames: Factory.UnresolvedNames(Factory.UnresolvedName()))
            .Settle();
        var view = new TargetsTabView { DataContext = harness.ViewModel };
        Show(view);

        var region = view.GetControl<ContentControl>("UnresolvedNamesRegion");
        Assert.Same(harness.ViewModel.UnresolvedNames, region.Content);
        Assert.Contains(region.GetVisualDescendants(), visual => visual is UnresolvedNamesView);

        var texts = VisibleTexts(view);
        Assert.Contains("Unresolved names", texts);
        Assert.Contains("Zzyzx Blob 42", texts);
        Assert.Contains("12 frames", texts);
        Assert.Contains("Retry unresolved", ButtonTexts(view));
        Assert.DoesNotContain("Every OBJECT name resolved.", texts);
    }

    [AvaloniaFact]
    public void TargetsTabView_EmptyUnresolvedList_RendersTheSpecEmptyStateText()
    {
        using var harness = Factory.Create(unresolvedNames: Factory.UnresolvedNames()).Settle();
        var view = new TargetsTabView { DataContext = harness.ViewModel };
        Show(view);

        // Spec 12.10, verbatim, including the full stop.
        Assert.Contains("Every OBJECT name resolved.", VisibleTexts(view));
    }

    [AvaloniaFact]
    public void TargetsTabView_RendersTheRenameHistorySection()
    {
        using var harness = Factory
            .Create(renameHistory: Factory.RenameHistory(Factory.Rename()))
            .Settle();
        var view = new TargetsTabView { DataContext = harness.ViewModel };
        Show(view);

        var region = view.GetControl<ContentControl>("RenameHistoryRegion");
        Assert.Same(harness.ViewModel.RenameHistory, region.Content);
        Assert.Contains(region.GetVisualDescendants(), visual => visual is RenameHistoryView);

        var texts = VisibleTexts(view);
        Assert.Contains("Rename history", texts);
        Assert.Contains("M 31", texts);
        Assert.Contains("Andromeda", texts);
        Assert.DoesNotContain("No renames recorded.", texts);
    }

    // ---- Phase 9 Task 5's Library tab, and lazy tab construction ---------------------------

    [AvaloniaFact]
    public void SettingsView_SelectingTheLibraryTab_ShowsTheLibraryTabView()
    {
        using var library = LibraryFactory.Create().Settle();
        using var targets = Factory.Create().Settle();
        using var page = new SettingsViewModel(
            () => library.ViewModel, () => targets.ViewModel, NewFiltersTab, NewEquipmentTab, NewMaintenanceTab, NewLocationTab, NewDisplayTab, NewStorageTab);
        var view = new SettingsView { DataContext = page };
        Show(view);

        // Library is Tabs[0] and the initial selection, so showing the page is what selects it.
        var region = view.GetControl<ContentControl>("SettingsTabRegion");
        Assert.Same(library.ViewModel, region.Content);
        Assert.Contains(region.GetVisualDescendants(), visual => visual is LibraryTabView);
    }

    [AvaloniaFact]
    public void SettingsView_TheRemainingTabs_StillRenderTheirPlaceholders()
    {
        using var library = LibraryFactory.Create().Settle();
        using var targets = Factory.Create().Settle();
        using var page = new SettingsViewModel(
            () => library.ViewModel, () => targets.ViewModel, NewFiltersTab, NewEquipmentTab, NewMaintenanceTab, NewLocationTab, NewDisplayTab, NewStorageTab);
        var view = new SettingsView { DataContext = page };
        Show(view);

        var region = view.GetControl<ContentControl>("SettingsTabRegion");
        // Every tab is real now: filters and equipment are Phase 9 Task 7's, location, display and
        // storage are Task 6's, diagnostics is Phase 10 Task 1's and about is Task 4's, so each is
        // asserted in its own case rather than in this loop.
        //
        // Diagnostics, about and general render placeholders here only because this page is built
        // without their factories, which is the surface a Settings page with no database, no build
        // information and no settings document gets. That surface still has to render, which is
        // what this case pins.
        foreach (var key in new[] { "diagnostics", "about", "general" })
        {
            page.Selected = page.Tabs.Single(tab => tab.Key == key);
            Dispatcher.UIThread.RunJobs();

            Assert.IsType<PlaceholderPageViewModel>(region.Content);
            Assert.Contains(region.GetVisualDescendants(), visual => visual is PlaceholderPageView);
        }

        // And the one other real tab.
        page.Selected = page.Tabs.Single(tab => tab.Key == "targets");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(targets.ViewModel, region.Content);
    }

    [AvaloniaFact]
    public void SettingsView_ShowingThePage_BuildsOnlyTheFirstTab()
    {
        using var library = LibraryFactory.Create().Settle();
        using var targets = Factory.Create().Settle();
        var targetsBuilt = 0;
        using var page = new SettingsViewModel(
            () => library.ViewModel,
            () =>
            {
                targetsBuilt++;
                return targets.ViewModel;
            },
            NewFiltersTab,
            NewEquipmentTab,
            NewMaintenanceTab,
            NewLocationTab,
            NewDisplayTab,
            NewStorageTab);

        var view = new SettingsView { DataContext = page };
        Show(view);

        // TRACKING item 16: the tab strip shows eleven entries and exactly one page exists.
        Assert.Equal(0, targetsBuilt);
        Assert.True(page.Tabs.Single(tab => tab.Key == "library").IsConstructed);
        Assert.False(page.Tabs.Single(tab => tab.Key == "targets").IsConstructed);
        Assert.False(page.Tabs.Single(tab => tab.Key == "filters").IsConstructed);
        Assert.False(page.Tabs.Single(tab => tab.Key == "equipment").IsConstructed);
    }

    // ---- Phase 9 Task 7's Filters and Equipment tabs ---------------------------------------

    [AvaloniaFact]
    public void SettingsView_SelectingFiltersAndEquipment_ShowsTheRightView()
    {
        using var library = LibraryFactory.Create().Settle();
        using var targets = Factory.Create().Settle();
        var filters = NewFiltersTab();
        var equipment = NewEquipmentTab();
        using var page = new SettingsViewModel(
            () => library.ViewModel, () => targets.ViewModel, () => filters, () => equipment, NewMaintenanceTab, NewLocationTab, NewDisplayTab, NewStorageTab);
        var view = new SettingsView { DataContext = page };
        Show(view);

        var region = view.GetControl<ContentControl>("SettingsTabRegion");

        page.Selected = page.Tabs.Single(tab => tab.Key == "filters");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(filters, region.Content);
        Assert.Contains(region.GetVisualDescendants(), visual => visual is FiltersTabView);

        page.Selected = page.Tabs.Single(tab => tab.Key == "equipment");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(equipment, region.Content);
        Assert.Contains(region.GetVisualDescendants(), visual => visual is EquipmentTabView);
    }

    // ---- Phase 9 Task 8's Maintenance tab ---------------------------------------------------

    [AvaloniaFact]
    public void SettingsView_SelectingTheMaintenanceTab_ShowsTheMaintenanceTabView()
    {
        using var library = LibraryFactory.Create().Settle();
        using var targets = Factory.Create().Settle();
        using var maintenance = NewMaintenanceTab();
        using var page = new SettingsViewModel(
            () => library.ViewModel, () => targets.ViewModel, NewFiltersTab, NewEquipmentTab, () => maintenance, NewLocationTab, NewDisplayTab, NewStorageTab);
        var view = new SettingsView { DataContext = page };
        Show(view);

        var region = view.GetControl<ContentControl>("SettingsTabRegion");
        page.Selected = page.Tabs.Single(tab => tab.Key == "maintenance");
        Dispatcher.UIThread.RunJobs();

        Assert.Same(maintenance, region.Content);
        Assert.Contains(region.GetVisualDescendants(), visual => visual is MaintenanceTabView);

        // Spec 12.7's six actions, and the reset card naming what a reset keeps.
        var texts = VisibleTexts(view);
        Assert.Contains("Rebuild targets", texts);
        Assert.Contains("Reset database", texts);
        Assert.Contains(
            texts,
            text => text.Contains("settings and the shipped catalogues are kept", StringComparison.Ordinal));
        Assert.Contains(
            texts,
            text => text.Contains("the database file is not deleted", StringComparison.Ordinal));
    }

    // ---- Phase 9 Task 6's Location, Display and Storage tabs --------------------------------

    [AvaloniaFact]
    public void SettingsView_SelectingTheThreePreferenceTabs_ShowsTheRightView()
    {
        using var library = LibraryFactory.Create().Settle();
        using var targets = Factory.Create().Settle();
        using var location = NewLocationTab();
        using var display = NewDisplayTab();
        using var storage = NewStorageTab();
        using var page = new SettingsViewModel(
            () => library.ViewModel,
            () => targets.ViewModel,
            NewFiltersTab,
            NewEquipmentTab,
            NewMaintenanceTab,
            () => location,
            () => display,
            () => storage);
        var view = new SettingsView { DataContext = page };
        Show(view);

        var region = view.GetControl<ContentControl>("SettingsTabRegion");

        page.Selected = page.Tabs.Single(tab => tab.Key == "location");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(location, region.Content);
        Assert.Contains(region.GetVisualDescendants(), visual => visual is LocationTabView);

        page.Selected = page.Tabs.Single(tab => tab.Key == "display");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(display, region.Content);
        Assert.Contains(region.GetVisualDescendants(), visual => visual is DisplayTabView);

        page.Selected = page.Tabs.Single(tab => tab.Key == "storage");
        Dispatcher.UIThread.RunJobs();
        Assert.Same(storage, region.Content);
        Assert.Contains(region.GetVisualDescendants(), visual => visual is StorageTabView);
    }

    // ---- Phase 10 Task 1's Diagnostics tab -----------------------------------------------------

    [AvaloniaFact]
    public async Task SettingsView_SelectingTheDiagnosticsTab_ShowsTheDiagnosticsView()
    {
        using var library = LibraryFactory.Create().Settle();
        using var targets = Factory.Create().Settle();
        using var diagnostics = await DiagnosticsFactory.CreateAsync();
        using var page = new SettingsViewModel(
            () => library.ViewModel, () => targets.ViewModel, NewFiltersTab, NewEquipmentTab,
            NewMaintenanceTab, NewLocationTab, NewDisplayTab, NewStorageTab, () => diagnostics);
        var view = new SettingsView { DataContext = page };
        Show(view);

        var region = view.GetControl<ContentControl>("SettingsTabRegion");
        page.Selected = page.Tabs.Single(tab => tab.Key == "diagnostics");
        Dispatcher.UIThread.RunJobs();

        // The same instance the rail's Diagnostics destination shows (coordinator ruling Q3), and
        // not a placeholder.
        Assert.Same(diagnostics, region.Content);
        Assert.Contains(region.GetVisualDescendants(), visual => visual is DiagnosticsView);
        Assert.DoesNotContain(region.GetVisualDescendants(), visual => visual is PlaceholderPageView);
    }

    // ---- Phase 10 Task 4's About tab -----------------------------------------------------------

    [AvaloniaFact]
    public void SettingsView_SelectingTheAboutTab_ShowsTheAboutTabView()
    {
        using var library = LibraryFactory.Create().Settle();
        using var targets = Factory.Create().Settle();
        using var about = AboutFactory.Create();
        using var page = new SettingsViewModel(
            () => library.ViewModel, () => targets.ViewModel, NewFiltersTab, NewEquipmentTab,
            NewMaintenanceTab, NewLocationTab, NewDisplayTab, NewStorageTab, null,
            () => about.ViewModel);
        var view = new SettingsView { DataContext = page };
        Show(view);

        var region = view.GetControl<ContentControl>("SettingsTabRegion");
        page.Selected = page.Tabs.Single(tab => tab.Key == "about");
        Dispatcher.UIThread.RunJobs();

        // The last placeholder in spec 12.7's tab strip is gone.
        Assert.Same(about.ViewModel, region.Content);
        Assert.Contains(region.GetVisualDescendants(), visual => visual is AboutTabView);
        Assert.DoesNotContain(region.GetVisualDescendants(), visual => visual is PlaceholderPageView);
        Assert.Contains(AboutTabViewModel.VersionLabel, VisibleTexts(view));
    }

    [AvaloniaFact]
    public void SettingsView_TheAboutTab_IsNotBuiltUntilItIsSelected()
    {
        using var library = LibraryFactory.Create().Settle();
        using var targets = Factory.Create().Settle();
        using var about = AboutFactory.Create();
        var built = 0;
        using var page = new SettingsViewModel(
            () => library.ViewModel, () => targets.ViewModel, NewFiltersTab, NewEquipmentTab,
            NewMaintenanceTab, NewLocationTab, NewDisplayTab, NewStorageTab, null,
            () =>
            {
                built++;
                return about.ViewModel;
            });
        var view = new SettingsView { DataContext = page };
        Show(view);

        // A lazy factory entry like the other tabs (HANDOFF section 5 note 1).
        Assert.Equal(0, built);
        Assert.False(page.Tabs.Single(tab => tab.Key == "about").IsConstructed);

        page.Selected = page.Tabs.Single(tab => tab.Key == "about");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, built);
    }

    // ---- Phase 11 Task 2's General tab --------------------------------------------------------

    [AvaloniaFact]
    public async Task SettingsView_SelectingTheGeneralTab_ShowsTheGeneralTabView()
    {
        using var library = LibraryFactory.Create().Settle();
        using var targets = Factory.Create().Settle();
        using var general = await TestSupport.PreferenceTabViewModelTestFactory
            .NewGeneralTab()
            .SettleAsync();
        using var page = new SettingsViewModel(
            () => library.ViewModel, () => targets.ViewModel, NewFiltersTab, NewEquipmentTab,
            NewMaintenanceTab, NewLocationTab, NewDisplayTab, NewStorageTab, null, null,
            () => general);
        var view = new SettingsView { DataContext = page };
        Show(view);

        var region = view.GetControl<ContentControl>("SettingsTabRegion");
        page.Selected = page.Tabs.Single(tab => tab.Key == "general");
        Dispatcher.UIThread.RunJobs();

        Assert.Same(general, region.Content);
        Assert.Contains(region.GetVisualDescendants(), visual => visual is GeneralTabView);
        Assert.DoesNotContain(region.GetVisualDescendants(), visual => visual is PlaceholderPageView);

        // Ruling Q2's sentence, on the tab that is the one place spec 12.11's controls live.
        Assert.Contains(GeneralTabViewModel.CloseToTrayDescription, VisibleTexts(view));
    }

    [AvaloniaFact]
    public async Task SettingsView_TheGeneralTab_IsNotBuiltUntilItIsSelected()
    {
        using var library = LibraryFactory.Create().Settle();
        using var targets = Factory.Create().Settle();
        using var general = await TestSupport.PreferenceTabViewModelTestFactory
            .NewGeneralTab()
            .SettleAsync();
        var built = 0;
        using var page = new SettingsViewModel(
            () => library.ViewModel, () => targets.ViewModel, NewFiltersTab, NewEquipmentTab,
            NewMaintenanceTab, NewLocationTab, NewDisplayTab, NewStorageTab, null, null,
            () =>
            {
                built++;
                return general;
            });
        var view = new SettingsView { DataContext = page };
        Show(view);

        // TRACKING item 16: an eleventh tab is one lazy factory entry, not an eleventh eager read
        // at window resolution. Library is Tabs[0] and General sits beside it, so this is the one
        // case where a mis-ordered strip would show as an eager build.
        Assert.Equal(0, built);
        Assert.False(page.Tabs.Single(tab => tab.Key == "general").IsConstructed);

        page.Selected = page.Tabs.Single(tab => tab.Key == "general");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, built);
    }

    private static IReadOnlyList<string> ButtonTexts(Control view)
        => [.. view.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.IsEffectivelyVisible)
            .Select(button => button.Content as string ?? "")];
}
