using System.Text.Json;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using GalactiLog.Data;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.App.Tests.Services;

/// <summary>
/// Spec 12.7's out-of-scan correlation re-run (Phase 15A Task 6b, ruling F5). Brief section 8.4
/// cases 12 and 14, plus the coordinator's overrides on coalescing, scan deferral and the
/// activity trigger.
/// </summary>
/// <remarks>
/// Every case runs against a real migrated SQLite file and a real <see cref="JobRegistry"/>: the
/// whole reason the runner is a type rather than a lambda in <c>AppHost</c> is that its
/// registration can be proven behaviourally, and a faked registry would prove nothing.
/// </remarks>
public sealed class Phd2CorrelationRunnerTests : IDisposable
{
    private readonly TempDatabase _database = new("galactilog-phd2-runner");

    public void Dispose() => _database.Dispose();

    /// <summary>
    /// Brief case 14, and the out-of-scan half of census member eleven. A failure looks like the
    /// eleventh census member being declared while the path a settings save actually takes
    /// registers nothing, so a corpus-wide re-run happens with an empty job monitor.
    /// </summary>
    [Fact]
    public async Task ARun_OpensExactlyOneSnakeCaseJobUnderPhd2Correlate_AndFinishesIt()
    {
        var registry = new JobRegistry(action => action());
        var runner = NewRunner(registry);

        await runner.RunAsync();

        Assert.Empty(registry.Running);
        var job = Assert.Single(registry.Recent);
        Assert.Equal("phd2_correlate", job.Kind);
        Assert.Equal(ScanStatusService.Phd2CorrelateJobKind, job.Kind);
        Assert.Equal(JobResult.Succeeded, job.Result);
        Assert.True(job.IsFinished);
        Assert.NotEqual("", job.Summary);
    }

    /// <summary>
    /// Spec 10.9: the out-of-scan pass writes its own <c>phd2_correlation_complete</c>, with no
    /// parent and with <c>trigger</c> <c>settings_change</c>. A failure looks like a user saving a
    /// mapping and having nothing anywhere tell them it took effect, or the row claiming a scan
    /// ran when none did.
    /// </summary>
    [Fact]
    public async Task ARun_WritesPhd2CorrelationComplete_WithTheSettingsChangeTrigger()
    {
        await NewRunner(jobs: null).RunAsync();

        var row = Assert.Single(EventsOfType("phd2_correlation_complete"));
        Assert.Null(row.ParentId);
        Assert.Equal("scan", row.Category);
        Assert.Equal("info", row.Severity);

        using var details = JsonDocument.Parse(row.Details!);
        Assert.Equal("settings_change", details.RootElement.GetProperty("trigger").GetString());
        Assert.Equal(
            Phd2CorrelationTriggers.SettingsChange,
            details.RootElement.GetProperty("trigger").GetString());
        foreach (var key in new[] { "nights", "frames_considered", "filled", "cleared", "below_gate" })
        {
            Assert.True(details.RootElement.TryGetProperty(key, out _), key);
        }
    }

    /// <summary>
    /// The coordinator's scan-deferral override, and the reason it exists: the correlation writes
    /// the four <c>images</c> guiding columns, so running it while a scan owns the resolution
    /// lease would put two writers on the same rows. A failure looks like the pass starting
    /// anyway, which this case sees as a job that finished while the lease was still refused.
    /// </summary>
    [Fact]
    public async Task ARun_WaitsForTheScanToReleaseTheLease_BeforeItRunsThePass()
    {
        var registry = new JobRegistry(action => action());
        var lease = new GatedLease();
        var runner = NewRunner(registry, lease.TryBegin, TimeSpan.FromSeconds(30));

        var run = runner.RunAsync();
        await WaitUntil(() => lease.Attempts >= 2);

        // Refused so far: the job is open and reporting, and nothing has been written or
        // finished.
        Assert.Empty(registry.Recent);
        Assert.Empty(EventsOfType("phd2_correlation_complete"));

        lease.Release();
        await run;

        Assert.Single(registry.Recent);
        Assert.Single(EventsOfType("phd2_correlation_complete"));
        Assert.True(lease.Released, "the run must give the lease back, or every later scan wedges");
    }

