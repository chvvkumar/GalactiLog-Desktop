using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Spec 11.4's "a missing thumbnail renders a placeholder that is replaced when generation
// completes", and spec 11.5's spinner while generating.
//
// Bitmaps are constructed through the headless render interface, which is why every test that
// holds one is [AvaloniaFact]: Avalonia.Media.Imaging.Bitmap needs a platform. The decode is a
// delegate, so no test here reads a JPEG off disk.
public class ThumbnailSlotViewModelTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private const string FramePath = "frame.fits";

    // ------------------------------------------------------ the roadmap's third Verify clause

    [AvaloniaFact]
    public async Task Slot_PlaceholderIsReplacedOnCompletion()
    {
        using var harness = new Harness();
        Assert.True(harness.Slot.ShowsPlaceholder);
        Assert.Null(harness.Slot.Image);

        harness.Slot.Load();
        harness.Probe.Complete(FramePath);
        await harness.SettleAsync();

        Assert.False(harness.Slot.ShowsPlaceholder);
        Assert.NotNull(harness.Slot.Image);
    }

    // ------------------------------------------------------------------------------ loading

    [Fact]
    public void Load_SetsIsLoadingSynchronously()
    {
        using var harness = new Harness();
        Assert.False(harness.Slot.IsLoading);

        harness.Slot.Load();

        // Spec 11.5's spinner appears on the frame the request is made, not when the render
        // eventually starts on a pump thread.
        Assert.True(harness.Slot.IsLoading);
    }

    // Phase 8 phase review finding 10: spec 12.10's three states are exclusive, and the detail
    // page centres the placeholder and the spinner in the same Panel, so a placeholder that stays
    // visible while a load is in flight renders underneath the spinner.
    [Fact]
    public void Load_WhileTheRenderIsInFlight_HidesThePlaceholder()
    {
        using var harness = new Harness();

        harness.Slot.Load();

        Assert.True(harness.Slot.IsLoading);
        Assert.False(harness.Slot.ShowsPlaceholder);
    }

    [AvaloniaFact]
    public async Task Load_ClearsIsLoadingOnCompletion()
    {
        using var harness = new Harness();
        harness.Slot.Load();
        harness.Probe.Complete(FramePath);
        await harness.SettleAsync();

        Assert.False(harness.Slot.IsLoading);
    }

    [AvaloniaFact]
    public async Task Load_NullResult_LeavesThePlaceholderShowing()
    {
        using var harness = new Harness();
        harness.Slot.Load();

        // Spec 6.2.6: a frame whose pixels cannot be read has no thumbnail and never gets one.
        harness.Probe.CompleteWithNull(FramePath);
        await harness.SettleAsync();

        Assert.True(harness.Slot.ShowsPlaceholder);
        Assert.Null(harness.Slot.Image);
        Assert.False(harness.Slot.IsLoading);
        Assert.Equal(0, harness.DecodeCount);
    }

    [AvaloniaFact]
    public async Task Load_DecodesOffTheUiThread()
    {
        Assert.True(Dispatcher.UIThread.CheckAccess(), "the test itself must run on the UI thread");

        using var harness = new Harness();
        harness.Slot.Load();
        harness.Probe.Complete(FramePath);

        // Awaited, never blocked on: a blocking wait from the headless UI thread (itself a pool
        // thread) lets the pool inline the decode onto the very thread this asserts it is not on
        // (TRACKING section 2 item 8).
        await harness.SettleAsync();

        Assert.NotEmpty(harness.DecodeOnUiThread);
        Assert.All(harness.DecodeOnUiThread, Assert.False);
    }

    [AvaloniaFact]
    public async Task Load_SecondCall_WithdrawsTheFirstRequest()
    {
        using var harness = new Harness();
        harness.Slot.Load();
        await harness.Probe.WaitForStartsAsync(1, Budget);

        harness.Slot.Load();
        harness.Probe.Complete(FramePath);
        await harness.SettleAsync();

        // The worker posts once per live callback, and nothing but this slot requests anything
        // here. Two Loads that both stayed registered would post twice for the one deduplicated
        // render; withdrawing the first is what makes it one.
        Assert.Equal(1, harness.WorkerPosts);
        Assert.NotNull(harness.Slot.Image);
    }

    [AvaloniaFact]
    public async Task Load_ReplacingTheImage_DisposesThePrevious()
    {
        using var harness = new Harness();
        harness.Slot.Load();
        harness.Probe.Complete(FramePath);
        await harness.SettleAsync();
        var first = Assert.IsType<TrackingBitmap>(harness.Slot.Image);
        Assert.False(first.IsDisposed);

        harness.Slot.Load();
        await harness.SettleAsync();

        // A modal stepping through five hundred frames otherwise holds five hundred decoded
        // bitmaps until a collection that never comes.
        Assert.True(first.IsDisposed);
        Assert.NotSame(first, harness.Slot.Image);
        Assert.False(Assert.IsType<TrackingBitmap>(harness.Slot.Image).IsDisposed);
    }

    // ------------------------------------------------------------------------------ disposal

    [AvaloniaFact]
    public async Task Dispose_WithdrawsTheInFlightRequest()
    {
        using var harness = new Harness();

        // Both pumps are occupied, so the slot's request is still queued when it is withdrawn.
        using var gate1 = harness.Worker.RequestFrame("gate1", _ => { });
        using var gate2 = harness.Worker.RequestFrame("gate2", _ => { });
        await harness.Probe.WaitForStartsAsync(2, Budget);

        harness.Slot.Load();
        harness.Slot.Dispose();

        harness.Probe.CompleteEverything();
        Assert.True(await harness.Worker.WaitForIdleAsync(Budget));

        Assert.DoesNotContain(harness.Probe.Started, entry => entry.Path == FramePath);
    }

    [AvaloniaFact]
    public async Task Dispose_DisposesTheBitmap()
    {
        using var harness = new Harness();
        harness.Slot.Load();
        harness.Probe.Complete(FramePath);
        await harness.SettleAsync();
        var bitmap = Assert.IsType<TrackingBitmap>(harness.Slot.Image);

        harness.Slot.Dispose();

        Assert.True(bitmap.IsDisposed);
    }

    [AvaloniaFact]
    public async Task Dispose_CompletionArrivingAfterwards_IsDropped()
    {
        // Posts are held rather than run, which is the real race: the pump snapshots the
        // callbacks and posts them, and the slot is disposed before the dispatcher gets to them.
        using var harness = new Harness(deferPosts: true);
        harness.Slot.Load();
        harness.Probe.Complete(FramePath);
        Assert.True(await harness.Worker.WaitForIdleAsync(Budget));
        await harness.WaitForDeferredAsync(1, Budget);

        harness.Slot.Dispose();
        harness.RunDeferred();

        Assert.Null(harness.Slot.Image);
        Assert.Equal(0, harness.DecodeCount);
    }

    // -------------------------------------------------------------------------------- spec 14

    [Fact]
    public void Slot_HoldsNoBrush()
    {
        // Spec 14 and 14.5, stated as a test because this is Phase 8's first view-model: a
        // view-model that holds a brush holds an ImmutableSolidColorBrush, never a mutable one
        // taken from a theme dictionary.
        var offenders = typeof(ThumbnailSlotViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(property => typeof(IBrush).IsAssignableFrom(property.PropertyType))
            .Where(property => property.PropertyType != typeof(ImmutableSolidColorBrush))
            .Select(property => $"{property.Name}: {property.PropertyType.Name}")
            .ToArray();

        Assert.Empty(offenders);
    }

    // ------------------------------------------------------------------------------- harness

    private sealed class Harness : IDisposable
    {
        private readonly ConcurrentQueue<bool> _decodeOnUiThread = new();
        private readonly ConcurrentQueue<Action> _deferred = new();
        private readonly SemaphoreSlim _deferredSignal = new(0);
        private readonly bool _deferPosts;
        private int _decodeCount;
        private int _workerPosts;

        public Harness(bool deferPosts = false)
        {
            _deferPosts = deferPosts;
            Worker = new ThumbnailWorker(Probe.Frame, Probe.Preview, PostFromWorker);
            Slot = new ThumbnailSlotViewModel(
                FramePath,
                Worker,
                _ => [0x01],
                decode: _ =>
                {
                    Interlocked.Increment(ref _decodeCount);
                    _decodeOnUiThread.Enqueue(Dispatcher.UIThread.CheckAccess());
                    return new TrackingBitmap();
                },
                post: action => action());
        }

        public RenderProbe Probe { get; } = new();

        public ThumbnailWorker Worker { get; }

        public ThumbnailSlotViewModel Slot { get; }

        public int DecodeCount => Volatile.Read(ref _decodeCount);

        public int WorkerPosts => Volatile.Read(ref _workerPosts);

        public IReadOnlyCollection<bool> DecodeOnUiThread => _decodeOnUiThread;

        /// <summary>Awaits the whole request-render-decode-assign chain. No test sleeps, and no
        /// test blocks on a task queued from the UI thread.</summary>
        public async Task SettleAsync()
        {
            Assert.NotNull(Slot.PendingLoad);
            await Slot.PendingLoad!.WaitAsync(Budget);
        }

        public async Task WaitForDeferredAsync(int count, TimeSpan timeout)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.True(await _deferredSignal.WaitAsync(timeout), "a post never arrived");
            }
        }

        public void RunDeferred()
        {
            while (_deferred.TryDequeue(out var action))
            {
                action();
            }
        }

        public void Dispose()
        {
            Slot.Dispose();
            Worker.Dispose();
        }

        private void PostFromWorker(Action action)
        {
            Interlocked.Increment(ref _workerPosts);
            if (_deferPosts)
            {
                _deferred.Enqueue(action);
                _deferredSignal.Release();
                return;
            }
            action();
        }
    }

    // A bitmap that records its own disposal. Avalonia's Bitmap is not sealed and its Dispose is
    // virtual, so the slot's ownership rule can be asserted without reading a JPEG: the headless
    // render interface returns a one pixel stub for any bytes at all.
    private sealed class TrackingBitmap : Bitmap
    {
        public TrackingBitmap()
            : base(new MemoryStream([0x01, 0x02, 0x03, 0x04]))
        {
        }

        public bool IsDisposed { get; private set; }

        public override void Dispose()
        {
            IsDisposed = true;
            base.Dispose();
        }
    }

    // The same shape as ThumbnailWorkerTests.RenderProbe, trimmed to what these tests need: a
    // per-path gate so a request can be held pending while the slot is driven.
    private sealed class RenderProbe
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, TaskCompletionSource<string?>> _gates = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _startSignal = new(0);
        private bool _auto;

        public ConcurrentQueue<(bool Preview, string Path)> Started { get; } = new();

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

        public async Task WaitForStartsAsync(int additional, TimeSpan timeout)
        {
            for (var i = 0; i < additional; i++)
            {
                Assert.True(await _startSignal.WaitAsync(timeout), "a render did not start in time");
            }
        }

        private string? Render(bool preview, string path, CancellationToken ct)
        {
            TaskCompletionSource<string?> gate;
            lock (_gate)
            {
                Started.Enqueue((preview, path));
                _startSignal.Release();
                gate = GateLocked(path);
                if (_auto)
                {
                    gate.TrySetResult($"frames/{path}.jpg");
                }
            }
            return gate.Task.WaitAsync(ct).GetAwaiter().GetResult();
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
}
