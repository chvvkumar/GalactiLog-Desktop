using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Mosaics;
using GalactiLog.Core.Settings;

namespace GalactiLog.App.Tests.TestSupport;

// Phase 18 Task 4. The one place App.Tests builds a Mosaics page: every collaborator is a delegate
// on MosaicsBackend, so no database is opened. The post is synchronous, the delay completes at
// once, and the display document is a local the writer saves back into.
internal sealed class MosaicsPageHarness : IDisposable
{
    private readonly object _ui = new();

    public MosaicsPageHarness(
        MosaicsBackend? backend = null, DisplaySettings? display = null, ScanStatusService? scanStatus = null, JobRegistry? jobs = null)
    {
        // Synchronous, but one action at a time across threads, the way the one UI thread runs
        // them: a reload's apply and a job's end both arrive from the thread pool (Task 6c).
        void Post(Action action)
        {
            lock (_ui)
            {
                action();
            }
        }

        Jobs = jobs ?? new JobRegistry(Post);
        Display = display ?? new DisplaySettings();
        Writer = new DisplayColumnWriter(() => Display, saved => Display = saved);
        Page = new MosaicsPageViewModel(
            backend ?? new MosaicsBackend(),
            Jobs,
            Display,
            Writer,
            scanStatus,
            post: Post,
            delay: (_, _) => Task.CompletedTask);
    }

    public JobRegistry Jobs { get; }

    public DisplaySettings Display { get; private set; }

    public DisplayColumnWriter Writer { get; }

    public MosaicsPageViewModel Page { get; }

    // On the same one "UI thread" as the posts, so a reload's apply cannot mutate the rows mid-dispose.
    public void Dispose()
    {
        lock (_ui)
        {
            Page.Dispose();
        }
    }
}

// Joins a Mosaics page's pending load from a synchronous test, the way
// AnalysisViewModelTestFactory.Settle joins the Analysis page's. Bounded; true when it finished.
internal static class MosaicsPageSettle
{
    public static bool Settle(MosaicsPageViewModel page) => page.PendingLoad.Wait(TimeSpan.FromSeconds(30));
}