    /// <summary>
    /// The coordinator's coalescing override. Three saves while one re-run is waiting on the scan
    /// produce that run and exactly one more, not three more. A failure looks like a job per
    /// keystroke on an autosaving field, and a queue of corpus-wide correlations behind it.
    /// </summary>
    [Fact]
    public async Task ChangesArrivingWhileARunIsInFlight_ProduceExactlyOneFurtherRun()
    {
        var registry = new JobRegistry(action => action());
        var lease = new GatedLease();
        var runner = NewRunner(registry, lease.TryBegin, TimeSpan.FromSeconds(30));

        runner.Queue();
        await WaitUntil(() => lease.Attempts >= 2);

        runner.Queue();
        runner.Queue();
        runner.Queue();

        lease.Release();
        await runner.InFlight;

        Assert.Empty(registry.Running);
        Assert.Equal(2, registry.Recent.Count);
        Assert.All(registry.Recent, job => Assert.Equal(ScanStatusService.Phd2CorrelateJobKind, job.Kind));
    }

    /// <summary>
    /// The coordinator's "reads the map at run time, not at subscription time" override. A failure
    /// looks like the re-run correlating against the map as it stood when the host was built, so
    /// the save the user just made is the one thing the pass cannot see.
    /// </summary>
    [Fact]
    public async Task ThePass_ReadsTheSettingsAtRunTime_NotAtConstruction()
    {
        var reads = 0;
        var runner = new Phd2CorrelationRunner(
            _database.ConnectionString,
            () => { reads++; return new GeneralSettings(); },
            () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()),
            () => new GatedLease().TryBeginOpen());

        Assert.Equal(0, reads);

        await runner.RunAsync();
        Assert.Equal(1, reads);

