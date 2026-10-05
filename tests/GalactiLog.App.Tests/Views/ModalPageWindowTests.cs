using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.App.Views;
using GalactiLog.App.Views.Merge;
using GalactiLog.App.Views.Settings;
using GalactiLog.Data.Maintenance;
using Xunit;
using MergeFactory = GalactiLog.App.Tests.TestSupport.MergeDialogViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// FIXER LIST F20. The three modal dialogs hand-rolled the same subscribed flag, the same
// OnDataContextChanged detach-and-reattach, the same CloseRequested-to-Close(result) path and three
// different close-reason policies; two of them vetoed an OS shutdown while a write was in flight.
// ModalPageWindow<TViewModel> owns all of it now and each dialog answers one question, RefuseClose.
// The wizard's own policy is pinned in SetupWizardWindowTests; these are the other two, plus the
// shared parts of the spine.
public class ModalPageWindowTests
{
    private static readonly WindowCloseReason[] PassThrough =
    [
        WindowCloseReason.OSShutdown,
        WindowCloseReason.ApplicationShutdown,
        WindowCloseReason.OwnerWindowClosing,
    ];

    // ---- MergeDialogWindow -----------------------------------------------------------------

    // The post seam marshals to the dispatcher rather than running inline: these cases show a real
    // window, and the merge completes on a pool thread, so an inline publish would raise
    // CanExecuteChanged at a bound Button from the wrong thread.
    [AvaloniaFact]
    public async Task MergeDialog_RefusesTheUsersDismissalWhileWriting_AndLetsEveryShutdownThrough()
    {
        using var release = new ManualResetEventSlim(false);
        using var harness = MergeFactory.Create(post: action => Dispatcher.UIThread.Post(action)).Settle();
        harness.MergeRelease = release;
        var window = new MergeDialogWindow { DataContext = harness.ViewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Nothing in flight: the dialog refuses nothing at all.
        Assert.False(WindowClosingProbe.RaiseClosing(window, WindowCloseReason.WindowClosing).Cancel);

        harness.ViewModel.ConfirmCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(harness.ViewModel.IsWriting);

        Assert.True(WindowClosingProbe.RaiseClosing(window, WindowCloseReason.WindowClosing).Cancel);
        foreach (var reason in PassThrough)
        {
            // The Important finding this item came from: a veto here keeps the process alive with
            // Windows reporting the application as blocking the session from ending.
            Assert.False(WindowClosingProbe.RaiseClosing(window, reason).Cancel);
        }

        release.Set();
        if (harness.ViewModel.ConfirmCommand.ExecutionTask is { } run)
        {
            await run;
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void MergeDialog_ClosesWithTheResultThePageAsksFor()
    {
        using var harness = MergeFactory.Create().Settle();
        var window = new MergeDialogWindow { DataContext = harness.ViewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        harness.ViewModel.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // The base class's CloseRequested-to-Close(result) path, which every dialog used to write
        // for itself.
        Assert.False(window.IsVisible);
        Assert.Equal([false], harness.Closes);
    }

    // ---- ResetConfirmWindow ----------------------------------------------------------------

    private sealed class GatedReset
    {
        public ManualResetEventSlim Gate { get; } = new(false);

        public int Calls { get; private set; }

        public DatabaseReset.ResetOutcome Run(CancellationToken cancellationToken)
        {
            Calls++;
            Gate.Wait(TimeSpan.FromSeconds(30));
            return new DatabaseReset.ResetOutcome(9, 123, true);
        }
    }

    [AvaloniaFact]
    public async Task ResetConfirm_RefusesTheUsersDismissalWhileResetting_AndLetsEveryShutdownThrough()
    {
        var reset = new GatedReset();
        var page = new ResetConfirmViewModel(reset.Run, post: action => Dispatcher.UIThread.Post(action));
        var window = new ResetConfirmWindow { DataContext = page };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(WindowClosingProbe.RaiseClosing(window, WindowCloseReason.WindowClosing).Cancel);

        page.Typed = ResetConfirmViewModel.RequiredPhrase;
        page.ConfirmCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(page.IsResetting);

        Assert.True(WindowClosingProbe.RaiseClosing(window, WindowCloseReason.WindowClosing).Cancel);
        foreach (var reason in PassThrough)
        {
            Assert.False(WindowClosingProbe.RaiseClosing(window, reason).Cancel);
        }

        reset.Gate.Set();
        if (page.ConfirmCommand.ExecutionTask is { } run)
        {
            await run;
        }

        Dispatcher.UIThread.RunJobs();
        reset.Gate.Dispose();
    }

    [AvaloniaFact]
    public void ResetConfirm_ClosesWithTheResultThePageAsksFor()
    {
        var reset = new GatedReset();
        reset.Gate.Set();
        var page = new ResetConfirmViewModel(reset.Run, post: action => action());
        var results = new List<bool>();
        page.CloseRequested += (_, ran) => results.Add(ran);
        var window = new ResetConfirmWindow { DataContext = page };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        page.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible);
        Assert.Equal([false], results);
        reset.Gate.Dispose();
    }

    // ---- the spine itself ------------------------------------------------------------------

    [AvaloniaFact]
    public void ADetachedPage_NoLongerClosesTheWindow()
    {
        // OnDataContextChanged's detach half, which all three dialogs wrote by hand. A page the
        // window has moved off must not be able to close it.
        var first = new ResetConfirmViewModel(_ => new DatabaseReset.ResetOutcome(0, 0, true), post: action => action());
        var second = new ResetConfirmViewModel(_ => new DatabaseReset.ResetOutcome(0, 0, true), post: action => action());
        var window = new ResetConfirmWindow { DataContext = first };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.DataContext = second;
        Dispatcher.UIThread.RunJobs();

        first.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);

        second.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
    }

    // A dialog whose policy is as wrong as a policy can be. Nothing constructs it outside the case
    // below; it exists so the rule is asserted against the base class rather than against the four
    // overrides that happen to be correct today.
    private sealed class RefusesEverythingWindow : ModalPageWindow<ResetConfirmViewModel>
    {
        protected override bool RefuseClose(WindowCloseReason reason) => true;
    }

    [AvaloniaFact]
    public void AShutdown_IsNeverVetoed_EvenByADialogThatRefusesEveryReason()
    {
        // Phase review finding P6, design-lessons rule 2. The rule was stated in the base class's
        // remarks and then implemented separately in four RefuseClose overrides. Phase 11 made it
        // load-bearing: a vetoed close now makes desktop.TryShutdown() return false AFTER the
        // ShutdownRequested handler has marked the residency service exiting and drained the
        // watcher, the scheduler, the update loop, the thumbnail worker, the scan completion
        // watcher and the single-instance listener, and RequestExit then reverts the flag, leaving
        // a live process with every subsystem stopped and nothing that restarts any of them.
        //
        // The filter is at the base now, so a fifth dialog cannot reintroduce it: this window
        // refuses every reason it is asked about and still lets all three shutdowns through.
        var window = new RefusesEverythingWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(WindowClosingProbe.RaiseClosing(window, WindowCloseReason.WindowClosing).Cancel);
        foreach (var reason in PassThrough)
        {
            Assert.False(WindowClosingProbe.RaiseClosing(window, reason).Cancel);
        }
    }
}
