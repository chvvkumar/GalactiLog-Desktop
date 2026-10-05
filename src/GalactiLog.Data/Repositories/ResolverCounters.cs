namespace GalactiLog.Data.Repositories;

/// <summary>
/// Spec 12.8's Resolver group: "hit and miss counters for the current process". Process
/// lifetime, never persisted, reset by a restart and by nothing else.
/// </summary>
/// <remarks>
/// <para>
/// A <b>hit</b> is a lookup <see cref="CatalogCacheRepository.Get"/> answered from the table
/// without a network call, which is <c>CacheHit.Positive</c> or <c>CacheHit.Negative</c>. A
/// <b>miss</b> is <c>CacheHit.Miss</c>, which includes an expired negative row, because spec 9.6
/// says an expired row "is treated as a miss" (coordinator ruling Q7). What the pair measures is
/// whether a scan is reaching the network, so a fresh negative row counts on the hit side.
/// </para>
/// <para>
/// <see cref="long"/> rather than <see cref="int"/>, and through <c>Interlocked</c>: a long scan
/// on a large library issues one lookup per distinct <c>OBJECT</c> per pass, and the counters are
/// read from the UI thread while the scan thread writes them.
/// </para>
/// <para>
/// <see cref="CatalogCacheRepository.Get"/> is the one increment site in the solution. Every
/// other member of that class reaches the table through it, so a second increment site would
/// double count <c>GetOrFetchDualKey</c>, which calls <c>Get</c> twice by design.
/// </para>
/// </remarks>
public sealed class ResolverCounters
{
    private long _hits;
    private long _misses;

    /// <summary>Lookups answered from <c>catalog_cache</c> without a network call.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Lookups the cache could not answer, an expired negative row included.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    public void RecordHit() => Interlocked.Increment(ref _hits);

    public void RecordMiss() => Interlocked.Increment(ref _misses);
}
