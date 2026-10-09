using GalactiLog.App.ViewModels;
using GalactiLog.Data.Ingest;

namespace GalactiLog.App.Services;

/// <summary>
/// The one door every manual scan button goes through (status bar, dashboard empty state, Library
/// tab). A scan reads the settings on disk, so a scan started while an edit is staged silently
/// ignores it: the origin bug was a library folder added in Settings, never saved, and never
/// scanned. Refusing here, at the shared delegate, rather than in each button means a fourth
/// button cannot forget (design-lessons rule 2).
/// </summary>
/// <remarks>
/// Every caller invokes its delegate inside Task.Run, and the registry is UI-thread only, so the
/// refusal is posted. The pending read itself is a bool and is re-checked in the post, so a
/// registry that went clean in between does not get a stale notice.
/// </remarks>
public sealed class ManualScanGate(
    PendingEditsRegistry registry,
    Func<ScanRunOptions?, CancellationToken, Task> run,
    Action<Action>? post = null)
{
    public const string RefusalNotice = "Save or discard your changes before scanning.";

    private readonly Action<Action> _post = post ?? UiPost.Default;

    public Task RunAsync(ScanRunOptions? options, CancellationToken token)
    {
        if (!registry.AnyPending) return run(options, token);

        _post(() =>
        {
            if (registry.AnyPending) registry.Notice = RefusalNotice;
        });
        return Task.CompletedTask;
    }
}
