using System.ComponentModel;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Core.Scanning;
using GalactiLog.Data;
using GalactiLog.Data.Ingest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// The single App-layer subscriber to ScanCoordinator.ProgressChanged and ScanFinished
// (design-spec 4.2, 10.4). Every envelope-shape assertion drives the coordinator through its
// internal RaiseProgress (InternalsVisibleTo added to GalactiLog.Data for this project in this
// task), which does no database or filesystem I/O of its own -- so those tests use a
// coordinator built over a connection string that is never opened
// (ScanCoordinatorTestFactory.CreateBare(), fix pass review item 5). The handful of tests that
// need a real ScanFinished (a completed run) drive an actual scan over SettingsFixture's empty
// temp root instead, reusing the fixture WatcherServiceTests already defines
// (ScanCoordinatorTestFactory.Create(settings)).
public class ScanStatusServiceTests
{
    [Fact]
    public void ProgressChanged_UpdatesEveryEnvelopeField()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var service = new ScanStatusService(coordinator, action => action());

        coordinator.RaiseProgress(ScanTaskNames.Classify, 3, 10, "Classifying 3/10 files", force: true);

        Assert.Equal(ScanTaskNames.Classify, service.TaskName);
        Assert.Equal("Classifying 3/10 files", service.Message);
        Assert.Equal(30d, service.Percent);
        Assert.True(service.HasDeterminatePercent);
    }

    [Fact]
    public void ProgressChanged_EveryMutation_HappensInsideThePostedClosure()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        var recorded = new List<Action>();
        using var service = new ScanStatusService(coordinator, recorded.Add);

        coordinator.RaiseProgress(ScanTaskNames.Discovery, 1, 5, "Discovering files...", force: true);

        var posted = Assert.Single(recorded);
        // Nothing has changed yet: the handler only built the closure and handed it to post.
        Assert.False(service.IsRunning);
        Assert.Equal("", service.TaskName);
        Assert.Equal("Ready", service.Message);
        Assert.Equal(0d, service.Percent);

        posted();

        Assert.True(service.IsRunning);
        Assert.Equal(ScanTaskNames.Discovery, service.TaskName);
        Assert.Equal("Discovering files...", service.Message);
        Assert.Equal(20d, service.Percent);
    }

    [Fact]
    public void ProgressChanged_WithZeroTotalSteps_ReportsIndeterminate()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var service = new ScanStatusService(coordinator, action => action());

        coordinator.RaiseProgress(ScanTaskNames.Discovery, 0, 0, "Discovering files...", force: true);

        Assert.False(service.HasDeterminatePercent);
        Assert.True(service.IsIndeterminate);
        Assert.Equal(0d, service.Percent);
    }

    [Fact]
    public void ProgressChanged_SetsIsRunning()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var service = new ScanStatusService(coordinator, action => action());

        Assert.False(service.IsRunning);

        coordinator.RaiseProgress(ScanTaskNames.Classify, 1, 2, "Classifying...", force: true);

        Assert.True(service.IsRunning);
    }

    [Fact]
    public async Task ScanFinished_ClearsIsRunningAndPercent_AndRaisesScanFinishedOnce()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        using var service = new ScanStatusService(coordinator, action => action());

        var raisedCount = 0;
        service.ScanFinished += (_, _) => raisedCount++;

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.False(service.IsRunning);
        Assert.Equal(0d, service.Percent);
        Assert.Equal("Ready", service.Message);
        Assert.Equal(1, raisedCount);
    }

    // Fix pass review item 1. Before the fix, OnScanFinished forced IsRunning = false
    // unconditionally, so a subscriber saw "Ready" even while the coordinator's own pending
    // follow-up (scheduled BEFORE ScanFinished is raised, Phase 4 Task 5 fix pass item 3) was
    // already running. A recording (non-invoking) post lets this test capture the first scan's
    // ScanFinished closure without running it, start a second scan on the same coordinator,
    // wait until that second scan is provably running (its own first progress event), and only
    // then invoke the captured closure -- so it reads coordinator.IsRunning at the moment the
    // fix says it should, not at the moment the closure was built.
    //
    // This test fails before the fix (asserts IsRunning is false, forced unconditionally) and
    // passes after it (asserts IsRunning is true, read live).
    [Fact]
    public async Task ScanFinished_CoordinatorAlreadyRunningAgainByThenClosureRuns_KeepsIsRunningTrue()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);

        var recorded = new List<Action>();
        using var service = new ScanStatusService(coordinator, recorded.Add);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        var scanFinishedClosure = recorded[^1];   // the ScanFinished closure for the run above

        // An empty root gives the second scan nothing to truly await, so it can run to
        // completion synchronously inside the RunAsync call before this method resumes -- a
        // TaskCompletionSource alone would race it. Blocking the pipeline's own thread inside
        // its first progress event, on a background thread so this test's thread is never the
        // one blocked, pins it "still running" until the test says otherwise.
        var secondScanIsRunning = new TaskCompletionSource();
        var releaseSecondScan = new ManualResetEventSlim(false);
        coordinator.ProgressChanged += (_, _) =>
        {
            secondScanIsRunning.TrySetResult();
            releaseSecondScan.Wait();
        };
        var secondRun = Task.Run(() => coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));
        await secondScanIsRunning.Task;

        scanFinishedClosure();

        Assert.True(service.IsRunning);

        releaseSecondScan.Set();
        await secondRun;
    }

    // Fix pass review item 2. OnScanFinished's re-raise of ScanFinished used to be unguarded.
    // A multicast .NET event stops calling subscribers the instant one of them throws, so a
    // throwing page-refresh subscriber (say, Task 7's) used to silently drop every subscriber
    // registered after it (Task 8's) -- not just itself. The coordinator's own guard one level
    // up only protects the scan; it does nothing for ScanStatusService's downstream fan-out.
    //
    // This test fails before the fix (the second subscriber is never called) and passes after
    // it (the throw is caught and logged, and the second subscriber still runs).
    [Fact]
    public async Task ScanFinished_ThrowingSubscriber_DoesNotBlockOtherSubscribers()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);

        // F7: the guard reports through the injected logger, so the test asserts it ran rather
        // than inferring it from the exception not escaping.
        var logger = new RecordingLogger();
        using var service = new ScanStatusService(coordinator, action => action(), logger);

        service.ScanFinished += (_, _) => throw new InvalidOperationException("boom");
        var secondSubscriberCalled = false;
        service.ScanFinished += (_, _) => secondSubscriberCalled = true;

        var exception = await Record.ExceptionAsync(
            () => coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));

        Assert.Null(exception);
        Assert.True(secondSubscriberCalled);

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("ScanFinished subscriber threw", warning.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(warning.Exception);
    }

    [Fact]
    public async Task Constructor_SeedsIsRunningFromCoordinator()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);

        ScanStatusService? service = null;
        var seededIsRunning = false;
        coordinator.ProgressChanged += (_, _) =>
        {
            if (service is not null) return;
            // Built from inside the running scan's own first progress event, so
            // coordinator.IsRunning is provably true at the moment of construction.
            service = new ScanStatusService(coordinator, action => action());
            seededIsRunning = service.IsRunning;
        };

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.True(seededIsRunning);
        service!.Dispose();
    }

    [Fact]
    public async Task Handler_DoesNotThrow_WhenNoSubscribersAreAttached()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        using var service = new ScanStatusService(coordinator, action => action());
        // No subscription to service.ScanFinished and no external PropertyChanged subscriber.

        var exception = await Record.ExceptionAsync(
            () => coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_UnsubscribesFromTheCoordinator()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        var service = new ScanStatusService(coordinator, action => action());

        service.Dispose();
        coordinator.RaiseProgress(ScanTaskNames.Classify, 1, 2, "after dispose", force: true);

        Assert.False(service.IsRunning);
        Assert.Equal("", service.TaskName);
    }

    [AvaloniaFact]
    public async Task ProgressChanged_DefaultPost_DeliversOnTheDispatcherThread()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var service = new ScanStatusService(coordinator);   // real default post

        var checkedOnDispatcherThread = false;
        ((INotifyPropertyChanged)service).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScanStatusService.IsRunning))
            {
                checkedOnDispatcherThread = Dispatcher.UIThread.CheckAccess();
            }
        };

        await Task.Run(() => coordinator.RaiseProgress(
            ScanTaskNames.Discovery, 0, 0, "from a background thread", force: true));

        Dispatcher.UIThread.RunJobs();

        Assert.True(checkedOnDispatcherThread);
    }

    // ---- spec 12's job registry (PAR-015, Phase 14B Task 2) ----------------------------------

    [Fact]
    public void TheScan_RegistersAJobOnItsFirstProgress()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        var jobs = new JobRegistry(action => action());
        using var service = new ScanStatusService(coordinator, action => action(), jobs: jobs);

        coordinator.RaiseProgress(ScanTaskNames.Discovery, 1, 5, "Discovering files...", force: true);

        var job = Assert.Single(jobs.Running);
        Assert.Equal("scan", job.Kind);
        Assert.Equal("Library scan", job.Title);
        Assert.Equal("Discovering files...", job.Message);
        Assert.Equal(20d, job.Percent);
        Assert.Equal(1, jobs.RunningCount);
    }

    [Fact]
    public void TheScan_ReportsEveryEnvelopeThroughTheJob()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        var jobs = new JobRegistry(action => action());
        using var service = new ScanStatusService(coordinator, action => action(), jobs: jobs);

        coordinator.RaiseProgress(ScanTaskNames.Discovery, 1, 5, "Discovering files...", force: true);
        coordinator.RaiseProgress(ScanTaskNames.Classify, 3, 10, "Classifying 3/10 files", force: true);
        coordinator.RaiseProgress(ScanTaskNames.Ingest, 20, 20, "Ingesting 20/20 files", force: true);

        // One job for the run, not one per envelope.
        var job = Assert.Single(jobs.Running);
        Assert.Equal("Ingesting 20/20 files", job.Message);
        Assert.Equal(100d, job.Percent);
    }

    [Fact]
    public void TheScansJob_CarriesNoPercentWhenTotalStepsIsZero()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        var jobs = new JobRegistry(action => action());
        using var service = new ScanStatusService(coordinator, action => action(), jobs: jobs);

        coordinator.RaiseProgress(ScanTaskNames.Discovery, 0, 0, "Discovering files...", force: true);

        var job = Assert.Single(jobs.Running);
        Assert.Null(job.Percent);
        Assert.False(job.HasPercent);
    }

    // Phase 15A fixer, review P2-2 and spec 10.9: "the registered phd2_correlate job ends with
    // outcome failed rather than succeeded, carrying the same reason as its one-line summary". The
    // Data-layer coordinator's only channel to this seam is the progress envelope, so a failed
    // correlation reports a terminal one carrying Phd2CorrelationEvents.FailedEnvelopeTotalSteps
    // and the reason as its message. The other half is pinned in Phd2ScanCorrelationTests, which
    // asserts that a throwing pass really raises that envelope.
    [Fact]
    public void APhd2PhaseThatReportsItsFailingEnvelope_FinishesItsSubJobFailed()
    {
        // A failure looks like: a correlation that threw halfway and filled nothing reads as a
        // finished, SUCCESSFUL "Correlating PHD2 guiding" entry in the flyout, with no
        // phd2_correlation_complete row in the feed and a guiding column that never filled, and
        // the only trace of any of it is one line in the application log.
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        var jobs = new JobRegistry(action => action());
        using var service = new ScanStatusService(coordinator, action => action(), jobs: jobs);

        coordinator.RaiseProgress(ScanTaskNames.Phd2Correlate, 0, 4, "Correlating PHD2 guiding...", force: true);
        Assert.Contains(jobs.Running, job => job.Kind == ScanStatusService.Phd2CorrelateJobKind);

        coordinator.RaiseProgress(
            ScanTaskNames.Phd2Correlate, 0, Phd2CorrelationEvents.FailedEnvelopeTotalSteps,
            Phd2CorrelationEvents.FailedMessage("no such table: phd2_calibrations"), force: true);

        var phd2 = Assert.Single(
            jobs.Recent, job => job.Kind == ScanStatusService.Phd2CorrelateJobKind);
        Assert.Equal(JobResult.Failed, phd2.Result);
        Assert.Contains("no such table", phd2.Summary, StringComparison.Ordinal);

        // And the failing envelope opens nothing of its own: a phase that never registered a job
        // has no job to fail.
        Assert.DoesNotContain(jobs.Running, job => job.Kind == ScanStatusService.Phd2CorrelateJobKind);
    }

    [Fact]
    public async Task ScanFinished_FinishesTheJob()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        var jobs = new JobRegistry(action => action());
        using var service = new ScanStatusService(coordinator, action => action(), jobs: jobs);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Empty(jobs.Running);
        Assert.Equal(0, jobs.RunningCount);
        var job = Assert.Single(jobs.Recent);
        Assert.True(job.IsFinished);
        Assert.Equal(JobResult.Succeeded, job.Result);
        Assert.Equal(ScanStatusService.ScanJobSummary, job.Summary);
    }

    // questions.md Q4, as proposed: one job per ScanFinished, not one spanning a run and the
    // follow-up the coordinator queues behind it. The flyout's value is one outcome per run.
    [Fact]
    public async Task AQueuedFollowUpScan_OpensASecondJob()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        var jobs = new JobRegistry(action => action());
        using var service = new ScanStatusService(coordinator, action => action(), jobs: jobs);

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);
        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Empty(jobs.Running);
        Assert.Equal(2, jobs.Recent.Count);
        Assert.NotSame(jobs.Recent[0], jobs.Recent[1]);
        Assert.All(jobs.Recent, job => Assert.Equal("Library scan", job.Title));
    }

    // Section 6.1 fact 2: the constructor seeds IsRunning synchronously, but no ProgressChanged
    // for the run already in flight has arrived, so there is nothing to report on and no job is
    // opened for it. The next envelope opens one.
    [Fact]
    public async Task AServiceBuiltMidScan_OpensNoJobForTheRunAlreadyInFlight()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        var jobs = new JobRegistry(action => action());

        ScanStatusService? service = null;
        var seededIsRunning = false;
        var runningJobsAtConstruction = -1;
        coordinator.ProgressChanged += (_, _) =>
        {
            if (service is not null) return;
            service = new ScanStatusService(coordinator, action => action(), jobs: jobs);
            seededIsRunning = service.IsRunning;
            runningJobsAtConstruction = jobs.Running.Count;
        };

        await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.True(seededIsRunning);
        Assert.Equal(0, runningJobsAtConstruction);
        service!.Dispose();
    }

    // Phase 14B fixer, fixer list item 19 (task2-review P3). This used to reach JobViewModel's
    // private _cancel field by reflection, pinning a field name no published contract covers. The
    // rule is in two halves and both are behavioural now: the registry runs the delegate it was
    // registered with when the job's own command is pressed (below), and the scan's job registers
    // one and it reaches the coordinator without throwing (here).
    [Fact]
    public void TheScansJob_OffersACancelThatReachesTheCoordinator()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        var jobs = new JobRegistry(action => action());
        using var service = new ScanStatusService(coordinator, action => action(), jobs: jobs);

        coordinator.RaiseProgress(ScanTaskNames.Discovery, 1, 5, "Discovering files...", force: true);

        var job = Assert.Single(jobs.Running);
        Assert.True(job.CanCancel);
        Assert.True(job.CancelCommand.CanExecute(null));

        // The coordinator refuses a cancel with no run in flight rather than throwing, which is
        // the same thing the status bar's own CancelButton relies on.
        job.CancelCommand.Execute(null);

        Assert.True(job.CanCancel);
    }

    [Fact]
    public void AJobsCancelCommand_RunsTheDelegateItWasRegisteredWith()
    {
        var jobs = new JobRegistry(action => action());
        var cancels = 0;
        using var handle = jobs.Begin("scan", "Scanning the library", cancel: () => cancels++);

        var job = Assert.Single(jobs.Running);
        job.CancelCommand.Execute(null);

        Assert.Equal(1, cancels);
    }

    [Fact]
    public void AServiceBuiltWithNoRegistry_DoesNotThrow()
    {
        var coordinator = ScanCoordinatorTestFactory.CreateBare();
        using var service = new ScanStatusService(coordinator, action => action());

        coordinator.RaiseProgress(ScanTaskNames.Discovery, 1, 5, "Discovering files...", force: true);

        Assert.True(service.IsRunning);
        Assert.Equal("Discovering files...", service.Message);
    }

    // ---- Phase 14B Task 5, coordinator override (Task 2 escalation 2) --------------------------
    //
    // ScanCoordinator used to raise ScanFinished with EventArgs.Empty for a completed, a cancelled
    // and a failed run alike, so the scan's registry job always finished Succeeded and a cancelled
    // scan read Succeeded in the status bar's recent list. The event now carries the run's outcome
    // and this type maps it. Appended, not reordered: the eight cases above are Task 2's.

    [Fact]
    public async Task ACompletedScan_FinishesTheJobSucceeded()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        var jobs = new JobRegistry(action => action());
        using var service = new ScanStatusService(coordinator, action => action(), jobs: jobs);

        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        Assert.Equal("complete", outcome.State);
        var job = Assert.Single(jobs.Recent);
        Assert.Equal(JobResult.Succeeded, job.Result);
        Assert.Equal(ScanStatusService.ScanJobSummary, job.Summary);
    }

    [Fact]
    public async Task ACancelledScan_FinishesTheJobCancelled()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        var jobs = new JobRegistry(action => action());
        using var service = new ScanStatusService(coordinator, action => action(), jobs: jobs);

        // Cancelled before the run reaches spec 10.5's between-phases checkpoint, which is a real
        // cancellation through the coordinator's own linked token rather than a faked event.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, cts.Token);

        Assert.Equal("cancelled", outcome.State);
        var job = Assert.Single(jobs.Recent);
        Assert.True(job.IsFinished);
        Assert.Equal(JobResult.Cancelled, job.Result);
        Assert.Equal(ScanStatusService.ScanJobCancelledSummary, job.Summary);
    }

    [Fact]
    public async Task AFailedScan_FinishesTheJobFailed()
    {
        using var settings = new SettingsFixture();
        settings.Save(general => general with { ScanRoots = [settings.Root] });
        var coordinator = ScanCoordinatorTestFactory.Create(settings);
        var jobs = new JobRegistry(action => action());
        using var service = new ScanStatusService(coordinator, action => action(), jobs: jobs);

        // The fault lands after the forced discovery envelope has opened the job and before the
        // run can finish: the known-file load is the first read of the images table.
        using (var context = new GalactiLogContext(
                   GalactiLogContextOptions.Create(settings.ConnectionString, tracking: true)))
        {
            context.Database.ExecuteSqlRaw("DROP TABLE images");
        }

        await Assert.ThrowsAnyAsync<Exception>(
            () => coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None));

        var job = Assert.Single(jobs.Recent);
        Assert.True(job.IsFinished);
        Assert.Equal(JobResult.Failed, job.Result);
        Assert.Equal(ScanStatusService.ScanJobFailedSummary, job.Summary);
    }

    // The three summaries are distinct sentences, so the recent list says which of the three
    // happened rather than only colouring it.
    [Fact]
    public void TheThreeSummaries_AreDistinct()
    {
        var summaries = new[]
        {
            ScanStatusService.ScanJobSummary,
            ScanStatusService.ScanJobCancelledSummary,
            ScanStatusService.ScanJobFailedSummary,
        };

        Assert.Equal(3, summaries.Distinct(StringComparer.Ordinal).Count());
        Assert.All(summaries, summary => Assert.False(string.IsNullOrWhiteSpace(summary)));
    }
}
