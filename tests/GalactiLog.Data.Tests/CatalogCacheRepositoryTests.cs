using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests;

public class CatalogCacheRepositoryTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly CatalogCacheRepository _repo;

    public CatalogCacheRepositoryTests()
    {
        _db = TestDatabaseFactory.CreateMigratedDatabase();
        // No real backoff: the schedule is asserted through a recording delegate below.
        _repo = new CatalogCacheRepository(_db.ConnectionString, wait: (_, _) => { });
    }

    public void Dispose() => _db.Dispose();

    // Inserts a row directly (bypassing Save, which always stamps FetchedAt = UtcNow) so a
    // backdated FetchedAt can be tested.
    private void InsertRow(string source, string key, string? payload, bool negative, DateTime fetchedAt)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));
        context.CatalogCacheEntries.Add(new CatalogCacheEntry
        {
            Source = source,
            Key = key,
            Payload = payload,
            Negative = negative,
            FetchedAt = fetchedAt,
        });
        context.SaveChanges();
    }

    private CatalogCacheEntry? FindRow(string source, string key)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString));
        return context.CatalogCacheEntries.SingleOrDefault(e => e.Source == source && e.Key == key);
    }

    [Fact]
    public void Get_NoRow_ReturnsMiss()
    {
        var result = _repo.Get("simbad", "M 31");

        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, result.Kind);
        Assert.Null(result.Payload);
    }

    [Fact]
    public void Save_ThenGet_PositiveRow_ReturnsPositiveWithPayload()
    {
        _repo.Save("simbad", "M 31", "{\"main_id\":\"M  31\"}");

        var result = _repo.Get("simbad", "M 31");

        Assert.Equal(CatalogCacheRepository.CacheHit.Positive, result.Kind);
        Assert.Equal("{\"main_id\":\"M  31\"}", result.Payload);
    }

    [Fact]
    public void Save_ThenGet_NegativeRow_ReturnsNegative()
    {
        _repo.Save("simbad", "NOTAREALOBJECT", null);

        var result = _repo.Get("simbad", "NOTAREALOBJECT");

        Assert.Equal(CatalogCacheRepository.CacheHit.Negative, result.Kind);
        Assert.Null(result.Payload);
    }

    [Fact]
    public void Get_ExpiredNegativeRow_ReturnsMiss()
    {
        InsertRow("simbad", "STALE", null, negative: true, fetchedAt: DateTime.UtcNow - TimeSpan.FromDays(8));

        var result = _repo.Get("simbad", "STALE");

        Assert.Equal(CatalogCacheRepository.CacheHit.Miss, result.Kind);
    }

    [Fact]
    public void Get_FreshNegativeRow_ReturnsNegative()
    {
        InsertRow("simbad", "FRESH", null, negative: true, fetchedAt: DateTime.UtcNow - TimeSpan.FromDays(6));

        var result = _repo.Get("simbad", "FRESH");

        Assert.Equal(CatalogCacheRepository.CacheHit.Negative, result.Kind);
    }

    [Fact]
    public void Get_PositiveRow_NeverExpiresRegardlessOfAge()
    {
        InsertRow("simbad", "OLD", "payload", negative: false, fetchedAt: DateTime.UtcNow - TimeSpan.FromDays(3650));

        var result = _repo.Get("simbad", "OLD");

        Assert.Equal(CatalogCacheRepository.CacheHit.Positive, result.Kind);
        Assert.Equal("payload", result.Payload);
    }

    [Fact]
    public void GetOrFetch_CacheHitPositive_NeverCallsFetch()
    {
        _repo.Save("simbad", "M 31", "cached");

        var result = _repo.GetOrFetch("simbad", "M 31", () => throw new InvalidOperationException("must not be called"));

        Assert.Equal("cached", result);
    }

    [Fact]
    public void GetOrFetch_CacheHitNegative_NeverCallsFetch()
    {
        _repo.Save("simbad", "NOTAREALOBJECT", null);

        var result = _repo.GetOrFetch("simbad", "NOTAREALOBJECT", () => throw new InvalidOperationException("must not be called"));

        Assert.Null(result);
    }

    [Fact]
    public void GetOrFetch_Miss_CallsFetchOnceOnSuccess_AndCachesPositive()
    {
        var calls = 0;
        var result = _repo.GetOrFetch("simbad", "M 31", () => { calls++; return "fetched"; });

        Assert.Equal(1, calls);
        Assert.Equal("fetched", result);
        var row = FindRow("simbad", "M 31");
        Assert.NotNull(row);
        Assert.False(row!.Negative);
        Assert.Equal("fetched", row.Payload);
    }

    [Fact]
    public void GetOrFetch_FetchReturnsNull_CachesNegative()
    {
        var result = _repo.GetOrFetch("simbad", "NOTAREALOBJECT", () => null);

        Assert.Null(result);
        var row = FindRow("simbad", "NOTAREALOBJECT");
        Assert.NotNull(row);
        Assert.True(row!.Negative);
        Assert.Null(row.Payload);
    }

    [Fact]
    public void GetOrFetch_TransientFailureThenSuccess_RetriesAndSucceeds()
    {
        var calls = 0;
        var result = _repo.GetOrFetch("simbad", "M 31", () =>
        {
            calls++;
            if (calls == 1)
            {
                throw new HttpRequestException("transient");
            }
            return "fetched";
        });

        Assert.Equal(2, calls);
        Assert.Equal("fetched", result);
        var row = FindRow("simbad", "M 31");
        Assert.NotNull(row);
        Assert.False(row!.Negative);
        Assert.Equal("fetched", row.Payload);
    }

    [Fact]
    public void GetOrFetch_AllAttemptsTransientlyFail_ThrowsAfterThreeAttempts()
    {
        var calls = 0;
        string? Fetch()
        {
            calls++;
            throw new HttpRequestException("still down");
        }

        var ex = Assert.Throws<HttpRequestException>(() => _repo.GetOrFetch("simbad", "M 31", Fetch));

        Assert.Equal("still down", ex.Message);
        Assert.Equal(3, calls);
        Assert.Null(FindRow("simbad", "M 31"));
    }

    [Fact]
    public void GetOrFetch_TransientFailures_BackOffOneSecondThenTwo()
    {
        var waits = new List<TimeSpan>();
        var repo = new CatalogCacheRepository(_db.ConnectionString, wait: (delay, _) => waits.Add(delay));

        Assert.Throws<HttpRequestException>(
            () => repo.GetOrFetch("simbad", "M 31", () => throw new HttpRequestException("still down")));

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waits);
    }

    [Fact]
    public void GetOrFetch_NonTransientOn4xx_DoesNotRetryAndDoesNotWriteCache()
    {
        var calls = 0;
        string? Fetch()
        {
            calls++;
            throw new NonTransientCatalogException("404");
        }

        Assert.Throws<NonTransientCatalogException>(() => _repo.GetOrFetch("simbad", "M 31", Fetch));

        Assert.Equal(1, calls);
        Assert.Null(FindRow("simbad", "M 31"));
    }

    [Fact]
    public void GetOrFetchDualKey_OriginalPositive_ReturnsWithoutFetchingOrCheckingMapped()
    {
        _repo.Save("simbad", "ORIGINAL", "original-payload");

        var result = _repo.GetOrFetchDualKey(
            "simbad", "ORIGINAL", "MAPPED",
            fetchMapped: () => throw new InvalidOperationException("must not be called"),
            fetchOriginal: () => throw new InvalidOperationException("must not be called"));

        Assert.Equal("original-payload", result);
    }

    [Fact]
    public void GetOrFetchDualKey_MappedPositive_ReturnsMappedPayload()
    {
        _repo.Save("simbad", "MAPPED", "mapped-payload");

        var result = _repo.GetOrFetchDualKey(
            "simbad", "ORIGINAL", "MAPPED",
            fetchMapped: () => throw new InvalidOperationException("must not be called"),
            fetchOriginal: () => throw new InvalidOperationException("must not be called"));

        Assert.Equal("mapped-payload", result);
    }

    [Fact]
    public void GetOrFetchDualKey_MappedNegative_ReturnsNullRegardlessOfOriginalState()
    {
        // originalKey is a miss (no row); mappedKey is negative.
        _repo.Save("simbad", "MAPPED", null);

        var result = _repo.GetOrFetchDualKey(
            "simbad", "ORIGINAL", "MAPPED",
            fetchMapped: () => throw new InvalidOperationException("must not be called"),
            fetchOriginal: () => throw new InvalidOperationException("must not be called"));

        Assert.Null(result);
    }

    [Fact]
    public void GetOrFetchDualKey_BothMiss_FetchesMappedFirst_WritesUnderBothKeys()
    {
        var originalCalled = false;
        var result = _repo.GetOrFetchDualKey(
            "simbad", "ORIGINAL", "MAPPED",
            fetchMapped: () => "mapped-fetched",
            fetchOriginal: () => { originalCalled = true; return "must not be used"; });

        Assert.Equal("mapped-fetched", result);
        Assert.False(originalCalled);
        var originalRow = FindRow("simbad", "ORIGINAL");
        var mappedRow = FindRow("simbad", "MAPPED");
        Assert.NotNull(originalRow);
        Assert.NotNull(mappedRow);
        Assert.False(originalRow!.Negative);
        Assert.False(mappedRow!.Negative);
        Assert.Equal("mapped-fetched", originalRow.Payload);
        Assert.Equal("mapped-fetched", mappedRow.Payload);
    }

    [Fact]
    public void GetOrFetchDualKey_MappedFetchMisses_FallsBackToOriginalFetch_WritesOnlyOriginalKey()
    {
        var result = _repo.GetOrFetchDualKey(
            "simbad", "ORIGINAL", "MAPPED",
            fetchMapped: () => null,
            fetchOriginal: () => "original-fetched");

        Assert.Equal("original-fetched", result);
        var originalRow = FindRow("simbad", "ORIGINAL");
        Assert.NotNull(originalRow);
        Assert.Equal("original-fetched", originalRow!.Payload);
        Assert.Null(FindRow("simbad", "MAPPED"));
    }

    [Fact]
    public void GetOrFetchDualKey_NoMappedKey_BehavesLikePlainGetOrFetch()
    {
        var calls = 0;
        var result = _repo.GetOrFetchDualKey(
            "simbad", "ORIGINAL", null,
            fetchMapped: () => throw new InvalidOperationException("must not be called"),
            fetchOriginal: () => { calls++; return "original-fetched"; });

        Assert.Equal(1, calls);
        Assert.Equal("original-fetched", result);
        var originalRow = FindRow("simbad", "ORIGINAL");
        Assert.NotNull(originalRow);
        Assert.Equal("original-fetched", originalRow!.Payload);
    }

    [Fact]
    public void GetOrFetchDualKey_SkipSimbad_NeverFetches()
    {
        var result = _repo.GetOrFetchDualKey(
            "simbad", "ORIGINAL", "MAPPED",
            fetchMapped: () => throw new InvalidOperationException("must not be called"),
            fetchOriginal: () => throw new InvalidOperationException("must not be called"),
            skipSimbad: true);

        Assert.Null(result);
        Assert.Null(FindRow("simbad", "ORIGINAL"));
        Assert.Null(FindRow("simbad", "MAPPED"));
    }

    [Fact]
    public void GetCacheOnly_PositiveRow_ReturnsPayload()
    {
        _repo.Save("simbad", "M 31", "cached");

        var result = _repo.GetCacheOnly("simbad", "M 31");

        Assert.Equal("cached", result);
    }

    [Fact]
    public void GetCacheOnly_MissOrNegative_ReturnsNullWithoutFetching()
    {
        Assert.Null(_repo.GetCacheOnly("simbad", "NEVERQUERIED"));

        _repo.Save("simbad", "NOTAREALOBJECT", null);
        Assert.Null(_repo.GetCacheOnly("simbad", "NOTAREALOBJECT"));
    }

    [Fact]
    public void Save_TwiceFromSeparateContextsWithNoReadInBetween_LeavesOneRowWithSecondPayload()
    {
        // Regression for the read-then-insert race: two Save calls on the same key, neither
        // one having read the other's write first (each Save opens and disposes its own
        // context), must still upsert rather than throw a PK violation or leave two rows.
        _repo.Save("simbad", "M 31", "first-payload");
        _repo.Save("simbad", "M 31", "second-payload");

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString));
        var rows = context.CatalogCacheEntries.Where(e => e.Source == "simbad" && e.Key == "M 31").ToList();
        Assert.Single(rows);
        Assert.Equal("second-payload", rows[0].Payload);
        Assert.False(rows[0].Negative);
    }

    [Fact]
    public void Save_TwoConcurrentCallsOnSeparateThreads_BothSucceedAndLeaveOneRow()
    {
        var barrier = new Barrier(2);
        void RunSave(string payload)
        {
            barrier.SignalAndWait();
            _repo.Save("simbad", "M 31", payload);
        }

        var t1 = new Thread(() => RunSave("thread-1-payload"));
        var t2 = new Thread(() => RunSave("thread-2-payload"));
        t1.Start();
        t2.Start();
        t1.Join();
        t2.Join();

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_db.ConnectionString));
        var rows = context.CatalogCacheEntries.Where(e => e.Source == "simbad" && e.Key == "M 31").ToList();
        Assert.Single(rows);
        Assert.Contains(rows[0].Payload, new[] { "thread-1-payload", "thread-2-payload" });
    }

    [Fact]
    public void ClearNegative_DeletesOnlyNegativeRowsForSource()
    {
        _repo.Save("simbad", "POSITIVE", "payload");
        _repo.Save("simbad", "NEGATIVE", null);
        _repo.Save("sesame", "NEGATIVE", null);

        var deleted = _repo.ClearNegative("simbad");

        Assert.Equal(1, deleted);
        Assert.NotNull(FindRow("simbad", "POSITIVE"));
        Assert.Null(FindRow("simbad", "NEGATIVE"));
        Assert.NotNull(FindRow("sesame", "NEGATIVE"));
    }

    [Fact]
    public void ClearNegative_WithKey_DeletesOnlyThatRow()
    {
        _repo.Save("simbad", "NEGATIVE-ONE", null);
        _repo.Save("simbad", "NEGATIVE-TWO", null);

        var deleted = _repo.ClearNegative("simbad", "NEGATIVE-ONE");

        Assert.Equal(1, deleted);
        Assert.Null(FindRow("simbad", "NEGATIVE-ONE"));
        Assert.NotNull(FindRow("simbad", "NEGATIVE-TWO"));
    }
}
