using System.IO;
using System.Text.RegularExpressions;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// The roadmap's Phase 9 row 7 first Verify clause: "rebuild targets performs no network call",
// plus spec 12.7's rebuild rules and TRACKING.md section 6 item 15's "deletes no targets row".
//
// Database rows only. No file is written and no HTTP handler is ever reached: the one test that
// uses a real TargetResolver hands it a handler that records and throws, and asserts it was never
// called.
public class TargetRebuildTests : IDisposable
{
    private readonly TestDatabaseHandle _db;

    public TargetRebuildTests() => _db = TestDatabaseFactory.CreateMigratedDatabase();

    public void Dispose() => _db.Dispose();

    // ---- fixtures ------------------------------------------------------------------------

    private sealed class Lease : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    // Records every outbound request and refuses it, so "was the network reached" is an assertion
    // rather than an inspection.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException($"network access attempted: {request.RequestUri}");
        }
    }

    private TargetRebuild Make(
        Func<string, CancellationToken, TargetResolver.ResolutionResult> resolve,
        Func<IDisposable?>? tryBeginResolution = null)
        => new(_db.ConnectionString, resolve, tryBeginResolution ?? (() => new Lease()), NullLogger.Instance);

    private static void Ignore(int step, int total, string message) { }

    private static TargetResolver.ResolutionResult Resolved(Guid targetId)
        => new(targetId, TargetResolver.ResolutionStage.Cache, null);

    private static readonly TargetResolver.ResolutionResult Unresolved =
        new(null, TargetResolver.ResolutionStage.Unresolved, null);

    private Target AddTarget(string name, Action<Target>? configure = null)
        => LibrarySeeder.AddTarget(_db.ConnectionString, name, configure);

    private Image AddFrame(Guid? targetId, string objectName, int day = 15)
        => LibrarySeeder.AddFrame(
            _db.ConnectionString, targetId, new DateOnly(2025, 3, day),
            image => image.RawHeaders = LibrarySeeder.RawHeadersWithObject(objectName));

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    private void AddCacheRow(string source, string key, string? payload)
        => new CatalogCacheRepository(_db.ConnectionString).Save(source, key, payload);

    // ---- the roadmap's first named assertion ----------------------------------------------

    [Fact]
    public void Run_PerformsNoNetworkCall()
    {
        // A real TargetResolver over real HTTP clients, bound exactly as AppHost binds it. The
        // names below are in no catalogue, so without skipOnline the resolver would reach SIMBAD
        // and then SESAME for every one of them.
        var handler = new RecordingHandler();
        var resolver = new TargetResolver(
            _db.ConnectionString,
            StaticCatalogLoader.ResolveCatalogsDirectory(),
            new CatalogCacheRepository(_db.ConnectionString),
            new SimbadClient(handler),
            new SesameClient(handler));

        AddFrame(null, "Zzyzx Blob 42");
        AddFrame(null, "Zzyzx Blob 43");

        var asked = 0;
        var rebuild = Make((name, ct) =>
        {
            asked++;
            return resolver.Resolve(name, createIfMissing: true, dryRun: false, skipOnline: true, ct: ct);
        });

        var outcome = rebuild.Run(Ignore, CancellationToken.None);

        // Non-vacuous: the loop really did ask for both names, and the network was still never
        // reached.
        Assert.Equal(2, asked);
        Assert.Equal(2, outcome.NamesExamined);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void AppHostBindsTheResolveDelegateWithSkipOnlineTrue()
    {
        // The companion to the case above. The delegate is what makes the no-network guarantee
        // true, so the production binding is asserted rather than assumed. Read from the
        // registration's source text, because GalactiLog.Data.Tests cannot reference
        // GalactiLog.App and building the whole host here would be a far larger fixture.
        var appHost = Path.Combine(FindRepoRoot(), "src", "GalactiLog.App", "AppHost.cs");
        Assert.True(File.Exists(appHost), $"AppHost.cs not found at {appHost}");

        var text = File.ReadAllText(appHost);
        var registration = Regex.Match(text, @"new TargetRebuild\((?:.|\n)*?\)\);");
        Assert.True(registration.Success, "no TargetRebuild registration found in AppHost.cs");
        Assert.Contains("skipOnline: true", registration.Value, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found.");
    }

    // ---- the unassignment ------------------------------------------------------------------

    [Fact]
    public void Run_ClearsResolvedTargetIdForNonUserDefinedTargets()
    {
        var target = AddTarget("NGC 7331");
        var frame = AddFrame(target.Id, "NGC7331");

        var outcome = Make((_, _) => Unresolved).Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.FramesUnassigned);
        using var context = OpenRead();
        Assert.Null(context.Images.Single(image => image.Id == frame.Id).ResolvedTargetId);
    }

    [Fact]
    public void Run_KeepsFrameAssignmentsForUserDefinedTargets()
    {
        var mine = AddTarget("Jupiter", row => row.UserDefined = true);
        var theirs = AddTarget("NGC 7331");
        var kept = AddFrame(mine.Id, "Jupiter");
        var cleared = AddFrame(theirs.Id, "NGC7331");

        var outcome = Make((_, _) => Unresolved).Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.FramesUnassigned);
        using var context = OpenRead();
        Assert.Equal(mine.Id, context.Images.Single(image => image.Id == kept.Id).ResolvedTargetId);
        Assert.Null(context.Images.Single(image => image.Id == cleared.Id).ResolvedTargetId);
    }

    [Fact]
    public void Run_KeepsFrameAssignmentsForCalibrationFrames()
    {
        // Review finding I2. ScanWriter assigns a target to any frame carrying an OBJECT card,
        // calibration included, but UnresolvedObjects.AssignFrames restores LIGHT frames only. A
        // rebuild that unassigned a calibration frame would orphan it permanently.
        var target = AddTarget("NGC 7331");
        var light = AddFrame(target.Id, "NGC7331", 1);
        var dark = LibrarySeeder.AddFrame(
            _db.ConnectionString, target.Id, new DateOnly(2025, 3, 2),
            image =>
            {
                image.ImageType = "DARK";
                image.RawHeaders = LibrarySeeder.RawHeadersWithObject("NGC7331");
            });

        var outcome = Make((_, _) => Unresolved).Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.FramesUnassigned);
        using var context = OpenRead();
        Assert.Null(context.Images.Single(image => image.Id == light.Id).ResolvedTargetId);
        Assert.Equal(target.Id, context.Images.Single(image => image.Id == dark.Id).ResolvedTargetId);
    }

    [Fact]
    public void Run_DeletesEveryMergeCandidate()
    {
        LibrarySeeder.AddMergeCandidate(_db.ConnectionString, "NGC7331 field");
        LibrarySeeder.AddMergeCandidate(_db.ConnectionString, "M31 field");

        var outcome = Make((_, _) => Unresolved).Run(Ignore, CancellationToken.None);

        Assert.Equal(2, outcome.CandidatesCleared);
        using var context = OpenRead();
        Assert.Empty(context.MergeCandidates);
    }

    // ---- TRACKING.md section 6 item 15 -----------------------------------------------------

    [Fact]
    public void Run_DeletesNoTargetsRow()
    {
        var derived = AddTarget("NGC 7331");
        var mine = AddTarget("Jupiter", row => row.UserDefined = true);
        var orphaned = AddTarget("M 31");
        AddFrame(derived.Id, "NGC7331");

        Make((_, _) => Unresolved).Run(Ignore, CancellationToken.None);

        using var context = OpenRead();
        Assert.Equal(
            new[] { derived.Id, mine.Id, orphaned.Id }.OrderBy(id => id),
            context.Targets.Select(row => row.Id).ToList().OrderBy(id => id));
    }

    [Fact]
    public void Run_LeavesMergeManifestsIntact()
    {
        var (winner, loser, manifestId, _) = SeedAMerge();

        Make((_, _) => Resolved(winner.Id)).Run(Ignore, CancellationToken.None);

        using var context = OpenRead();
        var manifest = context.MergeManifests.Single(row => row.Id == manifestId);
        Assert.Equal(winner.Id, manifest.WinnerId);
        Assert.Equal(loser.Id, manifest.LoserId);
    }

    [Fact]
    public void Run_AnUndoStillWorksAfterARebuild()
    {
        // The assertion item 15 actually cares about: the web's DELETE FROM targets would have
        // cascaded this manifest away, and the frames it restores would be unrecoverable.
        var (winner, loser, _, frameId) = SeedAMerge();

        // The rebuild unassigns the frame and re-resolves its OBJECT name back onto the winner,
        // which is where the merge left it, so the manifest's own restore condition still holds.
        Make((_, _) => Resolved(winner.Id)).Run(Ignore, CancellationToken.None);

        var unmerge = new MergeRepository(new DatabaseConnectionString(_db.ConnectionString))
            .Unmerge(loser.Id);

        Assert.Equal(UnmergeStatus.Unmerged, unmerge.Status);
        Assert.Equal(1, unmerge.FramesRestored);

        using var context = OpenRead();
        Assert.Equal(loser.Id, context.Images.Single(image => image.Id == frameId).ResolvedTargetId);
        Assert.Null(context.Targets.Single(row => row.Id == loser.Id).MergedIntoId);
    }

    // Winner "M 31", loser "Andromeda" with one LIGHT frame carrying OBJECT=Andromeda, merged.
    private (Target Winner, Target Loser, Guid ManifestId, Guid FrameId) SeedAMerge()
    {
        var winner = AddTarget("M 31");
        var loser = AddTarget("Andromeda");
        var frame = AddFrame(loser.Id, "Andromeda");

        var merge = new MergeRepository(new DatabaseConnectionString(_db.ConnectionString))
            .Merge(winner.Id, loser.Id);

        Assert.Equal(MergeStatus.Merged, merge.Status);
        Assert.NotNull(merge.ManifestId);
        return (winner, loser, merge.ManifestId!.Value, frame.Id);
    }

    // ---- the negative cache ------------------------------------------------------------------

    [Fact]
    public void Run_ClearsNegativeCacheRows_AndKeepsPositiveOnes()
    {
        AddCacheRow("resolver", "zzyzx blob 42", null);
        AddCacheRow("simbad", "m 31", "{\"name\":\"M 31\"}");

        Make((_, _) => Unresolved).Run(Ignore, CancellationToken.None);

        var cache = new CatalogCacheRepository(_db.ConnectionString);
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, cache.Get("resolver", "zzyzx blob 42").Kind);
        Assert.Equal(CatalogCacheRepository.CacheHit.Positive, cache.Get("simbad", "m 31").Kind);
    }

    // ---- the loop ----------------------------------------------------------------------------

    [Fact]
    public void Run_ReResolvesEachDistinctObjectName_MostFramesFirst()
    {
        AddFrame(null, "One", 1);
        AddFrame(null, "Three", 2);
        AddFrame(null, "Three", 3);
        AddFrame(null, "Three", 4);
        AddFrame(null, "Two", 5);
        AddFrame(null, "Two", 6);

        var seen = new List<string>();
        var outcome = Make((name, _) =>
        {
            seen.Add(name);
            return Unresolved;
        }).Run(Ignore, CancellationToken.None);

        Assert.Equal(new[] { "Three", "Two", "One" }, seen);
        Assert.Equal(3, outcome.NamesExamined);
    }

    [Fact]
    public void Run_AssignsFramesForANameThatNowResolves()
    {
        var target = AddTarget("NGC 7331");
        AddFrame(null, "NGC7331", 1);
        AddFrame(null, "NGC7331", 2);

        var outcome = Make((_, _) => Resolved(target.Id)).Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.NamesResolved);
        Assert.Equal(2, outcome.FramesAssigned);
        Assert.Equal(0, outcome.NamesStillUnresolved);

        using var context = OpenRead();
        Assert.All(context.Images.ToList(), image => Assert.Equal(target.Id, image.ResolvedTargetId));
    }

    [Fact]
    public void Run_ANameThatStillDoesNotResolve_IsCountedAndSkipped()
    {
        AddFrame(null, "Zzyzx Blob 42");

        var outcome = Make((_, _) => Unresolved).Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.NamesExamined);
        Assert.Equal(0, outcome.NamesResolved);
        Assert.Equal(0, outcome.FramesAssigned);
        Assert.Equal(1, outcome.NamesStillUnresolved);
    }

    [Fact]
    public void Run_APerNameException_IsCountedAndTheLoopContinues()
    {
        var target = AddTarget("NGC 7331");
        AddFrame(null, "Bad", 1);
        AddFrame(null, "NGC7331", 2);
        AddFrame(null, "NGC7331", 3);

        var outcome = Make((name, _) => name == "Bad"
            ? throw new NonTransientCatalogException("the catalog service rejected the query")
            : Resolved(target.Id)).Run(Ignore, CancellationToken.None);

        Assert.Equal(2, outcome.NamesExamined);
        Assert.Equal(1, outcome.NamesResolved);
        Assert.Equal(2, outcome.FramesAssigned);
        Assert.Equal(1, outcome.NamesStillUnresolved);
    }

    [Fact]
    public void Run_ReportsProgressEveryFiveNamesAndOnTheLast()
    {
        for (var i = 1; i <= 6; i++)
        {
            AddFrame(null, $"Name {i:00}", i);
        }

        var steps = new List<int>();
        var totals = new List<int>();
        Make((_, _) => Unresolved).Run(
            (step, total, _) =>
            {
                steps.Add(step);
                totals.Add(total);
            },
            CancellationToken.None);

        // Six names: the fifth (every five) and the sixth (the last).
        Assert.Equal(new[] { 5, 6 }, steps);
        Assert.Equal(new[] { 6, 6 }, totals);
    }

    [Fact]
    public void Run_NoUnresolvedNames_ReportsOnceAndDoesNothing()
    {
        var outcome = Make((_, _) => Unresolved).Run(
            (step, total, message) => Assert.Equal("No unresolved names to rebuild", message),
            CancellationToken.None);

        Assert.Equal(0, outcome.NamesExamined);
    }

    // ---- the lease ---------------------------------------------------------------------------

    [Fact]
    public void Run_WhileAScanIsRunning_ReturnsScanInProgress_AndTouchesNothing()
    {
        var target = AddTarget("NGC 7331");
        var frame = AddFrame(target.Id, "NGC7331");
        LibrarySeeder.AddMergeCandidate(_db.ConnectionString, "NGC7331 field");
        AddCacheRow("resolver", "zzyzx blob 42", null);

        var outcome = Make(
            (_, _) => throw new InvalidOperationException("the loop must not run"),
            tryBeginResolution: () => null).Run(Ignore, CancellationToken.None);

        Assert.Equal(TargetRebuild.RebuildStatus.ScanInProgress, outcome.Status);
        Assert.Equal(0, outcome.FramesUnassigned);
        Assert.Equal(0, outcome.CandidatesCleared);
        Assert.Equal(0, outcome.NamesExamined);

        using var context = OpenRead();
        Assert.Equal(target.Id, context.Images.Single(image => image.Id == frame.Id).ResolvedTargetId);
        Assert.Single(context.MergeCandidates);
        Assert.Equal(
            CatalogCacheRepository.CacheHit.Negative,
            new CatalogCacheRepository(_db.ConnectionString).Get("resolver", "zzyzx blob 42").Kind);
    }

    [Fact]
    public void Run_ReleasesTheLease()
    {
        var lease = new Lease();
        Make((_, _) => Unresolved, () => lease).Run(Ignore, CancellationToken.None);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void Run_Cancelled_KeepsWhatItCommitted()
    {
        var first = AddTarget("First");
        AddFrame(null, "First", 1);
        AddFrame(null, "First", 2);
        AddFrame(null, "Second", 3);

        using var cancellation = new CancellationTokenSource();
        var rebuild = Make((name, _) =>
        {
            // Cancelled while the first name is in flight: its assignment still commits, and the
            // check at the top of the next iteration is what ends the run.
            cancellation.Cancel();
            Assert.Equal("First", name);
            return Resolved(first.Id);
        });

        Assert.Throws<OperationCanceledException>(
            () => rebuild.Run(Ignore, cancellation.Token));

        using var context = OpenRead();
        // "First" was committed before the cancellation was noticed; "Second" was not reached.
        Assert.Equal(2, context.Images.Count(image => image.ResolvedTargetId == first.Id));
    }

    [Fact]
    public void Run_ANameLockedTarget_IsNotReDerived()
    {
        // smart_rebuild_targets' phase 4 rule: a user-renamed target is never re-derived. The
        // rebuild reaches it through TargetResolver, whose step 2 returns an existing target by
        // name and mutates no column, so the rename and the lock both survive.
        var locked = AddTarget("My Andromeda", row =>
        {
            row.NameLocked = true;
            row.CatalogId = "M 31";
        });
        AddFrame(locked.Id, "M31");

        Make((_, _) => Resolved(locked.Id)).Run(Ignore, CancellationToken.None);

        using var context = OpenRead();
        var row = context.Targets.Single(target => target.Id == locked.Id);
        Assert.Equal("My Andromeda", row.PrimaryName);
        Assert.True(row.NameLocked);
        Assert.Equal("M 31", row.CatalogId);

        // And no second target was created for the same frames: the rebuild reuses the row.
        Assert.Single(context.Targets);
        Assert.Equal(locked.Id, context.Images.Single().ResolvedTargetId);
    }
}
