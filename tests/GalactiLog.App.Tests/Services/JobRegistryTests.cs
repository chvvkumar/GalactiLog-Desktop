using System.Reflection;
using GalactiLog.App.Services;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Spec 12's job registry (PAR-015, ruling D1). Plain xunit, no window: the registry owns no
// thread, no database handle and no dispatcher of its own, so every case below drives it through
// the same post seam production uses, with a synchronous post so each mutation is visible
// immediately.
public class JobRegistryTests
{
    private static JobRegistry NewRegistry() => new(action => action());

    [Fact]
    public void Begin_AddsARunningJob_AndRaisesTheCount()
    {
        var registry = NewRegistry();
        var raised = new List<string?>();
        registry.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var handle = registry.Begin("scan", "Library scan");

        Assert.NotNull(handle);
        var job = Assert.Single(registry.Running);
        Assert.Equal("scan", job.Kind);
        Assert.Equal("Library scan", job.Title);
        Assert.False(job.IsFinished);
        Assert.Equal(1, registry.RunningCount);
        Assert.Contains(nameof(JobRegistry.RunningCount), raised);
        Assert.Empty(registry.Recent);
    }

    [Fact]
    public void TwoConcurrentJobs_AreListedSeparately_EachWithItsOwnProgress()
    {
        var registry = NewRegistry();

        var scan = registry.Begin("scan", "Library scan");
        var prune = registry.Begin("prune_activity", "Prune activity log");

        Assert.Equal(2, registry.Running.Count);
        Assert.Equal(2, registry.RunningCount);

        scan.Report("Classifying 3/10 files", 30d);
        prune.Report("Pruning...");

        Assert.Equal("Classifying 3/10 files", registry.Running[0].Message);
        Assert.Equal(30d, registry.Running[0].Percent);
        Assert.Equal("Pruning...", registry.Running[1].Message);
        Assert.Null(registry.Running[1].Percent);
    }

    [Fact]
    public void Report_UpdatesTheMessageAndPercent_OnTheRightJob()
    {
        var registry = NewRegistry();
        var first = registry.Begin("scan", "Library scan");
        registry.Begin("prune_activity", "Prune activity log");

        first.Report("Ingesting 5/20 files", 25d);

        Assert.Equal("Ingesting 5/20 files", registry.Running[0].Message);
        Assert.Equal(25d, registry.Running[0].Percent);
        Assert.True(registry.Running[0].HasPercent);
        Assert.Equal("", registry.Running[1].Message);
    }

    [Fact]
    public void Report_WithNoPercent_LeavesHasPercentFalse()
    {
        var registry = NewRegistry();
        var handle = registry.Begin("scan", "Library scan");

        handle.Report("Discovering files...");

        var job = Assert.Single(registry.Running);
        Assert.Null(job.Percent);
        Assert.False(job.HasPercent);
        Assert.Equal("Discovering files...", job.Message);
    }

    [Fact]
    public void Finish_MovesTheJobToRecent_AndDropsTheRunningCount()
    {
        var registry = NewRegistry();
        var handle = registry.Begin("scan", "Library scan");

        handle.Finish(JobResult.Succeeded, "Scanned 40 files.");

        Assert.Empty(registry.Running);
        Assert.Equal(0, registry.RunningCount);
        var job = Assert.Single(registry.Recent);
        Assert.True(job.IsFinished);
    }

    [Fact]
    public void AFinishedJob_KeepsItsOutcomeAndSummary()
    {
        var registry = NewRegistry();
        registry.Begin("rebuild_targets", "Rebuild targets")
            .Finish(JobResult.Failed, "The action could not be completed.");

        var job = Assert.Single(registry.Recent);
        Assert.Equal(JobResult.Failed, job.Result);
        Assert.Equal("The action could not be completed.", job.Summary);
        Assert.True(job.IsFinished);
    }

