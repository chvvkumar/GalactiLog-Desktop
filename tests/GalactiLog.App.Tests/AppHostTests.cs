using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Activity;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.ViewModels.Diagnostics;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.App.ViewModels.Merge;
using GalactiLog.App.ViewModels.Preview;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Integrations;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Survey;
using GalactiLog.Core.Targets;
using GalactiLog.Data;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GalactiLog.App.Tests;

// AppHost.Build sets the process-wide static Serilog.Log.Logger and the fixture below clears
// the process-wide SQLite connection pool, so running these concurrently with any other test
// class would race. xUnit runs test methods within one class sequentially by default, and
// AssemblyInfo.cs disables cross-collection parallelization for the whole assembly (phase 4
// review item 7), so nothing else in this project runs alongside these.
// Each test uses its own temp directory as appDataRootOverride via AppHostFixture, which
// disposes the IHost, flushes Serilog, clears the SQLite connection pool (otherwise SQLite
// keeps the file open and the directory delete below fails or silently no-ops), then
// deletes the override directory tree so no temp state survives the test run.
public sealed class AppHostTests
{
    private sealed class AppHostFixture : IDisposable
    {
        public string Root { get; }
        public IHost Host { get; }

        // `root` lets a test build a SECOND host over a root an earlier fixture already
        // created, which is what "the next application start" means for the interrupted-run
        // reconciliation test. Such a fixture does not own the directory, so it does not
        // delete it.
        private readonly bool _ownsRoot;

