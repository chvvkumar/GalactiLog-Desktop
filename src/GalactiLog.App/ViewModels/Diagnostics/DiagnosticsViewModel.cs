using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Io;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Diagnostics;

/// <summary>
/// Spec 12.8's Diagnostics page: seven field groups read once per refresh from one immutable
/// snapshot. Reached from the rail and from the Settings tab strip, which share this one instance
/// (coordinator ruling Q3).
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no poll</b> (ruling Q31). The page refreshes on demand, through the Refresh
/// button, and on <c>ScanStatusService.ScanFinished</c>, which is already on the UI thread by the
/// time a subscriber sees it. It never subscribes to <c>ScanCoordinator</c>: spec 4.2 gives that
/// subscription to <c>ScanStatusService</c> alone.
/// </para>
/// <para>
/// One composer, one read. Every figure on the page comes from <c>DiagnosticsService.Snapshot</c>,
/// so the Database group's row counts and Task 3's bundle describe the same instant.
/// </para>
/// </remarks>
public sealed partial class DiagnosticsViewModel : ObservableObject, IDisposable
{
    /// <summary>What a field renders when its value is genuinely absent. Never an empty string
    /// and never null: the roadmap's Verify line is "no null field where the spec names a value",
    /// and an empty cell reads as a rendering bug.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>Shown in place of the groups when the snapshot threw. A failed read taught
    /// nothing about the library, so it is reported rather than rendered as empty groups.</summary>
    public const string FailureText = "The diagnostics snapshot could not be read.";

    /// <summary>What <see cref="ExportStatus"/> says after a bundle was written, before the path
    /// the user chose. Named here so the sentence has one definition.</summary>
    public const string ExportedPrefix = "Diagnostics bundle written to ";

    /// <summary>Spec 12.8's "PHD2 data size" field label (ruling F2). Named here so the page and
    /// its case read the same string.</summary>
    public const string Phd2DataSizeLabel = "PHD2 data size";

    /// <summary>Spec 12.8's group names, in its table order.</summary>
    public static readonly IReadOnlyList<string> GroupTitles =
        ["Database", "Scan", "Resolver", "Unresolved", "Errors", "Versions", "Paths"];

    private const int Database = 0;
    private const int Scan = 1;
    private const int Resolver = 2;
    private const int UnresolvedGroup = 3;
    private const int Errors = 4;
    private const int Versions = 5;
    private const int Paths = 6;

    private readonly Func<DiagnosticsSnapshot> _snapshot;
    private readonly Action<string>? _exportBundle;
    private readonly ScanStatusService? _scanStatus;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    private bool _disposed;

    /// <param name="snapshot">Normally <c>DiagnosticsService.Snapshot</c>, which is the one
    /// composer of spec 12.8's reading. A delegate rather than the service object, exactly as
    /// every other page view-model in this application takes its reads, so the page constructs in
    /// a unit test with a lambda and no database (design-spec 18.3). <c>DiagnosticsService</c> is
    /// sealed and its <c>Snapshot</c> is non-virtual, so a delegate is also the only stub
    /// available to the tests that assert the read runs off the UI thread and that the
    /// constructor runs none.</param>
    /// <param name="unresolved">The shared <c>UnresolvedNamesViewModel</c> the Settings Targets
    /// tab also renders. Held, never disposed here: it is a DI singleton the host owns.</param>
    /// <param name="scanStatus">The only automatic refresh trigger. Already on the UI thread when
    /// a subscriber sees it, so its handler does not post again.</param>
    /// <param name="post">The dispatcher seam every view-model takes; defaults to
    /// <c>UiPost.Default</c>.</param>
    /// <param name="logger">A failed snapshot is logged and reported, never rethrown on the UI
    /// thread.</param>
    /// <param name="logViewer">Spec 12.8's log viewer (Phase 10 Task 2), hosted below the seven
    /// groups. Owned by this page and disposed with it, unlike <paramref name="unresolved"/>:
    /// nothing else renders it and nothing else holds it. Optional and null in the unit tests that
    /// assert the groups, because a log viewer needs a log directory and a settings store that
    /// those cases have no use for; <c>AppHost</c> always supplies one.</param>
    /// <param name="exportBundle">Normally <c>DiagnosticsService.ExportBundle</c>, taken as a
    /// delegate for the same reason <paramref name="snapshot"/> is: the service is sealed and the
    /// member is non-virtual, so a delegate is the only stub available to a unit test with no
    /// database. Null in a host with no export surface, which then exports nothing.</param>
    /// <param name="pickDestination">The initial value of <see cref="DestinationPicker"/>.</param>
    public DiagnosticsViewModel(
        Func<DiagnosticsSnapshot> snapshot,
        UnresolvedNamesViewModel unresolved,
        ScanStatusService? scanStatus = null,
        Action<Action>? post = null,
        ILogger? logger = null,
        LogViewerViewModel? logViewer = null,
        Action<string>? exportBundle = null,
        Func<Task<string?>>? pickDestination = null)
    {
        _snapshot = snapshot;
        _exportBundle = exportBundle;
        DestinationPicker = pickDestination;
        _scanStatus = scanStatus;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;
        Unresolved = unresolved;
        LogViewer = logViewer;

        // Built once, with their titles, and refilled in place on every refresh so the expander
        // state a user has set survives. The Database group opens by default; the rest follow the
        // user.
        Groups = [.. GroupTitles.Select(title => new DiagnosticsGroupViewModel(title)
        {
            Content = title == GroupTitles[UnresolvedGroup] ? unresolved : null,
        })];
        Groups[Database].IsExpanded = true;

        if (scanStatus is not null)
        {
            scanStatus.ScanFinished += OnScanFinished;
        }

        // Not a call to Snapshot(): the first read is scheduled through the dispatcher seam and
        // then runs on the pool, because a synchronous SQLite read in a constructor on the UI
        // thread is the defect TRACKING item 16 exists to prevent. The page is behind a
        // NavigationItem factory, so this constructor already runs on the first visit.
        _post(() => RefreshCommand.Execute(null));
    }

