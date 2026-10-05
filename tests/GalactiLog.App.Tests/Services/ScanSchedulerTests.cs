using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// SettingsFixture and FakeDelay are defined in WatcherServiceTests.cs (same namespace).
public class ScanSchedulerTests
{
    private const int IntervalMinutes = 7;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(IntervalMinutes);

    private sealed class Harness : IDisposable
    {
        public SettingsFixture Settings { get; }
        public FakeDelay Delay { get; } = new();
        public ScanScheduler Scheduler { get; }
        public bool ScanIsRunning { get; set; }
        public int Scans;

        public Harness(SettingsFixture settings)
        {
            Settings = settings;
            Scheduler = new ScanScheduler(
                settings.Store,
                () => ScanIsRunning,
                _ => { Interlocked.Increment(ref Scans); return Task.CompletedTask; },
                NullLogger<ScanScheduler>.Instance,
                Delay.Delay);
        }

        // Ends the loop and waits for it, so nothing from one test leaks into the next.
        public void Dispose()
        {
            Scheduler.Stop();
            Delay.Release();
            Scheduler.LoopTask?.Wait(TimeSpan.FromSeconds(30));
            Settings.Dispose();
        }
    }

    private static Harness CreateHarness(Func<GeneralSettings, GeneralSettings>? mutate = null)
    {
        var settings = new SettingsFixture();
        settings.Save(general =>
        {
            var configured = general with { AutoScanEnabled = true, AutoScanIntervalMinutes = IntervalMinutes };
            return mutate is null ? configured : mutate(configured);
        });
        return new Harness(settings);
    }

    [Fact]
    public async Task RunLoop_FiresScanAfterConfiguredInterval()
    {
        using var harness = CreateHarness();

        harness.Scheduler.Start();
        await harness.Delay.WaitForRequestCountAsync(1);
        Assert.Equal(Interval, harness.Delay.Requested[0]);

        harness.Delay.Release();
        // The next wait only opens after the scan call has returned, so seeing request #2 is
        // proof the scan ran rather than a race against it.
        await harness.Delay.WaitForRequestCountAsync(2);

        Assert.Equal(1, harness.Scans);
        Assert.Equal(Interval, harness.Delay.Requested[1]);
    }

    [Fact]
    public async Task RunLoop_SkippedWhileAScanIsAlreadyRunning()
    {
        using var harness = CreateHarness();
        harness.ScanIsRunning = true;

        harness.Scheduler.Start();
        await harness.Delay.WaitForRequestCountAsync(1);
        harness.Delay.Release();
        await harness.Delay.WaitForRequestCountAsync(2);

        Assert.Equal(0, harness.Scans);
    }

    [Fact]
    public async Task RunLoop_AutoScanDisabled_NeverFires()
    {
        using var harness = CreateHarness(general => general with { AutoScanEnabled = false });

        harness.Scheduler.Start();
        await harness.Delay.WaitForRequestCountAsync(1);
        harness.Delay.Release();
        await harness.Delay.WaitForRequestCountAsync(2);

        Assert.Equal(0, harness.Scans);
    }

    [Fact]
    public async Task ResetInterval_MidWait_PushesTheNextScanOutByAFullInterval()
    {
        using var harness = CreateHarness();

        harness.Scheduler.Start();
        await harness.Delay.WaitForRequestCountAsync(1);

        // What ScanCoordinator.ScanFinished is wired to in AppHost: a scan from any trigger
        // finished, so the current wait is abandoned and a whole fresh interval starts.
        harness.Scheduler.ResetInterval();
        await harness.Delay.WaitForRequestCountAsync(2);

        Assert.Equal(0, harness.Scans);
        Assert.Equal(Interval, harness.Delay.Requested[1]);

        // The restarted interval is a real one: letting it elapse still fires a scan.
        harness.Delay.Release();
        await harness.Delay.WaitForRequestCountAsync(3);
        Assert.Equal(1, harness.Scans);
    }

    [Fact]
    public async Task ResetInterval_WhenIdle_IsANoOp()
    {
        using var harness = CreateHarness();

        harness.Scheduler.ResetInterval();
        harness.Scheduler.Start();
        await harness.Delay.WaitForRequestCountAsync(1);

        Assert.Equal(0, harness.Scans);
    }

    [Fact]
    public async Task RunLoop_IntervalChangeTakesEffectOnTheNextWait()
    {
        using var harness = CreateHarness();

        harness.Scheduler.Start();
        await harness.Delay.WaitForRequestCountAsync(1);
        harness.Settings.Save(general => general with { AutoScanIntervalMinutes = 3 });
        harness.Delay.Release();
        await harness.Delay.WaitForRequestCountAsync(2);

        Assert.Equal(TimeSpan.FromMinutes(3), harness.Delay.Requested[1]);
    }

    [Fact]
    public async Task Stop_HaltsTheLoop_NoFurtherScansFire()
    {
        using var harness = CreateHarness();

        harness.Scheduler.Start();
        await harness.Delay.WaitForRequestCountAsync(1);
        harness.Scheduler.Stop();

        var loop = harness.Scheduler.LoopTask;
        Assert.NotNull(loop);
        await loop.WaitAsync(TimeSpan.FromSeconds(30));

        harness.Delay.Release();
        await Task.Delay(20);

        Assert.Equal(0, harness.Scans);
        Assert.Single(harness.Delay.Requested);
    }