        await runner.RunAsync();
        Assert.Equal(2, reads);
    }

    /// <summary>
    /// The coordinator's "a throw is caught, logged, finishes the job as failed and never takes
    /// the process down" override. The pump runs on a pool thread, so an escaping exception is an
    /// unobserved task exception at best and a dead process at worst. The connection string here
    /// points at a directory that does not exist, which is the honest way to make the real pass
    /// throw without faking it.
    /// </summary>
    [Fact]
    public async Task AThrowingPass_IsLogged_AndFinishesTheJobFailed()
    {
        var registry = new JobRegistry(action => action());
        var logger = new RecordingLogger();
        var runner = new Phd2CorrelationRunner(
            $"Data Source={Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "no", "such.db")}",
            () => new GeneralSettings(),
            () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()),
            () => new GatedLease().TryBeginOpen(),
            jobs: registry,
            logger: logger);

        runner.Queue();
        await runner.InFlight;

        var job = Assert.Single(registry.Recent);
        Assert.Equal(JobResult.Failed, job.Result);
        Assert.Contains(
            logger.Entries,
            entry => entry.Message.Contains("correlation re-run failed", StringComparison.Ordinal));
    }

    /// <summary>
    /// Task 6b review P1-1. <c>JobRegistry.Begin</c> posts to the dispatcher, and a post against a
    /// dispatcher that is shutting down can throw; the pump's own loop is what must survive it. A
    /// failure looks like one throw faulting the pump task that nothing observes and leaving
    /// <c>_running</c> true for the life of the process, so every later settings save is coalesced
    /// into a pump that is already dead: no job row, no activity event, no log line, and the
    /// correlation re-run is gone until the application is restarted.
    /// </summary>
    [Fact]
    public async Task AThrowingRegistryPost_DoesNotWedgeThePump_AndTheNextChangeStillRuns()
    {
        var throwOnce = 1;
        var registry = new JobRegistry(action =>
        {
            if (Interlocked.Exchange(ref throwOnce, 0) == 1)
            {
                throw new InvalidOperationException("the dispatcher is shutting down");
            }

            action();
        });
        var runner = NewRunner(registry);

        runner.Queue();
        await runner.InFlight;

        // The first pass lost its job to the throwing post and wrote nothing. Nothing escaped to
        // an unobserved task: awaiting the pump above did not throw.
        Assert.Empty(EventsOfType("phd2_correlation_complete"));

        runner.Queue();
        await runner.InFlight;

        Assert.Single(EventsOfType("phd2_correlation_complete"));
        Assert.Single(registry.Recent);
    }

    /// <summary>
    /// Task 6b review P2-2. <c>JobViewModel.CanCancel</c> is cleared inside the registry's own
    /// dispatcher post, which runs after the pass has returned, so a cancel click in that window
    /// reaches the delegate with the flag still true. A failure looks like
    /// <c>ObjectDisposedException</c> thrown on the UI thread out of a <c>RelayCommand</c>,
    /// which is the hazard TRACKING section 6 item 13 exists for and which
    /// <c>JobViewModel.Cancel</c>'s own body guard cannot catch because the flag has not been
    /// cleared yet.
    /// </summary>
    [Fact]
    public async Task TheJobsCancelDelegate_IsSafeAfterThePassHasFinished()
    {
        // A post that queues instead of running, which is what a real dispatcher does. It lets
        // the pass finish while the registry's finish closure is still waiting to run, which is
        // exactly the window a cancel click lands in.
        var queued = new List<Action>();
        var registry = new JobRegistry(queued.Add);

        await NewRunner(registry).RunAsync();

        // Everything the pass posted except its finish: the job is running and cancellable, and
        // the source the delegate closes over has already been disposed by the returning pass.
        Assert.NotEmpty(queued);
        for (var i = 0; i < queued.Count - 1; i++)
        {
            queued[i]();
        }

        var job = Assert.Single(registry.Running);
        Assert.True(job.CanCancel);

        job.CancelCommand.Execute(null);
    }

    /// <summary>
    /// Review P2-2 and the phase review's loss sequence 2: the out-of-scan re-run never read
    /// <c>general.phd2_scan_enabled</c>, so a settings save re-derived every stored session time
    /// and ran a full clear-and-refill of the <c>images</c> guiding columns for a feature the user
    /// had turned off, writing <c>phd2</c> values a fresh scan under the same settings would never
    /// produce. A failure looks like exactly that, with a job in the flyout announcing it.
    /// </summary>
    [Fact]
    public async Task WithTheKeyOff_ARunDoesNothingAndRegistersNoJob()
    {
        var registry = new JobRegistry(action => action());
        var runner = NewRunner(registry, general: new GeneralSettings { Phd2ScanEnabled = false });

        runner.Queue();
        await runner.InFlight;

        Assert.Empty(registry.Running);
        Assert.Empty(registry.Recent);
        Assert.Empty(EventsOfType("phd2_correlation_complete"));
        Assert.Empty(EventsOfType("phd2_correlation_failed"));
    }

    /// <summary>
    /// Spec 10.9's <c>phd2_correlation_failed</c> on the out-of-scan path: parentless, because
    /// this pass belongs to no scan, and carrying the same reason the job's summary carries. A
    /// failure looks like a user saving a mapping, the re-run dying, and the only trace of it
    /// being a line in the application log.
    /// </summary>
    [Fact]
    public async Task AThrowingPass_WritesPhd2CorrelationFailed_WithTheSettingsChangeTrigger()
    {
        // The throw is real rather than injected, and it happens in the real database so the
        // failure row has somewhere to go: the table RederiveSessionTimes walks is dropped, which
        // is how a corrupt or partly migrated database fails the very first thing the pass does.
        using (var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(_database.ConnectionString, tracking: true)))
        {
            context.Database.ExecuteSqlRaw("DROP TABLE phd2_calibrations");
        }

        var registry = new JobRegistry(action => action());
        var runner = NewRunner(registry);

        runner.Queue();
        await runner.InFlight;

        var row = Assert.Single(EventsOfType("phd2_correlation_failed"));
        Assert.Null(row.ParentId);
        Assert.Equal("error", row.Severity);
        using var details = JsonDocument.Parse(row.Details!);
        Assert.Equal(
            Phd2CorrelationTriggers.SettingsChange,
            details.RootElement.GetProperty("trigger").GetString());
        Assert.NotEqual("", details.RootElement.GetProperty("reason").GetString());

        // Exactly one of complete and failed per pass that ran to an end.
        Assert.Empty(EventsOfType("phd2_correlation_complete"));

        var job = Assert.Single(registry.Recent);
        Assert.Equal(JobResult.Failed, job.Result);
        Assert.Equal(
            Phd2CorrelationRunner.FailedSummary(details.RootElement.GetProperty("reason").GetString()!),
            job.Summary);
    }

    /// <summary>
    /// Spec 7.6's "The obligation survives a crash": only a pass that COMPLETED discharges it. A
    /// failure looks like the flag being cleared by a pass that threw, which is loss sequence 3
    /// with one extra step: the obligation is dropped and the nights the pass never reached keep
    /// values the saved settings do not produce.
    /// </summary>
    [Fact]
    public async Task OnlyACompletedPass_ClearsTheDurableObligation()
    {
        var cleared = 0;
        await NewRunner(jobs: null, clearCorrelationPending: _ => cleared++).RunAsync();
        Assert.Equal(1, cleared);

        await NewThrowingRunner(jobs: null, _ => cleared++).RunAsync();
        Assert.Equal(1, cleared);
    }

    /// <summary>
    /// Fix-wave review P1-1, on the path it was found on. A save that lands while a pass is in
    /// flight must not be discharged by that pass, and the coalesced successor, which does see it,
    /// is what discharges it.
    /// </summary>
    /// <remarks>
    /// A failure looks like the phase review's loss sequence 3 re-opened inside the mechanism built
    /// to close it: the newer change's obligation survives only in the runner's in-process
    /// coalescing flag, so quitting before the second pass finishes loses it for good, and for a
    /// telescope re-map nothing brings it back. The lease is what makes the interleaving
    /// deterministic: the pass reads its snapshot before it waits, so the save lands strictly
    /// between the snapshot and the clear.
    /// </remarks>
    [Fact]
    public async Task ASaveThatLandsWhileAPassWaitsOnTheLease_IsNotDischargedByThatPass()
    {
        var store = new SettingsStore(new SettingsRepository(_database.ConnectionString));
        store.MutateGeneral(general => general with { ObserverTimezone = "UTC" });
        Assert.True(store.GetGeneral().Phd2CorrelationPending);

        var lease = new GatedLease();
        var runner = new Phd2CorrelationRunner(
            _database.ConnectionString,
            store.GetGeneral,
            () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()),
            lease.TryBegin,
            leaseBudget: TimeSpan.FromSeconds(30),
            clearCorrelationPending: observed => store.ClearCorrelationPendingIfUnchanged(observed));

        var run = runner.RunAsync();
        await WaitUntil(() => lease.Attempts >= 2);

        // The snapshot is already taken; this save is the one the pass will never see.
        store.MutateGeneral(general => general with { ObserverLatitude = 51.4779 });

        lease.Release();
        await run;

        // The pass completed and wrote its row, and the obligation is still owed.
        Assert.Single(EventsOfType("phd2_correlation_complete"));
        Assert.True(store.GetGeneral().Phd2CorrelationPending);

        // The successor, which reads the document the save left, discharges it.
        await runner.RunAsync();
        Assert.False(store.GetGeneral().Phd2CorrelationPending);
    }

    /// <summary>
    /// Fix-wave review P3-5, spec 10.9's "exactly one of complete and failed per pass that ran to
    /// an end". <c>Finish</c> posts to the dispatcher, and this type's own comment says a post
    /// against a dispatcher that is shutting down is not guaranteed to be silent, so a throwing
    /// <c>Finish</c> used to land in the general catch and write <c>phd2_correlation_failed</c> for
    /// a pass that had already written its complete row.
    /// </summary>
    /// <remarks>
    /// A failure looks like a user shutting the application down mid-pass and finding two
    /// contradictory rows about one correlation in the feed, one saying it finished and one saying
    /// it failed, with no way to tell which is true.
    /// </remarks>
    [Fact]
    public async Task AFinishThatThrowsAfterTheCompleteRow_DoesNotAlsoWriteTheFailedRow()
    {
        // Over an empty database the pass posts exactly three times: Begin, the one terminal
        // progress envelope of a pass with nothing to visit, and Finish. The third is the one
        // that must not be able to contradict the row already written.
        var posts = 0;
        var registry = new JobRegistry(action =>
        {
            if (Interlocked.Increment(ref posts) == 3)
            {
                throw new InvalidOperationException("the dispatcher is shutting down");
            }

            action();
        });

        var runner = NewRunner(registry);
        runner.Queue();
        await runner.InFlight;

        Assert.Single(EventsOfType("phd2_correlation_complete"));
        Assert.Empty(EventsOfType("phd2_correlation_failed"));
    }

    /// <summary>
    /// Phase 15B fixer items 30 and 38, with F2. Exactly one of the four terminal shapes raises
    /// <c>PassCompleted</c>, and the other three must not: a disabled pass wrote nothing at all, a
    /// cancelled pass visited a half of the night set, and a failed one may have written some of
    /// it and then stopped. The two the event is wrong for are the ones that matter, because a
    /// raise there would tell every open page to re-read figures that were not rewritten and, on a
    /// cancelled or failed pass, would read as a completion the durable obligation deliberately
    /// does not treat as one.
    /// </summary>
    /// <remarks>
    /// A failure looks like the Statistics page and the Target detail band re-reading a full
    /// library aggregate every time a re-run is cancelled from the job flyout, or, in the other
    /// direction, a reader who maps a rig, waits for the pass to finish and still sees the figures
    /// the old mapping produced until they restart the application.
    /// </remarks>
    [Fact]
    public async Task OnlyACompletedPass_RaisesPassCompleted()
    {
        var completed = NewRunner(jobs: null);
        var raised = 0;
        completed.PassCompleted += (_, _) => Interlocked.Increment(ref raised);
        await completed.RunAsync();
        Assert.Equal(1, raised);

        // Disabled: spec 10.3's key is off, so nothing in phd2_* was read or written.
        var disabled = NewRunner(jobs: null, general: new GeneralSettings { Phd2ScanEnabled = false });
        disabled.PassCompleted += (_, _) => Interlocked.Increment(ref raised);
        await disabled.RunAsync();
        Assert.Equal(1, raised);

        // Cancelled: a token already cancelled when the pass is asked to run, which is what the
        // job flyout's cancel delegate does to a pass already in flight.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var stopped = NewRunner(jobs: null);
        stopped.PassCompleted += (_, _) => Interlocked.Increment(ref raised);
        await stopped.RunAsync(cancelled.Token);
        Assert.Equal(1, raised);

        // Failed: the real pass throws, which is what the unreachable database produces.
        var failed = NewThrowingRunner(jobs: null);
        failed.PassCompleted += (_, _) => Interlocked.Increment(ref raised);
        await failed.RunAsync();
        Assert.Equal(1, raised);
    }

    private Phd2CorrelationRunner NewRunner(
        JobRegistry? jobs,
        Func<IDisposable?>? tryBeginLease = null,
        TimeSpan? leaseBudget = null,
        GeneralSettings? general = null,
        Action<GeneralSettings>? clearCorrelationPending = null)
        => new(
            _database.ConnectionString,
            () => general ?? new GeneralSettings(),
            () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()),
            tryBeginLease ?? (() => new GatedLease().TryBeginOpen()),
            jobs: jobs,
            leaseBudget: leaseBudget,
            clearCorrelationPending: clearCorrelationPending);

    // The connection string points at a directory that does not exist, which is the honest way to
    // make the real pass throw without faking it. The activity write goes to the same place, so
    // the row it tries to write is lost too; the cases that read a row use the real database and
    // the ones that use this read the job and the flag instead.
    private static Phd2CorrelationRunner NewThrowingRunner(
        JobRegistry? jobs, Action<GeneralSettings>? clearCorrelationPending = null)
        => new(
            $"Data Source={Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "no", "such.db")}",
            () => new GeneralSettings(),
            () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()),
            () => new GatedLease().TryBeginOpen(),
            jobs: jobs,
            logger: new RecordingLogger(),
            clearCorrelationPending: clearCorrelationPending);

    private List<ActivityEvent> EventsOfType(string eventType)
        => _database.Read(context => context.ActivityEvents
            .Where(row => row.EventType == eventType)
            .ToList());

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the runner never reached the expected state");
            await Task.Delay(20);
        }
    }

    /// <summary>A stand-in for <c>ScanCoordinator</c>'s resolution lease: refused until
    /// <see cref="Release"/>, then granted once and released on dispose.</summary>
    private sealed class GatedLease
    {
        private volatile bool _open;
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public bool Released { get; private set; }

        public void Release() => _open = true;

        public IDisposable? TryBegin()
        {
            Interlocked.Increment(ref _attempts);
            return _open ? new Handle(this) : null;
        }

        /// <summary>The idle coordinator: the lease is free on the first ask.</summary>
        public IDisposable? TryBeginOpen()
        {
            Release();
            return TryBegin();
        }

        private sealed class Handle(GatedLease owner) : IDisposable
        {
            public void Dispose() => owner.Released = true;
        }
    }
}
