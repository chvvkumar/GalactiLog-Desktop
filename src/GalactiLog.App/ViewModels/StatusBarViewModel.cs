using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Core.Scanning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels;

/// <summary>
/// The persistent status bar (design-spec 12): scan state text, message, percent, and a cancel
/// button enabled only while a scan runs. Wraps <see cref="ScanStatusService"/>, which is
/// shared with pages that have no cancel button, so the command and its enablement stay a view
/// concern here instead of leaking into the shared service.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject, IDisposable
{
    // Keyed by the ScanTaskNames constants, not by string literals, so a vocabulary change
    // breaks the build instead of silently falling back to the raw token everywhere.
    private static readonly Dictionary<string, string> StateTextByTask = new()
    {
        [ScanTaskNames.Discovery] = "Discovering",
        [ScanTaskNames.Classify] = "Classifying",
        [ScanTaskNames.Ingest] = "Ingesting",
        [ScanTaskNames.PruneOrphans] = "Removing missing files",
        [ScanTaskNames.Phd2Ingest] = "Reading guide logs",
        [ScanTaskNames.Phd2Correlate] = "Matching guiding",
        [ScanTaskNames.Dedup] = "Finding duplicates",
        [ScanTaskNames.MosaicDetection] = "Detecting mosaics",
        [ScanTaskNames.RefThumbnails] = "Building thumbnails",
        [ScanTaskNames.PruneActivity] = "Pruning activity",
    };

    private readonly Action _cancelScan;
    private readonly Func<CancellationToken, Task>? _runScan;
    private readonly ILogger _logger;
    private readonly UpdateService? _updates;
    private readonly JobRegistry? _jobs;

    // Seeded from the service and replaced whole on every publish, so the four properties below
    // cannot disagree with one another about which update is in flight.
    private UpdateState _updateState = UpdateState.Nothing;
    private bool _disposed;

    /// <param name="status">The shared progress marshaller, bound directly for message and
    /// percent.</param>
    /// <param name="cancelScan">Bound in AppHost to <c>ScanCoordinator.Cancel</c>, matching how
    /// <c>WatcherService</c> and <c>ScanScheduler</c> already take delegates rather than the
    /// coordinator itself -- keeps this type constructible in a test with a counting lambda.</param>
    /// <param name="runScan">Bound in AppHost to the same manual-scan lambda the dashboard's own
    /// button uses. Phase 7 FIXER item 19: that button lives in the dashboard's empty state, so a
    /// library with frames in it had no manual rescan affordance at all. Null leaves the button
    /// disabled, which is what lets a test construct this with no coordinator.</param>
    /// <param name="logger">Optional. A scan that could not be started is logged, never rethrown:
    /// there is no global dispatcher exception handler to catch it.</param>
    /// <param name="updates">Spec 12's update indicator (Phase 10 Task 4). A trailing optional
    /// parameter, like <c>ScanCoordinator</c>'s own <c>ensureReferenceThumbnail</c>, so no
    /// existing construction site changes: a status bar built with no update service simply never
    /// shows the indicator.</param>
    /// <param name="jobs">Spec 12's job registry (PAR-015, ruling D1). The one thing the bar reads
    /// for its running-job count and flyout. Trailing and optional for the reason
    /// <paramref name="updates"/> is: a status bar built with no registry shows no job monitor at
    /// all.</param>
    public StatusBarViewModel(
        ScanStatusService status,
        Action cancelScan,
        Func<CancellationToken, Task>? runScan = null,
        ILogger<StatusBarViewModel>? logger = null,
        UpdateService? updates = null,
        JobRegistry? jobs = null)
    {
        Status = status;
        _cancelScan = cancelScan;
        _runScan = runScan;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _updates = updates;
        _jobs = jobs;
        Status.PropertyChanged += OnStatusPropertyChanged;

        if (_jobs is not null)
        {
            _jobs.PropertyChanged += OnJobsPropertyChanged;

            // Both collections, not only the running count: the monitor is the only way to reach
            // the recent list, so it must survive the last job ending.
            _jobs.Running.CollectionChanged += OnJobListChanged;
            _jobs.Recent.CollectionChanged += OnJobListChanged;
        }

        if (_updates is not null)
        {
            // Seeded synchronously, for the reason ScanStatusService seeds IsRunning that way: a
            // status bar built mid-download must not read as idle for the frame before the next
            // event arrives.
            _updateState = _updates.State;
            _updates.StateChanged += OnUpdateStateChanged;
        }
    }

    public ScanStatusService Status { get; }

    /// <summary>Spec 12's job registry, bound directly by the flyout's two lists. Null when the bar
    /// was built without one, which is what hides the monitor.</summary>
    public JobRegistry? Jobs => _jobs;

    /// <summary>Whether the running-count text beside the monitor button is shown, which is the
    /// one thing spec 12's "shows nothing when that number is zero" still governs after the
    /// coordinator's product correction. What puts the monitor itself on screen is
    /// <see cref="HasJobsToShow"/>.</summary>
    public bool HasRunningJobs => _jobs is { RunningCount: > 0 };

    /// <summary>Whether the monitor is on screen at all. Fix pass, review escalation 1: the spec
    /// keeps a finished job's outcome "because an outcome nobody has looked at yet is the reason the
    /// list exists", and hiding the button the instant the running count reached zero made that list
    /// unreachable at exactly the moment it became worth reading. The button is offered while a job
    /// runs or while anything is in the recent list; only the count itself hides at zero.</summary>
    public bool HasJobsToShow => _jobs is { } jobs && (jobs.RunningCount > 0 || jobs.Recent.Count > 0);

    /// <summary>Whether the flyout's "Recent" heading is drawn, which is whether there is anything
    /// under it (Phase 14B fixer, fixer list item 18). <see cref="HasRunningJobs"/> is the same
    /// gate for the "Running" heading.</summary>
    public bool HasRecentJobs => _jobs is { } jobs && jobs.Recent.Count > 0;

    /// <summary>The monitor's label. The spec asks for "the number of running jobs"; the noun is
    /// what makes a digit beside a progress bar read as a count rather than a percent.</summary>
    public string RunningJobsText => _jobs is { RunningCount: var count and > 0 }
        ? $"{count} job{(count == 1 ? "" : "s")}"
        : "";

    /// <summary>Human text for <see cref="ScanStatusService.TaskName"/>. An unknown token falls
    /// back to the token itself.</summary>
    public string StateText => StateTextByTask.GetValueOrDefault(Status.TaskName, Status.TaskName);

    /// <summary>
    /// Spec 12's "update indicator". Visible once a check has found something and until it is
    /// applied, and hidden otherwise.
    /// </summary>
    /// <remarks>
    /// <see cref="UpdatePhase.Idle"/> and <see cref="UpdatePhase.Checking"/> are hidden because a
    /// background check the user did not ask for is not a notification.
    /// <see cref="UpdatePhase.Failed"/> is hidden too: a failed background check is a log line and
    /// an About tab field, not an error banner the user has to dismiss. Spec 12 names one update
    /// indicator, not a notification area.
    /// </remarks>
    public bool UpdateAvailable => _updateState.Phase
        is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.ReadyToApply;

    /// <summary>What the indicator says. Empty while it is hidden.</summary>
    public string UpdateText => _updateState.Phase switch
    {
        UpdatePhase.Available => $"Update {_updateState.AvailableVersion} available",
        UpdatePhase.Downloading => $"Downloading update {_updateState.AvailableVersion}",
        UpdatePhase.ReadyToApply => $"Update {_updateState.AvailableVersion} ready to install",
        _ => "",
    };

    /// <summary>
    /// The download's progress, 0 to 100. Its own bar in the indicator, deliberately not the scan
    /// progress bar: they are two different operations, and one bar showing either is unreadable.
    /// </summary>
    public double UpdateDownloadPercent => _updateState.DownloadPercent;

    /// <summary>Whether the indicator's progress bar is shown.</summary>
    public bool IsDownloadingUpdate => _updateState.Phase == UpdatePhase.Downloading;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancelScan();

    private bool CanCancel => Status.IsRunning;

    /// <summary>
    /// The indicator's own affordance: opens spec 17.1's confirmation for an update that is
    /// already downloaded.
    /// </summary>
    /// <remarks>
    /// The mid-scan rule is the update service's and is repeated in the body here
    /// (TRACKING section 6 item 13), because <c>RelayCommand.Execute</c> ignores
    /// <c>CanExecute</c> and a click can land between a scan starting and the command being
    /// notified.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanShowUpdatePrompt))]
    private void ShowUpdatePrompt()
    {
        if (!CanShowUpdatePrompt())
        {
            return;
        }

        _updates!.PromptNow();
    }

    private bool CanShowUpdatePrompt()
        => !_disposed
            && _updates is not null
            && _updateState.Phase == UpdatePhase.ReadyToApply
            && !Status.IsRunning;

    /// <summary>Spec 12.10's Run Scan, on the one surface that is always on screen. The dashboard's
    /// own button is part of its empty state and disappears as soon as the library has frames.
    /// </summary>
    /// <remarks>
    /// No <see cref="CancellationToken"/> parameter (FIXER LIST F24, and Task 8's deviation D10):
    /// a command built from a <c>Func&lt;CancellationToken, Task&gt;</c> cancels the in-flight
    /// token on a second <c>Execute</c>, so a second press would abort the running scan rather
    /// than being refused by the guard below. Cancellation belongs to
    /// <see cref="CancelCommand"/>, which is the affordance next to this one.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRunScan))]
    private async Task RunScanAsync()
    {
        // TRACKING item 13: CanExecute is the affordance, the body is the guard. A direct Execute,
        // and a click that lands between a scan starting and the command being notified, both
        // arrive here, and the second press is refused rather than cancelling the first.
        if (_runScan is null || Status.IsRunning || Status.ResolutionInProgress)
        {
            return;
        }

        try
        {
            // Off the dispatcher: RunAsync validates the configured roots synchronously before it
            // returns a task, which is the dashboard's reason for the same Task.Run.
            await Task.Run(() => _runScan(CancellationToken.None), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The user cancelled the scan. Not a failure.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The status bar could not start a scan");
        }
    }

    // The same three conditions the dashboard's button uses, so the two agree: the coordinator
    // refuses a scan while one runs and while the unresolved-name retry holds the lease.
    private bool CanRunScan()
        => _runScan is not null && !Status.IsRunning && !Status.ResolutionInProgress;

    private void OnStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScanStatusService.IsRunning):
                CancelCommand.NotifyCanExecuteChanged();
                RunScanCommand.NotifyCanExecuteChanged();
                // The update prompt is suppressed while a scan runs (spec 17.1), so the
                // indicator's own affordance follows the same state the Run scan button does.
                ShowUpdatePromptCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(StateText));
                break;
            case nameof(ScanStatusService.ResolutionInProgress):
                RunScanCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ScanStatusService.TaskName):
                // Not called out separately by name in the brief's "when IsRunning changes"
                // wording, but TaskName changes several times within one run (discovery ->
                // classify -> ingest -> ...) without IsRunning ever toggling, and StateText
                // reads TaskName -- so the display would freeze on the first phase's label for
                // the rest of the run without this.
                OnPropertyChanged(nameof(StateText));
                break;
        }
    }

    // The registry publishes every mutation through its own post seam, so this is already on the
    // UI thread. Null PropertyName is not handled, matching OnStatusPropertyChanged above rather
    // than MaintenanceTabViewModel's equivalent, which does: the two shapes differ and the file
    // you are in is the one to follow.
    private void OnJobsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JobRegistry.RunningCount))
        {
            OnPropertyChanged(nameof(HasRunningJobs));
            OnPropertyChanged(nameof(RunningJobsText));
            OnPropertyChanged(nameof(HasJobsToShow));
        }
    }

    // The registry mutates both collections inside its own post seam, so this is already on the UI
    // thread. HasJobsToShow and HasRecentJobs read them; the count has its own notification above.
    private void OnJobListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasJobsToShow));
        OnPropertyChanged(nameof(HasRecentJobs));
    }

    // Already on the UI thread by the time this runs: UpdateService publishes through its own
    // dispatcher seam. Do not post again.
    private void OnUpdateStateChanged(object? sender, UpdateState state)
    {
        if (_disposed)
        {
            return;
        }

        _updateState = state;
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(UpdateText));
        OnPropertyChanged(nameof(UpdateDownloadPercent));
        OnPropertyChanged(nameof(IsDownloadingUpdate));
        ShowUpdatePromptCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Status.PropertyChanged -= OnStatusPropertyChanged;

        if (_jobs is not null)
        {
            _jobs.PropertyChanged -= OnJobsPropertyChanged;
            _jobs.Running.CollectionChanged -= OnJobListChanged;
            _jobs.Recent.CollectionChanged -= OnJobListChanged;
        }

        if (_updates is not null)
        {
            _updates.StateChanged -= OnUpdateStateChanged;
        }
    }
}
