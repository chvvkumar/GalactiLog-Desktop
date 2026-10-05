using System.Diagnostics;
using GalactiLog.Core.Targets;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Repositories;

// The one table (catalog_cache) that is both the positive and negative cache for simbad,
// sesame, and resolver lookups (design-spec 9.6), plus the retry/backoff wrapper around the
// network call it caches and SIMBAD's dual-key check-then-write behavior. Short-lived
// DbContext per call, per design-spec 4.2's repository pattern, same as SettingsRepository.
public sealed class CatalogCacheRepository
{
    /// <summary>
    /// Spec 9.6's negative-cache lifetime. Public since Phase 10 Task 1 (coordinator ruling Q8)
    /// so <c>DiagnosticsQuery</c> counts expired rows against this value rather than writing a
    /// second 7 in a second file. Positive rows never expire.
    /// </summary>
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromDays(7);

    private const int MaxAttempts = 3;

    private readonly string _connectionString;
    private readonly ResolverCounters? _counters;
    private readonly Action<TimeSpan, CancellationToken> _wait;

    /// <param name="connectionString">The application database.</param>
    /// <param name="counters">Spec 12.8's per-process resolver hit and miss counters. Optional
    /// and trailing since Phase 10 Task 1, so no existing construction site changes and a
    /// repository built by hand in a test counts nothing.</param>
    /// <param name="wait">How the retry backoff is spent. Null is the production wait on the
    /// cancellation token's handle for the given delay; a test passes a delegate that records
    /// the delay and returns at once, so the 1 s then 2 s schedule is asserted, not slept.</param>
    public CatalogCacheRepository(
        string connectionString,
        ResolverCounters? counters = null,
        Action<TimeSpan, CancellationToken>? wait = null)
    {
        _connectionString = connectionString;
        _counters = counters;
        _wait = wait ?? ((delay, ct) => ct.WaitHandle.WaitOne(delay));
    }

    public enum CacheHit { Miss, Positive, Negative }

    public readonly record struct CacheLookup(CacheHit Kind, string? Payload);