    /// <summary>The seven groups, in spec 12.8's table order.</summary>
    public ObservableCollection<DiagnosticsGroupViewModel> Groups { get; }

    /// <summary>Spec 9.7's unresolved names with their retry, hosted by the Unresolved group.
    /// The same instance the Settings Targets tab renders, so the retry on this page is the
    /// existing one and takes the same coordinator resolution lease.</summary>
    public UnresolvedNamesViewModel Unresolved { get; }

    /// <summary>Spec 12.8's log viewer, rendered below the seven groups, with the
    /// <c>general.log_level</c> selector spec 12.7 says appears nowhere else. Null only in the
    /// unit tests that assert the groups on their own.</summary>
    public LogViewerViewModel? LogViewer { get; }

    /// <summary>
    /// Opens the platform save dialog and returns the chosen absolute path, or null when the user
    /// cancelled or the dialog is unavailable. Bound in <c>DiagnosticsView</c>'s code-behind to
    /// <c>TopLevel.StorageProvider.SaveFilePickerAsync</c>; a test binds a recording delegate. It
    /// is the only source of an export path in the application (spec 12.8: the dialog "is the
    /// only place a path outside app data can enter the application", coordinator ruling Q10).
    /// </summary>
    /// <remarks>
    /// Settable rather than constructor-only because this page is a DI singleton built before any
    /// view exists, so the view installs its picker when it attaches to the visual tree. The
    /// constructor parameter is what the unit tests use. This property is the seam's whole
    /// surface: nothing else on this class accepts, composes or derives a path.
    /// </remarks>
    public Func<Task<string?>>? DestinationPicker { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsRefreshing { get; private set; }

    /// <summary>True while the save dialog is open or the bundle is being written.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportBundleCommand))]
    public partial bool IsExporting { get; private set; }

    /// <summary>Spec 12.10's export outcome: where the bundle went, or why it did not go there.
    /// Null before the first export, and unchanged when the user cancels the dialog.</summary>
    [ObservableProperty]
    public partial string? ExportStatus { get; private set; }

    /// <summary>The snapshot threw. Rendered as <see cref="FailureMessage"/>.</summary>
    [ObservableProperty]
    public partial bool LoadFailed { get; private set; }

    /// <summary>What the view renders when <see cref="LoadFailed"/> is true. Bound rather than
    /// repeated in the markup, so <see cref="FailureText"/> is the one place the sentence
    /// lives.</summary>
    public string FailureMessage => FailureText;

    /// <summary>When the groups on screen were read, or null before the first refresh
    /// completes.</summary>
    [ObservableProperty]
    public partial string? GeneratedAtText { get; private set; }

    /// <summary>
    /// Re-reads the whole snapshot. Spec 12.8's refresh action, and the only manual one.
    /// </summary>
    /// <remarks>
    /// The already-refreshing guard is repeated in the body and not left to
    /// <see cref="CanRefresh"/>, because <c>RelayCommand.Execute</c> ignores <c>CanExecute</c>
    /// (TRACKING section 6 item 13). Two refreshes running together would publish two readings
    /// into one set of groups. The command takes no <c>CancellationToken</c>: cancellation is not
    /// offered here.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (_disposed || IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;

        DiagnosticsSnapshot? snapshot = null;
        var failed = false;
        try
        {
            // Off the UI thread: the snapshot runs five statement groups and probes each scan
            // root's reachability, which an unreachable network share can make take seconds.
            snapshot = await Task.Run(_snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failed = true;
            _logger.LogWarning(ex, "The diagnostics snapshot could not be read");
        }

        // Inside a guard of its own, so this method cannot complete faulted. The command is
        // pressed fire-and-forget from OnScanFinished and from a button, and
        // AsyncRelayCommand.Execute rethrows a faulted or cancelled command task on the ambient
        // synchronization context (its AwaitAndThrowIfFailed helper). An exception escaping here
        // would therefore surface on whatever thread happened to be running, rather than on the
        // caller. Fix round 2.
        try
        {
            _post(() => Finish(snapshot, failed));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Publishing the diagnostics snapshot failed");
        }
    }

    private bool CanRefresh() => !IsRefreshing && !_disposed;

    // Runs on the UI thread through the post seam, which is where every binding is written and
    // where theme tokens resolve.
    private void Finish(DiagnosticsSnapshot? snapshot, bool failed)
    {
        // A refresh started before Dispose can land after it: the read runs on the pool and the
        // publish is posted. Nothing is published into a page that has gone (fix round 2).
        if (_disposed)
        {
            return;
        }

        IsRefreshing = false;
        LoadFailed = failed;

        if (snapshot is null)
        {
            return;
        }

        GeneratedAtText = Instant(snapshot.GeneratedAt.UtcDateTime);
        Publish(snapshot);
    }

    /// <summary>
    /// Spec 16.3's export: the user picks a destination in the platform save dialog and the
    /// bundle is written there, off the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The already-exporting guard is repeated in the body and not left to
    /// <see cref="CanExport"/>, because <c>RelayCommand.Execute</c> ignores <c>CanExecute</c>
    /// (TRACKING section 6 item 13). The command takes no <c>CancellationToken</c>: cancellation
    /// is not offered here.
    /// </para>
    /// <para>
    /// This method never builds a path. There is no default file name computed here, no
    /// combination and no app data fallback: the suggested file name belongs to the dialog options
    /// in the view's code-behind, and the only path this page ever holds is the one
    /// <see cref="DestinationPicker"/> returned.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportBundleAsync()
    {
        if (_disposed || IsExporting)
        {
            return;
        }

        // A surface with no dialog and no service exports nothing. A null seam is not an error
        // and not a log line: it is the shape a unit-test host has.
        var pickDestination = DestinationPicker;
        var exportBundle = _exportBundle;
        if (pickDestination is null || exportBundle is null)
        {
            return;
        }

        // Set on the caller's thread, which is the UI thread for a button press, and cleared
        // through the post seam below because the continuation runs on the pool.
        IsExporting = true;

        string? status = null;
        try
        {
            var destination = await pickDestination().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(destination))
            {
                // Off the UI thread: the export runs every diagnostics read and then one file
                // write. Cancelling the dialog leaves ExportStatus as it was.
                await Task.Run(() => exportBundle(destination)).ConfigureAwait(false);
                status = ExportedPrefix + destination;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedPathException or IOException or UnauthorizedAccessException)
        {
            // Spec 12.10's failure state: reported to the user and logged, never swallowed and
            // never rethrown onto the dispatcher.
            status = ex.Message;
            _logger.LogWarning(ex, "The diagnostics bundle could not be written");
        }
        catch (Exception ex)
        {
            // Nothing else is expected, but this method must not be able to complete faulted:
            // AsyncRelayCommand.Execute rethrows a faulted command task on the ambient
            // synchronization context (its AwaitAndThrowIfFailed helper), which under a
            // fire-and-forget press is whatever thread happened to be running (fix round 2).
            status = ex.Message;
            _logger.LogWarning(ex, "The diagnostics bundle export failed");
        }

        try
        {
            _post(() => FinishExport(status));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Publishing the diagnostics export result failed");
        }
    }

    private bool CanExport() => !IsExporting && !_disposed;

    // Runs on the UI thread through the post seam, which is where every binding is written.
    private void FinishExport(string? status)
    {
        if (_disposed)
        {
            return;
        }

        IsExporting = false;
        if (status is not null)
        {
            ExportStatus = status;
        }
    }

    private void OnScanFinished(object? sender, EventArgs e)
    {
        // ScanStatusService has already marshalled onto the UI thread; do not post again. The
        // command's own body guard drops this when a refresh is already in flight.
        if (!_disposed)
        {
            RefreshCommand.Execute(null);
        }
    }

    private void Publish(DiagnosticsSnapshot snapshot)
    {
        var database = snapshot.Database;
        var databaseFields = new List<DiagnosticsFieldViewModel>
        {
            new("File", Text(database.FilePath)),
            new("File size", MetricText.Bytes(database.FileBytes)),
            // 0 bytes is the ordinary reading, not an error: SQLite deletes the write-ahead log
            // on a clean close, so a fresh process routinely has none (ruling Q9).
            new("Write-ahead log size", MetricText.Bytes(database.WalBytes)),
            new("Page count", Number(database.PageCount)),
        };
        // Iterated through the ordered table list, never through the dictionary: a dictionary's
        // enumeration order is unspecified, and spec 12.8 names an order (review finding M1). The
        // dictionary is the lookup; DiagnosticsQuery.RowCountTables is the order.
        databaseFields.AddRange(DiagnosticsQuery.RowCountTables.Select(
            table => new DiagnosticsFieldViewModel(
                table,
                database.RowCounts.TryGetValue(table, out var count) ? Number(count) : Unavailable)));
        // Spec 12.8 (ruling F2): one figure beside the four guide-log row counts, and the method
        // travels with it. A reader who cannot tell a measured figure from an estimate has a figure
        // they cannot use, so the word is in the value rather than in a tooltip.
        databaseFields.Add(new DiagnosticsFieldViewModel(
            Phd2DataSizeLabel,
            MetricText.Bytes(database.Phd2Bytes) + " (" + database.Phd2BytesMethod + ")"));
        Fill(Groups[Database], databaseFields);

        var scan = snapshot.Scan;
        Fill(Groups[Scan],
        [
            new("Scan running", scan.IsRunning ? "yes" : "no"),
            new("Current task", Text(scan.CurrentTask)),
            new("Current message", Text(scan.CurrentMessage)),
            new("Progress", scan.IsRunning
                ? scan.HasDeterminatePercent
                    ? Percent(scan.CurrentPercent)
                    : "indeterminate"
                : Unavailable),
            // Spec 12.8's Scan row names five attributes of the last run: trigger, start, finish,
            // duration and every counter. Start travels with the state on the "Last run" row; the
            // other four have a row each.
            new("Last run", DescribeRun(scan.LastRun)),
            new("Last run trigger", Text(scan.LastRun?.Trigger)),
            new("Last run finished", Instant(scan.LastRun?.FinishedAt)),
            new("Last run duration", DescribeRunDuration(scan.LastRun)),
            new("Last run results", DescribeRunCounts(scan.LastRun)),
            new("Next scheduled scan", Instant(scan.NextScheduledUtc)),
            new("Library folders", Number(scan.WatcherStates.Count)),
        ]);
        Fill(Groups[Scan].WatcherRoots, scan.WatcherStates.Select(WatcherRootViewModel.From));

        var resolver = snapshot.Resolver;
        Fill(Groups[Resolver],
        [
            new("Cached positive rows", Number(resolver.CachePositive)),
            new("Cached negative rows", Number(resolver.CacheNegative)),
            // A subset of the negative rows, not a fourth bucket.
            new("Expired negative rows", Number(resolver.CacheExpired)),
            new("Cache hits this session", Number(resolver.Hits)),
            new("Cache misses this session", Number(resolver.Misses)),
        ]);

        Fill(Groups[UnresolvedGroup],
        [
            new("Unresolved names", Number(snapshot.Unresolved.Count)),
            new("Frames affected", Number(snapshot.Unresolved.Sum(row => (long)row.FrameCount))),
        ]);

        var errorFields = new List<DiagnosticsFieldViewModel>
        {
            new("Warnings and errors held", Number(snapshot.RecentErrors.Count)),
        };
        // Newest first on screen; the ring itself is oldest first.
        errorFields.AddRange(snapshot.RecentErrors
            .Reverse()
            .Select(entry => new DiagnosticsFieldViewModel(
                Instant(entry.Timestamp.UtcDateTime) + " " + entry.Level,
                Text(entry.Message))));
        Fill(Groups[Errors], errorFields);

        var app = snapshot.App;
        Fill(Groups[Versions],
        [
            new("GalactiLog", Text(app.Version)),
            new("Git commit", Text(app.GitSha)),
            new("Update channel", Text(app.Channel)),
            new(".NET runtime", Text(app.Dotnet)),
            new("Avalonia", Text(app.Avalonia)),
            new("SQLite", Text(app.Sqlite)),
            new("Operating system", Text(app.Os)),
        ]);

        var paths = snapshot.Paths;
        Fill(Groups[Paths],
        [
            new("Application data", Text(paths.AppData)),
            // Spec 12.8's Paths row and ruling Q9.10: the path alone cannot say whether it came
            // from the user's own choice, the pointer, the environment or a test override, which is
            // the first question a support request is asked.
            new("Data location source", Text(paths.AppDataSource)),
            new("Database", Text(paths.Database)),
            new("Logs", Text(paths.Logs)),
            new("Thumbnail cache", Text(paths.Thumbnails)),
            new("Catalogues", Text(paths.Catalogs)),
            // Spec 12.8's Paths group and ruling Q9: the two fields Phase 11 Task 3 adds close out
            // the group, in the order the spec table prints them (spec 12.11 behaviours 8, 9).
            new("Startup shortcut", Text(paths.StartupShortcut)),
            new("Started minimized", Text(paths.StartedMinimized)),
        ]);
    }

    // Replaced in place rather than by a new collection, so the expander state survives and the
    // ItemsControl re-uses its containers.
    private static void Fill(
        DiagnosticsGroupViewModel group, IEnumerable<DiagnosticsFieldViewModel> fields)
    {
        group.Fields.Clear();
        foreach (var field in fields)
        {
            group.Fields.Add(field);
        }
    }

    private static void Fill(
        ObservableCollection<WatcherRootViewModel> target, IEnumerable<WatcherRootViewModel> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
        }
    }

