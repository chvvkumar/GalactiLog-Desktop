using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GalactiLog.App.Services;

/// <summary>The outcome a job reports when it ends (design-spec 12, PAR-015).</summary>
public enum JobResult
{
    /// <summary>The action completed.</summary>
    Succeeded,

    /// <summary>The action could not be completed.</summary>
    Failed,

    /// <summary>The action stopped before it completed, at the user's request or on shutdown.</summary>
    Cancelled,
}

/// <summary>
/// The one registry every long-running action in the application registers with (design-spec 12,
/// ruling D1). The status bar reads this and nothing else for its running-job count and flyout.
/// </summary>
/// <remarks>
/// <para>
/// Built at the second occurrence of the pattern rather than the sixth (design-lessons rule 1).
/// The application already carries four unrelated background-work mechanisms with no shared type
/// between them; this is the shared spine the later ones register with, and the census test in
/// <c>JobRegistryCensusTest</c> is what keeps registration a rule rather than a convention.
/// </para>
/// <para>
/// <b>It holds no database handle, writes no activity event and owns no thread.</b> An action that
/// wants an activity event emits its own (spec 10.9); registering is how it becomes visible, not
/// what it does. It is therefore not <see cref="IDisposable"/> either: there is nothing to release,
/// and a disposable singleton would join the host's shutdown order for no reason.
/// </para>
/// <para>
/// <b>It serializes nothing and refuses nothing.</b> <see cref="Begin"/> never blocks, never
/// returns null and never throws on a second concurrent job. An action that must not run beside
/// another refuses in its own service, where that rule already lives: the resolution lease inside
/// <c>ScanCoordinator</c>, <c>TargetRebuild</c>, <c>UnresolvedRetry</c> and <c>DatabaseReset</c>.
/// </para>
/// <para>
/// <b>Threading.</b> Every mutation of <see cref="Running"/>, <see cref="Recent"/>,
/// <see cref="RunningCount"/> and of a <see cref="JobViewModel"/>'s own state happens inside the
/// post seam, and there is no lock anywhere in this file. The post is what serializes the list
/// arithmetic, so a lock would guard nothing a single dispatcher queue does not already guard, and
/// the collections have dispatcher affinity through their bindings in any case.
/// <see cref="Begin"/> is callable from any thread because it only constructs a row and hands a
/// closure to the post.
/// </para>
/// </remarks>
public sealed partial class JobRegistry : ObservableObject
{
    /// <summary>How many finished jobs <see cref="Recent"/> keeps. Spec 12: a finished entry stays
    /// until an eleventh newer one pushes it out, and nothing clears it on a timer.</summary>
    public const int RecentCap = 10;

    private readonly Action<Action> _post;

    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>, the same
    /// seam with the same default that every other background writer in this application takes.
    /// </param>
    public JobRegistry(Action<Action>? post = null) => _post = post ?? UiPost.Default;

    /// <summary>The jobs running now, in the order they started.</summary>
    public ObservableCollection<JobViewModel> Running { get; } = [];

    /// <summary>The most recently finished jobs, newest first, capped at <see cref="RecentCap"/>.
    /// </summary>
    public ObservableCollection<JobViewModel> Recent { get; } = [];

    /// <summary>How many jobs are running. The status bar shows nothing when this is zero.
    /// </summary>
    [ObservableProperty]
    public partial int RunningCount { get; private set; }

    /// <summary>Registers a job that is starting.</summary>
    /// <param name="kind">A stable snake_case kind token. The tokens are the ones that already
    /// exist: <c>scan</c> for the scan, and each Maintenance action's own spec 10.9
    /// <c>action</c> value. There is no second token vocabulary.</param>
    /// <param name="title">The display title the flyout shows.</param>
    /// <param name="cancel">What to call to cancel, or null when the action cannot be cancelled.
    /// </param>
    /// <returns>The handle the caller reports and finishes through. A handle rather than an id
    /// taken back, because a handle cannot name a job that was never begun, and its
    /// <see cref="JobHandle.Dispose"/> is the one place a forgotten
    /// <see cref="JobHandle.Finish"/> is caught.</returns>
    public JobHandle Begin(string kind, string title, Action? cancel = null)
    {
        var job = new JobViewModel(kind, title, cancel);
        _post(() =>
        {
            Running.Add(job);
            RunningCount = Running.Count;
        });

        return new JobHandle(this, job);
    }

    internal void Report(JobViewModel job, string message, double? percent)
        => _post(() =>
        {
            job.Message = message;
            job.Percent = percent;
        });

    internal void Finish(JobViewModel job, JobResult result, string summary)
        => _post(() =>
        {
            Running.Remove(job);
            RunningCount = Running.Count;

            // The last progress line is kept, so a reader who opens the flyout after the fact sees
            // both the last thing the job said and its outcome.
            job.MarkFinished(result, summary);

            Recent.Insert(0, job);
            while (Recent.Count > RecentCap)
            {
                Recent.RemoveAt(Recent.Count - 1);
            }
        });
}

