using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.Core.Scanning;
using GalactiLog.Data.Ingest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.Services;

/// <summary>
/// The one place scan progress crosses onto the UI thread (spec 4.2, 10.4). ScanCoordinator
/// raises its events on whatever thread reached the reporting point; every property below is
/// mutated only inside a dispatcher post, so bindings never see a cross-thread write. The
/// single App-layer subscriber to <see cref="ScanCoordinator.ProgressChanged"/> and
/// <see cref="ScanCoordinator.ScanFinished"/> -- nothing else in the application subscribes to
/// either for UI purposes; view-models bind to this instead.
/// </summary>
/// <remarks>
/// Review fix pass item 6: every property below is a partial property with a private setter
/// (CommunityToolkit.Mvvm 8.4.2), not a public-setter property over a private field. "Mutated
/// only inside a dispatcher post" used to be a convention a caller had to honor; now it is
/// structural -- nothing outside this class can assign these properties at all.
/// </remarks>
public sealed partial class ScanStatusService : ObservableObject, IDisposable
{
    private readonly ScanCoordinator _coordinator;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;
    private readonly JobRegistry? _jobs;

    /// <summary>The handle for the run in flight, or null between runs. Read and written only
    /// inside the post seam, which is what serializes it.</summary>
    private JobHandle? _scanJob;

    /// <summary>The handle for the PHD2 phase in flight, or null. Spec 10.3 step 5 and spec 12's
    /// job monitor paragraph both make the guide-log pass and the correlation their OWN jobs
    /// rather than phases of the scan job, which is what makes them census members ten and
    /// eleven. Read and written only inside the post seam, beside <see cref="_scanJob"/>.
    /// </summary>
    private JobHandle? _phd2Job;

    /// <summary>Which of the two PHD2 kinds <see cref="_phd2Job"/> is open for, or null.</summary>
    private string? _phd2JobKind;