        public AppHostFixture(bool cliMode = false, string? root = null)
        {
            _ownsRoot = root is null;
            Root = root ?? Path.Combine(Path.GetTempPath(), "GalactiLogAppHostTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);

            // Serilog's Log.Logger is process-global and Build() overwrites it, so a previous
            // fixture's file sink would otherwise keep its galactilog-<date>.log open with no
            // reference left that Dispose can reach. Closing it here is what lets a second host
            // over the SAME root (the "next application start" cases below) delete that root.
            // Not a production path: the application builds exactly one host per process.
            Serilog.Log.CloseAndFlush();
            Host = AppHost.Build(Root, cliMode);
        }

        // Every page-level view-model that reads SQLite on construction is joined here before the
        // host goes away, because Dispose deletes the database directory underneath whatever is
        // still in flight. One line per list: Phase 7 Task 5 added the Targets tab's candidate
        // list and its merge history; Task 6 adds the unresolved-name and rename-history lists.
        // Resolving a tab that no test built is cheap and still correct: the read it starts is
        // joined on the next line.
        private void Quiesce()
        {
            var targets = Host.Services.GetRequiredService<TargetsTabViewModel>();
            targets.PendingLoad?.Wait(QuiesceBudget);
            targets.MergeHistory?.PendingLoad?.Wait(QuiesceBudget);
            targets.UnresolvedNames?.PendingLoad?.Wait(QuiesceBudget);
            targets.RenameHistory?.PendingLoad?.Wait(QuiesceBudget);
        }

        private static readonly TimeSpan QuiesceBudget = TimeSpan.FromSeconds(30);

        public void Dispose()
        {
            Quiesce();
            Host.Dispose();
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();
            if (_ownsRoot && Directory.Exists(Root))
            {
                // Swallowed, which is Phd2ReRunHostWiringTests' own idiom three times over: a
                // `using var fixture` whose BODY throws and whose delete then throws reports only
                // the delete, because the finally block's exception replaces the one in flight. A
                // real assertion failure in any of the cases in this file would surface as "the
                // process cannot access galactilog.db" and name nothing. A leaked temp directory is
                // not worth a test failure and is certainly not worth hiding one.
                try
                {
                    Directory.Delete(Root, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    [Fact]
    public void Build_ResolvesAppWriter()
    {
        using var fixture = new AppHostFixture();

        var appWriter = fixture.Host.Services.GetRequiredService<AppWriter>();

        Assert.Equal(Path.GetFullPath(fixture.Root), appWriter.AppDataRoot);
    }

    // Phase 18 final review: every delegate of the Mosaics backend is bound by AppHost, so a member
    // added without a binding fails here instead of answering with its inert test default.
    [Fact]
    public void Build_BindsEveryMosaicsBackendDelegate()
    {
        using var fixture = new AppHostFixture();

        var bound = fixture.Host.Services.GetRequiredService<MosaicsBackend>();
        var defaults = new MosaicsBackend();

        var members = typeof(MosaicsBackend).GetProperties()
            .Where(property => typeof(Delegate).IsAssignableFrom(property.PropertyType))
            .ToList();
        Assert.NotEmpty(members);
        Assert.All(members, property =>
        {
            var value = property.GetValue(bound);
            Assert.True(value is not null && !Equals(value, property.GetValue(defaults)), $"MosaicsBackend.{property.Name} is not bound");
        });
    }

    [Fact]
    public void Build_ResolvesSettingsStore()
    {
        using var fixture = new AppHostFixture();

        var settingsStore = fixture.Host.Services.GetRequiredService<SettingsStore>();
        var general = settingsStore.GetGeneral();

        Assert.Equal("Information", general.LogLevel);
        Assert.Equal(240, general.AutoScanIntervalMinutes);
        Assert.Equal(50, general.DefaultPageSize);
    }

    [Fact]
    public void Build_ResolvesLoggingLevelSwitch()
    {
        using var fixture = new AppHostFixture();

        var levelSwitch = fixture.Host.Services.GetRequiredService<LoggingLevelSwitch>();

        Assert.Equal(LogEventLevel.Information, levelSwitch.MinimumLevel);
    }

    [Fact]
    public void Build_ResolvesLogRingBuffer()
    {
        using var fixture = new AppHostFixture();

        var ringBuffer = fixture.Host.Services.GetRequiredService<LogRingBuffer>();

        Assert.NotNull(ringBuffer);
    }

    // FIXER LIST F11. This test used to emit its own Serilog event before looking for the file,
    // which masked the real defect: Build itself logged nothing unconditionally, the rolling file
    // sink creates logs\ lazily on its first event, and a GUI start over a fresh root therefore
    // left no log directory at all. Nothing here writes to the log: the file must exist, and be
    // non-empty, purely because Build started.
    [Fact]
    public void Build_WritesAStartupLineToTheLogFileUnderAppData()
    {
        using var fixture = new AppHostFixture();

        Serilog.Log.CloseAndFlush();

        var logsDir = Path.Combine(Path.GetFullPath(fixture.Root), "logs");
        Assert.True(Directory.Exists(logsDir), $"No logs directory under {fixture.Root}");
        var matches = Directory.GetFiles(logsDir, "galactilog-*.log");

        Assert.NotEmpty(matches);
        Assert.All(matches, path => Assert.True(new FileInfo(path).Length > 0, $"{path} is empty"));
    }

    [Fact]
    public void Build_ChangingLogLevel_UpdatesLevelSwitchWithoutRestart()
    {
        using var fixture = new AppHostFixture();

        var levelSwitch = fixture.Host.Services.GetRequiredService<LoggingLevelSwitch>();
        var settingsStore = fixture.Host.Services.GetRequiredService<SettingsStore>();
        var current = settingsStore.GetGeneral();

        settingsStore.SaveGeneral(current with { LogLevel = "Debug" });

        Assert.Equal(LogEventLevel.Debug, levelSwitch.MinimumLevel);
    }

    // FIXER LIST F5. The rig baselines are grouped by canonical telescope, camera and filter, so
    // an alias edit regroups every one of them without a frame changing. Resolving
    // ScanStatusService is what runs the factory that wires both invalidations, exactly as the
    // shell does at startup.
    [Fact]
    public void Build_AliasSourcesChanged_InvalidatesTheRigBaselines()
    {
        using var fixture = new AppHostFixture();

        fixture.Host.Services.GetRequiredService<ScanStatusService>();
        var baselines = fixture.Host.Services.GetRequiredService<RigBaselinesCache>();
        var settingsStore = fixture.Host.Services.GetRequiredService<SettingsStore>();

        var before = baselines.Current;
        Assert.Same(before, baselines.Current);

        settingsStore.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Aliases = ["H-alpha"] },
        });

        // A rebuilt value, not the five minute TTL's cached one.
        Assert.NotSame(before, baselines.Current);
    }

    // FIXER LIST F22. A rebuild, a retry, a smart rebuild and a catalog identity backfill all
    // rewrite the frame-to-target mapping, so every
    // figure StatsCache memoizes and every rig baseline is stale afterwards. StatsCache has no TTL
    // by design (questions.md Q7), so before this the Statistics page kept its pre-rebuild target
    // counts, top targets and equipment inventory until a scan completed or a settings document
    // was saved. The reset binding already did both lines; these two are the ones that did not.
    [Theory]
    [InlineData(MaintenanceTabViewModel.RebuildTargetsAction)]
    [InlineData(MaintenanceTabViewModel.RetryUnresolvedAction)]
    [InlineData(MaintenanceTabViewModel.SmartRebuildAction)]
    [InlineData(MaintenanceTabViewModel.CatalogIdentityBackfillAction)]
    public async Task Build_AMaintenanceActionThatRewritesTheCatalogue_InvalidatesBothDerivedCaches(string token)
    {
        using var fixture = new AppHostFixture();

        var tab = fixture.Host.Services.GetRequiredService<MaintenanceTabViewModel>();
        var stats = fixture.Host.Services.GetRequiredService<StatsCache>();
        var baselines = fixture.Host.Services.GetRequiredService<RigBaselinesCache>();

        // Both memos are warm, and both would be handed back unchanged for the life of the
        // process without an explicit invalidation.
        var statsBefore = stats.Current;
        var baselinesBefore = baselines.Current;
        Assert.Same(statsBefore, stats.Current);
        Assert.Same(baselinesBefore, baselines.Current);

        var action = tab.Action(token);
        var button = action.Buttons[0];

        // Rebuild targets carries the web's inline two-click confirm, so the first press only arms
        // the card. Pressing until the card is no longer armed runs whichever action this is.
        for (var press = 0; press < 2; press++)
        {
            button.Command.Execute(null);
            if (button.Command.ExecutionTask is { } run)
            {
                await run;
            }

            if (!action.ConfirmPending)
            {
                break;
            }
        }

        Assert.False(action.ConfirmPending);

        Assert.NotSame(statsBefore, stats.Current);
        Assert.NotSame(baselinesBefore, baselines.Current);
    }

    [Fact]
    public void Build_ThumbnailCacheRoot_ResolvesUnderOverrideRoot_NotRealProfile()
    {
        using var fixture = new AppHostFixture();

        var appWriter = fixture.Host.Services.GetRequiredService<AppWriter>();
        var expected = Path.Combine(Path.GetFullPath(fixture.Root), "thumbnails");

        Assert.Equal(expected, appWriter.ThumbnailCacheRoot);
        Assert.True(Directory.Exists(expected));
    }

    [Fact]
    public void Build_GeneralChanged_ReAuthorizesThumbnailCacheRoot_WithoutRestart()
    {
        using var fixture = new AppHostFixture();

        var appWriter = fixture.Host.Services.GetRequiredService<AppWriter>();
        var settingsStore = fixture.Host.Services.GetRequiredService<SettingsStore>();
        var newRoot = Path.Combine(fixture.Root, "relocated-thumbnails");

        settingsStore.SaveGeneral(settingsStore.GetGeneral() with { ThumbnailCacheDir = newRoot });

        Assert.Equal(Path.GetFullPath(newRoot), appWriter.ThumbnailCacheRoot);
    }

    [Fact]
    public void Build_CliMode_RoutesLogsToStderr_NotWhenCliModeFalse()
    {
        var originalError = Console.Error;
        try
        {
            var cliWriter = new StringWriter();
            Console.SetError(cliWriter);
            using (new AppHostFixture(cliMode: true))
            {
                Serilog.Log.Information("cli-mode-marker");
            }
            Assert.Contains("cli-mode-marker", cliWriter.ToString());

            var guiWriter = new StringWriter();
            Console.SetError(guiWriter);
            using (new AppHostFixture(cliMode: false))
            {
                Serilog.Log.Information("gui-mode-marker");
            }
            Assert.DoesNotContain("gui-mode-marker", guiWriter.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    // Program.Main's GUI branch cannot be driven headlessly (it calls
    // StartWithClassicDesktopLifetime, which blocks until the app exits), so this asserts
    // the round trip Program.Main performs instead: AppHost.Build succeeds, then assigning
    // its provider to App.Services makes it observable.
    [Fact]
    public void AppServices_AfterBuildAndAssignment_RoundTrips()
    {
        using var fixture = new AppHostFixture();

        App.Services = fixture.Host.Services;

        Assert.Same(fixture.Host.Services, App.Services);
    }

    [Fact]
    public void Build_MigratesDatabase()
    {
        using var fixture = new AppHostFixture();

        var connectionString = DatabasePaths.BuildConnectionString(
            Path.Combine(Path.GetFullPath(fixture.Root), DatabasePaths.DatabaseFileName));

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'images';";
        var result = command.ExecuteScalar();

        Assert.Equal("images", result);
    }

    // Phase 3 Task 8: the resolution stack (Tasks 5-7) is registered in AppHost.Build so both
    // the GUI and the CLI's resolve/scan verbs can resolve TargetResolver from the same host.
    [Fact]
    public void Build_ResolvesCatalogCacheRepository()
    {
        using var fixture = new AppHostFixture();

        Assert.NotNull(fixture.Host.Services.GetRequiredService<CatalogCacheRepository>());
    }

    [Fact]
    public void Build_ResolvesSimbadAndSesameClients()
    {
        using var fixture = new AppHostFixture();

        Assert.NotNull(fixture.Host.Services.GetRequiredService<SimbadClient>());
        Assert.NotNull(fixture.Host.Services.GetRequiredService<SesameClient>());
    }

    [Fact]
    public void Build_ResolvesTargetResolver()
    {
        using var fixture = new AppHostFixture();

        Assert.NotNull(fixture.Host.Services.GetRequiredService<TargetResolver>());
    }

    // FIXER LIST 5: the connection string is a registered value, so nothing has to re-derive
    // AppHost's expression from the AppWriter.
    [Fact]
    public void Build_RegistersTheDatabaseConnectionString()
    {
        using var fixture = new AppHostFixture();

        var registered = fixture.Host.Services.GetRequiredService<DatabaseConnectionString>();

        Assert.Equal(
            DatabasePaths.BuildConnectionString(
                Path.Combine(Path.GetFullPath(fixture.Root), DatabasePaths.DatabaseFileName)),
            registered.Value);
    }

    // Phase 5 Task 4. DashboardViewModel is a singleton per coordinator ruling Q4: design-spec
    // 12.2's "persist for the session" is the process lifetime on a desktop application, so
    // navigating away from the dashboard and back must hand back the same instance.
    [Fact]
    public void Build_ResolvesTheShellViewModels_AndTheDashboardIsOneInstance()
    {
        using var fixture = new AppHostFixture();

        var shell = fixture.Host.Services.GetRequiredService<MainWindowViewModel>();
        Assert.NotNull(shell);

        // Phase 9 Task 4 review finding 14. Task 4's `activity:` argument is an OPTIONAL trailing
        // parameter (deviation D2), so dropping it from the AppHost call site is not a compile
        // error the way dropping Task 3's positional `statistics` one would be: it silently
        // restores the Phase 5 placeholder. This is the assertion that fails if it is ever
        // dropped. Both real pages are checked, so the same gap cannot open on either.
        Assert.IsType<StatisticsViewModel>(shell.Items[2].Page);

        // Phase 17 Task 4, the same shape one phase later: `analysis:` is a required positional
        // argument, so dropping it from the AppHost call site is a compile error, but pointing it
        // at the wrong registration is not. This is the assertion that fails if it ever is.
        Assert.IsType<AnalysisViewModel>(shell.Items[3].Page);
        Assert.IsType<ActivityViewModel>(shell.Items[4].Page);

        // Phase 10 Task 1 review finding I2, the same shape one phase later: both of Task 1's
        // `diagnostics:` arguments are OPTIONAL, so dropping either from the AppHost call site
        // compiles and silently restores a placeholder. These are the assertions that fail.
        Assert.IsType<DiagnosticsViewModel>(shell.Items[5].Page);

        // And coordinator ruling Q3's core guarantee, asserted against the real container rather
        // than against a hand-built page: the rail destination and the Settings Diagnostics tab
        // are ONE instance, so there is one refresh, one log viewer and one export button.
        var settingsPage = fixture.Host.Services.GetRequiredService<SettingsViewModel>();
        settingsPage.Selected = settingsPage.Tabs.Single(tab => tab.Key == "diagnostics");
        Assert.Same(shell.Items[5].Page, settingsPage.CurrentTab);

        // That page's first refresh holds a SQLite connection to this fixture's temp database,
        // the same reason the activity page and the dashboard are quiesced below.
        var diagnostics = fixture.Host.Services.GetRequiredService<DiagnosticsViewModel>();
        Assert.Same(shell.Items[5].Page, diagnostics);

        // Phase review Important P1, the observable half: the log viewer spec 12.8 puts on this
        // page is an OPTIONAL constructor argument, so dropping it from the AppHost call site
        // compiles and leaves the page rendering its seven groups with no viewer under them.
        Assert.NotNull(diagnostics.LogViewer);
        // And the export button is not merely enabled: CanExport() tests only IsExporting and
        // disposal, so an unbound exportBundle: seam leaves the button pressable and silent. The
        // structural half is AppHost_BindsEveryOptionalPhase10Seam below.
        Assert.True(diagnostics.ExportBundleCommand.CanExecute(null));
        // Joined through the page's own harness helper, which is bounded and swallowing like the
        // dashboard's Quiesce below: a harness join must not turn a background outcome into this
        // test's failure.
        DiagnosticsViewModelTestFactory.Settle(diagnostics);

        // The Analysis page's filter bar reads its two option lists off the UI thread the moment
        // the page is built, and this fixture deletes its app data root on dispose, so that read is
        // joined here the way the dashboard's and the activity page's are.
        AnalysisViewModelTestFactory.Settle((AnalysisViewModel)shell.Items[3].Page);

        // Phase 18 Task 4: `mosaics:` is an OPTIONAL trailing argument, so dropping it from the
        // AppHost call site compiles and restores the placeholder. Its first load reads this
        // fixture's database off the UI thread, so it is joined here like the Analysis page's.
        var mosaics = Assert.IsType<MosaicsPageViewModel>(shell.Items[1].Page);
        Assert.True(MosaicsPageSettle.Settle(mosaics), "The Mosaics page's first load did not finish.");

        var dashboard = fixture.Host.Services.GetRequiredService<DashboardViewModel>();
        Assert.Same(dashboard, fixture.Host.Services.GetRequiredService<DashboardViewModel>());

        // The activity page's own first load holds a SQLite connection to this fixture's temp
        // database, the same reason the dashboard is quiesced below.
        fixture.Host.Services.GetRequiredService<ActivityViewModel>().Quiesce(TimeSpan.FromSeconds(30));

        // Phase 5 Task 6 fix pass, widened by F6: the dashboard's option-list reload, first
        // listing query and scan-root probe all run on background tasks that still hold SQLite
        // connections to this fixture's temp database when the test body ends. Joining all of
        // them is what lets Dispose delete the directory.
        dashboard.Quiesce(TimeSpan.FromSeconds(30));
    }

    // Phase 6 Task 3. The detail route end to end through the real DI graph: the write
    // repository, the shell integration and the page factory all resolve, and the factory builds
    // a page whose own load runs TargetDetailQuery against the migrated temp database without
    // faulting.
    //
    // What the page then shows is not asserted here: this class has no Avalonia application, so
    // the production post seam (Dispatcher.UIThread.Post) has nothing pumping it and the publish
    // never reaches the bindings. The published state is TargetDetailViewModelTests' subject,
    // with the post seam faked; what is only assertable here is that the real registrations
    // exist and the real query runs.
    [Fact]
    public async Task Build_ResolvesTheTargetDetailRoute()
    {
        using var fixture = new AppHostFixture();

        Assert.NotNull(fixture.Host.Services.GetRequiredService<TargetWriteRepository>());
        Assert.NotNull(fixture.Host.Services.GetRequiredService<ShellIntegration>());

        var openDetail = fixture.Host.Services
            .GetRequiredService<Func<string, DateOnly?, TargetDetailViewModel>>();
        using var page = openDetail("obj:M 31", null);

        Assert.Equal("obj:M 31", page.GroupKey);

        // Phase 6 Task 8: the page's cross-session chart and the card's per-session chart factory
        // both resolve, and both took the one shared selection singleton (spec 5.8.3).
        var selection = fixture.Host.Services.GetRequiredService<ChartSelectionViewModel>();
        Assert.Same(selection, page.TargetChart.Selection);

        var createChart = fixture.Host.Services
            .GetRequiredService<Func<GalactiLog.Data.Queries.SessionDetail, SessionChartViewModel>>();
        using var sessionChart = createChart(
            TestSupport.SessionCardViewModelTestFactory.PopulatedDetail());
        Assert.Same(selection, sessionChart.Selection);

        var load = page.PendingLoad;
        Assert.NotNull(load);
        await load.WaitAsync(TimeSpan.FromSeconds(30));

        var dashboard = fixture.Host.Services.GetRequiredService<DashboardViewModel>();
        dashboard.Quiesce(TimeSpan.FromSeconds(30));
    }

    // Phase review item 4: AliasMapCache subscribes to SettingsStore.AliasSourcesChanged, and the
    // DI container disposes only the singletons it constructed. Registered as an instance, its
    // Dispose was dead code.
    [Fact]
    public void Build_DisposesTheAliasMapCacheWithTheHost()
    {
        var root = Path.Combine(Path.GetTempPath(), "GalactiLogAppHostTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AliasMapCache cache;
        try
        {
            using (var fixture = new AppHostFixture(root: root))
            {
                cache = fixture.Host.Services.GetRequiredService<AliasMapCache>();
                Assert.False(cache.IsDisposed);
            }

            Assert.True(cache.IsDisposed);
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // Review item 3: a crash, a kill, or a drain that ran out of budget leaves scan_runs rows
    // at "running" and nothing else ever closes them. Build reconciles them, for the GUI and
    // the CLI alike, before anything can start a new scan.
    [Fact]
    public void Build_MarksRunsLeftRunningByAPreviousProcessAsInterrupted()
    {
        using var fixture = new AppHostFixture();
        var connectionString = DatabasePaths.BuildConnectionString(
            Path.Combine(Path.GetFullPath(fixture.Root), DatabasePaths.DatabaseFileName));

        // The row a killed process would have left behind.
        var abandoned = new ScanRunRepository(connectionString).Start("watcher");

        // A second Build over the same root is the next application start.
        using (var restart = new AppHostFixture(root: fixture.Root))
        {
            var run = new ScanRunRepository(connectionString).Get(abandoned);

            Assert.NotNull(run);
            Assert.Equal("failed", run!.State);
            Assert.Equal("interrupted", run.ErrorText);
            Assert.NotNull(run.FinishedAt);
        }
    }

    // design-spec 4.3, 17.2; coordinator ruling Q10: CatalogSeeder.LoadIfNeeded runs
    // unconditionally inside Build(), so a fresh override root has the bundled catalogs
    // loaded before Build() even returns -- no separate first-run wizard step is needed yet.
    [Fact]
    public void Build_SeedsCatalogsOnFreshOverrideRoot()
    {
        using var fixture = new AppHostFixture();

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(
            DatabasePaths.BuildConnectionString(Path.Combine(Path.GetFullPath(fixture.Root), DatabasePaths.DatabaseFileName))));

        Assert.True(context.OpenNgcCatalogEntries.Any());
        Assert.True(context.StaticCatalogEntries.Any());
    }

    // Phase 4 Task 1, design-spec 17.2: GALACTILOG_APPDATA lets CLI verification and CI
    // point the whole host at a fresh directory with no code change. Uses AppHost.Build()
    // directly (not AppHostFixture, which always passes an explicit override) so the env
    // var is actually exercised.
    [Fact]
    public void Build_GalactilogAppdataEnvVar_OverridesDefaultRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "GalactiLogAppHostEnvVarTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable("GALACTILOG_APPDATA");
        try
        {
            Environment.SetEnvironmentVariable("GALACTILOG_APPDATA", root);

            using var host = AppHost.Build();
            var appWriter = host.Services.GetRequiredService<AppWriter>();

            Assert.Equal(Path.GetFullPath(root), appWriter.AppDataRoot);
            // Phase 10 Task 9: an environment-supplied root reads no pointer and moves nothing,
            // which is what keeps this case off a real library.
            Assert.Equal(
                AppDataRootSource.EnvironmentVariable,
                host.Services.GetRequiredService<AppDataRootResolution>().Source);
            Assert.False(host.Services.GetRequiredService<RelocationOutcome>().Moved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GALACTILOG_APPDATA", previous);
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Build_ExplicitOverride_WinsOverEnvVar()
    {
        var envRoot = Path.Combine(Path.GetTempPath(), "GalactiLogAppHostEnvVarTests_" + Guid.NewGuid().ToString("N"));
        var explicitRoot = Path.Combine(Path.GetTempPath(), "GalactiLogAppHostExplicitTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(envRoot);
        Directory.CreateDirectory(explicitRoot);
        var previous = Environment.GetEnvironmentVariable("GALACTILOG_APPDATA");
        try
        {
            Environment.SetEnvironmentVariable("GALACTILOG_APPDATA", envRoot);

            using var host = AppHost.Build(explicitRoot);
            var appWriter = host.Services.GetRequiredService<AppWriter>();

            Assert.Equal(Path.GetFullPath(explicitRoot), appWriter.AppDataRoot);
            // Phase 10 Task 9: the explicit override reads no pointer and moves nothing either.
            Assert.Equal(
                AppDataRootSource.ExplicitOverride,
                host.Services.GetRequiredService<AppDataRootResolution>().Source);
            Assert.False(host.Services.GetRequiredService<RelocationOutcome>().Moved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GALACTILOG_APPDATA", previous);
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(envRoot))
            {
                Directory.Delete(envRoot, recursive: true);
            }
            if (Directory.Exists(explicitRoot))
            {
                Directory.Delete(explicitRoot, recursive: true);
            }
        }
    }

    // Review item 6's relative/empty/drive-root cases moved out with the predicate itself in
    // Phase 10 Task 9: AppHost.IsUsableAppDataOverride became
    // AppDataRootResolver.IsUsableRoot, and the theory moved to
    // GalactiLog.Core.Tests.Io.AppDataRootResolverTests with the same five InlineData rows. A
    // move, not a deletion.

    // Task 1's per-table guard (plus the general.catalogs_loaded_version flag) makes
    // LoadIfNeeded a no-op after the first successful run -- a second Build() against the
    // SAME already-seeded root must not duplicate rows or throw (Q10: "permanent, no-op
    // after first run").
    [Fact]
    public void Build_SeedingIsNoOpOnASecondBuildAgainstTheSameRoot()
    {
        using var fixture = new AppHostFixture();

        int CountOpenNgcRows()
        {
            using var context = new GalactiLogContext(GalactiLogContextOptions.Create(
                DatabasePaths.BuildConnectionString(Path.Combine(Path.GetFullPath(fixture.Root), DatabasePaths.DatabaseFileName))));
            return context.OpenNgcCatalogEntries.Count();
        }

        var firstCount = CountOpenNgcRows();
        Assert.True(firstCount > 0);

        // The fixture closes the process-global logger before each Build; this second host is
        // built directly, so it does the same thing here for the same reason.
        Serilog.Log.CloseAndFlush();

        using var secondHost = AppHost.Build(fixture.Root, cliMode: false);
        try
        {
            Assert.Equal(firstCount, CountOpenNgcRows());
        }
        finally
        {
            secondHost.Dispose();
        }
    }

    // Phase 7 Task 4. The merge preview read, the dialog factory and the one modal host are
    // registered, and the dialog's search delegate is the dashboard's own TargetSearchQuery, so
    // the two dropdowns cannot drift (spec 12.9).
    [Fact]
    public async Task Build_RegistersTheMergePreviewQueryAndTheMergeDialogHost()
    {
        using var fixture = new AppHostFixture();

        Assert.NotNull(fixture.Host.Services.GetRequiredService<MergePreviewQuery>());
        Assert.NotNull(fixture.Host.Services.GetRequiredService<MergeDialogService>());
        Assert.NotNull(fixture.Host.Services.GetRequiredService<TargetSearchQuery>());

        var create = fixture.Host.Services.GetRequiredService<Func<MergeRequest, MergeDialogViewModel>>();
        var request = new MergeRequest(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid());
        var page = create(request);
        try
        {
            // The dialog's constructor starts its preview read on the thread pool. Awaited, not
            // just cancelled by Dispose: the fixture deletes the database directory in its own
            // Dispose, and a read still in flight holds the file open.
            if (page.PendingPreview is { } pending)
            {
                await pending;
            }
        }
        finally
        {
            page.Dispose();
        }

        // The round trip, not a null check on a non-nullable (review finding 3): the factory
        // hands the request it was given to the dialog it builds.
        Assert.Equal(request, page.Request);
    }

    // Ruling Q18: which merge shape a candidate maps to is answered in Data, by
    // MergePreviewQuery, never by a string match in a view-model.
    [Fact]
    public void ToMergeRequest_MapsAPass2CandidateToALoserIdAndAnUnresolvedNameToALoserName()
    {
        using var fixture = new AppHostFixture();
        var connectionString = fixture.Host.Services.GetRequiredService<DatabaseConnectionString>();
        var preview = fixture.Host.Services.GetRequiredService<MergePreviewQuery>();

        var winnerId = Guid.NewGuid();
        var loserId = Guid.NewGuid();
        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value, tracking: true)))
        {
            context.Targets.Add(new GalactiLog.Data.Entities.Target { Id = winnerId, PrimaryName = "NGC 7331" });
            context.Targets.Add(new GalactiLog.Data.Entities.Target { Id = loserId, PrimaryName = "Deer Lick" });
            context.SaveChanges();
        }

        var candidateId = Guid.NewGuid();
        var pass2 = new MergeCandidateRow(
            candidateId, "Deer Lick", 12, winnerId, "NGC 7331", 0.9d, "duplicate", "reason", DateTime.UtcNow);
        var pass1 = pass2 with { SourceName = "ngc7331 mosaic" };

        Assert.Equal(
            new MergeRequest(winnerId, loserId, null, candidateId),
            AppHost.ToMergeRequest(preview, pass2));
        Assert.Equal(
            new MergeRequest(winnerId, null, "ngc7331 mosaic", candidateId),
            AppHost.ToMergeRequest(preview, pass1));
    }

    // Phase 7 fixer item 6 (phase finding 6).
    [Fact]
    public void ToMergeRequest_WhenTheLoserIsTheSuggestedTarget_ReturnsNoWinner()
    {
        using var fixture = new AppHostFixture();
        var connectionString = fixture.Host.Services.GetRequiredService<DatabaseConnectionString>();
        var preview = fixture.Host.Services.GetRequiredService<MergePreviewQuery>();

        var targetId = Guid.NewGuid();
        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value, tracking: true)))
        {
            context.Targets.Add(new GalactiLog.Data.Entities.Target { Id = targetId, PrimaryName = "NGC 7331" });
            context.SaveChanges();
        }

        // The candidate's source_name resolves to the target it suggests. A request naming it on
        // both sides opens a dialog whose Confirm can never enable, so the winner is dropped and
        // the search box chooses the survivor.
        var candidateId = Guid.NewGuid();
        var row = new MergeCandidateRow(
            candidateId, "NGC 7331", 12, targetId, "NGC 7331", 0.9d, "duplicate", "reason", DateTime.UtcNow);

        Assert.Equal(
            new MergeRequest(null, targetId, null, candidateId),
            AppHost.ToMergeRequest(preview, row));
    }

    /// <summary>
    /// Phase review Important P1. Four optional constructor arguments added in Phase 10 are bound
    /// in <c>AppHost.Build</c> with nothing structural behind them, so dropping any one of them
    /// compiles, leaves the suite green and silently un-ships a roadmap row: <c>logViewer:</c>
    /// removes spec 12.8's log viewer, <c>exportBundle:</c> leaves the export button enabled and
    /// silent, <c>scanStatus:</c> removes ruling Q31's post-scan refresh, and <c>updates:</c>
    /// removes spec 12's update indicator. The same shape
    /// <c>BuildInfoTests.AppHost_BindsBothDiagnosticsSeams_ToBuildInfo</c> already uses for Task
    /// 4's own two seams, narrowed to the one registration block each argument belongs to so a
    /// matching spelling somewhere else in the file cannot satisfy it.
    /// </summary>
    [Fact]
    public void AppHost_BindsEveryOptionalPhase10Seam()
    {
        var appHost = File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "AppHost.cs"));

        var diagnostics = RegistrationBlock(appHost, "new DiagnosticsViewModel(");
        Assert.Contains(
            "logViewer: serviceProvider.GetRequiredService<LogViewerViewModel>()",
            diagnostics,
            StringComparison.Ordinal);
        Assert.Contains(
            "exportBundle: serviceProvider.GetRequiredService<DiagnosticsService>().ExportBundle",
            diagnostics,
            StringComparison.Ordinal);
        Assert.Contains(
            "scanStatus: serviceProvider.GetRequiredService<ScanStatusService>()",
            diagnostics,
            StringComparison.Ordinal);

        var statusBar = RegistrationBlock(appHost, "new StatusBarViewModel(");
        Assert.Contains(
            "updates: serviceProvider.GetRequiredService<UpdateService>()",
            statusBar,
            StringComparison.Ordinal);
    }

    // Spec 12.4's Sky view: without openSurveyView the page's button stays disabled forever with
    // the suite green. A failure names the missing argument.
    [Fact]
    public void AppHost_BindsTheTargetPagesSurveyViewSeam()
    {
        var appHost = File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "AppHost.cs"));

        var targetDetail = RegistrationBlock(appHost, "new TargetDetailViewModel(");
        Assert.Contains(
            "openSurveyView: target => serviceProvider.GetRequiredService<SurveyViewModalService>().ShowAsync(target)",
            targetDetail,
            StringComparison.Ordinal);
    }

