using System.Text.Json;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// Phase 7 Task 6. Spec 9.7's retry action, and the roadmap Verify line it owns: the retry clears
// negative rows and assigns frames for a name made resolvable by a newly loaded catalog, and a
// still-unresolvable name is re-negative-cached.
//
// The resolver is a delegate, so every outcome is driven with no network and no catalogs
// directory (spec 18.2). The stub writes the resolver's own negative row where the real
// TargetResolver would (spec 9.6), because "re-negative-cached" is a claim about the resolver's
// behaviour surviving the clear, not about this service writing one.
public class UnresolvedRetryTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();

    private static readonly DateOnly SessionDate = new(2025, 3, 15);

    public void Dispose() => _db.Dispose();

    private string Cs => _db.ConnectionString;

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(Cs));

    private CatalogCacheRepository Cache() => new(Cs);

    // ---- helpers -------------------------------------------------------------------------

    private static ResolvedIdentity Identity(string primaryName)
        => new(primaryName, null, null, null, null, null, []);

    private static TargetResolver.ResolutionResult Unresolved()
        => new(null, TargetResolver.ResolutionStage.Unresolved, null);

    private static TargetResolver.ResolutionResult Resolved(Guid targetId)
        => new(targetId, TargetResolver.ResolutionStage.Simbad, Identity("resolved"), Created: true);

    private static TargetResolver.ResolutionResult NetworkFailure()
        => new(null, TargetResolver.ResolutionStage.Unresolved, null, TransientNetworkFailure: true);

    private void AddFrames(string? rawHeaders, int count, string imageType = "LIGHT", Guid? targetId = null)
    {
        for (var i = 0; i < count; i++)
        {
            LibrarySeeder.AddFrame(Cs, targetId, SessionDate, image =>
            {
                image.RawHeaders = rawHeaders;
                image.ImageType = imageType;
            });
        }
    }

    private void AddNamedFrames(string objectName, int count, string imageType = "LIGHT", Guid? targetId = null)
        => AddFrames(LibrarySeeder.RawHeadersWithObject(objectName), count, imageType, targetId);

    private void AddCacheRow(string source, string key, string? payload)
        => Cache().Save(source, key, payload);

    private List<CatalogCacheEntry> CacheRows()
    {
        using var context = OpenRead();
        return [.. context.CatalogCacheEntries.OrderBy(row => row.Source).ThenBy(row => row.Key)];
    }

    private int FrameCountOf(Guid targetId)
    {
        using var context = OpenRead();
        return context.Images.Count(image => image.ResolvedTargetId == targetId);
    }

    private List<ActivityEvent> Events()
    {
        using var context = OpenRead();
        return [.. context.ActivityEvents.OrderBy(row => row.Id)];
    }

    // Every outcome is a function of the name alone, so the stub is a name -> answer map. A name
    // with no entry resolves to nothing and writes its own negative row, which is exactly what
    // TargetResolver does for a clean no-match from every source (spec 9.6).
    private sealed class ResolverStub(CatalogCacheRepository cache)
    {
        public Dictionary<string, Func<TargetResolver.ResolutionResult>> Answers { get; } =
            new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        public Action<string>? AfterCall { get; set; }

        public TargetResolver.ResolutionResult Resolve(string name, CancellationToken ct)
        {
            Calls.Add(name);
            TargetResolver.ResolutionResult answer;
            if (Answers.TryGetValue(name, out var supplier))
            {
                answer = supplier();
            }
            else
            {
                cache.Save("resolver", NameNormalizer.Normalize(name), null);
                answer = Unresolved();
            }

            AfterCall?.Invoke(name);
            return answer;
        }
    }

    private UnresolvedRetry.RetryOutcome Run(
        ResolverStub stub, Action<int, int, string>? report = null, CancellationToken ct = default,
        bool scanRunning = false)
        => new UnresolvedRetry(Cs, Cache(), stub.Resolve, () => scanRunning ? null : new NoopLease())
            .Run(report ?? ((_, _, _) => { }), ct);

    // A scan already running is the lease refusing to issue; the real coordinator's own lease is
    // exercised against a real ScanCoordinator further down.
    private sealed class NoopLease : IDisposable
    {
        public void Dispose() { }
    }

    // A real coordinator over the same database, for the two-way gate test. No roots are
    // configured and none are needed: the refusal happens in RunGuardedAsync, before the
    // pipeline reads any setting. The catalog clients throw if anything reaches for the network.
    private ScanCoordinator MakeCoordinator()
        => new(
            Cs,
            new SettingsStore(new SettingsRepository(Cs)),
            new TargetResolver(
                Cs, StaticCatalogLoader.ResolveCatalogsDirectory(), Cache(),
                new SimbadClient(new ThrowingHandler()), new SesameClient(new ThrowingHandler())),
            new ScanRunRepository(Cs),
            NullLogger<ScanCoordinator>.Instance);

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException($"network access attempted: {request.RequestUri}");
    }

    // ---- the cache clear -----------------------------------------------------------------

    [Fact]
    public void Run_ClearsEveryNegativeCacheRow()
    {
        // Spec 9.7 says "every negative cache row", not the resolver source's alone.
        AddCacheRow("resolver", "zzyzx blob 42", null);
        AddCacheRow("simbad", "zzyzx blob 42", null);
        AddCacheRow("sesame", "something else", null);

        var stub = new ResolverStub(Cache());
        var outcome = Run(stub);

        Assert.Equal(3, outcome.NegativeRowsCleared);
        Assert.Empty(CacheRows());
    }

    [Fact]
    public void Run_LeavesPositiveCacheRowsAlone()
    {
        // Spec 9.6: positive rows are "cleared only by the reset-database action".
        AddCacheRow("simbad", "m 31", "{\"name\":\"M 31\"}");
        AddCacheRow("resolver", "zzyzx blob 42", null);

        var outcome = Run(new ResolverStub(Cache()));

        Assert.Equal(1, outcome.NegativeRowsCleared);
        var row = Assert.Single(CacheRows());
        Assert.Equal("simbad", row.Source);
        Assert.False(row.Negative);
    }

    // ---- the roadmap Verify line ---------------------------------------------------------

    [Fact]
    public void Run_AssignsFramesForANameMadeResolvableByANewlyLoadedCatalog()
    {
        AddNamedFrames("Zzyzx Blob 42", 4);
        AddCacheRow("resolver", "zzyzx blob 42", null);
        var target = LibrarySeeder.AddTarget(Cs, "Zzyzx Blob");

        var stub = new ResolverStub(Cache());
        stub.Answers["Zzyzx Blob 42"] = () => Resolved(target.Id);

        var outcome = Run(stub);

        Assert.Equal(1, outcome.NamesResolved);
        Assert.Equal(4, outcome.FramesAssigned);
        Assert.Equal(4, FrameCountOf(target.Id));
        Assert.Empty(CacheRows());
    }

    // Phase 7 Task 7's sixth ResolutionStage, which this switch must handle or throw
    // NotSupportedException and fail the whole run. It is the same arm as any other created
    // target, and it is this run that rescues a solar-system name an older negative cache row was
    // hiding, because Run clears every such row before the loop.
    [Fact]
    public void Run_SolarSystemStage_AssignsTheFramesLikeAnyCreatedTarget()
    {
        AddNamedFrames("Jupiter", 3);
        AddCacheRow("resolver", "jupiter", null);
        var target = LibrarySeeder.AddTarget(Cs, "Jupiter", row =>
        {
            row.ObjectType = "Planet";
            row.UserDefined = true;
        });

        var stub = new ResolverStub(Cache());
        stub.Answers["Jupiter"] = () => new TargetResolver.ResolutionResult(
            target.Id, TargetResolver.ResolutionStage.SolarSystem, Identity("Jupiter"), Created: true);

        var outcome = Run(stub);

        Assert.Equal(1, outcome.NamesResolved);
        Assert.Equal(0, outcome.NamesStillUnresolved);
        Assert.Equal(3, outcome.FramesAssigned);
        Assert.Equal(3, FrameCountOf(target.Id));
    }

    [Fact]
    public void Run_StillUnresolvableName_IsReNegativeCached()
    {
        AddNamedFrames("Zzyzx Blob 42", 2);
        AddCacheRow("resolver", "zzyzx blob 42", null);

        // No stub answer: the resolver writes its own negative row, the way it does for a clean
        // no-match from every source consulted (spec 9.6). This service writes none.
        //
        // The limit of this test, stated plainly: the row it asserts was written by the stub, so
        // what is pinned here is that UnresolvedRetry clears the pre-existing row and then leaves
        // the resolver's own write alone -- never that TargetResolver writes one. That rule is
        // TargetResolverTests' (spec 9.6: only a clean no-match from every source consulted is
        // negative-cached), and driving the real resolver here would need its catalogs directory
        // and its online clients, which spec 18.2 keeps out of these tests.
        var outcome = Run(new ResolverStub(Cache()));

        Assert.Equal(1, outcome.NamesStillUnresolved);
        var row = Assert.Single(CacheRows());
        Assert.Equal("resolver", row.Source);
        Assert.Equal("ZZYZX BLOB 42", row.Key);
        Assert.True(row.Negative);
    }

    // ---- the frame assignment ------------------------------------------------------------

    [Fact]
    public void Run_AssignsANumericObjectCardsFrames()
    {
        // An unquoted numeric card (Phase 5 fix F1): the assignment compares against the group
        // key expression, which forces both sides to text.
        AddFrames("{\"OBJECT\":7331}", 3);
        var target = LibrarySeeder.AddTarget(Cs, "NGC 7331");

        var stub = new ResolverStub(Cache());
        stub.Answers["7331"] = () => Resolved(target.Id);

        var outcome = Run(stub);

        Assert.Equal(3, outcome.FramesAssigned);
        Assert.Equal(3, FrameCountOf(target.Id));
    }

    [Fact]
    public void Run_LeavesCalibrationFramesUnassigned()
    {
        AddNamedFrames("Zzyzx Blob 42", 2);
        AddNamedFrames("Zzyzx Blob 42", 5, imageType: "DARK");
        var target = LibrarySeeder.AddTarget(Cs, "Zzyzx Blob");

        var stub = new ResolverStub(Cache());
        stub.Answers["Zzyzx Blob 42"] = () => Resolved(target.Id);

        var outcome = Run(stub);

        Assert.Equal(2, outcome.FramesAssigned);
        Assert.Equal(2, FrameCountOf(target.Id));
    }

    [Fact]
    public void Run_LeavesAlreadyResolvedFramesAlone()
    {
        var other = LibrarySeeder.AddTarget(Cs, "M 31");
        AddNamedFrames("Zzyzx Blob 42", 3, targetId: other.Id);
        AddNamedFrames("Zzyzx Blob 42", 2);
        var target = LibrarySeeder.AddTarget(Cs, "Zzyzx Blob");

        var stub = new ResolverStub(Cache());
        stub.Answers["Zzyzx Blob 42"] = () => Resolved(target.Id);

        var outcome = Run(stub);

        Assert.Equal(2, outcome.FramesAssigned);
        Assert.Equal(3, FrameCountOf(other.Id));
        Assert.Equal(2, FrameCountOf(target.Id));
    }

    // ---- counts, progress and failures ---------------------------------------------------

    [Fact]
    public void Run_ReportsTheCounts()
    {
        AddNamedFrames("resolvable", 4);
        AddNamedFrames("hopeless", 1);
        AddCacheRow("resolver", "hopeless", null);
        var target = LibrarySeeder.AddTarget(Cs, "Something");

        var stub = new ResolverStub(Cache());
        stub.Answers["resolvable"] = () => Resolved(target.Id);

        var outcome = Run(stub);

        Assert.Equal(1, outcome.NegativeRowsCleared);
        Assert.Equal(2, outcome.NamesExamined);
        Assert.Equal(1, outcome.NamesResolved);
        Assert.Equal(4, outcome.FramesAssigned);
        Assert.Equal(1, outcome.NamesStillUnresolved);
        Assert.False(outcome.StoppedOnNetworkFailure);
    }

    [Fact]
    public void Run_TransientNetworkFailure_StopsTheLoop()
    {
        // Most frames first, so "first" is the name with four frames.
        AddNamedFrames("first", 4);
        AddNamedFrames("second", 1);

        var stub = new ResolverStub(Cache());
        stub.Answers["first"] = NetworkFailure;

        var outcome = Run(stub);

        Assert.True(outcome.StoppedOnNetworkFailure);
        Assert.Equal(1, outcome.NamesExamined);
        Assert.Equal(0, outcome.NamesStillUnresolved);
        Assert.Equal(["first"], stub.Calls);
    }

    [Fact]
    public void Run_NonTransientFailureOnOneName_SkipsThatNameAndContinues()
    {
        AddNamedFrames("first", 4);
        AddNamedFrames("second", 1);
        var target = LibrarySeeder.AddTarget(Cs, "Something");

        var stub = new ResolverStub(Cache());
        stub.Answers["first"] = () => throw new NonTransientCatalogException("400 Bad Request");
        stub.Answers["second"] = () => Resolved(target.Id);

        var outcome = Run(stub);

        Assert.False(outcome.StoppedOnNetworkFailure);
        Assert.Equal(2, outcome.NamesExamined);
        Assert.Equal(1, outcome.NamesResolved);
        Assert.Equal(1, outcome.FramesAssigned);
    }

    [Fact]
    public void Run_Cancellation_StopsAndKeepsWhatItAssigned()
    {
        AddNamedFrames("first", 4);
        AddNamedFrames("second", 1);
        var target = LibrarySeeder.AddTarget(Cs, "Something");

        using var cancellation = new CancellationTokenSource();
        var stub = new ResolverStub(Cache());
        stub.Answers["first"] = () => Resolved(target.Id);
        stub.AfterCall = _ => cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => Run(stub, ct: cancellation.Token));

        // The first name's frames were committed before the cancellation was noticed.
        Assert.Equal(4, FrameCountOf(target.Id));
        Assert.Equal(["first"], stub.Calls);
    }

    [Fact]
    public void Run_ReportsProgressOncePerName()
    {
        AddNamedFrames("first", 4);
        AddNamedFrames("second", 1);

        var reports = new List<(int Step, int Total, string Message)>();
        Run(new ResolverStub(Cache()), (step, total, message) => reports.Add((step, total, message)));

        Assert.Equal(2, reports.Count);
        Assert.Equal((1, 2), (reports[0].Step, reports[0].Total));
        Assert.Equal((2, 2), (reports[1].Step, reports[1].Total));
        Assert.Equal("Retrying 1/2 unresolved names...", reports[0].Message);
    }

    // ---- the scan gate ---------------------------------------------------------------------

    [Fact]
    public void Run_WhileAScanIsRunning_RefusesAndWritesNothing()
    {
        // A scan owns resolution and the negative cache for its whole run (spec 5.1, 9.6). The
        // refusal is here, at the choke point, so Phase 9's Maintenance button and Phase 10's
        // Diagnostics button cannot each forget it.
        AddNamedFrames("Zzyzx Blob 42", 4);
        AddCacheRow("resolver", "zzyzx blob 42", null);
        var target = LibrarySeeder.AddTarget(Cs, "Zzyzx Blob");

        var stub = new ResolverStub(Cache());
        stub.Answers["Zzyzx Blob 42"] = () => Resolved(target.Id);

        var outcome = Run(stub, scanRunning: true);

        Assert.Equal(UnresolvedRetry.RetryStatus.ScanInProgress, outcome.Status);
        Assert.Equal(0, outcome.NegativeRowsCleared);
        Assert.Equal(0, outcome.NamesExamined);

        // Nothing was touched: the negative row is still there, the resolver was never called,
        // the frames never moved, and no activity event was written.
        Assert.Single(CacheRows());
        Assert.Empty(stub.Calls);
        Assert.Equal(0, FrameCountOf(target.Id));
        Assert.Empty(Events());
    }

    [Fact]
    public async Task Run_HoldsTheResolutionLease_SoAScanRequestedMeanwhileIsRefused()
    {
        // Phase 7 fixer item 1 (phase finding 1): the gate used to be one-way. The retry asked
        // "is a scan running" once, and nothing stopped a scan from starting underneath it, which
        // is the same two-writer hazard (two TargetResolvers creating targets, one of them over a
        // negative cache the other just cleared) approached from the other side.
        AddNamedFrames("Zzyzx Blob 42", 4);
        var target = LibrarySeeder.AddTarget(Cs, "Zzyzx Blob");

        var coordinator = MakeCoordinator();
        Task<ScanRunOutcome>? scan = null;

        var stub = new ResolverStub(Cache());
        stub.Answers["Zzyzx Blob 42"] = () => Resolved(target.Id);
        // Requested from inside the retry's loop, which is exactly the window the old gate left
        // open. RunGuardedAsync answers synchronously before its first await, so the task is
        // already complete by the time the retry returns.
        stub.AfterCall = _ => scan = coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

        var retry = new UnresolvedRetry(Cs, Cache(), stub.Resolve, coordinator.TryBeginResolution);
        var outcome = await Task.Run(() => retry.Run((_, _, _) => { }, CancellationToken.None));

        Assert.Equal(UnresolvedRetry.RetryStatus.Completed, outcome.Status);
        Assert.Equal(1, outcome.NamesResolved);

        var scanOutcome = await scan!;
        Assert.Equal(ScanRunOutcome.AlreadyRunning, scanOutcome);

        // No second resolver ran: the scan never got as far as a scan_runs row, so nothing but
        // the retry touched resolution.
        using var context = OpenRead();
        Assert.Empty(context.ScanRuns);

        // And the lease is released when Run returns, so the next scan is free to start.
        Assert.NotNull(coordinator.TryBeginResolution());
    }

    [Fact]
    public void Run_WithNoScanRunning_ReportsCompleted()
        => Assert.Equal(UnresolvedRetry.RetryStatus.Completed, Run(new ResolverStub(Cache())).Status);

    [Fact]
    public void Run_EmitsAUserActionActivityEvent()
    {
        AddNamedFrames("resolvable", 4);
        var target = LibrarySeeder.AddTarget(Cs, "Something");

        var stub = new ResolverStub(Cache());
        stub.Answers["resolvable"] = () => Resolved(target.Id);

        var outcome = Run(stub);

        // Ruling Q21: one user_action event per run, carrying the counts.
        var evt = Assert.Single(Events(), row => row.EventType == "retry_unresolved");
        Assert.Equal("user_action", evt.Category);
        Assert.Equal("info", evt.Severity);

        // snake_case, like every other details document in this solution.
        using var details = JsonDocument.Parse(evt.Details!);
        Assert.Equal(outcome.NamesExamined, details.RootElement.GetProperty("names_examined").GetInt32());
        Assert.Equal(outcome.FramesAssigned, details.RootElement.GetProperty("frames_assigned").GetInt32());
    }
}