    [Fact]
    public void AFinishedJob_KeepsItsLastProgressMessage()
    {
        var registry = NewRegistry();
        var handle = registry.Begin("scan", "Library scan");
        handle.Report("Ingesting 20/20 files", 100d);

        handle.Finish(JobResult.Succeeded, "Scanned 20 files.");

        var job = Assert.Single(registry.Recent);
        Assert.Equal("Ingesting 20/20 files", job.Message);
        Assert.Equal("Scanned 20 files.", job.Summary);
    }

    [Fact]
    public void Recent_IsNewestFirst()
    {
        var registry = NewRegistry();
        registry.Begin("scan", "First").Finish(JobResult.Succeeded, "one");
        registry.Begin("scan", "Second").Finish(JobResult.Succeeded, "two");
        registry.Begin("scan", "Third").Finish(JobResult.Succeeded, "three");

        Assert.Equal(["Third", "Second", "First"], registry.Recent.Select(job => job.Title));
    }

    [Fact]
    public void Recent_IsCappedAtTen_AndTheEleventhPushesTheOldestOut()
    {
        var registry = NewRegistry();
        Assert.Equal(10, JobRegistry.RecentCap);

        for (var index = 1; index <= 11; index++)
        {
            registry.Begin("scan", $"Job {index}").Finish(JobResult.Succeeded, "done");
        }

        Assert.Equal(JobRegistry.RecentCap, registry.Recent.Count);
        Assert.Equal("Job 11", registry.Recent[0].Title);
        Assert.Equal("Job 2", registry.Recent[^1].Title);
        Assert.DoesNotContain(registry.Recent, job => job.Title == "Job 1");
    }

