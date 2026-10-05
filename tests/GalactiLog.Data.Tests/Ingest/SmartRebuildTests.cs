using System.IO;
using System.Text.Json;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.Data.Tests.Ingest;

// Spec 12.7's smart rebuild (PAR-007) and the Phase 14B roadmap row 4 Verify clause: "each repair
// class on a seeded database; no network call (a resolver stub asserts none); counts reported;
// targets rows never deleted".
//
// Database rows only. No file is written and no HTTP handler is ever reached: the one case that
// uses a real TargetResolver hands it a handler that records and throws, and asserts it was never
// called.
public class SmartRebuildTests : IDisposable
{
    private readonly TestDatabaseHandle _db;

    public SmartRebuildTests() => _db = TestDatabaseFactory.CreateMigratedDatabase();

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

    private static readonly TargetResolver.ResolutionResult Unresolved =
        new(null, TargetResolver.ResolutionStage.Unresolved, null);

    private SmartRebuild Make(
        Func<string, bool, CancellationToken, TargetResolver.ResolutionResult>? resolve = null,
        Func<IDisposable?>? tryBeginResolution = null)
        => new(
            _db.ConnectionString,
            resolve ?? ((_, _, _) => Unresolved),
            tryBeginResolution ?? (() => new Lease()),
            NullLogger.Instance);

    private static void Ignore(int step, int total, string message) { }

    private Target AddTarget(string name, Action<Target>? configure = null)
        => LibrarySeeder.AddTarget(_db.ConnectionString, name, configure);

    private Image AddFrame(Guid? targetId, string objectName, int day = 15)
        => LibrarySeeder.AddFrame(
            _db.ConnectionString, targetId, new DateOnly(2025, 3, day),
            image => image.RawHeaders = LibrarySeeder.RawHeadersWithObject(objectName));

    private GalactiLogContext OpenRead() => new(GalactiLogContextOptions.Create(_db.ConnectionString));

    // The exact shape TargetResolver caches a SIMBAD or SESAME answer in: PascalCase keys, because
    // TargetResolver.Serialize uses JsonSerializer with no naming policy.
    private void AddCacheRow(string key, string mainId, params string[] aliases)
        => new CatalogCacheRepository(_db.ConnectionString).Save(
            "simbad",
            key,
            JsonSerializer.Serialize(new
            {
                MainId = mainId,
                ObjectType = "Galaxy",
                Ra = 10.68,
                Dec = 41.27,
                Aliases = aliases,
            }));

    private static string Aliases(params string[] aliases) => JsonSerializer.Serialize(aliases);

    private static IReadOnlyList<string> AliasesOf(Target target)
        => JsonSerializer.Deserialize<List<string>>(target.Aliases) ?? [];

    // ---- pass 1: merged-away redirect -----------------------------------------------------