    /// <param name="coordinator">The single process-wide scan orchestrator.</param>
    /// <param name="post">How to reach the UI thread. Defaults to Dispatcher.UIThread.Post.
    /// The only seam in this type, and it exists because a unit test has no dispatcher.</param>
    /// <param name="logger">F7: the guard below logs through the injected logger rather than the
    /// static Serilog one, so a test can observe it. AppHost passes the host's
    /// <c>ILogger&lt;ScanStatusService&gt;</c>; tests pass <c>NullLogger.Instance</c> by default.</param>
    /// <param name="jobs">Spec 12's job registry (PAR-015, ruling D1). A trailing optional
    /// parameter, like <c>StatusBarViewModel</c>'s <c>updates</c>, so no existing construction site
    /// moved: a service built with no registry simply registers nothing. This is the scan's
    /// registration site and the spec names it, because this type already sees every progress
    /// envelope and every finish. The registry is a second reader, never a second source of truth:
    /// nothing below reads back from it and the bar's own state is unchanged.</param>
    public ScanStatusService(
        ScanCoordinator coordinator,
        Action<Action>? post = null,
        ILogger? logger = null,
        JobRegistry? jobs = null)
    {
        _coordinator = coordinator;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;
        _jobs = jobs;

        TaskName = "";
        Message = "Ready";

        // Seeded synchronously, not through a post: a status bar built while a CLI-triggered
        // scan is already running must not read as idle for the one frame before the next
        // progress event arrives.
        //
        // No job is opened here for a run already in flight, deliberately. No ProgressChanged for
        // that run has arrived yet, so there is nothing to report on it and the first thing the
        // flyout would show is a title with no progress and no outcome until the next envelope. It
        // is the same gap the bar's own state has always had, and the next progress event closes
        // it by opening the job then.
        IsRunning = coordinator.IsRunning;
        ResolutionInProgress = coordinator.ResolutionInProgress;

        coordinator.ProgressChanged += OnProgressChanged;
        coordinator.ScanFinished += OnScanFinished;
        coordinator.ResolutionStateChanged += OnResolutionStateChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    public partial bool IsRunning { get; private set; }

    /// <summary>True while spec 9.7's unresolved-name retry holds the coordinator's resolution
    /// lease. A scan cannot start while it is held (Phase 7 fixer item 1), so the Run scan button
    /// greys on it exactly as it does on <see cref="IsRunning"/>.</summary>
    [ObservableProperty]
    public partial bool ResolutionInProgress { get; private set; }

    /// <summary>The raw vocabulary token (design-spec 10.4): "discovery", "classify", ...
    /// Turning it into display text is <c>StatusBarViewModel</c>'s job.</summary>
    [ObservableProperty]
    public partial string TaskName { get; private set; }

    [ObservableProperty]
    public partial string Message { get; private set; }

    [ObservableProperty]
    public partial double Percent { get; private set; }

    /// <summary>False when the envelope's <c>TotalSteps</c> is 0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    public partial bool HasDeterminatePercent { get; private set; }

    /// <summary>Convenience for <c>ProgressBar.IsIndeterminate</c>, so the view needs no
    /// negation converter.</summary>
    public bool IsIndeterminate => IsRunning && !HasDeterminatePercent;

    /// <summary>Raised on the UI thread after a scan finishes, so a page can refresh itself.
    /// Already on the UI thread by the time a subscriber sees it -- do not post again.</summary>
    public event EventHandler? ScanFinished;

    // The coordinator already guards subscriber exceptions and logs them (Phase 4 Task 5 fix
    // pass), but a handler that blocks would still throttle the scan's writer task, so this
    // does no work of its own beyond building the closure: no query, no log, no blocking call.
    private void OnProgressChanged(object? sender, ScanProgress progress)
    {
        _post(() =>
        {
            IsRunning = true;
            TaskName = progress.Task;
            Message = progress.Message;
            Percent = progress.Percent;
            HasDeterminatePercent = progress.TotalSteps > 0;

            // Spec 12: the scan is a registered job like any other. Opened on the first progress
            // of a run and reported on every one after it, inside this same closure so the job
            // and the bar can never disagree about which envelope they are showing. The cancel
            // delegate is the coordinator's own Cancel, the same bare Action AppHost already hands
            // to StatusBarViewModel; the status bar's CancelButton keeps its own binding and is
            // not re-pointed.
            _scanJob ??= _jobs?.Begin(ScanJobKind, ScanJobTitle, _coordinator.Cancel);
            _scanJob?.Report(
                progress.Message,
                progress.TotalSteps > 0 ? progress.Percent : null);

            TrackPhd2SubJob(progress);
        });
    }

    // The two PHD2 phases of spec 10.3 step 5 are their own jobs, opened and finished from the
    // same envelope stream inside the same closure, so the sub-job and the bar can never disagree
    // about which phase they are showing. An envelope of either PHD2 task opens (or keeps) a
    // sub-job of that kind; an envelope of any other task closes whichever is open, which is what
    // makes the phase's last envelope its terminal one without a second signal. Called only from
    // inside the post seam.
    private void TrackPhd2SubJob(ScanProgress progress)
    {
        var kind = progress.Task switch
        {
            ScanTaskNames.Phd2Ingest => Phd2IngestJobKind,
            ScanTaskNames.Phd2Correlate => Phd2CorrelateJobKind,

            // Phase 18 Task 4: the scan's mosaic detection pass (spec 7.7) is a job of its own in
            // the same shape, and its failed envelope uses the same -1 total.
            ScanTaskNames.MosaicDetection => MosaicDetectionJobKind,
            _ => null,
        };

        // Spec 10.9: a correlation that threw ends its registered job FAILED, carrying the same
        // reason as its one-line summary. The progress envelope is the only channel the Data-layer
        // coordinator has to this seam, so a failed phase reports a terminal envelope carrying
        // Phd2CorrelationEvents.FailedEnvelopeTotalSteps and the reason as its message. Without
        // this branch the next phase's envelope closes the sub-job Succeeded below, and a pass that
        // filled nothing reads as a finished, successful job in the flyout.
        if (kind is not null && progress.TotalSteps == Phd2CorrelationEvents.FailedEnvelopeTotalSteps)
        {
            if (kind == _phd2JobKind)
            {
                FinishPhd2SubJob(JobResult.Failed, progress.Message);
            }

            return;
        }

        if (kind is null || kind != _phd2JobKind)
        {
            // Spec 7.7: the detection job's summary is its terminal envelope's "n suggestions".
            FinishPhd2SubJob(JobResult.Succeeded, SubJobSummary);
        }

        if (kind is null)
        {
            return;
        }

        if (_phd2Job is null)
        {
            // A phase with nothing to do opens no job. Spec 10.4 has the guide-log pass emit its
            // envelope even when it found no log, so that a reader can tell "the pass ran and
            // found nothing" from "the pass did not run"; that is a status-bar fact, not a job.
            // Spec 12's census sentence scopes the registry to "every action that can run longer
            // than a second", and a pass over zero candidates cannot. Without this, every scan of
            // a library that holds no guide log would leave a finished "Reading PHD2 guide logs"
            // entry in the flyout beside the scan's own.
            if (progress.TotalSteps <= 0)
            {
                return;
            }

            _phd2JobKind = kind;
            _phd2Job = _jobs?.Begin(kind, Phd2JobTitle(kind), _coordinator.Cancel);
        }

        _phd2Job?.Report(progress.Message, progress.Percent);
        _subJobLastMessage = progress.Message;
    }

    // The last message the open sub-job reported, which is the mosaic detection job's summary.
    private string _subJobLastMessage = "";

    // A finished sub-job's summary: the PHD2 sentence, or the detection job's own "n suggestions".
    private string SubJobSummary => _phd2JobKind == MosaicDetectionJobKind ? _subJobLastMessage : Phd2JobSummary;

    private void FinishPhd2SubJob(JobResult result, string summary)
    {
        var finished = _phd2Job;
        _phd2Job = null;
        _phd2JobKind = null;
        finished?.Finish(result, summary);
    }

    // Review fix pass item 1: the coordinator schedules a pending follow-up scan BEFORE it
    // raises ScanFinished (Phase 4 Task 5 fix pass item 3), so by the time this closure runs
    // the coordinator may already be running again. Forcing IsRunning to false unconditionally
    // made the bar flash "Ready" mid follow-up; reading it live is the fix.
    private void OnScanFinished(object? sender, ScanFinishedEventArgs e)
    {
        _post(() =>
        {
            // One job per ScanFinished, not one spanning a run and its queued follow-up: the
            // flyout's value is one outcome per run, and the follow-up opens a second job on its
            // own first progress (questions.md Q4, as proposed). This runs before IsRunning is
            // re-read on purpose, so the finish is not conditional on the coordinator having
            // stopped.
            //
            // Phase 14B Task 5, coordinator override (Task 2 escalation 2). The coordinator now
            // carries the run's outcome on the event, so the job's result is the run's result: a
            // cancelled scan reads Cancelled in the flyout's recent list rather than Succeeded.
            // The three summaries are distinct sentences for the same reason.
            var finished = _scanJob;
            _scanJob = null;
            var (result, summary) = e.State switch
            {
                "cancelled" => (JobResult.Cancelled, ScanJobCancelledSummary),
                "failed" => (JobResult.Failed, ScanJobFailedSummary),
                _ => (JobResult.Succeeded, ScanJobSummary),
            };

            // Any PHD2 sub-job still open goes first, and with the run's own result: a scan the
            // user stopped inside the guide-log pass must not leave that pass reading Succeeded
            // in the flyout, and a sub-job left running would outlive the scan that owns it.
            FinishPhd2SubJob(result, summary == ScanJobSummary ? SubJobSummary : summary);
            finished?.Finish(result, summary);

            IsRunning = _coordinator.IsRunning;
            if (!IsRunning)
            {
                Percent = 0;
                Message = "Ready";
            }

            // Review fix pass item 2: a throwing subscriber (Task 7/8's page refresh) must not
            // take the window down with it, AND must not silently drop every subscriber
            // registered after it -- a plain try/catch around one Invoke() call only achieves
            // the first: a multicast delegate stops calling targets the instant one throws, so
            // wrapping the whole call still lets a throwing Task 7 handler swallow Task 8's.
            // Each target is therefore invoked, and guarded, on its own, and reports through the
            // injected logger (F7) so the guard is observable in a test.
            foreach (var handler in ScanFinished?.GetInvocationList() ?? [])
            {
                try
                {
                    ((EventHandler)handler).Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex, "A ScanStatusService.ScanFinished subscriber threw; other subscribers still ran");
                }
            }
        });
    }

