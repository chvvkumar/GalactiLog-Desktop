using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// Spec 12.7's unresolved-name list with its retry action, and spec 12.8's Unresolved group,
/// which Phase 10 renders from this same type.
/// </summary>
/// <remarks>
/// <para>
/// One view-model for both surfaces (design-lessons rule 1): two lists that disagree about which
/// names are unresolved, or that clear different cache rows, is the failure this prevents. The
/// retry itself is <see cref="UnresolvedRetry"/>, which Phase 9's Maintenance tab calls directly.
/// </para>
/// <para>
/// Every collaborator arrives as a delegate, so the list builds in a unit test with no database
/// and no network (design-spec 18.3).
/// </para>
/// </remarks>
public sealed partial class UnresolvedNamesViewModel : ObservableObject, IDisposable
{
    private readonly Func<IReadOnlyList<UnresolvedNameRow>> _load;
    private readonly Func<Action<int, int, string>, CancellationToken, UnresolvedRetry.RetryOutcome> _retry;
    private readonly Func<string, Task<bool>>? _assignToTarget;
    private readonly Action<string>? _createTarget;
    private readonly ScanStatusService? _scanStatus;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    // One list-lifetime source every background window is linked to, the shape
    // DashboardViewModel established: disposing the host cancels a load, and a retry, that is
    // still in flight.
    private readonly CancellationTokenSource _lifetime = new();

    // Only the newest load may write to the bindings. The token alone is not enough: a load that
    // already finished can still be sitting in the dispatcher queue when a newer one is
    // requested, and posting it then would show a stale list.
    private int _generation;

    private bool _hasLoaded;
    private bool _disposed;

    /// <param name="load">Normally <c>UnresolvedNamesQuery.All</c>.</param>
    /// <param name="retry">Normally <c>UnresolvedRetry.Run</c>. It reaches the network, so it runs
    /// off the UI thread and reports progress through its first argument.</param>
    /// <param name="assignToTarget">Opens Task 4's merge dialog for one unresolved name, so the
    /// user can attach a name the catalogues will never resolve to an existing target. Normally
    /// <c>MergeDialogService.ShowAsync</c> over a <c>LoserName</c>-shaped request. Null renders the
    /// action disabled, the pattern Phase 6 used for the preview action.</param>
    /// <param name="scanStatus">Reloads the list after a scan, because a scan can both add and
    /// remove unresolved names. Subscribed here, never to <c>ScanCoordinator</c>.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed load, retry or dialog is logged, never rethrown on
    /// the UI thread.</param>
    /// <param name="createTarget">Spec 12.7's Create target row action (PAR-001): opens the
    /// Targets tab's own create form with this <c>OBJECT</c> string pre-filled. Normally
    /// <c>CreateTargetViewModel.OpenFor</c>. Null renders the action disabled, the same pattern
    /// <paramref name="assignToTarget"/> uses.</param>
    public UnresolvedNamesViewModel(
        Func<IReadOnlyList<UnresolvedNameRow>> load,
        Func<Action<int, int, string>, CancellationToken, UnresolvedRetry.RetryOutcome> retry,
        Func<string, Task<bool>>? assignToTarget = null,
        ScanStatusService? scanStatus = null,
        Action<Action>? post = null,
        ILogger? logger = null,
        Action<string>? createTarget = null)
    {
        _load = load;
        _retry = retry;
        _assignToTarget = assignToTarget;
        _createTarget = createTarget;
        _scanStatus = scanStatus;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        if (scanStatus is not null)
        {
            // ScanStatusService has already marshalled onto the UI thread; do not post again.
            scanStatus.ScanFinished += OnScanFinished;

            // The DashboardViewModel pattern: the Run scan button and this one are disabled by
            // the same signal, from the same property.
            scanStatus.PropertyChanged += OnScanStatusChanged;
        }

        Load();
    }

