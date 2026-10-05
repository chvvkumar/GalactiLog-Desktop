using GalactiLog.App.Services;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// FIXER LIST F11, rewritten by the coordinator: the restart-window shape was written four times
// (DashboardViewModel.Restart for its three windows, ActivityViewModel, TargetsTabViewModel,
// MergeDialogViewModel) and is now one type. These are the three properties every one of those
// callers was relying on: rapid inputs coalesce, the newest wins, and disposal cancels.
//
// Plain xunit facts over FakeDelay, the same seam the view-models take, so nothing here sleeps.
public class DebouncerTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Restart_RapidInputs_CoalesceIntoOneCompletedRun()
    {
        var delay = new FakeDelay();
        using var lifetime = new CancellationTokenSource();
        using var debouncer = new Debouncer(lifetime.Token, delay.Delay, Window);

        var ran = 0;
        var tasks = new List<Task>();
        for (var i = 0; i < 5; i++)
        {
            tasks.Add(debouncer.Restart(async (_, token) =>
            {
                try
                {
                    await debouncer.Wait(token).ConfigureAwait(false);
                    Interlocked.Increment(ref ran);
                }
                catch (OperationCanceledException)
                {
                    // A newer input superseded this window, which is the whole point.
                }
            }));
        }

        // Releasing every parked wait at once is the worst case: four of the five are already
        // cancelled, so only the newest gets past its Wait.
        var deadline = DateTime.UtcNow + Budget;
        while (tasks.Exists(task => !task.IsCompleted) && DateTime.UtcNow < deadline)
        {
            delay.Release();
            await Task.Delay(2);
        }

        await Task.WhenAll(tasks);
        Assert.Equal(1, Volatile.Read(ref ran));

        // And the window each one ran under is the one figure its owner passed in.
        Assert.All(delay.Requested, requested => Assert.Equal(Window, requested));
    }

    [Fact]
    public void Restart_BumpsTheGeneration_AndOnlyTheNewestIsCurrent()
    {
        var delay = new FakeDelay();
        using var lifetime = new CancellationTokenSource();
        using var debouncer = new Debouncer(lifetime.Token, delay.Delay, Window);

        var generations = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            debouncer.Restart((generation, _) =>
            {
                generations.Add(generation);
                return Task.CompletedTask;
            });
        }

        // Latest wins: the publish guard every caller applies before it writes to a binding.
        Assert.Equal([1, 2, 3], generations);
        Assert.False(debouncer.IsCurrent(1));
        Assert.False(debouncer.IsCurrent(2));
        Assert.True(debouncer.IsCurrent(3));
    }

    [Fact]
    public void Restart_CancelsThePreviousWindowsToken()
    {
        var delay = new FakeDelay();
        using var lifetime = new CancellationTokenSource();
        using var debouncer = new Debouncer(lifetime.Token, delay.Delay, Window);

        CancellationToken first = default;
        debouncer.Restart((_, token) => { first = token; return Task.CompletedTask; });
        Assert.False(first.IsCancellationRequested);

        CancellationToken second = default;
        debouncer.Restart((_, token) => { second = token; return Task.CompletedTask; });

        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
    }

    [Fact]
    public void Dispose_CancelsTheOpenWindow_AndLaterRestartsRunNothing()
    {
        var delay = new FakeDelay();
        using var lifetime = new CancellationTokenSource();
        var debouncer = new Debouncer(lifetime.Token, delay.Delay, Window);

        CancellationToken open = default;
        debouncer.Restart((_, token) => { open = token; return Task.CompletedTask; });

        debouncer.Dispose();

        Assert.True(open.IsCancellationRequested);

        var ran = false;
        var task = debouncer.Restart((_, _) => { ran = true; return Task.CompletedTask; });

        // A completed task, never null: every caller assigns this straight to its Pending seam.
        Assert.False(ran);
        Assert.True(task.IsCompleted);

        // Idempotent, because the owners dispose their lifetime source as well.
        debouncer.Dispose();
    }

    [Fact]
    public void TheOwnersLifetime_CancelsAnOpenWindow()
    {
        var delay = new FakeDelay();
        using var lifetime = new CancellationTokenSource();
        using var debouncer = new Debouncer(lifetime.Token, delay.Delay, Window);

        CancellationToken open = default;
        debouncer.Restart((_, token) => { open = token; return Task.CompletedTask; });

        lifetime.Cancel();

        // The F6 rule: a window opened just before shutdown must not keep running after the host
        // and its SQLite files are gone.
        Assert.True(open.IsCancellationRequested);
    }

    [Fact]
    public async Task Wait_IsCancelledWithItsWindow()
    {
        var delay = new FakeDelay();
        using var lifetime = new CancellationTokenSource();
        using var debouncer = new Debouncer(lifetime.Token, delay.Delay, Window);

        Task? parked = null;
        _ = debouncer.Restart((_, token) => { parked = debouncer.Wait(token); return parked; });
        Assert.NotNull(parked);
        Assert.False(parked!.IsCompleted);

        _ = debouncer.Restart((_, _) => Task.CompletedTask);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parked!);
    }
}
