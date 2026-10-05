using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using GalactiLog.Core.Diagnostics;
using GalactiLog.Core.Io;
using GalactiLog.Data;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;

namespace GalactiLog.App.Services;

/// <summary>
/// The one composer of spec 12.8's seven field groups. Everything SQLite answers comes from
/// <see cref="DiagnosticsQuery"/> and <see cref="UnresolvedNamesQuery"/>; the App-layer halves
/// (versions, paths, the progress envelope, watcher state, the scheduler time, the log ring) are
/// read here and nowhere else, so the page and Task 3's bundle cannot report different numbers
/// for the same instant.
/// </summary>
/// <remarks>
/// <para>
/// Sealed, and it stays sealed: Task 3 appends <c>ExportBundle(string destination)</c> to this
/// class as a member rather than deriving from it, and Task 4 binds the two
/// <c>Func&lt;string&gt;</c> seams below from <c>AppHost</c> without editing a line of this file.
/// </para>
/// <para>
/// <paramref name="scanStatus"/>, <paramref name="watcher"/> and <paramref name="scheduler"/>
/// arrive as factories because of a registration cycle: <c>ScanStatusService</c> is registered
/// after this service would be, and the watcher and the scheduler are constructed with delegates
/// bound to <c>ScanCoordinator</c>. Resolving them inside the lambda makes registration order
/// irrelevant, which is the pattern <c>AppHost</c>'s <c>ScanCoordinator</c> registration already
/// uses for <c>ThumbnailCache</c>.
/// </para>
/// </remarks>
/// <param name="query">Spec 12.8's Database and Resolver cache figures.</param>
/// <param name="unresolved">Spec 9.7's unresolved names, reused rather than re-implemented.
/// </param>
/// <param name="scanRuns">Spec 5.13's run history, for the Scan group's last run.</param>
/// <param name="counters">The per-process resolver hit and miss counters.</param>
/// <param name="ring">Spec 16.1's 500-entry warning ring, which feeds the Errors group.</param>
/// <param name="appWriter">The authorized app data root. Every path in the Paths group is
/// composed through it, never with <c>Path.Combine</c> at this call site.</param>
/// <param name="catalogsDirectory">The shipped catalogue directory, passed in from
/// <c>AppHost</c>, which already resolved it. <c>StaticCatalogLoader.ResolveCatalogsDirectory</c>
/// is not called a second time here.</param>
/// <param name="scanStatus">The one App-layer subscriber to <c>ScanCoordinator</c>. This service
/// reads it and never subscribes to the coordinator itself.</param>
/// <param name="watcher">Spec 10.7's watcher, for its per-root state.</param>
/// <param name="scheduler">Spec 10.8's interval scheduler, for its next wake time.</param>
/// <param name="logReader">Spec 16.1's log files (Task 2). The bundle's <c>log_tail</c> is the
/// second caller of <c>LogReader.All</c>; nothing here reads a log file a second way.</param>
/// <param name="activity">Spec 5.12's activity feed. The bundle's <c>recent_events</c> is one
/// page of the existing read at its cap, not a second query.</param>
/// <param name="settingsRepository">The stored settings document, for the bundle's
/// <c>settings</c> key. Read through the repository rather than through <c>SettingsStore</c>,
/// because the bundle embeds the stored JSON verbatim and the store hands back a deserialized
/// shape that would drop anything the C# record does not model.</param>
/// <param name="gitSha">The build's commit. A seam Task 4 binds to <c>BuildInfo.GitSha</c>;
/// until then it reports the literal <c>unknown</c>, which is what a local build reports too.
/// </param>
/// <param name="channel">The update channel. A seam Task 4 binds to the update manager's
/// channel; until then it reports the literal <c>unknown</c>.</param>
/// <param name="appDataSource">Where the app data root came from (spec 17.2). A seam Task 9 binds
/// to <c>AppDataRootResolver.SourceLabel</c>; unbound it reports the literal <c>unknown</c>.
/// </param>
/// <param name="startupShortcut">Spec 12.11 behaviour 8's seam, whose one implementation is
/// <c>VelopackStartupShortcut</c> (Phase 11 Task 3). Optional and trailing, like
/// <paramref name="gitSha"/> and <paramref name="channel"/> before it, so no existing
/// construction site changes. Null, which is what a <c>dotnet run</c> and the whole test suite
/// get, reports <see cref="StartupShortcutNotApplicable"/> without calling anything on it.
/// <see cref="IStartupShortcut.Exists"/> is a filesystem read and <see cref="Snapshot"/> is
/// already documented as blocking and called inside a <c>Task.Run</c>, so no new threading rule
/// appears here.</param>
/// <param name="startedMinimized">Spec 12.11 behaviour 9's seam, left as a <c>Func&lt;bool&gt;</c>
/// defaulting to false for Phase 11 Task 4 to bind, exactly as <paramref name="gitSha"/> and
/// <paramref name="channel"/> were left for Task 4 by Phase 10 Task 1.</param>
public sealed class DiagnosticsService(
    DiagnosticsQuery query,
    UnresolvedNamesQuery unresolved,
    ScanRunRepository scanRuns,
    ResolverCounters counters,
    LogRingBuffer ring,
    AppWriter appWriter,
    string catalogsDirectory,
    Func<ScanStatusService> scanStatus,
    Func<WatcherService> watcher,
    Func<ScanScheduler> scheduler,
    LogReader logReader,
    ActivityQuery activity,
    SettingsRepository settingsRepository,
    Func<string>? gitSha = null,
    Func<string>? channel = null,
    Func<string>? appDataSource = null,
    IStartupShortcut? startupShortcut = null,
    Func<bool>? startedMinimized = null)
{
    /// <summary>Spec 12.8's Errors group shows "the last 50" of the 500-entry ring. Named rather
    /// than a literal, because Task 3's bundle takes a different cut of the same ring.</summary>
    public const int RecentErrorCount = 50;

    /// <summary>What a field reports when the value is genuinely absent. Also the literal the
    /// web's health endpoint uses for missing build metadata, which is why it is a word rather
    /// than an empty string.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// The Errors group entry <see cref="Snapshot"/> adds when the stored <c>general</c> document
    /// could not be read, which is what leaves the Scan group's watcher rows empty. Named rather
    /// than a literal so the page, the bundle and the test all mean the same sentence.
    /// </summary>
    public const string WatcherStatesUnavailable =
        "The watcher state could not be read because the stored general settings document did not parse.";

    /// <summary>The rolling log directory under the app data root. <c>AppHost</c> composes
    /// Serilog's file sink path from this constant rather than from a literal of its own
    /// (spec 16.1), spec 12.8's Paths group reports it, and Task 2's log reader enumerates it.
    /// One name, three consumers, one definition.</summary>
    public const string LogDirectoryName = "logs";

    /// <summary>The rolling log file's stem, before Serilog appends the date and the extension:
    /// <c>galactilog-20250915.log</c>. Named here beside <see cref="LogDirectoryName"/> so
    /// <c>AppHost</c> composes the sink path from a constant rather than a literal, and it reads
    /// <see cref="LogFileSet.FileNamePrefix"/> rather than repeating it, so the sink and the log
    /// reader's glob cannot drift onto two spellings of one name (review round 1, ruled
    /// escalation).</summary>
    public const string LogFileNamePrefix = LogFileSet.FileNamePrefix;

    /// <summary>Spec 12.8's Paths group: what the Startup shortcut field reports when the
    /// shortcut is on disk.</summary>
    public const string StartupShortcutPresent = "present";

    /// <summary>What the Startup shortcut field reports when the shortcut is not on disk, on a
    /// build that could carry one.</summary>
    public const string StartupShortcutAbsent = "absent";

    /// <summary>What the Startup shortcut field reports on a build the updater did not install,
    /// or with no seam bound at all. A different answer from <see cref="StartupShortcutAbsent"/>:
    /// a support bundle that cannot tell "no shortcut here" from "no shortcut possible" is the
    /// bundle's problem (spec 12.8, 12.11 behaviour 8).</summary>
    public const string StartupShortcutNotApplicable = "not applicable";

    /// <summary>
    /// A fourth state the Startup shortcut field can carry, beyond the three spec 12.11 behaviour
    /// 11 and spec 12.8 name: <see cref="Unknown"/> itself, reused rather than a fifth named
    /// constant, for a supported seam whose <see cref="IStartupShortcut.Exists"/> returned null
    /// (the COM read itself failed on a build that could otherwise carry a shortcut). Phase 11
    /// Task 3 review, Important 2: this is a real, reachable branch (<c>VelopackStartupShortcut</c>
    /// returns null from a caught exception), not a documentation gap to close by making it
    /// impossible. The docs fixer records it as spec 12.11 behaviour 11 and spec 12.8's fourth
    /// value.
    /// </summary>
    private const string StartupShortcutReadFailed = Unknown;

    /// <summary>What the Started minimized field reports when the process started into the tray
    /// with no window (spec 12.11 behaviour 9).</summary>
    public const string StartedMinimizedYes = "yes";

    /// <summary>What the Started minimized field reports otherwise, and what it reports with no
    /// seam bound, which is Phase 11 Task 4's default until it binds the real answer.</summary>
    public const string StartedMinimizedNo = "no";

    private readonly Func<string> _gitSha = gitSha ?? (() => Unknown);
    private readonly Func<string> _channel = channel ?? (() => Unknown);
    private readonly Func<string> _appDataSource = appDataSource ?? (() => Unknown);
    private readonly IStartupShortcut? _startupShortcut = startupShortcut;
    private readonly Func<bool> _startedMinimized = startedMinimized ?? (() => false);

    /// <summary>One whole snapshot of spec 12.8's seven groups. Blocking: it runs SQLite
    /// statements and probes each scan root's reachability, so every caller runs it off the UI
    /// thread.</summary>
    /// <remarks>
    /// <para>
    /// Synchronous on purpose. Nothing inside is genuinely asynchronous, and an <c>async</c>
    /// wrapper over synchronous SQLite would be a lie. The view-model calls it inside
    /// <c>Task.Run</c> and awaits that.
    /// </para>
    /// <para>
    /// The <c>ScanStatusService</c> properties it reads are mutated only inside a dispatcher
    /// post, so a background read can see a torn pair: <c>IsRunning</c> true beside a
    /// <c>Percent</c> from the previous envelope. That is accepted for a diagnostics readout and
    /// is not a reason to put a lock into <c>ScanStatusService</c>, whose whole point is that it
    /// does no work on the scan's reporting thread.
    /// </para>
    /// <para>
    /// A stored <c>general</c> document that does not parse leaves the Scan group's watcher rows
    /// empty and adds <see cref="WatcherStatesUnavailable"/> to the Errors group, rather than
    /// throwing. See the comment on the guarded read.
    /// </para>
    /// </remarks>
    public DiagnosticsSnapshot Snapshot()
    {
        var status = scanStatus();
        var database = query.Database();
        var cache = query.ResolverCache();

        var entries = ring.Snapshot();
        IReadOnlyList<LogRingEntry> recentErrors = entries.Count <= RecentErrorCount
            ? entries
            : [.. entries.Skip(entries.Count - RecentErrorCount)];

        // Fixer list code item 4 (Task 3 escalation, Phase 1 behaviour). A hand-edited
        // user_settings.general column that does not parse makes SettingsStore.GetGeneral throw,
        // and WatcherService.DescribeRoots reads it whenever the watcher has not bound a root set.
        // That took the whole Diagnostics page down and failed the bundle export with it, for the
        // one column a support bundle is most often collected to inspect. The readout tolerates it
        // instead: the watcher rows come back empty, the failure is reported in the Errors group
        // beside every other warning, and the export still writes. The bundle's own settings key
        // already embeds the unparseable document verbatim under a parse_error
        // (DiagnosticsBundle.Embed), so the two halves agree about what is wrong.
        //
        // Caught broadly on purpose: the reader cannot enumerate the ways a hand-edited document
        // fails (a JsonException from the deserializer, a NullReferenceException from a stored
        // JSON null), and a diagnostics readout is the last thing that may be the component that
        // falls over. Nothing else in this method is wrapped: a SQLite failure is a different
        // diagnosis and must still surface.
        IReadOnlyList<WatcherRootState> watcherStates;
        try
        {
            watcherStates = watcher().DescribeRoots();
        }
        catch (Exception ex)
        {
            watcherStates = [];
            recentErrors =
            [
                .. recentErrors,
                // Appended after the cut, so it is always on screen rather than competing with the
                // ring for one of the fifty slots. Newest last, which is this list's order.
                new LogRingEntry(DateTimeOffset.UtcNow, "Error", WatcherStatesUnavailable, ex.ToString()),
            ];
        }

        return new DiagnosticsSnapshot(
            DateTimeOffset.UtcNow,
            new AppDiagnostics(
                typeof(AppHost).Assembly.GetName().Version?.ToString() ?? Unknown,
                _gitSha(),
                _channel(),
                Environment.Version.ToString(),
                typeof(Avalonia.Application).Assembly.GetName().Version?.ToString() ?? Unknown,
                // The Data layer returns null when SQLite answered with nothing and does not
                // spell the fallback word a second time (review finding M5).
                query.SqliteVersion() ?? Unknown,
                Environment.OSVersion.VersionString),
            new PathDiagnostics(
                appWriter.AppDataRoot,
                // The same value the Database group reports, read once, so the two groups cannot
                // name different files.
                database.FilePath,
                appWriter.ResolveAppDataPath(LogDirectoryName),
                appWriter.ThumbnailCacheRoot,
                catalogsDirectory,
                _appDataSource(),
                StartupShortcutState(),
                _startedMinimized() ? StartedMinimizedYes : StartedMinimizedNo),
            database,
            new ScanDiagnostics(
                status.IsRunning,
                status.TaskName,
                status.Message,
                status.Percent,
                status.HasDeterminatePercent,
                scanRuns.Latest(),
                watcherStates,
                scheduler().NextScheduledUtc),
            new ResolverDiagnostics(
                cache.CachePositive,
                cache.CacheNegative,
                cache.CacheExpired,
                counters.Hits,
                counters.Misses),
            unresolved.All(),
            recentErrors);
    }

    /// <summary>
    /// Spec 16.3's eleven-key bundle, written as a new UTF-8 JSON file at
    /// <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">An absolute path the user chose in the platform save dialog, and
    /// from nowhere else (spec 12.8's write path, step 1). This is the sole sanctioned write
    /// outside app data in the whole application: <c>AppWriter.BeginExport</c> is called here and
    /// in no other place in the solution, which <c>FileSafetyTest</c>'s <c>BeginExport</c> pattern
    /// group and <c>ExportWriterContainmentTests</c> enforce structurally. Never compose, default,
    /// or derive this path.</param>
    /// <remarks>
    /// <para>
    /// Blocking: it runs every diagnostics read and then one file write. Callers run it off the
    /// UI thread.
    /// </para>
    /// <para>
    /// The whole document is built and serialized <b>before</b> <c>BeginExport</c> is called, so a
    /// failure in a read leaves no partial file at the user's chosen path. That is the reason the
    /// two statements are in this order and it must not be reordered.
    /// </para>
    /// <para>
    /// <paramref name="destination"/> is passed to both <c>BeginExport</c> and
    /// <c>WriteAllText</c>, which is what makes <c>AuthorizeExact</c> a real check rather than a
    /// tautology: the two arguments are the same variable here, and the check exists so a future
    /// caller that passes a different second path fails.
    /// </para>
    /// <para>
    /// No overwrite prompt, no existence check, no backup copy and no temp-file-then-rename. The
    /// save dialog's own confirmation is the only replacement path (spec 12.8 step 3), and a temp
    /// file plus a rename would be two writes outside app data and a move of a user file, both of
    /// which spec 2.1 forbids. There is no retry: an <c>IOException</c> propagates to the
    /// view-model, which reports it (spec 12.10).
    /// </para>
    /// </remarks>
    public void ExportBundle(string destination)
    {
        var bundle = BuildBundle();
        var json = JsonSerializer.Serialize(bundle, BundleOptions);

        // using var: the scoped writer is valid for this one operation and disposed when it ends
        // (spec 12.8 step 2). WriteAllText rather than WriteAllBytes, because File.WriteAllText's
        // default encoding is UTF-8 without a BOM, which is what spec 16.3's "one UTF-8 JSON
        // file" means.
        using var writer = appWriter.BeginExport(destination);
        writer.WriteAllText(destination, json);
    }

    /// <summary>
    /// Task 7's "Save log as" (PAR-016, spec 12.8), beside <see cref="ExportBundle"/> on this
    /// sealed class rather than as a new type: <c>FileSafetyTest</c>'s <c>BeginExportAllowlist</c>
    /// names exactly two files by full path, <c>AppWriter.cs</c> and this one, so a second caller of
    /// <c>AppWriter.BeginExport</c> anywhere else fails the build (<c>questions.md</c> Q8).
    /// </summary>
    /// <param name="destination">The path the user chose in the save dialog, and from nowhere else
    /// (spec 12.8 step 1). Never composed, defaulted or derived here.</param>
    /// <param name="contents">The text to write, already composed by the caller. Unlike
    /// <see cref="ExportBundle"/>, this method reads nothing itself: <c>LogViewerViewModel</c> holds
    /// the lines already on screen and spec 12.8 says the export is "the lines the viewer currently
    /// holds", not a fresh read of the log files.</param>
    /// <remarks>Same shape as <see cref="ExportBundle"/>: the writer is opened, written once and
    /// disposed. No overwrite prompt, no existence check and no temp-file-then-rename; the save
    /// dialog's own confirmation is the only replacement path (spec 12.8 step 3).</remarks>
    public void ExportLog(string destination, string contents)
    {
        using var writer = appWriter.BeginExport(destination);
        writer.WriteAllText(destination, contents);
    }

    // Spec 12.8's Paths group, third-state rule: "not applicable" is a different answer from
    // "absent", so the two are never collapsed into a bool (spec 12.11 behaviour 8). IsSupported
    // is read fresh through the seam rather than cached, because it is the same predicate
    // BuildInfo.IsInstalled always answers, so there is nothing to gain by caching it here.
    //
    // A fourth, reachable outcome (Phase 11 Task 3 review, Important 2): a supported seam whose
    // Exists() itself could not read the answer (the COM call threw). StartupShortcutReadFailed
    // is Unknown by value, which is what every other field in this composer reports for the same
    // situation, so a support reader sees one word for "this could not be read" everywhere it
    // occurs, rather than a fifth invented word for one field.
    private string StartupShortcutState()
    {
        if (_startupShortcut is not { IsSupported: true } shortcut)
        {
            return StartupShortcutNotApplicable;
        }

        return shortcut.Exists() switch
        {
            true => StartupShortcutPresent,
            false => StartupShortcutAbsent,
            null => StartupShortcutReadFailed,
        };
    }

    // One snapshot plus the four reads it does not carry. Nothing is read a second way: the
    // snapshot is the same reading spec 12.8's page renders, so the page and the bundle cannot
    // report different numbers for the same instant.
    private DiagnosticsBundle BuildBundle() => DiagnosticsBundle.From(
        Snapshot(),
        DiagnosticsBundle.SettingsOf(settingsRepository.Load()),
        scanRuns.Recent(DiagnosticsBundle.ScanRunCount),
        activity.Page(new ActivityFilters(), null, DiagnosticsBundle.RecentEventCount).Rows,
        logReader.All(new LogFilters(), DiagnosticsBundle.LogTailCount));

    /// <remarks>
    /// One instance, static and readonly, per the analyzer rule that a serializer options object
    /// is cached rather than rebuilt per call.
    /// <list type="bullet">
    /// <item><c>WriteIndented</c>: a person reads this file.</item>
    /// <item><c>DefaultIgnoreCondition = Never</c>: a null field must appear as <c>null</c>,
    /// because "the field is absent" and "the key is missing" are different diagnoses.</item>
    /// <item><c>UnsafeRelaxedJsonEscaping</c>: Windows paths and log messages otherwise arrive
    /// full of escaped quotes and non-ASCII escape sequences. The name draws a review finding on
    /// sight, so the reason is here: this document is written to a local file and never to a web
    /// page or an HTML context, which is the only place the stricter default encoder buys
    /// anything.</item>
    /// <item>No global naming policy. Every key is an explicit <c>[JsonPropertyName]</c> on
    /// <c>DiagnosticsBundle</c>, so a property rename cannot silently rename a bundle key.</item>
    /// </list>
    /// </remarks>
    private static readonly JsonSerializerOptions BundleOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
