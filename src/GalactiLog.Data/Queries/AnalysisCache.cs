namespace GalactiLog.Data.Queries;

/// <summary>
/// The memoized Analysis page (spec 12.14's caching subsection): the same eight members as
/// <see cref="AnalysisQuery"/>, each building its key at this one choke point and delegating, plus
/// an explicit <see cref="Invalidate"/> the App layer calls.
/// </summary>
/// <remarks>
/// <para>
/// The key is built HERE and never by a caller (design lesson 2, applied to a cache rather than to
/// auth): a key a caller assembles is a key a caller can assemble wrongly, and the failure is
/// silently serving one tab's answer to another for a week. The eight members mirror the query's
/// exactly, so a tab view-model can be handed either and a test can pass the query directly.
/// </para>
/// <para>
/// <b>There is no time to live</b> (ruling A19). The web's <c>_ANALYSIS_CACHE_TTL</c> and
/// <c>_MATRIX_CACHE_TTL</c> are five minutes because its cache is shared between processes and it
/// cannot see an ingest; this application can, so the two real triggers are explicit and reliable
/// and ride the one <c>AppHost</c> staleness handler. There is no clock seam and no
/// <c>Func&lt;DateTime&gt;</c> parameter, exactly as <see cref="StatsCache"/>'s own remark argues
/// for itself.
/// </para>
/// <para>
/// This is the fourth cache of this family in the namespace and <see cref="StatsCache"/>'s own
/// remark says a fourth is where the spine gets extracted. It is recorded rather than done,
/// because this one is a different shape: <see cref="AliasMapCache"/>,
/// <see cref="RigBaselinesCache"/> and <see cref="StatsCache"/> are all ONE lazily built value
/// behind a gate, and this is a KEYED memo of eight different answer types with an eviction rule.
/// The part they share is the lock and the null check, about ten lines; the part they do not share
/// is all of this file. A spine over both shapes would be a generic cache with a type parameter
/// per member, which is more code than the three copies it replaces.
/// </para>
/// </remarks>
public sealed class AnalysisCache(AnalysisQuery query)
{
    /// <summary>
    /// ponytail: a hard cap with oldest-inserted eviction, not a least-recently-used one
    /// (ruling A29, question Q7). Ceiling: a reader who walks one tab's pickers past 32 entries
    /// and then returns to the first one pays for one rebuild, because the entry that leaves is
    /// the oldest INSERTED and not the least recently used. Upgrade path if a reader ever reports
    /// a slow tab switch after a long session: an access-ordered list, which is the whole of the
    /// difference. Without a cap, and with no TTL, the memo grows with every metric pair and
    /// filter combination the user visits and shrinks only at an invalidation, and one
    /// <see cref="CorrelationResult"/> holds up to <c>Analysis.CorrelationPointCap</c> points.
    /// 32 covers every tab's current state plus a deep walk of one tab's pickers, which is the
    /// pattern a reader actually has.
    /// </summary>
    public const int Capacity = 32;

    // The whole memo behind one reference, so Invalidate can swap it without touching either
    // collection. Dictionary and Queue are not thread-safe, and clearing one while a build holds
    // the gate would corrupt it; assigning this field is not a collection access at all.
    private sealed class Memo
    {
        // The value slot is the nullable wrapper the "not enough data" answer needs: presence is
        // TryGetValue's own answer and never the value's nullness, so a memoized null
        // DistributionResult is a HIT and is not recomputed on every visit. Only Distribution can
        // produce one; Compare never returns null (ruling S7).
        public Dictionary<AnalysisCacheKey, object?> Entries { get; } = [];

        // Insertion order, for the cap. No access bookkeeping, no timer, no clock.
        public Queue<AnalysisCacheKey> Order { get; } = new();
    }

    private readonly Lock _gate = new();

    private volatile Memo _memo = new();

    /// <inheritdoc cref="AnalysisQuery.Correlation"/>
    public CorrelationResult Correlation(AnalysisMetric x, AnalysisMetric y, AnalysisFilter filter)
        => Get(
            "correlation",
            $"{AnalysisMetrics.Key(x)}|{AnalysisMetrics.Key(y)}",
            filter,
            () => query.Correlation(x, y, filter));

    /// <inheritdoc cref="AnalysisQuery.Distribution"/>
    public DistributionResult? Distribution(AnalysisMetric metric, AnalysisFilter filter)
        => Get<DistributionResult?>(
            "distribution",
            AnalysisMetrics.Key(metric),
            filter,
            () => query.Distribution(metric, filter));

    /// <inheritdoc cref="AnalysisQuery.BoxPlot"/>
    public BoxPlotResult BoxPlot(AnalysisMetric metric, BoxPlotGrouping groupBy, AnalysisFilter filter)
        => Get(
            "boxplot",
            $"{AnalysisMetrics.Key(metric)}|{groupBy}",
            Frames(filter),
            () => query.BoxPlot(metric, groupBy, filter));

