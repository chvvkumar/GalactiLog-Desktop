using GalactiLog.App.Services;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The test double behind design-spec 12.11 behaviour 10's one notifier seam: it records what it
/// was asked to show instead of touching a notification area.
/// </summary>
/// <remarks>
/// The roadmap's Phase 11 row 5 Verify line is about the notice being <em>requested</em>, which is
/// exactly what this records. Every field is read under a lock, because the request arrives on
/// whichever thread the watcher's post seam publishes on and the assertion runs on the test's.
/// </remarks>
internal sealed class RecordingScanCompletionNotifier : IScanCompletionNotifier
{
    private readonly Lock _gate = new();
    private readonly List<ScanCompletionNotice> _notices = [];

    /// <summary>Whether this notifier reports that it can show anything. False is the state a
    /// build with no surface for the notice would have.</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>Set to have <see cref="Show"/> throw, so the watcher's guard is observable.
    /// </summary>
    public Exception? ThrowOnShow { get; set; }

    /// <summary>Every notice requested, in order.</summary>
    public IReadOnlyList<ScanCompletionNotice> Notices
    {
        get { lock (_gate) { return [.. _notices]; } }
    }

    /// <summary>How many notices were requested.</summary>
    public int Count
    {
        get { lock (_gate) { return _notices.Count; } }
    }

    public void Show(ScanCompletionNotice notice)
    {
        lock (_gate)
        {
            _notices.Add(notice);
        }

        if (ThrowOnShow is { } error)
        {
            throw error;
        }
    }
}
