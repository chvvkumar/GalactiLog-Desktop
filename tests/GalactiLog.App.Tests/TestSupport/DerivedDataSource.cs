namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// A stand-in for the one process-level "the derived data behind an open page has been rewritten"
/// notification <c>AppHost</c> raises (Phase 15B fixer F2, with items 30 and 38), so a page test
/// can raise it and observe what the page did.
/// </summary>
/// <remarks>
/// It exists because a page follows that notification through a subscribe and unsubscribe delegate
/// pair and never takes the source itself (spec 18.3), which leaves a test with nothing to hold.
/// The production source is <c>AppHost.DerivedDataNotifier</c>, and the host-level case in
/// <c>Phd2ReRunHostWiringTests</c> is what proves the real one is wired to the real sources.
/// </remarks>
internal sealed class DerivedDataSource
{
    private EventHandler? _changed;

    /// <summary>Whether anything is still following. False once a page has been disposed, which is
    /// the half of the pair that matters: the real notifier outlives every page it speaks to.
    /// </summary>
    public bool HasFollower => _changed is not null;

    public void Subscribe(EventHandler handler) => _changed += handler;

    public void Unsubscribe(EventHandler handler) => _changed -= handler;

    public void Raise() => _changed?.Invoke(this, EventArgs.Empty);
}