    /// <inheritdoc cref="AnalysisQuery.TimeSeries"/>
    public TimeSeriesResult TimeSeries(AnalysisMetric metric, AnalysisFilter filter)
        => Get(
            "timeseries",
            AnalysisMetrics.Key(metric),
            Frames(filter),
            () => query.TimeSeries(metric, filter));

    /// <inheritdoc cref="AnalysisQuery.Matrix"/>
    public MatrixResult Matrix(AnalysisFilter filter)
        => Get("matrix", "", Frames(filter), () => query.Matrix(filter));

    /// <summary>The memoized Compare tab. <b>Cached here although the web leaves it uncached</b>
    /// (it has no <c>cached_json</c> wrapper at line 821); spec 12.14 says all six.</summary>
    /// <remarks>
    /// The key carries the two group strings and the two dates and no other filter, matching the
    /// signature. The two group strings are SEPARATE members of <see cref="AnalysisCacheKey"/> and
    /// are never concatenated with anything: in Filter mode a group string IS a raw
    /// <c>images.filter_used</c> value and in Equipment mode it is built from
    /// <c>images.telescope</c> and <c>images.camera</c>, all of them catalogue strings that came
    /// from file headers, so a separator character in one of them is a value this application
    /// reads and not one it chooses. Joined into the discriminator with a bar, the pairs
    /// <c>("R|G", "B")</c> and <c>("R", "G|B")</c> build one string and the second call is served
    /// the first's boxes, medians, counts and verdict, with nothing downstream able to notice
    /// because the names come out of the memoized record. Structural equality over two separate
    /// members cannot collide, and there is no separator and no escaping rule to get wrong.
    /// </remarks>
    public CompareResult Compare(
        AnalysisMetric metric, CompareMode mode, string groupA, string groupB,
        DateOnly? from, DateOnly? to)
        => Get(
            "compare",
            $"{AnalysisMetrics.Key(metric)}|{mode}",
            new AnalysisFilter(null, null, null, AnalysisGranularity.Frame, from, to),
            () => query.Compare(metric, mode, groupA, groupB, from, to),
            groupA,
            groupB);

    /// <inheritdoc cref="AnalysisQuery.EquipmentCombinations"/>
    public IReadOnlyList<EquipmentCombination> EquipmentCombinations()
        => Get("equipment", "", Empty, query.EquipmentCombinations);

    /// <inheritdoc cref="AnalysisQuery.Filters"/>
    public IReadOnlyList<string> Filters()
        => Get("filters", "", Empty, query.Filters);

    /// <summary>Drops every memoized answer so the next read recomputes. The App layer calls this
    /// from the one staleness handler that already carries scan completion and a general settings
    /// save (ruling A6).</summary>
    /// <remarks>
    /// Takes no lock, deliberately, for the reason <see cref="StatsCache.Invalidate"/>'s own
    /// remark gives: it runs on the UI thread and must never wait on a build. It publishes a fresh
    /// memo rather than clearing the live one, so a build holding the gate keeps writing into the
    /// instance it started on, which is then discarded.
    /// </remarks>
    public void Invalidate() => _memo = new Memo();

    // The three tabs that IGNORE granularity normalise it to Frame in the key they build, because
    // without that each would keep two byte identical entries, one per granularity, against the
    // 32 entry cap. The normalisation happens here, at the one choke point that builds keys, and
    // never in the query, whose answer is the same either way.
    private static AnalysisFilter Frames(AnalysisFilter filter)
        => filter.Granularity == AnalysisGranularity.Frame
            ? filter
            : filter with { Granularity = AnalysisGranularity.Frame };

    // Neither list depends on a filter, so both sit under one fixed key.
    private static readonly AnalysisFilter Empty =
        new(null, null, null, AnalysisGranularity.Frame, null, null);

    // groupA and groupB are Compare's alone and null everywhere else: a caller value that is not a
    // closed set never joins the discriminator, it becomes its own key member.
    private T Get<T>(
        string tab, string discriminator, AnalysisFilter filter, Func<T> compute,
        string? groupA = null, string? groupB = null)
    {
        var key = new AnalysisCacheKey(tab, discriminator, filter, groupA, groupB);
        var memo = _memo;

        lock (_gate)
        {
            if (memo.Entries.TryGetValue(key, out var cached))
            {
                return (T)cached!;
            }
        }

        // Computed outside the gate: a correlation over a large library takes seconds and a
        // second tab must not wait on it. Two callers arriving on the same miss both compute and
        // the second's answer replaces the first's, which is the same answer.
        var built = compute();

        lock (_gate)
        {
            if (memo.Entries.TryAdd(key, built))
            {
                memo.Order.Enqueue(key);
                while (memo.Order.Count > Capacity)
                {
                    memo.Entries.Remove(memo.Order.Dequeue());
                }
            }
        }

        return built;
    }
}
