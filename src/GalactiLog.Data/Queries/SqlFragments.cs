namespace GalactiLog.Data.Queries;

/// <summary>
/// SQL text shared by more than one dashboard query, so the queries cannot drift apart.
/// </summary>
internal static class SqlFragments
{
    /// <summary>
    /// Spec 7.5: <c>image_type</c> is stored trimmed and upper-cased by
    /// <c>MetadataExtractor</c>, and a frame with no <c>IMAGETYP</c> is stored as
    /// <c>LIGHT</c> with provenance <c>default</c>, so an exact match is the whole rule. The
    /// fragment starts with the bare column name; a query that aliases the images table
    /// prefixes it (<c>$"i.{LightFrameOnly}"</c>).
    /// </summary>
    public const string LightFrameOnly = "image_type = 'LIGHT'";

    /// <summary>
    /// Spec 12.2 step 2's group key, verbatim. Requires the images table to be aliased <c>i</c> and a
    /// <c>targets</c> join aliased <c>t</c> to be in scope for the callers that need it. One constant
    /// so the listing query, the detail queries and the header panel cannot drift: a frame that lands
    /// in two different keys lands in two groups.
    /// </summary>
    public const string GroupKeyExpression =
        "coalesce(i.resolved_target_id, 'obj:' || coalesce(nullif(json_extract(i.raw_headers,'$.OBJECT'),''), '__uncategorized__'))";

    /// <summary>
    /// Spec 9.7's unresolved-name grouping: one row per distinct non-empty <c>OBJECT</c> string
    /// over LIGHT frames with no resolved target, with its frame count. Column 0 is the raw
    /// <c>OBJECT</c> value (read with <see cref="SqlReaders.ReadText"/>, because
    /// <c>json_extract</c> yields a SQLite number for an unquoted numeric card), column 1 the
    /// count. Ordered by count descending then name, so a caller that pages or truncates is
    /// deterministic.
    /// </summary>
    /// <remarks>
    /// One constant, three callers: <c>TargetSearchQuery</c>, <c>DuplicateDetector</c> and Task 6's
    /// <c>UnresolvedNamesQuery</c>. A second copy would let the dashboard's search dropdown, the
    /// dedup pass and the Settings list disagree about which names are unresolved.
    /// </remarks>
    public const string UnresolvedObjectCounts =
        $"""
        SELECT json_extract(i.raw_headers,'$.OBJECT') AS obj, count(*)
        FROM images i
        WHERE i.{LightFrameOnly}
          AND i.resolved_target_id IS NULL
          AND obj IS NOT NULL
          AND obj <> ''
        GROUP BY obj
        ORDER BY count(*) DESC, obj
        """;

    public const string UnresolvedKeyPrefix = "obj:";

    private const string UncategorizedDisplayName = "Uncategorized";

    /// <summary>
    /// The WHERE fragment that restricts <c>images i</c> to exactly one dashboard group.
    /// <para>
    /// A key that parses as a GUID becomes <c>i.resolved_target_id = @p</c> with the value bound as a
    /// <see cref="Guid"/>, which is how <c>TargetListingQuery</c> already pins a target: never a text
    /// comparison against the group-key expression, because the storage form of a GUID column is the
    /// provider's business. Anything else is treated as an <c>obj:</c> key and compared against
    /// <see cref="GroupKeyExpression"/> itself, which is what forces both sides to text so an
    /// unquoted numeric OBJECT card (7331) still matches (Phase 5 fix F1).
    /// </para>
    /// <para>
    /// Never emits the <see cref="LightFrameOnly"/> predicate: every caller composes it with
    /// <c>$"i.{LightFrameOnly}"</c> explicitly, so a caller that wants calibration frames one day
    /// does not inherit a hidden filter.
    /// </para>
    /// </summary>
    /// <param name="groupKey">A <c>TargetRow.GroupKey</c>: either a GUID string or
    /// <c>obj:&lt;name&gt;</c>, including <c>obj:__uncategorized__</c>.</param>
    /// <param name="add">The caller's parameter-bag <c>Add</c>; returns the parameter name.</param>
    public static string GroupScope(string groupKey, Func<object?, string> add)
        => Guid.TryParse(groupKey, out var targetId)
            ? $"i.resolved_target_id = {add(targetId)}"
            : $"(i.resolved_target_id IS NULL AND {GroupKeyExpression} = {add(groupKey)})";

    /// <summary>
    /// The name a group key shows in the UI: a resolved key has a <c>targets</c> row and the caller
    /// prefers its <c>primary_name</c>, so this covers the unresolved case only. The inverse of the
    /// <c>'obj:'</c> concatenation in <see cref="GroupKeyExpression"/>, and it lives beside it so the
    /// two cannot drift.
    /// </summary>
    public static string DisplayNameOf(string groupKey)
    {
        if (!groupKey.StartsWith(UnresolvedKeyPrefix, StringComparison.Ordinal))
        {
            return groupKey;
        }

        var name = groupKey[UnresolvedKeyPrefix.Length..];
        return name == TargetListingCriteria.UncategorizedObject ? UncategorizedDisplayName : name;
    }
}
