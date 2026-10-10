using System.Collections.Concurrent;
using System.Globalization;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Core.Io;
using GalactiLog.Core.Wbpp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.Services;

/// <summary>
/// The owner of every in-app stacking copy (spec 12.13), for the process: the copy outlives the
/// export window that started it. Each run is one cancellable <see cref="CopyJobKind"/> job with
/// its progress, one Activity row when it copied something or went wrong, and one warning in the
/// log carrying the file list when it went wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>The overlap rule lives here</b> (design-lessons rule 2): two copies whose destinations sit
/// one inside the other, in either direction, never run at once. <see cref="RefusalFor"/> is what
/// the wizard asks before it commits, and <see cref="StartAsync"/> repeats the check under the same
/// lock, so a race cannot slip past it.
/// </para>
/// <para>
/// <b>It never waits on the UI thread.</b> Every await is <c>ConfigureAwait(false)</c> and the
/// registry and the viewer only post, so the blocking shutdown drain cannot deadlock against a
/// run. It is not <see cref="IDisposable"/>: <c>App.DrainForShutdown</c> cancels and joins it.
/// </para>
/// </remarks>
/// <param name="jobs">The job registry every run registers with. Null in a case that does not
/// assert it.</param>
/// <param name="emit">(severity, event type, message, details): one <c>user_action</c> Activity
/// row, normally <c>ActivityRepository.EmitStandalone</c>.</param>
public sealed class StagingCopyService(
    JobRegistry? jobs = null,
    Action<string, string, string, object>? emit = null,
    ILogger? logger = null)
{
    /// <summary>The copy's job kind in the status bar's registry, census member fifteen.</summary>
    public const string CopyJobKind = "stacking_copy";

    /// <summary>The running row's message once Cancel is pressed, until the files in flight
    /// finish.</summary>
    public const string CancellingText = "Cancelling: finishing the files being copied";

    /// <summary>Where the logged file list can be read.</summary>
    internal const string LogPointer = " The file list is in the log viewer on the Diagnostics page.";

    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly object _gate = new();
    private readonly List<Running> _running = [];
    private bool _stopping;

    /// <summary>Why a copy into <paramref name="destination"/> may not start now, or null when it
    /// may.</summary>
    public string? RefusalFor(string destination)
    {
        var full = Path.GetFullPath(destination);
        lock (_gate)
        {
            if (_stopping)
            {
                return "GalactiLog is closing.";
            }

            return _running.FirstOrDefault(entry => PathConfinement.IsUnderOrEqual(entry.Destination, full)
                    || PathConfinement.IsUnderOrEqual(full, entry.Destination)) is { } busy
                ? "A copy into " + busy.Destination + " is still running. Wait for it to finish or cancel it "
                    + "from the Jobs list in the status bar, or choose another staging folder."
                : null;
        }
    }

    /// <summary>Starts one copy and returns its result. The task never faults: an exception from
    /// <paramref name="run"/> becomes a Cancelled or Aborted result. A refused start returns an
    /// Aborted result at once and registers no job.</summary>
    /// <param name="run">The copy itself, normally <c>WbppExportViewModel.RunCopyAsync</c>. Its
    /// second argument wraps the copier's io so the service knows which files are open, for the
    /// shutdown drain's warning.</param>
    /// <param name="viewer">The open window's progress, or null. Raised inside the copier's lock,
    /// so it only posts.</param>
    /// <param name="ct">The window's Cancel. Linked, so disposing it does not stop the copy.</param>
    public Task<StagingCopyResult> StartAsync(
        string targetName,
        string destination,
        Func<IProgress<StagingProgress>, Func<StagingIo, StagingIo>, CancellationToken, Task<StagingCopyResult>> run,
        IProgress<StagingProgress>? viewer,
        CancellationToken ct)
    {
        Running entry;
        lock (_gate)
        {
            if (RefusalFor(destination) is { } refusal)
            {
                return Task.FromResult(new StagingCopyResult(StagingOutcome.Aborted, 0, 0, [], [], [], refusal));
            }

            entry = new Running(Path.GetFullPath(destination), CancellationTokenSource.CreateLinkedTokenSource(ct));
            _running.Add(entry);
        }

        return RunCoreAsync(entry, targetName, run, viewer);
    }

    /// <summary>The other <c>StartAsync</c>, for a run with no io to track.</summary>
    public Task<StagingCopyResult> StartAsync(
        string targetName,
        string destination,
        Func<IProgress<StagingProgress>, CancellationToken, Task<StagingCopyResult>> run,
        IProgress<StagingProgress>? viewer,
        CancellationToken ct)
        => StartAsync(targetName, destination, (progress, _, token) => run(progress, token), viewer, ct);

    /// <summary>Cancels every run and refuses new ones: no new file starts, and files in flight
    /// finish. Idempotent.</summary>
    public void CancelAll()
    {
        Running[] snapshot;
        lock (_gate)
        {
            _stopping = true;
            snapshot = [.. _running];
        }

        foreach (var entry in snapshot)
        {
            entry.TryCancel();
        }
    }

    /// <summary>Waits up to <paramref name="budget"/> for every run to end. False when one is still
    /// finishing files: the exit cuts those short, so each open file is named in a warning, for
    /// the user to delete before the export runs again.</summary>
    public bool WaitForIdle(TimeSpan budget)
    {
        Running[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _running];
        }

        if (budget < TimeSpan.Zero)
        {
            budget = TimeSpan.Zero;
        }

        if (Task.WaitAll([.. snapshot.Select(entry => (Task)entry.Done.Task)], budget))
        {
            return true;
        }

        foreach (var entry in snapshot.Where(entry => !entry.Done.Task.IsCompleted))
        {
            _logger.LogWarning(
                "Shutdown drain budget of {Seconds}s elapsed with a stacking copy into {Destination} still finishing files; files in flight may be left short",
                budget.TotalSeconds,
                entry.Destination);
            foreach (var path in entry.InFlight.Keys)
            {
                _logger.LogWarning("Stacking copy file likely left part written by the exit: {Path}", path);
            }
        }

        return false;
    }

    /// <summary>"8.1 GB of 17.2 GB, 112 MB/s, about 1m 25s left".</summary>
    internal static string TransferStats(StagingProgress p, TimeSpan elapsed)
    {
        var text = MetricText.Bytes(p.BytesDone) + " of " + MetricText.Bytes(p.BytesTotal);
        var rate = RateText(p, elapsed);
        return rate.Length > 0 ? text + ", " + rate : text;
    }

    /// <summary>"112 MB/s, about 1m 25s left", or empty. The speed is the run's average over bytes
    /// written, so skipped files do not inflate it; it is left out until a second has
    /// passed.</summary>
    internal static string RateText(StagingProgress p, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 1 || p.BytesWritten <= 0)
        {
            return "";
        }

        var speed = p.BytesWritten / elapsed.TotalSeconds;
        var left = Math.Max(0, p.BytesTotal - p.BytesDone) / speed;
        return MetricText.Bytes(speed) + "/s, about " + GuidingSectionViewModel.CompactDuration(left) + " left";
    }

    private async Task<StagingCopyResult> RunCoreAsync(
        Running entry,
        string targetName,
        Func<IProgress<StagingProgress>, Func<StagingIo, StagingIo>, CancellationToken, Task<StagingCopyResult>> run,
        IProgress<StagingProgress>? viewer)
    {
        JobHandle? job = null;
        StagingCopyResult result;
        try
        {
            // Inside the try, as Phd2CorrelationRunner's is: a post during shutdown may throw.
            // A method group, not a lambda: a lambda would share this method's closure with the
            // relay's, and the finished row in Recent would keep the viewer, and through it the
            // closed window, alive.
            job = jobs?.Begin(CopyJobKind, "Copy for stacking: " + targetName, entry.CancelQuietly);
            entry.Job = job;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Relay(p =>
            {
                // Short enough for the flyout's one line; the bar carries the bytes.
                var rate = RateText(p, clock.Elapsed);
                entry.Percent = p.BytesTotal > 0 ? 100d * p.BytesDone / p.BytesTotal : 0d;
                job?.Report(
                    entry.Cancel.IsCancellationRequested
                        ? CancellingText
                        : string.Create(CultureInfo.InvariantCulture, $"Copying {p.FilesDone:N0} of {p.FilesTotal:N0} files")
                            + (rate.Length > 0 ? ", " + rate : ""),
                    entry.Percent);
                viewer?.Report(p);
            });

            result = await run(progress, entry.Track, entry.Cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new StagingCopyResult(StagingOutcome.Cancelled, 0, 0, [], [], [], null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnauthorizedPathException)
        {
            _logger.LogWarning(ex, "The staging copy could not start");
            result = new StagingCopyResult(StagingOutcome.Aborted, 0, 0, [], [], [], ex.Message);
        }
        catch (Exception ex)
        {
            // Anything else still ends as a result, so neither the window nor the drain waits on a fault.
            _logger.LogError(ex, "The staging copy failed");
            result = new StagingCopyResult(
                StagingOutcome.Aborted, 0, 0, [], [], [], "an unexpected error: " + ex.Message);
        }

        try
        {
            var problems = HasProblems(result);
            job?.Finish(
                result.Outcome switch
                {
                    StagingOutcome.Completed => JobResult.Succeeded,
                    StagingOutcome.Cancelled => JobResult.Cancelled,
                    _ => JobResult.Failed,
                },
                ResultStep.OutcomeTextFor(result) + " " + ResultStep.CountsTextFor(result)
                    + (problems ? LogPointer : ""));
            Record(entry, targetName, result, problems);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Finishing the staging copy's job failed");
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(entry);
            }

            entry.Cancel.Dispose();
            entry.Done.TrySetResult();
        }

        return result;
    }

    // Wider than ResultStep.HasProblems: an aborted run is a problem even with nothing listed.
    private static bool HasProblems(StagingCopyResult r)
        => r.Outcome == StagingOutcome.Aborted
            || r.Failed.Count > 0
            || r.PartialPaths.Count > 0
            || r.Skipped.Any(skip => skip.Reason == StagingSkipReason.ExistsDifferentSize);

    // The same outcome whether the window is open or closed: one Activity row, and the file list in
    // the log, since nothing can be saved once the window and its picker are gone.
    private void Record(Running entry, string targetName, StagingCopyResult r, bool problems)
    {
        if (problems)
        {
            try
            {
                _logger.LogWarning(
                    "The stacking copy ended with problems:{NewLine}{Report}",
                    Environment.NewLine,
                    ResultStep.ReportTextFor(targetName, entry.Destination, r));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Logging the staging copy's report failed");
            }
        }

        if (emit is null || (r.Copied == 0 && !problems))
        {
            return;
        }

        bool stopping;
        lock (_gate)
        {
            stopping = _stopping;
        }

        var suffix = r.Outcome switch
        {
            StagingOutcome.Cancelled => stopping ? " (cancelled because GalactiLog closed)" : " (cancelled)",
            StagingOutcome.Aborted => " (stopped: " + r.AbortReason + ")",
            _ => "",
        };

        int Count(StagingSkipReason reason) => r.Skipped.Count(skip => skip.Reason == reason);
        try
        {
            emit(
                problems ? "warning" : "info",
                CopyJobKind,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Export for stacking copied {r.Copied:N0} files to {Path.GetFileName(Path.TrimEndingDirectorySeparator(entry.Destination))}")
                    + suffix,
                new
                {
                    target = targetName,
                    destination = entry.Destination,
                    outcome = r.Outcome.ToString(),
                    copied = r.Copied,
                    bytes_copied = r.BytesCopied,
                    skipped_same_size = Count(StagingSkipReason.ExistsSameSize),
                    skipped_different_size = Count(StagingSkipReason.ExistsDifferentSize),
                    skipped_linked = Count(StagingSkipReason.ReparsePoint),
                    failed = r.Failed.Count,
                    partial = r.PartialPaths.Count,
                });
        }
        catch (Exception ex)
        {
            // Guarded: a row the activity log could not take must not turn a copy into a failure.
            _logger.LogWarning(ex, "The {EventType} event could not be written", CopyJobKind);
        }
    }

    private sealed class Running(string destination, CancellationTokenSource cancel)
    {
        public string Destination { get; } = destination;

        public CancellationTokenSource Cancel { get; } = cancel;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The destination files open for writing now.</summary>
        public ConcurrentDictionary<string, byte> InFlight { get; } = new(StringComparer.OrdinalIgnoreCase);

        public JobHandle? Job { get; set; }

        public double Percent { get; set; }

        // The status bar's cancel may arrive after the run ended and the source was disposed.
        public bool TryCancel()
        {
            try
            {
                Cancel.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        // The status bar's Cancel: the row says so at once, since the files in flight can take a
        // while to finish.
        public void CancelQuietly()
        {
            if (TryCancel())
            {
                Job?.Report(CancellingText, Percent);
            }
        }

        public StagingIo Track(StagingIo io)
            => io with { CreateDestination = path => new TrackedStream(io.CreateDestination(path), path, InFlight) };
    }

    // Holds its path in the run's in-flight set from open to dispose.
    private sealed class TrackedStream : Stream
    {
        private readonly Stream _inner;
        private readonly string _path;
        private readonly ConcurrentDictionary<string, byte> _inFlight;

        public TrackedStream(Stream inner, string path, ConcurrentDictionary<string, byte> inFlight)
        {
            _inner = inner;
            _path = path;
            _inFlight = inFlight;
            inFlight[path] = 0;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _inner.Dispose();
                }
                finally
                {
                    _inFlight.TryRemove(_path, out _);
                }
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _inFlight.TryRemove(_path, out _);
            }

            GC.SuppressFinalize(this);
        }
    }

    // Raised inside the copier's lock, so it stays O(1): the registry and the viewer only post.
    private sealed class Relay(Action<StagingProgress> report) : IProgress<StagingProgress>
    {
        public void Report(StagingProgress value) => report(value);
    }
}