    private static string DescribeRun(ScanRun? run) => run is null
        ? Unavailable
        : run.State + ", started " + Instant(run.StartedAt);

    /// <summary>Spec 12.8's "last run duration". Unavailable when nothing has ever scanned, and
    /// unavailable when the run has no finish: a run closed by <c>MarkInterrupted</c> after a
    /// crash has a <c>finished_at</c>, but one still running does not, and neither has a duration
    /// worth reporting as a number.</summary>
    private static string DescribeRunDuration(ScanRun? run)
    {
        if (run?.FinishedAt is not { } finished)
        {
            return Unavailable;
        }

        var elapsed = finished - run.StartedAt;
        if (elapsed < TimeSpan.Zero)
        {
            return Unavailable;
        }

        // Days only appear when there are days, so an ordinary scan reads 00:03:11.
        return elapsed.Days > 0
            ? elapsed.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string DescribeRunCounts(ScanRun? run) => run is null
        ? Unavailable
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{run.Discovered} discovered, {run.NewFiles} new, {run.ChangedFiles} changed, " +
            $"{run.Completed} completed, {run.Failed} failed, {run.SkippedCalibration} calibration skipped, " +
            $"{run.Removed} removed, {run.Phd2Found} guide logs found, " +
            $"{run.Phd2Ingested} guide logs ingested, {run.Phd2Failed} guide logs failed");