    // Never calls the network. A stale negative row (older than 7 days) is reported as Miss,
    // not Negative -- spec 9.6: "An expired row is treated as a miss... overwritten by the
    // fresh result." Positive rows never expire.
    //
    // The one increment site for ResolverCounters (spec 12.8's Resolver group, design-lessons
    // rule 2): GetOrFetch, GetCacheOnly and GetOrFetchDualKey all reach the table through here,
    // so exactly one hit or one miss is recorded per lookup on every return path below. Do not
    // add an increment to any other member; GetOrFetchDualKey calls this twice by design and a
    // second site would double count it.
    public CacheLookup Get(string source, string key)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_connectionString));
        var row = context.CatalogCacheEntries.SingleOrDefault(e => e.Source == source && e.Key == key);
        if (row is null)
        {
            _counters?.RecordMiss();
            return new CacheLookup(CacheHit.Miss, null);
        }
        if (row.Negative)
        {
            if (row.FetchedAt + NegativeTtl < DateTime.UtcNow)
            {
                // Spec 9.6's expired row: a miss, so the counter reads it as one too (ruling Q7).
                _counters?.RecordMiss();
                return new CacheLookup(CacheHit.Miss, null);
            }

            // A fresh negative answers without a network call, which is what a hit means here.
            _counters?.RecordHit();
            return new CacheLookup(CacheHit.Negative, null);
        }

        _counters?.RecordHit();
        return new CacheLookup(CacheHit.Positive, row.Payload);
    }

    // Upsert by (source, key). payload == null writes a negative row. A single atomic SQL
    // upsert, not read-then-insert: the CLI and a scan writer can race on the same
    // (source, key), and a read-then-write here would let one writer's insert lose to a
    // conflicting insert from the other (or silently overwrite it out of order). SQLite's
    // own ON CONFLICT clause makes this one statement, serialized by the existing
    // journal_mode=WAL + busy_timeout=5000 pragmas (PragmaConnectionInterceptor), so
    // concurrent Save calls block-and-retry rather than lose data.
    public void Save(string source, string key, string? payload)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_connectionString, tracking: true));
        context.Database.ExecuteSqlInterpolated($"""
            INSERT INTO catalog_cache (source, key, payload, negative, fetched_at)
            VALUES ({source}, {key}, {payload}, {payload is null}, {DateTime.UtcNow})
            ON CONFLICT(source, key) DO UPDATE SET
                payload = excluded.payload,
                negative = excluded.negative,
                fetched_at = excluded.fetched_at
            """);
    }

    // Get-or-fetch with retry/backoff (spec 9.6): a fresh cache hit (positive or negative)
    // never calls fetch. On a miss, fetch() runs with up to 3 total attempts, sleeping 1s
    // then 2s between attempts. fetch() returning null means "queried successfully, no
    // result" and is cached as a negative row; a non-null return is cached positive.
    // fetch() throwing NonTransientCatalogException aborts immediately: no further attempts,
    // no cache write, and the exception propagates to the caller untouched. If every attempt
    // fails transiently (timeout, connection failure, 5xx, 429), the last attempt's exception
    // propagates uncaught and no cache row is written -- a network outage must not poison the
    // resolver cache for the 7 day negative TTL (coordinator ruling Q7).
    public string? GetOrFetch(string source, string key, Func<string?> fetch, CancellationToken ct = default)
    {
        var cached = Get(source, key);
        if (cached.Kind == CacheHit.Negative)
        {
            return null;
        }
        if (cached.Kind == CacheHit.Positive)
        {
            return cached.Payload;
        }

        var payload = FetchWithRetry(fetch, ct);
        Save(source, key, payload);
        return payload;
    }

    // skipSimbad / cache-only mode (spec 9.6, used by the "rebuild targets" maintenance
    // action): consult the cache only, never call fetch, never write. A miss stays a miss.
    public string? GetCacheOnly(string source, string key)
    {
        var cached = Get(source, key);
        return cached.Kind == CacheHit.Positive ? cached.Payload : null;
    }

    // SIMBAD's dual-key case (spec 9.6, port of simbad.resolve_target_name_cached's cache
    // shape): both keys are checked before any network call, in this exact order --
    //   1. originalKey positive -> return it, no fetch.
    //   2. mappedKey (if not null) positive -> return it, no fetch.
    //      mappedKey negative -> return null immediately, regardless of originalKey's state.
    //   3. (mappedKey null or a miss) and originalKey negative -> return null.
    //   4. skipSimbad -> return null.
    //   5. otherwise: fetchMapped() first (if mappedKey is not null), falling back to
    //      fetchOriginal() when fetchMapped() returns null; the whole thing runs inside the
    //      same GetOrFetch retry/backoff wrapper, keyed on originalKey. If the mapped fetch
    //      is what produced the result, ALSO save the payload under mappedKey -- a cache
    //      write of data already fetched, not a second network call.
    public string? GetOrFetchDualKey(
        string source, string originalKey, string? mappedKey,
        Func<string?> fetchMapped, Func<string?> fetchOriginal, bool skipSimbad = false,
        CancellationToken ct = default)
    {
        var originalHit = Get(source, originalKey);
        if (originalHit.Kind == CacheHit.Positive)
        {
            return originalHit.Payload;
        }

        if (mappedKey is not null)
        {
            var mappedHit = Get(source, mappedKey);
            if (mappedHit.Kind == CacheHit.Positive)
            {
                return mappedHit.Payload;
            }
            if (mappedHit.Kind == CacheHit.Negative)
            {
                return null;
            }
            // mappedHit is Miss: fall through to the originalKey-negative check below,
            // exactly mirroring the Python source's fallthrough (it only returns None here
            // early for a POSITIVE or NEGATIVE mapped row, never for a miss).
        }

        if (originalHit.Kind == CacheHit.Negative)
        {
            return null;
        }

        if (skipSimbad)
        {
            return null;
        }

        var usedMapped = false;
        string? Fetch()
        {
            if (mappedKey is not null)
            {
                var result = fetchMapped();
                if (result is not null)
                {
                    usedMapped = true;
                    return result;
                }
            }
            usedMapped = false;
            return fetchOriginal();
        }

        // Inlined rather than delegating to GetOrFetch: originalKey's row was already read at
        // the top of this method, and GetOrFetch would open a second context to read it again
        // for a result this method already has (review fix, item 10). What is left of
        // GetOrFetch's miss path is exactly these two lines.
        var payload = FetchWithRetry(Fetch, ct);
        Save(source, originalKey, payload);
        if (usedMapped && payload is not null && mappedKey is not null)
        {
            Save(source, mappedKey, payload);
        }
        return payload;
    }

    // Deletes every negative row, across every source. Spec 9.7's retry action says "clears
    // every negative cache row", and this is that sentence as one statement: no source list
    // that has to be kept in sync with TargetResolver's stages, which is what a caller looping
    // over "resolver", "simbad" and "sesame" would become the moment a sixth source appears.
    // Positive rows are never touched (spec 9.6: they are "cleared only by the reset-database
    // action"). Returns the number of rows deleted.
    public int ClearNegative()
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_connectionString, tracking: true));
        return context.CatalogCacheEntries.Where(e => e.Negative).ExecuteDelete();
    }

    // Deletes every negative row for `source` (and `key`, if given). Used by Target detail's
    // re-resolve action (ruling Q12) so a previously-failed name gets a real re-query next
    // time, never a cache-suppressed skip.
    public int ClearNegative(string source, string? key = null)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(_connectionString, tracking: true));
        var query = context.CatalogCacheEntries.Where(e => e.Source == source && e.Negative);
        if (key is not null)
        {
            query = query.Where(e => e.Key == key);
        }
        var rows = query.ToList();
        context.CatalogCacheEntries.RemoveRange(rows);
        context.SaveChanges();
        return rows.Count;
    }

    // A cancelled `ct` ends the whole thing with OperationCanceledException and no cache
    // write: the retry guard stops catching once cancellation is requested (so a cancelled
    // fetch is not mistaken for a transient failure worth retrying), and the backoff waits on
    // the token's own handle so a cancellation mid-backoff is noticed immediately rather than
    // after the full 1s or 2s sleep (review ruling, item 14).
    private string? FetchWithRetry(Func<string?> fetch, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return fetch();
            }
            catch (NonTransientCatalogException)
            {
                throw; // No retry, no cache write. Propagates to the caller untouched.
            }
            catch (Exception) when (attempt < MaxAttempts - 1 && !ct.IsCancellationRequested)
            {
                // Transient: timeout, connection failure, 5xx, or 429. Backoff before the
                // next attempt: 1s before attempt 2, 2s before attempt 3.
                _wait(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                ct.ThrowIfCancellationRequested();
            }
        }
        // Every attempt failed transiently: the LAST attempt's exception is allowed to
        // propagate (the `when (attempt < MaxAttempts - 1)` guard above does not catch it),
        // so this line is unreachable in practice; the loop always returns or the final
        // attempt's exception escapes on its own. No explicit re-throw needed here.
        throw new UnreachableException();
    }
}