    // The lease is taken and released on a background thread (the retry's own), so the mirror is
    // written inside a post like every other property here. Read live rather than assumed, for
    // the same reason OnScanFinished reads IsRunning live.
    private void OnResolutionStateChanged(object? sender, EventArgs e)
        => _post(() => ResolutionInProgress = _coordinator.ResolutionInProgress);

    /// <summary>The scan's snake_case job kind token (spec 12). One token, not a second
    /// vocabulary beside <c>ScanTaskNames</c>: the seven task names are phases within this one
    /// job.</summary>
    internal const string ScanJobKind = "scan";

    /// <summary>The scan job's display title in the flyout.</summary>
    internal const string ScanJobTitle = "Library scan";

    /// <summary>The scan job's one-line outcome summary.</summary>
    internal const string ScanJobSummary = "The scan finished.";

    /// <summary>The recent-list summary for a run the user stopped, or that shutdown stopped.
    /// </summary>
    internal const string ScanJobCancelledSummary = "The scan was stopped before it finished.";

    /// <summary>The recent-list summary for a run that threw. The exception itself is already in
    /// the log and in the failed <c>scan_runs</c> row; the flyout says which run to go and look
    /// at, not what went wrong.</summary>
    internal const string ScanJobFailedSummary = "The scan failed. See the log for details.";

