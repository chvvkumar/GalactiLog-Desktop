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

// Spec 12.7's catalog identity backfill (PAR-007) and the Phase 14B roadmap row 4 Verify clause.
// Database rows only, and no HTTP handler is ever reached.
public class CatalogIdentityBackfillTests : IDisposable
{
    private readonly TestDatabaseHandle _db;

    public CatalogIdentityBackfillTests() => _db = TestDatabaseFactory.CreateMigratedDatabase();

    public void Dispose() => _db.Dispose();

    // ---- fixtures ------------------------------------------------------------------------

    private sealed class Lease : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

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

    // What ResolveIdentity returns on a hit: an identity, and always a null TargetId.
    private static TargetResolver.ResolutionResult Identity(string catalogId, string primaryName)
        => new(
            null,
            TargetResolver.ResolutionStage.Cache,
            new ResolvedIdentity(primaryName, catalogId, null, null, null, null, []));

    private CatalogIdentityBackfill Make(
        Func<string, CancellationToken, TargetResolver.ResolutionResult>? resolve = null,
        Func<IDisposable?>? tryBeginResolution = null)
        => new(
            _db.ConnectionString,
            resolve ?? ((_, _) => Unresolved),
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

    // ---- the match ------------------------------------------------------------------------

    [Fact]
    public void AnUnlinkedNameResolvingToAnExistingTargetsIdentity_IsLinked()
    {
        var target = AddTarget("M 31 - Andromeda Galaxy", t =>
        {
            t.CatalogId = "M 31";
            t.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId("M 31");
        });
        var frame = AddFrame(null, "Andromeda");

        var outcome = Make((_, _) => Identity("M 31", "M 31 - Andromeda Galaxy"))
            .Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.LinkedNames);
        Assert.Equal(1, outcome.LinkedFrames);
        Assert.Equal(0, outcome.SkippedNames);

        using var read = OpenRead();
        Assert.Equal(target.Id, read.Images.Single(i => i.Id == frame.Id).ResolvedTargetId);
    }

    [Fact]
    public void TheMatch_IsTheSameIdentityRuleTheScanUses()
    {
        // Spec 9.5 step 3: on a hit the panel-stripped uppercase incoming name is appended as an
        // alias. That side effect is the fingerprint of TargetResolver.MatchTargetByIdentity, so a
        // second hand-written matcher would fail this case.
        var target = AddTarget("M 31 - Andromeda Galaxy", t =>
        {
            t.CatalogId = "M 31";
            t.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId("M 31");
        });
        AddFrame(null, "andromeda Panel 3");

        Make((_, _) => Identity("M 31", "M 31 - Andromeda Galaxy")).Run(Ignore, CancellationToken.None);

        using var read = OpenRead();
        var stored = read.Targets.Single(t => t.Id == target.Id);
        Assert.Contains("ANDROMEDA", JsonSerializer.Deserialize<List<string>>(stored.Aliases) ?? []);
    }

    [Fact]
    public void AllOfThatNamesUnresolvedLightFrames_Move()
    {
        var target = AddTarget("M 31", t =>
        {
            t.CatalogId = "M 31";
            t.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId("M 31");
        });
        AddFrame(null, "Andromeda", 10);
        AddFrame(null, "Andromeda", 11);
        AddFrame(null, "Andromeda", 12);

        var outcome = Make((_, _) => Identity("M 31", "M 31")).Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.LinkedNames);
        Assert.Equal(3, outcome.LinkedFrames);

