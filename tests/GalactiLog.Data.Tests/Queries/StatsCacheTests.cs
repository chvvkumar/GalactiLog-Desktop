using System.Reflection;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

/// <summary>
/// The roadmap's Phase 9 row 2 Verify line ends with "plus an assertion that a completed scan
/// invalidates the cache", which is <see cref="ScanFinished_InvalidatesTheCache"/>. The rest cover
/// the memo contract itself: one build per invalidation, and no TTL.
/// </summary>
/// <remarks>
/// Every build is counted through the <c>thumbnailCacheBytes</c> delegate, which
/// <see cref="StatsQuery.Get"/> invokes exactly once per call. That needs no counting subclass or
/// test-only seam on either type, both of which are sealed.
/// </remarks>
public class StatsCacheTests
{
    [Fact]
    public void Current_BuildsOnce_AndReturnsTheSameInstanceUntilInvalidated()
    {
        using var library = StatsLibrary.Seeded();
        var builds = 0;
        var cache = new StatsCache(library.Query, () => Count(ref builds));

        var first = cache.Current;
        var second = cache.Current;

        Assert.Same(first, second);
        Assert.Equal(1, builds);
    }

    [Fact]
    public void Invalidate_CausesARebuild()
    {
        using var library = StatsLibrary.Seeded();
        var builds = 0;
        var cache = new StatsCache(library.Query, () => Count(ref builds));
        var first = cache.Current;

        cache.Invalidate();
        var second = cache.Current;

        Assert.NotSame(first, second);
        Assert.Equal(2, builds);
        Assert.Equal(first.Overview, second.Overview);
    }

    [Fact]
    public async Task Current_IsSafeFromTwoThreads()
    {
        using var library = StatsLibrary.Seeded();
        var builds = 0;
        var cache = new StatsCache(library.Query, () => Count(ref builds));

        var responses = await Task.WhenAll(
            Task.Run(() => cache.Current),
            Task.Run(() => cache.Current));

        Assert.Same(responses[0], responses[1]);
        Assert.Equal(1, builds);
    }

    [Fact]
    public void Current_HasNoTtl_AndDoesNotRebuildOnItsOwn()
    {
        using var library = StatsLibrary.Seeded();
        var builds = 0;
        var cache = new StatsCache(library.Query, () => Count(ref builds));

        var first = cache.Current;

        Assert.Same(first, cache.Current);
        Assert.Equal(1, builds);

        // There is no clock seam to advance, deliberately (questions.md Q7), so the rest of the
        // assertion is structural: unlike AliasMapCache and RigBaselinesCache this type exposes no
        // TTL and takes no clock, so nothing but Invalidate can drop the memo. No wall-clock delay,
        // because no plausible TTL would have elapsed inside one.
        Assert.DoesNotContain(
            typeof(StatsCache).GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance),
            member => member.Name.Contains("Ttl", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(StatsCache).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(Func<DateTime>));
    }

    [Fact]
    public void Current_DiscardsABuildThatWasInvalidatedWhileItWasInFlight()
    {
        using var library = StatsLibrary.Seeded();
        var builds = 0;
        StatsCache? cache = null;
        cache = new StatsCache(library.Query, () =>
        {
            // The delegate runs inside StatsQuery.Get, so invalidating here is a scan finishing
            // exactly while the aggregate it invalidates is in flight. Only the first build races;
            // the second publishes.
            if (Interlocked.Increment(ref builds) == 1)
            {
                cache!.Invalidate();
            }

            return 0;
        });

        var response = cache.Current;

        Assert.Equal(2, builds);
        Assert.Same(response, cache.Current);
        Assert.Equal(2, builds);
    }

    [Fact]
    public async Task ScanFinished_InvalidatesTheCache()
    {
        using var library = StatsLibrary.Seeded();
        var root = Directory.CreateTempSubdirectory("galactilog-stats-cache-").FullName;
        try
        {
            library.Settings.SaveGeneral(new GeneralSettings
            {
                ScanRoots = [root],
                ScanFilters = ScanFilterConfig.Empty,
            });

            var builds = 0;
            var cache = new StatsCache(library.Query, () => Count(ref builds));
            var before = cache.Current;

            var coordinator = MakeCoordinator(library);
            // Exactly what AppHost wires, except that AppHost subscribes through
            // ScanStatusService rather than to the coordinator directly, because
            // ScanStatusService is an App-layer type this project cannot see.
            coordinator.ScanFinished += (_, _) => cache.Invalidate();

            var outcome = await coordinator.RunAsync(ScanTrigger.Manual, null, CancellationToken.None);

            Assert.Equal("complete", outcome.State);
            Assert.NotSame(before, cache.Current);
            Assert.Equal(2, builds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static long Count(ref int builds)
    {
        Interlocked.Increment(ref builds);
        return 0;
    }

    private static ScanCoordinator MakeCoordinator(StatsLibrary library)
    {
        var resolver = new TargetResolver(
            library.ConnectionString,
            StaticCatalogLoader.ResolveCatalogsDirectory(),
            new CatalogCacheRepository(library.ConnectionString),
            new SimbadClient(new ThrowingHandler()),
            new SesameClient(new ThrowingHandler()));

        return new ScanCoordinator(
            library.ConnectionString,
            library.Settings,
            resolver,
            new ScanRunRepository(library.ConnectionString),
            NullLogger<ScanCoordinator>.Instance);
    }

    /// <summary>The scan root is empty, so nothing should ever reach the network; this makes that
    /// an assertion rather than an assumption.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException($"network access attempted: {request.RequestUri}");
    }
}