    // The Sky view's services are built only when the button is pressed, so a bad registration
    // fails here instead. A failure is the resolution exception naming the unregistered type.
    [Fact]
    public async Task Build_ResolvesTheSurveyViewServices()
    {
        using var fixture = new AppHostFixture();

        Assert.NotNull(fixture.Host.Services.GetRequiredService<SurveyImageService>());
        Assert.NotNull(fixture.Host.Services.GetRequiredService<SurveyViewModalService>());
        var open = fixture.Host.Services.GetRequiredService<Func<SurveyTarget, SurveyViewViewModel>>();

        // The switch is off in a fresh settings file, so the page's first load fetches nothing.
        using var page = open(new SurveyTarget(Guid.NewGuid(), "M 31", 10.6847, 41.2690, 178));
        await page.Settled;
    }

    // Phase 14A Task 7 review P2-1. Spec 11.5 reads general.preview_render_on_navigate on every
    // navigation step and on every binding read of the checkbox, so the getter must not be a
    // database call: SettingsStore.GetGeneral opens a SQLite context and deserializes the whole
    // document per call, which is the reason AppHost keeps the currentGeneral memo at all ("a raw
    // Func<> would be a database read per thumbnail"). Holding an arrow key through the few hundred
    // frames spec 11.5 names would otherwise cost a round trip per keypress on the UI thread, in
    // the one feature whose purpose is making that stepping cheap.
    //
    // Asserted over the source, the same way AppHost_BindsEveryOptionalPhase10Seam asserts its
    // seams: RegistrationBlock strips comments first, so the explanation beside the delegate is not
    // what makes this pass. The write half is unchanged and still goes through MutateGeneral, which
    // raises GeneralChanged synchronously and refreshes the memo the getter reads.
    [Fact]
    public void AppHost_ThePreviewModalsRenderOnNavigateGetter_ReadsTheMemoAndNotTheStore()
    {
        var appHost = File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "AppHost.cs"));

