using GalactiLog.App.Tests.Services;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Targets;
using GalactiLog.Data;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.Tests.TestSupport;

// Phase 5 Task 5 fix pass, review item 5 (closes FIXER LIST item 5): the one place
// GalactiLog.App.Tests builds a ScanCoordinator. Six call sites duplicated this object graph
// before this pass (MainWindowViewModelTests, ScanStatusServiceTests x2, StatusBarViewModelTests,
// ShellNavigationTests, AppShutdownTests); all six now call one of the two factories below.
// Tasks 6, 7 and 8 use this instead of hand-rolling the graph again.
internal static class ScanCoordinatorTestFactory
{
    // For tests that only drive ScanCoordinator.RaiseProgress or read IsRunning/Cancel and
    // never call RunAsync/RunTargetedAsync/settingsStore.GetGeneral(). Every constructor on
    // this path only stores its connection string -- none of them opens it -- so this never
    // touches a real database.
    public static ScanCoordinator CreateBare()
    {
        const string connectionString = "Data Source=:memory:";
        return new ScanCoordinator(
            connectionString,
            new SettingsStore(new SettingsRepository(connectionString)),
            new TargetResolver(
                connectionString,
                StaticCatalogLoader.ResolveCatalogsDirectory(),
                new CatalogCacheRepository(connectionString),
                new SimbadClient(new HttpClientHandler()),
                new SesameClient(new HttpClientHandler())),
            new ScanRunRepository(connectionString),
            NullLogger<ScanCoordinator>.Instance);
    }

    // For tests that run a real scan to completion (ScanFinished, scan_runs rows, IsRunning
    // transitions). Pass a SettingsFixture already configured with ScanRoots.
    public static ScanCoordinator Create(SettingsFixture settings) => new(
        settings.ConnectionString,
        settings.Store,
        new TargetResolver(
            settings.ConnectionString,
            StaticCatalogLoader.ResolveCatalogsDirectory(),
            new CatalogCacheRepository(settings.ConnectionString),
            new SimbadClient(new HttpClientHandler()),
            new SesameClient(new HttpClientHandler())),
        new ScanRunRepository(settings.ConnectionString),
        NullLogger<ScanCoordinator>.Instance);
}
