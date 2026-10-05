using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.Services;

/// <summary>
/// One whole reading of spec 12.8's seven field groups, taken at one instant. Immutable, and
/// shaped so Task 3's bundle (spec 16.3) serializes it directly: every property name here is the
/// C# name, and the snake_case JSON names are Task 3's <c>[JsonPropertyName]</c> attributes.
/// </summary>
/// <param name="GeneratedAt">When <c>DiagnosticsService.Snapshot</c> composed this reading.</param>
/// <param name="App">Spec 12.8's Versions group.</param>
/// <param name="Paths">Spec 12.8's Paths group, which is spec 17.2's table.</param>
/// <param name="Database">Spec 12.8's Database group.</param>
/// <param name="Scan">Spec 12.8's Scan group.</param>
/// <param name="Resolver">Spec 12.8's Resolver group.</param>
/// <param name="Unresolved">Spec 12.8's Unresolved group, from the one
/// <see cref="UnresolvedNamesQuery"/> the Settings Targets tab already reads.</param>
/// <param name="RecentErrors">Spec 12.8's Errors group: the newest
/// <c>DiagnosticsService.RecentErrorCount</c> entries of the in-memory warning ring (spec 16.1),
/// newest last. The ring holds 500; this is the page's cut of it, and Task 3's bundle takes its
/// own.</param>
public sealed record DiagnosticsSnapshot(
    DateTimeOffset GeneratedAt,
    AppDiagnostics App,
    PathDiagnostics Paths,
    DatabaseDiagnostics Database,
    ScanDiagnostics Scan,
    ResolverDiagnostics Resolver,
    IReadOnlyList<UnresolvedNameRow> Unresolved,
    IReadOnlyList<LogRingEntry> RecentErrors);

/// <summary>Spec 12.8's Versions group.</summary>
/// <param name="Version">The application assembly version, or <c>unknown</c>.</param>
/// <param name="GitSha">The commit the build came from, or <c>unknown</c> on a local build.
/// Bound by Task 4 through <c>DiagnosticsService</c>'s <c>gitSha</c> seam.</param>
/// <param name="Channel">The update channel, or <c>unknown</c>. Bound by Task 4 through
/// <c>DiagnosticsService</c>'s <c>channel</c> seam.</param>
/// <param name="Dotnet">The running .NET runtime version.</param>
/// <param name="Avalonia">The Avalonia assembly version.</param>
/// <param name="Sqlite">The SQLite library version behind this process.</param>
/// <param name="Os">The operating system version string.</param>
public sealed record AppDiagnostics(
    string Version, string GitSha, string Channel,
    string Dotnet, string Avalonia, string Sqlite, string Os);

/// <summary>Spec 17.2's paths, as spec 12.8's Paths group reports them.</summary>
/// <param name="AppData">The authorized app data root, the one place this application writes.
/// </param>
/// <param name="Database">The database file.</param>
/// <param name="Logs">The rolling log directory (spec 16.1).</param>
/// <param name="Thumbnails">The effective thumbnail cache root, which
/// <c>general.thumbnail_cache_dir</c> can move off the app data root.</param>
/// <param name="Catalogs">The shipped catalogue directory. It sits with the binaries, so it is
/// the one path here outside app data (spec 17.2).</param>
/// <param name="AppDataSource">Where the app data root came from: <c>default</c>, <c>pointer</c>,
/// <c>environment</c> or <c>override</c> (spec 12.8, 17.2). The path alone cannot say whether it
/// came from the user's own choice or from a stray environment variable, which is the first
/// question a support bundle is asked.</param>
/// <param name="StartupShortcut">Spec 12.11 behaviour 8's Startup shortcut, as spec 12.8's Paths
/// group and spec 16.3's bundle report it: <c>DiagnosticsService.StartupShortcutPresent</c>,
/// <c>StartupShortcutAbsent</c>, or <c>StartupShortcutNotApplicable</c> on a build the updater did
/// not install, or with no seam bound at all. A string rather than a bool, because "no shortcut
/// here" and "no shortcut possible" are different diagnoses (spec 12.11 behaviour 11). A fourth
/// value, <c>DiagnosticsService.Unknown</c>, is also reachable: a supported seam whose read of
/// the shortcut itself failed (Phase 11 Task 3 review, Important 2). Task 6's docs fixer records
/// this fourth value in spec 12.11 behaviour 11 and spec 12.8's Paths row.</param>
/// <param name="StartedMinimized">Spec 12.11 behaviour 9's <c>yes</c> or <c>no</c>
/// (<c>DiagnosticsService.StartedMinimizedYes</c> or <c>StartedMinimizedNo</c>). A
/// <c>Func&lt;bool&gt;</c> seam on <c>DiagnosticsService</c>, defaulting to false, until Phase 11
/// Task 4 binds it.</param>
public sealed record PathDiagnostics(
    string AppData, string Database, string Logs, string Thumbnails, string Catalogs,
    string AppDataSource, string StartupShortcut, string StartedMinimized);

/// <summary>Spec 12.8's Scan group.</summary>
/// <param name="IsRunning">Whether a scan is in flight, from <c>ScanStatusService</c>.</param>
/// <param name="CurrentTask">Spec 10.4's progress envelope task token, or an empty string when
/// idle.</param>
/// <param name="CurrentMessage">The envelope's message line.</param>
/// <param name="CurrentPercent">The envelope's percentage.</param>
/// <param name="HasDeterminatePercent">False when the envelope reports no total.</param>
/// <param name="LastRun">The newest <c>scan_runs</c> row, or null on a library that has never
/// scanned.</param>
/// <param name="WatcherStates">One row per configured scan root (spec 10.7).</param>
/// <param name="NextScheduledUtc">When the interval scheduler is next due to wake (spec 10.8),
/// or null when no wait is in flight.</param>
public sealed record ScanDiagnostics(
    bool IsRunning, string CurrentTask, string CurrentMessage,
    double CurrentPercent, bool HasDeterminatePercent,
    ScanRun? LastRun,
    IReadOnlyList<WatcherRootState> WatcherStates,
    DateTime? NextScheduledUtc);

/// <summary>Spec 12.8's Resolver group: the three database figures plus the two process
/// counters.</summary>
/// <param name="CachePositive"><c>catalog_cache</c> rows with a payload.</param>
/// <param name="CacheNegative">Negative rows.</param>
/// <param name="CacheExpired">Negative rows past the seven day TTL. A subset of
/// <paramref name="CacheNegative"/>, not a fourth bucket.</param>
/// <param name="Hits">Lookups this process answered from the cache without a network call.
/// </param>
/// <param name="Misses">Lookups the cache could not answer, an expired negative included
/// (spec 9.6).</param>
public sealed record ResolverDiagnostics(
    long CachePositive, long CacheNegative, long CacheExpired, long Hits, long Misses);
