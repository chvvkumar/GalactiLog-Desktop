using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.Services;

/// <summary>
/// Spec 11.4's bounded on-demand thumbnail worker: most-recently-requested served first, at most
/// <see cref="MaxConcurrency"/> renders in flight (spec 10.6), and a request for something already
/// queued or already running is folded into the existing one rather than duplicated.
/// </summary>
/// <remarks>
/// <para>
/// LIFO, not FIFO, and that is the whole point: a user stepping through frames queues a request
/// per step, and by the time the worker reaches the head of a FIFO queue the user is somewhere
/// else entirely. Serving the newest request first means the frame on screen is the one that
/// renders.
/// </para>
/// <para>
/// This type writes nothing and knows nothing about keys, widths or pixels: the two delegates are
/// <c>ThumbnailCache.EnsureFrame</c> and <c>ThumbnailCache.EnsurePreview</c>, and the cache owns
/// every write (spec 2.1). A null from either is a normal outcome (spec 6.2.6, an unrenderable
/// frame) and is completed as null so the caller shows a placeholder.
/// </para>
/// <para>
/// A long-lived service holding two background tasks: registered as a factory so DI disposes it,
/// and drained at shutdown (<c>TRACKING.md</c> section 6 item 6). Disposing cancels every pending
/// and running render and completes every outstanding callback as "not rendered".
/// </para>
/// </remarks>
public sealed class ThumbnailWorker : IDisposable
{
    /// <summary>Spec 10.6's thumbnail row. Memory-bound: a full-frame decode of a 60 megapixel
    /// sensor is large, and a preview at native resolution is larger still. Public so every
    /// caller reads the budget rather than re-deciding it.</summary>
    public const int MaxConcurrency = 2;

    /// <summary>Spec 10.5's shutdown budget, the same five seconds the scan drain uses.</summary>
    internal static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<string, CancellationToken, string?> _ensureFrame;
    private readonly Func<string, CancellationToken, string?> _ensurePreview;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    private readonly Lock _gate = new();

    // A Stack plus a counting semaphore, not a Channel: Channel<T> is FIFO and has no withdraw,
    // and spec 11.4 needs both. Every push is paired with exactly one Release, so a pump that
    // takes a permit finds either a request or a stack already emptied by its peer.
    //
    // ponytail: no length cap. "Bounded" here is spec 11.4's concurrency of 2 plus LIFO order,
    // deduplication by key and withdrawal, which is what keeps the queue short in practice: the
    // modal is v1's only requester (ruling Q11), it withdraws the previous request on every step,
    // and a re-request folds into the pending one rather than adding to it. Ceiling: a future
    // requester that fires per row without withdrawing (a frame grid) could grow this without
    // limit. Upgrade path: a max length, dropping from the bottom of the stack, which is the
    // oldest request and the one LIFO was never going to reach anyway.
    private readonly Stack<Request> _stack = new();
    private readonly SemaphoreSlim _pending = new(0);

    // Pending and running requests by key. A second request for a key already here attaches its
    // callback to the existing one; without this, stepping back and forth through a list queues
    // the same frames over and over. The entry leaves on completion, on disposal, and when the
    // last callback of a not-yet-started request is withdrawn, so a later request re-queues
    // cleanly. Its Count is exactly "pending plus running".
    private readonly Dictionary<string, Request> _live = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task[] _pumps;
    private bool _disposed;

    /// <param name="ensureFrame"><c>ThumbnailCache.EnsureFrame</c>. Synchronous, runs on a pump
    /// thread, and returns a cache-relative path or null.</param>
    /// <param name="ensurePreview"><c>ThumbnailCache.EnsurePreview</c>. Shares the same queue and
    /// the same budget of two: a preview render and a thumbnail render are both a full-frame
    /// decode.</param>
    /// <param name="post">How to reach the UI thread; defaults to
    /// <see cref="UiPost.Default"/>. The render runs on the pump's thread, and the callback
    /// assigns to a bound property.</param>
    /// <param name="logger">Optional; tests pass none.</param>
    public ThumbnailWorker(
        Func<string, CancellationToken, string?> ensureFrame,
        Func<string, CancellationToken, string?> ensurePreview,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(ensureFrame);
        ArgumentNullException.ThrowIfNull(ensurePreview);

        _ensureFrame = ensureFrame;
        _ensurePreview = ensurePreview;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        // The in-flight bound is the number of pumps and nothing else: a second semaphore
        // gating renders would be two mechanisms enforcing one number, which is how they drift.
        _pumps = new Task[MaxConcurrency];
        for (var i = 0; i < MaxConcurrency; i++)
        {
            _pumps[i] = Task.Run(PumpAsync);
        }
    }