        using var read = OpenRead();
        Assert.Equal(3, read.Images.Count(i => i.ResolvedTargetId == target.Id));
    }

    [Fact]
    public void OnlyLightFramesMove()
    {
        AddTarget("M 31", t =>
        {
            t.CatalogId = "M 31";
            t.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId("M 31");
        });
        var light = AddFrame(null, "Andromeda");
        var dark = LibrarySeeder.AddFrame(
            _db.ConnectionString, null, new DateOnly(2025, 3, 15),
            image =>
            {
                image.ImageType = "DARK";
                image.RawHeaders = LibrarySeeder.RawHeadersWithObject("Andromeda");
            });

        Make((_, _) => Identity("M 31", "M 31")).Run(Ignore, CancellationToken.None);

        using var read = OpenRead();
        Assert.NotNull(read.Images.Single(i => i.Id == light.Id).ResolvedTargetId);
        Assert.Null(read.Images.Single(i => i.Id == dark.Id).ResolvedTargetId);
    }

    [Fact]
    public void AFrameAlreadyOwnedByATarget_IsNotMoved()
    {
        var owner = AddTarget("Owner already holding it");
        var other = AddTarget("M 31", t =>
        {
            t.CatalogId = "M 31";
            t.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId("M 31");
        });
        var owned = AddFrame(owner.Id, "Andromeda");

        var outcome = Make((_, _) => Identity("M 31", "M 31")).Run(Ignore, CancellationToken.None);

        // The name is not unresolved at all, so it is never even examined.
        Assert.Equal(0, outcome.LinkedNames);
        Assert.Equal(0, outcome.LinkedFrames);

        using var read = OpenRead();
        Assert.Equal(owner.Id, read.Images.Single(i => i.Id == owned.Id).ResolvedTargetId);
        Assert.NotNull(other);
    }

    // ---- the two orphan outcomes -----------------------------------------------------------

    [Fact]
    public void ANameResolvingToNothing_IsLeftOrphanedAndCounted()
    {
        var frame = AddFrame(null, "Zzyzx Blob 42");

        var outcome = Make().Run(Ignore, CancellationToken.None);

        Assert.Equal(0, outcome.LinkedNames);
        Assert.Equal(1, outcome.SkippedNames);

        using var read = OpenRead();
        Assert.Null(read.Images.Single(i => i.Id == frame.Id).ResolvedTargetId);
    }

    [Fact]
    public void ANameResolvingToNoExistingTarget_IsLeftOrphanedAndCounted()
    {
        // An identity the cache answered with, that no target carries. Nothing is created for it.
        var frame = AddFrame(null, "NGC 7000");

        var outcome = Make((_, _) => Identity("NGC 7000", "NGC 7000 - North America Nebula"))
            .Run(Ignore, CancellationToken.None);

        Assert.Equal(0, outcome.LinkedNames);
        Assert.Equal(1, outcome.SkippedNames);

        using var read = OpenRead();
        Assert.Empty(read.Targets);
        Assert.Null(read.Images.Single(i => i.Id == frame.Id).ResolvedTargetId);
    }

    [Fact]
    public void Run_CreatesNoTargetRow()
    {
        AddTarget("M 31", t =>
        {
            t.CatalogId = "M 31";
            t.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId("M 31");
        });
        AddFrame(null, "Andromeda");
        AddFrame(null, "NGC 7000");

        var backfill = Make((name, _) => name == "Andromeda"
            ? Identity("M 31", "M 31")
            : Identity("NGC 7000", "NGC 7000 - North America Nebula"));

        backfill.Run(Ignore, CancellationToken.None);

        using var read = OpenRead();
        Assert.Equal(1, read.Targets.Count());
    }

    // ---- ordering, counts and the delegate --------------------------------------------------

    [Fact]
    public void Run_ProcessesNamesMostFramesFirst()
    {
        // UnresolvedObjects.Read's own ordering, most frames first then by name, which is spec
        // 12.7's "most frames first" inherited rather than sorted again.
        AddFrame(null, "One frame only", 10);
        AddFrame(null, "Three frames here", 11);
        AddFrame(null, "Three frames here", 12);
        AddFrame(null, "Three frames here", 13);
        AddFrame(null, "Two frames here", 14);
        AddFrame(null, "Two frames here", 15);

        var order = new List<string>();
        Make((name, _) =>
        {
            order.Add(name);
            return Unresolved;
        }).Run(Ignore, CancellationToken.None);

        Assert.Equal(["Three frames here", "Two frames here", "One frame only"], order);
    }

    [Fact]
    public void Run_ReportsLinkedNamesLinkedFramesAndSkippedNames()
    {
        AddTarget("M 31", t =>
        {
            t.CatalogId = "M 31";
            t.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId("M 31");
        });
        AddFrame(null, "Andromeda", 10);
        AddFrame(null, "Andromeda", 11);
        AddFrame(null, "Zzyzx Blob 42", 12);

        var outcome = Make((name, _) => name == "Andromeda" ? Identity("M 31", "M 31") : Unresolved)
            .Run(Ignore, CancellationToken.None);

        Assert.Equal(1, outcome.LinkedNames);
        Assert.Equal(2, outcome.LinkedFrames);
        Assert.Equal(1, outcome.SkippedNames);
        Assert.Equal(CatalogIdentityBackfill.BackfillStatus.Completed, outcome.Status);
    }

    [Fact]
    public void Run_MakesNoNetworkCall()
    {
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
        var backfill = Make((name, ct) =>
        {
            asked++;
            return resolver.ResolveIdentity(name, skipOnline: true, ct: ct);
        });

        var outcome = backfill.Run(Ignore, CancellationToken.None);

        Assert.Equal(2, asked);
        Assert.Equal(2, outcome.SkippedNames);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Run_UsesResolveIdentityRatherThanResolve()
    {
        // The case that catches the obvious implementation: a delegate bound to
        // Resolve(createIfMissing: true) passes every other case in this file until the day a name
        // resolves to a new identity, and then it silently creates a target the spec forbids. The
        // production binding is read from AppHost's own source, because GalactiLog.Data.Tests
        // cannot reference GalactiLog.App.
        var appHost = Path.Combine(FindRepoRoot(), "src", "GalactiLog.App", "AppHost.cs");
        Assert.True(File.Exists(appHost), $"AppHost.cs not found at {appHost}");

        var text = File.ReadAllText(appHost);
        var registration = System.Text.RegularExpressions.Regex.Match(
            text, @"new CatalogIdentityBackfill\((?:.|\n)*?\)\);");
        Assert.True(registration.Success, "no CatalogIdentityBackfill registration found in AppHost.cs");
        Assert.Contains("ResolveIdentity(", registration.Value, StringComparison.Ordinal);
        Assert.Contains("skipOnline: true", registration.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("createIfMissing", registration.Value, StringComparison.Ordinal);

        // And the pass itself never reads a TargetId from the result, because ResolveIdentity
        // always returns null for one: the frames go to the matched target's id and nowhere else.
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.Data", "Ingest", "CatalogIdentityBackfill.cs"));
        Assert.DoesNotContain("result.TargetId", source, StringComparison.Ordinal);
    }

    // ---- the lease, cancellation and re-runnability -----------------------------------------

    [Fact]
    public void Run_WhileAScanIsRunning_IsRefusedAndWritesNothing()
    {
        AddTarget("M 31", t =>
        {
            t.CatalogId = "M 31";
            t.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId("M 31");
        });
        var frame = AddFrame(null, "Andromeda");

        var outcome = Make(
                resolve: (_, _) => Identity("M 31", "M 31"),
                tryBeginResolution: () => null)
            .Run(Ignore, CancellationToken.None);

        Assert.Equal(CatalogIdentityBackfill.BackfillStatus.ScanInProgress, outcome.Status);
        Assert.Equal(0, outcome.LinkedNames);
        Assert.Equal(0, outcome.LinkedFrames);
        Assert.Equal(0, outcome.SkippedNames);

        using var read = OpenRead();
        Assert.Null(read.Images.Single(i => i.Id == frame.Id).ResolvedTargetId);
    }

    [Fact]
    public void Run_TakesTheResolutionLeaseBeforeAnyWrite()
    {
        var lease = new Lease();
        var taken = 0;

        AddTarget("M 31", t =>
        {
            t.CatalogId = "M 31";
            t.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId("M 31");
        });
        AddFrame(null, "Andromeda");

        var outcome = Make(
                resolve: (_, _) => Identity("M 31", "M 31"),
                tryBeginResolution: () =>
                {
                    taken++;
                    return lease;
                })
            .Run(Ignore, CancellationToken.None);

        Assert.Equal(1, taken);
        Assert.Equal(1, outcome.LinkedFrames);
        Assert.True(lease.Disposed);
    }

    [Fact]
    public void Run_Cancelled_StopsAtTheNextCheckpoint()
    {
        AddTarget("M 31", t =>
        {
            t.CatalogId = "M 31";
            t.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId("M 31");
        });
        var frame = AddFrame(null, "Andromeda");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => Make((_, _) => Identity("M 31", "M 31")).Run(Ignore, cancelled.Token));

        using var read = OpenRead();
        Assert.Null(read.Images.Single(i => i.Id == frame.Id).ResolvedTargetId);
    }

    [Fact]
    public void Run_IsSafelyReRunnable_AndTheSecondRunLinksNothing()
    {
        AddTarget("M 31", t =>
        {
            t.CatalogId = "M 31";
            t.CatalogIdNormalized = NameNormalizer.NormalizeCatalogId("M 31");
        });
        AddFrame(null, "Andromeda", 10);
        AddFrame(null, "Andromeda", 11);

        var backfill = Make((_, _) => Identity("M 31", "M 31"));
        var first = backfill.Run(Ignore, CancellationToken.None);
        var second = backfill.Run(Ignore, CancellationToken.None);

        Assert.Equal(1, first.LinkedNames);
        Assert.Equal(2, first.LinkedFrames);

        Assert.Equal(0, second.LinkedNames);
        Assert.Equal(0, second.LinkedFrames);
        Assert.Equal(0, second.SkippedNames);
    }

    [Fact]
    public void Run_PortsNoCacheRepairPhase()
    {
        // Departure 7: the web task's first phase repairs quote-corrupted SIMBAD cache rows, a
        // defect this port never had. Kept true after a later edit.
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.Data", "Ingest", "CatalogIdentityBackfill.cs"));

        Assert.DoesNotContain("catalog_cache", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CatalogCacheRepository", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE", source, StringComparison.Ordinal);
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