    [Fact]
    public void Pass1_FramesPointingAtAMergedAwayTarget_AreRedirectedToTheWinner()
    {
        var winner = AddTarget("M 31");
        var loser = AddTarget("Andromeda", t => t.MergedIntoId = winner.Id);
        var frame = AddFrame(loser.Id, "Andromeda");

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.FramesRedirected);
        using var read = OpenRead();
        Assert.Equal(winner.Id, read.Images.Single(i => i.Id == frame.Id).ResolvedTargetId);
    }

    [Fact]
    public void Pass1_AChainOfTwoMerges_ResolvesToTheEndOfTheChain()
    {
        var end = AddTarget("M 31");
        var middle = AddTarget("Andromeda", t => t.MergedIntoId = end.Id);
        var start = AddTarget("Andromeda Galaxy", t => t.MergedIntoId = middle.Id);
        var frame = AddFrame(start.Id, "Andromeda Galaxy");

        Make().Run(Ignore, CancellationToken.None);

        using var read = OpenRead();
        Assert.Equal(end.Id, read.Images.Single(i => i.Id == frame.Id).ResolvedTargetId);
    }

    [Fact]
    public void Pass1_ACycleInMergedInto_DoesNotLoopForever()
    {
        // No writer in this application can produce this; a hand-edited database can, and the pass
        // must end rather than walk the ring.
        var first = AddTarget("A");
        var second = AddTarget("B");
        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true)))
        {
            context.Targets.Single(t => t.Id == first.Id).MergedIntoId = second.Id;
            context.Targets.Single(t => t.Id == second.Id).MergedIntoId = first.Id;
            context.SaveChanges();
        }

        var frame = AddFrame(first.Id, "A");

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(0, outcome.FramesRedirected);
        using var read = OpenRead();
        Assert.Equal(first.Id, read.Images.Single(i => i.Id == frame.Id).ResolvedTargetId);
    }

    // ---- pass 2: alias link ---------------------------------------------------------------

    [Fact]
    public void Pass2_AnUnresolvedObjectMatchingAnAlias_IsLinked()
    {
        var target = AddTarget("M 31", t => t.Aliases = Aliases("ANDROMEDA"));
        var frame = AddFrame(null, "Andromeda");

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.FramesLinkedByAlias);
        using var read = OpenRead();
        Assert.Equal(target.Id, read.Images.Single(i => i.Id == frame.Id).ResolvedTargetId);
    }

    [Fact]
    public void Pass2_TheAliasComparison_IsOnTheNormalizedUppercaseForm()
    {
        // Aliases are stored uppercase and panel-stripped, so a mixed-case OBJECT with a panel
        // suffix and a doubled space still matches.
        var target = AddTarget("M 31", t => t.Aliases = Aliases("ANDROMEDA GALAXY"));
        var frame = AddFrame(null, "  andromeda   galaxy Panel 2 ");

        Make().Run(Ignore, CancellationToken.None);

        using var read = OpenRead();
        Assert.Equal(target.Id, read.Images.Single(i => i.Id == frame.Id).ResolvedTargetId);
    }

    [Fact]
    public void Pass2_OnlyLightFramesMove()
    {
        AddTarget("M 31", t => t.Aliases = Aliases("ANDROMEDA"));
        var light = AddFrame(null, "Andromeda");
        var dark = LibrarySeeder.AddFrame(
            _db.ConnectionString, null, new DateOnly(2025, 3, 15),
            image =>
            {
                image.ImageType = "DARK";
                image.RawHeaders = LibrarySeeder.RawHeadersWithObject("Andromeda");
            });

        Make().Run(Ignore, CancellationToken.None);

        using var read = OpenRead();
        Assert.NotNull(read.Images.Single(i => i.Id == light.Id).ResolvedTargetId);
        Assert.Null(read.Images.Single(i => i.Id == dark.Id).ResolvedTargetId);
    }

    // ---- pass 3: alias backfill -----------------------------------------------------------

    [Fact]
    public void Pass3_AnObjectOnATargetsOwnFramesMissingFromItsAliases_IsAdded()
    {
        var target = AddTarget("M 31");
        AddFrame(target.Id, "Andromeda");

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.AliasesAdded);
        using var read = OpenRead();
        Assert.Contains("ANDROMEDA", AliasesOf(read.Targets.Single(t => t.Id == target.Id)));
    }

    [Fact]
    public void Pass3_AddsAnAliasEvenToANameLockedTarget()
    {
        // Spec 9.7 permits this write explicitly: a name-locked pass "may add an alias to it".
        var locked = AddTarget("Comet C/2026 X1", t => { t.NameLocked = true; t.UserDefined = true; });
        AddFrame(locked.Id, "Comet C-2026 X1");

        Make().Run(Ignore, CancellationToken.None);

        using var read = OpenRead();
        Assert.Contains("COMET C-2026 X1", AliasesOf(read.Targets.Single(t => t.Id == locked.Id)));
    }

    // ---- pass 4: identity re-derive -------------------------------------------------------

    [Fact]
    public void Pass4_APositiveCacheRow_ReDerivesTheThreeColumnsThroughTheCuration()
    {
        var target = AddTarget("M 31", t => t.Aliases = Aliases("M 31"));
        AddCacheRow("M 31", "M 31", "M 31", "NGC 224", "NAME Andromeda Galaxy");

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.IdentitiesReDerived);

        using var read = OpenRead();
        var stored = read.Targets.Single(t => t.Id == target.Id);

        // Exactly what a resolution would have produced, taken from the same helpers rather than
        // from a second expectation written by hand.
        var aliases = new[] { "M 31", "NGC 224", "NAME Andromeda Galaxy" };
        var catalogId = CatalogPriority.ExtractCatalogId(aliases, "M 31");
        var commonName = AliasCurator.ExtractCommonName(aliases);

        Assert.Equal(catalogId, stored.CatalogId);
        Assert.Equal(commonName, stored.CommonName);
        Assert.Equal(AliasCurator.BuildPrimaryName(catalogId, commonName), stored.PrimaryName);
        Assert.Equal(NameNormalizer.NormalizeCatalogId(catalogId), stored.CatalogIdNormalized);
    }

    // Phase 14B fixer, fixer list item 2 (Task 4 review escalation 2, ruled to the fixer). Pass 3
    // runs immediately before this one and appends every distinct normalized OBJECT seen on the
    // target's frames to its aliases, so an alias key here would let one mis-assigned frame
    // supply the cache row that rewrites a target's whole catalogue identity in the same run.
    // The key set is catalog_id or primary_name, web parity, and never an alias.
    [Fact]
    public void Pass4_ACacheRowMatchingOnlyAnAlias_IsIgnored()
    {
        var target = AddTarget("Sh2 155", t => t.Aliases = Aliases("Sh2 155", "M 31"));
        AddCacheRow("M 31", "M 31", "M 31", "NGC 224", "NAME Andromeda Galaxy");

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(0, outcome.IdentitiesReDerived);
        using var read = OpenRead();
        var stored = read.Targets.Single(t => t.Id == target.Id);
        Assert.Equal("Sh2 155", stored.PrimaryName);
        Assert.Null(stored.CatalogId);
        Assert.Null(stored.CommonName);
    }

    [Fact]
    public void Pass4_SkipsANameLockedTarget()
    {
        var target = AddTarget("M 31", t => { t.NameLocked = true; t.Aliases = Aliases("M 31"); });
        AddCacheRow("M 31", "M 31", "M 31", "NGC 224", "NAME Andromeda Galaxy");

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(0, outcome.IdentitiesReDerived);
        using var read = OpenRead();
        var stored = read.Targets.Single(t => t.Id == target.Id);
        Assert.Equal("M 31", stored.PrimaryName);
        Assert.Null(stored.CatalogId);
        Assert.Null(stored.CommonName);
    }

    [Fact]
    public void Pass4_SkipsAUserDefinedTarget()
    {
        var target = AddTarget("M 31", t => { t.UserDefined = true; t.Aliases = Aliases("M 31"); });
        AddCacheRow("M 31", "M 31", "M 31", "NGC 224", "NAME Andromeda Galaxy");

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(0, outcome.IdentitiesReDerived);
        using var read = OpenRead();
        Assert.Equal("M 31", read.Targets.Single(t => t.Id == target.Id).PrimaryName);
    }

    // ---- pass 5: name rebuild -------------------------------------------------------------

    [Fact]
    public void Pass5_ANameDisagreeingWithItsCatalogIdAndCommonName_IsRebuilt()
    {
        var target = AddTarget("some stale name", t =>
        {
            t.CatalogId = "NGC 7000";
            t.CommonName = "North America Nebula";
        });

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.NamesRebuilt);
        using var read = OpenRead();
        Assert.Equal(
            "NGC 7000 - North America Nebula",
            read.Targets.Single(t => t.Id == target.Id).PrimaryName);
    }

    [Fact]
    public void Pass5_SkipsANameLockedTarget()
    {
        var target = AddTarget("some stale name", t =>
        {
            t.NameLocked = true;
            t.CatalogId = "NGC 7000";
            t.CommonName = "North America Nebula";
        });

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(0, outcome.NamesRebuilt);
        using var read = OpenRead();
        Assert.Equal("some stale name", read.Targets.Single(t => t.Id == target.Id).PrimaryName);
    }

    [Fact]
    public void Pass5_SkipsAUserDefinedTarget()
    {
        var target = AddTarget("some stale name", t =>
        {
            t.UserDefined = true;
            t.CatalogId = "NGC 7000";
            t.CommonName = "North America Nebula";
        });

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(0, outcome.NamesRebuilt);
        using var read = OpenRead();
        Assert.Equal("some stale name", read.Targets.Single(t => t.Id == target.Id).PrimaryName);
    }

    [Fact]
    public void Pass5_SkipsATargetPass4AlreadyFixed()
    {
        // Pass 4 rewrote this one from the cache, so pass 5 leaves it alone: "remaining" is the
        // spec's own word, and a second rewrite would be a second answer for the same target.
        var target = AddTarget("M 31", t => t.Aliases = Aliases("M 31"));
        AddCacheRow("M 31", "M 31", "M 31", "NGC 224", "NAME Andromeda Galaxy");

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.IdentitiesReDerived);
        Assert.Equal(0, outcome.NamesRebuilt);
        using var read = OpenRead();
        Assert.Equal(
            "M 31 - Andromeda Galaxy",
            read.Targets.Single(t => t.Id == target.Id).PrimaryName);
    }

    // ---- pass 6: stale candidate sweep ----------------------------------------------------

    [Fact]
    public void Pass6_ACandidateNamingAMergedAwayTarget_IsDeleted()
    {
        var winner = AddTarget("M 31");
        var loser = AddTarget("Andromeda", t => t.MergedIntoId = winner.Id);
        var candidate = LibrarySeeder.AddMergeCandidate(
            _db.ConnectionString, "Andromeda", c =>
            {
                c.SuggestedTargetId = loser.Id;
                c.Method = "trigram";
            });

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.StaleCandidatesRemoved);
        using var read = OpenRead();
        Assert.Empty(read.MergeCandidates.Where(c => c.Id == candidate.Id));
    }

    [Fact]
    public void Pass6_ACandidateNamingAnAbsentTarget_IsDeleted()
    {
        // suggested_target_id is a nullable FK with SetNull, so a target that went leaves a
        // trigram candidate with a null id: that null is what "absent" means.
        var candidate = LibrarySeeder.AddMergeCandidate(
            _db.ConnectionString, "Andromeda", c =>
            {
                c.SuggestedTargetId = null;
                c.Method = "trigram";
            });

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.StaleCandidatesRemoved);
        using var read = OpenRead();
        Assert.Empty(read.MergeCandidates.Where(c => c.Id == candidate.Id));
    }

    [Fact]
    public void Pass6_AHealthyCandidate_IsKept()
    {
        var active = AddTarget("M 31");
        var suggestion = LibrarySeeder.AddMergeCandidate(
            _db.ConnectionString, "Andromeda", c =>
            {
                c.SuggestedTargetId = active.Id;
                c.Method = "trigram";
            });

        // An orphan candidate also carries a null suggested_target_id, because it never named a
        // target. It is the record the Create target form is built on, so it is not stale.
        var orphan = LibrarySeeder.AddMergeCandidate(_db.ConnectionString, "Zzyzx Blob 42");

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(0, outcome.StaleCandidatesRemoved);
        using var read = OpenRead();
        Assert.Single(read.MergeCandidates.Where(c => c.Id == suggestion.Id));
        Assert.Single(read.MergeCandidates.Where(c => c.Id == orphan.Id));
    }

    // ---- the inline follow-up and the whole-run rules ---------------------------------------

    [Fact]
    public void DuplicateDetection_RunsInlineAtTheEnd()
    {
        // Departure 6: this port has no queue, so the pass the web source queued runs in the same
        // action. A name that resolves to nothing and matches no target is exactly what
        // DuplicateDetector records as an orphan candidate, and nothing else in this run writes a
        // merge_candidates row.
        AddFrame(null, "Zzyzx Blob 42");

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.DuplicateCandidatesWritten);
        using var read = OpenRead();
        var candidate = Assert.Single(read.MergeCandidates);
        Assert.Equal("Zzyzx Blob 42", candidate.SourceName);
        Assert.Equal("orphan", candidate.Method);
    }

    // Fix-wave review, with the Maintenance card's own sentence: the scope is THE SIX PASSES, not
    // the action. The inline duplicate detection that spec 12.7 orders at the end of the same
    // action reaches DuplicateDetector's Pass 1 outcome 2, which creates a target and assigns
    // frames to it, so "smart rebuild creates no target" is true only of the passes. Here the
    // unresolved name is one the stub resolver answers nothing for, so outcome 2 is not reached
    // and the count that stays at 1 is the passes' own.
    [Fact]
    public void Run_CreatesNoTargetRow()
    {
        AddTarget("M 31", t => t.Aliases = Aliases("M 31"));
        AddCacheRow("M 31", "M 31", "M 31", "NGC 224", "NAME Andromeda Galaxy");
        AddFrame(null, "Zzyzx Blob 42");

        using (var before = OpenRead())
        {
            Assert.Equal(1, before.Targets.Count());
        }

        Make().Run(Ignore, CancellationToken.None);

        using var after = OpenRead();
        Assert.Equal(1, after.Targets.Count());
    }

    [Fact]
    public void Run_DeletesNoTargetRow()
    {
        // Including a target with no frames at all, which the web source's phase 1 would delete.
        AddTarget("M 31");
        AddTarget("Orphaned target with no frames");
        var winner = AddTarget("Winner");
        AddTarget("Andromeda", t => t.MergedIntoId = winner.Id);

        Make().Run(Ignore, CancellationToken.None);

        using var read = OpenRead();
        Assert.Equal(4, read.Targets.Count());
    }

    [Fact]
    public void Run_ReportsACountPerPass()
    {
        var winner = AddTarget("M 31", t => t.Aliases = Aliases("M 31"));
        var loser = AddTarget("Andromeda", t => t.MergedIntoId = winner.Id);
        AddFrame(loser.Id, "Andromeda");

        var aliased = AddTarget("NGC 7000", t => t.Aliases = Aliases("NORTH AMERICA"));
        AddFrame(null, "North America");

        var stale = AddTarget("some stale name", t =>
        {
            t.CatalogId = "IC 434";
            t.CommonName = "Horsehead Nebula";
        });
        AddCacheRow("M 31", "M 31", "M 31", "NGC 224", "NAME Andromeda Galaxy");
        LibrarySeeder.AddMergeCandidate(
            _db.ConnectionString, "Andromeda", c =>
            {
                c.SuggestedTargetId = loser.Id;
                c.Method = "trigram";
            });

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.FramesRedirected);
        Assert.Equal(1, outcome.FramesLinkedByAlias);
        Assert.True(outcome.AliasesAdded >= 1);
        Assert.Equal(1, outcome.IdentitiesReDerived);
        Assert.Equal(1, outcome.NamesRebuilt);
        Assert.Equal(1, outcome.StaleCandidatesRemoved);
        Assert.Equal(SmartRebuild.SmartRebuildStatus.Completed, outcome.Status);

        Assert.NotNull(aliased);
        Assert.NotNull(stale);
    }

    [Fact]
    public void Run_ReportsProgressPerPass()
    {
        var reports = new List<(int Step, int Total, string Message)>();

        Make().Run((step, total, message) => reports.Add((step, total, message)), CancellationToken.None);

        // One boundary report per pass, against the same denominator, so the caller's progress
        // surface reads six of six rather than two different scales.
        var boundaries = reports.Where(r => r.Total == 6).Select(r => r.Step).ToList();
        Assert.Equal([1, 2, 3, 4, 5, 6], boundaries);
    }

    [Fact]
    public void Run_MakesNoNetworkCall()
    {
        // A real TargetResolver over real HTTP clients, bound exactly as AppHost binds it. The
        // name below is in no catalogue, so without skipOnline the resolver would reach SIMBAD and
        // then SESAME for it.
        var handler = new RecordingHandler();
        var resolver = new TargetResolver(
            _db.ConnectionString,
            StaticCatalogLoader.ResolveCatalogsDirectory(),
            new CatalogCacheRepository(_db.ConnectionString),
            new SimbadClient(handler),
            new SesameClient(handler));

        AddFrame(null, "Zzyzx Blob 42");

        var asked = 0;
        var rebuild = Make((name, createIfMissing, ct) =>
        {
            asked++;
            return resolver.Resolve(name, createIfMissing, dryRun: false, skipOnline: true, ct: ct);
        });

        rebuild.Run(Ignore, CancellationToken.None);

        // Non-vacuous: the inline duplicate detection really did ask, and the network was still
        // never reached.
        Assert.True(asked > 0);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void AppHostBindsTheResolveDelegateWithSkipOnlineTrue()
    {
        // The companion to the case above: the delegate is what makes the no-network guarantee
        // true, so the production binding is asserted rather than assumed.
        var appHost = Path.Combine(FindRepoRoot(), "src", "GalactiLog.App", "AppHost.cs");
        Assert.True(File.Exists(appHost), $"AppHost.cs not found at {appHost}");

        var text = File.ReadAllText(appHost);
        var registration = System.Text.RegularExpressions.Regex.Match(text, @"new SmartRebuild\((?:.|\n)*?\)\);");
        Assert.True(registration.Success, "no SmartRebuild registration found in AppHost.cs");
        Assert.Contains("skipOnline: true", registration.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_WhileAScanIsRunning_IsRefusedAndWritesNothing()
    {
        var winner = AddTarget("M 31");
        var loser = AddTarget("Andromeda", t => t.MergedIntoId = winner.Id);
        var frame = AddFrame(loser.Id, "Andromeda");
        var candidate = LibrarySeeder.AddMergeCandidate(
            _db.ConnectionString, "Andromeda", c =>
            {
                c.SuggestedTargetId = loser.Id;
                c.Method = "trigram";
            });

        var outcome = Make(tryBeginResolution: () => null).Run(Ignore, CancellationToken.None);

        Assert.Equal(SmartRebuild.SmartRebuildStatus.ScanInProgress, outcome.Status);
        Assert.Equal(0, outcome.FramesRedirected);
        Assert.Equal(0, outcome.StaleCandidatesRemoved);

        using var read = OpenRead();
        Assert.Equal(loser.Id, read.Images.Single(i => i.Id == frame.Id).ResolvedTargetId);
        Assert.Single(read.MergeCandidates.Where(c => c.Id == candidate.Id));
    }

    [Fact]
    public void Run_TakesTheResolutionLeaseBeforeAnyWrite()
    {
        var lease = new Lease();
        var taken = 0;

        var winner = AddTarget("M 31");
        var loser = AddTarget("Andromeda", t => t.MergedIntoId = winner.Id);
        AddFrame(loser.Id, "Andromeda");

        var rebuild = Make(tryBeginResolution: () =>
        {
            taken++;
            return lease;
        });

        var outcome = rebuild.Run(Ignore, CancellationToken.None);

        Assert.Equal(1, taken);
        Assert.Equal(1, outcome.FramesRedirected);

        // Held for the whole run and released when Run returns.
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void Run_Cancelled_StopsAtTheNextCheckpoint()
    {
        var winner = AddTarget("M 31");
        var loser = AddTarget("Andromeda", t => t.MergedIntoId = winner.Id);
        var frame = AddFrame(loser.Id, "Andromeda");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => Make().Run(Ignore, cancelled.Token));

        using var read = OpenRead();
        Assert.Equal(loser.Id, read.Images.Single(i => i.Id == frame.Id).ResolvedTargetId);
    }

    [Fact]
    public void Run_IsSafelyReRunnable()
    {
        var winner = AddTarget("M 31", t => t.Aliases = Aliases("M 31"));
        var loser = AddTarget("Andromeda", t => t.MergedIntoId = winner.Id);
        AddFrame(loser.Id, "Andromeda");
        AddCacheRow("M 31", "M 31", "M 31", "NGC 224", "NAME Andromeda Galaxy");

        var rebuild = Make();
        var first = rebuild.Run(Ignore, CancellationToken.None);
        var second = rebuild.Run(Ignore, CancellationToken.None);

        Assert.Equal(1, first.FramesRedirected);
        Assert.Equal(0, second.FramesRedirected);
        Assert.Equal(0, second.FramesLinkedByAlias);
        Assert.Equal(0, second.AliasesAdded);
        Assert.Equal(0, second.IdentitiesReDerived);
        Assert.Equal(0, second.NamesRebuilt);
        Assert.Equal(0, second.StaleCandidatesRemoved);
    }

    [Fact]
    public void Run_PortsNoMosaicPanelPass()
    {
        // Departure 7, kept true after a later edit: spec 12.7 names the web source's mosaic panel
        // membership recomputation and this port has no panels (spec 19.1).
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.Data", "Ingest", "SmartRebuild.cs"));

        Assert.DoesNotContain("MosaicPanel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PanelMembership", source, StringComparison.Ordinal);
        Assert.DoesNotContain("mosaic_panels", source, StringComparison.Ordinal);
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
}