    /// <summary>Pending plus running, for the shutdown drain and for tests.</summary>
    public int OutstandingCount
    {
        get
        {
            lock (_gate)
            {
                return _live.Count;
            }
        }
    }

    /// <summary>
    /// Requests a frame thumbnail. <paramref name="completed"/> is invoked through the post
    /// delegate with the cache-relative path, or with null when the frame could not be rendered
    /// (spec 6.2.6) or the request was cancelled. Returns an <see cref="IDisposable"/> whose
    /// disposal withdraws the request: a slot loading a different frame disposes its handle and
    /// the worker drops the request if it has not started.
    /// </summary>
    public IDisposable RequestFrame(string framePath, Action<string?> completed)
        => Enqueue(preview: false, framePath, completed);

    /// <summary>As <see cref="RequestFrame"/>, for spec 11.5's preview at
    /// <c>general.preview_resolution</c>.</summary>
    public IDisposable RequestPreview(string framePath, Action<string?> completed)
        => Enqueue(preview: true, framePath, completed);

    /// <summary>Completes when nothing is pending or running, or when the timeout elapses.
    /// Mirrors <c>ScanCoordinator.WaitForIdleAsync</c>.</summary>
    public async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (OutstandingCount == 0)
            {
                return true;
            }
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            // ponytail: a 15 ms poll, not a completion signal, for the reason
            // ScanCoordinator.WaitForIdleAsync has one. Ceiling: shutdown checks this once with a
            // five second budget, so the granularity is irrelevant; swap in a
            // TaskCompletionSource if a caller ever needs to react in microseconds.
            await Task.Delay(15).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels every pending and running render and completes every outstanding callback
    /// with null, without waiting for the pumps to leave. Idempotent, and safe to call before
    /// <see cref="Dispose"/>: the shutdown drain starts the worker unwinding, spends spec 10.5's
    /// budget on the scan, and only then joins what is left of it.</summary>
    public void Cancel()
    {
        List<Action<string?>> orphaned = [];
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var request in _live.Values)
            {
                orphaned.AddRange(request.Callbacks);
                request.Callbacks.Clear();
                request.Finished = true;
            }
            _live.Clear();
            _stack.Clear();
        }

        _cancellation.Cancel();

        // Nothing outstanding is rendered now, so every caller waiting on a bitmap is told so
        // rather than left with a spinner while the process exits.
        foreach (var callback in orphaned)
        {
            _post(() => callback(null));
        }
    }

    /// <summary>Cancels every pending and running render, completes every outstanding callback
    /// with null, and waits for both pumps inside <see cref="DisposeDrainTimeout"/>.
    /// Idempotent.</summary>
    public void Dispose() => DisposeWithin(DisposeDrainTimeout);

    /// <summary>
    /// <see cref="Dispose"/> with the caller's join budget rather than this type's. Spec 10.5
    /// gives the whole shutdown five seconds, not five per subsystem, so
    /// <c>App.DrainForShutdown</c> passes whatever the scan drain left. A budget of
    /// <see cref="TimeSpan.Zero"/> cancels and returns without waiting at all.
    /// </summary>
    /// <returns>Whether both pumps left the loop inside the budget.</returns>
    internal bool DisposeWithin(TimeSpan joinBudget)
    {
        Cancel();

        if (Task.WaitAll(_pumps, joinBudget < TimeSpan.Zero ? TimeSpan.Zero : joinBudget))
        {
            // Only once both pumps are provably out of the loop: the token is handed to the
            // render delegates, and disposing it under a render in flight would throw there.
            _cancellation.Dispose();
            return true;
        }

        _logger.LogWarning(
            "Thumbnail worker join budget of {Seconds}s elapsed with a render still running; exiting anyway",
            joinBudget.TotalSeconds);
        return false;
    }

    private IDisposable Enqueue(bool preview, string framePath, Action<string?> completed)
    {
        ArgumentNullException.ThrowIfNull(framePath);
        ArgumentNullException.ThrowIfNull(completed);

        var key = (preview ? "preview|" : "frame|") + framePath;
        lock (_gate)
        {
            if (_disposed)
            {
                // Never leave a caller waiting on a callback that can no longer arrive.
                _post(() => completed(null));
                return Subscription.Withdrawn;
            }

            if (!_live.TryGetValue(key, out var request))
            {
                request = new Request(key, framePath, preview);
                _live[key] = request;
            }
            request.Callbacks.Add(completed);

            if (!request.Started)
            {
                // Pushed even when it is already in the stack: this is the most recent request
                // for it, and it should be served as such. The stale entry left behind is
                // skipped when it surfaces, which is cheaper than removing from a stack's middle.
                _stack.Push(request);
                _pending.Release();
            }
            return new Subscription(this, request, completed);
        }
    }

    private async Task PumpAsync()
    {
        var token = _cancellation.Token;
        while (true)
        {
            try
            {
                await _pending.WaitAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            Request? request = null;
            lock (_gate)
            {
                while (_stack.Count > 0)
                {
                    var candidate = _stack.Pop();
                    if (candidate.Started || candidate.Finished || candidate.Callbacks.Count == 0)
                    {
                        // A duplicate push, or a request every caller has withdrawn.
                        continue;
                    }
                    candidate.Started = true;
                    request = candidate;
                    break;
                }
            }

            if (request is null)
            {
                continue;
            }

            string? result = null;
            try
            {
                result = request.Preview
                    ? _ensurePreview(request.FramePath, token)
                    : _ensureFrame(request.FramePath, token);
            }
            catch (OperationCanceledException)
            {
                // Shutdown. The callback below still fires, with null.
            }
            catch (Exception ex)
            {
                // One bad frame must not take down a pump: a dead pump would silently halve the
                // throughput, and a second one would halve it to nothing.
                _logger.LogWarning(ex, "Thumbnail render failed for {Frame}", request.FramePath);
            }

            Action<string?>[] callbacks;
            lock (_gate)
            {
                request.Finished = true;
                if (_live.TryGetValue(request.Key, out var live) && ReferenceEquals(live, request))
                {
                    _live.Remove(request.Key);
                }
                callbacks = [.. request.Callbacks];
                request.Callbacks.Clear();
            }

            // Outside the lock: the callback assigns a bound property and may run inline under a
            // test's post delegate.
            foreach (var callback in callbacks)
            {
                _post(() => callback(result));
            }
        }
    }

    private void Withdraw(Request request, Action<string?> callback)
    {
        lock (_gate)
        {
            if (request.Finished)
            {
                return;
            }
            request.Callbacks.Remove(callback);
            if (request.Callbacks.Count > 0 || request.Started)
            {
                // Still wanted by another caller, or already rendering: a render in flight runs
                // to completion either way, because the cache has the file open.
                return;
            }
            if (_live.TryGetValue(request.Key, out var live) && ReferenceEquals(live, request))
            {
                _live.Remove(request.Key);
            }
        }
    }

    private sealed class Request(string key, string framePath, bool preview)
    {
        public string Key { get; } = key;

        public string FramePath { get; } = framePath;

        public bool Preview { get; } = preview;

        /// <summary>Every caller that asked for this render. Mutated only under the worker's
        /// lock.</summary>
        public List<Action<string?>> Callbacks { get; } = [];

        public bool Started { get; set; }

        public bool Finished { get; set; }
    }

    private sealed class Subscription(ThumbnailWorker worker, Request request, Action<string?> callback)
        : IDisposable
    {
        /// <summary>The handle handed back for a request that was never queued, so a caller can
        /// dispose it without a null check.</summary>
        public static IDisposable Withdrawn { get; } = new NoOp();

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            worker.Withdraw(request, callback);
        }

        private sealed class NoOp : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
