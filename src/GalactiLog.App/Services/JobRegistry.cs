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

    /// <summary>A job ended by <see cref="JobHandle.Dispose"/> alone, with no summary, that ran for
    /// less than this raises no notice: a trivial job that finished before anyone could look is
    /// noise, not news.</summary>
    public static readonly TimeSpan QuietImplicitFinish = TimeSpan.FromMilliseconds(500);

    private readonly Action<Action> _post;
    private readonly INotifier? _notifier;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>, the same
    /// seam with the same default that every other background writer in this application takes.
    /// </param>
    /// <param name="notifier">Where a finished job's notice goes. The registry is the one caller
    /// for every job, so feedback is structural rather than something each action has to remember
    /// (design-lessons rule 2). Null shows nothing, which is what most tests want.</param>
    /// <param name="now">The clock, local time. Defaults to <see cref="DateTimeOffset.Now"/>.</param>
    public JobRegistry(Action<Action>? post = null, INotifier? notifier = null, Func<DateTimeOffset>? now = null)
    {
        _post = post ?? UiPost.Default;
        _notifier = notifier;
        _now = now ?? (() => DateTimeOffset.Now);
    }

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
    /// <param name="notify">False for a quiet job: one that is a step of a larger action the user
    /// already hears about (the scan's sub-jobs), or background work the user did not ask for (a
    /// survey image fetch). A quiet job raises a notice only when it fails. The rule lives here,
    /// in the one place notices are raised, so no handler decides it for itself.</param>
    public JobHandle Begin(string kind, string title, Action? cancel = null, bool notify = true)
    {
        var job = new JobViewModel(kind, title, cancel, _now(), _now) { Notify = notify };
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

    internal void Finish(JobViewModel job, JobResult result, string summary, string? noticeText, bool implicitFinish)
    {
        // Stamped here, on the caller's thread, rather than inside the post, so the duration is
        // the job's and not the job's plus the dispatcher queue's.
        var finishedAt = _now();
        var notice = NoticeFor(job.Title, result, summary, noticeText, implicitFinish, finishedAt - job.StartedAt, job.Notify);

        _post(() =>
        {
            Running.Remove(job);
            RunningCount = Running.Count;

            // The last progress line is kept, so a reader who opens the flyout after the fact sees
            // both the last thing the job said and its outcome.
            job.MarkFinished(result, summary, finishedAt);

            Recent.Insert(0, job);
            while (Recent.Count > RecentCap)
            {
                Recent.RemoveAt(Recent.Count - 1);
            }

            if (notice is { } shown)
            {
                _notifier?.Show(shown.Text, shown.Kind);
            }
        });
    }

    /// <summary>What a finished job says in the notice stack, or null for nothing. Succeeded is
    /// a Success notice with the summary (the title plus " finished" when there is none), Failed
    /// is an Error, Cancelled is Info. A job ended only by <see cref="JobHandle.Dispose"/> with no
    /// summary in under <see cref="QuietImplicitFinish"/> says nothing.</summary>
    internal static (string Text, NoticeKind Kind)? NoticeFor(
        string title, JobResult result, string summary, string? noticeText, bool implicitFinish, TimeSpan ran,
        bool notify = true)
    {
        if (!notify && result != JobResult.Failed)
        {
            return null;
        }

        if (implicitFinish && summary.Length == 0 && ran < QuietImplicitFinish)
        {
            return null;
        }

        string Or(string fallback) => noticeText ?? (summary.Length > 0 ? summary : fallback);

        return result switch
        {
            JobResult.Failed => (Or(title + " failed"), NoticeKind.Error),
            JobResult.Cancelled => (Or(title + " cancelled"), NoticeKind.Info),
            _ => (Or(title + " finished"), NoticeKind.Success),
        };
    }
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

    /// <summary>Publishes the outcome, moves the job to the recent list and raises the job's
    /// notice. The first call wins; a second does nothing.</summary>
    /// <param name="result">The outcome.</param>
    /// <param name="summary">The one-line summary the flyout keeps.</param>
    /// <param name="notice">The notice text, when it should say more than the summary. Null uses
    /// the summary. The kind still follows <paramref name="result"/>.</param>
    public void Finish(JobResult result, string summary, string? notice = null)
        => Finish(result, summary, notice, implicitFinish: false);

    /// <summary>Finishes an unfinished job as <see cref="JobResult.Succeeded"/> with an empty
    /// summary, so a caller that throws past its own <see cref="Finish(JobResult, string, string?)"/>
    /// leaves no phantom running job in the flyout. Does nothing to a job that already finished.
    /// A job this ends in under <see cref="JobRegistry.QuietImplicitFinish"/> raises no notice.
    /// </summary>
    public void Dispose() => Finish(JobResult.Succeeded, "", null, implicitFinish: true);

    private void Finish(JobResult result, string summary, string? notice, bool implicitFinish)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        _registry.Finish(_job, result, summary, notice, implicitFinish);
    }
}

