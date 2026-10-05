using System;
using System.Linq;
using System.Threading.Tasks;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests.Repositories;

/// <summary>
/// Spec 12.8's Resolver group, and the rule from spec 9.6 that an expired negative row "is
/// treated as a miss" (coordinator ruling Q7). <c>CatalogCacheRepository.Get</c> is the one
/// increment site in the solution, so every case here drives the counters through a real
/// repository over a real database rather than by calling the counter object.
/// </summary>
public class ResolverCountersTests
{
    private const string Source = "simbad";

    private static void SeedRow(string connectionString, string key, bool negative, DateTime fetchedAt)
    {
        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(connectionString, tracking: true));
        context.CatalogCacheEntries.Add(new CatalogCacheEntry
        {
            Source = Source,
            Key = key,
            Payload = negative ? null : "{\"ra\":1.0}",
            Negative = negative,
            FetchedAt = fetchedAt,
        });
        context.SaveChanges();
    }

    [Fact]
    public void Get_OnAPositiveRow_RecordsOneHit()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedRow(database.ConnectionString, "M31", negative: false, DateTime.UtcNow);
        var counters = new ResolverCounters();
        var repository = new CatalogCacheRepository(database.ConnectionString, counters);

        var lookup = repository.Get(Source, "M31");

        Assert.Equal(CatalogCacheRepository.CacheHit.Positive, lookup.Kind);
        Assert.Equal(1, counters.Hits);
        Assert.Equal(0, counters.Misses);
    }

    [Fact]
    public void Get_OnAFreshNegativeRow_RecordsOneHit()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedRow(database.ConnectionString, "NoSuchThing", negative: true, DateTime.UtcNow.AddDays(-1));
        var counters = new ResolverCounters();
        var repository = new CatalogCacheRepository(database.ConnectionString, counters);

        var lookup = repository.Get(Source, "NoSuchThing");

        // A fresh negative row answers without a network call, which is what the counter measures.
        Assert.Equal(CatalogCacheRepository.CacheHit.Negative, lookup.Kind);
        Assert.Equal(1, counters.Hits);
        Assert.Equal(0, counters.Misses);
    }

    [Fact]
    public void Get_OnAnExpiredNegativeRow_RecordsOneMiss()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedRow(
            database.ConnectionString,
            "Stale",
            negative: true,
            DateTime.UtcNow - CatalogCacheRepository.NegativeTtl - TimeSpan.FromDays(1));
        var counters = new ResolverCounters();
        var repository = new CatalogCacheRepository(database.ConnectionString, counters);

        var lookup = repository.Get(Source, "Stale");

        // Spec 9.6: an expired row is treated as a miss, so it is counted as one.
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, lookup.Kind);
        Assert.Equal(0, counters.Hits);
        Assert.Equal(1, counters.Misses);
    }

    [Fact]
    public void Get_OnAMissingRow_RecordsOneMiss()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        var counters = new ResolverCounters();
        var repository = new CatalogCacheRepository(database.ConnectionString, counters);

        var lookup = repository.Get(Source, "NeverSeen");

        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, lookup.Kind);
        Assert.Equal(0, counters.Hits);
        Assert.Equal(1, counters.Misses);
    }

    [Fact]
    public void GetOrFetch_OnACacheHit_RecordsExactlyOneHit_AndDoesNotFetch()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedRow(database.ConnectionString, "M42", negative: false, DateTime.UtcNow);
        var counters = new ResolverCounters();
        var repository = new CatalogCacheRepository(database.ConnectionString, counters);
        var fetches = 0;

        var payload = repository.GetOrFetch(Source, "M42", () => { fetches++; return "{}"; });

        Assert.Equal("{\"ra\":1.0}", payload);
        Assert.Equal(0, fetches);
        Assert.Equal(1, counters.Hits);
        Assert.Equal(0, counters.Misses);
    }

    [Fact]
    public void GetOrFetchDualKey_CheckingBothKeys_RecordsOneEntryPerGet()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        var counters = new ResolverCounters();
        var repository = new CatalogCacheRepository(database.ConnectionString, counters);

        // Neither key is cached, so the dual-key path reads both rows and misses both. One entry
        // per Get call: the increment lives in Get and nowhere else, so a second increment site
        // would show up here as three.
        var payload = repository.GetOrFetchDualKey(
            Source, "NGC 224", "M31", () => null, () => null, skipSimbad: true);

        Assert.Null(payload);
        Assert.Equal(0, counters.Hits);
        Assert.Equal(2, counters.Misses);
    }

    [Fact]
    public void Counters_IncrementedFromManyThreads_TotalIsExact()
    {
        var counters = new ResolverCounters();

        Parallel.For(0, 1000, index =>
        {
            counters.RecordHit();
            if (index % 2 == 0)
            {
                counters.RecordMiss();
            }
        });

        Assert.Equal(1000, counters.Hits);
        Assert.Equal(500, counters.Misses);
    }

    [Fact]
    public void RepositoryBuiltWithoutCounters_DoesNotThrow()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedRow(database.ConnectionString, "M13", negative: false, DateTime.UtcNow);

        // The trailing parameter is optional so no existing construction site changes and a
        // repository built by hand in a test counts nothing.
        var repository = new CatalogCacheRepository(database.ConnectionString);

        Assert.Equal(CatalogCacheRepository.CacheHit.Positive, repository.Get(Source, "M13").Kind);
        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, repository.Get(Source, "Absent").Kind);
    }
}