        var preview = RegistrationBlock(appHost, "new PreviewModalViewModel(");

        Assert.Contains(
            "getRenderOnNavigate: () => currentGeneral.Value.PreviewRenderOnNavigate",
            preview,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GetGeneral", preview, StringComparison.Ordinal);
        Assert.Contains("setRenderOnNavigate:", preview, StringComparison.Ordinal);
        Assert.Contains("MutateGeneral", preview, StringComparison.Ordinal);
    }

    // One registration's argument list: from the constructor call to the next registration
    // statement. Comments are stripped first, so an argument named only in a comment is not a
    // match, which is the rule SourceScan's other callers already follow.
    private static string RegistrationBlock(string appHost, string constructorCall)
    {
        var stripped = SourceScan.StripComments(appHost);
        var start = stripped.IndexOf(constructorCall, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{constructorCall} was not found in AppHost.cs.");

        var end = stripped.IndexOf("builder.Services", start, StringComparison.Ordinal);
        return end < 0 ? stripped[start..] : stripped[start..end];
    }

    // Phase 12 verification, blocker 2, and ruling Q13's "a stored id wins" at the one moment it
    // has to be true. general.theme is read once in Build, beside every other settings document,
    // and registered as the StartupTheme that App.OnFrameworkInitializationCompleted hands to
    // ThemeManager.ApplyStored before the first window is built. Before this existed nothing
    // applied the stored id at all: App.axaml merged Luminance.axaml by hand and the only
    // callers of Apply were the Display tab and the delegate registered for it, so Red Light and
    // Deep Sky survived only until the next launch.
    [Fact]
    public void Build_CarriesTheStoredThemeIdAsTheStartupTheme()
    {
        using var fixture = new AppHostFixture();

        // A fresh catalogue stores the default, and the startup theme is that.
        Assert.Equal(
            "civil-dusk",
            fixture.Host.Services.GetRequiredService<GalactiLog.App.Theme.StartupTheme>().ThemeId);

        fixture.Host.Services
            .GetRequiredService<SettingsStore>()
            .MutateGeneral(general => general with { Theme = "deep-sky" });

        // A second Build over the same root is the next application start, which is the launch
        // that used to lose the theme.
        using (var restart = new AppHostFixture(root: fixture.Root))
        {
            Assert.Equal(
                "deep-sky",
                restart.Host.Services.GetRequiredService<GalactiLog.App.Theme.StartupTheme>().ThemeId);
        }
    }

    // Task 7 (PAR-012, questions.md Q7, coordinator ruling on the migration-order escalation).
    // The sink's retainedFileCountLimit is read from general.app_log_retention_days through a
    // tolerant, pre-migration peek that must run before Database.Migrate(), never after: a
    // migration failure is exactly the event the file sink exists to capture, so Log.Logger has
    // to exist first (HEAD's order, restored). Serilog exposes no public way to read a configured
    // sink's own options back out of a built Log.Logger, so the sink argument itself is pinned by
    // a source-text read (TRACKING section 6 item 29's shape, for the same reason: unreachable,
    // here through Serilog's API rather than through the headless harness); both cases below are
    // the behavioural half.
    [Fact]
    public void Build_OnAFreshProfileWithNoDatabaseYet_Succeeds()
    {
        // Every first-ever Build() already exercises "no database yet" at the moment of the
        // sink's pre-migration read: that read runs before Migrate() creates the file. This must
        // never be the reason startup logging goes dark; constructing without throwing, over a
        // brand new temp root with no pointer, no database and no prior state, is the assertion.
        using var fixture = new AppHostFixture();

        Assert.NotNull(fixture.Host);
    }

    [Fact]
    public void Build_WithAStoredAppLogRetentionDays_ReadsItForTheSink()
    {
        using var fixture = new AppHostFixture();
        fixture.Host.Services
            .GetRequiredService<SettingsStore>()
            .MutateGeneral(general => general with { AppLogRetentionDays = 30 });

        var dbPath = Path.Combine(fixture.Root, DatabasePaths.DatabaseFileName);

        // The exact read path AppHost.ReadStoredAppLogRetentionDaysOrDefault uses for the sink:
        // proves the stored value round-trips through DatabasePaths.TryReadStoredGeneralJson
        // rather than only through the full, post-migration SettingsStore.GetGeneral() every
        // other reader uses.
        var json = DatabasePaths.TryReadStoredGeneralJson(dbPath);
        Assert.NotNull(json);
        Assert.Contains("\"app_log_retention_days\":30", json, StringComparison.Ordinal);

        // The sink argument itself: wired to the variable the tolerant read produces, never to
        // the literal 14 the roadmap's Files column and HEAD both once hard-coded.
        var appHostSource = File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "AppHost.cs"));
        Assert.Contains(
            "retainedFileCountLimit: appLogRetentionDaysForSink", appHostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("retainedFileCountLimit: 14", appHostSource, StringComparison.Ordinal);
    }

