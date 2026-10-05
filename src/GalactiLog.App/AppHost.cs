using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using GalactiLog.App.Services;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Activity;
using GalactiLog.App.ViewModels.Analysis;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.Diagnostics;
using GalactiLog.App.ViewModels.Merge;
using GalactiLog.App.ViewModels.Preview;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.ViewModels.Setup;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.App.ViewModels.Tray;
using GalactiLog.App.ViewModels.Update;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Diagnostics;
using GalactiLog.Core.Imaging;
using GalactiLog.Core.Integrations;
using GalactiLog.Core.Io;
using GalactiLog.Core.Survey;
using GalactiLog.Core.Targets;
using GalactiLog.Data;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Maintenance;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace GalactiLog.App;

// Wires Microsoft.Extensions.Hosting, Serilog, and the pieces that exist so far (AppWriter,
// SettingsRepository, SettingsStore) into one IHost that both the GUI and the CLI share
// (design-spec 4.3, 16.1, 17.2).
public static class AppHost
{
    // appDataRootOverride exists solely so tests can point the whole host at an isolated
    // temp directory instead of the real %LOCALAPPDATA%\GalactiLogData. Production code
    // (Task 7) always calls AppHost.Build() with no override. Deliberately not
    // Build(string[] args): this application's CLI verbs are bare positional tokens that
    // do not fit Host.CreateApplicationBuilder(args)'s --key=value configuration binding.
    //
    // cliMode adds a console sink that writes every log line to stderr, never stdout, so
    // logging never pollutes a CLI verb's --json stdout contract (spec 15, 16.1). GUI
    // startup (cliMode: false) gets no console sink at all.
    //
    // httpHandlerOverride exists solely so tests can substitute a stub HttpMessageHandler for
    // SimbadClient/SesameClient instead of touching the real network (Task 8). Production
    // code always calls Build() with no override, so both clients get a real
    // HttpClientHandler.
    public static IHost Build(
        string? appDataRootOverride = null,
        bool cliMode = false,
        HttpMessageHandler? httpHandlerOverride = null,
        Action<TimeSpan, CancellationToken>? retryWaitOverride = null)
        => Build(appDataRootOverride, cliMode, httpHandlerOverride, inspectServices: null, retryWaitOverride: retryWaitOverride);

    // inspectServices is Phase 10 Task 8's structural-rule seam, and it is internal because it
    // has no production caller: the public overload above passes null and nothing else in the
    // solution calls this one. HostDisposalTests needs the registration descriptors themselves
    // to assert that no disposable type is ever registered as an instance, and the built
    // IServiceProvider does not expose them; the alternative is reflecting into
    // ServiceProvider's private call-site factory, which pins a test to a framework internal.
    //
    // The delegate is invoked once, immediately before builder.Build(), and is handed a SNAPSHOT
    // of the descriptors rather than the live IServiceCollection (Task 8 review minor 4). "May
    // only read" was a comment before, so a future test could have registered a service through
    // the seam and changed the container the production path builds. The snapshot is all
    // HostDisposalTests ever used: it materializes the list immediately.
    //
    // pointerPathOverride and folderOverride are Phase 10 Task 9's seams, internal for the same
    // reason as inspectServices: neither has a production caller, and the public overload above
    // passes null for both. AppDataRootStartupTests passes a temp pointer path and a temp
    // default/legacy pair, so the pointer resolution, the pending move and the legacy adoption are
    // exercised without the real %APPDATA%\GalactiLog\datapath.json, the real
    // %LOCALAPPDATA%\GalactiLogData or the real %LOCALAPPDATA%\GalactiLog ever being read,
    // written or created.
    internal static IHost Build(
        string? appDataRootOverride,
        bool cliMode,
        HttpMessageHandler? httpHandlerOverride,
        Action<IReadOnlyList<ServiceDescriptor>>? inspectServices,
        string? pointerPathOverride = null,
        AppDataRootFolders? folderOverride = null,
        Action<TimeSpan, CancellationToken>? retryWaitOverride = null)
    {
        var builder = Host.CreateApplicationBuilder();

        // Spec 17.2's four-step resolution, plus any move this start performs (Phase 10 Task 9).
        // Deliberately ahead of the LoggerConfiguration below: the sink's path is derived from the
        // final root, and configuring it against a root the relocation is about to change would
        // put the first lines of this start in one directory and the rest in another. The outcome
        // is carried as a value and logged immediately after Log.Logger is assigned.
        var (appWriter, resolution, relocation) =
            PrepareAppDataRoot(appDataRootOverride, pointerPathOverride, folderOverride);

        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        var ringBuffer = new LogRingBuffer();

        // Task 7 (PAR-012, spec 5.8.1, 16.1, questions.md Q7; coordinator ruling on the escalation
        // this raised). general.app_log_retention_days has to be known before Log.Logger is built,
        // because Serilog's retainedFileCountLimit is fixed at sink construction and never
        // re-read. But the database must NOT be opened or migrated this early: a migration failure
        // is exactly the event the file sink exists to capture, so Migrate() stays after
        // Log.Logger, in HEAD's order, below. This is a tolerant, pre-migration peek instead: a
        // bare read-only connection (DatabasePaths.TryReadStoredGeneralJson, allowlisted there by
        // FileSafetyTest) that returns null for a database that does not exist yet, a missing
        // table, a missing row or any failure, and the local function below falls back to the
        // documented default 14 for null, a missing key or a malformed document. No migration and
        // no EF model build happen in this read. Phase 14B fixer (phase review P2-1): the value
        // is also clamped to spec 5.8.1's 1 to 3650 before it reaches the sink, because Serilog
        // refuses a retainedFileCountLimit below 1 and this read is the only one in the
        // application that does not pass through SettingsStore.ClampGeneral.
        var appLogRetentionDaysForSink = ReadStoredAppLogRetentionDaysOrDefault(
            appWriter.ResolveAppDataPath(DatabasePaths.DatabaseFileName));

        // Configured before anything else that might fail, so a startup failure is still
        // logged (design-spec 16.1's opening sentence). The one exception is the app data root
        // resolution and relocation above, which has to run first because this sink's path is
        // composed from the root it produces; its outcome is logged on the first lines below.
        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .Enrich.FromLogContext()
            .WriteTo.File(
                // The directory name is DiagnosticsService's constant, so the sink, spec 12.8's
                // Paths group and Task 2's log reader cannot drift onto three spellings of one
                // name (review finding M3, design-lessons rule 1).
                appWriter.ResolveAppDataPath(
                    DiagnosticsService.LogDirectoryName + "/" + DiagnosticsService.LogFileNamePrefix + ".log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: appLogRetentionDaysForSink,
                fileSizeLimitBytes: 33_554_432,
                rollOnFileSizeLimit: true,
                shared: false,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(ringBuffer, LogEventLevel.Warning);

        if (cliMode)
        {
            // standardErrorFromLevel: Verbose routes every accepted event to stderr (the
            // lowest level, so nothing falls through to stdout), keeping a CLI verb's
            // --json stdout contract clean.
            loggerConfiguration.WriteTo.Console(
                restrictedToMinimumLevel: LogEventLevel.Information,
                standardErrorFromLevel: LogEventLevel.Verbose);
        }

        Log.Logger = loggerConfiguration.CreateLogger();

        // FIXER LIST F11: one unconditional event, right here. Serilog's rolling file sink creates
        // logs\ lazily on the first event it writes, so a GUI start that logged nothing left no
        // log directory at all under the resolved app data root (spec 16.1). This is also the
        // startup line a support request needs: version, where the data actually went, which mode.
        Log.Information(
            "GalactiLog {Version} starting; app data root {AppDataRoot} from {RootSource}; relocation {Relocation}; cliMode {CliMode}",
            typeof(AppHost).Assembly.GetName().Version?.ToString() ?? "unknown",
            appWriter.AppDataRoot,
            AppDataRootResolver.SourceLabel(resolution.Source),
            DescribeRelocation(relocation),
            cliMode);

        if (resolution.PointerWarning is { Length: > 0 } pointerWarning)
        {
            Log.Warning("{PointerWarning}", pointerWarning);
        }

        builder.Services.AddSerilog(dispose: true);

        var connectionString = DatabasePaths.BuildConnectionString(
            appWriter.ResolveAppDataPath(DatabasePaths.DatabaseFileName));

        // Migration runs before any window is shown (design-spec 17.2). A failure here
        // propagates out of Build() uncaught; Serilog has already been configured above so
        // whatever it can log about the failure is captured.
        using (var migrationContext = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true)))
        {
            migrationContext.Database.Migrate();
        }

        var settingsRepository = new SettingsRepository(connectionString);
        // Fix-wave review P2-4: SettingsStore's repair of a hand-edited display document
        // (SettingsStore.cs ReadDisplay/RepairDisplay) logs through the optional trailing
        // ILogger the house idiom already gives FrameHeadersQuery and TargetWriteRepository
        // (both resolved as `serviceProvider.GetRequiredService<ILoggerFactory>()` or
        // `ILogger<T>` once the container exists). This construction runs before
        // builder.Build(), so no IServiceProvider exists yet to resolve from; Log.Logger is
        // already assigned above, so SerilogLoggerFactory, the same bridge AddSerilog wires
        // into the container's ILoggerFactory, is built directly from it instead. No second
        // logging route: it is the identical Serilog pipeline every other warning in this
        // method reaches, ring buffer sink included.
        var settingsStore = new SettingsStore(
            settingsRepository, new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger).CreateLogger<SettingsStore>());

        // Empty thumbnail_cache_dir means the default location under the app data root;
        // any other value is used as given (design-spec 5.8.1, 17.2). Shared between the
        // initial resolution below and the GeneralChanged handler so a settings change
        // re-authorizes the new root without a restart.
        string ResolveThumbnailCacheRoot(string thumbnailCacheDir)
            => thumbnailCacheDir.Length > 0
                ? thumbnailCacheDir
                : Path.Combine(appWriter.AppDataRoot, "thumbnails");

        // Phase review item 2: every settings document the view-models need is read here, once,
        // on Program.Main's thread, and passed by value. A Func<> that reached a view-model
        // constructor would be a synchronous SQLite read on the UI thread at window construction.
        var initialGeneral = settingsStore.GetGeneral();
        var initialDisplay = settingsStore.GetDisplay();
        // Phase 6 Task 7: user_settings.graph (spec 5.8.3), the Target detail charts' metric and
        // filter selection. Read here for the same reason as the two above.
        var initialGraph = settingsStore.GetGraph();
        levelSwitch.MinimumLevel = ParseLevel(initialGeneral.LogLevel);
        appWriter.ThumbnailCacheRoot = ResolveThumbnailCacheRoot(initialGeneral.ThumbnailCacheDir);
        appWriter.CreateDirectory(appWriter.ThumbnailCacheRoot);

        // Phase 8 Task 4: the one settings document a background worker reads repeatedly.
        // ThumbnailCache needs general.thumbnail_width, preview_resolution and preview_cache_mb at
        // call time, not at construction (a stale bound would keep evicting to the old cap
        // forever), and SettingsStore.GetGeneral opens a SQLite context on every call, so a raw
        // Func<> would be a database read per thumbnail on two worker threads. This memo is the
        // read, refreshed by the GeneralChanged handler below.
        //
        // A one-field holder rather than a captured local: the reference assignment is atomic and
        // GeneralSettings is immutable, so a reader sees one whole document or the other, but a
        // closure field cannot be marked volatile and a reader on a pump or the scan thread has no
        // barrier that guarantees it ever sees the new one. Volatile.Write/Volatile.Read gives it
        // one for the cost of a field.
        var currentGeneral = new GeneralSettingsHolder(initialGeneral);

        // Live-reload: changing general.log_level or general.thumbnail_cache_dir on the
        // Diagnostics/Settings tab takes effect immediately with no restart (design-spec
        // 16.1, 17.2).
        settingsStore.GeneralChanged += (_, general) =>
        {
            levelSwitch.MinimumLevel = ParseLevel(general.LogLevel);
            appWriter.ThumbnailCacheRoot = ResolveThumbnailCacheRoot(general.ThumbnailCacheDir);
            appWriter.CreateDirectory(appWriter.ThumbnailCacheRoot);
            currentGeneral.Value = general;
        };

        // THE INSTANCE-REGISTRATION CENSUS (TRACKING section 6 item 6, coordinator ruling Q24).
        //
        // These five, plus activityRepository and scanRunRepository further down, are the only
        // seven registrations in this method that hand the container an object it did not
        // construct. Every other registration is a factory. The reason is the same for all seven
        // and it is not a preference: Build() itself uses each of them before any container
        // exists, so there is nothing to resolve them from.
        //
        //   appWriter            CreateDirectory and ResolveAppDataPath for the Serilog sink, the
        //                        GeneralChanged handler's cache-root re-authorization, and the
        //                        ThumbnailCacheBytes closure.
        //   levelSwitch          the LoggerConfiguration above and the GeneralChanged handler.
        //   ringBuffer           the LoggerConfiguration above.
        //   settingsRepository   new SettingsStore(settingsRepository) on the next line.
        //   settingsStore        four initial reads, the GeneralChanged subscription, CatalogSeeder.
        //   activityRepository   PruneRetention at startup.
        //   scanRunRepository    MarkInterrupted at startup.
        //
        // The container therefore does not own these seven and never disposes them. That leaks
        // nothing today: verified type by type, none of the seven implements IDisposable, so none
        // has a Dispose path to be dead. Item 6's original wording said otherwise and was wrong
        // about these types; the real undisposed resources it was pointing at were the two
        // HttpClientHandler instances in the resolution stack below, and those are now owned by
        // the container through factory registrations.
        //
        // Adding a Dispose to any of these seven in future is therefore a decision, not an
        // omission, and it has exactly two honest resolutions: move that type's eager use out of
        // Build() so its registration can become a factory, or dispose it explicitly from
        // Program.Main beside host.Dispose(). Registering it as an instance and giving it a
        // Dispose is the third option and it is the dead path item 6 describes. This comment is
        // the record of that decision so a future reader does not have to redo the census, and
        // AppHost_RegistersNoInstanceThatImplementsIDisposable is what enforces it.
        builder.Services.AddSingleton(appWriter);
        builder.Services.AddSingleton(levelSwitch);
        builder.Services.AddSingleton(ringBuffer);
        builder.Services.AddSingleton(settingsRepository);
        builder.Services.AddSingleton(settingsStore);
        // Phase review item 4: a factory registration, not an instance. The DI container disposes
        // only the singletons it constructed, so registering the object itself left AliasMapCache's
        // unsubscribe as dead code.
        builder.Services.AddSingleton(_ => new AliasMapCache(settingsStore));

        // Catalog seeding runs here, unconditionally, on every Build() call for both the GUI
        // and the CLI (design-spec 4.3; coordinator ruling Q10: permanent, not a Phase-3-only
        // shim). LoadIfNeeded is a no-op after the first successful run (Task 1's per-table
        // guard plus the general.catalogs_loaded_version flag), so there is no repeated cost.
        var catalogsDirectory = StaticCatalogLoader.ResolveCatalogsDirectory();
        var catalogSeeder = new CatalogSeeder(connectionString, settingsStore);
        catalogSeeder.LoadIfNeeded(catalogsDirectory);

        // Spec 5.12: activity_events is pruned to general.activity_retention_days on
        // application start as well as after each scan (ScanCoordinator does the latter).
        // Unconditional for both the GUI and the CLI: it is a database-only delete with no
        // console output, so there is nothing for cliMode to protect.
        var activityRepository = new ActivityRepository(connectionString);
        var prunedAtStartup = activityRepository.PruneRetention(initialGeneral.ActivityRetentionDays);
        if (prunedAtStartup > 0)
        {
            Log.Information("Activity log pruned at startup: {Deleted} rows removed", prunedAtStartup);
        }
        // Spec 10.9's data_root_moved row (Phase 10 Task 9). Written here because the database it
        // goes into is the one the relocation just produced, and the migration above has run. A
        // failed relocation writes no event: the database it would reach is the old one, and the
        // failure is already on the Storage tab and in the log.
        if (relocation.Moved)
        {
            activityRepository.EmitStandalone(
                category: "system", severity: "info", eventType: "data_root_moved",
                message: $"Moved GalactiLog's data from {relocation.From} to {relocation.To}",
                // snake_case, like every other details document in this solution.
                details: new
                {
                    from = relocation.From,
                    to = relocation.To,
                    files_copied = relocation.FilesCopied,
                    bytes_copied = relocation.BytesCopied,
                });
        }

        // Census exception 6 of 7; see the comment block above the appWriter registration.
        builder.Services.AddSingleton(activityRepository);

        // Review item 3. A crash, a kill, or a shutdown drain that ran out of budget leaves
        // scan_runs rows at "running" and nothing else ever closes them. Reconciled here,
        // next to the retention prune and before anything can start a new scan, for both the
        // GUI and the CLI. Database rows only, like the prune above.
        var scanRunRepository = new ScanRunRepository(connectionString);
        var interruptedRuns = scanRunRepository.MarkInterrupted();
        if (interruptedRuns > 0)
        {
            Log.Warning(
                "Reconciled {Count} scan run(s) left running by a previous process; marked interrupted",
                interruptedRuns);
        }

        // FIXER LIST 5: the connection string is a registered value, not an expression a
        // second component re-derives from the AppWriter. A factory rather than the value
        // itself (Task 8): nothing in Build() reads it back through the container, so there is
        // no reason for this to be one of the seven exceptions above.
        builder.Services.AddSingleton(_ => new DatabaseConnectionString(connectionString));

