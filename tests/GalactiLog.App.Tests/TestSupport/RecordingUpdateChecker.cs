using GalactiLog.App.Services;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The update feed as a recording stub. The one <see cref="IUpdateChecker"/> implementation in
/// <c>GalactiLog.App.Tests</c>, in the shape <see cref="RecordingLogger"/> established: no test
/// builds a Velopack update manager and no test reaches the network.
/// </summary>
internal sealed class RecordingUpdateChecker : IUpdateChecker
{
    private int _checks;
    private int _downloads;
    private int _applies;

    /// <summary>Whether the updater installed this process. Every update path is gated on it.
    /// </summary>
    public bool IsInstalled { get; set; } = true;

    /// <summary>The channel the feed reports (spec 17.4).</summary>
    public string Channel { get; set; } = "alpha";

    /// <summary>What the next check returns. Null is "no update".</summary>
    public AvailableUpdate? Offered { get; set; }

    /// <summary>Thrown by every check while it is set.</summary>
    public Exception? CheckThrows { get; set; }

    /// <summary>
    /// Parks every check until it is completed, so a test can press a button a second time while
    /// the first press is genuinely in flight. Null lets checks complete at once.
    /// </summary>
    public TaskCompletionSource? CheckGate { get; set; }

    /// <summary>The percents a download reports before it completes.</summary>
    public IReadOnlyList<int> ProgressPercents { get; set; } = [50, 100];

    /// <summary>Thrown by every download while it is set, after the progress it reported.
    /// </summary>
    public Exception? DownloadThrows { get; set; }

    /// <summary>Runs inside <see cref="ApplyAndRestart"/> before the call is recorded, so a test
    /// can observe what the database held at the moment of the apply.</summary>
    public Action? OnApply { get; set; }

    /// <summary>The update the last apply was given.</summary>
    public AvailableUpdate? Applied { get; private set; }

    public int Checks => Volatile.Read(ref _checks);

    public int Downloads => Volatile.Read(ref _downloads);

    public int Applies => Volatile.Read(ref _applies);

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _checks);

        if (CheckGate is { } gate)
        {
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }

        return CheckThrows is { } failure ? throw failure : Offered;
    }

    public Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken ct)
    {
        Interlocked.Increment(ref _downloads);
        foreach (var percent in ProgressPercents)
        {
            progress(percent);
        }

        return DownloadThrows is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    public void ApplyAndRestart(AvailableUpdate update)
    {
        OnApply?.Invoke();
        Applied = update;
        Interlocked.Increment(ref _applies);
    }
}