/// <summary>One row in the status bar's flyout: a running job, or a finished one kept in the
/// recent list with its outcome.</summary>
public sealed partial class JobViewModel : ObservableObject
{
    private readonly Action? _cancel;
    private readonly Func<DateTimeOffset> _now;

    internal JobViewModel(string kind, string title, Action? cancel, DateTimeOffset startedAt, Func<DateTimeOffset> now)
    {
        Kind = kind;
        Title = title;
        _cancel = cancel;
        _now = now;
        StartedAt = startedAt;
        Message = "";
        Summary = "";
        CanCancel = cancel is not null;
    }

    /// <summary>False for a quiet job, which raises a notice only on failure. See
    /// <see cref="JobRegistry.Begin"/>.</summary>
    internal bool Notify { get; init; } = true;

    /// <summary>When the job began, local time.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>When the job finished, local time, or null while it runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    public partial DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>The flyout's right-aligned time: the finish time once finished, "started 14:31"
    /// while running.</summary>
    public string TimeText => FinishedAt is { } finished
        ? FormatClock(finished, _now())
        : "started " + FormatClock(StartedAt, _now());

    /// <summary>The recent list's one status line: "Succeeded in 1 m 12 s", "Failed after 12 s",
    /// "Cancelled after 5 s". Empty while running.</summary>
    public string StatusLine => (Result, FinishedAt) switch
    {
        (JobResult.Succeeded, { } at) => "Succeeded in " + FormatDuration(at - StartedAt),
        (JobResult.Failed, { } at) => "Failed after " + FormatDuration(at - StartedAt),
        (JobResult.Cancelled, { } at) => "Cancelled after " + FormatDuration(at - StartedAt),
        _ => "",
    };

    /// <summary>Whether the recent list draws the last progress line. Not when it repeats the
    /// summary word for word, which the mosaic detection job's "34 suggestions" did.</summary>
    public bool ShowRecentMessage => HasMessage && !string.Equals(Message, Summary, StringComparison.Ordinal);

    /// <summary>"14:32" for today, "Oct 10 14:32" for any other day. No "yesterday" form.
    /// </summary>
    internal static string FormatClock(DateTimeOffset at, DateTimeOffset now)
        => at.Date == now.Date
            ? at.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : at.ToString("MMM d HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"under 1 s", "12 s", "1 m 12 s", "2 h 5 m".</summary>
    internal static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.FromSeconds(1))
        {
            return "under 1 s";
        }

        var seconds = (long)span.TotalSeconds;
        return seconds switch
        {
            < 60 => $"{seconds} s",
            < 3600 => $"{seconds / 60} m {seconds % 60} s",
            _ => $"{seconds / 3600} h {seconds % 3600 / 60} m",
        };
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
    [NotifyPropertyChangedFor(nameof(ShowRecentMessage))]
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
    [NotifyPropertyChangedFor(nameof(StatusLine))]
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
    [NotifyPropertyChangedFor(nameof(ShowRecentMessage))]
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

    internal void MarkFinished(JobResult result, string summary, DateTimeOffset finishedAt)
    {
        FinishedAt = finishedAt;
        Result = result;
        Summary = summary;
        IsFinished = true;
        CanCancel = false;
    }
}