    [Fact]
    public void Start_CalledTwice_RunsOneLoop()
    {
        using var harness = CreateHarness();

        harness.Scheduler.Start();
        var first = harness.Scheduler.LoopTask;
        harness.Scheduler.Start();

        Assert.Same(first, harness.Scheduler.LoopTask);
    }

    // ---- Phase 10 Task 1: spec 12.8's "next scheduled scan time" ------------------------------

    [Fact]
    public void NextScheduledUtc_IsNull_BeforeStart()
    {
        using var harness = CreateHarness();

        Assert.Null(harness.Scheduler.NextScheduledUtc);
    }

    [Fact]
    public async Task NextScheduledUtc_IsSetWhileTheLoopIsWaiting()
    {
        using var harness = CreateHarness();
        var before = DateTime.UtcNow;

        harness.Scheduler.Start();
        await harness.Delay.WaitForRequestCountAsync(1);

        var next = harness.Scheduler.NextScheduledUtc;

        Assert.NotNull(next);

        // Recorded when the wait started, so it sits one whole interval past that instant and is
        // never re-derived from the settings interval at read time.
        Assert.InRange(next.Value, before + Interval, DateTime.UtcNow + Interval);
    }

    [Fact]
    public async Task NextScheduledUtc_IsNull_WhileAScheduledScanIsRunning()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { AutoScanEnabled = true, AutoScanIntervalMinutes = IntervalMinutes });
        var delay = new FakeDelay();
        var insideScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The loop clears the field in the wait's finally, before it runs the scan, so nothing is
        // scheduled while a scheduled scan is in flight. The scan itself parks so the test can
        // read the property at that exact point.
        var scheduler = new ScanScheduler(
            settings.Store,
            () => false,
            async _ =>
            {
                insideScan.SetResult();
                await releaseScan.Task;
            },
            NullLogger<ScanScheduler>.Instance,
            delay.Delay);

        try
        {
            scheduler.Start();
            await delay.WaitForRequestCountAsync(1);
            delay.Release();
            await insideScan.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Null(scheduler.NextScheduledUtc);
        }
        finally
        {
            releaseScan.TrySetResult();
            scheduler.Stop();
            delay.Release();
            if (scheduler.LoopTask is { } loop)
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }

    [Fact]
    public async Task NextScheduledUtc_MovesForward_AfterResetInterval()
    {
        using var harness = CreateHarness();

        harness.Scheduler.Start();
        await harness.Delay.WaitForRequestCountAsync(1);
        var first = harness.Scheduler.NextScheduledUtc;
        Assert.NotNull(first);

        // ScanFinished is wired to this in AppHost: a scan from any trigger finished, so the wait
        // restarts and the reported time moves out by a whole fresh interval.
        await Task.Delay(20);
        harness.Scheduler.ResetInterval();
        await harness.Delay.WaitForRequestCountAsync(2);

        var second = harness.Scheduler.NextScheduledUtc;

        Assert.NotNull(second);
        Assert.True(
            second.Value > first.Value,
            $"Expected the next scheduled time to move forward, saw {first} then {second}.");
    }

    [Fact]
    public async Task NextScheduledUtc_IsNull_AfterStop()
    {
        using var harness = CreateHarness();

        harness.Scheduler.Start();
        await harness.Delay.WaitForRequestCountAsync(1);
        Assert.NotNull(harness.Scheduler.NextScheduledUtc);

        harness.Scheduler.Stop();
        var loop = harness.Scheduler.LoopTask;
        Assert.NotNull(loop);
        await loop.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(harness.Scheduler.NextScheduledUtc);
    }

    /// <summary>
    /// Task 4 review M2, fixer list code item 6: the same case exists for <c>UpdateService</c>'s
    /// identical wait. A delay seam that fails any way other than cancellation used to end the
    /// loop and fault <c>LoopTask</c> with nobody observing it, which is a
    /// <c>TaskScheduler.UnobservedTaskException</c> at the next collection. It now ends the loop
    /// cleanly and says so. Only an injected seam reaches this: in production <c>_delay</c> is
    /// <c>Task.Delay</c> over an interval read from settings.
    /// </summary>
    [Fact]
    public async Task RunLoop_WhenTheDelaySeamThrows_EndsCleanly_AndDoesNotFaultTheLoopTask()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { AutoScanEnabled = true, AutoScanIntervalMinutes = IntervalMinutes });

        var logger = new RecordingLogger();
        var scans = 0;
        var scheduler = new ScanScheduler(
            settings.Store,
            () => false,
            _ => { Interlocked.Increment(ref scans); return Task.CompletedTask; },
            logger.For<ScanScheduler>(),
            (_, _) => Task.FromException(new InvalidOperationException("the delay seam failed")));

        scheduler.Start();
        var loop = scheduler.LoopTask;
        Assert.NotNull(loop);

        await loop.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(TaskStatus.RanToCompletion, loop.Status);
        Assert.Equal(0, scans);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("wait failed", StringComparison.Ordinal));

        // Idempotent afterwards: Stop on a loop that already ended must not throw.
        scheduler.Stop();
    }
}