    [Fact]
    public void Recent_IsNotClearedOnATimerOrByAnyCommand()
    {
        // Structural, not temporal: a case that waited for a timer that does not exist would pass
        // whether or not one did. The rule is that the registry owns no clock and offers no way to
        // empty the list, so its own declared surface is what the assertion reads.
        var registry = NewRegistry();
        registry.Begin("scan", "Library scan").Finish(JobResult.Succeeded, "done");

        var declared = typeof(JobRegistry)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(member => member.Name)
            .ToArray();

        Assert.DoesNotContain(declared, name => name.Contains("Clear", StringComparison.Ordinal));
        Assert.DoesNotContain(declared, name => name.Contains("Timer", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(JobRegistry).GetFields(BindingFlags.NonPublic | BindingFlags.Instance),
            field => field.FieldType.Name.Contains("Timer", StringComparison.Ordinal));

        Assert.Single(registry.Recent);
    }

    [Fact]
    public void Dispose_OnAnUnfinishedHandle_FinishesItAsSucceeded()
    {
        var registry = NewRegistry();
        var handle = registry.Begin("scan", "Library scan");
        handle.Report("Discovering files...");

        handle.Dispose();

        Assert.Empty(registry.Running);
        Assert.Equal(0, registry.RunningCount);
        var job = Assert.Single(registry.Recent);
        Assert.Equal(JobResult.Succeeded, job.Result);
        Assert.Equal("", job.Summary);
        Assert.True(job.IsFinished);
    }

    [Fact]
    public void Dispose_OnAFinishedHandle_DoesNothing()
    {
        var registry = NewRegistry();
        var handle = registry.Begin("scan", "Library scan");
        handle.Finish(JobResult.Failed, "It failed.");

        handle.Dispose();

        var job = Assert.Single(registry.Recent);
        Assert.Equal(JobResult.Failed, job.Result);
        Assert.Equal("It failed.", job.Summary);
    }

    [Fact]
    public void CancelCommand_IsDisabledWhenTheJobRegisteredNoCancelDelegate()
    {
        var registry = NewRegistry();
        registry.Begin("prune_activity", "Prune activity log");

        var job = Assert.Single(registry.Running);
        Assert.False(job.CanCancel);
        Assert.False(job.CancelCommand.CanExecute(null));

        // And the body refuses too, not only the affordance.
        job.CancelCommand.Execute(null);
    }

    [Fact]
    public void CancelCommand_InvokesTheCancelDelegateOnce()
    {
        var registry = NewRegistry();
        var cancels = 0;
        registry.Begin("scan", "Library scan", () => cancels++);

        var job = Assert.Single(registry.Running);
        Assert.True(job.CanCancel);
        Assert.True(job.CancelCommand.CanExecute(null));

        job.CancelCommand.Execute(null);

        Assert.Equal(1, cancels);
    }

    [Fact]
    public void CancelCommand_OnAFinishedJob_DoesNothing_EvenWhenExecutedDirectly()
    {
        // TRACKING.md section 6 item 13: RelayCommand.Execute ignores CanExecute, so the gate is
        // repeated in the body and this case presses past the affordance to prove it held.
        var registry = NewRegistry();
        var cancels = 0;
        var handle = registry.Begin("scan", "Library scan", () => cancels++);
        handle.Finish(JobResult.Succeeded, "done");

        var job = Assert.Single(registry.Recent);
        Assert.False(job.CanCancel);
        Assert.False(job.CancelCommand.CanExecute(null));

        job.CancelCommand.Execute(null);

        Assert.Equal(0, cancels);
    }

    [Fact]
    public void EveryMutation_HappensInsideThePostedClosure()
    {
        // The shape ScanStatusServiceTests.ProgressChanged_EveryMutation_HappensInsideThePostedClosure
        // already uses: collect the closures instead of running them, and assert nothing moved.
        var posted = new List<Action>();
        var registry = new JobRegistry(posted.Add);

        var handle = registry.Begin("scan", "Library scan");
        Assert.Empty(registry.Running);
        Assert.Equal(0, registry.RunningCount);
        posted[0]();
        Assert.Single(registry.Running);
        Assert.Equal(1, registry.RunningCount);

        handle.Report("Classifying 3/10 files", 30d);
        Assert.Equal("", registry.Running[0].Message);
        posted[1]();
        Assert.Equal("Classifying 3/10 files", registry.Running[0].Message);
        Assert.Equal(30d, registry.Running[0].Percent);

        handle.Finish(JobResult.Succeeded, "Scanned 10 files.");
        Assert.Single(registry.Running);
        Assert.Empty(registry.Recent);
        posted[2]();
        Assert.Empty(registry.Running);
        Assert.Equal(0, registry.RunningCount);
        Assert.Single(registry.Recent);

        Assert.Equal(3, posted.Count);
    }

    [Fact]
    public void Begin_FromABackgroundThread_DoesNotThrow()
    {
        var posted = new List<Action>();
        var registry = new JobRegistry(action =>
        {
            lock (posted)
            {
                posted.Add(action);
            }
        });

        // A plain thread rather than Task.Run, so the case stays synchronous: it is asserting that
        // Begin is callable off the dispatcher, not anything about the task scheduler.
        JobHandle? handle = null;
        Exception? thrown = null;
        var worker = new Thread(() =>
        {
            try
            {
                handle = registry.Begin("scan", "Library scan");
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)));

        Assert.Null(thrown);
        Assert.NotNull(handle);
        Assert.Single(posted);

        foreach (var closure in posted)
        {
            closure();
        }

        Assert.Single(registry.Running);
    }

    [Fact]
    public void TheRegistry_RefusesNothing_AndSerializesNothing()
    {
        var posted = new List<Action>();
        var registry = new JobRegistry(action =>
        {
            lock (posted)
            {
                posted.Add(action);
            }
        });

        var handles = new JobHandle[50];
        Parallel.For(0, handles.Length, index =>
        {
            handles[index] = registry.Begin("scan", $"Job {index}");
        });

        Assert.All(handles, Assert.NotNull);

        List<Action> drained;
        lock (posted)
        {
            drained = [.. posted];
        }

        foreach (var closure in drained)
        {
            closure();
        }

        Assert.Equal(handles.Length, registry.Running.Count);
        Assert.Equal(handles.Length, registry.RunningCount);
    }
}
