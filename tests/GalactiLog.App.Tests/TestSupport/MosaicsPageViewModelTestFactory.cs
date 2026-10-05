using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.Core.Settings;

namespace GalactiLog.App.Tests.TestSupport;

// Phase 18 Task 4. The one place App.Tests builds a Mosaics page: every collaborator is a delegate
// on MosaicsBackend, so no database is opened. The post is synchronous, the delay completes at
// once, and the display document is a local the writer saves back into.
internal sealed class MosaicsPageHarness : IDisposable
{
    public MosaicsPageHarness(
        MosaicsBackend? backend = null, DisplaySettings? display = null, ScanStatusService? scanStatus = null, JobRegistry? jobs = null)
    {
        Jobs = jobs ?? new JobRegistry(action => action());
        Display = display ?? new DisplaySettings();
        Writer = new DisplayColumnWriter(() => Display, saved => Display = saved);
        Page = new MosaicsPageViewModel(
            backend ?? new MosaicsBackend(),
            Jobs,
            Display,
            Writer,
            scanStatus,
            post: action => action(),
            delay: (_, _) => Task.CompletedTask);
    }

    public JobRegistry Jobs { get; }

    public DisplaySettings Display { get; private set; }

    public DisplayColumnWriter Writer { get; }

    public MosaicsPageViewModel Page { get; }

    public void Dispose() => Page.Dispose();
}

// Joins a Mosaics page's pending load from a synchronous test, the way
// AnalysisViewModelTestFactory.Settle joins the Analysis page's. Bounded; true when it finished.
internal static class MosaicsPageSettle
{
    public static bool Settle(MosaicsPageViewModel page) => page.PendingLoad.Wait(TimeSpan.FromSeconds(30));
}