    // Fix-wave review, fixwave-review.md AppHost.cs:516 (ruling B34). Nothing else pinned that the
    // integration client is built over IntegrationHttp.NewHandler(): a hand edit to
    // new HttpClientHandler() here would re-enable redirects with every other case still green.
    [Fact]
    public void Build_TheIntegrationClientHandler_IsIntegrationHttpNewHandler()
    {
        var appHostSource = File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "AppHost.cs"));

        Assert.Contains(
            "new HttpClient(IntegrationHttp.NewHandler(), disposeHandler: true)",
            appHostSource,
            StringComparison.Ordinal);
    }

    // Fix-wave review, fixwave-review.md AppHost.cs:516 (ruling B34). The same shape as
    // Build_DisposesTheAliasMapCacheWithTheHost: registered as a service, IntegrationHttpClient's
    // Dispose now runs with the host instead of being dead code on an unregistered local.
    [Fact]
    public void Build_DisposesTheIntegrationHttpClientWithTheHost()
    {
        var root = Path.Combine(Path.GetTempPath(), "GalactiLogAppHostTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IntegrationHttpClient holder;
        try
        {
            using (var fixture = new AppHostFixture(root: root))
            {
                holder = fixture.Host.Services.GetRequiredService<IntegrationHttpClient>();
                Assert.False(holder.IsDisposed);
            }

            Assert.True(holder.IsDisposed);
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // Phase 14B fixer, phase review P2-1. This read is the one path in the application that
    // reaches general without SettingsStore.ClampGeneral, and its result goes straight to
    // Serilog as retainedFileCountLimit, which refuses anything below 1 with an
    // ArgumentException thrown inside the LoggerConfiguration chain, before Log.Logger exists.
    // The stored document is written raw here, because no writer in the application can produce
    // these values; a hand edit of the database can.
    [Theory]
    [InlineData("0", 1)]              // below the range: Serilog would refuse it outright
    [InlineData("-5", 1)]             // the same, from the other side of zero
    [InlineData("4000", 3650)]        // above the range, still an Int32
    [InlineData("99999999999", 14)]   // wider than Int32: GetInt32 throws FormatException here
    [InlineData("30", 30)]            // inside the range, carried through unchanged
    public void ReadStoredAppLogRetentionDays_ClampsToTheKeysDeclaredRange(
        string storedJsonNumber, int expected)
    {
        using var fixture = new AppHostFixture();
        var dbPath = StoreRawGeneralJson(
            fixture, $"{{\"app_log_retention_days\":{storedJsonNumber}}}");

        Assert.Equal(expected, AppHost.ReadStoredAppLogRetentionDaysOrDefault(dbPath));
    }

    // The behavioural half of the same fix: a second Build over the same root is the next
    // application start, and it must reach a built Log.Logger rather than throwing out of the
    // sink's own construction.
    //
    // A stored value WIDER than Int32 is deliberately not one of these rows, and the clamp theory
    // above is where it is covered. This read tolerates it and the sink is built, but the start
    // then fails further down at SettingsStore.GetGeneral(), because
    // JsonSerializer.Deserialize<GeneralSettings> cannot convert the number to Int32 and
    // SettingsStore.Deserialize catches nothing, so ClampGeneral never sees the document. That is
    // a separate defect, it is not on this fixer's list, and it is below Log.Logger, so such a
    // start fails WITH a logged exception rather than silently, which is the outcome P2-1 was
    // about. Escalated to the coordinator against verification step 38.
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void Build_WithAnOutOfRangeStoredAppLogRetentionDays_StillStarts(string storedJsonNumber)
    {
        using var fixture = new AppHostFixture();
        StoreRawGeneralJson(fixture, $"{{\"app_log_retention_days\":{storedJsonNumber}}}");

        using var restart = new AppHostFixture(root: fixture.Root);

        Assert.NotNull(restart.Host);
    }

    // Phase 14B fixer, fixer list item 40 (task6-review P3). Ruling D4: the dashboard's column
    // gear and the Display tab's "Dashboard columns" section are ONE picker over ONE live list,
    // not two that agree. The eight D4 cases in DashboardColumnGearTests build their own
    // ColumnPickerViewModel.ForDashboard(...) rather than the composition root's, so the wiring
    // that makes the rule true could break with every one of them still green. This is the
    // composition root's own resolution, over the real container.
    [Fact]
    public void Build_TheDashboardColumnPicker_ReadsTheDashboardsOwnLiveColumnList()
    {
        using var fixture = new AppHostFixture();

        var dashboard = fixture.Host.Services.GetRequiredService<DashboardViewModel>();
        var dashboardColumns = dashboard.Targets.Columns;
        var picker = fixture.Host.Services
            .GetRequiredService<DisplayTabViewModel>().DashboardColumns;

        Assert.NotNull(picker);

        // The whole rule: one list, read by both surfaces, so the gear's checkboxes and the
        // Display tab's cannot disagree about what is on. Not "two equal lists". The toggle is
        // deliberately not pressed here: it persists through DisplayColumnWriter's queued write
        // and this fixture deletes its own app data root on dispose, so a press would race the
        // write; the toggle's own behaviour is DashboardColumnGearTests' eight D4 cases.
        Assert.Same(dashboardColumns, picker!.Columns);

        // The dashboard's own first query is in flight the moment it is resolved, and this
        // fixture deletes its app data root on dispose, so it is joined here the way the other
        // case in this file that resolves the dashboard joins it.
        dashboard.Quiesce(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Ruling A6 and question Q8, PROVED rather than assumed: <c>AnalysisCache</c> rides the one
    /// <c>GeneralChanged</c> staleness handler, and the Analysis page's own
    /// <c>display.analysis</c> save never reaches it, so remembering a tab does not discard every
    /// cached result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Step 0 is load bearing.</b> The staleness subscription is made inside the
    /// <c>ScanStatusService</c> registration factory, so it does not exist until that service has
    /// been resolved, which is what GUI startup does. Dropping step 0 was run under the build lock
    /// and fails step 2, the control, reading <c>["Ha"]</c> where it expects <c>["Ha", "OIII"]</c>:
    /// with nothing subscribed the general save drops nothing, so every later assertion about the
    /// handler NOT firing would be pinning an absent subscription.
    /// <c>Phd2ReRunHostWiringTests</c> says it in as many words and every one of its cases resolves
    /// the service first; this is the same shape.
    /// </para>
    /// <para>
    /// The memo is observed the way <c>AnalysisCacheTests</c> observes it, by moving the database
    /// between two calls: <c>AnalysisCache</c> exposes no entry count, and a counting fake of it is
    /// impossible because it is sealed with no interface (ruling P1-1). A second call that answers
    /// the OLD row set is a hit; one that answers the new set is a miss, which is the drop.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheAnalysisCache_IsDroppedByAGeneralSave_AndNeverByADisplaySave()
    {
        using var fixture = new AppHostFixture();

        // 0. Resolving ScanStatusService is what runs the factory that makes the subscription.
        fixture.Host.Services.GetRequiredService<ScanStatusService>();

        var store = fixture.Host.Services.GetRequiredService<SettingsStore>();
        var cache = fixture.Host.Services.GetRequiredService<AnalysisCache>();

        // 1. Seed one entry through any member.
        SeedLightFrame(fixture, "Ha");
        Assert.Equal(new[] { "Ha" }, cache.Filters());

        // The memo really is a memo: a second frame in the database does not move the answer.
        SeedLightFrame(fixture, "OIII");
        Assert.Equal(new[] { "Ha" }, cache.Filters());

        // 2. THE CONTROL, first. A general save that moves a key outside the inert list drops the
        // entry, which is what proves the handler is wired before anything is asserted about it
        // not firing.
        //
        // The key is general.auto_scan_interval_minutes, and which key it is matters. It is
        // outside OnlyInertKeysMoved's four wbpp_ keys, so the control
        // still asks what ruling A6 wants asked, and it is NOT one of the four inputs of
        // SettingsStore.GuidingInputsMoved (the profile map, the observer timezone, the latitude
        // and the longitude), so the save raises no Phd2GuidingInputsChanged and queues no
        // correlation pass. A pass would reach this same memo twice more on a background thread,
        // once through Phd2CorrelationRunner.PassCompleted and once through the
        // phd2_correlation_pending clear's own GeneralChanged, and either arrival would race steps
        // 3 and 4 and fail them naming the display save. Nothing subscribes to GeneralChanged for
        // this key either: ScanScheduler reads the interval live on each iteration.
        //
        // Awaiting Phd2CorrelationRunner.InFlight here instead would WEAKEN the control, because
        // the pass's own invalidation would satisfy step 2 and the case would stop isolating
        // GeneralChanged.
        store.SaveGeneral(store.GetGeneral() with { AutoScanIntervalMinutes = 180 });
        Assert.Equal(new[] { "Ha", "OIII" }, cache.Filters());

        // 3. A display save does NOT. display.analysis lives in the display document, which is
        // saved through SaveDisplay and raises DisplayChanged; the staleness handler follows
        // GeneralChanged only, so a tab switch discards nothing.
        SeedLightFrame(fixture, "SII");
        var display = store.GetDisplay();
        store.SaveDisplay(display with { Analysis = display.Analysis with { Tab = "matrix" } });
        Assert.Equal(new[] { "Ha", "OIII" }, cache.Filters());

        // 4. And OnlyInertKeysMoved's fail-closed list working as documented: a general save that
        // moves only a wbpp_ key leaves the entry. No display key is on that list and none may be
        // added to it: the inert list is general-document keys and the display document does not
        // reach the handler at all.
        store.SaveGeneral(store.GetGeneral() with
        {
            WbppDefaultOsDocument = GalactiLog.Core.Wbpp.WbppSettingsRead.WriteDefaultOs("bash"),
        });
        Assert.Equal(new[] { "Ha", "OIII" }, cache.Filters());

        // Neither SaveGeneral above moves a guiding input, so neither queues a pass. The join is
        // kept as the fixture-lifetime guard every other case in this file carries: a pass this
        // host queued for any reason holds a SQLite handle on the root the fixture deletes on
        // dispose, and a delete that throws over a live handle masks what the assertions said.
        await fixture.Host.Services.GetRequiredService<Phd2CorrelationRunner>().InFlight;
    }

    /// <summary>
    /// Phase review P1-A, on the real host: the registered Analysis page follows the one
    /// notification this host raises, so the filter bar's option lists are re-read after a general
    /// save instead of standing at the rows this process first read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case above proves the memo is dropped; this one proves the open page is told. Both
    /// halves are needed: a dropped memo nothing asks a second time buys nothing, which is exactly
    /// what the page shipped with. The structural half, that <c>AppHost</c> hands the page both
    /// delegates, is the source scan in <c>Phd2ReRunHostWiringTests</c>.
    /// </para>
    /// <para>
    /// An <c>AvaloniaFact</c> rather than a <c>Fact</c>, and the only case in this file that is
    /// one: the registration passes <c>post: null</c>, so the page marshals through
    /// <c>UiPost.Default</c> and both the handler and the list publish are queued on the
    /// dispatcher. Without a dispatcher to pump, the notification would reach nothing observable
    /// and the case would pass over a page that never answered.
    /// </para>
    /// <para>
    /// The save moves <c>general.auto_scan_interval_minutes</c> for the reasons the case above
    /// records: it is outside the inert list, so it really does raise the notification, and it is
    /// not one of the four guiding inputs, so it queues no correlation pass whose own notification
    /// would arrive later on a background thread.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public async Task TheAnalysisPageOnTheRealHost_ReReadsItsFilterList_WhenTheDerivedDataChanges()
    {
        using var fixture = new AppHostFixture();

        // Resolving ScanStatusService is what runs the factory that subscribes the one staleness
        // handler, and it is what GUI startup does.
        fixture.Host.Services.GetRequiredService<ScanStatusService>();
        var store = fixture.Host.Services.GetRequiredService<SettingsStore>();

        SeedLightFrame(fixture, "Ha");
        var page = fixture.Host.Services.GetRequiredService<AnalysisViewModel>();
        Drain(page);

        Assert.Equal(new[] { "All filters", "Ha" }, page.SharedFilter.FilterChoices);

        // The library gains a filter, and the save is what tells the page the derived data behind
        // it was rewritten.
        SeedLightFrame(fixture, "OIII");
        store.SaveGeneral(store.GetGeneral() with { AutoScanIntervalMinutes = 180 });
        Drain(page);

        Assert.Equal(new[] { "All filters", "Ha", "OIII" }, page.SharedFilter.FilterChoices);

        // The fixture-lifetime guard every other case in this file carries: a pass this host
        // queued for any reason holds a SQLite handle on the root the fixture deletes on dispose.
        await fixture.Host.Services.GetRequiredService<Phd2CorrelationRunner>().InFlight;
    }

    /// <summary>
    /// Design lesson 2 over the two memos an open page shows: every trigger that drops the
    /// Statistics memo drops the Analysis memo as well, because one handler drops both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A source scan, because the thing being pinned is that no future trigger can be wired to one
    /// and forgotten on the other, and that is a property of the file rather than of one run. The
    /// gap this closes shipped exactly that way: <c>ScanFinished</c> and <c>AliasSourcesChanged</c>
    /// each dropped <c>StatsCache</c> and neither dropped <c>AnalysisCache</c>, so the Analysis
    /// page served pre-scan figures after the commonest event there is.
    /// </para>
    /// <para>
    /// The second assertion is the other half of the same ruling: the Statistics page follows
    /// <c>ScanStatusService</c> itself, so raising the process-level notification from a finished
    /// scan would refresh that page twice for one scan. The Analysis page is given the scan through
    /// its own composed subscription instead, and this is what fails if anyone routes a scan into
    /// the notifier later.
    /// </para>
    /// </remarks>
    [Fact]
    public void AppHostSource_EveryTriggerThatDropsTheStatisticsMemo_DropsTheAnalysisMemoToo()
    {
        var code = SourceScan.StripComments(File.ReadAllText(
            Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "AppHost.cs")));

        // The Statistics page's Refresh button drops its own memo through the page's `invalidate:`
        // seam, and that one is excluded here by name: it is a button a reader presses, not a
        // trigger the host raises, and the Analysis page has no equivalent, so counting it would
        // pin an imbalance that is correct.
        const string exceptTheRefreshButton = @"(?<!invalidate: \(\) => )";
        var stats = Regex.Matches(
            code, exceptTheRefreshButton + @"serviceProvider\.GetRequiredService<StatsCache>\(\)\.Invalidate\(\)").Count;
        var analysis = Regex.Matches(
            code, @"serviceProvider\.GetRequiredService<AnalysisCache>\(\)\.Invalidate\(\)").Count;

        // Two sites each today: InvalidateOpenPageMemos, which ScanFinished and
        // AliasSourcesChanged both call, and InvalidateDerivedCaches, which the settings save, the
        // completed correlation pass, the four maintenance actions and the reset all call.
        Assert.True(stats >= 2, $"The StatsCache drop needle no longer matches AppHost.cs ({stats} sites).");
        Assert.True(
            stats == analysis,
            "A trigger drops one open page's memo and not the other's. ScanFinished and "
            + "AliasSourcesChanged must drop both through InvalidateOpenPageMemos, and a settings "
            + $"save, a correlation pass, a maintenance action and a reset through "
            + $"InvalidateDerivedCaches. StatsCache drops: {stats}, AnalysisCache drops: {analysis}.");

        Assert.DoesNotContain(
            "ScanFinished += (_, _) => RaiseDerivedDataChanged()",
            code,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Phase review P1-A, the larger half: a completed scan drops the Analysis memo and reaches the
    /// open page, and it still costs the Statistics page exactly one refresh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A real scan through the registered <c>ScanCoordinator</c> over an empty root of this
    /// fixture's own making, because <c>ScanStatusService.ScanFinished</c> has no raise of its own
    /// and a stand-in would pin nothing about the host. The root is empty, so the walk discovers
    /// nothing, the zero-discovery guard holds orphan pruning back and the seeded rows below are
    /// untouched: what the scan contributes to this case is its finish, not its catalogue.
    /// </para>
    /// <para>
    /// One assertion covers both halves of the fix, because the page's re-read goes through the
    /// memo: a memo that was not dropped answers the pre-scan list, and a page that was not told
    /// never asks.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public async Task AFinishedScanOnTheRealHost_DropsTheAnalysisMemo_AndRefreshesTheOpenPage()
    {
        using var fixture = new AppHostFixture();

        fixture.Host.Services.GetRequiredService<ScanStatusService>();
        SeedLightFrame(fixture, "Ha");

        var page = fixture.Host.Services.GetRequiredService<AnalysisViewModel>();
        Drain(page);
        Assert.Equal(new[] { "All filters", "Ha" }, page.SharedFilter.FilterChoices);

        // The control on the ruling's other half, counted rather than argued: the Statistics page
        // follows the scan itself, so one scan must still cost it one load. Its first load is the
        // one its constructor starts, before this counter exists.
        var statistics = fixture.Host.Services.GetRequiredService<StatisticsViewModel>();
        StatisticsViewModelTestFactory.Settle(statistics);
        var statisticsLoads = 0;
        statistics.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(StatisticsViewModel.IsLoading) && statistics.IsLoading)
            {
                statisticsLoads++;
            }
        };

        SeedLightFrame(fixture, "OIII");

        var root = Path.Combine(Path.GetFullPath(fixture.Root), "scan-root");
        Directory.CreateDirectory(root);
        await fixture.Host.Services.GetRequiredService<ScanCoordinator>()
            .RunAsync(ScanTrigger.Manual, [root], CancellationToken.None);

        Drain(page);

        Assert.Equal(new[] { "All filters", "Ha", "OIII" }, page.SharedFilter.FilterChoices);
        Assert.Equal(1, statisticsLoads);

        StatisticsViewModelTestFactory.Settle(statistics);
        await fixture.Host.Services.GetRequiredService<Phd2CorrelationRunner>().InFlight;
    }

    /// <summary>
    /// A target a scan catalogued is on the dashboard once that scan's
    /// ScanFinished has been handled, with no filter change needed to fetch it. A failure reads
    /// as the target missing from <c>Targets.Rows</c> after the finish.
    /// </summary>
    [AvaloniaFact]
    public async Task AFinishedScanOnTheRealHost_PutsTheScansNewTargetOnTheDashboard()
    {
        using var fixture = new AppHostFixture();

        fixture.Host.Services.GetRequiredService<ScanStatusService>();
        var dashboard = fixture.Host.Services.GetRequiredService<DashboardViewModel>();
        SeedTargetWithLightFrame(fixture, "IC 1805");
        dashboard.RequestQuery();
        DrainDashboard(dashboard);
        Assert.Contains(dashboard.Targets.Rows, row => row.Name == "IC 1805");

        SeedTargetWithLightFrame(fixture, "NGC 7000");

        var root = Path.Combine(Path.GetFullPath(fixture.Root), "scan-root");
        Directory.CreateDirectory(root);
        await fixture.Host.Services.GetRequiredService<ScanCoordinator>()
            .RunAsync(ScanTrigger.Manual, [root], CancellationToken.None);

        DrainDashboard(dashboard);

        Assert.Contains(dashboard.Targets.Rows, row => row.Name == "NGC 7000");
        await fixture.Host.Services.GetRequiredService<Phd2CorrelationRunner>().InFlight;
    }

    /// <summary>
    /// A target written while another page is showing is on the dashboard
    /// once the rail returns to it, with no filter click. A failure reads as NGC 7000 missing.
    /// </summary>
    [AvaloniaFact]
    public void ATargetWrittenWhileOnSettings_IsOnTheDashboardAfterTheRoundTrip()
    {
        using var fixture = new AppHostFixture();

        fixture.Host.Services.GetRequiredService<ScanStatusService>();
        var shell = fixture.Host.Services.GetRequiredService<MainWindowViewModel>();
        var dashboard = fixture.Host.Services.GetRequiredService<DashboardViewModel>();
        SeedTargetWithLightFrame(fixture, "IC 1805");
        dashboard.RequestQuery();
        DrainDashboard(dashboard);
        Assert.Contains(dashboard.Targets.Rows, row => row.Name == "IC 1805");

        shell.Selected = shell.Items.First(item => item.Key == "settings");
        SeedTargetWithLightFrame(fixture, "NGC 7000");
        shell.Selected = shell.Items.First(item => item.Key == "dashboard");
        DrainDashboard(dashboard);

        Assert.Contains(dashboard.Targets.Rows, row => row.Name == "NGC 7000");
    }

    /// <summary>
    /// The merge route: a merge committed while a detail page is open is on the dashboard once the
    /// detail page closes back to it. A failure reads as the merged-away target still listed.
    /// </summary>
    [AvaloniaFact]
    public void AMergeWhileADetailPageIsOpen_DropsTheMergedAwayTargetFromTheDashboard()
    {
        using var fixture = new AppHostFixture();

        fixture.Host.Services.GetRequiredService<ScanStatusService>();
        var shell = fixture.Host.Services.GetRequiredService<MainWindowViewModel>();
        var dashboard = fixture.Host.Services.GetRequiredService<DashboardViewModel>();
        var winner = SeedTargetWithLightFrame(fixture, "NGC 7000");
        var loser = SeedTargetWithLightFrame(fixture, "North America Nebula");
        dashboard.RequestQuery();
        DrainDashboard(dashboard);
        Assert.Contains(dashboard.Targets.Rows, row => row.Name == "North America Nebula");

        shell.OpenDetail(winner.ToString());
        fixture.Host.Services.GetRequiredService<MergeRepository>().Merge(winner, loser);
        shell.CloseDetailCommand.Execute(null);
        DrainDashboard(dashboard);

        Assert.DoesNotContain(dashboard.Targets.Rows, row => row.Name == "North America Nebula");
    }

    // The precondition seeds write through EF directly and raise nothing the dashboard hears, and the
    // constructor's own query may have read before they committed, so each test asks for the read
    // itself before the first drain. The re-query under test (return to the page) stays unasked.
    private static void DrainDashboard(DashboardViewModel dashboard)
    {
        for (var round = 0; round < 3; round++)
        {
            Dispatcher.UIThread.RunJobs();
            dashboard.Quiesce(TimeSpan.FromSeconds(30));
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Guid SeedTargetWithLightFrame(AppHostFixture fixture, string name)
    {
        var connectionString = fixture.Host.Services.GetRequiredService<DatabaseConnectionString>();
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value, tracking: true));
        var targetId = Guid.NewGuid();
        context.Targets.Add(new GalactiLog.Data.Entities.Target { Id = targetId, PrimaryName = name });
        context.Images.Add(new GalactiLog.Data.Entities.Image
        {
            Id = Guid.NewGuid(),
            FilePath = $@"X:\seed\{name}.fits",
            FileName = $"{name}.fits",
            ImageType = "LIGHT",
            FilterUsed = "Ha",
            ResolvedTargetId = targetId,
            SessionDate = new DateOnly(2025, 6, 1),
            CaptureDate = new DateTime(2025, 6, 1, 22, 0, 0, DateTimeKind.Utc),
        });
        context.SaveChanges();
        return targetId;
    }

    /// <summary>
    /// The same for an alias source save, which regroups every figure the page draws without a
    /// single frame changing.
    /// </summary>
    /// <remarks><c>SaveFilters</c> writes the filters document and raises
    /// <c>AliasSourcesChanged</c> and nothing else, so this case isolates that one route: no
    /// general document is written, so the process-level notification the other cases use cannot
    /// stand in for it.</remarks>
    [AvaloniaFact]
    public void AnAliasSourceSaveOnTheRealHost_DropsTheAnalysisMemo_AndRefreshesTheOpenPage()
    {
        using var fixture = new AppHostFixture();

        fixture.Host.Services.GetRequiredService<ScanStatusService>();
        var store = fixture.Host.Services.GetRequiredService<SettingsStore>();
        SeedLightFrame(fixture, "Ha");

        var page = fixture.Host.Services.GetRequiredService<AnalysisViewModel>();
        Drain(page);
        Assert.Equal(new[] { "All filters", "Ha" }, page.SharedFilter.FilterChoices);

        SeedLightFrame(fixture, "SII");
        store.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new FilterSetting { Aliases = ["H-alpha"] },
        });

        Drain(page);

        Assert.Equal(new[] { "All filters", "Ha", "SII" }, page.SharedFilter.FilterChoices);
    }

    // Runs the queued handler, joins the read it starts on a pool thread and then runs the publish
    // that read queued. Two pumps, because the second closure does not exist until the first has
    // run: this is the dispatcher half of AnalysisViewModelTestFactory.Settle, which joins tasks
    // and pumps nothing.
    private static void Drain(AnalysisViewModel page)
    {
        for (var round = 0; round < 3; round++)
        {
            Dispatcher.UIThread.RunJobs();
            AnalysisViewModelTestFactory.Settle(page);
            Dispatcher.UIThread.RunJobs();
        }
    }

    // One catalogued LIGHT frame carrying the given filter name, which is the whole of what
    // AnalysisQuery.Filters reads.
    private static void SeedLightFrame(AppHostFixture fixture, string filterUsed)
    {
        var connectionString = fixture.Host.Services.GetRequiredService<DatabaseConnectionString>();
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value, tracking: true));
        context.Images.Add(new GalactiLog.Data.Entities.Image
        {
            Id = Guid.NewGuid(),
            FilePath = $@"X:\seed\{filterUsed}.fits",
            FileName = $"{filterUsed}.fits",
            ImageType = "LIGHT",
            FilterUsed = filterUsed,
            SessionDate = new DateOnly(2025, 6, 1),
            CaptureDate = new DateTime(2025, 6, 1, 22, 0, 0, DateTimeKind.Utc),
        });
        context.SaveChanges();
    }

    // Writes the general document exactly as given, past every clamp and every serializer, and
    // returns the database path the pre-migration peek reads.
    private static string StoreRawGeneralJson(AppHostFixture fixture, string json)
    {
        // Forces the settings row to exist before the raw update below.
        fixture.Host.Services.GetRequiredService<SettingsStore>().MutateGeneral(general => general);

        var dbPath = Path.Combine(fixture.Root, DatabasePaths.DatabaseFileName);
        using var connection = new SqliteConnection(DatabasePaths.BuildConnectionString(dbPath));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE user_settings SET general = $general;";
        command.Parameters.AddWithValue("$general", json);
        Assert.Equal(1, command.ExecuteNonQuery());
        return dbPath;
    }
}
