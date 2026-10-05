using Avalonia.Threading;
using GalactiLog.App.ViewModels.Analysis;
using Xunit;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The one join between a case and an Analysis load, correct under every post seam the Analysis
/// test files use.
/// </summary>
/// <remarks>
/// <para>
/// A tab's <c>PendingLoad</c> is the QUERY's task, and the publish is handed to the post seam from
/// inside it (<c>AnalysisTabViewModel.Refresh</c>). Under the inline seam the publish therefore
/// runs before that task completes; under <c>UiPost.Default</c> it is only queued, and the awaited
/// task can complete a whole dispatcher turn before any value lands. Awaiting the task alone is
/// right under one seam and a race under the other, so every join here awaits the task AND drains
/// the dispatcher.
/// </para>
/// <para>
/// The task is replaced rather than chained: a refresh started while a case is awaiting leaves the
/// awaited task stale, so the join repeats until the task it awaited is still the current one. It
/// is bounded, it fails the case with a sentence rather than hanging or answering a stale value,
/// and there is no sleep anywhere in it.
/// </para>
/// <para>
/// What it does NOT do: it cannot wait out a load a case left in flight BEFORE starting another
/// one, because only the current task is reachable. Two overlapping loads publish on two
/// thread-pool threads under the inline seam, where the base's generation compare is no longer the
/// single-threaded read its remarks describe, and the earlier load can write its own state over
/// the later one's. That is a case shape and not a tab defect: the application's seam is
/// <c>UiPost.Default</c>, where every publish and every generation read is on the dispatcher and
/// the stale publish is discarded. A case therefore joins here after EACH load it starts,
/// including the one a tab's first selection starts, and not only after the last.
/// </para>
/// </remarks>
internal static class AnalysisSettle
{
    // A harness bound and never a figure: it has to outlast a loaded machine running the whole
    // Analysis filter at once, and a case that reaches it has a defect rather than a slow machine.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // Each round joins one load and drains what it posted, and a drained publish can start the
    // next: the page's own filter handler refreshes the selected tab on every change.
    private const int Rounds = 8;

    /// <summary>Joins whatever load <paramref name="tab"/> has in flight, and the publish it
    /// posted.</summary>
    public static async Task Tab(AnalysisTabViewModel tab)
    {
        for (var round = 0; round < Rounds; round++)
        {
            if (tab.PendingLoad is not { } load)
            {
                Drain();
                return;
            }

            try
            {
                await load.WaitAsync(Bound);
            }
            catch (TimeoutException)
            {
                Assert.Fail(
                    $"The {tab.Key} tab's load did not finish within {Bound.TotalSeconds} seconds.");
            }
            catch (Exception)
            {
                // The load loop turns a failing query into a tab state, so a faulted task here is
                // the case's own subject rather than this join's problem. The same reason
                // AnalysisViewModelTestFactory.Settle swallows one.
            }

            Drain();

            if (ReferenceEquals(load, tab.PendingLoad))
            {
                return;
            }
        }

        Assert.Fail(
            $"The {tab.Key} tab started a further load on each of {Rounds} joins and never settled.");
    }

    /// <summary>
    /// Joins the filter bar's list read, every tab's load and the publishes they posted. Synchronous,
    /// because the cases that show the page are <c>AvaloniaFact</c> bodies that run on the UI thread
    /// and read the bound controls straight after.
    /// </summary>
    public static void Page(AnalysisViewModel page)
    {
        for (var round = 0; round < Rounds; round++)
        {
            AnalysisViewModelTestFactory.Settle(page);
            Drain();

            if (Settled(page))
            {
                return;
            }
        }

        Assert.Fail(
            $"The Analysis page started a further load on each of {Rounds} joins and never settled.");
    }

    private static bool Settled(AnalysisViewModel page)
        => page.SharedFilter.PendingLoad.IsCompleted
            && page.Tabs.All(tab => tab.PendingLoad is null or { IsCompleted: true });

    // From the UI thread only: a windowless case runs on an xunit thread with nothing posted to the
    // dispatcher at all, and RunJobs there fails the dispatcher's own access check.
    private static void Drain()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.RunJobs();
        }
    }
}
