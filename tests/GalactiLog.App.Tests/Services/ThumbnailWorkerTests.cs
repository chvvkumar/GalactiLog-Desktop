using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Services;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Spec 11.4 and 10.6, roadmap Phase 8 row 5: the queue serves the most recently requested item
// first, concurrency never exceeds 2, and a request that is already queued or already running is
// folded into the existing one rather than duplicated.
//
// No test here decodes an image or touches ThumbnailCache: the two render delegates are a probe
// whose completion is controlled per path, so the ordering and concurrency assertions are
// deterministic rather than timing-dependent. Every test that asserts ordering, concurrency or
// off-thread work AWAITS the work and never blocks on it (TRACKING section 2 item 8, xUnit1031).
public class ThumbnailWorkerTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static ThumbnailWorker Create(RenderProbe probe, Action<Action>? post = null)
        => new(probe.Frame, probe.Preview, post ?? (action => action()));

    // ---------------------------------------------------------------- the roadmap's Verify line

    [Fact]
    public async Task Worker_ServesTheMostRecentlyRequestedItemFirst()
    {
        var probe = new RenderProbe();
        using var worker = Create(probe);
        var done = new Completions();

        // Both pumps are occupied, so A, B and C all sit in the queue and the order they are
        // taken out in is the thing under test.
        using var first = worker.RequestFrame("gate1", done.For("gate1"));
        using var second = worker.RequestFrame("gate2", done.For("gate2"));
        await probe.WaitForStartsAsync(2, Budget);

        using var a = worker.RequestFrame("A", done.For("A"));
        using var b = worker.RequestFrame("B", done.For("B"));
        using var c = worker.RequestFrame("C", done.For("C"));

        // One pump at a time, so the service order is observed rather than inferred: the newest
        // request goes first and the oldest goes last.
        probe.Complete("gate1");
        await probe.WaitForStartsAsync(1, Budget);
        Assert.Equal("C", probe.Started.ToArray()[2].Path);

        probe.Complete("gate2");
        await probe.WaitForStartsAsync(1, Budget);
        Assert.Equal("B", probe.Started.ToArray()[3].Path);

        probe.Complete("C");
        await probe.WaitForStartsAsync(1, Budget);
        Assert.Equal("A", probe.Started.ToArray()[4].Path);

        probe.CompleteEverything();
        await done.WaitAsync(5, Budget);
    }

    [Fact]
    public async Task Worker_ConcurrencyNeverExceedsTwo()
    {
        var probe = new RenderProbe();
        using var worker = Create(probe);
        var done = new Completions();

        var handles = new List<IDisposable>();
        for (var i = 0; i < 20; i++)
        {
            handles.Add(worker.RequestFrame($"frame{i:D2}", done.For($"frame{i:D2}")));
        }

        // Reached 2: two renders are inside the delegate at once, both blocked.
        await probe.WaitForStartsAsync(2, Budget);
        Assert.Equal(2, probe.MaxConcurrent);

        // Never exceeded 2: the remaining eighteen run as fast as the pumps can take them.
        probe.CompleteEverything();
        await done.WaitAsync(20, Budget);
        Assert.Equal(20, probe.Started.Count);
        Assert.Equal(ThumbnailWorker.MaxConcurrency, probe.MaxConcurrent);

        foreach (var handle in handles)
        {
            handle.Dispose();
        }
    }

    // ------------------------------------------------------------------------- queue behaviour

    [Fact]
    public async Task Worker_SecondRequestForTheSamePath_DoesNotRenderTwice()
    {
        var probe = new RenderProbe();
        using var worker = Create(probe);
        var done = new Completions();
        using var g1 = worker.RequestFrame("gate1", done.For("gate1"));
        using var g2 = worker.RequestFrame("gate2", done.For("gate2"));
        await probe.WaitForStartsAsync(2, Budget);

        using var a1 = worker.RequestFrame("A", done.For("A-1"));
        using var a2 = worker.RequestFrame("A", done.For("A-2"));

        probe.CompleteEverything();
        await done.WaitAsync(4, Budget);
        Assert.True(await worker.WaitForIdleAsync(Budget));

        Assert.Single(probe.Started, entry => entry.Path == "A");
    }

    [Fact]
    public async Task Worker_SecondRequestForTheSamePath_InvokesBothCallbacks()
    {
        var probe = new RenderProbe();
        using var worker = Create(probe);
        var done = new Completions();
        using var g1 = worker.RequestFrame("gate1", done.For("gate1"));
        using var g2 = worker.RequestFrame("gate2", done.For("gate2"));
        await probe.WaitForStartsAsync(2, Budget);

        using var a1 = worker.RequestFrame("A", done.For("A-1"));
        using var a2 = worker.RequestFrame("A", done.For("A-2"));

        probe.CompleteEverything();
        await done.WaitAsync(4, Budget);

        var results = done.Items.ToArray();
        Assert.Contains(results, item => item.Tag == "A-1" && item.Result == "frames/A.jpg");
        Assert.Contains(results, item => item.Tag == "A-2" && item.Result == "frames/A.jpg");
    }

    [Fact]
    public async Task Worker_SecondRequestForTheSamePath_MovesItToTheFrontOfTheQueue()
    {
        var probe = new RenderProbe();
        using var worker = Create(probe);
        var done = new Completions();
        using var g1 = worker.RequestFrame("gate1", done.For("gate1"));
        using var g2 = worker.RequestFrame("gate2", done.For("gate2"));
        await probe.WaitForStartsAsync(2, Budget);

        using var a1 = worker.RequestFrame("A", done.For("A-1"));
        using var b = worker.RequestFrame("B", done.For("B"));
        using var a2 = worker.RequestFrame("A", done.For("A-2"));

        probe.Complete("gate1");
        await probe.WaitForStartsAsync(1, Budget);

        // A was queued first and B second; re-requesting A makes it the most recent request,
        // so it is served ahead of B.
        Assert.Equal("A", probe.Started.ToArray()[2].Path);

        probe.CompleteEverything();
        await done.WaitAsync(5, Budget);
    }

    [Fact]
    public async Task Worker_FrameAndPreviewForTheSamePath_AreSeparateRequests()
    {
        var probe = new RenderProbe();
        using var worker = Create(probe);
        var done = new Completions();
        using var g1 = worker.RequestFrame("gate1", done.For("gate1"));
        using var g2 = worker.RequestFrame("gate2", done.For("gate2"));
        await probe.WaitForStartsAsync(2, Budget);

        using var frame = worker.RequestFrame("A", done.For("frame"));
        using var preview = worker.RequestPreview("A", done.For("preview"));

        probe.CompleteEverything();
        await done.WaitAsync(4, Budget);
        Assert.True(await worker.WaitForIdleAsync(Budget));

        var started = probe.Started.ToArray();
        Assert.Contains(started, entry => entry is { Preview: false, Path: "A" });
        Assert.Contains(started, entry => entry is { Preview: true, Path: "A" });
    }

    [Fact]
    public async Task Worker_WithdrawnRequest_IsNeverRendered()
    {
        var probe = new RenderProbe();
        using var worker = Create(probe);
        var done = new Completions();
        using var g1 = worker.RequestFrame("gate1", done.For("gate1"));
        using var g2 = worker.RequestFrame("gate2", done.For("gate2"));
        await probe.WaitForStartsAsync(2, Budget);

        var withdrawn = worker.RequestFrame("A", done.For("A"));
        withdrawn.Dispose();

        probe.CompleteEverything();
        await done.WaitAsync(2, Budget);
        Assert.True(await worker.WaitForIdleAsync(Budget));

        Assert.DoesNotContain(probe.Started, entry => entry.Path == "A");
    }

    [Fact]
    public async Task Worker_WithdrawnRequest_DoesNotInvokeItsCallback()
    {
        var probe = new RenderProbe();
        using var worker = Create(probe);
        var done = new Completions();
        using var g1 = worker.RequestFrame("gate1", done.For("gate1"));
        using var g2 = worker.RequestFrame("gate2", done.For("gate2"));
        await probe.WaitForStartsAsync(2, Budget);

        var withdrawn = worker.RequestFrame("A", done.For("A"));
        withdrawn.Dispose();

        probe.CompleteEverything();
        await done.WaitAsync(2, Budget);
        Assert.True(await worker.WaitForIdleAsync(Budget));

        Assert.DoesNotContain(done.Items, item => item.Tag == "A");
    }

    [Fact]
    public async Task Worker_WithdrawingOneOfTwoCallbacks_StillRendersForTheOther()
    {
        var probe = new RenderProbe();
        using var worker = Create(probe);
        var done = new Completions();
        using var g1 = worker.RequestFrame("gate1", done.For("gate1"));
        using var g2 = worker.RequestFrame("gate2", done.For("gate2"));
        await probe.WaitForStartsAsync(2, Budget);

        var first = worker.RequestFrame("A", done.For("A-1"));
        using var secondHandle = worker.RequestFrame("A", done.For("A-2"));
        first.Dispose();

        probe.CompleteEverything();
        await done.WaitAsync(3, Budget);
        Assert.True(await worker.WaitForIdleAsync(Budget));

        Assert.Single(probe.Started, entry => entry.Path == "A");
        Assert.Contains(done.Items, item => item.Tag == "A-2");
        Assert.DoesNotContain(done.Items, item => item.Tag == "A-1");
    }

    [Fact]
    public async Task Worker_RequestWithdrawnWhileRunning_StillCompletesTheOtherCallback()
    {
        var probe = new RenderProbe();
        using var worker = Create(probe);
        var done = new Completions();

        var first = worker.RequestFrame("A", done.For("A-1"));
        await probe.WaitForStartsAsync(1, Budget);
        using var secondHandle = worker.RequestFrame("A", done.For("A-2"));
        first.Dispose();

        probe.Complete("A");
        await done.WaitAsync(1, Budget);
        Assert.True(await worker.WaitForIdleAsync(Budget));

        Assert.Contains(done.Items, item => item.Tag == "A-2" && item.Result == "frames/A.jpg");
        Assert.DoesNotContain(done.Items, item => item.Tag == "A-1");
    }

    [Fact]
    public async Task Worker_OutstandingCount_CountsPendingAndRunning()
    {
        var probe = new RenderProbe();
        using var worker = Create(probe);
        var done = new Completions();
        Assert.Equal(0, worker.OutstandingCount);

        using var g1 = worker.RequestFrame("gate1", done.For("gate1"));
        using var g2 = worker.RequestFrame("gate2", done.For("gate2"));
        await probe.WaitForStartsAsync(2, Budget);

        using var a = worker.RequestFrame("A", done.For("A"));
        using var b = worker.RequestFrame("B", done.For("B"));
        using var c = worker.RequestFrame("C", done.For("C"));

        Assert.Equal(5, worker.OutstandingCount);

        probe.CompleteEverything();
        await done.WaitAsync(5, Budget);
        Assert.True(await worker.WaitForIdleAsync(Budget));
        Assert.Equal(0, worker.OutstandingCount);
    }

    // -------------------------------------------------------------- failure and cancellation

    [Fact]
    public async Task Worker_RenderReturnsNull_CompletesWithNull()
    {
        var probe = new RenderProbe();
        using var worker = Create(probe);
        var done = new Completions();

        using var a = worker.RequestFrame("A", done.For("A"));
        await probe.WaitForStartsAsync(1, Budget);
        probe.CompleteWithNull("A");

        await done.WaitAsync(1, Budget);
        Assert.Equal(("A", (string?)null), done.Items.Single());
    }

    [Fact]
    public async Task Worker_RenderThrows_CompletesWithNullAndKeepsThePumpAlive()
    {
        var probe = new RenderProbe();
        probe.ThrowFor("bad");
        using var worker = Create(probe);
        var done = new Completions();

        using var bad = worker.RequestFrame("bad", done.For("bad"));
        await done.WaitAsync(1, Budget);
        Assert.Equal(("bad", (string?)null), done.Items.Single());

        // The regression this guards: a pump that died on the exception would never serve this.
        probe.CompleteEverything();
        using var good = worker.RequestFrame("good", done.For("good"));
        await done.WaitAsync(1, Budget);
        Assert.Contains(done.Items, item => item.Tag == "good" && item.Result == "frames/good.jpg");
    }

    [Fact]
    public async Task Worker_RenderThrowsRepeatedly_BothPumpsSurvive()
    {
        var probe = new RenderProbe();
        for (var i = 0; i < 8; i++)
        {
            probe.ThrowFor($"bad{i}");
        }
        using var worker = Create(probe);
        var done = new Completions();

        var handles = new List<IDisposable>();
        for (var i = 0; i < 8; i++)
        {
            handles.Add(worker.RequestFrame($"bad{i}", done.For($"bad{i}")));
        }
        await done.WaitAsync(8, Budget);
        await probe.WaitForStartsAsync(8, Budget);
        Assert.All(done.Items, item => Assert.Null(item.Result));

        // Two gated renders occupying two pumps at once is the proof that neither pump died on
        // the eight exceptions: one survivor would serve these one after the other.
        using var goodA = worker.RequestFrame("goodA", done.For("goodA"));
        using var goodB = worker.RequestFrame("goodB", done.For("goodB"));
        await probe.WaitForStartsAsync(2, Budget);
        Assert.Equal(ThumbnailWorker.MaxConcurrency, probe.MaxConcurrent);

        probe.CompleteEverything();
        await done.WaitAsync(2, Budget);
        Assert.Contains(done.Items, item => item.Tag == "goodA" && item.Result is not null);
        Assert.Contains(done.Items, item => item.Tag == "goodB" && item.Result is not null);

        foreach (var handle in handles)
        {
            handle.Dispose();
        }
    }

    [Fact]
    public async Task Worker_Disposed_CompletesEveryOutstandingCallbackWithNull()
    {
        var probe = new RenderProbe();
        var worker = Create(probe);
        var done = new Completions();

        using var g1 = worker.RequestFrame("gate1", done.For("gate1"));
        using var g2 = worker.RequestFrame("gate2", done.For("gate2"));
        await probe.WaitForStartsAsync(2, Budget);
        using var a = worker.RequestFrame("A", done.For("A"));
        using var b = worker.RequestFrame("B", done.For("B"));

        worker.Dispose();

        await done.WaitAsync(4, Budget);
        Assert.All(done.Items, item => Assert.Null(item.Result));
        Assert.Equal(0, worker.OutstandingCount);
    }

    [Fact]
    public async Task Worker_Disposed_IsIdempotent()
    {
        var probe = new RenderProbe();
        var worker = Create(probe);
        var done = new Completions();
        using var a = worker.RequestFrame("A", done.For("A"));
        await probe.WaitForStartsAsync(1, Budget);

        worker.Dispose();
        worker.Dispose();

        await done.WaitAsync(1, Budget);
        Assert.Null(done.Items.Single().Result);
    }

    // Spec 10.5's budget is five seconds for the whole shutdown, not five per subsystem, so the
    // cancel half and the join half are separable: App.DrainForShutdown starts the worker
    // unwinding, spends the budget on the scan, and joins the pumps with what is left.
    [Fact]
    public async Task Worker_Cancel_CompletesEveryOutstandingCallbackWithoutWaitingForTheRenders()
    {
        var probe = new RenderProbe();
        var worker = Create(probe);
        var done = new Completions();
        using var g1 = worker.RequestFrame("gate1", done.For("gate1"));
        using var g2 = worker.RequestFrame("gate2", done.For("gate2"));
        await probe.WaitForStartsAsync(2, Budget);
        using var a = worker.RequestFrame("A", done.For("A"));

        var clock = Stopwatch.StartNew();
        worker.Cancel();
        clock.Stop();

        // Both renders are still inside the delegate, and Cancel did not wait for either.
        Assert.True(
            clock.Elapsed < ThumbnailWorker.DisposeDrainTimeout,
            $"Cancel waited {clock.Elapsed.TotalSeconds:F1}s for a render still in flight");
        await done.WaitAsync(3, Budget);
        Assert.All(done.Items, item => Assert.Null(item.Result));
        Assert.Equal(0, worker.OutstandingCount);

        probe.CompleteEverything();
        worker.Dispose();
    }

    [Fact]
    public async Task Worker_DisposeWithin_ZeroBudget_DoesNotWaitForAStalledRender()
    {
        // A render that ignores its cancellation token, which is what a decode in the middle of
        // a frame looks like: the pump cannot leave until it returns.
        var stalled = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new SemaphoreSlim(0);
        var worker = new ThumbnailWorker(
            (_, _) =>
            {
                started.Release();
                return stalled.Task.GetAwaiter().GetResult();
            },
            (_, _) => null,
            action => action());
        var done = new Completions();
        using var a = worker.RequestFrame("A", done.For("A"));
        Assert.True(await started.WaitAsync(Budget));

        var clock = Stopwatch.StartNew();
        var joined = worker.DisposeWithin(TimeSpan.Zero);
        clock.Stop();

        Assert.False(joined, "the stalled pump cannot have left inside a zero budget");
        Assert.True(
            clock.Elapsed < ThumbnailWorker.DisposeDrainTimeout,
            $"a zero budget still waited {clock.Elapsed.TotalSeconds:F1}s");
        await done.WaitAsync(1, Budget);
        Assert.Null(done.Items.Single().Result);

        stalled.TrySetResult(null);
    }

    [Fact]
    public void Worker_RequestAfterDisposal_CompletesWithNullImmediately()
    {
        var probe = new RenderProbe(auto: true);
        var worker = Create(probe);
        worker.Dispose();

        var done = new Completions();
        using var handle = worker.RequestFrame("A", done.For("A"));

        // The post delegate is inline here, so "immediately" is literal: no pump is involved.
        Assert.Equal(("A", (string?)null), done.Items.Single());
        Assert.Empty(probe.Started);
    }

    [Fact]
    public async Task Worker_WaitForIdleAsync_CompletesWhenTheQueueDrains()
    {
        var probe = new RenderProbe(auto: true);
        using var worker = Create(probe);
        var done = new Completions();

        var handles = new List<IDisposable>();
        for (var i = 0; i < 5; i++)
        {
            handles.Add(worker.RequestFrame($"frame{i}", done.For($"frame{i}")));
        }

        Assert.True(await worker.WaitForIdleAsync(Budget));
        Assert.Equal(0, worker.OutstandingCount);
        await done.WaitAsync(5, Budget);

        foreach (var handle in handles)
        {
            handle.Dispose();
        }
    }

    [Fact]
    public async Task Worker_WaitForIdleAsync_TimesOutWhenARenderHangs()
    {
        var probe = new RenderProbe();
        using var worker = Create(probe);
        var done = new Completions();
        using var a = worker.RequestFrame("A", done.For("A"));
        await probe.WaitForStartsAsync(1, Budget);

        Assert.False(await worker.WaitForIdleAsync(TimeSpan.FromMilliseconds(50)));

        probe.CompleteEverything();
        await done.WaitAsync(1, Budget);
    }

    // ------------------------------------------------------------------------------- threading

    [AvaloniaFact]
    public async Task Worker_RendersOffTheUiThread()
    {
        var probe = new RenderProbe(auto: true) { TrackDispatcher = true };
        using var worker = Create(probe);
        var done = new Completions();

        using var a = worker.RequestFrame("A", done.For("A"));

        // Awaited, never blocked on: a blocking wait here would let the thread pool inline the
        // pump onto the very thread this test claims the render did not run on (TRACKING section
        // 2 item 8, the shape DashboardEmptyStateTests got wrong in Phase 6).
        await done.WaitAsync(1, Budget);

        Assert.False(probe.LastRenderOnUiThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, probe.LastRenderThreadId);
        Assert.True(Dispatcher.UIThread.CheckAccess(), "the test itself runs on the dispatcher");
    }

    [Fact]
    public async Task Worker_InvokesCallbacksThroughThePostDelegate()
    {
        var posted = new ConcurrentQueue<Action>();
        var postSignal = new SemaphoreSlim(0);
        var probe = new RenderProbe(auto: true);
        using var worker = Create(probe, action => { posted.Enqueue(action); postSignal.Release(); });
        var done = new Completions();

        using var a = worker.RequestFrame("A", done.For("A"));
        Assert.True(await postSignal.WaitAsync(Budget));

        // Nothing ran the callback but the post delegate, so nothing has completed yet.
        Assert.Empty(done.Items);
        Assert.True(posted.TryDequeue(out var action));
        action!();
        Assert.Equal(("A", "frames/A.jpg"), done.Items.Single());
    }

    // ------------------------------------------------------------------------------- harness

    // The two render delegates, with per-path completion. Blocking inside the delegate is what
    // ThumbnailCache.EnsureFrame does for real (it is synchronous and renders on the caller's
    // thread), and it happens on a pump thread, never on the test's.
    private sealed class RenderProbe(bool auto = false)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, TaskCompletionSource<string?>> _gates = new(StringComparer.Ordinal);
        private readonly HashSet<string> _throwing = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _startSignal = new(0);
        private bool _auto = auto;
        private int _concurrent;
        private int _maxConcurrent;

        public ConcurrentQueue<(bool Preview, string Path)> Started { get; } = new();

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public int LastRenderThreadId { get; private set; }

        public bool LastRenderOnUiThread { get; private set; }

        // Only the one test that runs under a headless Avalonia application reads the
        // dispatcher: Dispatcher.UIThread has no platform to bind to in a plain [Fact].
        public bool TrackDispatcher { get; init; }

        public string? Frame(string path, CancellationToken ct) => Render(preview: false, path, ct);

        public string? Preview(string path, CancellationToken ct) => Render(preview: true, path, ct);

        public void Complete(string path)
        {
            lock (_gate)
            {
                GateLocked(path).TrySetResult($"frames/{path}.jpg");
            }
        }

        public void CompleteWithNull(string path)
        {
            lock (_gate)
            {
                GateLocked(path).TrySetResult(null);
            }
        }

        // Releases everything in flight and everything that arrives later, so a test that is done
        // asserting order can let the rest of the queue run out.
        public void CompleteEverything()
        {
            lock (_gate)
            {
                _auto = true;
                foreach (var (path, gate) in _gates)
                {
                    gate.TrySetResult($"frames/{path}.jpg");
                }
            }
        }

        public void ThrowFor(string path)
        {
            lock (_gate)
            {
                _throwing.Add(path);
            }
        }

        public async Task WaitForStartsAsync(int additional, TimeSpan timeout)
        {
            for (var i = 0; i < additional; i++)
            {
                Assert.True(
                    await _startSignal.WaitAsync(timeout),
                    $"expected {additional} more renders to start; {Started.Count} have started in total");
            }
        }

        private string? Render(bool preview, string path, CancellationToken ct)
        {
            var entered = Interlocked.Increment(ref _concurrent);
            var observed = Volatile.Read(ref _maxConcurrent);
            while (entered > observed)
            {
                var prior = Interlocked.CompareExchange(ref _maxConcurrent, entered, observed);
                if (prior == observed)
                {
                    break;
                }
                observed = prior;
            }

            try
            {
                TaskCompletionSource<string?> gate;
                lock (_gate)
                {
                    LastRenderThreadId = Environment.CurrentManagedThreadId;
                    if (TrackDispatcher)
                    {
                        LastRenderOnUiThread = Dispatcher.UIThread.CheckAccess();
                    }
                    Started.Enqueue((preview, path));
                    _startSignal.Release();
                    if (_throwing.Contains(path))
                    {
                        throw new InvalidOperationException($"render failed for {path}");
                    }
                    gate = GateLocked(path);
                    if (_auto)
                    {
                        gate.TrySetResult($"frames/{path}.jpg");
                    }
                }
                return gate.Task.WaitAsync(ct).GetAwaiter().GetResult();
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        private TaskCompletionSource<string?> GateLocked(string path)
        {
            if (!_gates.TryGetValue(path, out var gate))
            {
                gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _gates[path] = gate;
            }
            return gate;
        }
    }

    private sealed class Completions
    {
        private readonly SemaphoreSlim _signal = new(0);

        public ConcurrentQueue<(string Tag, string? Result)> Items { get; } = new();

        public Action<string?> For(string tag) => result =>
        {
            Items.Enqueue((tag, result));
            _signal.Release();
        };

        public async Task WaitAsync(int count, TimeSpan timeout)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.True(
                    await _signal.WaitAsync(timeout),
                    $"expected {count} more completions; {Items.Count} have arrived in total");
            }
        }
    }
}