    /// <summary>A stored instant as UTC. Diagnostics and a support bundle read UTC deliberately:
    /// spec 5.8.1's display zone is for the library screens, and a support request is answered
    /// against the log file, whose timestamps are not the reader's local time either.</summary>
    private static string Instant(DateTime? utc) => utc is { } value
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
        : Unavailable;

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Percent(double value)
        => string.Create(CultureInfo.InvariantCulture, $"{value:0.#} %");

    private static string Text(string? value)
        => string.IsNullOrWhiteSpace(value) ? Unavailable : value;

    /// <summary>
    /// Drops the scan subscription. The shared <c>UnresolvedNamesViewModel</c> is deliberately not
    /// disposed here: it is a DI singleton the host owns, the Settings Targets tab renders the
    /// same instance, and so does the Settings Diagnostics tab, which is this same page
    /// (coordinator ruling Q3).
    /// </summary>
    /// <remarks>
    /// Tolerates a refresh in flight. Disposing cancels nothing and waits for nothing: the read
    /// is a short SQLite pass with no token, and <see cref="Finish"/> drops its publish when it
    /// lands on a disposed page. Nothing this page does not own is cancelled or disposed here.
    /// <see cref="LogViewer"/> is the one exception, and it is not an exception to the rule: this
    /// page owns it, nothing else renders it, and its follow-tail loop has to stop when the page
    /// goes.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_scanStatus is not null)
        {
            _scanStatus.ScanFinished -= OnScanFinished;
        }

        LogViewer?.Dispose();
    }
}