    /// <summary>The distinct unresolved names, most frames first, as the query ordered them.
    /// </summary>
    public ObservableCollection<UnresolvedNameRowViewModel> Names { get; } = [];

    /// <summary>Spec 12.10: "Every OBJECT name resolved." True when the load has completed and
    /// the list is empty; false while the first load is in flight, so the empty state does not
    /// flash before the rows arrive.</summary>
    public bool ShowAllResolved => _hasLoaded && Names.Count == 0;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>A load that threw. The section renders one neutral line instead of a bare header
    /// (spec 12.10: a failure is reported, never a silent empty list). Carries no text from the
    /// exception; the log has that. Cleared by the next successful load.</summary>
    [ObservableProperty]
    public partial bool LoadFailed { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial bool IsRetrying { get; private set; }

    /// <summary>The running retry's progress line, or null. Published once per name; a library has
    /// tens of unresolved names, not thousands, so it is not throttled.</summary>
    [ObservableProperty]
    public partial string? RetryProgress { get; private set; }

    /// <summary>The last retry's outcome as one sentence, or null.</summary>
    [ObservableProperty]
    public partial string? RetrySummary { get; private set; }

    /// <summary>True while a scan owns resolution and the negative cache (spec 5.1, 9.6), which is
    /// what greys the retry button. The refusal itself is <c>UnresolvedRetry.Run</c>'s, so a race
    /// between this flag and the scan starting is refused rather than run.</summary>
    /// <remarks>
    /// FIXER LIST F15: <c>ResolutionInProgress</c> as well as <c>IsRunning</c>. The Maintenance
    /// tab's retry-unresolved action takes the same resolution lease this button needs, and while
    /// it holds it no scan is running, so reading only <c>IsRunning</c> left this button enabled
    /// through a run it could not possibly win. The lease refuses the press either way; greying it
    /// is what stops the user being told the retry failed for no visible reason.
    /// </remarks>
    public bool ScanRunning => _scanStatus is { IsRunning: true } or { ResolutionInProgress: true };

    /// <summary>The in-flight load, so a test can await it instead of sleeping. Mirrors
    /// <c>TargetDetailViewModel.PendingLoad</c>.</summary>
    internal Task? PendingLoad { get; private set; }

    /// <summary>The in-flight retry, so a test can await it instead of sleeping.</summary>
    internal Task? PendingRetry { get; private set; }

    /// <summary>Re-reads the list. The path every refresh takes (a scan finishing, a retry
    /// completing, an assignment).</summary>
    internal void Reload() => Load();

    /// <summary>Re-reads the list and completes with that read, so the command settles with the
    /// load rather than before it (the shape Task 3's <c>ReloadAsync</c> established).</summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        Load();
        if (PendingLoad is { } load)
        {
            await load.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Spec 9.7's retry: clear every negative cache row, re-run resolution for each distinct
    /// unresolved name, and assign the frames where it now succeeds.
    /// </summary>
    /// <remarks>
    /// The one-at-a-time guard is in the body, not only in <c>CanExecute</c>:
    /// <c>RelayCommand.Execute</c> runs regardless of <c>CanExecute</c>, and two retries running
    /// together would clear each other's cache rows mid-loop and report two contradictory
    /// summaries.
    /// <para>
    /// No <see cref="CancellationToken"/> parameter (FIXER LIST F24, and Task 8's deviation D10):
    /// a command built from a <c>Func&lt;CancellationToken, Task&gt;</c> cancels the in-flight
    /// token on a second <c>Execute</c>, so the second press would abort the running retry
    /// mid-loop rather than be refused by the guard below. The page lifetime is the only thing
    /// that cancels a retry.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync()
    {
        // Re-checked in the body because RelayCommand.Execute ignores CanExecute. The scan check
        // is repeated inside UnresolvedRetry.Run, which is the authority: this one only saves the
        // user a pointless round trip and a misleading progress line.
        if (_disposed || IsRetrying)
        {
            return;
        }

        if (ScanRunning)
        {
            RetrySummary = ScanInProgressMessage;
            return;
        }

        IsRetrying = true;
        RetryProgress = null;
        RetrySummary = null;

        using var window = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var windowToken = window.Token;

        try
        {
            // UnresolvedRetry reaches the network, so it never runs on the dispatcher.
            var run = Task.Run(() => _retry(Report, windowToken), windowToken);
            PendingRetry = run;
            var outcome = await run.ConfigureAwait(false);

            _post(() => FinishRetry(Describe(outcome)));
        }
        catch (OperationCanceledException)
        {
            _post(() => FinishRetry("The retry was cancelled."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The unresolved-name retry failed");
            _post(() => FinishRetry("The retry could not be completed. See the log for details."));
        }
    }

    private bool CanRetry() => !IsRetrying && !ScanRunning;

    private void OnScanStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null
            or nameof(ScanStatusService.IsRunning)
            or nameof(ScanStatusService.ResolutionInProgress))
        {
            OnPropertyChanged(nameof(ScanRunning));
            RetryCommand.NotifyCanExecuteChanged();
        }
    }

    // Called from the retry's own thread, once per name.
    private void Report(int step, int total, string message) => _post(() => RetryProgress = message);

    // Runs on the UI thread through the post seam. The list is re-read whatever the outcome: a
    // run that stopped partway may still have assigned frames.
    private void FinishRetry(string? summary)
    {
        if (_disposed)
        {
            return;
        }

        IsRetrying = false;
        RetryProgress = null;
        RetrySummary = summary;
        Load();
    }

    /// <summary>What a refused run reads, on both the pre-check and the service's own refusal.
    /// </summary>
    internal const string ScanInProgressMessage =
        "A scan is running. The retry is available when it finishes.";

    private static string Describe(UnresolvedRetry.RetryOutcome outcome)
    {
        if (outcome.Status == UnresolvedRetry.RetryStatus.ScanInProgress)
        {
            return ScanInProgressMessage;
        }

        var sentence =
            $"Retried {Plural(outcome.NamesExamined, "name")}: {outcome.NamesResolved} resolved, "
            + $"{Plural(outcome.FramesAssigned, "frame")} assigned, "
            + $"{outcome.NamesStillUnresolved} still unresolved.";

        return outcome.StoppedOnNetworkFailure
            ? sentence + " A catalogue could not be reached, so the rest were left for the next run."
            : sentence;
    }

    /// <summary>The one plural rule these two surfaces share: "1 frame", "12 frames". Internal
    /// rather than private because <c>CreateTargetViewModel</c> renders spec 12.7's "Created
    /// &lt;name&gt;, linked &lt;n&gt; frames from &lt;m&gt; unresolved names" with the same rule,
    /// and a second copy is the drift this avoids (design-lessons rule 1).</summary>
    internal static string Plural(int count, string noun)
        => count == 1
            ? "1 " + noun
            : count.ToString("N0", CultureInfo.InvariantCulture) + " " + noun + "s";

    /// <summary>
    /// Spec 12.9's merge, entered from a name rather than from a candidate: opens the dialog with
    /// this name as the merged-away side and no winner, so the dialog's own search box chooses the
    /// surviving target.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAssign))]
    private async Task AssignAsync(UnresolvedNameRowViewModel row)
    {
        if (_assignToTarget is null || _disposed)
        {
            return;
        }

        try
        {
            // Not wrapped in Task.Run: the dialog is a window, and a window is opened on the UI
            // thread. The delegate completes when the modal closes.
            var merged = await _assignToTarget(row.Name).ConfigureAwait(false);
            if (merged)
            {
                _post(Load);
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog was dismissed, or the list went away underneath it.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The merge dialog for unresolved name {ObjectName} failed", row.Name);
        }
    }

    // Null in production leaves the button disabled, which is the whole rule; the row itself is
    // never the reason it is unavailable.
    private bool CanAssign(UnresolvedNameRowViewModel row) => _assignToTarget is not null;

    /// <summary>
    /// Spec 12.7's Create target row action (PAR-001): opens the Targets tab's create form with
    /// this name as the primary name and already in the alias list. The write itself is
    /// <c>CreateTargetViewModel</c>'s; this list only opens the form.
    /// </summary>
    /// <remarks>The null guard is repeated in the body because <c>RelayCommand.Execute</c> ignores
    /// <c>CanExecute</c> (TRACKING section 6 item 13).</remarks>
    [RelayCommand(CanExecute = nameof(CanCreateTarget))]
    private void CreateTarget(UnresolvedNameRowViewModel row)
    {
        if (_createTarget is null || _disposed)
        {
            return;
        }

        _createTarget(row.Name);
    }

    // Same rule as CanAssign: null in production leaves the button disabled, and the row is never
    // the reason it is unavailable.
    private bool CanCreateTarget(UnresolvedNameRowViewModel row) => _createTarget is not null;

    // A scan can both add and remove unresolved names, so the list a scan just rewrote is re-read
    // here. ScanStatusService has already marshalled onto the UI thread.
    private void OnScanFinished(object? sender, EventArgs e) => Load();

    // The same shape TargetDetailViewModel.Load uses: a generation counter plus the lifetime
    // token, and the read itself on a background thread because UnresolvedNamesQuery is a
    // synchronous SQLite read and this list is constructed on the UI thread.
    private void Load()
    {
        if (_disposed)
        {
            return;
        }

        IsLoading = true;
        var generation = ++_generation;
        var token = _lifetime.Token;
        PendingLoad = Task.Run(
            () =>
            {
                try
                {
                    var rows = _load();
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _post(() => Publish(generation, rows));
                }
                catch (OperationCanceledException)
                {
                    // The list went away while the read was in flight.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Loading the unresolved-name list failed");
                    _post(() => Publish(generation, null));
                }
            },
            token);
    }

    // Runs on the UI thread, through the post seam. A response older than the newest request is
    // dropped here rather than overwriting it.
    private void Publish(int generation, IReadOnlyList<UnresolvedNameRow>? rows)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        if (rows is not null)
        {
            Names.Clear();
            foreach (var row in rows)
            {
                Names.Add(new UnresolvedNameRowViewModel(row));
            }

            _hasLoaded = true;
        }

        LoadFailed = rows is null;
        IsLoading = false;
        OnPropertyChanged(nameof(ShowAllResolved));
    }

    /// <summary>Cancels the lifetime source every background window is linked to, which also
    /// cancels a retry still walking the name list, and drops the scan subscription. Closing the
    /// application does not leave a resolution loop running.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();

        if (_scanStatus is not null)
        {
            _scanStatus.ScanFinished -= OnScanFinished;
            _scanStatus.PropertyChanged -= OnScanStatusChanged;
        }

        _lifetime.Dispose();
    }
}

/// <summary>
/// One row of spec 12.7's unresolved-name list: the name, its frame count, and the dashboard
/// group key the name belongs to. Formatting only; the retry and the assign action are the list's
/// commands, because one row must not own a write.
/// </summary>
public sealed class UnresolvedNameRowViewModel(UnresolvedNameRow row)
{
    public UnresolvedNameRow Row { get; } = row;

    public string Name => Row.Name;

    /// <summary><c>obj:&lt;name&gt;</c>, the key the dashboard groups these frames under, so an
    /// open action needs no second place that builds the prefix.</summary>
    public string GroupKey => Row.GroupKey;

    /// <summary>The same wording <c>SearchResultViewModel.FrameCountText</c> uses.</summary>
    public string FrameCountText => Row.FrameCount == 1 ? "1 frame" : $"{Row.FrameCount} frames";
}