    /// <summary>The guide-log pass's job kind (spec 10.3 step 5). It IS the envelope task name of
    /// spec 10.4, deliberately: spec 12's census paragraph says there is no second vocabulary, and
    /// these two phases are phases that are also jobs.</summary>
    internal const string Phd2IngestJobKind = ScanTaskNames.Phd2Ingest;

    /// <summary>The correlation's job kind (spec 7.6). The same token the out-of-scan re-run a
    /// profile map change dispatches registers under, so there is one kind for one activity
    /// however it was started.</summary>
    internal const string Phd2CorrelateJobKind = ScanTaskNames.Phd2Correlate;

    /// <summary>The flyout title for a PHD2 sub-job, by kind.</summary>
    internal static string Phd2JobTitle(string kind) => kind switch
    {
        Phd2CorrelateJobKind => "Matching guiding to frames",
        MosaicDetectionJobKind => MosaicDetectionJobTitle,
        _ => "Reading PHD2 guide logs",
    };

    /// <summary>Spec 7.7's detection job kind, the envelope task name itself, for the reason the
    /// two PHD2 kinds are: a phase that is also a job keeps its one token. The Mosaics page's Run
    /// Detection registers under the same kind.</summary>
    public const string MosaicDetectionJobKind = ScanTaskNames.MosaicDetection;

    /// <summary>Spec 7.7's detection job title.</summary>
    public const string MosaicDetectionJobTitle = "Mosaic detection";

    /// <summary>The recent-list summary for a finished PHD2 sub-job.</summary>
    internal const string Phd2JobSummary = "The guiding step finished.";

    public void Dispose()
    {
        _coordinator.ProgressChanged -= OnProgressChanged;
        _coordinator.ScanFinished -= OnScanFinished;
        _coordinator.ResolutionStateChanged -= OnResolutionStateChanged;
    }
}
