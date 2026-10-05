using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.ViewModels.Diagnostics;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// Spec 12.7's Settings page: a tab strip over the thirteen tabs. Phase 7 made one real (Targets),
/// Phase 9 made the rest real one at a time, Phase 10 made the last two real (Diagnostics in
/// Task 1 and About in Task 4), Phase 11 Task 2 added the eleventh, General, Phase 20 Task 4
/// added the twelfth, Custom Columns, between Display and Storage (spec amendment 2.8, user
/// choice 18), and Phase 21 Task 4 added the thirteenth, External Tools, keyed
/// <c>external-tools</c>, between Custom Columns and Storage (spec amendment 4c). One entry each.
/// </summary>
/// <remarks>
/// Takes its tabs as factories, not as constructed view-models and not as a service provider, so
/// it builds in a unit test with no database (design-spec 18.3). It holds no settings document
/// and performs no write: each tab owns its own.
/// <para>
/// <see cref="NavigationItem"/> is reused rather than a second, near-identical tab record: it is
/// already "key, title, page view-model" and the rail proves the shape.
/// </para>
/// <para>
/// TRACKING.md section 6 item 16: a tab is constructed on its first visit, never at page
/// construction. Ten eagerly built tabs meant resolving the main window cost several SQLite
/// reads for tabs nobody had opened, and Phase 9 adds up to nine more. The mechanism is on
/// <see cref="NavigationItem"/>, so a later Settings task adds one factory line here and nothing
/// else. <see cref="Dispose"/> checks <see cref="NavigationItem.IsConstructed"/>, or disposing
/// the page would build every tab in order to dispose it.
/// </para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    // The one tab this page does not own. Null on the surfaces that keep the placeholder.
    private readonly NavigationItem? _diagnosticsTab;

    private bool _disposed;

    /// <param name="library">Spec 12.7's Library tab (Phase 9 Task 5). Invoked on first visit.
    /// </param>
    /// <param name="targets">Spec 12.7's Targets tab (Phase 7 Task 3). Invoked on first visit.
    /// In production it resolves a DI singleton, which is itself built lazily, so the tab's four
    /// SQLite reads still wait for the first visit.</param>
    /// <param name="filters">Spec 12.7's Filters tab (Phase 9 Task 7). Invoked on first visit.
    /// </param>
    /// <param name="equipment">Spec 12.7's Equipment tab (Phase 9 Task 7). Invoked on first
    /// visit.</param>
    /// <param name="maintenance">Spec 12.7's Maintenance tab (Phase 9 Task 8). Invoked on first
    /// visit, which matters here: the tab subscribes to <c>ScanStatusService</c>, so a window that
    /// is never navigated to it holds no subscription.</param>
    /// <param name="location">Spec 12.7's Location tab (Phase 9 Task 6). Invoked on first visit.
    /// </param>
    /// <param name="display">Spec 12.7's Display tab (Phase 9 Task 6). Invoked on first visit,
    /// which matters here too: the tab reads the display and graph documents as well as the
    /// general one.</param>
    /// <param name="storage">Spec 12.7's Storage tab (Phase 9 Task 6). Invoked on first visit.
    /// </param>
    /// <param name="diagnostics">Spec 12.8's Diagnostics page (Phase 10 Task 1). Invoked on first
    /// visit, and it resolves the same DI singleton the rail's Diagnostics entry resolves
    /// (coordinator ruling Q3), so both surfaces show one page. Host-owned: <see cref="Dispose"/>
    /// skips it. Optional, so a test surface with no database keeps the placeholder.</param>
    /// <param name="about">Spec 12.7's About tab (Phase 10 Task 4). Invoked on first visit.
    /// Optional, so a test surface with no build information keeps the placeholder.</param>
    /// <param name="general">Spec 12.7's General tab (Phase 11 Task 2), which carries spec
    /// 12.11's five residency controls. Invoked on first visit. Optional and trailing, like the
    /// two tabs before it, so a test surface with no settings document keeps the placeholder;
    /// ruling Q1 puts it immediately after Library in the strip.</param>
    /// <param name="customColumns">Spec 12.15's Custom Columns tab (Phase 20 Task 4). Invoked on
    /// first visit. Optional and trailing, like the three tabs before it, so a test surface with
    /// no database keeps the placeholder; spec amendment 2.8 and user choice 18 put it between
    /// Display and Storage in the strip.</param>
    /// <param name="externalTools">Spec amendment 4c's External Tools tab (Phase 21 Task 4).
    /// Invoked on first visit. Optional and trailing, like the four tabs before it, so a test
    /// surface with no database keeps the placeholder; the tab is keyed <c>external-tools</c>,
    /// the thirteenth, placed after Custom Columns and before Storage.</param>
    public SettingsViewModel(
        Func<LibraryTabViewModel> library,
        Func<TargetsTabViewModel> targets,
        Func<FiltersTabViewModel> filters,
        Func<EquipmentTabViewModel> equipment,
        Func<MaintenanceTabViewModel> maintenance,
        Func<LocationTabViewModel> location,
        Func<DisplayTabViewModel> display,
        Func<StorageTabViewModel> storage,
        Func<DiagnosticsViewModel>? diagnostics = null,
        Func<AboutTabViewModel>? about = null,
        Func<GeneralTabViewModel>? general = null,
        Func<CustomColumnsTabViewModel>? customColumns = null,
        Func<ExternalToolsTabViewModel>? externalTools = null)
        : this(new NavigationItem("library", "Library", () => library()),
               new NavigationItem("targets", "Targets", () => targets()),
               new NavigationItem("filters", "Filters", () => filters()),
               new NavigationItem("equipment", "Equipment", () => equipment()),
               new NavigationItem("maintenance", "Maintenance", () => maintenance()),
               new NavigationItem("location", "Location", () => location()),
               new NavigationItem("display", "Display", () => display()),
               new NavigationItem("storage", "Storage", () => storage()),
               diagnostics is null
                   ? null
                   : new NavigationItem("diagnostics", "Diagnostics", () => diagnostics()),
               about is null
                   ? null
                   : new NavigationItem("about", "About", () => about()),
               general is null
                   ? null
                   : new NavigationItem("general", "General", () => general()),
               customColumns is null
                   ? null
                   : new NavigationItem("custom-columns", "Custom Columns", () => customColumns()),
               externalTools is null
                   ? null
                   : new NavigationItem("external-tools", "External Tools", () => externalTools()))
    {
    }

    /// <summary>
    /// The page with no Library tab: the Library entry stays a placeholder. Used by the shell
    /// tests and the Targets tab's own view tests, which need the Settings destination to be the
    /// real page and have no Library view-model to give it.
    /// </summary>
    /// <remarks>Internal since Phase 9 Task 6, per the Task 5 review's ruling: it exists for the
    /// test factories in <c>GalactiLog.App.Tests</c> and nothing in the application uses it, so
    /// the application has exactly one way to build this page.</remarks>
    /// <param name="targets">Spec 12.7's Targets tab, already constructed.</param>
    internal SettingsViewModel(TargetsTabViewModel targets)
        : this(new NavigationItem("library", "Library", new PlaceholderPageViewModel(
                   "Library", "The Library settings tab is not available on this surface.")),
               new NavigationItem("targets", "Targets", targets),
               new NavigationItem("filters", "Filters", new PlaceholderPageViewModel(
                   "Filters", "The Filters settings tab is not available on this surface.")),
               new NavigationItem("equipment", "Equipment", new PlaceholderPageViewModel(
                   "Equipment", "The Equipment settings tab is not available on this surface.")),
               new NavigationItem("maintenance", "Maintenance", new PlaceholderPageViewModel(
                   "Maintenance", "The Maintenance settings tab is not available on this surface.")),
               new NavigationItem("location", "Location", new PlaceholderPageViewModel(
                   "Location", "The Location settings tab is not available on this surface.")),
               new NavigationItem("display", "Display", new PlaceholderPageViewModel(
                   "Display", "The Display settings tab is not available on this surface.")),
               new NavigationItem("storage", "Storage", new PlaceholderPageViewModel(
                   "Storage", "The Storage settings tab is not available on this surface.")),
               diagnostics: null,
               about: null,
               general: null,
               customColumns: null,
               externalTools: null)
    {
    }

    private SettingsViewModel(
        NavigationItem library,
        NavigationItem targets,
        NavigationItem filters,
        NavigationItem equipment,
        NavigationItem maintenance,
        NavigationItem location,
        NavigationItem display,
        NavigationItem storage,
        NavigationItem? diagnostics = null,
        NavigationItem? about = null,
        NavigationItem? general = null,
        NavigationItem? customColumns = null,
        NavigationItem? externalTools = null)
    {
        // Spec 12.8's Diagnostics page is one page in two places (coordinator ruling Q3): this
        // entry and the rail's resolve the same DI singleton through the same lazy factory. It is
        // therefore host-owned, and Dispose skips it by reference below.
        _diagnosticsTab = diagnostics;

        Tabs =
        [
            library,

            // Ruling Q1: the General tab sits immediately after Library, and Tabs[0] stays
            // Library, so the page's initial selection is unchanged.
            general ?? new NavigationItem("general", "General", new PlaceholderPageViewModel(
                "General", "The General settings tab is not available on this surface.")),
            filters,
            equipment,
            location,
            display,

            // Spec amendment 2.8, user choice 18: "so the pages that decide what a table shows
            // sit together." Between Display and Storage; the first hyphenated tab key, because
            // "customcolumns" is unreadable and the key is never persisted anywhere.
            customColumns ?? new NavigationItem("custom-columns", "Custom Columns", new PlaceholderPageViewModel(
                "Custom Columns", "The Custom Columns settings tab is not available on this surface.")),

            // Spec amendment 4c: the thirteenth entry, after Custom Columns and before Storage.
            externalTools ?? new NavigationItem("external-tools", "External Tools", new PlaceholderPageViewModel(
                "External Tools", "The External Tools settings tab is not available on this surface.")),
            storage,
            targets,
            maintenance,
            diagnostics ?? new NavigationItem("diagnostics", "Diagnostics", new PlaceholderPageViewModel(
                "Diagnostics", "The Diagnostics settings tab is not available on this surface.")),
            about ?? new NavigationItem("about", "About", new PlaceholderPageViewModel(
                "About", "The About settings tab is not available on this surface.")),
        ];

        // Assigned to the generated property before anything can observe it, like
        // MainWindowViewModel's own first selection. Spec 12.7's table order puts Library first.
        // This does not read Selected.Page, so it constructs nothing: the first tab is built when
        // the content region first binds CurrentTab.
        Selected = Tabs[0];
    }

    /// <summary>The tab strip, in spec 12.7's table order.</summary>
    public IReadOnlyList<NavigationItem> Tabs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTab))]
    public partial NavigationItem Selected { get; set; }

    /// <summary>What the tab content region shows. Reading it is what builds a lazy tab, which
    /// is why the first read happens on the UI thread when the region binds.</summary>
    public object CurrentTab => Selected.Page;

    /// <summary>
    /// The page is a DI singleton, like <c>DashboardViewModel</c>, so the host owns its lifetime.
    /// Disposing it disposes each tab that was actually visited and owns background work or a
    /// subscription. An unvisited tab is not built here: building nine view-models in order to
    /// dispose them is the defect the lazy factory exists to avoid.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var tab in Tabs)
        {
            // The Diagnostics tab is the rail's Diagnostics page (ruling Q3): one DI singleton
            // reached from two places, whose lifetime the host container owns. Disposing it here
            // would tear down a page the rail still shows. Reference equality against the entry
            // this page was given, so nothing depends on a key string.
            if (ReferenceEquals(tab, _diagnosticsTab))
            {
                continue;
            }

            if (tab.IsConstructed && tab.Page is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