/// <summary>
/// One registered job's ticket. Returned by <see cref="JobRegistry.Begin"/> and held by the action
/// that started the work.
/// </summary>
public sealed class JobHandle : IDisposable
{
    private readonly JobRegistry _registry;
    private readonly JobViewModel _job;

    // Interlocked rather than a bool: Report and Finish arrive from the action's own thread, and
    // Dispose can arrive from a finally block on another.
    private int _finished;

    internal JobHandle(JobRegistry registry, JobViewModel job)
    {
        _registry = registry;
        _job = job;
    }

    /// <summary>Publishes one progress line, with an optional determinate percent. Null percent
    /// means the job has no determinate progress and the flyout shows no bar for it. Ignored once
    /// the job has finished.</summary>
    public void Report(string message, double? percent = null)
    {
        if (Volatile.Read(ref _finished) != 0)
        {
            return;
        }

        _registry.Report(_job, message, percent);
    }

    /// <summary>Publishes the outcome and moves the job to the recent list. The first call wins;
    /// a second does nothing.</summary>
    public void Finish(JobResult result, string summary)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        _registry.Finish(_job, result, summary);
    }

    /// <summary>Finishes an unfinished job as <see cref="JobResult.Succeeded"/> with an empty
    /// summary, so a caller that throws past its own <see cref="Finish"/> leaves no phantom running
    /// job in the flyout. Does nothing to a job that already finished.</summary>
    public void Dispose() => Finish(JobResult.Succeeded, "");
}

/// <summary>One row in the status bar's flyout: a running job, or a finished one kept in the
/// recent list with its outcome.</summary>
public sealed partial class JobViewModel : ObservableObject
{
    private readonly Action? _cancel;

    internal JobViewModel(string kind, string title, Action? cancel)
    {
        Kind = kind;
        Title = title;
        _cancel = cancel;
        Message = "";
        Summary = "";
        CanCancel = cancel is not null;
    }

    /// <summary>The stable snake_case kind token the job registered with.</summary>
    public string Kind { get; }

    /// <summary>The display title.</summary>
    public string Title { get; }

    /// <summary>The last progress line the job reported. Kept after the job finishes, and
    /// rendered in the recent list too (Phase 14B fixer, fixer list item 17): keeping it and never
    /// showing it made the stated reason for keeping it, that a reader who opens the flyout after
    /// the fact sees where the job got to, unreachable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string Message { get; internal set; }

    /// <summary>Whether there is a progress line to draw. A computed property rather than a
    /// converter, the way <see cref="HasPercent"/> is.</summary>
    public bool HasMessage => Message.Length > 0;

    /// <summary>The determinate percent, or null when the job has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPercent))]
    [NotifyPropertyChangedFor(nameof(PercentOrZero))]
    public partial double? Percent { get; internal set; }

    /// <summary>Whether the flyout draws a bar for this job. A computed property rather than a
    /// converter, the way <c>ScanStatusService.IsIndeterminate</c> is.</summary>
    public bool HasPercent => Percent is not null;

    /// <summary>What the flyout's bar binds to. <see cref="Percent"/> itself is nullable and
    /// <c>ProgressBar.Value</c> is not; a computed property is this repository's answer to that,
    /// not a converter. The bar is hidden whenever the value would be this fallback.</summary>
    public double PercentOrZero => Percent ?? 0;

    /// <summary>True when the job registered a cancel delegate and has not finished.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool CanCancel { get; private set; }

    /// <summary>The outcome, or null while the job is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    public partial JobResult? Result { get; private set; }

    /// <summary>The outcome as the one word the flyout shows. Empty while the job is running.
    /// </summary>
    public string ResultText => Result switch
    {
        JobResult.Succeeded => "Succeeded",
        JobResult.Failed => "Failed",
        JobResult.Cancelled => "Cancelled",
        _ => "",
    };

    /// <summary>The one-line outcome summary. Empty while the job is running.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; }

    /// <summary>True once the job has reported an outcome.</summary>
    [ObservableProperty]
    public partial bool IsFinished { get; private set; }

    /// <summary>Cancels the job.</summary>
    /// <remarks>
    /// The gate is repeated in the body because <c>RelayCommand.Execute</c> ignores
    /// <c>CanExecute</c> (<c>TRACKING.md</c> section 6 item 13), so a direct Execute and a click
    /// that lands between the job finishing and the command being notified both arrive here and
    /// are refused.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (!CanCancel || _cancel is null)
        {
            return;
        }

        _cancel();
    }

    internal void MarkFinished(JobResult result, string summary)
    {
        Result = result;
        Summary = summary;
        IsFinished = true;
        CanCancel = false;
    }
}
