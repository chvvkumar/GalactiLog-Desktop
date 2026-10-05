namespace GalactiLog.App.Services;

/// <summary>
/// One restartable background window: cancel whatever window of this kind was open, open a new one
/// linked to the owner's lifetime, bump its generation, and hand the work the generation it is
/// running under. <see cref="Wait"/> is the debounce itself, awaited by work that is driven by
/// typing.
/// </summary>
/// <remarks>
/// <para>
/// FIXER LIST F11, rewritten by the coordinator at the Phase 9 close: this shape was written four
/// times, and the design-lessons rule 1 threshold was passed in Phase 7 rather than in Phase 9.
/// <c>DashboardViewModel</c> had it as a private <c>Restart</c> shared by its three windows;
/// <c>ActivityViewModel</c>, <c>TargetsTabViewModel</c> and <c>MergeDialogViewModel</c> each wrote
/// it out again, sharing only the window duration.
/// </para>
/// <para>
/// The generation is not redundant with the token. A token stops work that has not finished; it
/// cannot stop a query that already finished and is sitting in the dispatcher queue behind a newer
/// one. Every publish therefore asks <see cref="IsCurrent"/> before it writes to a binding.
/// </para>
/// <para>
/// Not thread-safe by design, and it does not need to be: <see cref="Restart"/> is called from the
/// UI thread (a keystroke, a pill toggle, a scan finishing through the post seam) and
/// <see cref="IsCurrent"/> from the publish thread, which is the same one. The generation is moved
/// and read through <see cref="Volatile"/> anyway, because the work between the two runs on the
/// pool.
/// </para>
/// </remarks>
/// <param name="lifetime">The owner's lifetime token. Every window is linked to it, so disposing
/// the owner stops work parked in a window it opened.</param>
/// <param name="delay">The delay seam every view-model takes, so a test drives the window instead
/// of sleeping. Normally <c>Task.Delay</c>.</param>
/// <param name="window">How long <see cref="Wait"/> waits. Normally
/// <c>DashboardViewModel.DebounceWindow</c>, which is the one figure ruling Q3 gives.</param>
internal sealed class Debouncer(
    CancellationToken lifetime,
    Func<TimeSpan, CancellationToken, Task> delay,
    TimeSpan window) : IDisposable
{
    private CancellationTokenSource? _current;
    private int _generation;
    private bool _disposed;

    /// <summary>
    /// Cancels the open window, opens a new one, and starts <paramref name="work"/> in it. Returns
    /// the work's task, which every caller exposes as its <c>Pending*</c> test seam.
    /// </summary>
    public Task Restart(Func<int, CancellationToken, Task> work)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        _current?.Cancel();
        _current?.Dispose();
        _current = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        return work(Interlocked.Increment(ref _generation), _current.Token);
    }

    /// <summary>The debounce: N rapid inputs coalesce into one piece of work, because each
    /// <see cref="Restart"/> cancels the previous window's wait.</summary>
    public Task Wait(CancellationToken cancellationToken) => delay(window, cancellationToken);

    /// <summary>Whether <paramref name="generation"/> is still the newest, which is what a publish
    /// asks before it writes to a binding.</summary>
    public bool IsCurrent(int generation) => generation == Volatile.Read(ref _generation);

    /// <summary>Cancels the open window and releases it. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _current?.Cancel();
        _current?.Dispose();
        _current = null;
    }
}