        // Dashboard queries (Phase 5 Task 2). Stateless and read-only, so one instance each;
        // both take the registered connection string and the registered alias map cache rather
        // than rebuilding either (FIXER LIST 2).
        builder.Services.AddSingleton(serviceProvider => new TargetListingQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>(),
            serviceProvider.GetRequiredService<AliasMapCache>()));
        builder.Services.AddSingleton(serviceProvider => new DashboardFacetsQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>(),
            serviceProvider.GetRequiredService<AliasMapCache>()));
        // Spec 12.7's discovered-name lists (Phase 9 Task 7). Deliberately not an alias-cache
        // consumer, unlike the two queries above: it returns raw names with no LIGHT filter and
        // no alias folding (questions.md Q25), so the Filters and Equipment tabs fold them in C#
        // against the current alias map themselves.
        builder.Services.AddSingleton(serviceProvider => new DiscoveredNamesQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        // FITS header key combo box (Phase 5 Task 3). Read-only over the registered connection
        // string, like the two queries above (FIXER LIST 2).
        builder.Services.AddSingleton(serviceProvider => new DistinctHeaderKeysQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        // Dashboard search (Phase 5 Task 8), same shape: read-only over the registered connection
        // string. The trigram scoring is in C# (spec 9.7), so the query needs nothing else.
        builder.Services.AddSingleton(serviceProvider => new TargetSearchQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        // Target detail (Phase 6 Task 1). Same shape as the listing query: read-only over the
        // registered connection string and the registered alias map cache.
        builder.Services.AddSingleton(serviceProvider => new TargetDetailQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>(),
            serviceProvider.GetRequiredService<AliasMapCache>(),
            // The live profile map, read per call, exactly as Phd2NightQuery is given it below.
            // Phase 15B fixer item 33: the closed Guiding band's session count comes from this
            // query's per-night read, and it has to resolve each session's rig through the same
            // live map the band itself uses, or the closed count and the opened list disagree.
            // Omitted here, the count is right in every test that passes a map and silently zero
            // in the running application.
            () => settingsStore.GetGeneral().Phd2ProfileMap));
        // The library-wide frame-quality baselines the session insights grade against (Phase 6
        // Task 2). A singleton because it is a cache: registering it per call would rescan the
        // images table on every card expansion. Task 3 wires ScanStatusService.ScanFinished to
        // Invalidate(); nothing subscribes to ScanCoordinator directly.
        builder.Services.AddSingleton(serviceProvider => new RigBaselinesCache(
            serviceProvider.GetRequiredService<DatabaseConnectionString>(),
            serviceProvider.GetRequiredService<AliasMapCache>()));
        // Session detail (Phase 6 Task 2), issued when a card expands rather than up front.
        builder.Services.AddSingleton(serviceProvider => new SessionDetailQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>(),
            serviceProvider.GetRequiredService<AliasMapCache>(),
            serviceProvider.GetRequiredService<RigBaselinesCache>()));
        // Spec 12.13's Export for stacking (Phase 16 Task 2), issued once per page open and never
        // per night: it reads the selected nights' frame paths and builds the contamination index
        // over the whole catalogue in one round trip. The connection-string argument shape every
        // query above takes, minus the two caches it has no equipment or baseline work for.
        builder.Services.AddSingleton(serviceProvider => new WbppPathsQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        // Spec 12.4's Guiding section (Phase 15B Task 3), issued when the band opens rather than
        // up front. A singleton over the registered connection string and the registered alias map
        // cache, in the shape every query above takes: it opens a short-lived context per call, so
        // it is safe to share and is never registered per card.
        builder.Services.AddSingleton(serviceProvider => new Phd2NightQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>(),
            serviceProvider.GetRequiredService<AliasMapCache>(),
            // The live profile map, read per call: the stored telescope column is the map's answer
            // as it stood at ingest and nothing writes a newer one back (task5b-review.md P2-1).
            () => settingsStore.GetGeneral().Phd2ProfileMap));
        // The guide graph's frames read (Phase 15B Task 4b), one session per call and issued only
        // for a session the graph can draw. No alias map: nothing in it is keyed by equipment.
        builder.Services.AddSingleton(serviceProvider => new Phd2FramesQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        // Raw header panel (Phase 6 Task 6), issued once per frame row on that row's first
        // expansion, never up front: SessionDetailQuery deliberately omits raw_headers because it
        // is the largest column in the table and only one frame's worth is ever on screen.
        builder.Services.AddSingleton(serviceProvider => new FrameHeadersQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>(),
            serviceProvider.GetRequiredService<ILogger<FrameHeadersQuery>>()));
        // Statistics (Phase 9 Task 2). The query is stateless; the cache beside it is the memo
        // spec 12.5 asks for, so the page does not re-run a full-library pass on every
        // navigation. Both are singletons for that reason, and the three invalidation triggers
        // are wired in the ScanStatusService registration below.
        // Spec 12.5's Guiding section (Phase 15B Task 5a). Stateless like StatsQuery, and read
        // through it rather than by the page: the guiding figures ride inside the one cached
        // response, so a profile remap drops them with the rest of it.
        builder.Services.AddSingleton(serviceProvider => new GuidingStatsQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>(),
            serviceProvider.GetRequiredService<AliasMapCache>(),
            // The live profile map, read per call. StatsCache already drops on GeneralChanged,
            // which a profile map save raises, so the rebuild that follows a remap now reads the
            // new attribution instead of rebuilding from the stale column (task5b-review.md P2-1).
            () => settingsStore.GetGeneral().Phd2ProfileMap));
        builder.Services.AddSingleton(serviceProvider => new StatsQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>(),
            serviceProvider.GetRequiredService<AliasMapCache>(),
            serviceProvider.GetRequiredService<GuidingStatsQuery>()));
        builder.Services.AddSingleton(serviceProvider => new StatsCache(
            serviceProvider.GetRequiredService<StatsQuery>(),
            // Questions.md Q12: GalactiLog.Data cannot reference GalactiLog.App, so the thumbnail
            // cache's size arrives as a delegate and this is the one place it is bound, exactly
            // like ScanCoordinator's render delegate above. A read through AppWriter, which
            // resolves each subdirectory under the authorized cache root; never a walk of a scan
            // root and never a `du`.
            () => ThumbnailCacheBytes(appWriter)));
        // Spec 12.14's Analysis page (Phase 17 Task 4). Stateless like StatsQuery, with the memo
        // beside it; both are singletons for the reason the pair above are, and the cache rides the
        // one staleness handler below (ruling A6) rather than carrying a TTL of its own.
        builder.Services.AddSingleton(serviceProvider => new AnalysisQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>(),
            serviceProvider.GetRequiredService<AliasMapCache>(),
            // The equipment combination list is a projection of the response StatsCache already
            // memoizes, so the page's filter bar runs no second full-library pass.
            serviceProvider.GetRequiredService<StatsCache>(),
            // The live profile map, read once per call and never per row, the same delegate the
            // GuidingStatsQuery registration above passes and for the same reason (ruling A10).
            () => settingsStore.GetGeneral().Phd2ProfileMap));
        builder.Services.AddSingleton(serviceProvider => new AnalysisCache(
            serviceProvider.GetRequiredService<AnalysisQuery>()));
        // Phase 15B fixer F2, with items 30 and 38: the one process-level notification the open
        // pages follow after the memos above have been dropped. Beside the caches it speaks for.
        // The one handler that raises it is wired in the ScanStatusService registration below,
        // with the three invalidations.
        //
        // Built here and registered as the instance, rather than resolved from the provider
        // wherever it is needed, because the unsubscribe half runs from a page's Dispose and the
        // container disposes its singletons while it is itself being torn down: a
        // GetRequiredService there throws ObjectDisposedException out of Host.Dispose. It has no
        // dependency to resolve and nothing to dispose, so a captured instance costs nothing.
        var derivedDataNotifier = new DerivedDataNotifier();
        builder.Services.AddSingleton(derivedDataNotifier);

        // Resolution stack (Phase 3, Tasks 1-7): registered here so both the GUI and the
        // CLI's `resolve`/`scan` verbs share one set of instances per Build() call.
        //
        // Five factory registrations, not five eager locals (Task 8, coordinator ruling Q24).
        // REGISTRATION ORDER DOES NOT MATTER HERE, and this is worth stating because the block
        // used to build the stack eagerly, bottom up, and a reader will look for that order: a
        // factory is not invoked until the first resolve, which is after builder.Build(), so
        // TargetResolver's factory can ask for a CatalogCacheRepository whose own factory is
        // registered on a later line. The same rule the ScanCoordinator registration below has
        // relied on since Phase 8 for ThumbnailCache.
        //
        // What the conversion buys is ownership, which is the whole of item 6's real content.
        // Each client below builds its own HttpClientHandler when there is no override, and the
        // container disposes only the singletons it constructed, so the handler and the
        // HttpClient around it now go away with the host. Registered as instances they did not:
        // a process that built several hosts (the test suite builds one per fixture) leaked one
        // handler pair per host.
        //
        // Spec 12.8's per-process resolver hit and miss counters (Phase 10 Task 1). One instance
        // per host either way, so the Diagnostics page still reads the counters the scan writes;
        // what changed is that CatalogCacheRepository resolves it instead of capturing it.
        builder.Services.AddSingleton(_ => new ResolverCounters());
        builder.Services.AddSingleton(serviceProvider => new CatalogCacheRepository(
            connectionString,
            serviceProvider.GetRequiredService<ResolverCounters>(),
            retryWaitOverride));

        // OWNERSHIP, stated at the registration site rather than inferred (ruling Q25). A handler
        // this method creates is this client's to dispose, so ownsHandler is true. A handler the
        // caller supplied through httpHandlerOverride is NOT: the test seam passes ONE stub to
        // BOTH clients and keeps using it after the host is gone, so disposing it here would make
        // the other client's next request throw ObjectDisposedException, and it would present as
        // an unrelated intermittent failure in a different test class. ownsHandler defaults to
        // false for that reason; these two lines are the only places in the solution that pass
        // true.
        builder.Services.AddSingleton(_ => httpHandlerOverride is null
            ? new SimbadClient(new HttpClientHandler(), ownsHandler: true)
            : new SimbadClient(httpHandlerOverride, ownsHandler: false));
        builder.Services.AddSingleton(_ => httpHandlerOverride is null
            ? new SesameClient(new HttpClientHandler(), ownsHandler: true)
            : new SesameClient(httpHandlerOverride, ownsHandler: false));

        // Phase 21, spec 12.16. ONE HttpClient for both integration clients, owned by the
        // IntegrationHttpClient singleton below rather than the bare HttpClient type (fix-wave
        // finding), so the container disposes it with the host and nothing else can resolve it
        // and rewrite the Timeout both clients leave alone.
        builder.Services.AddSingleton(_ => httpHandlerOverride is null
            ? new IntegrationHttpClient(new HttpClient(IntegrationHttp.NewHandler(), disposeHandler: true))
            : new IntegrationHttpClient(new HttpClient(httpHandlerOverride, disposeHandler: false)));
        builder.Services.AddSingleton(serviceProvider => new NinaClient(
            serviceProvider.GetRequiredService<IntegrationHttpClient>().Http));
        builder.Services.AddSingleton(serviceProvider => new StellariumClient(
            serviceProvider.GetRequiredService<IntegrationHttpClient>().Http));

        // Spec 11.3: the Sky view's one host over the same client, behind the one
        // service that reads the survey switch before every fetch.
        builder.Services.AddSingleton(serviceProvider => new Hips2FitsClient(
            serviceProvider.GetRequiredService<IntegrationHttpClient>().Http));
        builder.Services.AddSingleton(serviceProvider => new SurveyImageService(
            serviceProvider.GetRequiredService<Hips2FitsClient>(),
            serviceProvider.GetRequiredService<AppWriter>(),
            serviceProvider.GetRequiredService<JobRegistry>(),
            () => serviceProvider.GetRequiredService<SettingsStore>().GetGeneral(),
            serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<SurveyImageService>()));

        builder.Services.AddSingleton(serviceProvider => new TargetResolver(
            connectionString,
            catalogsDirectory,
            serviceProvider.GetRequiredService<CatalogCacheRepository>(),
            serviceProvider.GetRequiredService<SimbadClient>(),
            serviceProvider.GetRequiredService<SesameClient>()));

        // Scan pipeline (Phase 4). The concrete type, with no interface (coordinator ruling
        // Q4): ScanCoordinator lives in GalactiLog.Data.Ingest and has no Avalonia
        // dependency, so the CLI dispatcher and the App view models resolve the same
        // singleton without a seam. Registered through a factory rather than a manual `new`
        // so it gets the host's real ILogger<ScanCoordinator>, which does not exist until
        // builder.Build().
        // Census exception 7 of 7; see the comment block above the appWriter registration.
        builder.Services.AddSingleton(scanRunRepository);
        builder.Services.AddSingleton(serviceProvider => new ScanCoordinator(
            connectionString,
            settingsStore,
            // Resolved, never captured (Task 8): the resolver is a factory registration now, so
            // capturing a local here would be capturing nothing.
            serviceProvider.GetRequiredService<TargetResolver>(),
            serviceProvider.GetRequiredService<ScanRunRepository>(),
            serviceProvider.GetRequiredService<ILogger<ScanCoordinator>>(),
            // Spec 11.4's reference thumbnail render (Phase 8 Task 6). GalactiLog.Data cannot
            // reference GalactiLog.App, so the coordinator takes the render as a delegate and this
            // is the one place it is bound. Resolved inside the lambda, so registration order does
            // not matter: ThumbnailCache is registered further down this method.
            (targetId, framePath, force, cancellationToken) => serviceProvider
                .GetRequiredService<ThumbnailCache>()
                .EnsureReference(targetId, framePath, force, cancellationToken)));

        // Watcher and scheduler (Phase 4 Task 7). Constructed here, started nowhere in this
        // file: App.axaml.cs starts them once the GUI window exists, which is how spec 15's
        // "the CLI never starts the watcher or the scheduler" stays structural -- the CLI
        // branch of Program.Main returns before Avalonia is ever built.
        //
        // Both take delegates bound to the coordinator rather than the coordinator itself,
        // so neither App-layer service has to know about ScanTrigger or ScanRunOutcome.
        builder.Services.AddSingleton(serviceProvider =>
        {
            var coordinator = serviceProvider.GetRequiredService<ScanCoordinator>();
            var watcher = new WatcherService(
                settingsStore,
                (files, token) => coordinator.RunTargetedAsync(ScanTrigger.Watcher, files, token),
                token => coordinator.RunAsync(ScanTrigger.Watcher, null, token),
                serviceProvider.GetRequiredService<ILogger<WatcherService>>());

            // Phase 9 Task 5 (questions.md Q36): the watcher binds its root set at Start(), and
            // Phase 9 is the first phase in which the running process can write general.scan_roots
            // or general.watcher_enabled (the Settings Library tab, and the setup wizard). Without
            // this the watcher watches the wrong set until the next application start.
            //
            // Wired beside the ScanFinished subscription in the scheduler registration below
            // rather than beside the two GeneralChanged handlers further up, because this one
            // needs the instance: subscribing here means an application that never resolves the
            // watcher (the CLI) never subscribes either. The service compares the root set and
            // the enabled flag on the raising thread and posts the restart to the pool, so an
            // unrelated general save costs one set comparison.
            watcher.FollowSettingsChanges();
            return watcher;
        });
        builder.Services.AddSingleton(serviceProvider =>
        {
            var coordinator = serviceProvider.GetRequiredService<ScanCoordinator>();
            var scheduler = new ScanScheduler(
                settingsStore,
                () => coordinator.IsRunning,
                token => coordinator.RunAsync(ScanTrigger.Scheduler, null, token),
                serviceProvider.GetRequiredService<ILogger<ScanScheduler>>());

            // Spec 10.8's "the interval resets after any scan finishes, from any trigger":
            // ScanFinished fires for every run the coordinator completes, cancels or fails.
            coordinator.ScanFinished += (_, _) => scheduler.ResetInterval();
            return scheduler;
        });

        // Spec 12's job registry (PAR-015, ruling D1). One instance, process-wide, and the only
        // thing the status bar reads for its running-job count and flyout. Registered before the
        // three blocks that consume it and outside every one of them, because
        // AppHostTests.RegistrationBlock slices a block from its constructor call to the next
        // literal builder.Services. It owns no thread and no handle and is deliberately not
        // IDisposable, so the host's disposal order is unchanged.
        builder.Services.AddSingleton(_ => new JobRegistry());

        // Spec 12.7's out-of-scan correlation re-run (Phase 15A Task 6, ruling F5), census member
        // eleven. A factory, never an instance, and the type is deliberately not IDisposable, so
        // AppHost_RegistersNoInstanceThatImplementsIDisposable has nothing to say about it. Every
        // seam is a delegate resolved inside the lambda, so registration order does not matter and
        // the CLI, which never resolves it, builds none of it:
        //   - GetGeneral and AliasMapCache.Current are read at RUN time, not here: a re-run has to
        //     see the document the user just saved, which is the whole point of the pass.
        //   - TryBeginResolution is the coordinator's own lease, the same gate every Maintenance
        //     action takes, so a re-run cannot run beside a scan or beside another pass.
        builder.Services.AddSingleton(serviceProvider => new Phd2CorrelationRunner(
            connectionString,
            settingsStore.GetGeneral,
            () => serviceProvider.GetRequiredService<AliasMapCache>().Current,
            () => serviceProvider.GetRequiredService<ScanCoordinator>().TryBeginResolution(),
            jobs: serviceProvider.GetRequiredService<JobRegistry>(),
            logger: serviceProvider.GetRequiredService<ILogger<Phd2CorrelationRunner>>(),
            //   - clearCorrelationPending discharges spec 7.6's durable obligation, and only a
            //     pass that completed calls it. It is the store's own compare-and-clear, the ONE
            //     door in the solution that clears the flag: it takes the document the pass read
            //     at its start and clears only while the stored guiding inputs still equal it, so
            //     a save that landed mid-pass leaves the obligation standing for the coalesced
            //     second pass (fix-wave review P1-1). The scan calls the same member directly.
            clearCorrelationPending: observed =>
                settingsStore.ClearCorrelationPendingIfUnchanged(observed)));

        // Scan progress marshalling and the status bar (Phase 5 Task 5). ScanStatusService is
        // the single App-layer subscriber to ScanCoordinator's ProgressChanged and
        // ScanFinished; it is IDisposable so the host disposes it on shutdown. StatusBarViewModel
        // takes coordinator.Cancel as a delegate rather than the coordinator itself, matching
        // WatcherService and ScanScheduler above.
        builder.Services.AddSingleton(serviceProvider =>
        {
            var scanStatus = new ScanStatusService(
                serviceProvider.GetRequiredService<ScanCoordinator>(),
                // F7: the guard around a throwing ScanFinished subscriber reports through the
                // host's logger, not the static Serilog one, so it is observable in a test.
                logger: serviceProvider.GetRequiredService<ILogger<ScanStatusService>>(),
                // Spec 12 (PAR-015): the scan registers here, because this is the one App-layer
                // type that sees every progress envelope and every finish. Named, not positional,
                // for the reason the StatusBarViewModel block below gives.
                jobs: serviceProvider.GetRequiredService<JobRegistry>());

            // Phase 6 Task 3 (ruling Q7): the library-wide frame-quality baselines are stale
            // after a scan, and this is the one place the invalidation is wired. Through
            // ScanStatusService, never ScanCoordinator, which stays that service's own
            // subscription. Resolved inside the handler so the cache is not built eagerly at
            // startup.
            scanStatus.ScanFinished += (_, _) =>
                serviceProvider.GetRequiredService<RigBaselinesCache>().Invalidate();

            // FIXER LIST F5, wired beside the ScanFinished subscription because it is the same
            // cache and the same reason: the baselines are keyed on canonical telescope, camera
            // and filter names, so an edit to the filter or equipment aliases regroups every
            // baseline without a single frame changing. AliasMapCache invalidates itself on this
            // event; this cache folds raw names through that map at Load time and would otherwise
            // keep grouping by the old names for up to its five minute TTL.
            settingsStore.AliasSourcesChanged += (_, _) =>
                serviceProvider.GetRequiredService<RigBaselinesCache>().Invalidate();

            // Spec 12.5's "cached in memory until the next scan completes or settings change" and
            // spec 12.14's Analysis memo under exactly the same rule. Neither cache has a TTL,
            // precisely because all three triggers are explicit and in-process: the two below, and
            // the settings save, which reaches both through InvalidateDerivedCaches.
            //
            // ONE handler drops BOTH (design lesson 2, and the phase 17 review's P1-A). Two
            // handlers per event is how the Analysis memo came to be wired to none of them while
            // the Statistics memo was wired to both: after the commonest event there is, a scan
            // that adds a night, the Analysis page served its pre-scan figures even for a filter
            // combination the reader had already asked for. With one handler a fourth trigger
            // cannot reach one page's memo and be forgotten on the other, and
            // AppHostTests pins the two counts against each other so it cannot be split again.
            //
            // Both caches are resolved inside the handler, like the lines above, so an application
            // that opens neither page builds neither.
            void InvalidateOpenPageMemos()
            {
                serviceProvider.GetRequiredService<StatsCache>().Invalidate();
                serviceProvider.GetRequiredService<AnalysisCache>().Invalidate();
            }

            scanStatus.ScanFinished += (_, _) => InvalidateOpenPageMemos();
            settingsStore.AliasSourcesChanged += (_, _) => InvalidateOpenPageMemos();

            // Phase 15B fixer item 34, wired beside the lines above and resolved inside the
            // handler for their reason. Phd2NightQuery memoizes the one "does this library carry
            // any guide log at all" EXISTS that decides whether the Guiding band is built (spec
            // 12.4), so a scan that catalogues a library's FIRST guide log has to drop it before
            // the page rebuild that same scan raises. Ordering is what makes that true, and it is
            // structural rather than lucky: this subscription is made when ScanStatusService is
            // first resolved, which is before any page that takes ScanStatusService exists, so it
            // stands ahead of every page's own ScanFinished handler. The settings and maintenance
            // paths reach the same reset through InvalidateDerivedCaches.
            scanStatus.ScanFinished += (_, _) =>
                serviceProvider.GetRequiredService<Phd2NightQuery>().InvalidateGuideLogMemo();

            // Phase 15B fixer F2, with items 30 and 38. ONE handler and ONE mechanism for
            // "the derived data an open page is showing has been rewritten", subscribed to the two
            // sources that can do it without a scan, and in this order: drop the memos, then tell
            // the pages, so a page that answers re-reads a rebuilt response rather than the one it
            // already held. Design-lessons rule 2: the staleness rule lives at this choke point
            // and not as a check each page has to remember.
            //
            // GeneralChanged is the first source, and it subsumes the StatsCache-only handler that
            // stood here before: the observer coordinates the timeline's efficiency labels need,
            // the scan roots the library is built from (spec 8.4) and the PHD2 profile map the
            // guiding figures are attributed through all live in the general document, and
            // InvalidateDerivedCaches drops StatsCache along with the rest.
            //
            // PassCompleted is the second (task5b-review.md P3-10): the settings save queues a
            // correlation re-run, and the pass that follows rewrites images.guiding_rms_arcsec long
            // after the save's own notification has been answered. Only a pass that ran to
            // completion raises it. The runner is resolved here rather than inside the handler,
            // unlike the caches, because a subscription needs the instance; this block is the
            // GUI-startup path, which already resolves the runner a few lines below, and the CLI
            // returns before Avalonia is built so it subscribes nothing.
            void RaiseDerivedDataChanged()
            {
                InvalidateDerivedCaches(serviceProvider);
                derivedDataNotifier.Raise();
            }

            // Phase 16 phase review P2-2, coordinator ruling 1. The general side FAILS CLOSED: a
            // save invalidates unless the document it wrote differs from the last one this handler
            // saw ONLY in keys on the inert list, which OnlyInertKeysMoved holds and which is
            // exactly spec 5.8.1's four wbpp_ keys today. Deliberately not the other shape, a list
            // of the inputs the derived data is built from: that one fails open the day an input is
            // added, while this one can only fail open if a key is deliberately put on the inert
            // list. A key this build does not recognise rides in GeneralSettings.ExtensionData and
            // is therefore compared like every other one.
            //
            // The "before" side is the last document this handler observed, because GeneralChanged
            // carries the document that was written and nothing else. Seeded from the store when
            // the subscription is made, so the first save of a session is judged like every other
            // one rather than invalidating on principle. The compare-and-swap takes a gate because
            // the event is raised outside the store's write gate, so two saves can reach this at
            // once; the raise itself stays outside it, like every other subscriber's work. Two
            // arrivals out of order can cost one extra invalidation and can never lose one, since
            // each is compared against the most recent document seen.
            //
            // The seeding read is GUARDED for the reason the correlation read below is, and it was
            // an unguarded one that ReadStoredAppLogRetentionDays_ClampsToTheKeysDeclaredRange
            // caught: this factory is on the startup path, and a hand-edited general document that
            // will not deserialize must not be able to stop the window appearing. A seed that
            // failed leaves the field null, which the handler reads as "nothing to compare
            // against" and therefore invalidates, which is this predicate's fail-closed answer.
            Core.Settings.GeneralSettings? lastGeneral = null;
            try
            {
                lastGeneral = settingsStore.GetGeneral();
            }
            catch (Exception ex)
            {
                serviceProvider.GetRequiredService<ILogger<DerivedDataNotifier>>().LogWarning(
                    ex, "Could not read the general document at start; the next settings save " +
                    "will rebuild the derived data whatever it moved");
            }

            var stalenessGate = new Lock();

            settingsStore.GeneralChanged += (_, general) =>
            {
                bool inertOnly;
                lock (stalenessGate)
                {
                    inertOnly = lastGeneral is { } previous && OnlyInertKeysMoved(previous, general);
                    lastGeneral = general;
                }

                if (!inertOnly)
                {
                    RaiseDerivedDataChanged();
                }
            };

            serviceProvider.GetRequiredService<Phd2CorrelationRunner>().PassCompleted +=
                (_, _) => RaiseDerivedDataChanged();

            // Spec 12.15, ruling C22: ONE mechanism by which a custom column definition change
            // reaches the surfaces that are already open. The repository raises Changed after a
            // definition write and never after a value write, and this is its one route.
            //
            // Wired here, in the block that is only built at GUI startup, for the reason the three
            // subscriptions above give: the CLI returns before Avalonia exists and subscribes
            // nothing. The dashboard is resolved inside the handler, not at subscription time, so
            // this line does not decide when the page is built.
            //
            // POSTED. The repository raises on whichever thread wrote, which is a pool thread for
            // every write the Custom Columns tab makes, and the refresh touches collections the UI
            // is bound to. The two events routed into RaiseDerivedDataChanged above are already
            // marshalled by their own raisers, so copying their wiring verbatim would be wrong here.
            //
            // NOT RaiseDerivedDataChanged, deliberately (ruling C32): that local function drops
            // StatsCache and AnalysisCache first, so renaming a custom column would make the
            // Statistics and Analysis pages re-run a full library pass for data neither of them
            // reads. The dashboard is the one open surface that needs telling; the Settings tabs
            // subscribe to the same event through their own constructors, so an unvisited tab is
            // never built by this route.
            serviceProvider.GetRequiredService<CustomColumnRepository>().Changed += (_, _) =>
                UiPost.Default(() =>
                    serviceProvider.GetRequiredService<DashboardViewModel>().RefreshCustomColumns());

            // Spec 12.7's re-run (Phase 15A Task 6). THE ONE TRIGGER PATH: the store's own event,
            // which fires at most once per save and only when the normalised profile map, the
            // observer timezone, the observer latitude or the observer longitude really moved.
            // Nothing on the PHD2 profiles panel dispatches the re-run; a second trigger there
            // would run one save's correlation twice, and the store is the choke point every
            // writer of the general document already passes through, including the Location tab
            // and the equipment rename rewrite (design-lessons rule 2).
            //
            // Wired here, beside the three subscriptions above, for the reason they give: the
            // handler resolves the runner lazily, so an application that never opens a window
            // never builds one, and ScanStatusService is resolved once at GUI startup so the
            // subscription is live for the whole session. The CLI, which returns before Avalonia
            // is built, subscribes nothing.
            settingsStore.Phd2GuidingInputsChanged += (_, _) =>
                serviceProvider.GetRequiredService<Phd2CorrelationRunner>().Queue();

            // Spec 12.11 item 11a and spec 7.6's "The obligation survives a crash": a re-run a
            // settings save queued and that never finished left general.phd2_correlation_pending
            // true, and this is what picks it up again. ONE pass, and only when the flag is set:
            // InvalidatedNights on a real corpus is the whole guide-log history, so a pass at every
            // start would make every launch pay for it. Here rather than in the runner, beside the
            // subscription and for the same reason, which is also what keeps it off the CLI path:
            // this factory is resolved at GUI startup and the CLI returns before Avalonia is built.
            // It does not block the window: Queue returns at once and the pump is a pool task.
            //
            // The READ is guarded, and deliberately not by giving SettingsStore.Deserialize<T> the
            // catch it has never had: that is carried item 39 with an owner of its own, and this is
            // a new READER of the document rather than a change to the one door into it. A
            // hand-edited general document that will not deserialize must not be able to stop the
            // window appearing over a guiding column, and the obligation stays recorded either way,
            // so the next start takes it.
            //
            // The guard covers the read ALONE (fix-wave review P3-2). It used to wrap the DI
            // resolve and Queue() too, so a misconfigured registration or a throwing Queue would
            // have been swallowed into the same warning as a malformed settings document, which is
            // a real fault reported as a cosmetic one.
            var correlationOwed = false;
            try
            {
                correlationOwed = settingsStore.GetGeneral().Phd2CorrelationPending;
            }
            catch (Exception ex)
            {
                serviceProvider.GetRequiredService<ILogger<Phd2CorrelationRunner>>().LogWarning(
                    ex, "Could not read general.phd2_correlation_pending at start; no PHD2 " +
                    "correlation re-run was queued");
            }

            if (correlationOwed)
            {
                serviceProvider.GetRequiredService<Phd2CorrelationRunner>().Queue();
            }

            return scanStatus;
        });
        builder.Services.AddSingleton(serviceProvider => new StatusBarViewModel(
            serviceProvider.GetRequiredService<ScanStatusService>(),
            serviceProvider.GetRequiredService<ScanCoordinator>().Cancel,
            // Phase 7 FIXER item 19: the same manual-scan lambda the dashboard's empty-state
            // button uses, so a library with frames in it still has a rescan affordance.
            token => serviceProvider
                .GetRequiredService<ScanCoordinator>()
                .RunAsync(ScanTrigger.Manual, null, token),
            serviceProvider.GetRequiredService<ILogger<StatusBarViewModel>>(),
            // Spec 12's update indicator (Phase 10 Task 4), a trailing optional parameter so no
            // other construction site of this view-model changed. Named, not positional, because
            // dropping it compiles: AppHostTests.AppHost_BindsEveryOptionalPhase10Seam is what
            // fails then (phase review Important P1).
            updates: serviceProvider.GetRequiredService<UpdateService>(),
            // Spec 12's job monitor (PAR-015, ruling D1), beside the update indicator and for the
            // same reason: a trailing optional parameter, named rather than positional, so dropping
            // it fails a test instead of compiling.
            jobs: serviceProvider.GetRequiredService<JobRegistry>()));

        // Spec 12.11's residency owner and notification-area icon (Phase 11 Task 2). Factories,
        // like every other Phase 11 registration; neither is resolved until
        // App.OnFrameworkInitializationCompleted asks for it, so the CLI branch of Program.Main,
        // which returns before Avalonia is built, reaches neither (spec 15).
        //
        // The residency service takes SettingsStore.GetGeneral as a delegate rather than the
        // store, and it is read live on every close: the General tab writes close_to_tray while
        // the window is open and the next close has to honour it. requestShutdown is bound to the
        // desktop lifetime's TryShutdown, never to Shutdown, because only TryShutdown is
        // documented to raise ShutdownRequested and ShutdownRequested is where the one drain
        // lives. Left unbound on a lifetime that is not the classic desktop one, which is what a
        // test surface gets.
        // Spec 12.11 behaviour 9 (Phase 11 Task 4). The resolved answer to "did this process come
        // up with no window", composed once, here, because this is the one place that holds both
        // halves of the rule: the switch Program.Main parsed into App.StartupArguments before this
        // method was called, and the general document this method already read at the top. No
        // second settings read reaches the startup path (phase review item 2).
        //
        // App.ShouldShowWindowAtStartup is the one owner of the rule and this is its negation,
        // because "started minimized" is the fact spec 12.8's field answers: whether the process
        // came up with no window, not which of the two inputs asked for it. A factory
        // registration, like every other Phase 11 one, so the instance-registration census above
        // is unchanged. The CLI path builds this too and never resolves it: the CLI branch of
        // Program.Main returns before Avalonia is built (spec 15).
        // Read once, here, and closed over by value. Not read inside the factory:
        // App.StartupArguments is a mutable static, and a factory that read it at resolve time would
        // answer with whatever the static held long after Build finished composing everything
        // else, which in a test run is whatever the previous case left there (Task 4 review,
        // minor finding on AppHost.cs).
        var startedMinimized =
            !App.ShouldShowWindowAtStartup(App.StartupArguments?.Minimized == true, initialGeneral);
        builder.Services.AddSingleton(_ => new StartupState(startedMinimized));

        // Ruling Q13's "a stored id wins", at the one moment it has to be true: the id the document
        // held at startup, read once above with every other settings document and carried by value,
        // so App.OnFrameworkInitializationCompleted can hand it to ThemeManager.ApplyStored on the
        // UI thread without opening a SQLite context in front of the first frame. The CLI path
        // builds this too and never resolves it, exactly like StartupState above.
        builder.Services.AddSingleton(_ => new Theme.StartupTheme(initialGeneral.Theme));

        builder.Services.AddSingleton(serviceProvider => new WindowResidencyService(
            settingsStore.GetGeneral,
            requestShutdown: () =>
                Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                && desktop.TryShutdown(),
            logger: serviceProvider.GetRequiredService<ILogger<WindowResidencyService>>()));

        // Spec 12.11 behaviour 4's menu. Scan now is StatusBarViewModel.RunScanCommand itself and
        // Check for updates is UpdateService.CheckNowAsync behind UpdateService.CanCheckNow, so
        // the tray adds no second run-scan guard and no second update-check guard
        // (design-lessons rule 1).
        builder.Services.AddSingleton(serviceProvider => new TrayIconViewModel(
            serviceProvider.GetRequiredService<StatusBarViewModel>(),
            serviceProvider.GetRequiredService<WindowResidencyService>(),
            updates: serviceProvider.GetRequiredService<UpdateService>(),
            logger: serviceProvider.GetRequiredService<ILogger<TrayIconViewModel>>()));

        // Spec 12.11 behaviour 10's notice (Phase 11 Task 5). Two factories, like every other
        // Phase 11 registration: nothing here is resolved until App.axaml.cs starts the watcher,
        // so the CLI branch of Program.Main, which returns before Avalonia is built, structurally
        // never notifies (spec 15).
        //
        // The notifier is the one seam behind which the mechanism lives (coordinator ruling Q6).
        // This version writes the tray tooltip, which needs no package, no P/Invoke and no
        // Windows-flavoured TargetFramework; the toast route is spec 12.11 behaviour 10's
        // recorded upgrade path and it replaces this one registration and one file.
        builder.Services.AddSingleton<IScanCompletionNotifier>(
            serviceProvider => new TrayTooltipScanNotifier(
                serviceProvider.GetRequiredService<TrayIconViewModel>(),
                serviceProvider.GetRequiredService<ILogger<TrayTooltipScanNotifier>>()));

        // Delegates rather than the store and the repository, so the watcher constructs in a unit
        // test with lambdas and no database (design-spec 18.3). readRecentRuns is the method group
        // ScanRunRepository.Recent, which is the shape DiagnosticsService's registration uses.
        // Recent rather than Latest (fix round 1, review important finding): the newest row is not
        // always the run that just finished, because ScanCoordinator starts its pending follow-up
        // before raising the event and a galactilog scan beside the window writes its own running
        // row (spec 12.11 behaviour 2), so the watcher takes the newest terminal row instead.
        builder.Services.AddSingleton(serviceProvider => new ScanCompletionWatcher(
            serviceProvider.GetRequiredService<ScanStatusService>(),
            serviceProvider.GetRequiredService<WindowResidencyService>(),
            settingsStore.GetGeneral,
            scanRunRepository.Recent,
            serviceProvider.GetRequiredService<IScanCompletionNotifier>(),
            logger: serviceProvider.GetRequiredService<ILogger<ScanCompletionWatcher>>()));

        // Shell view-models (Phase 5 Task 4). DashboardViewModel is a singleton because
        // coordinator ruling Q4 reads design-spec 12.2's "persist for the session" as the
        // process lifetime on a desktop application: navigating away from the dashboard and
        // back keeps every filter, and nothing is written to disk to achieve that. Registered
        // by type only while it stays parameterless; Tasks 6 to 8 turn it into an explicit
        // factory registration like the others above.
        //
        // MainWindowViewModel takes the general settings by value, not the store, so it
        // constructs in a unit test with no database (design-spec 18.3).
        //
        // Phase 5 Task 6 turned it into that factory: the dashboard takes delegates bound to the
        // three registered query objects and to the alias map cache, never the objects
        // themselves, so it constructs in a unit test with lambdas and no database. The debounce
        // delay is left at its default (Task.Delay); only tests inject one.
        // The one writer of display.columns (design-spec 5.8.2), Phase 6 Task 5. Two tables
        // persist their column lists, the dashboard target list and every Target detail frame
        // table, and each write is a load-modify-save of the whole display document: one chain
        // per writer would let two clicks interleave and drop one. Method groups rather than the
        // store itself, for the reason every other delegate here is one (design-spec 18.3).
        builder.Services.AddSingleton(serviceProvider => new DisplayColumnWriter(
            settingsStore.GetDisplay,
            settingsStore.SaveDisplay,
            serviceProvider.GetRequiredService<ILogger<DisplayColumnWriter>>()));

        // P13 R5's display.target_page (phase review P2-1). A singleton for the reason
        // ChartSelectionViewModel is one: the document holds one value per key, so the process must
        // hold one too, or a toggle on one night's card is invisible on the next night and a page
        // built after it is seeded from the startup snapshot and shows the old value until a
        // relaunch. Seeded once from initialDisplay and writing through the one chain over the
        // display document above.
        builder.Services.AddSingleton(serviceProvider => new TargetPageState(
            initialDisplay.TargetPage,
            serviceProvider.GetRequiredService<DisplayColumnWriter>().Write));

        builder.Services.AddSingleton(serviceProvider =>
        {
            var dashboard = new DashboardViewModel(
            serviceProvider.GetRequiredService<TargetListingQuery>().List,
            serviceProvider.GetRequiredService<DashboardFacetsQuery>().Load,
            () => serviceProvider.GetRequiredService<DistinctHeaderKeysQuery>().Load(),
            () => serviceProvider.GetRequiredService<AliasMapCache>().Current,
            initialGeneral,
            delay: null,
            // Phase review item 2: the column list by value, read above on Program.Main's thread.
            initialDisplay,
            // Phase 5 Task 7: column visibility (design-spec 5.8.2) is the dashboard's only
            // persisted state, and it round-trips through the settings store, never a file. The
            // two delegates are unused in the application now that the writer below is passed;
            // they stay bound so a null writer would still behave, and so the signature keeps
            // documenting what the writer is built from (design-spec 18.3).
            settingsStore.GetDisplay,
            settingsStore.SaveDisplay,
            serviceProvider.GetRequiredService<ScanStatusService>(),
            post: null,
            // Phase 5 Task 8. The search query and the Run Scan trigger are delegates for the
            // same reason every other one here is: the view-model constructs in a unit test with
            // lambdas and no database and no coordinator (design-spec 18.3).
            search: term => serviceProvider.GetRequiredService<TargetSearchQuery>().Search(term),
            // Spec 12.10's unreachable-root banner. The reachability test lives here, not in the
            // view-model, so a test can fake an unreachable root without creating or deleting a
            // directory. UserFiles.DirectoryExists is a read: this application never creates,
            // moves, deletes or modifies a user file or directory (TRACKING hard rule 1), and the
            // call runs on a background thread because a disconnected share can block for
            // seconds (ruling Q18).
            probeRoots: () =>
            {
                var roots = settingsStore.GetGeneral().ScanRoots;
                return [.. roots.Where(root => !UserFiles.DirectoryExists(root))];
            },
            startScan: token => serviceProvider
                .GetRequiredService<ScanCoordinator>()
                .RunAsync(ScanTrigger.Manual, null, token),
            // Phase 6 Task 5: the process-wide display.columns chain, shared with every frame
            // table, so a dashboard column click and a frame-table column click cannot interleave
            // into a lost update of the one display document. Given the writer, the two delegates
            // above go unused; they stay because the parameter is optional and a test that names
            // only them still gets a working dashboard.
            displayColumns: serviceProvider.GetRequiredService<DisplayColumnWriter>(),
            // Phase 14C Task 4, spec 5.8.1: the pager's page-size select writes
            // general.default_page_size through the one door into that document, MutateGeneral.
            // The GeneralChanged subscription just below already applies the key back to this
            // same singleton and returns early once the value already matches, which is what
            // keeps the round trip to one write (questions.md Q6).
            writeDefaultPageSize: size => settingsStore.MutateGeneral(general => general with { DefaultPageSize = size }),
            // Polish wave 1, ruling 1: the notice's Review action persists scan_filters_reviewed
            // through the same door, and the GeneralChanged subscription below keeps both
            // surfaces reading the one stored document.
            mutateGeneral: settingsStore.MutateGeneral,
            // F7: one logger for the page and the two view-models it constructs (the filter panel
            // and the target list are not DI-registered; the dashboard is their factory).
            logger: serviceProvider.GetRequiredService<ILogger<DashboardViewModel>>(),
            // Phase 20, spec 12.15: the filter panel's eighth section. Read on the panel's own
            // background reload beside the facets, never on the UI thread, and re-read after every
            // scan, so a column defined on the Settings tab appears here without a restart.
            loadCustomColumns: serviceProvider.GetRequiredService<CustomColumnRepository>().List,
            // Phase 20 Task 5a, spec 12.15: the dashboard cells. The page read is taken on the
            // listing query's own background hop; the per-target read is taken only when a row is
            // expanded (ruling C11); the write is the repository's one write path, invoked by each
            // cell off the UI thread.
            loadTargetValues: serviceProvider.GetRequiredService<CustomColumnRepository>().TargetValues,
            loadValuesForTarget: serviceProvider.GetRequiredService<CustomColumnRepository>().ValuesForTarget,
            writeCustomValue: serviceProvider.GetRequiredService<CustomColumnRepository>().SetValue);

            // Phase 9 FIXER item 5. TargetListViewModel seeds PageSize once at construction from
            // general.default_page_size and is a singleton underneath this page, so a page size
            // saved on the Settings Display tab would otherwise not take effect until a restart.
            // The whole fix is this subscription: no line of the view-model changes, because
            // PageSize is already a settable observable property whose change handler resets the
            // page and re-queries.
            settingsStore.GeneralChanged += (_, general) =>
                FollowDefaultPageSize(dashboard.Targets, general.DefaultPageSize, UiPost.Default);

            // Phase 14B Task 5, spec 12.2's scan filter notice (PAR-014). The same subscription
            // shape as the page size above, on the same event: saving a rule on the Library tab
            // takes the notice down on this page with no restart, from the one computed condition
            // both surfaces read.
            settingsStore.GeneralChanged += (_, general) => dashboard.FollowScanFilters(general);

            return dashboard;
        });
        // Target detail's three database writes (Phase 6 Task 3). A repository, not a query, so
        // it takes the registered connection string and opens its own tracking context per call,
        // like ScanRunRepository and ActivityRepository.
        builder.Services.AddSingleton(serviceProvider => new TargetWriteRepository(
            serviceProvider.GetRequiredService<DatabaseConnectionString>(),
            // Fix-wave review: the create form's partial-write sentence says "See the log for
            // details", and this is the logger that puts the line there.
            serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<TargetWriteRepository>()));
        // Merge and unmerge (Phase 7 Task 2). A named sibling of TargetWriteRepository, not an
        // extension of it: one transaction over five tables, and the only writer of
        // merged_into_id and merged_at.
        builder.Services.AddSingleton(serviceProvider => new MergeRepository(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        // Spec 12.15's one custom-column write path (Phase 20). Every validation sentence lives in
        // it and no surface carries a check of its own, so a fifth surface added later is
        // validated by default.
        builder.Services.AddSingleton(serviceProvider => new CustomColumnRepository(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        // The catalog re-enrichment writer (Phase 7 Task 7, FIXER LIST item 13). The third named
        // sibling, and with the two above the complete list of App-callable writers of targets
        // rows: rename and notes, merge and unmerge, catalog columns.
        builder.Services.AddSingleton(serviceProvider => new TargetEnrichmentRepository(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        // The candidate list read and its two status writes (Phase 7 Task 3). Same shapes as
        // above: a read-only query over the registered connection string, and a repository that
        // opens its own tracking context per call.
        builder.Services.AddSingleton(serviceProvider => new MergeCandidateQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        builder.Services.AddSingleton(serviceProvider => new MergeCandidateRepository(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        // The merge preview read (Phase 7 Task 4): both sides of spec 12.9's comparison, the
        // frames the merge would move, the colliding note dates, and the aliases the winner would
        // gain. Read-only; the merge itself stays MergeRepository's.
        builder.Services.AddSingleton(serviceProvider => new MergePreviewQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        // Spec 12.9's merge history read (Phase 7 Task 5): one row per merge_manifests row, with
        // an optional winner predicate, because the Settings tab lists every manifest and Target
        // detail lists one target's. Read-only; the undo itself stays MergeRepository's.
        builder.Services.AddSingleton(serviceProvider => new MergeHistoryQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        // Spec 9.7's unresolved-name list and spec 12.7's rename history (Phase 7 Task 6). Both
        // read-only. The rename history reads the target_renamed activity events
        // TargetWriteRepository.Rename writes; there is no rename_history table (questions.md
        // Q11).
        builder.Services.AddSingleton(serviceProvider => new UnresolvedNamesQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        builder.Services.AddSingleton(serviceProvider => new RenameHistoryQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        // Spec 12.6's activity feed read (Phase 9 Task 4): a keyset-paged page of top-level
        // activity_events plus the sub-events of the rows on it. Read-only; ActivityRepository
        // stays the one writer and the one deleter of that table (spec 10.9).
        builder.Services.AddSingleton(serviceProvider => new ActivityQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        // Spec 12.8's Diagnostics page (Phase 10 Task 1): the SQLite half of the snapshot, and
        // the one App-layer composer over it. The composer takes ScanStatusService, WatcherService
        // and ScanScheduler as factories because all three are registered further down this
        // method and the last two are built with delegates bound to ScanCoordinator; resolving
        // them inside the lambda makes registration order irrelevant, exactly as the
        // ScanCoordinator registration above does for ThumbnailCache.
        builder.Services.AddSingleton(serviceProvider => new DiagnosticsQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        // Spec 12.7's PHD2 profiles panel read (Phase 15A Task 6): one row per equipment profile
        // the guide logs themselves named, newest first. Read-only, on a connection closed with
        // the read, the same shape DiagnosticsQuery above has.
        builder.Services.AddSingleton(serviceProvider => new Phd2ProfilesQuery(
            serviceProvider.GetRequiredService<DatabaseConnectionString>()));
        builder.Services.AddSingleton(serviceProvider => new DiagnosticsService(
            serviceProvider.GetRequiredService<DiagnosticsQuery>(),
            serviceProvider.GetRequiredService<UnresolvedNamesQuery>(),
            serviceProvider.GetRequiredService<ScanRunRepository>(),
            serviceProvider.GetRequiredService<ResolverCounters>(),
            serviceProvider.GetRequiredService<LogRingBuffer>(),
            appWriter,
            // Already resolved once at the top of this method; the composer does not call
            // StaticCatalogLoader.ResolveCatalogsDirectory a second time.
            catalogsDirectory,
            serviceProvider.GetRequiredService<ScanStatusService>,
            serviceProvider.GetRequiredService<WatcherService>,
            serviceProvider.GetRequiredService<ScanScheduler>,
            // Spec 16.3's bundle (Phase 10 Task 3) reads three things the page's snapshot does
            // not carry. Each is the existing read: LogReader.All is Task 2's reader, ActivityQuery
            // is spec 12.6's feed at its cap, and the settings document is embedded verbatim from
            // the repository rather than from the deserialized SettingsStore shape.
            serviceProvider.GetRequiredService<LogReader>(),
            serviceProvider.GetRequiredService<ActivityQuery>(),
            settingsRepository,
            // Task 1 left these two as Func<string> seams defaulting to "unknown"; Phase 10 Task 4
            // binds them to the one reader of build identity, so spec 12.8's Versions group, spec
            // 12.7's About tab and spec 16.3's bundle cannot disagree about which build is
            // running. This is the whole of Task 4's change to that service: no line of
            // DiagnosticsService.cs moved.
            gitSha: () => serviceProvider.GetRequiredService<BuildInfo>().GitSha,
            channel: () => serviceProvider.GetRequiredService<BuildInfo>().Channel,
            // Spec 12.8's Paths group (Phase 10 Task 9): the word beside the path that says
            // whether it came from the user's own choice or from a stray environment variable.
            appDataSource: () => AppDataRootResolver.SourceLabel(resolution.Source),
            // Spec 12.8's Paths group, Startup shortcut field (Phase 11 Task 3). The seam is
            // resolved here, below its own registration further down this method, the same
            // pattern gitSha and channel already use for BuildInfo.
            startupShortcut: serviceProvider.GetRequiredService<IStartupShortcut>(),
            // Spec 12.8's Paths group, Started minimized field (Phase 11 Task 4). The one
            // StartupState composed above, so the field reports what actually happened rather
            // than what was asked for: a process that came up with no window reads "yes" whether
            // the --minimized argument or general.start_minimized caused it, which is the
            // question a support bundle is answering. No line of DiagnosticsService.cs moved.
            startedMinimized: () => serviceProvider.GetRequiredService<StartupState>().StartedMinimized));
        // Spec 9.7's retry action (Phase 7 Task 6): clear every negative cache row, re-run
        // resolution for each distinct unresolved OBJECT, assign the frames where it now
        // succeeds. One implementation for spec 12.7's two surfaces; Phase 9's Maintenance tab
        // calls this same service. The resolver arrives as a delegate with createIfMissing true,
        // because the retry creates targets: that is what "assigning frames where it now
        // succeeds" means.
        builder.Services.AddSingleton(serviceProvider => new UnresolvedRetry(
            serviceProvider.GetRequiredService<DatabaseConnectionString>().Value,
            serviceProvider.GetRequiredService<CatalogCacheRepository>(),
            (objectName, cancellationToken) => serviceProvider
                .GetRequiredService<TargetResolver>()
                .Resolve(objectName, createIfMissing: true, ct: cancellationToken),
            // The scan owns resolution and the negative cache for its whole run (spec 5.1, 9.6),
            // so the retry refuses while one is in flight, and holds the coordinator's resolution
            // lease for its own run so a scan started meanwhile is refused too (Phase 7 fixer
            // item 1). The coordinator itself, not ScanStatusService, which is a UI-thread mirror
            // that lags it by one post.
            () => serviceProvider.GetRequiredService<ScanCoordinator>().TryBeginResolution(),
            serviceProvider.GetRequiredService<ILogger<UnresolvedRetry>>()));

        // The three permitted interactions with a user file (spec 2.1): reveal, open-with, and
        // the clipboard. Nothing here opens, writes, moves or deletes anything.
        //
        // The clipboard belongs to a TopLevel, not to the Application, and no window exists
        // while this host is being built, so it is reached through a deferred delegate that is
        // evaluated at click time. A missing clipboard makes the copy action a no-op rather than
        // an exception.
        builder.Services.AddSingleton(serviceProvider => new ShellIntegration(
            text => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow?.Clipboard
                ?.SetTextAsync(text)
                ?? Task.CompletedTask,
            logger: serviceProvider.GetRequiredService<ILogger<ShellIntegration>>()));

        // Spec 11.3's thumbnail cache (Phase 8 Task 4). A factory registration, not an instance,
        // for the reason AliasMapCache's is one: the container disposes only the singletons it
        // constructed. It holds nothing disposable today, and registering it this way costs
        // nothing if it ever does.
        //
        // Spec 11.3's second eviction trigger, the one at application start, is not run here:
        // Build() runs on Program.Main's thread before any window exists, and enumerating a cache
        // directory that can hold thousands of files belongs on a Task.Run whose exception is
        // logged and swallowed (questions.md Q9). Task 5 adds that call in Program.Main;
        // EvictPreviews is public for it.
        builder.Services.AddSingleton(serviceProvider => new ThumbnailCache(
            appWriter,
            () => currentGeneral.Value,
            ThumbnailRenderer.Render,
            serviceProvider.GetRequiredService<ILogger<ThumbnailCache>>()));

        // Spec 11.4's bounded LIFO worker (Phase 8 Task 5). A factory registration for the reason
        // TRACKING section 6 item 6 requires one: it holds two background tasks and a
        // CancellationTokenSource, and the container disposes only the singletons it constructed.
        // App.DrainForShutdown disposes it ahead of the scan drain as well, so the process does
        // not exit with two full-frame decodes still running.
        builder.Services.AddSingleton(serviceProvider => new ThumbnailWorker(
            (path, ct) => serviceProvider.GetRequiredService<ThumbnailCache>().EnsureFrame(path, ct),
            (path, ct) => serviceProvider.GetRequiredService<ThumbnailCache>().EnsurePreview(path, ct),
            logger: serviceProvider.GetRequiredService<ILogger<ThumbnailWorker>>()));

        // The thumbnail slot factory (Phase 8 Task 5). A slot is per-surface and short-lived, so
        // it is a Func<>, like every other per-item view-model here. The kind is an argument
        // because spec 11.5's modal holds two slots for one frame: the general.thumbnail_width
        // thumbnail (default 800) it shows immediately and the preview it shows when that
        // finishes.
        builder.Services.AddSingleton<Func<string, ThumbnailKind, ThumbnailSlotViewModel>>(
            serviceProvider => (framePath, kind) => new ThumbnailSlotViewModel(
                framePath,
                serviceProvider.GetRequiredService<ThumbnailWorker>(),
                serviceProvider.GetRequiredService<ThumbnailCache>().ReadBytes,
                kind,
                logger: serviceProvider.GetRequiredService<ILogger<ThumbnailSlotViewModel>>()));

        // The reference thumbnail slot factory (Phase 8 Task 6), keyed on the STORED
        // cache-relative path rather than on a frame path: spec 11.4's pass has already generated
        // the file, so the slot loads it directly instead of joining the render queue.
        //
        // The frame path is empty because a target's header has none to offer: the pass chose the
        // frame, and the page never learns which one it was. LoadExisting's fallback therefore
        // leaves the placeholder showing when the cache has been relocated or emptied, and the
        // next scan regenerates the file (spec 11.3, 12.10).
        builder.Services.AddSingleton<Func<string, ThumbnailSlotViewModel>>(serviceProvider =>
            cacheRelativePath =>
            {
                var slot = new ThumbnailSlotViewModel(
                    framePath: "",
                    serviceProvider.GetRequiredService<ThumbnailWorker>(),
                    serviceProvider.GetRequiredService<ThumbnailCache>().ReadBytes,
                    logger: serviceProvider.GetRequiredService<ILogger<ThumbnailSlotViewModel>>());
                slot.LoadExisting(cacheRelativePath);
                return slot;
            });

        // The frame table factory (Phase 6 Task 5). One table per expanded session card, built
        // from the SessionDetail the card loaded, so it holds the frames it was handed and never
        // queries.
        //
        // display and general arrive by value for the reason the card's do (ruling Q13): the
        // metric-group gate is evaluated once at construction, and nothing in the running process
        // can change display.groups yet.
        //
        // openPreview opens spec 11.5's modal on the table's current Rows in their current sort
        // order, positioned at the clicked row (Phase 8 Task 7, ruling Q16). The frame table is
        // the only surface in this port that holds a frame list: the dashboard's rows are target
        // groups, so the spec's second origin does not exist here.
        //
        // Phase 6 Task 6: FrameHeadersQuery.Get is a delegate for the reason every other
        // collaborator here is one (design-spec 18.3), and it is issued from ToggleRawHeaders on
        // a row's first expansion, never from this factory.
        builder.Services.AddSingleton<Func<SessionDetail, FrameTableViewModel>>(
            serviceProvider => detail => new FrameTableViewModel(
                detail.Frames,
                initialDisplay,
                serviceProvider.GetRequiredService<DisplayColumnWriter>(),
                serviceProvider.GetRequiredService<ShellIntegration>(),
                // Fire and forget: the click handler cannot await a modal, and ModalHost catches
                // and logs everything a failed open can throw, so nothing escapes this discard.
                openPreview: (rows, index) =>
                    _ = serviceProvider.GetRequiredService<PreviewModalService>().ShowAsync(rows, index),
                // The live memo, not the startup snapshot: a table built after a timezone or
                // clock change on the Location tab formats its Time column with the new one.
                currentGeneral.Value,
                serviceProvider.GetRequiredService<FrameHeadersQuery>().Get,
                logger: serviceProvider.GetRequiredService<ILogger<FrameTableViewModel>>(),
                // Phase 9 FIXER item 7: initialDisplay above is a snapshot, and the Settings
                // Display tab is the first thing in the process that can write display.groups.
                // The table takes only the groups half from here; the columns half keeps arriving
                // through DisplayColumnWriter.Changed, so one column click (which raises both) is
                // never applied twice.
                subscribeDisplayChanged: handler => settingsStore.DisplayChanged += handler,
                unsubscribeDisplayChanged: handler => settingsStore.DisplayChanged -= handler,
                // Spec 12.4's "Compare to" baseline. The same process-wide holder every session
                // card reads, so a flip on one night's pane re-grades every live table and every
                // table built afterwards, rather than each table freezing the value it was built
                // with (P13 phase review P2-1).
                targetPage: serviceProvider.GetRequiredService<TargetPageState>(),
                // P25 R7: a merged night's rows carry their capture date in the Time cell.
                datePrefixed: detail.Nights.Count > 1));

        // The per-session chart factory (Phase 6 Task 8). One chart per expanded session card,
        // built from the SessionDetail the card loaded, so it holds the frames it was handed and
        // never queries. It takes the shared selection singleton: spec 5.8.3 is one document, so a
        // metric toggled here is also on in the cross-session chart.
        builder.Services.AddSingleton<Func<SessionDetail, SessionChartViewModel>>(
            serviceProvider => detail => new SessionChartViewModel(
                detail,
                serviceProvider.GetRequiredService<ChartSelectionViewModel>(),
                serviceProvider.GetRequiredService<ILogger<SessionChartViewModel>>()));

        // The session card factory (Phase 6 Task 4). One card per dated session (ruling Q5),
        // built by the detail page from the header it loaded: the notes writer is keyed on the
        // target id, which an obj: group does not have, and the expansion query on the header's
        // group key, which is the storage form (Task 1 handoff).
        //
        // display and general arrive by value, read above on Program.Main's thread, for the same
        // reason the dashboard's column list does (ruling Q13): nothing in the running process
        // can change display.groups yet, and Phase 9's Settings tab is the first writer.
        //
        // P25 R2: a non-null spec builds the merged review card, which reads through the spec's
        // merging reader and owns no notes field and no guiding section of its own.
        builder.Services.AddSingleton<Func<TargetHeaderBlock, SessionOverview, MergedCardSpec?, SessionCardViewModel>>(
            serviceProvider => (header, overview, spec) => new SessionCardViewModel(
                overview,
                header.GroupKey,
                spec?.GetDetail ?? serviceProvider.GetRequiredService<SessionDetailQuery>().Get,
                spec is null && header.TargetId is { } targetId
                    ? (sessionDate, notes) => serviceProvider
                        .GetRequiredService<TargetWriteRepository>()
                        .SaveSessionNotes(targetId, sessionDate, notes)
                    : null,
                // Task 5's frame table and Task 8's per-session chart, both built from the loaded
                // detail. Both seams stay typed object? on the card, which is what lets it dispose
                // either child through one rule (Task 4 handoff).
                serviceProvider.GetRequiredService<Func<SessionDetail, FrameTableViewModel>>(),
                serviceProvider.GetRequiredService<Func<SessionDetail, SessionChartViewModel>>(),
                initialDisplay,
                // The live memo, for the frame times and the night band (polish 2 ruling 2).
                currentGeneral.Value,
                logger: serviceProvider.GetRequiredService<ILogger<SessionCardViewModel>>(),
                // P12: the ledger row's filter dots resolve through the same lookup the chart
                // pills use, so a filter cannot be one colour in the ledger and another in the
                // chart. The selection is the process-wide singleton both charts already share.
                filterTint: serviceProvider.GetRequiredService<ChartSelectionViewModel>().FilterTint,
                // P13 R5: the two section flags are the holder's, not this card's, so every night
                // reads one value and the holder queues the write through the one process-wide
                // chain over the display document (phase review P2-1).
                targetPage: serviceProvider.GetRequiredService<TargetPageState>(),
                // P14A PAR-008: the night header's thumbnail strip, one box per rig. The already
                // registered slot factory with ThumbnailKind.Frame bound in, so a night's box is
                // the same cached file the frame table's own preview of that frame would use and
                // no second cache directory appears (questions.md Q4).
                createFrameThumbnail: framePath => serviceProvider
                    .GetRequiredService<Func<string, ThumbnailKind, ThumbnailSlotViewModel>>()(
                        framePath, ThumbnailKind.Frame),
                // Spec 12.4's Guiding section (Phase 15B). The night read, issued when the band
                // opens, and the library-wide EXISTS that decides whether the band is drawn at
                // all, which the card calls once in its own constructor.
                getGuiding: spec is null ? serviceProvider.GetRequiredService<Phd2NightQuery>().Get : null,
                anyGuideLogs: spec is null ? () => serviceProvider.GetRequiredService<Phd2NightQuery>().AnyGuideLogs() : null,
                getFrames: serviceProvider.GetRequiredService<Phd2FramesQuery>().Get,
                // Polish wave 2 ruling 1: the ledger row's filter order takes the same alias
                // fallback its dots' colours do.
                aliases: () => serviceProvider.GetRequiredService<AliasMapCache>().Current,
                nights: spec?.Nights,
                noteNights: spec?.NoteNights));

        // The detail page factory (Phase 6 Task 3). A delegate keyed on the dashboard group key,
        // registered as a delegate rather than as the view-model type because the page is
        // per-target and transient: the shell disposes the outgoing one on every navigation.
        // P14A Task 6 widened it with spec 12.4's optional night (PAR-018): the shell hands the
        // date straight to the page, which consumes it once on its first load.
        builder.Services.AddSingleton<Func<string, DateOnly?, TargetDetailViewModel>>(serviceProvider =>
            (groupKey, initialSessionDate) => new TargetDetailViewModel(
                groupKey,
                serviceProvider.GetRequiredService<TargetDetailQuery>().Get,
                serviceProvider
                    .GetRequiredService<Func<TargetHeaderBlock, SessionOverview, MergedCardSpec?, SessionCardViewModel>>(),
                serviceProvider.GetRequiredService<TargetWriteRepository>().Rename,
                serviceProvider.GetRequiredService<TargetWriteRepository>().SaveTargetNotes,
                // Ruling Q12's re-resolve, completed by FIXER LIST item 13: clear the resolver
                // negative-cache row for the primary name, read the identity the catalogues give
                // for it now (ResolveIdentity ignores the stored target, which is why Resolve
                // could only ever report Cache here), write that identity back through the
                // re-enrichment writer, and report what changed. The view-model wraps this call in
                // Task.Run, so it is written synchronously here.
                (targetId, primaryName, cancellationToken) =>
                {
                    serviceProvider
                        .GetRequiredService<CatalogCacheRepository>()
                        .ClearNegative("resolver", NameNormalizer.Normalize(primaryName));

                    var result = serviceProvider
                        .GetRequiredService<TargetResolver>()
                        .ResolveIdentity(primaryName, ct: cancellationToken);

                    if (result.Identity is null)
                    {
                        return Task.FromResult((false, $"{primaryName} still resolves to nothing."));
                    }

                    var enrichment = serviceProvider
                        .GetRequiredService<TargetEnrichmentRepository>()
                        .ReEnrich(targetId, result.Identity);

                    var outcome = enrichment.Outcome switch
                    {
                        EnrichmentOutcome.Enriched =>
                            $"{primaryName} resolved from {result.Stage}. Updated {string.Join(", ", enrichment.FieldsChanged)}.",
                        EnrichmentOutcome.Unchanged =>
                            $"{primaryName} resolved from {result.Stage}. Nothing changed.",
                        EnrichmentOutcome.Suppressed =>
                            $"{primaryName} is user-defined, so catalog enrichment is suppressed.",
                        EnrichmentOutcome.Conflict =>
                            $"{primaryName} resolved from {result.Stage}, but another target already carries that name.",
                        _ => "That target no longer exists.",
                    };

                    return Task.FromResult((enrichment.Outcome == EnrichmentOutcome.Enriched, outcome));
                },
                serviceProvider.GetRequiredService<ShellIntegration>(),
                // Phase 6 Task 8: the page owns the cross-session chart, which it builds over its
                // own card collection from this shared selection. The selection is the singleton,
                // never a second instance, or the two charts would disagree.
                serviceProvider.GetRequiredService<ChartSelectionViewModel>(),
                // Spec 12.4's merge action (Phase 7 Task 5): this target survives, and the
                // dialog's own search box chooses the loser. Task 4's MergePreview.Loser is
                // nullable for exactly this entry point.
                // ShowAsync returns spec 12.9's count sentence, or null when no merge happened
                // (Phase 7 FIXER item 17). This page only needs to know whether to reload; the
                // counts are shown on the Targets tab, which is where the candidate list they
                // describe lives.
                async targetId => await serviceProvider
                    .GetRequiredService<MergeDialogService>()
                    .ShowAsync(new MergeRequest(targetId, null, null, null))
                    .ConfigureAwait(true) is not null,
                // Ruling Q12: called only after Get returned null, so a merged-away key names its
                // winner instead of reading as a generic stale key. Get is unchanged.
                serviceProvider.GetRequiredService<TargetDetailQuery>().MergedInto,
                targetId => serviceProvider
                    .GetRequiredService<Func<Guid?, MergeHistoryViewModel>>()(targetId),
                scanStatus: serviceProvider.GetRequiredService<ScanStatusService>(),
                logger: serviceProvider.GetRequiredService<ILogger<TargetDetailViewModel>>(),
                // Spec 12.4's reference thumbnail (Phase 8 Task 6). The page owns the slot and
                // disposes it on reload and on close.
                createReferenceSlot: serviceProvider
                    .GetRequiredService<Func<string, ThumbnailSlotViewModel>>(),
                // P13 R7 and R5: the ledger's disclosure is the holder's, the same instance every
                // session card above reads, so a page opened after a collapse opens collapsed
                // without a relaunch (phase review P2-1). The holder queues its write through the
                // one process-wide chain over the display document, so a ledger toggle, a section
                // toggle and a column toggle cannot interleave into a lost update.
                targetPage: serviceProvider.GetRequiredService<TargetPageState>(),
                // Spec 12.4's Copy Frame List dialog (PAR-006, Phase 14A Task 4), over the nights
                // the ledger's selection column has checked. The page hands over each checked
                // night with the detail it has already loaded, so a night the user has been
                // looking at costs no second query.
                openFrameList: (groupKeyForList, nights) => serviceProvider
                    .GetRequiredService<FrameListDialogService>()
                    .ShowAsync(groupKeyForList, nights),
                // Spec 12.13's Export for stacking page (Phase 16 Task 5b), the flyout's second
                // entry, over the same checked nights. The page issues its own read, so only the
                // dates travel; the frame list's already-loaded detail would be the wrong shape
                // for it.
                openWbppExport: (groupKeyForExport, targetName, nights) => serviceProvider
                    .GetRequiredService<WbppExportDialogService>()
                    .ShowAsync(groupKeyForExport, targetName, nights),
                // Spec 12.4's object type edit (PAR-009, Phase 14A Task 6). The outcome is
                // discarded here because the page has nothing to say about a target that vanished
                // between the click and the write; the next scan-driven reload shows the truth.
                setObjectType: (targetId, category) => serviceProvider
                    .GetRequiredService<TargetWriteRepository>()
                    .SetObjectType(targetId, category),
                // Spec 12.4's PAR-018 deep link: the night the caller asked for, consumed once by
                // the page's first load and null on every route that exists today.
                initialSessionDate: initialSessionDate,
                // Phase 15B fixer F2, with items 30 and 38. The second reload trigger, beside the
                // scan: a settings save can move the PHD2 profile map, the observer zone or the
                // site, and the correlation re-run that follows rewrites this night's guiding
                // figures long afterwards. The page unfollows in its own Dispose, which the shell
                // calls on every navigation away.
                subscribeDerivedDataChanged: handler => derivedDataNotifier.Changed += handler,
                unsubscribeDerivedDataChanged: handler => derivedDataNotifier.Changed -= handler,
                // Spec 12.15's session-scope cells on the Nights ledger (Phase 20 Task 6a). The
                // snapshot read on Program.Main's thread is the fallback half of the column list's
                // seeding rule; the writer beside it carries the half that outranks it and the
                // Changed event that brings the Display tab's own toggle to an open page.
                display: initialDisplay,
                displayColumns: serviceProvider.GetRequiredService<DisplayColumnWriter>(),
                // One definition read and one value read per page load, both on the page's existing
                // background load path. ValuesForTarget answers the session-scope and rig-scope
                // values together, so the ledger rows and the session pane's rig rows partition one
                // result rather than issuing a read each.
                loadCustomColumns: serviceProvider.GetRequiredService<CustomColumnRepository>().List,
                loadValuesForTarget: serviceProvider
                    .GetRequiredService<CustomColumnRepository>().ValuesForTarget,
                writeCustomValue: serviceProvider
                    .GetRequiredService<CustomColumnRepository>().SetValue,
                // Phase 21, spec 12.16 (Task 5). The two clients Task 2 registered, the job
                // registry every long-running action shares, and the activity writer pinned to
                // category user_action (spec 5.12 amendment 2b): the page's own severity per call
                // is the only thing that varies. getGeneral is read fresh on every menu rebuild
                // rather than cached, and the GeneralChanged pair below is what brings an External
                // Tools tab save to an open page with no navigation. getSessionDetail is the same
                // delegate the card factory above takes, for a checked night the AstroBin CSV
                // needs that the page has not opened yet.
                ninaClient: serviceProvider.GetRequiredService<NinaClient>(),
                stellariumClient: serviceProvider.GetRequiredService<StellariumClient>(),
                jobs: serviceProvider.GetRequiredService<JobRegistry>(),
                emitActivity: (severity, eventType, message, details, targetId) => serviceProvider
                    .GetRequiredService<ActivityRepository>()
                    .EmitStandalone("user_action", severity, eventType, message, details, targetId),
                getGeneral: () => serviceProvider.GetRequiredService<SettingsStore>().GetGeneral(),
                getAliasMap: () => serviceProvider.GetRequiredService<AliasMapCache>().Current,
                getSessionDetail: serviceProvider.GetRequiredService<SessionDetailQuery>().Get,
                subscribeGeneralChanged: handler => settingsStore.GeneralChanged += handler,
                unsubscribeGeneralChanged: handler => settingsStore.GeneralChanged -= handler,
                openSurveyView: target => serviceProvider.GetRequiredService<SurveyViewModalService>().ShowAsync(target)));

        // Spec 12.4's Copy Frame List page (Phase 14A Task 4). Per opening, so a factory keyed on
        // the group key and the checked nights, the same shape the merge dialog uses. Every
        // collaborator is a delegate (design-spec 18.3), and the only thing it writes is the
        // clipboard.
        builder.Services
            .AddSingleton<Func<string, IReadOnlyList<FrameListNight>, FrameListDialogViewModel>>(
                serviceProvider => (groupKey, nights) => new FrameListDialogViewModel(
                    groupKey,
                    nights,
                    serviceProvider.GetRequiredService<SessionDetailQuery>().Get,
                    serviceProvider.GetRequiredService<ShellIntegration>().CopyTextAsync,
                    serviceProvider.GetRequiredService<TargetPageState>(),
                    logger: serviceProvider.GetRequiredService<ILogger<FrameListDialogViewModel>>()));

        builder.Services.AddSingleton(serviceProvider => new FrameListDialogService(
            serviceProvider
                .GetRequiredService<Func<string, IReadOnlyList<FrameListNight>, FrameListDialogViewModel>>(),
            serviceProvider.GetRequiredService<ModalHost>()));

        // Spec 12.13's Export for stacking page (Phase 16 Task 5a). Per opening, so a factory
        // keyed on the group key, the target name and the checked nights, the same shape the frame
        // list factory above has. Every collaborator is a delegate (design-spec 18.3); the one
        // exception is the AppWriter, which the page takes as the object because the scoped writer
        // it hands back is what bounds the one write this page makes.
        builder.Services
            .AddSingleton<Func<string, string, IReadOnlyList<DateOnly>, WbppExportViewModel>>(
                serviceProvider => (groupKey, targetName, nights) => new WbppExportViewModel(
                    groupKey,
                    targetName,
                    nights,
                    serviceProvider.GetRequiredService<WbppPathsQuery>().Read,
                    serviceProvider.GetRequiredService<SessionDetailQuery>().Get,
                    settingsStore.GetGeneral,
                    settingsStore.MutateGeneral,
                    serviceProvider.GetRequiredService<AppWriter>(),
                    serviceProvider.GetRequiredService<ShellIntegration>().CopyTextAsync,
                    // Spec 12.13's quality filter panel (Phase 16 Task 3b), built into the page's
                    // own slot. The page resolves the rig and holds the frames and their gradings;
                    // this lambda is the one place the two shapes meet, and it is here rather than
                    // on either view-model so neither has to name a type the other declares.
                    createQualityPanel: (frames, gradings, rigKey, excludedChanged) => new QualityPanelViewModel(
                        [.. frames.Select(frame => new QualityPanelFrame(
                            frame,
                            gradings.TryGetValue(frame.ImageId, out var grading) ? grading : null))],
                        rigKey,
                        settingsStore.GetGeneral,
                        settingsStore.MutateGeneral,
                        excludedChanged,
                        logger: serviceProvider.GetRequiredService<ILogger<QualityPanelViewModel>>()),
                    logger: serviceProvider.GetRequiredService<ILogger<WbppExportViewModel>>()));

        // The export wizard over the page factory above: the job
        // registry for the status bar, one activity row per copy, Copy path and Open folder.
        builder.Services.AddSingleton(serviceProvider => new WbppExportDialogService(
            (groupKey, targetName, nights) => new WbppExportWizardViewModel(
                serviceProvider
                    .GetRequiredService<Func<string, string, IReadOnlyList<DateOnly>, WbppExportViewModel>>()(
                        groupKey, targetName, nights),
                serviceProvider.GetRequiredService<ShellIntegration>().CopyTextAsync,
                serviceProvider.GetRequiredService<JobRegistry>(),
                message => serviceProvider.GetRequiredService<ActivityRepository>()
                    .EmitStandalone("user_action", "info", "stacking_copy", message),
                serviceProvider.GetRequiredService<ShellIntegration>().OpenFolderInExplorer,
                logger: serviceProvider.GetRequiredService<ILogger<WbppExportWizardViewModel>>()),
            serviceProvider.GetRequiredService<ModalHost>()));

        // Spec 12.9's merge preview modal (Phase 7 Task 4). The dialog is per-request and
        // transient, so it is registered as a factory keyed on the request, the same shape the
        // detail page uses. Every collaborator is a delegate (design-spec 18.3).
        builder.Services.AddSingleton<Func<MergeRequest, MergeDialogViewModel>>(serviceProvider =>
            request => new MergeDialogViewModel(
                request,
                serviceProvider.GetRequiredService<MergePreviewQuery>().Get,
                term => serviceProvider.GetRequiredService<TargetSearchQuery>().Search(term),
                serviceProvider.GetRequiredService<MergeRepository>().Merge,
                serviceProvider.GetRequiredService<MergeRepository>().MergeUnresolvedName,
                logger: serviceProvider.GetRequiredService<ILogger<MergeDialogViewModel>>()));

        // The one modal host in this application (ruling Q9), extracted at the second occurrence of
        // the pattern rather than the sixth (Phase 8 Task 7, design-lessons rule 1). The owner
        // window is reached through the same deferred lookup ShellIntegration's clipboard delegate
        // uses, because no window exists while this host is being built. Every modal in the
        // application goes through this one registration; Phase 9's Settings dialogs and Phase
        // 10's diagnostics export use it too and write no third host.
        builder.Services.AddSingleton(serviceProvider => new ModalHost(
            () => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow,
            serviceProvider.GetRequiredService<ILogger<ModalHost>>()));

        // Spec 12.9's merge preview (Phase 7 Task 4). Its name, its registration and its
        // ShowAsync(MergeRequest) signature are unchanged; only its body moved onto the host.
        builder.Services.AddSingleton(serviceProvider => new MergeDialogService(
            serviceProvider.GetRequiredService<Func<MergeRequest, MergeDialogViewModel>>(),
            serviceProvider.GetRequiredService<ModalHost>()));

        // Spec 11.5's preview modal (Phase 8 Task 7). The page is built per opening from the
        // originating frame list and the clicked index; the list is a snapshot and does not change
        // while the modal is open.
        //
        // The slot factory is Task 5's: two slots per frame, the general.thumbnail_width thumbnail
        // (default 800) and the preview, both through the one bounded worker so they share spec
        // 10.6's budget of two. The
        // resolution is not passed here: ThumbnailCache reads general.preview_resolution at call
        // time for every EnsurePreview, which is one answer to "how big is a preview".
        builder.Services.AddSingleton<Func<IReadOnlyList<FrameRowViewModel>, int, PreviewModalViewModel>>(
            serviceProvider => (frames, index) => new PreviewModalViewModel(
                [.. frames.Select(PreviewFrameViewModel.From)],
                index,
                serviceProvider.GetRequiredService<Func<string, ThumbnailKind, ThumbnailSlotViewModel>>(),
                serviceProvider.GetRequiredService<ShellIntegration>(),
                serviceProvider.GetRequiredService<FrameHeadersQuery>().Get,
                logger: serviceProvider.GetRequiredService<ILogger<PreviewModalViewModel>>(),
                // Spec 11.5's render-on-navigation checkbox, through the same MutateGeneral seam
                // every other general-document writer uses (spec 12.7). The getter reads the live
                // document rather than a snapshot taken here, because the box has to survive a
                // second opening of the modal, and spec 11.5 reads the flag on each step.
                //
                // It reads the currentGeneral memo above and not SettingsStore.GetGeneral, for the
                // reason that memo exists: GetGeneral opens a SQLite context and deserializes the
                // whole document on every call, and this getter runs on every navigation step and
                // every binding read. Holding an arrow key through the few hundred frames spec 11.5
                // names would otherwise cost a database round trip per keypress on the UI thread,
                // in the one feature whose purpose is making that stepping cheap (review P2-1).
                // The memo is still live: MutateGeneral raises GeneralChanged synchronously after
                // its gate, and the handler above refreshes it, so the getter reads back the write
                // the checkbox just made.
                getRenderOnNavigate: () => currentGeneral.Value.PreviewRenderOnNavigate,
                setRenderOnNavigate: value => serviceProvider
                    .GetRequiredService<SettingsStore>()
                    .MutateGeneral(general => general with { PreviewRenderOnNavigate = value }),
                // Phase review P3-6: the getter above reads the live document, so a second writer
                // would already be in effect on the next step, and this is what makes the open
                // checkbox show it rather than waiting to be clicked. The modal unsubscribes in
                // Dispose, so a closed one is not kept alive by the store.
                subscribeGeneralChanged: handler => settingsStore.GeneralChanged += handler,
                unsubscribeGeneralChanged: handler => settingsStore.GeneralChanged -= handler));

        builder.Services.AddSingleton(serviceProvider => new PreviewModalService(
            serviceProvider
                .GetRequiredService<Func<IReadOnlyList<FrameRowViewModel>, int, PreviewModalViewModel>>(),
            serviceProvider.GetRequiredService<ModalHost>()));

        // Spec 12.4's Sky view window, per opening and on the same modal host. The survey
        // key is read raw from the live memo (the page resolves it) and written through the one
        // MutateGeneral seam.
        builder.Services.AddSingleton<Func<SurveyTarget, SurveyViewViewModel>>(serviceProvider =>
            target => new SurveyViewViewModel(
                target,
                (view, refresh, token) => serviceProvider
                    .GetRequiredService<SurveyImageService>()
                    .GetAsync(target.TargetId, view, refresh, token),
                () => currentGeneral.Value.SurveyViewSurvey,
                id => serviceProvider
                    .GetRequiredService<SettingsStore>()
                    .MutateGeneral(general => general with { SurveyViewSurvey = id }),
                logger: serviceProvider.GetRequiredService<ILogger<SurveyViewViewModel>>()));

        builder.Services.AddSingleton(serviceProvider => new SurveyViewModalService(
            serviceProvider.GetRequiredService<Func<SurveyTarget, SurveyViewViewModel>>(),
            serviceProvider.GetRequiredService<ModalHost>()));

        // Spec 12.9's merge history (Phase 7 Task 5), on both surfaces from one view-model. One
        // factory keyed on a nullable winner rather than two registrations: null is the Settings
        // list (every manifest), a target id is that target's page. The only thing that differs
        // between the two is the load delegate, which is exactly why there is one type.
        builder.Services.AddSingleton<Func<Guid?, MergeHistoryViewModel>>(serviceProvider =>
            winnerId => new MergeHistoryViewModel(
                winnerId is { } id
                    ? () => serviceProvider.GetRequiredService<MergeHistoryQuery>().ForWinner(id)
                    : serviceProvider.GetRequiredService<MergeHistoryQuery>().All,
                serviceProvider.GetRequiredService<MergeRepository>().Unmerge,
                serviceProvider.GetRequiredService<MergeRepository>().UndoUnresolvedNameMerge,
                currentGeneral.Value,
                logger: serviceProvider.GetRequiredService<ILogger<MergeHistoryViewModel>>()));

        // Spec 12.7's Create target form (PAR-001, Phase 14B Task 3). A singleton, because the
        // Targets tab renders it inline and the unresolved-name list below opens the same
        // instance from a row action: two forms would be two half-typed drafts of the same write.
        // afterCreate is the tab's own reload, resolved when it runs rather than when this factory
        // does, so the two singletons do not have to be constructed in a cycle.
        builder.Services.AddSingleton(serviceProvider => new CreateTargetViewModel(
            serviceProvider.GetRequiredService<TargetWriteRepository>().CreateUserDefined,
            afterCreate: () => serviceProvider.GetRequiredService<TargetsTabViewModel>().ReloadRegions(),
            logger: serviceProvider.GetRequiredService<ILogger<CreateTargetViewModel>>()));

        // Spec 12.7's unresolved names with their retry, and its rename history (Phase 7 Task 6).
        // Plain singletons, not a keyed factory like the merge history above: there is one of each
        // list, and Phase 10's Diagnostics page binds this same UnresolvedNamesViewModel instance
        // for spec 12.8's Unresolved group. The container owns their lifetime for that reason; the
        // Targets tab renders them without disposing them.
        //
        // assignToTarget opens the merge dialog with the name as the merged-away side and no
        // winner, so the dialog's own search box chooses the surviving target (Task 4's
        // LoserName-shaped request).
        builder.Services.AddSingleton(serviceProvider => new UnresolvedNamesViewModel(
            serviceProvider.GetRequiredService<UnresolvedNamesQuery>().All,
            (report, cancellationToken) => serviceProvider
                .GetRequiredService<UnresolvedRetry>()
                .Run(report, cancellationToken),
            async objectName => await serviceProvider
                .GetRequiredService<MergeDialogService>()
                .ShowAsync(new MergeRequest(null, null, objectName, null))
                .ConfigureAwait(true) is not null,
            // Ruling: the post-scan refresh comes from ScanStatusService.ScanFinished, because a
            // scan can both add and remove unresolved names.
            scanStatus: serviceProvider.GetRequiredService<ScanStatusService>(),
            logger: serviceProvider.GetRequiredService<ILogger<UnresolvedNamesViewModel>>(),
            // Spec 12.7's Create target row action, which opens the tab's own form with this
            // OBJECT string pre-filled. Resolved when the row is pressed, not when this factory
            // runs, so the two singletons do not have to be constructed in a cycle.
            createTarget: name => serviceProvider.GetRequiredService<CreateTargetViewModel>().OpenFor(name)));
        builder.Services.AddSingleton(serviceProvider => new RenameHistoryViewModel(
            () => serviceProvider.GetRequiredService<RenameHistoryQuery>().Recent(),
            initialGeneral,
            logger: serviceProvider.GetRequiredService<ILogger<RenameHistoryViewModel>>()));

        // Spec 12.7's Settings page and its one real tab (Phase 7 Task 3, ruling Q2). Singletons
        // like DashboardViewModel: the tab's candidate list, its edit-target dropdown state and
        // its scan subscription outlive one visit to the page, and the host disposes both.
        //
        // openMerge is Task 4's dialog. The candidate is turned into a MergeRequest by asking
        // MergePreviewQuery whether its source_name is an active target's primary_name (ruling
        // Q18): the merge shape is decided in Data, never by a string match in a view-model.
        // That read runs on the pool, and ShowAsync resumes on the UI thread to build the window.
        builder.Services.AddSingleton(serviceProvider => new TargetsTabViewModel(
            serviceProvider.GetRequiredService<MergeCandidateQuery>().Pending,
            serviceProvider.GetRequiredService<MergeCandidateRepository>().Dismiss,
            async row =>
            {
                var request = await Task
                    .Run(() => ToMergeRequest(serviceProvider.GetRequiredService<MergePreviewQuery>(), row))
                    .ConfigureAwait(true);

                return await serviceProvider
                    .GetRequiredService<MergeDialogService>()
                    .ShowAsync(request)
                    .ConfigureAwait(true);
            },
            term => serviceProvider.GetRequiredService<TargetSearchQuery>().Search(term),
            serviceProvider.GetRequiredService<MergeCandidateRepository>().Retarget,
            // Task 5's merge history seam, over every manifest.
            serviceProvider.GetRequiredService<Func<Guid?, MergeHistoryViewModel>>()(null),
            // Task 6's two seams. Both are container-owned singletons, so the tab renders them
            // and does not dispose them.
            serviceProvider.GetRequiredService<UnresolvedNamesViewModel>(),
            serviceProvider.GetRequiredService<RenameHistoryViewModel>(),
            // Ruling: the post-scan refresh comes from ScanStatusService.ScanFinished, never from
            // a direct ScanCoordinator subscription.
            scanStatus: serviceProvider.GetRequiredService<ScanStatusService>(),
            logger: serviceProvider.GetRequiredService<ILogger<TargetsTabViewModel>>(),
            // Phase 14B Task 3's create form, rendered inline in this tab's markup.
            createTarget: serviceProvider.GetRequiredService<CreateTargetViewModel>()));
        // Spec 12.7's Library tab (Phase 9 Task 5). A singleton like the Targets tab, and for the
        // same reason: its unsaved filter edits and its scan subscription outlive one visit to the
        // page, and the host disposes it.
        //
        // Delegates, never SettingsStore or ScanCoordinator. save is SettingsStore.SaveGeneral,
        // which is the one settings write path and the one place ScanFilterConfig.Validate runs
        // (design-lessons rule 2); runScan is the same manual-scan lambda StatusBarViewModel
        // takes, so the two buttons cannot disagree about what a manual scan is.
        builder.Services.AddSingleton(serviceProvider => new LibraryTabViewModel(
            settingsStore.GetGeneral,
            // MutateGeneral, not GetGeneral plus SaveGeneral: the read, the mutation, the
            // validation and the write happen under the store's own gate in one critical section,
            // so a concurrent writer of the same document cannot be lost in the window between a
            // read and a write (Task 5 review escalation). Task 6's tabs and Task 9's wizard use
            // the same method.
            settingsStore.MutateGeneral,
            // Spec 10.3's per-run arguments (PAR-013): this tab is the one surface that can set
            // them, so its delegate carries a ScanRunOptions the status bar's does not have.
            (options, token) => serviceProvider
                .GetRequiredService<ScanCoordinator>()
                .RunAsync(ScanTrigger.Manual, null, token, options),
            // Resolved inside the lambda, like runScan above: capturing the coordinator here would
            // build it while this factory runs, which is exactly what the lazy tab defers.
            () => serviceProvider.GetRequiredService<ScanCoordinator>().Cancel(),
            // Ruling: progress comes from ScanStatusService, never from a second ScanCoordinator
            // subscription.
            scanStatus: serviceProvider.GetRequiredService<ScanStatusService>(),
            // Phase 9 Task 9 fills Task 5's seam: one probe, two callers, so the count this tab
            // shows for a folder and the count the wizard shows for it cannot disagree
            // (design-lessons rule 1). Synchronous here because the tab's seam is
            // Func<string, int>; the wizard runs the same call on a background thread.
            probeSupportedFiles: folder => serviceProvider
                .GetRequiredService<SupportedFileProbe>()
                .Count(folder, CancellationToken.None),
            logger: serviceProvider.GetRequiredService<ILogger<LibraryTabViewModel>>(),
            // Task 5 review finding I3: the tab is a singleton whose lists outlive a visit to the
            // Settings page, and Task 9's wizard writes the same document from a link on this tab.
            // Without this the tab shows stale lists and the next save writes them back.
            subscribeGeneralChanged: handler => settingsStore.GeneralChanged += handler,
            unsubscribeGeneralChanged: handler => settingsStore.GeneralChanged -= handler,
            // Spec 12.1's last sentence: "The wizard is also reachable from Settings as 'Run setup
            // again'." The same SetupWizardService the first-run branch in App.axaml.cs uses.
            runSetupAgain: () => serviceProvider.GetRequiredService<SetupWizardService>().ShowAsync()));

        // Spec 12.7's Filters and Equipment tabs (Phase 9 Task 7). Delegates over
        // SettingsStore's filter and equipment write paths, never the store itself
        // (design-spec 18.3), and SaveFilters/SaveEquipment are the only two writes that raise
        // AliasSourcesChanged, so AliasMapCache really drops its memo on Save; nothing here pokes
        // the cache directly (design-lessons rule 2). Both tabs share
        // SettingsStore.GetDismissedSuggestions/SaveDismissedSuggestions, per spec 5.8.
        builder.Services.AddSingleton(serviceProvider => new FiltersTabViewModel(
            settingsStore.GetFilters,
            settingsStore.SaveFilters,
            settingsStore.GetDismissedSuggestions,
            settingsStore.SaveDismissedSuggestions,
            () => serviceProvider.GetRequiredService<DiscoveredNamesQuery>()
                .Read(DiscoveredNameColumn.Filters)
                .Select(row => (row.Name, row.FrameCount))
                .ToList(),
            logger: serviceProvider.GetRequiredService<ILogger<FiltersTabViewModel>>()));
        builder.Services.AddSingleton(serviceProvider =>
        {
            // Spec 12.7's PHD2 profiles panel (Phase 15A Task 6), a child of this tab rather than
            // a registration of its own: it reads the telescope names off the grouping editor
            // above it through the tab's own EquipmentTabViewModel.KnownTelescopes, which is where
            // that rule lives, and nothing else in the application resolves it.
            //
            // The two-phase local is the ordinary DI answer to that circularity: the panel needs
            // the tab's TelescopesEditor and the tab needs the panel to construct.
            //
            // THE CLOSURE IS INVOKED BEFORE `tab` IS ASSIGNED, and that is why the reload below
            // is not optional (Task 6b review P2-1). The panel's constructor ends with its own
            // Load(), which reads the database on a pool thread and calls this closure from
            // there, so the read races the `new EquipmentTabViewModel(...)` on the next statement.
            // When the pool wins, `tab` is still null, the null reference is swallowed into one
            // log warning, and the panel publishes with NO telescope options at all: every row's
            // picker offers only "Not mapped" and no profile can be mapped until something
            // reloads.
            //
            // A reload placed immediately after the assignment is NOT enough, and the difference
            // is the whole point of the schedule below: the tab loads its own groups on the pool
            // too and publishes them through the dispatcher, so a reload issued at that moment
            // reads a non-null tab whose TelescopesEditor.Groups is still empty, and the picker
            // is empty for a second time with no null reference to blame. The reload is therefore
            // scheduled after the tab's own load, and posted, because PendingLoad completes once
            // the tab has QUEUED its publish and the dispatcher runs queued work in order: the
            // reload's own pool read cannot start until that publish has put the groups on the
            // editor. The panel's generation guard discards both earlier results whichever way
            // they raced.
            //
            // The panel has no re-run dispatch callback of any kind: the one trigger for the
            // correlation re-run is SettingsStore's own change event, subscribed in the
            // ScanStatusService block above, and a second trigger on the panel would run one
            // save's correlation twice.
            EquipmentTabViewModel tab = null!;
            var phd2Profiles = new Phd2ProfilesViewModel(
                settingsStore.GetGeneral,
                () => serviceProvider.GetRequiredService<Phd2ProfilesQuery>().Read(),
                settingsStore.MutateGeneral,
                () => tab.KnownTelescopes(),
                logger: serviceProvider.GetRequiredService<ILogger<Phd2ProfilesViewModel>>());

            tab = new EquipmentTabViewModel(
                settingsStore.GetEquipment,
                settingsStore.SaveEquipment,
                settingsStore.GetDismissedSuggestions,
                settingsStore.SaveDismissedSuggestions,
                () => serviceProvider.GetRequiredService<DiscoveredNamesQuery>()
                    .Read(DiscoveredNameColumn.Cameras)
                    .Select(row => (row.Name, row.FrameCount))
                    .ToList(),
                () => serviceProvider.GetRequiredService<DiscoveredNamesQuery>()
                    .Read(DiscoveredNameColumn.Telescopes)
                    .Select(row => (row.Name, row.FrameCount))
                    .ToList(),
                logger: serviceProvider.GetRequiredService<ILogger<EquipmentTabViewModel>>(),
                phd2Profiles: phd2Profiles,
                // Spec 12.15's third write: a telescope or camera rename moves every stored
                // rig_label, and the repository reads and splits the labels itself.
                rewriteRigLabels: serviceProvider.GetRequiredService<CustomColumnRepository>().RewriteRigLabels);

            if (tab.PendingLoad is { } pendingTabLoad)
            {
                _ = pendingTabLoad.ContinueWith(
                    _ => UiPost.Default(phd2Profiles.Reload),
                    TaskScheduler.Default);
            }
            else
            {
                phd2Profiles.Reload();
            }

            // Phase 15B Task 5c, carried item 64: the panel reads general.phd2_correlation_pending
            // only at load, so the "a re-run is still owed" sentence stayed up after the re-run
            // cleared the flag. The clearing write is a MutateGeneral like any other, so the
            // store's own change event is the completion signal and no second one is invented.
            tab.FollowGeneralChanges(
                handler => settingsStore.GeneralChanged += handler,
                handler => settingsStore.GeneralChanged -= handler);

            return tab;
        });

        // Spec 12.7's rebuild-targets maintenance action (Phase 9 Task 8). The resolve delegate
        // carries skipOnline: true, which is spec 9.6's skipSimbad: the cache and the local
        // catalogues only, never the network, which is what makes the roadmap's "rebuild targets
        // performs no network call" true. createIfMissing: true because the rebuild re-creates the
        // assignments it just cleared, exactly as the retry does. The lease is the coordinator's
        // own, never ScanStatusService, which is a UI-thread mirror that lags it by one post.
        builder.Services.AddSingleton(serviceProvider => new TargetRebuild(
            serviceProvider.GetRequiredService<DatabaseConnectionString>().Value,
            (objectName, cancellationToken) => serviceProvider
                .GetRequiredService<TargetResolver>()
                .Resolve(objectName, createIfMissing: true, dryRun: false, skipOnline: true, ct: cancellationToken),
            () => serviceProvider.GetRequiredService<ScanCoordinator>().TryBeginResolution(),
            serviceProvider.GetRequiredService<ILogger<TargetRebuild>>()));

        // Spec 12.7's smart-rebuild maintenance action (PAR-007, Phase 14B Task 4). None of its six
        // passes resolves anything: the delegate exists for the duplicate detection that runs
        // inline at the end (departure 6), and it carries skipOnline: true for the same reason the
        // rebuild above does, which is what makes "no network call at all" true. createIfMissing is
        // forwarded rather than pinned, because the detector probes with false and creates with
        // true, and a delegate that dropped the flag would turn its probe into a create.
        builder.Services.AddSingleton(serviceProvider => new SmartRebuild(
            serviceProvider.GetRequiredService<DatabaseConnectionString>().Value,
            (objectName, createIfMissing, cancellationToken) => serviceProvider
                .GetRequiredService<TargetResolver>()
                .Resolve(objectName, createIfMissing, dryRun: false, skipOnline: true, ct: cancellationToken),
            () => serviceProvider.GetRequiredService<ScanCoordinator>().TryBeginResolution(),
            serviceProvider.GetRequiredService<ILogger<SmartRebuild>>()));

        // Spec 12.7's catalog-identity-backfill maintenance action (PAR-007, Phase 14B Task 4).
        // ResolveIdentity, never Resolve: it runs on a non-tracking context, always returns a null
        // TargetId and cannot create a target, which is the spec's "creates no target" written into
        // the binding rather than trusted to the loop. skipOnline: true is spec 9.6's skipSimbad.
        builder.Services.AddSingleton(serviceProvider => new CatalogIdentityBackfill(
            serviceProvider.GetRequiredService<DatabaseConnectionString>().Value,
            (objectName, cancellationToken) => serviceProvider
                .GetRequiredService<TargetResolver>()
                .ResolveIdentity(objectName, skipOnline: true, ct: cancellationToken),
            () => serviceProvider.GetRequiredService<ScanCoordinator>().TryBeginResolution(),
            serviceProvider.GetRequiredService<ILogger<CatalogIdentityBackfill>>()));

        // Spec 12.7's reset-database maintenance action (Phase 9 Task 8). Rows only: the database
        // file is never deleted (questions.md Q28), and user_settings and the shipped catalogues
        // are kept (Q29). Under the same resolution lease, so a reset cannot delete the rows a
        // scan writer is mid-transaction on.
        builder.Services.AddSingleton(serviceProvider => new DatabaseReset(
            serviceProvider.GetRequiredService<DatabaseConnectionString>().Value,
            () => serviceProvider.GetRequiredService<ScanCoordinator>().TryBeginResolution(),
            serviceProvider.GetRequiredService<ILogger<DatabaseReset>>()));

        // Spec 12.7's typed confirmation for reset database (Phase 9 Task 8), on ModalHost, the one
        // modal host in this application (TRACKING item 22). A service beside MergeDialogService
        // and PreviewModalService, not a lambda in this file, because that is where the other two
        // "build the window, read the page" wrappers live (review escalation ruling).
        //
        // The reset itself is bound here and nowhere else. After a run that really emptied the
        // database, the two in-process memos that would otherwise keep serving pre-reset figures
        // for the life of the process are dropped: StatsCache has no TTL by design (questions.md
        // Q7) and RigBaselinesCache would be stale until its five minute TTL expired (review
        // finding I5). Open pages are not reloaded; that ceiling is recorded in HANDOFF section 7.
        builder.Services.AddSingleton(serviceProvider => new ResetConfirmDialogService(
            () => new ResetConfirmViewModel(
                cancellationToken =>
                {
                    var outcome = serviceProvider.GetRequiredService<DatabaseReset>().Run(cancellationToken);
                    if (outcome.Status == DatabaseReset.ResetStatus.Completed)
                    {
                        InvalidateDerivedCaches(serviceProvider);

                        // Spec 12.15, ruling C22: the reset truncates custom_columns and
                        // custom_column_values without passing through the repository, so it
                        // announces the change itself. Without this the dashboard keeps rows and
                        // cells for columns that no longer exist, and every toggle in one of them is
                        // refused. The route above posts it.
                        serviceProvider.GetRequiredService<CustomColumnRepository>().NotifyDefinitionsChanged();
                    }

                    return outcome;
                },
                logger: serviceProvider.GetRequiredService<ILogger<ResetConfirmViewModel>>()),
            serviceProvider.GetRequiredService<ModalHost>()));

        // Spec 12.7's Maintenance tab (Phase 9 Task 8). A singleton like the other real tabs: its
        // running-action state and its scan subscription outlive one visit to the page, and the
        // host disposes it.
        //
        // Delegates, never the services themselves, so the tab builds in a unit test with no
        // database, no cache root and no window (design-spec 18.3). Three of them are the phase's
        // designated owners and are reached here and nowhere else: the reference run goes through
        // ScanCoordinator, which owns the render delegate and the lease (questions.md Q27); the
        // purge goes through ThumbnailCache.Purge, the one place a cache path is composed
        // (TRACKING item 21); and the typed confirmation opens on ModalHost, the one modal host
        // (TRACKING item 22).
        builder.Services.AddSingleton(serviceProvider => new MaintenanceTabViewModel(
            // FIXER LIST F22: a rebuild reassigns frames, creates targets and clears candidates, so
            // every figure the Statistics page memoizes and every rig baseline is stale afterwards.
            // StatsCache has no TTL by design (questions.md Q7), so without this the page kept its
            // pre-rebuild target counts, top targets and equipment inventory until a scan completed
            // or a settings document was saved.
            (report, cancellationToken) =>
            {
                var outcome = serviceProvider.GetRequiredService<TargetRebuild>().Run(report, cancellationToken);
                if (outcome.Status == TargetRebuild.RebuildStatus.Completed)
                {
                    InvalidateDerivedCaches(serviceProvider);
                }

                return outcome;
            },
            // The same UnresolvedRetry instance the Targets tab's retry button uses. One retry
            // implementation, two surfaces (its own class comment says so). Invalidated on the same
            // terms as the rebuild above: a retry that resolved a name assigned frames to a target.
            (report, cancellationToken) =>
            {
                var outcome = serviceProvider.GetRequiredService<UnresolvedRetry>().Run(report, cancellationToken);
                if (outcome.Status == UnresolvedRetry.RetryStatus.Completed)
                {
                    InvalidateDerivedCaches(serviceProvider);
                }

                return outcome;
            },
            // FIXER LIST F22 again: both new actions reassign frames, so both drop the two memos
            // that are derived from frame assignments, on the same terms as the two above.
            (report, cancellationToken) =>
            {
                var outcome = serviceProvider.GetRequiredService<SmartRebuild>().Run(report, cancellationToken);
                if (outcome.Status == SmartRebuild.SmartRebuildStatus.Completed)
                {
                    InvalidateDerivedCaches(serviceProvider);
                }

                return outcome;
            },
            (report, cancellationToken) =>
            {
                var outcome = serviceProvider
                    .GetRequiredService<CatalogIdentityBackfill>()
                    .Run(report, cancellationToken);
                if (outcome.Status == CatalogIdentityBackfill.BackfillStatus.Completed)
                {
                    InvalidateDerivedCaches(serviceProvider);
                }

                return outcome;
            },
            (force, report, cancellationToken) => serviceProvider
                .GetRequiredService<ScanCoordinator>()
                .RunReferenceThumbnailsAsync(force, report, cancellationToken),
            (cancellationToken) => serviceProvider
                .GetRequiredService<ThumbnailCache>()
                .Purge(ThumbnailKind.Frame, cancellationToken),
            // Read at click time, not captured, so a retention change on the Display tab takes
            // effect with no restart.
            () => settingsStore.GetGeneral().ActivityRetentionDays,
            serviceProvider.GetRequiredService<ActivityRepository>().PruneRetention,
            // The reset runs inside its own modal, so the window cannot be dismissed mid-delete,
            // and the summary leaves with the dialog the way MergeDialogService's does.
            () => serviceProvider.GetRequiredService<ResetConfirmDialogService>().ShowAsync(),
            (eventType, message, details) => serviceProvider
                .GetRequiredService<ActivityRepository>()
                .EmitStandalone("rebuild", "info", eventType, message, details),
            scanStatus: serviceProvider.GetRequiredService<ScanStatusService>(),
            logger: serviceProvider.GetRequiredService<ILogger<MaintenanceTabViewModel>>(),
            // Spec 12.7's last sentence: every action on this tab is a registered job (PAR-015).
            jobs: serviceProvider.GetRequiredService<JobRegistry>()));

        // Spec 12.7's Location, Display and Storage tabs (Phase 9 Task 6). Delegates, never the
        // store (design-spec 18.3), and every general-document write is MutateGeneral rather than
        // a GetGeneral plus SaveGeneral pair, so two tabs saving at once cannot lose one another's
        // key (Task 5 review escalation). SettingsStore.ValidateGeneral stays the one enforcement
        // point behind all three tabs' inline messages (design-lessons rule 2).
        // Spec 12.7's General tab (Phase 11 Task 2), the sixth surface on
        // GeneralSettingsTabViewModel and the one place spec 12.11's five residency controls live.
        // Delegates, never the store, and every write is MutateGeneral. The startup shortcut seam
        // is bound below, to the one IStartupShortcut implementation (Phase 11 Task 3), resolved
        // inside the lambda below its own registration further down this method, exactly as the
        // DiagnosticsService registration above resolves BuildInfo.
        builder.Services.AddSingleton(serviceProvider => new GeneralTabViewModel(
            settingsStore.GetGeneral,
            settingsStore.MutateGeneral,
            serviceProvider.GetRequiredService<IStartupShortcut>(),
            logger: serviceProvider.GetRequiredService<ILogger<GeneralTabViewModel>>(),
            subscribeGeneralChanged: handler => settingsStore.GeneralChanged += handler,
            unsubscribeGeneralChanged: handler => settingsStore.GeneralChanged -= handler));

        builder.Services.AddSingleton(serviceProvider => new LocationTabViewModel(
            settingsStore.GetGeneral,
            settingsStore.MutateGeneral,
            logger: serviceProvider.GetRequiredService<ILogger<LocationTabViewModel>>(),
            subscribeGeneralChanged: handler => settingsStore.GeneralChanged += handler,
            unsubscribeGeneralChanged: handler => settingsStore.GeneralChanged -= handler));

        builder.Services.AddSingleton(serviceProvider =>
        {
            // Resolved once, here, rather than inside the two delegates below: the unsubscribe runs
            // from this tab's own Dispose, which the host performs while it is tearing the container
            // down, and a resolve there throws ObjectDisposedException. The same reason every other
            // subscribe pair on this tab closes over settingsStore rather than the provider.
            var customColumns = serviceProvider.GetRequiredService<CustomColumnRepository>();
            return new DisplayTabViewModel(
            settingsStore.GetGeneral,
            settingsStore.MutateGeneral,
            settingsStore.GetDisplay,
            settingsStore.SaveDisplay,
            settingsStore.GetGraph,
            // The one writer of the graph document, shared with the Target detail charts, so
            // default_chart_sessions written here and a metric toggled there cannot lose one
            // another (design-lessons rule 2).
            serviceProvider.GetRequiredService<GraphSettingsWriter>(),
            // The one writer of display.columns, shared with the dashboard and every frame table.
            serviceProvider.GetRequiredService<DisplayColumnWriter>(),
            () => ThemeManager.Available,
            // FIXER item 8, ruling Q19: merges the theme dictionary, sets the theme variant, then
            // calls ChartTheme.Apply(), in that order.
            ThemeManager.Apply,
            // The dashboard's column picker drives the live target list rather than a second copy
            // of its column state: the dashboard is a singleton, so the Settings picker and the
            // in-header picker are one thing. Resolving the dashboard here is free, because the
            // window has already built it by the time this tab can be visited.
            dashboardColumns: serviceProvider.GetRequiredService<DashboardViewModel>().Targets.Columns,
            toggleDashboardColumn: column => serviceProvider
                .GetRequiredService<DashboardViewModel>()
                .Targets
                .ToggleColumnCommand
                .Execute(column),
            logger: serviceProvider.GetRequiredService<ILogger<DisplayTabViewModel>>(),
            subscribeGeneralChanged: handler => settingsStore.GeneralChanged += handler,
            unsubscribeGeneralChanged: handler => settingsStore.GeneralChanged -= handler,
            // FIXER item 7: the tab follows another writer of display.groups, and takes only the
            // groups half from it.
            subscribeDisplayChanged: handler => settingsStore.DisplayChanged += handler,
            unsubscribeDisplayChanged: handler => settingsStore.DisplayChanged -= handler,
            // Phase 20 Task 6c's Nights ledger column picker. Read on this tab's own background
            // pass, and re-read on the event below.
            loadCustomColumns: customColumns.List,
            // Spec 12.15, ruling C22 with C32. The ledger picker's rows ARE the custom columns, and
            // this tab is a lazily built singleton behind a memoized navigation item, so without
            // this pair a reader who visited Display before defining a night column read "No custom
            // columns yet." for the rest of the process. A delegate pair, not a handler attached
            // from here: resolving this tab inside an AppHost handler would build it and read four
            // documents for a reader who never opened it.
            subscribeCustomColumnsChanged: handler => customColumns.Changed += handler,
            unsubscribeCustomColumnsChanged: handler => customColumns.Changed -= handler);
        });

        builder.Services.AddSingleton(serviceProvider => new StorageTabViewModel(
            settingsStore.GetGeneral,
            settingsStore.MutateGeneral,
            // DriveInfo, a read-only API, invoked off the UI thread by the tab. Nothing on this
            // path creates the directory: AppHost's own GeneralChanged handler above already does
            // that through AppWriter after a save, which is the one sanctioned creation.
            volumeSpace: null,
            defaultCacheRoot: () => appWriter.ThumbnailCacheRoot,
            logger: serviceProvider.GetRequiredService<ILogger<StorageTabViewModel>>(),
            subscribeGeneralChanged: handler => settingsStore.GeneralChanged += handler,
            unsubscribeGeneralChanged: handler => settingsStore.GeneralChanged -= handler,
            // Spec 12.7's data location (Phase 10 Task 9). Values and delegates, never the pointer
            // and never AppWriter: the tab reads where the root came from, shows a pending move,
            // and hands a picked path to the one recorder.
            dataRoot: () => resolution,
            lastRelocation: () => relocation,
            requestDataRootMove: serviceProvider.GetRequiredService<DataRootMoveRequest>().Request,
            cancelDataRootMove: serviceProvider.GetRequiredService<DataRootMoveRequest>().Cancel));

        // Spec 12.15's Custom Columns tab (Phase 20 Task 4), between Display and Storage in the
        // strip. Delegates onto the one repository (CustomColumnRepository is registered above,
        // beside MergeRepository), never the repository itself, so the tab builds in a unit test
        // with no database (spec 18.3).
        builder.Services.AddSingleton(serviceProvider =>
        {
            var customColumns = serviceProvider.GetRequiredService<CustomColumnRepository>();
            return new CustomColumnsTabViewModel(
                customColumns.List,
                customColumns.Create,
                customColumns.Update,
                customColumns.Reorder,
                customColumns.Delete,
                logger: serviceProvider.GetRequiredService<ILogger<CustomColumnsTabViewModel>>());
        });

        // Spec amendment 4c's External Tools tab (Phase 21 Task 4), between Custom Columns and
        // Storage. Ruling B2: the AstroBin filter id map's names come from the Filters tab's own
        // union declaration through this delegate, never a second union rule or a constructed
        // FiltersTabViewModel of this registration's own; resolving it here is the same lazy
        // build every other cross-tab read in this file already performs (the dashboard-columns
        // delegate above is the shape).
        builder.Services.AddSingleton(serviceProvider => new ExternalToolsTabViewModel(
            settingsStore.GetGeneral,
            settingsStore.MutateGeneral,
            knownFilters: () => serviceProvider.GetRequiredService<FiltersTabViewModel>().KnownFilters(),
            logger: serviceProvider.GetRequiredService<ILogger<ExternalToolsTabViewModel>>(),
            subscribeGeneralChanged: handler => settingsStore.GeneralChanged += handler,
            unsubscribeGeneralChanged: handler => settingsStore.GeneralChanged -= handler,
            // Ruling B32: the filter id map's union follows a rename on the Filters tab and a
            // scan discovery the same way AliasMapCache does, through the same two signals.
            subscribeAliasSourcesChanged: handler => settingsStore.AliasSourcesChanged += handler,
            unsubscribeAliasSourcesChanged: handler => settingsStore.AliasSourcesChanged -= handler,
            // Named subscribeFilterUnionRefresh, not subscribeDerivedDataChanged: this is a
            // Settings tab, not one of the three pages Phd2ReRunHostWiringTests censuses, so it
            // adds no fourth line to that count while still following the one process-level
            // derivedDataNotifier every other follower does.
            subscribeFilterUnionRefresh: handler => derivedDataNotifier.Changed += handler,
            unsubscribeFilterUnionRefresh: handler => derivedDataNotifier.Changed -= handler));

        // TRACKING item 16: one factory per real tab, invoked on the tab's first visit. The
        // factories resolve DI singletons, which are themselves built on first resolve, so a
        // window that is never navigated to Settings builds neither tab and performs none of
        // their reads.
        builder.Services.AddSingleton(serviceProvider => new SettingsViewModel(
            serviceProvider.GetRequiredService<LibraryTabViewModel>,
            serviceProvider.GetRequiredService<TargetsTabViewModel>,
            serviceProvider.GetRequiredService<FiltersTabViewModel>,
            serviceProvider.GetRequiredService<EquipmentTabViewModel>,
            serviceProvider.GetRequiredService<MaintenanceTabViewModel>,
            serviceProvider.GetRequiredService<LocationTabViewModel>,
            serviceProvider.GetRequiredService<DisplayTabViewModel>,
            serviceProvider.GetRequiredService<StorageTabViewModel>,
            // Spec 12.8's Diagnostics page as a Settings tab (ruling Q3). The same factory the
            // rail entry gets, so both surfaces resolve one instance; the container owns it and
            // SettingsViewModel.Dispose leaves it alone.
            serviceProvider.GetRequiredService<Func<DiagnosticsViewModel>>(),
            // Spec 12.7's About tab (Phase 10 Task 4). A lazy factory like every other tab: a
            // session that never opens it builds no tab.
            serviceProvider.GetRequiredService<AboutTabViewModel>,
            // Spec 12.7's General tab (Phase 11 Task 2), the eleventh entry, shown immediately
            // after Library (ruling Q1). Lazy for the same reason: a session that never opens it
            // reads no settings document for it.
            serviceProvider.GetRequiredService<GeneralTabViewModel>,
            // Spec 12.15's Custom Columns tab (Phase 20 Task 4), the twelfth entry, between
            // Display and Storage in the strip (spec amendment 2.8, user choice 18). Lazy for the
            // same reason: a session that never opens it reads no custom-column row.
            serviceProvider.GetRequiredService<CustomColumnsTabViewModel>,
            // Spec amendment 4c's External Tools tab (Phase 21 Task 4), the thirteenth entry,
            // between Custom Columns and Storage. Lazy for the same reason: a session that never
            // opens it reads no general-settings document for it and never builds the Filters tab
            // its known-filters delegate would otherwise resolve.
            serviceProvider.GetRequiredService<ExternalToolsTabViewModel>));

        // Statistics (Phase 9 Task 3). A singleton for the reason DashboardViewModel is one: the
        // page's section state (the timeline's granularity and preset, the calendar's range, the
        // expanded equipment rows) is session state, so navigating away and back keeps it, and the
        // memoized stats response behind it is read once rather than per visit.
        //
        // Delegates, never the query objects, so the page constructs in a unit test with lambdas
        // and no database (design-spec 18.3). general is a Func<> rather than initialGeneral by
        // value because the Location settings tab can change the observer coordinates while this
        // page is alive, and spec 8.4's efficiency series appears and disappears with them.
        builder.Services.AddSingleton(serviceProvider => new StatisticsViewModel(
            () => serviceProvider.GetRequiredService<StatsCache>().Current,
            (from, to) => serviceProvider.GetRequiredService<StatsQuery>().Calendar(from, to),
            settingsStore.GetGeneral,
            () => serviceProvider.GetRequiredService<AliasMapCache>().Current,
            // What the page's Refresh button drops before it re-reads. StatsCache carries no TTL
            // (questions.md Q7), so without this the button would re-project the memoized response
            // for the life of the process. Task 3 review finding I2.
            invalidate: () => serviceProvider.GetRequiredService<StatsCache>().Invalidate(),
            // Ruling: the post-scan refresh comes from ScanStatusService.ScanFinished, never from
            // a direct ScanCoordinator subscription. The cache's own invalidation is wired in the
            // ScanStatusService registration above, and it runs before this subscriber, so the
            // reload this raises reads a rebuilt response.
            scanStatus: serviceProvider.GetRequiredService<ScanStatusService>(),
            post: null,
            today: null,
            logger: serviceProvider.GetRequiredService<ILogger<StatisticsViewModel>>(),
            // Phase 15B fixer F2. The second refresh trigger, beside the scan: the one
            // process-level notification raised after the derived memos have been dropped, which
            // is what a general save and a completed correlation re-run both route through.
            // Without it this page, a singleton, is the page the reader left: they follow its own
            // "Map profiles" link into Settings, map a profile, come back, and read the same empty
            // notice that sent them. Resolved inside the two delegates, like every other seam in
            // this registration.
            subscribeDerivedDataChanged: handler => derivedDataNotifier.Changed += handler,
            unsubscribeDerivedDataChanged: handler => derivedDataNotifier.Changed -= handler));

        // Spec 12.14's Analysis page (Phase 17 Task 4). A singleton for the reason the Statistics
        // page above is one: the tab selection, the filter bar and each tab's last result are
        // session state, so navigating away and back keeps them.
        //
        // Delegates, never AnalysisCache, which is sealed with no interface and no virtual member
        // (ruling P1-1), so the page constructs in a unit test with lambdas and no database (spec
        // 18.3). This is the one place the eight are bound, exactly as loadStats is bound to
        // StatsCache.Current three registrations above.
        builder.Services.AddSingleton(serviceProvider =>
        {
            // Resolved ONCE, here, and held by both halves of the pair below.
            //
            // Resolving it at all is load bearing, and the ordering argument is the guide log
            // memo's own subscription above: this resolve is what runs the ScanStatusService
            // factory, so that factory's invalidations stand ahead of the page's handler on both
            // events, and the page re-reads a dropped memo rather than the one the event was
            // raised about.
            //
            // Resolving it HERE rather than inside the two delegates is what a filtered run caught:
            // the unsubscribe runs from AnalysisViewModel.Dispose, which the container calls while
            // it is disposing itself, and a GetRequiredService on that path throws
            // ObjectDisposedException out of host shutdown. Dropping a handler from a disposed
            // service's event is harmless; asking a disposed provider for the service is not.
            var scanStatus = serviceProvider.GetRequiredService<ScanStatusService>();

            return new AnalysisViewModel(
                () => serviceProvider.GetRequiredService<AnalysisCache>().EquipmentCombinations(),
                () => serviceProvider.GetRequiredService<AnalysisCache>().Filters(),
                (x, y, filter) => serviceProvider.GetRequiredService<AnalysisCache>().Correlation(x, y, filter),
                (metric, filter) => serviceProvider.GetRequiredService<AnalysisCache>().Distribution(metric, filter),
                (metric, groupBy, filter) => serviceProvider.GetRequiredService<AnalysisCache>().BoxPlot(metric, groupBy, filter),
                (metric, filter) => serviceProvider.GetRequiredService<AnalysisCache>().TimeSeries(metric, filter),
                filter => serviceProvider.GetRequiredService<AnalysisCache>().Matrix(filter),
                (metric, mode, groupA, groupB, from, to) =>
                    serviceProvider.GetRequiredService<AnalysisCache>().Compare(metric, mode, groupA, groupB, from, to),
                // Read once, for the four display.analysis keys. The write side is the one serialized
                // chain over the display document, the same writer the dashboard's columns use.
                display: settingsStore.GetDisplay(),
                writeDisplay: serviceProvider.GetRequiredService<DisplayColumnWriter>().Write,
                post: null,
                logger: serviceProvider.GetRequiredService<ILogger<AnalysisViewModel>>(),
                // The page's refresh trigger: every event that drops one of the memos behind it, as
                // one subscription the page follows through the pair StatisticsViewModel takes. The
                // three are the process-level notification (a general save and a completed correlation
                // pass), a completed scan, and an alias source save, which are exactly the three that
                // invalidate AnalysisCache. Without them this page, a singleton, is the page the reader
                // left: they scan a night taken with a second rig or rename an alias group, come back,
                // and find the new rig in neither the equipment picker nor Compare's two group
                // pickers, over figures read before the scan, with no control on the page able to
                // recover either.
                //
                // Composed HERE rather than by giving the page a second seam: the page takes values and
                // delegates and names no service (spec 18.3), and one composite keeps its own rule,
                // "re-read everything, once, whatever moved", in one place.
                //
                // The Statistics page is NOT routed through this. It follows ScanStatusService itself,
                // and raising the notification from a finished scan as well would refresh it twice for
                // one scan; AppHostTests pins that no ScanFinished handler here raises it.
                subscribeDerivedDataChanged: handler =>
                {
                    derivedDataNotifier.Changed += handler;
                    scanStatus.ScanFinished += handler;
                    settingsStore.AliasSourcesChanged += handler;
                },
                unsubscribeDerivedDataChanged: handler =>
                {
                    derivedDataNotifier.Changed -= handler;
                    scanStatus.ScanFinished -= handler;
                    settingsStore.AliasSourcesChanged -= handler;
                });
        });

        // Spec 12.6's Activity page (Phase 9 Task 4). A singleton for the reason DashboardViewModel
        // is one: the filter pills, the search term and the pages already loaded are session state,
        // so navigating away and back keeps them.
        //
        // Delegates, never the query and repository objects, so the page constructs in a unit test
        // with lambdas and no database (design-spec 18.3). The retention window is read per prune
        // rather than by value, because the Storage settings tab can change it while this page is
        // alive. PruneRetention is ActivityRepository's whole prune-now action, the activity_pruned
        // event included; nothing in the page re-implements any of it.
        builder.Services.AddSingleton(serviceProvider => new ActivityViewModel(
            (filters, before, limit) => serviceProvider
                .GetRequiredService<ActivityQuery>()
                .Page(filters, before, limit),
            () => settingsStore.GetGeneral().ActivityRetentionDays,
            serviceProvider.GetRequiredService<ActivityRepository>().PruneRetention,
            settingsStore.GetGeneral,
            // Ruling questions.md Q19: ScanStatusService.ScanFinished is the only automatic refresh
            // and Refresh is a button. Never a direct ScanCoordinator subscription, and no poll.
            scanStatus: serviceProvider.GetRequiredService<ScanStatusService>(),
            post: null,
            delay: null,
            logger: serviceProvider.GetRequiredService<ILogger<ActivityViewModel>>(),
            // Task 7 (PAR-017, spec 12.6). The one door into general.activity_seen_at, the same
            // MutateGeneral delegate every other writer of the general document takes.
            mutateGeneral: settingsStore.MutateGeneral,
            // The rail badge's count (spec 12.6: "the count of unseen rows"), ignoring every
            // filter and every search, which ActivityQuery.Page's Total cannot answer.
            countUnseen: serviceProvider.GetRequiredService<ActivityQuery>().CountUnseen));

        // Spec 12.8's Diagnostics page (Phase 10 Task 1). ONE instance, reached from the rail and
        // from the Settings tab strip through the same factory (coordinator ruling Q3): one page,
        // one refresh, and from Task 3 one export button. A singleton for that reason rather than
        // for session state, and the container owns its lifetime, which is why
        // SettingsViewModel.Dispose skips this tab.
        //
        // A delegate, never the service object, so the page constructs in a unit test with a
        // lambda and no database (design-spec 18.3). The post-scan refresh comes from
        // ScanStatusService.ScanFinished, never a direct ScanCoordinator subscription, and there
        // is no poll (ruling Q31).
        //
        // Spec 12.8's log viewer (Phase 10 Task 2) is hosted by that page and disposed with it.
        // The reader and the viewer are registered as factories, not instances, for the reason
        // AliasMapCache's registration is one: the container disposes only the singletons it
        // constructed. Disposing the viewer twice is harmless, and the page's own Dispose is what
        // stops its follow-tail loop when the page goes before the host does.
        var logDirectory = appWriter.ResolveAppDataPath(DiagnosticsService.LogDirectoryName);
        builder.Services.AddSingleton(_ => new LogReader(logDirectory));
        builder.Services.AddSingleton(serviceProvider => new LogViewerViewModel(
            serviceProvider.GetRequiredService<LogReader>(),
            settingsStore.MutateGeneral,
            serviceProvider.GetRequiredService<ShellIntegration>(),
            logDirectory,
            // Read once, here, from the document this method already loaded, so the picker does
            // not open a SQLite read on the UI thread at page construction (phase review item 2).
            ParseViewerLevel(initialGeneral.LogLevel),
            post: null,
            delay: null,
            // Ruling D3: the copy cap is min(50000, app_log_max_rows). Computed once here, from
            // the document this method already loaded; departure 2 (task1-report.md) is why this
            // is the copy cap and the viewer cap's construction-time seed and not LogRingBuffer's
            // 500-entry capacity, which this key does not govern.
            copyAllCap: Math.Min(LogViewerViewModel.DefaultCopyAllCap, initialGeneral.AppLogMaxRows),
            logger: serviceProvider.GetRequiredService<ILogger<LogViewerViewModel>>(),
            initialActivityRetentionDays: initialGeneral.ActivityRetentionDays,
            initialAppLogRetentionDays: initialGeneral.AppLogRetentionDays,
            initialAppLogMaxRows: initialGeneral.AppLogMaxRows,
            // The view's code-behind installs the real one when it attaches (section 6.2); null
            // here is the same "no export surface yet" shape DiagnosticsViewModel's own
            // pickDestination seam uses.
            saveLogAsDestinationPicker: null,
            // Task 7 (PAR-016, questions.md Q8): beside ExportBundle on DiagnosticsService, reached
            // by delegate so FileSafetyTest's BeginExportAllowlist needs no second entry.
            exportLog: serviceProvider.GetRequiredService<DiagnosticsService>().ExportLog,
            appWriter: appWriter));
        builder.Services.AddSingleton(serviceProvider => new DiagnosticsViewModel(
            serviceProvider.GetRequiredService<DiagnosticsService>().Snapshot,
            serviceProvider.GetRequiredService<UnresolvedNamesViewModel>(),
            scanStatus: serviceProvider.GetRequiredService<ScanStatusService>(),
            post: null,
            logger: serviceProvider.GetRequiredService<ILogger<DiagnosticsViewModel>>(),
            logViewer: serviceProvider.GetRequiredService<LogViewerViewModel>(),
            // Spec 16.3's export, as a method group: the page holds a delegate, exactly as it
            // holds the snapshot, and the service stays sealed with a non-virtual member. The
            // destination seam is left null here on purpose: DiagnosticsView's code-behind
            // installs the save dialog when it attaches, and that dialog is the only source of an
            // export path in the application (coordinator ruling Q10).
            exportBundle: serviceProvider.GetRequiredService<DiagnosticsService>().ExportBundle));
        builder.Services.AddSingleton<Func<DiagnosticsViewModel>>(
            serviceProvider => serviceProvider.GetRequiredService<DiagnosticsViewModel>);

        // Spec 17.1's update flow and spec 12.7's About tab (Phase 10 Task 4).
        //
        // VelopackUpdateChecker is the one type in the solution that names the updater's own
        // types; everything else here talks to IUpdateChecker, which is what keeps the test suite
        // off the network. BuildInfo is given that same checker rather than building a second
        // one, so the process constructs one update manager.
        //
        // Nothing below starts a loop or reaches the network: UpdateService.Start is called from
        // App.axaml.cs beside the watcher and the scheduler, so the CLI never checks for updates
        // (spec 15), and every path is gated on the updater having installed this build.
        builder.Services.AddSingleton<IUpdateChecker>(serviceProvider => new VelopackUpdateChecker(
            serviceProvider.GetRequiredService<ILogger<VelopackUpdateChecker>>()));
        builder.Services.AddSingleton(serviceProvider => BuildInfo.FromProcess(
            serviceProvider.GetRequiredService<IUpdateChecker>(),
            serviceProvider.GetRequiredService<ILogger<BuildInfo>>()));
        // Spec 12.11 behaviour 8's Startup shortcut (Phase 11 Task 3). VelopackStartupShortcut is
        // the second file in the solution that names the updater's own types
        // (VelopackUpdateChecker is the first); everything else talks to IStartupShortcut, which
        // is what keeps the test suite off the real Startup folder. IsSupported reads the same
        // BuildInfo.IsInstalled the About tab's update button reads, so the two cannot disagree
        // about which build this is.
        builder.Services.AddSingleton<IStartupShortcut>(serviceProvider => new VelopackStartupShortcut(
            serviceProvider.GetRequiredService<BuildInfo>(),
            serviceProvider.GetRequiredService<ILogger<VelopackStartupShortcut>>()));
        builder.Services.AddSingleton(serviceProvider => new UpdateService(
            serviceProvider.GetRequiredService<BuildInfo>(),
            serviceProvider.GetRequiredService<IUpdateChecker>,
            // The one App-layer mirror of ScanCoordinator, never a second subscription: spec
            // 17.1's "nothing is applied silently while the user is mid-scan" reads this.
            serviceProvider.GetRequiredService<ScanStatusService>(),
            activityRepository,
            // A delegate rather than the service, because the prompt's page calls
            // UpdateService.ApplyAndRestart back. Resolved inside the lambda so a session that is
            // never offered an update builds no dialog service.
            showPrompt: () => serviceProvider.GetRequiredService<UpdatePromptService>().ShowAsync(),
            logger: serviceProvider.GetRequiredService<ILogger<UpdateService>>()));
        // The fifth user of the one modal host (TRACKING section 6 item 22). The page is built
        // from the update service's current state at show time, and is null when that state has
        // moved on, which is the case the service's own prompt request cannot rule out.
        builder.Services.AddSingleton(serviceProvider => new UpdatePromptService(
            () =>
            {
                var updates = serviceProvider.GetRequiredService<UpdateService>();
                var state = updates.State;
                if (state.Phase != UpdatePhase.ReadyToApply || state.AvailableVersion is null)
                {
                    return null;
                }

                var buildInfo = serviceProvider.GetRequiredService<BuildInfo>();
                return new UpdatePromptViewModel(
                    state.AvailableVersion,
                    buildInfo.Version,
                    updates.Channel,
                    state.ReleaseNotes,
                    updates.ApplyAndRestart);
            },
            serviceProvider.GetRequiredService<ModalHost>()));
        // Spec 12.7's About tab. The log directory is the one AppHost already composed for the
        // sink and the log reader; the tab composes no path of its own.
        builder.Services.AddSingleton(serviceProvider => new AboutTabViewModel(
            serviceProvider.GetRequiredService<BuildInfo>(),
            serviceProvider.GetRequiredService<UpdateService>(),
            serviceProvider.GetRequiredService<ShellIntegration>(),
            logDirectory,
            post: null,
            logger: serviceProvider.GetRequiredService<ILogger<AboutTabViewModel>>()));

        builder.Services.AddSingleton(serviceProvider =>
        {
            var shell = new MainWindowViewModel(
            initialGeneral,
            serviceProvider.GetRequiredService<DashboardViewModel>(),
            serviceProvider.GetRequiredService<StatusBarViewModel>(),
            serviceProvider.GetRequiredService<SettingsViewModel>(),
            // Spec 12.5's Statistics page and spec 12.6's Activity page, resolved on the first
            // visit rather than here (FIXER LIST F21). Both are still DI singletons, so session
            // state survives navigating away and back; what changed is that resolving the shell no
            // longer runs Task 2's full-library aggregate and the activity first page on an
            // application start that never opens either.
            serviceProvider.GetRequiredService<StatisticsViewModel>,
            // Spec 12.14's Analysis page, the sixth rail destination (ruling A3), on the same lazy
            // footing: the page reads the display document and resolves the cache in its own
            // constructor, so an application start that never opens it builds neither.
            serviceProvider.GetRequiredService<AnalysisViewModel>,
            serviceProvider.GetRequiredService<ActivityViewModel>,
            // Spec 12.8's Diagnostics page, on the same lazy footing and resolving the same
            // singleton the Settings Diagnostics tab resolves (ruling Q3).
            serviceProvider.GetRequiredService<Func<DiagnosticsViewModel>>(),
            // Spec 12.2's row click navigation. The shell holds one nullable detail overlay and
            // builds its page through this delegate (ruling Q9).
            serviceProvider.GetRequiredService<Func<string, DateOnly?, TargetDetailViewModel>>());

            // Phase 9 FIXER item 2 and spec 5.8.1's content_width. The Settings Display tab writes
            // both keys while this shell is alive, so the window's root font size and the content
            // region's maximum width follow the document rather than the value read at startup.
            // Posted to the UI thread because GeneralChanged is raised on the saving thread and
            // both properties drive layout.
            settingsStore.GeneralChanged += (_, general) => UiPost.Default(() => shell.ApplyGeneral(general));

            return shell;
        });

        // Chart selection and its write path (Phase 6 Task 7). Method groups rather than the
        // store itself, so the writer stays constructible in a unit test with no database
        // (design-spec 18.3), and the same shape as the dashboard's column chain above.
        //
        // ChartSelectionViewModel is a singleton for the same reason DashboardViewModel is: spec
        // 5.8.3 is one document, so a metric toggled on the cross-session chart is also on in the
        // per-session chart, and the selection outlives one visit to one target's page. It takes
        // the initial document by value and the alias map through a Func<>, never a store.
        //
        // The Func<> is deferred, not free: AliasMapCache.Current can fall through to a
        // synchronous SQLite settings read. Review finding 2: the view-model's constructor
        // therefore never invokes it, and the first call is from OfferFilters, after the target's
        // data has loaded. Phase review item 2's rule stands, no settings read on a view-model
        // construction path.
        builder.Services.AddSingleton(serviceProvider => new GraphSettingsWriter(
            settingsStore.GetGraph,
            settingsStore.SaveGraph,
            serviceProvider.GetRequiredService<ILogger<GraphSettingsWriter>>()));
        builder.Services.AddSingleton(serviceProvider => new ChartSelectionViewModel(
            initialGraph,
            serviceProvider.GetRequiredService<GraphSettingsWriter>(),
            () => serviceProvider.GetRequiredService<AliasMapCache>().Current));

        // Spec 12.1's setup wizard (Phase 9 Task 9), and the shallow probe it shares with the
        // Settings Library tab.
        //
        // One probe, two callers (design-lessons rule 1): the probe below is bound into the
        // Library tab's probeSupportedFiles seam as well, so a folder's count on the wizard and
        // the same folder's count on the tab come from one implementation. It reads directory
        // entries through UserFiles and opens nothing.
        builder.Services.AddSingleton(_ => new SupportedFileProbe());

        // A factory, not a singleton: each opening reads the stored document again, so "Run setup
        // again" shows what is actually saved rather than the first run's fields.
        builder.Services.AddSingleton<Func<SetupWizardViewModel>>(serviceProvider => () =>
            new SetupWizardViewModel(
                settingsStore.GetGeneral,
                // MutateGeneral, like every other general-document writer in this application:
                // the read, the mutation, the validation and the write happen under the store's
                // own gate in one critical section (Task 5 review escalation).
                settingsStore.MutateGeneral,
                (folder, token) => Task.Run(
                    () => serviceProvider.GetRequiredService<SupportedFileProbe>().Count(folder, token),
                    token),
                // The Settings Storage tab's own free-space read, not a second one
                // (collision map's designated owner table). Invoked off the UI thread by the step.
                StorageTabViewModel.ReadVolumeSpace,
                // The app data default, not appWriter.ThumbnailCacheRoot, which is the effective
                // root and equals whatever custom thumbnail_cache_dir is stored. Step 2 stores ""
                // when the typed path equals this one, so binding the effective root would make a
                // re-run match a custom path against itself and silently reset it to the default
                // (Task 9 review, Important finding 1). This is exactly what
                // ResolveThumbnailCacheRoot above treats "" as.
                () => Path.Combine(appWriter.AppDataRoot, "thumbnails"),
                // HANDOFF.md section 5: the roots override is how the first scan sees the folders
                // just chosen, even before the settings save has propagated, and the trigger is
                // ScanTrigger.FirstRun, which spec 5.13 records in scan_runs.trigger.
                (roots, token) => serviceProvider
                    .GetRequiredService<ScanCoordinator>()
                    .RunAsync(ScanTrigger.FirstRun, roots, token),
                // Resolved inside the lambda, like every other coordinator use here.
                () => serviceProvider.GetRequiredService<ScanCoordinator>().Cancel(),
                // Spec 12.1: "Finishing writes setup_complete = true and navigates to the
                // Dashboard." The shell owns the rail, so the navigation is its own selection.
                navigateToDashboard: () =>
                {
                    var shell = serviceProvider.GetRequiredService<MainWindowViewModel>();
                    shell.Selected = shell.Items.First(item => item.Key == "dashboard");
                },
                // Ruling: progress comes from ScanStatusService, never from a second
                // ScanCoordinator subscription.
                scanStatus: serviceProvider.GetRequiredService<ScanStatusService>(),
                logger: serviceProvider.GetRequiredService<ILogger<SetupWizardViewModel>>(),
                // Spec 12.1 step 2's data location (Phase 10 Task 9). The same three seams the
                // Settings Storage tab takes, so the two surfaces record a move one way.
                dataRoot: () => resolution,
                requestDataRootMove: serviceProvider.GetRequiredService<DataRootMoveRequest>().Request,
                cancelDataRootMove: serviceProvider.GetRequiredService<DataRootMoveRequest>().Cancel));

        // Phase 10 Task 9's four registrations, appended rather than inserted among the lists
        // above. All four are factories, so none of them hands the container an object it did not
        // construct and the instance-registration census stays at seven (ruling Q24).
        builder.Services.AddSingleton(_ => resolution);
        builder.Services.AddSingleton(_ => relocation);
        builder.Services.AddSingleton(_ => new DataRootPointer(appWriter));
        // The one place a data location the user picked is recorded. The view-models never touch
        // the pointer file: they hand a path here and get back null, or a one-line refusal to show.
        builder.Services.AddSingleton(serviceProvider => new DataRootMoveRequest(
            destination => RequestDataRootMove(
                serviceProvider.GetRequiredService<DataRootPointer>(), appWriter.AppDataRoot, destination),
            () => serviceProvider.GetRequiredService<DataRootPointer>().CancelMove()));

        // The third user of the one modal host (TRACKING item 22), shaped exactly like
        // MergeDialogService and PreviewModalService. No second host.
        builder.Services.AddSingleton(serviceProvider => new SetupWizardService(
            serviceProvider.GetRequiredService<Func<SetupWizardViewModel>>(),
            serviceProvider.GetRequiredService<ModalHost>()));

        // Last, so the seam sees every registration this method made. Null in production.
        inspectServices?.Invoke([.. builder.Services]);

        return builder.Build();
    }

    /// <summary>
    /// Drops every in-process memo derived from the catalogue, after an action that rewrote it.
    /// </summary>
    /// <remarks>
    /// FIXER LIST F22 and design-lessons rule 1: the third destructive Maintenance action is where
    /// this stops being two pasted lines per binding and becomes one member. <c>StatsCache</c>
    /// carries no TTL by design (questions.md Q7), so a memo it holds survives for the life of the
    /// process unless something drops it; <c>RigBaselinesCache</c> would otherwise serve
    /// pre-rebuild baselines until its five minute TTL expired. Every future action that rewrites
    /// <c>targets</c>, <c>images</c> or the frame-to-target mapping calls this instead of
    /// remembering which caches exist.
    /// <para>
    /// Both caches are resolved here rather than captured, which is what keeps an application that
    /// never opens the Statistics page from building a <c>StatsCache</c> at all.
    /// </para>
    /// </remarks>
    private static void InvalidateDerivedCaches(IServiceProvider serviceProvider)
    {
        serviceProvider.GetRequiredService<StatsCache>().Invalidate();
        serviceProvider.GetRequiredService<RigBaselinesCache>().Invalidate();

        // Spec 12.14's Analysis memo (Phase 17, ruling A6): it rides this one staleness handler and
        // has no TTL, no second invalidation path and no subscription of its own. Its own
        // display.analysis save does NOT reach here, because this handler follows GeneralChanged
        // and display.analysis lives in the display document; AppHostTests pins that.
        serviceProvider.GetRequiredService<AnalysisCache>().Invalidate();

        // Phase 15B fixer item 34. Phd2NightQuery memoizes its one "does this library carry any
        // guide log at all" EXISTS, which is what decides whether the Guiding band is built at
        // all (spec 12.4). A maintenance action that rewrites the catalogue, a database reset or a
        // settings save would otherwise leave the band showing the answer this process first read,
        // until it was restarted. Every caller of this member drops the memos BEFORE it tells
        // anything to rebuild, which is what makes the reset useful rather than merely correct.
        serviceProvider.GetRequiredService<Phd2NightQuery>().InvalidateGuideLogMemo();
    }

    /// <summary>
    /// Whether two general documents differ only in keys on the inert list: the keys no derived
    /// data is built from (Phase 16 phase review P2-2, coordinator ruling 1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one predicate the <c>GeneralChanged</c> subscription in <see cref="Build"/> asks, and
    /// the reason that handler can be cheap without being an allowlist of inputs. It fails closed:
    /// anything that is not one of the named inert keys counts as a move, including a key this
    /// build does not recognise, which <c>GeneralSettings.ExtensionData</c> carries, and a member
    /// added to <c>GeneralSettings</c> later, which joins the comparison with no edit here.
    /// </para>
    /// <para>
    /// The comparison is the two documents serialised with the inert keys blanked, rather than a
    /// property-by-property equality: a record comparison would answer "moved" for every save,
    /// because a <c>JsonElement</c> from one parse is never equal to the same JSON from another,
    /// and it would not see a key that only <c>ExtensionData</c> holds. The options are the
    /// defaults <c>SettingsStore</c> writes these documents with.
    /// </para>
    /// </remarks>
    internal static bool OnlyInertKeysMoved(Core.Settings.GeneralSettings before, Core.Settings.GeneralSettings after)
        => string.Equals(WithoutInertKeys(before), WithoutInertKeys(after), StringComparison.Ordinal);

    // THE INERT LIST, and the only place it is written: spec 5.8.1's four wbpp_ keys, which are the
    // export page's own settings. Nothing StatsCache, RigBaselinesCache or Phd2NightQuery builds
    // reads one, and the quality panel writes the last of them once per coalesced chip edit and
    // again when the page closes.
    private static string WithoutInertKeys(Core.Settings.GeneralSettings general)
        => System.Text.Json.JsonSerializer.Serialize(general with
        {
            WbppDefaultOsDocument = null,
            WbppStagingPathDocument = null,
            WbppExclusionsDocument = null,
            WbppQualityByRigDocument = null,
        });

    // Spec 12.5's "thumbnail cache bytes" figure (Phase 9 Task 2, questions.md Q12). Sums the
    // lengths AppWriter.EnumerateThumbnailFiles already reports for the flat subdirectories
    // ThumbnailCache writes into, which is a read through the same authorization every thumbnail
    // write goes through: no directory walk of a scan root, no process spawn, no `du`.
    //
    // The directory names come from ThumbnailCache.Directories, which derives them from its one
    // kind-to-directory mapping. They used to be three literals repeated here with a note saying a
    // fourth thumbnail kind would be silently omitted from the total; fixer list code item 7 closed
    // that by exposing the list instead, so the hazard is structural rather than noted.
    private static long ThumbnailCacheBytes(AppWriter appWriter)
    {
        var total = 0L;
        foreach (var directory in ThumbnailCache.Directories)
        {
            foreach (var file in appWriter.EnumerateThumbnailFiles(directory, "*"))
            {
                total += file.Length;
            }
        }

        return total;
    }

    /// <summary>The one route from a view-model to the data location pointer (spec 17.2).
    /// </summary>
    /// <remarks>
    /// A named record rather than a bare <c>Func&lt;string, string?&gt;</c> registration: the two
    /// view-models hand a path in and get back null or a one-line refusal to show, and neither of
    /// them touches the pointer file, creates a directory, or copies anything.
    /// </remarks>
    /// <param name="Request">Records a move to the given root. Null on success, a refusal
    /// otherwise.</param>
    /// <param name="Cancel">Clears a pending move.</param>
    public sealed record DataRootMoveRequest(Func<string, string?> Request, Action Cancel);

    /// <summary>
    /// Spec 17.2's whole app data root decision for one start: resolve the root, perform whatever
    /// move the pointer or a legacy install asks for, and hand back the writer the rest of
    /// <c>Build</c> uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here rather than inline in <c>Build</c> so the startup body gains six lines and Task 8's
    /// instance-registration census block is untouched.
    /// </para>
    /// <para>
    /// When the root came from the explicit override or from <c>GALACTILOG_APPDATA</c>, the
    /// pointer is neither read nor written and no relocation runs. That is the property that keeps
    /// the suite and every packaging verification run off a real library.
    /// </para>
    /// <para>
    /// Nothing here deletes anything except the write probe's own file, which is created and
    /// removed under the resolved root.
    /// </para>
    /// </remarks>
    private static (AppWriter Writer, AppDataRootResolution Resolution, RelocationOutcome Relocation)
        PrepareAppDataRoot(
            string? appDataRootOverride, string? pointerPathOverride, AppDataRootFolders? folderOverride)
    {
        var pointerPath = pointerPathOverride ?? AppWriter.DefaultDataRootPointerPath;
        var folders = folderOverride ?? AppDataRootFolders.System;

        // Can throw AppDataRootUnavailableException, which propagates out of Build uncaught and is
        // handled in Program.Main and in CliDispatcher.
        var resolution = AppDataRootResolver.Resolve(
            appDataRootOverride,
            Environment.GetEnvironmentVariable(AppDataRootResolver.EnvironmentVariableName),
            pointerPath,
            folders);

        if (resolution.Source is AppDataRootSource.ExplicitOverride or AppDataRootSource.EnvironmentVariable)
        {
            return (OpenRoot(resolution.Root, pointerPath, resolution.Source), resolution, RelocationOutcome.None);
        }

        var move = ChooseMove(resolution, folders);
        if (move is null)
        {
            var writer = OpenRoot(resolution.Root, pointerPath, resolution.Source);

            // Review finding I2: a pointer that exists and cannot be read is left exactly where it
            // is. Read returns null for a torn or hand-edited document as well as for an absent
            // one, so recording the default root here would overwrite the only record of the root
            // the user chose. The warning is already on the startup line and on the Storage tab;
            // the file stays for the user to repair or delete themselves.
            if (resolution.Source == AppDataRootSource.Default
                && resolution.PointerWarning is null
                && DataRootPointer.Read(pointerPath)?.DataRoot is null)
            {
                // So the next start reads a pointer rather than recomputing a default.
                new DataRootPointer(writer).RecordRoot(writer.AppDataRoot);
            }

            return (writer, resolution, RelocationOutcome.None);
        }

        var (source, destination) = move.Value;
        AppWriter target;
        try
        {
            // The destination is not the resolved root, so a destination that cannot be created is
            // a relocation failure rather than a reason not to start: the old root is still there
            // and still holds the data.
            target = OpenRoot(destination, pointerPath, AppDataRootSource.Default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or UnauthorizedPathException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (
                OpenRoot(resolution.Root, pointerPath, resolution.Source),
                resolution,
                RelocationOutcome.None with
                {
                    From = source,
                    To = destination,
                    Failure = $"'{destination}' could not be prepared: {ex.Message}",
                });
        }

        var outcome = AppDataRelocation.Run(source, target);
        if (outcome.Moved)
        {
            new DataRootPointer(target).CompleteMove(target.AppDataRoot, source);
            return (target, resolution, outcome);
        }

        // The destination directory created just above is left in place. It holds no database, and
        // this application deletes nothing. data_root is not changed on a failure, so a
        // half-copied destination is never opened, and pending_root survives so the Storage tab can
        // offer Cancel and the next start retries.
        return (OpenRoot(resolution.Root, pointerPath, resolution.Source), resolution, outcome);
    }

    // The move this start performs, as (source root, destination root), or null for none.
    private static (string Source, string Destination)? ChooseMove(
        AppDataRootResolution resolution, AppDataRootFolders folders)
    {
        if (resolution.PendingRoot is { Length: > 0 } pending)
        {
            // A pending move onto the root already in use is not a move; the pointer keeps it and
            // the Storage tab keeps offering Cancel.
            return string.Equals(
                Path.TrimEndingDirectorySeparator(pending),
                Path.TrimEndingDirectorySeparator(resolution.Root),
                StringComparison.OrdinalIgnoreCase)
                    ? null
                    : (resolution.Root, pending);
        }

        // The legacy adoption: the upgrade path for every install that predates Phase 10 Task 9.
        // Without it the first start after the update opens an empty library beside the user's
        // real one. It copies the six known names only and leaves the legacy root in place.
        var legacyDatabase = Path.Combine(folders.LegacyRoot, AppDataRelocation.DatabaseFileName);
        var currentDatabase = Path.Combine(resolution.Root, AppDataRelocation.DatabaseFileName);
        return resolution.Source == AppDataRootSource.Default
            && !UserFiles.Exists(currentDatabase)
            && UserFiles.Exists(legacyDatabase)
                ? (folders.LegacyRoot, resolution.Root)
                : null;
    }

    // Creates the root and proves it can be written, through AppWriter on both counts. The probe
    // file is removed whether or not the write succeeded; it is the only thing this task deletes.
    // A failure is an AppDataRootUnavailableException only when the pointer named the root: a
    // default root that cannot be written is a broken profile, not a data location problem, and
    // keeps the existing behaviour of propagating out of Build for Program.Main to report as 4.
    private static AppWriter OpenRoot(string root, string pointerPath, AppDataRootSource source)
    {
        var writer = new AppWriter(root, dataRootPointerPath: pointerPath);
        try
        {
            writer.CreateDirectory(writer.AppDataRoot);
            var probe = writer.ResolveAppDataPath(WriteProbeFileName);
            try
            {
                writer.WriteAllBytes(probe, []);
            }
            finally
            {
                writer.Delete(probe);
            }
        }
        catch (Exception ex) when (source == AppDataRootSource.Pointer)
        {
            throw new AppDataRootUnavailableException(writer.AppDataRoot, pointerPath, ex.Message);
        }

        return writer;
    }

    /// <summary>The write probe's file name, under the resolved root.</summary>
    internal const string WriteProbeFileName = ".galactilog-write-test";

    // Runs the rules a picked data location has to pass and records the move when it does.
    // Returns null on success and the refusal to show otherwise. The only disk read is the
    // destination's database probe, which is the one rule a view-model cannot check for itself.
    private static string? RequestDataRootMove(DataRootPointer pointer, string currentRoot, string destination)
    {
        var refusal = StorageTabViewModel.ValidateDataRoot(destination, currentRoot);
        if (refusal is not null)
        {
            return refusal;
        }

        try
        {
            if (UserFiles.Exists(Path.Combine(Path.GetFullPath(destination), AppDataRelocation.DatabaseFileName)))
            {
                return StorageTabViewModel.DataRootHasDatabase;
            }

            pointer.RequestMove(destination);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or PathTooLongException)
        {
            return $"That location could not be recorded: {ex.Message}";
        }
    }

    // What the startup line's {Relocation} property says.
    private static string DescribeRelocation(RelocationOutcome relocation)
        => relocation switch
        {
            { Moved: true } => $"moved {relocation.FilesCopied} files from {relocation.From}",
            { Failure: { Length: > 0 } failure } => $"failed: {failure}",
            _ => "none",
        };

    // Ruling Q18: a candidate whose source_name is an active target's primary_name is a Pass 2
    // duplicate and maps to that target's id as the loser; anything else is an unresolved OBJECT
    // string and maps to a name loser. The question is answered by MergePreviewQuery, which
    // already reads targets, and never by string-matching in a view-model.
    //
    // Internal so AppHostTests can assert both shapes without building a window.
    internal static MergeRequest ToMergeRequest(MergePreviewQuery preview, MergeCandidateRow row)
    {
        var loserId = preview.ActiveTargetIdByPrimaryName(row.SourceName);

        // Phase 7 fixer item 6: the candidate's source_name can resolve to the very target it
        // suggests (a Pass 2 row whose winner was later renamed to the loser's name, for one), and
        // a request with loser == winner opens a dialog whose Confirm can never become enabled.
        // The winner is dropped instead, so the dialog opens on the loser alone and the search box
        // chooses the survivor.
        var winnerId = loserId is not null && loserId == row.SuggestedTargetId ? null : row.SuggestedTargetId;

        return new MergeRequest(
            winnerId,
            loserId,
            loserId is null ? row.SourceName : null,
            row.Id);
    }

    /// <summary>
    /// Phase 9 FIXER item 5: brings a saved <c>general.default_page_size</c> to the singleton
    /// dashboard target list.
    /// </summary>
    /// <remarks>
    /// Two constraints, both of them load bearing. The assignment is posted to the UI thread,
    /// because <c>GeneralChanged</c> is raised on the saving thread and <c>PageSize</c>'s change
    /// notification drives an <c>ObservableCollection</c> reload. And it only assigns when the
    /// value actually changed, because setting <c>PageSize</c> resets the list to page one: an
    /// unrelated general save (a theme change, say) must not throw away the page the user is
    /// looking at. The check is repeated inside the post, since the list can have moved on between
    /// the raise and the post running.
    /// <para>
    /// Internal and static rather than an inline lambda so the rule is assertable without building
    /// a window (design-spec 18.3), in the shape <c>IsUsableAppDataOverride</c> already uses.
    /// </para>
    /// <para>
    /// Phase 14C Task 4 fix pass (task4-review.md): normalizes through
    /// <see cref="TargetListViewModel.NormalizePageSize"/>, spec 5.8.1's rule, rather than the
    /// constructor's own <c>Math.Max(1, ...)</c> clamp, and calls
    /// <see cref="TargetListViewModel.FollowPageSize"/> rather than assigning <c>PageSize</c>
    /// directly, so a stored value outside <c>PageSizeOptions</c> reads as 50 through every writer
    /// of the key, and the pager's own select moves with a page-size change made anywhere else
    /// (the Settings Display tab today) instead of being left to disagree with it.
    /// </para>
    /// </remarks>
    internal static void FollowDefaultPageSize(TargetListViewModel targets, int defaultPageSize, Action<Action> post)
    {
        var normalized = TargetListViewModel.NormalizePageSize(defaultPageSize);
        if (targets.PageSize == normalized && targets.SelectedPageSize == normalized)
        {
            return;
        }

        post(() =>
        {
            if (targets.PageSize != normalized || targets.SelectedPageSize != normalized)
            {
                targets.FollowPageSize(defaultPageSize);
            }
        });
    }

    // Plain Enum.TryParse works because design-spec 5.8.1's general.log_level values
    // (Verbose, Debug, Information, Warning, Error, Fatal) are spelled identically to
    // Serilog.Events.LogEventLevel's member names.
    private static LogEventLevel ParseLevel(string level)
        => Enum.TryParse<LogEventLevel>(level, out var parsed) ? parsed : LogEventLevel.Information;

    // The same parse into the log viewer's parallel vocabulary. GalactiLog.Core has no Serilog
    // reference and must not gain one (ruling Q5), so LogLineLevel is a separate enum whose member
    // names are pinned equal to LogEventLevel's by
    // LogViewerViewModelTests.LogLineLevel_NamesMatchSerilogLogEventLevelNames. The same fallback,
    // for the same reason: an unrecognized stored value is corrected silently rather than failing
    // a page.
    private static LogLineLevel ParseViewerLevel(string level)
        => Enum.TryParse<LogLineLevel>(level, out var parsed) ? parsed : LogLineLevel.Information;

    /// <summary>
    /// Task 7's tolerant pre-migration peek at <c>general.app_log_retention_days</c>, for the
    /// Serilog sink alone (questions.md Q7). <see cref="DatabasePaths.TryReadStoredGeneralJson"/>
    /// already returns null for a database that does not exist, a missing table or a missing row;
    /// this adds the same tolerance for a missing key or a document that does not parse, so
    /// nothing short of a genuine stored integer overrides the compile-time default 14 that
    /// <see cref="Core.Settings.GeneralSettings"/> itself ships.
    /// </summary>
    /// <remarks>
    /// Phase 14B fixer, phase review P2-1. The result is clamped to spec 5.8.1's declared 1 to
    /// 3650, the same range <c>SettingsStore.ClampGeneral</c> applies to every other reader of
    /// this key, because this is the one read path that bypasses <c>SettingsStore</c> and its
    /// value goes straight to Serilog as <c>retainedFileCountLimit</c>, which refuses anything
    /// below 1 with an <see cref="ArgumentException"/> thrown inside the
    /// <c>LoggerConfiguration</c> chain, before <c>Log.Logger</c> exists and so with nowhere to
    /// report it. The number read is taken with <c>TryGetInt32</c> rather than
    /// <c>GetInt32</c> for the same reason: a stored number the document holds but that does not
    /// fit <see cref="int"/> makes <c>GetInt32</c> throw <see cref="FormatException"/>, which is
    /// not a <c>JsonException</c> and would escape the catch below. Nothing this function reads
    /// can throw out of it and nothing it returns is outside the key's own range.
    /// </remarks>
    internal static int ReadStoredAppLogRetentionDaysOrDefault(string databaseFilePath)
    {
        const int DefaultAppLogRetentionDays = 14;
        const int MinAppLogRetentionDays = 1;
        const int MaxAppLogRetentionDays = 3650;

        var json = DatabasePaths.TryReadStoredGeneralJson(databaseFilePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return DefaultAppLogRetentionDays;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("app_log_retention_days", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.Number
                && value.TryGetInt32(out var days)
                ? Math.Clamp(days, MinAppLogRetentionDays, MaxAppLogRetentionDays)
                : DefaultAppLogRetentionDays;
        }
        catch (System.Text.Json.JsonException)
        {
            return DefaultAppLogRetentionDays;
        }
    }

    /// <summary>
    /// The one process-level "the derived data behind an open page has been rewritten" signal
    /// (Phase 15B fixer F2, with items 30 and 38).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Design-lessons rule 2. Two things can rewrite the guiding figures a page is showing without
    /// a scan: a general save, which can move the PHD2 profile map, the observer zone or the site,
    /// and a correlation re-run that ran to completion, which rewrites
    /// <c>images.guiding_rms_arcsec</c> across an arbitrary set of nights. One handler in
    /// <see cref="Build"/> is subscribed to both, drops the derived memos and then raises this;
    /// the staleness rule therefore lives at one choke point rather than as a check every page has
    /// to remember, and a page added later follows this rather than re-deriving which sources can
    /// make it stale.
    /// </para>
    /// <para>
    /// No payload: the pass rewrote rows across nights nobody enumerated, so the only truthful
    /// statement is "re-read". A subscriber follows through a subscribe and unsubscribe delegate
    /// pair and drops its handler in its own <c>Dispose</c>, because this instance outlives every
    /// page. It is not <c>IDisposable</c>: it owns nothing but its invocation list, which is what
    /// <c>AppHost_RegistersNoInstanceThatImplementsIDisposable</c> requires of it.
    /// </para>
    /// </remarks>
    internal sealed class DerivedDataNotifier
    {
        public event EventHandler? Changed;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }

    // The general settings memo the thumbnail cache reads on its worker threads. One mutable
    // field, written on the thread that raised GeneralChanged and read on the two pumps and the
    // scan thread, with the barrier a captured local cannot have.
    private sealed class GeneralSettingsHolder(Core.Settings.GeneralSettings initial)
    {
        private Core.Settings.GeneralSettings _value = initial;

        public Core.Settings.GeneralSettings Value
        {
            get => Volatile.Read(ref _value);
            set => Volatile.Write(ref _value, value);
        }
    }
}
