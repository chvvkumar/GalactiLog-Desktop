namespace GalactiLog.App.Services;

/// <summary>
/// Spec 12.11 behaviour 10: how a finished scan reaches a user with no window open. One method,
/// because that is the whole contract, and one seam, because the mechanism is a coordinator ruling
/// that a later phase may change (questions.md Q6) and this is the one file that would change.
/// </summary>
public interface IScanCompletionNotifier
{
    /// <summary>Whether this notifier can show anything on this build and this machine. False
    /// leaves the watcher silent and logs once.</summary>
    bool IsAvailable { get; }

    /// <summary>Shows the notice. Called on the UI thread. Must not block and must not throw:
    /// it catches, logs and returns (spec 12.10).</summary>
    void Show(ScanCompletionNotice notice);
}
