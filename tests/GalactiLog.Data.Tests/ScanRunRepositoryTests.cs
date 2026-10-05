using System.Linq;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests;

// Spec 5.13: the durable half of scan state. Live state lives in memory on ScanCoordinator.
public class ScanRunRepositoryTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();

    public void Dispose() => _db.Dispose();

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    [Fact]
    public void Start_CreatesRunningRow_ReturnsId()
    {
        var repository = new ScanRunRepository(_db.ConnectionString);

        var id = repository.Start("watcher");

        Assert.True(id > 0);
        using var context = OpenRead();
        var run = context.ScanRuns.Single();
        Assert.Equal(id, run.Id);
        Assert.Equal("watcher", run.Trigger);
        Assert.Equal("running", run.State);
        Assert.Null(run.FinishedAt);
        Assert.Null(run.ErrorText);
    }

    [Fact]
    public void Complete_SetsStateCountersAndFinishedAt()
    {
        var repository = new ScanRunRepository(_db.ConnectionString);
        var id = repository.Start("manual");

        repository.Complete(id, "complete", discovered: 40, newFiles: 31, changedFiles: 2,
            completed: 30, failed: 1, skippedCalibration: 2, removed: 5);

        using var context = OpenRead();
        var run = context.ScanRuns.Single();
        Assert.Equal("complete", run.State);
        Assert.NotNull(run.FinishedAt);
        Assert.True(run.FinishedAt >= run.StartedAt);
        Assert.Equal(40, run.Discovered);
        Assert.Equal(31, run.NewFiles);
        Assert.Equal(2, run.ChangedFiles);
        Assert.Equal(30, run.Completed);
        Assert.Equal(1, run.Failed);
        Assert.Equal(2, run.SkippedCalibration);
        Assert.Equal(5, run.Removed);
        Assert.Null(run.ErrorText);
    }

    [Fact]
    public void Complete_Failed_SetsErrorText()
    {
        var repository = new ScanRunRepository(_db.ConnectionString);
        var id = repository.Start("cli");

        repository.Complete(id, "failed", 3, 3, 0, 1, 0, 0, 0, errorText: "disk went away");

        using var context = OpenRead();
        var run = context.ScanRuns.Single();
        Assert.Equal("failed", run.State);
        Assert.Equal("disk went away", run.ErrorText);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public void Complete_Cancelled_LeavesEarlierRunsUntouched()
    {
        var repository = new ScanRunRepository(_db.ConnectionString);
        var first = repository.Start("manual");
        repository.Complete(first, "complete", 1, 1, 0, 1, 0, 0, 0);
        var second = repository.Start("scheduler");

        repository.Complete(second, "cancelled", 9, 9, 0, 4, 0, 0, 0);

        using var context = OpenRead();
        var runs = context.ScanRuns.OrderBy(r => r.Id).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal("complete", runs[0].State);
        Assert.Equal("cancelled", runs[1].State);
        Assert.Equal(4, runs[1].Completed);
    }

    // FIXER LIST 5: the CLI's --json payload reads the row back through this, not through a
    // second copy of AppHost's connection-string expression.
    [Fact]
    public void Get_ReturnsTheRow_AndNullForAnUnknownId()
    {
        var repository = new ScanRunRepository(_db.ConnectionString);
        var id = repository.Start("cli");
        repository.Complete(id, "complete", 7, 7, 0, 7, 0, 0, 0);

        var run = repository.Get(id);

        Assert.NotNull(run);
        Assert.Equal(id, run!.Id);
        Assert.Equal("cli", run.Trigger);
        Assert.Equal("complete", run.State);
        Assert.Equal(7, run.Discovered);
        Assert.Null(repository.Get(id + 1000));
    }

    // Review item 3: rows left "running" by a crash, a kill, or a drain that timed out.
    [Fact]
    public void MarkInterrupted_ClosesRunningRows_AndLeavesTerminalOnesAlone()
    {
        var repository = new ScanRunRepository(_db.ConnectionString);
        var finished = repository.Start("manual");
        repository.Complete(finished, "complete", 1, 1, 0, 1, 0, 0, 0);
        var abandoned = repository.Start("watcher");
        var alsoAbandoned = repository.Start("scheduler");

        var closed = repository.MarkInterrupted();

        Assert.Equal(2, closed);
        using var context = OpenRead();
        var runs = context.ScanRuns.OrderBy(r => r.Id).ToDictionary(r => r.Id);

        Assert.Equal("complete", runs[finished].State);
        Assert.Null(runs[finished].ErrorText);

        foreach (var id in new[] { abandoned, alsoAbandoned })
        {
            Assert.Equal("failed", runs[id].State);
            Assert.Equal("interrupted", runs[id].ErrorText);
            Assert.NotNull(runs[id].FinishedAt);
        }
    }

    [Fact]
    public void MarkInterrupted_WithNothingRunning_ClosesNothing()
    {
        var repository = new ScanRunRepository(_db.ConnectionString);
        var id = repository.Start("cli");
        repository.Complete(id, "cancelled", 2, 2, 0, 1, 0, 0, 0);

        Assert.Equal(0, repository.MarkInterrupted());
        Assert.Equal(0, repository.MarkInterrupted());
    }

    // ---- Phase 10 Task 1: spec 12.8's Scan group "last run", and spec 16.3's last 50 runs ----

    [Fact]
    public void ScanRunRepository_Latest_ReturnsTheNewestRunByStartedAt()
    {
        var repository = new ScanRunRepository(_db.ConnectionString);

        Assert.Null(repository.Latest());

        var first = repository.Start("manual");
        var second = repository.Start("watcher");

        var latest = repository.Latest();

        Assert.NotNull(latest);

        // Two runs started in the same millisecond is a matter of clock resolution, so the order
        // is the pair (started_at DESC, id DESC) and the newer id wins the tie.
        Assert.Equal(second, latest.Id);
        Assert.NotEqual(first, latest.Id);
    }

    [Fact]
    public void ScanRunRepository_Recent_ReturnsNewestFirst_AndRespectsTheLimit()
    {
        var repository = new ScanRunRepository(_db.ConnectionString);
        var ids = Enumerable.Range(0, 5).Select(_ => repository.Start("manual")).ToList();

        var recent = repository.Recent(3);

        Assert.Equal(3, recent.Count);
        Assert.Equal(Enumerable.Reverse(ids).Take(3), recent.Select(run => run.Id));

        Assert.Equal(5, repository.Recent(50).Count);
        Assert.Empty(repository.Recent(0));
    }
}
