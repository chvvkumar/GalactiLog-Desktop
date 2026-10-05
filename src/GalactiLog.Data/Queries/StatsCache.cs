namespace GalactiLog.Data.Queries;

/// <summary>
/// The memoized statistics response (spec 12.5's Query paragraph: "cached in memory until the next
/// scan completes or settings change"). Shaped exactly like <see cref="RigBaselinesCache"/>: a
/// lazily built value behind a <see cref="Lock"/> and an explicit <see cref="Invalidate"/> the App
/// layer calls.
/// </summary>
/// <remarks>
/// <para>
/// No TTL, deliberately, and this is the one place the difference is recorded.
/// <see cref="AliasMapCache"/> and <see cref="RigBaselinesCache"/> both carry one as a backstop
/// against a write that arrived through some path that raised no event. Here all three triggers
/// are explicit, in-process and reliable (<c>ScanStatusService.ScanFinished</c>,
/// <c>SettingsStore.AliasSourcesChanged</c> and <c>SettingsStore.GeneralChanged</c>), and a timer
/// would make the Statistics page re-run a full-library aggregate for no reason. questions.md Q7.
/// </para>
/// <para>
/// Second difference: <see cref="Invalidate"/> takes no lock, where
/// <see cref="RigBaselinesCache.Invalidate"/> does. See that method's own remark for why the UI
/// thread must never wait on a build here.
/// </para>
/// <para>
/// This is the third copy of that cache shape, not a generic TTL cache. Phase 6's questions.md Q7
/// ruled that copying the shape for two call sites beat extracting one; at three the balance is
/// still copying, because this one differs (no TTL, no clock, a non-blocking invalidate) and the
/// shared part is about ten lines. If a fourth appears, extracting the spine is that change's
/// first step, not a someday refactor (design-lessons rule 1).
/// </para>
/// </remarks>
/// <param name="thumbnailCacheBytes">Passed straight to <see cref="StatsQuery.Get"/> on every
/// build (questions.md Q12). Held here rather than at the call site so every reader of
/// <see cref="Current"/> gets a response whose storage section is filled in the same way.</param>
public sealed class StatsCache(StatsQuery query, Func<long>? thumbnailCacheBytes = null)
{
    private readonly Lock _buildGate = new();

    private volatile StatsResponse? _cached;
    private int _generation;

    /// <summary>Returns the memoized response, building it on the first read after construction or
    /// an invalidation. Safe to call from any thread: a hit takes no lock at all, and two callers
    /// arriving on a miss serialize on the build gate so the library is aggregated once rather
    /// than twice.</summary>
    public StatsResponse Current
    {
        get
        {
            while (true)
            {
                if (_cached is { } memo)
                {
                    return memo;
                }

                lock (_buildGate)
                {
                    // Another caller may have published while this one waited for the gate.
                    if (_cached is { } published)
                    {
                        return published;
                    }

                    var generation = Volatile.Read(ref _generation);
                    var built = query.Get(thumbnailCacheBytes);
                    if (Volatile.Read(ref _generation) == generation)
                    {
                        _cached = built;
                        return built;
                    }
                }

                // A build that was invalidated while it was in flight describes a library state
                // that no longer exists, so it is discarded and the loop rebuilds. The loop can
                // only repeat while invalidations keep arriving, and each pass does one aggregate.
            }
        }
    }

    /// <summary>Drops the memoized response so the next read rebuilds. The App layer calls this
    /// from <c>ScanStatusService.ScanFinished</c> and from the two <c>SettingsStore</c> events,
    /// never from a second <c>ScanCoordinator</c> subscription.</summary>
    /// <remarks>
    /// Takes no lock, deliberately, so it can never block. <c>ScanStatusService</c> raises
    /// <c>ScanFinished</c> through <c>Dispatcher.UIThread.Post</c>, so this runs on the UI thread;
    /// if it waited on the build gate, a scan finishing while a background read of
    /// <see cref="Current"/> was in flight would freeze the window for the length of a
    /// full-library aggregate. Instead it clears the memo and bumps a generation counter, and a
    /// build already in flight is discarded when it finds the generation moved under it.
    /// </remarks>
    public void Invalidate()
    {
        _cached = null;
        Interlocked.Increment(ref _generation);
    }
}
