using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace GalactiLog.App.Tests.Services;

/// <summary>
/// Spec 12.8's snapshot, composed by the one composer. Temp databases come from
/// <c>TestSupport/TempDatabase</c>; the watcher and the scheduler are real services over a real
/// settings store, because both are sealed with non-virtual members and the cheapest honest
/// substitute is the real thing.
/// </summary>
public class DiagnosticsServiceTests : IDisposable
{
    // Declared before the database, because the database file goes inside it: AppHost composes
    // the production connection string with AppWriter.ResolveAppDataPath, so the Paths group's
    // "everything except catalogs is under the app data root" claim is only testable when the
    // fixture places it the same way.
    private readonly string _appDataRoot = Directory.CreateTempSubdirectory("galactilog-diag-root-").FullName;
    private readonly TempDatabase _database;
    private readonly List<string> _scanRoots = [];
    private readonly List<IDisposable> _disposables = [];

    public DiagnosticsServiceTests()
        => _database = new TempDatabase("galactilog-diagnostics", _appDataRoot);

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _database.Dispose();
        foreach (var root in _scanRoots.Append(_appDataRoot))
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        GC.SuppressFinalize(this);
    }

    private string NewScanRoot()
    {
        var root = Directory.CreateTempSubdirectory("galactilog-diag-scan-").FullName;
        _scanRoots.Add(root);
        return root;
    }

    private sealed record Harness(
        DiagnosticsService Service,
        SettingsStore Settings,
        ResolverCounters Counters,
        LogRingBuffer Ring,
        ScanScheduler Scheduler,
        WatcherService Watcher,
        AppWriter Writer,
        string CatalogsDirectory);

    private Harness CreateHarness(
        IReadOnlyList<string>? scanRoots = null,
        Func<string>? gitSha = null,
        Func<string>? channel = null,
        Func<string>? appDataSource = null,
        IStartupShortcut? startupShortcut = null,
        Func<bool>? startedMinimized = null)
    {
        var settings = new SettingsStore(new SettingsRepository(_database.ConnectionString));
        if (scanRoots is not null)
        {
            settings.SaveGeneral(settings.GetGeneral() with { ScanRoots = [.. scanRoots] });
        }

        var connectionString = new DatabaseConnectionString(_database.ConnectionString);
        var counters = new ResolverCounters();
        var ring = new LogRingBuffer();
        // Review finding M6: an explicit temp pointer path, never the constructor default, which is
        // the real %APPDATA%GalactiLogdatapath.json.
        var writer = new AppWriter(
            _appDataRoot, dataRootPointerPath: Path.Combine(_appDataRoot, "datapath.json"));
        const string catalogs = @"C:\Program Files\GalactiLog\catalogs";

        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        var status = new ScanStatusService(coordinator, action => action());
        var watcher = new WatcherService(
            settings,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            NullLogger<WatcherService>.Instance,
            path => new FakeWatcherSource(path));
        var scheduler = new ScanScheduler(
            settings,
            () => false,
            _ => Task.CompletedTask,
            NullLogger<ScanScheduler>.Instance,
            (_, _) => Task.CompletedTask);

        _disposables.Add(status);
        _disposables.Add(watcher);

        var service = new DiagnosticsService(
            new DiagnosticsQuery(connectionString),
            new UnresolvedNamesQuery(connectionString),
            new ScanRunRepository(_database.ConnectionString),
            counters,
            ring,
            writer,
            catalogs,
            () => status,
            () => watcher,
            () => scheduler,
            // Phase 10 Task 3's three bundle reads. This suite asserts Snapshot(), which touches
            // none of them; DiagnosticsBundleTests is where they are exercised.
            new GalactiLog.Core.Diagnostics.LogReader(
                Path.Combine(_appDataRoot, DiagnosticsService.LogDirectoryName)),
            new ActivityQuery(connectionString),
            new SettingsRepository(_database.ConnectionString),
            gitSha,
            channel,
            appDataSource,
            startupShortcut,
            startedMinimized);

        return new Harness(service, settings, counters, ring, scheduler, watcher, writer, catalogs);
    }

    private static void Warn(LogRingBuffer ring, string message)
        => ring.Emit(new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Warning,
            exception: null,
            new MessageTemplate(message, [new TextToken(message)]),
            []));

    [Fact]
    public void Snapshot_PopulatesEveryGroup()
    {
        var harness = CreateHarness(scanRoots: [NewScanRoot()]);

        var snapshot = harness.Service.Snapshot();

        Assert.NotEqual(default, snapshot.GeneratedAt);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.App.Version));
        Assert.False(string.IsNullOrWhiteSpace(snapshot.App.Dotnet));
        Assert.False(string.IsNullOrWhiteSpace(snapshot.App.Avalonia));
        Assert.False(string.IsNullOrWhiteSpace(snapshot.App.Sqlite));
        Assert.False(string.IsNullOrWhiteSpace(snapshot.App.Os));
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Paths.AppData));
        Assert.Equal(DiagnosticsQuery.RowCountTables, snapshot.Database.RowCounts.Keys.ToArray());
        Assert.Single(snapshot.Scan.WatcherStates);
        Assert.NotNull(snapshot.Unresolved);
        Assert.NotNull(snapshot.RecentErrors);
        Assert.Equal(0, snapshot.Resolver.CachePositive);
    }

    [Fact]
    public void Snapshot_Paths_AreAllUnderTheAppWritersRoots_ExceptCatalogs()
    {
        var harness = CreateHarness();

        var paths = harness.Service.Snapshot().Paths;

        Assert.Equal(harness.Writer.AppDataRoot, paths.AppData);
        Assert.StartsWith(harness.Writer.AppDataRoot, paths.Database, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(harness.Writer.AppDataRoot, paths.Logs, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(harness.Writer.ThumbnailCacheRoot, paths.Thumbnails);

        // Spec 17.2: the shipped catalogues sit with the binaries, so this is the one path the
        // Paths group reports from outside app data.
        Assert.Equal(harness.CatalogsDirectory, paths.Catalogs);
        Assert.DoesNotContain(harness.Writer.AppDataRoot, paths.Catalogs, StringComparison.OrdinalIgnoreCase);
    }

    // Spec 12.8's Paths group (Phase 10 Task 9): the word beside the path that says whether it
    // came from the user's own choice, the pointer, the environment or a test override.
    [Fact]
    public void Snapshot_AppDataSource_UsesTheInjectedSeam()
    {
        var calls = 0;
        var harness = CreateHarness(appDataSource: () => { calls++; return "pointer"; });

        Assert.Equal("pointer", harness.Service.Snapshot().Paths.AppDataSource);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Snapshot_AppDataSource_IsUnknownWhenUnbound()
    {
        var harness = CreateHarness();

        Assert.Equal(DiagnosticsService.Unknown, harness.Service.Snapshot().Paths.AppDataSource);
    }

    [Fact]
    public void Snapshot_GitShaAndChannel_UseTheInjectedSeams()
    {
        var shaCalls = 0;
        var channelCalls = 0;
        var harness = CreateHarness(
            gitSha: () => { shaCalls++; return "abc1234"; },
            channel: () => { channelCalls++; return "stable"; });

        var app = harness.Service.Snapshot().App;

        Assert.Equal("abc1234", app.GitSha);
        Assert.Equal("stable", app.Channel);
        Assert.Equal(1, shaCalls);
        Assert.Equal(1, channelCalls);
    }

    [Fact]
    public void Snapshot_GitShaAndChannel_DefaultToUnknown_WhenNoSeamIsBound()
    {
        var harness = CreateHarness();

        var app = harness.Service.Snapshot().App;

        // The unbound default, which is the literal the web's health endpoint uses for missing
        // build metadata. AppHost binds both seams (Phase 10 Task 4), which the case below pins.
        Assert.Equal(DiagnosticsService.Unknown, app.GitSha);
        Assert.Equal(DiagnosticsService.Unknown, app.Channel);
    }

    [Fact]
    public void Snapshot_GitShaAndChannel_ReportBuildInfo_WhenBoundAsAppHostBindsThem()
    {
        // Phase 10 Task 4 binds the two seams to the one reader of build identity, so spec 12.8's
        // Versions group, spec 12.7's About tab and spec 16.3's bundle cannot disagree about which
        // build is running. Bound here exactly as AppHost binds them; a source scan in
        // BuildInfoTests pins the binding itself.
        var buildInfo = new BuildInfo("4.5.6.0", "0f1e2d3c4b5a", "rc", isInstalled: true);
        var harness = CreateHarness(
            gitSha: () => buildInfo.GitSha,
            channel: () => buildInfo.Channel);

        var app = harness.Service.Snapshot().App;

        Assert.Equal("0f1e2d3c4b5a", app.GitSha);
        Assert.Equal("rc", app.Channel);
        Assert.NotEqual(DiagnosticsService.Unknown, app.GitSha);
        Assert.NotEqual(DiagnosticsService.Unknown, app.Channel);
    }

    [Fact]
    public void Snapshot_RecentErrors_AreTheNewestFifty_WhenTheRingHoldsMore()
    {
        var harness = CreateHarness();
        for (var index = 0; index < DiagnosticsService.RecentErrorCount + 20; index++)
        {
            Warn(harness.Ring, "warning " + index);
        }

        var errors = harness.Service.Snapshot().RecentErrors;

        Assert.Equal(DiagnosticsService.RecentErrorCount, errors.Count);
        Assert.Equal("warning 20", errors[0].Message);
        Assert.Equal("warning 69", errors[^1].Message);
    }

    [Fact]
    public void Snapshot_RecentErrors_IsEmpty_WhenNothingHasWarned()
    {
        var harness = CreateHarness();

        Assert.Empty(harness.Service.Snapshot().RecentErrors);
    }

    [Fact]
    public void Snapshot_WatcherStates_ReportOneRowPerConfiguredRoot_WhenTheWatcherWasNeverStarted()
    {
        var first = NewScanRoot();
        var second = NewScanRoot();
        var harness = CreateHarness(scanRoots: [first, second]);

        var states = harness.Service.Snapshot().Scan.WatcherStates;

        // The CLI never starts the watcher (spec 15), so this is the shape a CLI-built host
        // reports: every configured root listed, none of them watched.
        Assert.Equal(new[] { first, second }, states.Select(state => state.Root));
        Assert.All(states, state => Assert.False(state.Watching));
        Assert.All(states, state => Assert.True(state.Reachable));
    }

    [Fact]
    public void Snapshot_WatcherStates_MarkAMissingRootUnreachable()
    {
        var missing = Path.Combine(NewScanRoot(), "unplugged");
        var harness = CreateHarness(scanRoots: [missing]);

        var state = Assert.Single(harness.Service.Snapshot().Scan.WatcherStates);

        Assert.Equal(missing, state.Root);
        Assert.False(state.Reachable);
    }

    [Fact]
    public void Snapshot_NextScheduled_IsNull_WhenTheSchedulerIsNotRunning()
    {
        var harness = CreateHarness();

        Assert.Null(harness.Service.Snapshot().Scan.NextScheduledUtc);
    }

    [Fact]
    public void Snapshot_LastRun_IsNull_OnALibraryThatHasNeverScanned()
    {
        var harness = CreateHarness();

        Assert.Null(harness.Service.Snapshot().Scan.LastRun);
    }

    [Fact]
    public void Snapshot_Resolver_ReportsTheProcessCounters()
    {
        var harness = CreateHarness();
        harness.Counters.RecordHit();
        harness.Counters.RecordHit();
        harness.Counters.RecordMiss();

        var resolver = harness.Service.Snapshot().Resolver;

        Assert.Equal(2, resolver.Hits);
        Assert.Equal(1, resolver.Misses);
    }

    /// <summary>
    /// Fixer list code item 4 (Task 3 escalation, Phase 1 behaviour). A hand-edited
    /// <c>user_settings.general</c> column that does not parse used to fail the whole snapshot
    /// through <c>WatcherService.DescribeRoots</c> and <c>SettingsStore.GetGeneral</c>, so the
    /// Diagnostics page and the bundle export both fell over for the one column a support bundle
    /// is most often collected to inspect. The page now reports it and carries on.
    /// </summary>
    [Fact]
    public void Snapshot_WithAnUnparseableGeneralDocument_ReportsIt_AndTheBundleStillExports()
    {
        var harness = CreateHarness(scanRoots: [NewScanRoot()]);
        Assert.Single(harness.Service.Snapshot().Scan.WatcherStates);

        // Written through the repository, not the store: nothing in the application can produce
        // this document, because MutateGeneral validates inside the write gate. A person editing
        // the database by hand can.
        var repository = new SettingsRepository(_database.ConnectionString);
        var row = repository.Load();
        row.General = "{ \"scan_roots\": ";
        repository.Save(row);

        var snapshot = harness.Service.Snapshot();

        // The watcher rows are the part that genuinely cannot be known.
        Assert.Empty(snapshot.Scan.WatcherStates);
        Assert.Contains(
            snapshot.RecentErrors,
            entry => entry.Message == DiagnosticsService.WatcherStatesUnavailable);

        // Everything that does not depend on that document is still read, or the page would be
        // reporting a settings failure as a database failure.
        Assert.Equal(DiagnosticsQuery.RowCountTables, snapshot.Database.RowCounts.Keys.ToArray());
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Paths.AppData));

        // And the export completes. The stored document is embedded verbatim with its parse error
        // under settings.general, which is what a support reader needs to see.
        var destination = Path.Combine(_appDataRoot, "bundle-with-bad-settings.json");
        harness.Service.ExportBundle(destination);

        var json = File.ReadAllText(destination);
        Assert.Contains("parse_error", json, StringComparison.Ordinal);

        // Spec 16.3's eleven keys carry no errors group, so the bundle's own record of the same
        // failure is the empty watcher_states list beside the embedded parse_error. Both halves
        // are asserted so a later edit cannot drop one of them.
        Assert.Contains("\"watcher_states\": []", json, StringComparison.Ordinal);
    }

    // ---- Phase 11 Task 3: the Startup shortcut and Started minimized Paths fields ------------

    [Fact]
    public void Snapshot_StartupShortcut_IsNotApplicable_WithNoSeam()
    {
        var harness = CreateHarness();

        Assert.Equal(
            DiagnosticsService.StartupShortcutNotApplicable,
            harness.Service.Snapshot().Paths.StartupShortcut);
    }

    [Fact]
    public void Snapshot_StartupShortcut_IsPresent_WhenTheSeamSaysSo()
    {
        var shortcut = new RecordingStartupShortcut { Present = true };
        var harness = CreateHarness(startupShortcut: shortcut);

        Assert.Equal(
            DiagnosticsService.StartupShortcutPresent,
            harness.Service.Snapshot().Paths.StartupShortcut);
    }

    [Fact]
    public void Snapshot_StartupShortcut_IsAbsent_WhenTheSeamSaysSo()
    {
        var shortcut = new RecordingStartupShortcut { Present = false };
        var harness = CreateHarness(startupShortcut: shortcut);

        Assert.Equal(
            DiagnosticsService.StartupShortcutAbsent,
            harness.Service.Snapshot().Paths.StartupShortcut);
    }

    [Fact]
    public void Snapshot_StartupShortcut_IsNotApplicable_WhenTheSeamIsUnsupported()
    {
        // Not applicable, not absent: a build the updater did not install could not have a
        // shortcut, which is a different diagnosis from "installed but none found".
        var shortcut = new RecordingStartupShortcut { IsSupported = false, Present = true };
        var harness = CreateHarness(startupShortcut: shortcut);

        Assert.Equal(
            DiagnosticsService.StartupShortcutNotApplicable,
            harness.Service.Snapshot().Paths.StartupShortcut);
    }

    [Fact]
    public void Snapshot_StartupShortcut_WhenTheSeamCannotRead()
    {
        // Phase 11 Task 3 review, Important 2: the fourth, reachable state. A supported seam
        // whose own read of the shortcut failed reports Unknown, the same word every other field
        // in this composer uses for "this could not be read", rather than a bool that would
        // collapse it into present or absent.
        var shortcut = new RecordingStartupShortcut { Present = null };
        var harness = CreateHarness(startupShortcut: shortcut);

        Assert.Equal(
            DiagnosticsService.Unknown,
            harness.Service.Snapshot().Paths.StartupShortcut);
    }

    [Fact]
    public void Snapshot_StartedMinimized_DefaultsToNo_WithNoSeam()
    {
        var harness = CreateHarness();

        Assert.Equal(
            DiagnosticsService.StartedMinimizedNo,
            harness.Service.Snapshot().Paths.StartedMinimized);
    }

    // Spec 12.8's Started minimized field with the seam bound (Phase 11 Task 4). AppHost binds it
    // to the one StartupState, which is the resolved answer: whether the process came up with no
    // window, whether the --minimized argument or general.start_minimized caused it. The two cases
    // below are that bound seam's two answers.
    [Fact]
    public void Snapshot_StartedMinimized_IsYes_WhenTheBoundSeamSaysSo()
    {
        var harness = CreateHarness(
            startedMinimized: () => new StartupState(StartedMinimized: true).StartedMinimized);

        Assert.Equal(
            DiagnosticsService.StartedMinimizedYes,
            harness.Service.Snapshot().Paths.StartedMinimized);
    }

    [Fact]
    public void Snapshot_StartedMinimized_IsNo_WhenTheBoundSeamSaysSo()
    {
        var harness = CreateHarness(
            startedMinimized: () => new StartupState(StartedMinimized: false).StartedMinimized);

        Assert.Equal(
            DiagnosticsService.StartedMinimizedNo,
            harness.Service.Snapshot().Paths.StartedMinimized);
    }

    [Fact]
    public void Bundle_CarriesBothNewFields_WithSnakeCaseNames()
    {
        var shortcut = new RecordingStartupShortcut { Present = true };
        var harness = CreateHarness(startupShortcut: shortcut, startedMinimized: () => true);
        var destination = Path.Combine(_appDataRoot, "bundle-with-startup-fields.json");

        harness.Service.ExportBundle(destination);

        var json = File.ReadAllText(destination);
        Assert.Contains("\"startup_shortcut\": \"present\"", json, StringComparison.Ordinal);
        Assert.Contains("\"started_minimized\": \"yes\"", json, StringComparison.Ordinal);
    }
}
