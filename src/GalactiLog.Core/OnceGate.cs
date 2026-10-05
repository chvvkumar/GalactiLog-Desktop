using System.Threading;

namespace GalactiLog.Core;

// A tiny thread-safe latch for "fire a warning once per scan run" (spec 8.3, 16.1). One
// instance per concern per scan run; never static/shared across runs or concerns.
public sealed class OnceGate
{
    private int _fired;

    // Returns true exactly once (the first caller across any thread), false on every
    // subsequent call. Interlocked, not a lock: reader tasks in the scan pipeline call this
    // concurrently (spec 10.6) and must never race into firing twice.
    public bool TryFire() => Interlocked.Exchange(ref _fired, 1) == 0;
}
