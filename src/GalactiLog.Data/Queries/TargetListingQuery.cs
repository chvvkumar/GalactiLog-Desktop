using System.Globalization;
using System.Text.Json;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Targets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>
/// The dashboard target list (spec 12.2), port of <c>target_listing.list_targets_aggregated</c>:
/// filter LIGHT frames on non-merged targets, group by the synthetic group key, aggregate per
/// group, apply the per-frame metric range filters of 12.2.1, return one page plus the overall
/// aggregates in a single round trip, then enrich only that page's groups.
/// </summary>
/// <remarks>
/// Hand-written SQL rather than LINQ: the group key is a <c>json_extract</c> expression, the
/// metric filters are a <c>HAVING</c> over <c>min</c>/<c>max</c>, and Task 3's header conditions
/// are dynamic. Every user-supplied value is a bound parameter without exception; no user text is
/// ever concatenated into the command text. Commands go through a short-lived
/// <see cref="GalactiLogContext"/> so <see cref="PragmaConnectionInterceptor"/> stays in the path
/// and the busy timeout of spec 5.1 applies to dashboard reads taken while a scan writes.
/// </remarks>
public sealed class TargetListingQuery(DatabaseConnectionString connectionString, AliasMapCache aliases)
{
    // The group key, the group-scope filter and the unresolved display name all live in
    // SqlFragments now: TargetDetailQuery is the second query that has to name a group and
    // SessionDetailQuery and FrameHeadersQuery are the third and fourth (design-lessons rule 1).
    private const string UnresolvedKeyPrefix = SqlFragments.UnresolvedKeyPrefix;
    private const string UnknownFilterName = "Unknown";

    public TargetListingPage List(TargetListingCriteria criteria)
    {
        var map = aliases.Current;
        var page = Math.Max(1, criteria.Page);
        var pageSize = Math.Max(1, criteria.PageSize);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        var parameters = new SqlParameters();
        var baseFilter = BuildBaseFilter(criteria, map, parameters, connection);
        var cte = BuildCte(baseFilter, BuildMetricHaving(criteria, parameters));

        var (rows, totals) = ReadPageAndTotals(connection, cte, criteria, page, pageSize, parameters);
        if (rows.Count == 0)
        {
            return new TargetListingPage([], totals.Groups, totals.Integration, totals.Frames, page, pageSize);
        }

        var enrichment = ReadEnrichment(connection, baseFilter, rows, parameters, map);
        var targetRows = new List<TargetRow>(rows.Count);
        foreach (var row in rows)
        {
            targetRows.Add(ToTargetRow(row, enrichment, map));
        }

        return new TargetListingPage(targetRows, totals.Groups, totals.Integration, totals.Frames, page, pageSize);
    }

    // ---- step 1: the base filter -------------------------------------------------------

    private static string BuildBaseFilter(
        TargetListingCriteria criteria,
        AliasMap map,
        SqlParameters parameters,
        SqliteConnection connection)
    {
        var clauses = new List<string>
        {
            $"i.{SqlFragments.LightFrameOnly}",
            "(i.resolved_target_id IS NULL OR t.merged_into_id IS NULL)",
        };

        if (criteria.TargetId is { } targetId)
        {
            clauses.Add(SqlFragments.GroupScope(targetId.ToString(), parameters.Add));
        }

        if (!string.IsNullOrWhiteSpace(criteria.UnresolvedObject))
        {
            // F1 lives in SqlFragments.GroupScope: it compares the group-key expression itself,
            // not the bare json_extract, so an unquoted numeric OBJECT card still matches.
            clauses.Add(SqlFragments.GroupScope(
                UnresolvedKeyPrefix + criteria.UnresolvedObject,
                parameters.Add));
        }

        if (criteria.SessionDateFrom is { } from)
        {
            clauses.Add($"i.session_date >= {parameters.Add(from.ToString(SqlReaders.DateFormat, CultureInfo.InvariantCulture))}");
        }

        if (criteria.SessionDateTo is { } to)
        {
            clauses.Add($"i.session_date <= {parameters.Add(to.ToString(SqlReaders.DateFormat, CultureInfo.InvariantCulture))}");
        }

        AddExpansionClause(clauses, parameters, "i.filter_used", criteria.Filters, map.ExpandFilter);
        if (!string.IsNullOrWhiteSpace(criteria.Camera))
        {
            AddExpansionClause(clauses, parameters, "i.camera", [criteria.Camera], map.ExpandCamera);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Telescope))
        {
            AddExpansionClause(clauses, parameters, "i.telescope", [criteria.Telescope], map.ExpandTelescope);
        }

        if (criteria.ObjectCategories.Count > 0)
        {
            clauses.Add(BuildObjectCategoryClause(criteria.ObjectCategories, parameters, connection));
        }

        // Task 3 (spec 12.3) appends its validated FITS header condition clauses here. It must
        // not restructure the CTE around this call.
        HeaderQueryBuilder.Append(criteria.HeaderConditions, clauses, parameters.Add);

        // Phase 20 (spec 12.15) appends its custom column clauses here, after the header
        // conditions and before the join.
        AppendCustomFilters(criteria.CustomFilters, clauses, parameters, connection);

        return string.Join("\n    AND ", clauses);
    }

    // ---- spec 12.15's custom column filters --------------------------------------------

    /// <summary>
    /// Appends one <c>EXISTS</c> per active custom filter, all combined with <c>AND</c> with each
    /// other and with every other filter section. Every custom filter is target level whatever the
    /// column's scope (user choice 5): a night-scope filter says "this target has at least one
    /// night with this value", never "show me only those nights", because the dashboard's unit is
    /// a target and the rows it returns are whole targets.
    /// </summary>
    /// <remarks>
    /// Internal for the composed-clause scan of
    /// <c>TargetListingCustomFilterTests.NoUserTextReachesTheCommandText</c>: the rule stated at
    /// the top of this file is worth a case that reads the text this method actually produces
    /// rather than one that infers it from a result set.
    /// </remarks>
    internal static void AppendCustomFilters(
        IReadOnlyList<CustomColumnFilter> filters,
        List<string> clauses,
        SqlParameters parameters,
        SqliteConnection connection)
    {
        if (filters.Count == 0)
        {
            // The one extra round trip below is the only one this feature adds, and a library
            // that filters on no custom column never pays it.
            return;
        }

        var columns = ReadCustomColumns(connection);
        foreach (var filter in filters)
        {
            // An unknown slug is dropped silently, the way an unknown metric key already is
            // (spec 12.2.1): the column was deleted while its filter was still set. A dropdown
            // filter naming an option the column no longer offers is NOT dropped, because that
            // would silently widen the result; it is applied and matches nothing.
            if (!columns.TryGetValue(filter.Slug, out var column))
            {
                continue;
            }

            var columnId = parameters.Add(column.Id);
            if (ValuePredicate(filter, parameters) is not { } value)
            {
                continue;
            }

            // cv.target_id = i.resolved_target_id, NOT t.id. The CTE groups frames by the
            // synthetic group key and LEFT JOINs targets, so t is null for an unresolved group
            // while the frame's own column is always present. An unresolved group's null id
            // therefore matches no custom filter, which is correct rather than an omission: an
            // unresolved OBJECT string has no target row and can hold no custom value.
            clauses.Add(
                $"""
                EXISTS (SELECT 1 FROM custom_column_values cv
                          WHERE cv.column_id = {columnId}
                            AND cv.target_id = i.resolved_target_id
                            {ScopePredicate(column.Scope)}
                            {value})
                """);
        }
    }

    // Spec 12.15's key rule (core-shapes section 5.3): the parts a scope leaves unset are SQL
    // null, so a target-scope filter must require BOTH null, or a night value would satisfy it.
    private static string ScopePredicate(CustomColumnScope scope) => scope switch
    {
        CustomColumnScope.Session => "AND cv.session_date IS NOT NULL",
        CustomColumnScope.Rig => "AND cv.rig_label IS NOT NULL",
        // target, and the reserved mosaic scope (U1), which no surface can create: the repository
        // refuses it, and a mosaic row's null target_id matches no group in any case.
        _ => "AND cv.session_date IS NULL AND cv.rig_label IS NULL",
    };

    // Null means "contributes no clause". Any never reaches the criteria at all (the panel drops
    // it), and neither does a Contains whose text is blank; both are checked again here so that
    // TargetListingCriteria.AnyFilterActive cannot disagree with the SQL even if a caller other
    // than the panel builds the list.
    private static string? ValuePredicate(CustomColumnFilter filter, SqlParameters parameters) => filter.Mode switch
    {
        // A target with no value row at all fails both Yes and No: that follows from the EXISTS
        // and it is spec 12.15's own sentence.
        CustomFilterMode.Yes => $"AND cv.value = {parameters.Add(CustomColumnSlug.True)}",
        CustomFilterMode.No => $"AND cv.value = {parameters.Add(CustomColumnSlug.False)}",

        // Departure 17: an explicit ESCAPE, which the web omits. SQLite's LIKE is case
        // insensitive for ASCII by default, which is the folding the table promises.
        CustomFilterMode.Contains when !string.IsNullOrWhiteSpace(filter.Text) =>
            $"AND cv.value LIKE {parameters.Add("%" + HeaderQueryBuilder.EscapeLike(filter.Text.Trim()) + "%")} ESCAPE '\\'",

        // Exact and case sensitive: the column carries no NOCASE collation and an option is a
        // stored spelling, not free text.
        CustomFilterMode.Equals when filter.Text is not null =>
            $"AND cv.value = {parameters.Add(filter.Text)}",

        _ => null,
    };

    // The one new round trip, taken inside the connection BuildBaseFilter already holds.
    private static Dictionary<string, (Guid Id, CustomColumnScope Scope)> ReadCustomColumns(
        SqliteConnection connection)
    {
        var columns = new Dictionary<string, (Guid Id, CustomColumnScope Scope)>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, slug, applies_to FROM custom_columns;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // A hand-edited applies_to outside the set parses to null and the row is skipped,
            // exactly as the repository skips it, rather than throwing the listing away.
            if (CustomColumnSlug.ParseScope(SqlReaders.ReadText(reader, 2)) is { } scope)
            {
                columns[reader.GetString(1)] = (reader.GetGuid(0), scope);
            }
        }

        return columns;
    }

    // Spec 12.2: filter and equipment criteria are expanded through the alias map before
    // matching; the stored column is never folded (that would mean reimplementing the alias map
    // as a CASE expression in SQL, in a second language). Q13: filter_used, camera and telescope
    // carry no NOCASE collation, and both sides are user data written at different times, so the
    // comparison folds case explicitly. The lower() call forfeits the column's index, which is
    // acceptable at this scale and no worse than the group key, which forces a scan anyway.
    private static void AddExpansionClause(
        List<string> clauses,
        SqlParameters parameters,
        string column,
        IReadOnlyList<string> selected,
        Func<string, IReadOnlyList<string>> expand)
    {
        var raw = new SortedSet<string>(StringComparer.Ordinal);
        var anySelected = false;
        foreach (var canonical in selected)
        {
            if (string.IsNullOrWhiteSpace(canonical))
            {
                continue;
            }

            anySelected = true;
            foreach (var name in expand(canonical))
            {
                raw.Add(name.Trim().ToLowerInvariant());
            }
        }

        if (raw.Count == 0)
        {
            // F3: a selection whose expansion yields nothing matches nothing. Dropping the clause
            // would widen the result instead, which is the opposite of what the user asked for.
            // Nothing selected at all still contributes no clause, and AnyFilterActive agrees.
            if (anySelected)
            {
                clauses.Add("1 = 0");
            }

            return;
        }

        clauses.Add($"lower({column}) IN ({string.Join(", ", raw.Select(name => parameters.Add(name)))})");
    }

    // ObjectTypeCategories.Categorize is C# and cannot run in SQLite, so the category selection
    // is resolved in memory to an id list first (Q9).
    //
    // ponytail: this is a full read of the targets table on every dashboard query that has an
    // Object Type pill selected. Ceiling: a library with tens of thousands of targets. Upgrade
    // path if it ever matters: store the display category on targets as a column written at
    // resolve time and filter on it directly. Do not build that now.
    private static string BuildObjectCategoryClause(
        IReadOnlyList<string> categories,
        SqlParameters parameters,
        SqliteConnection connection)
    {
        var selected = new HashSet<string>(
            categories.Where(category => !string.IsNullOrWhiteSpace(category)),
            StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0)
        {
            return "1 = 1";
        }

        var includeUnresolved = selected.Remove(TargetListingCriteria.UnresolvedCategory);

        var ids = new List<Guid>();
        if (selected.Count > 0)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, object_type FROM targets WHERE merged_into_id IS NULL;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var category = ObjectTypeCategories.Categorize(reader.IsDBNull(1) ? null : reader.GetString(1));
                if (selected.Contains(category))
                {
                    ids.Add(reader.GetGuid(0));
                }
            }
        }

        if (ids.Count == 0)
        {
            // No target matches: the result must be empty rather than unfiltered.
            return includeUnresolved ? "i.resolved_target_id IS NULL" : "1 = 0";
        }

        var idList = string.Join(", ", ids.Select(id => parameters.Add(id)));
        return includeUnresolved
            ? $"(i.resolved_target_id IS NULL OR i.resolved_target_id IN ({idList}))"
            : $"i.resolved_target_id IN ({idList})";
    }

    // ---- step 4: the metric range filters (spec 12.2.1) --------------------------------

    private static string BuildMetricHaving(TargetListingCriteria criteria, SqlParameters parameters)
    {
        var clauses = new List<string>();
        foreach (var (key, range) in criteria.MetricRanges.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            // An unknown metric key is dropped, exactly like an invalid header key. The column
            // name comes from the map, never from user text.
            if (!range.IsSet || !MetricColumns.ByKey.TryGetValue(key, out var column))
            {
                continue;
            }

            if (range.Min is { } low)
            {
                clauses.Add($"(min(i.{column}) >= {parameters.Add(low)} OR min(i.{column}) IS NULL)");
            }

            if (range.Max is { } high)
            {
                clauses.Add($"(max(i.{column}) <= {parameters.Add(high)} OR max(i.{column}) IS NULL)");
            }
        }

        return clauses.Count == 0 ? "" : "\n  HAVING " + string.Join("\n     AND ", clauses);
    }

    // ---- steps 2, 3 and 5: grouping, aggregates, page and totals in one round trip -----

    private static string BuildCte(string where, string having) =>
        $"""
        WITH g AS (
          SELECT {SqlFragments.GroupKeyExpression} AS gk,
                 max(i.resolved_target_id) AS target_id,
                 sum(coalesce(i.exposure_time, 0)) AS integration_seconds,
                 count(*) AS frame_count,
                 count(DISTINCT i.session_date) AS session_count,
                 min(i.session_date) AS first_session,
                 max(i.session_date) AS last_session,
                 min(trim(coalesce(i.telescope,'') || ' ' || coalesce(i.camera,''))) AS equipment_sort
          FROM images i
          LEFT JOIN targets t ON t.id = i.resolved_target_id
          WHERE {where}
          GROUP BY gk{having}
        )
        """;

    private static (List<RawRow> Rows, (int Groups, double Integration, int Frames) Totals) ReadPageAndTotals(
        SqliteConnection connection,
        string cte,
        TargetListingCriteria criteria,
        int page,
        int pageSize,
        SqlParameters parameters)
    {
        var limit = parameters.Add(pageSize);
        var offset = parameters.Add((long)(page - 1) * pageSize);
        var direction = criteria.Descending ? "DESC" : "ASC";

        using var command = connection.CreateCommand();
        // Two statements, one round trip (spec 12.2 step 5), read with NextResult. SQLite scopes
        // a WITH clause to a single statement, so the CTE text is emitted before each of them.
        command.CommandText =
            $"""
            {cte}
            SELECT g.gk, g.target_id, g.integration_seconds, g.frame_count, g.session_count,
                   g.first_session, g.last_session, g.equipment_sort,
                   t.primary_name, t.common_name, t.catalog_id, t.object_type
            FROM g
            LEFT JOIN targets t ON t.id = g.target_id
            ORDER BY {SortExpression(criteria.Sort)} {direction}, g.gk ASC
            LIMIT {limit} OFFSET {offset};

            {cte}
            SELECT count(*), coalesce(sum(integration_seconds), 0), coalesce(sum(frame_count), 0) FROM g;
            """;
        parameters.ApplyTo(command);

        var rows = new List<RawRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RawRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                SqlReaders.ReadDouble(reader, 2),
                SqlReaders.ReadInt(reader, 3),
                SqlReaders.ReadInt(reader, 4),
                SqlReaders.ReadDate(reader, 5),
                SqlReaders.ReadDate(reader, 6),
                SqlReaders.ReadText(reader, 8),
                SqlReaders.ReadText(reader, 9),
                SqlReaders.ReadText(reader, 10),
                SqlReaders.ReadText(reader, 11)));
        }

        var totals = (Groups: 0, Integration: 0d, Frames: 0);
        if (reader.NextResult() && reader.Read())
        {
            totals = (SqlReaders.ReadInt(reader, 0), SqlReaders.ReadDouble(reader, 1), SqlReaders.ReadInt(reader, 2));
        }

        return (rows, totals);
    }

    private static string SortExpression(TargetListingSort sort) => sort switch
    {
        // Phase review item 7: an unresolved group's key is 'obj:' || OBJECT, and sorting on the
        // raw key filed every unresolved group under "o". The prefix is stripped so a group sorts
        // under the name the Name column actually shows.
        TargetListingSort.Name =>
            $"lower(coalesce(t.primary_name, CASE WHEN g.gk LIKE '{UnresolvedKeyPrefix}%'"
                + $" THEN substr(g.gk, {UnresolvedKeyPrefix.Length + 1}) ELSE g.gk END))",
        TargetListingSort.Integration => "g.integration_seconds",
        TargetListingSort.Frames => "g.frame_count",
        TargetListingSort.Sessions => "g.session_count",
        TargetListingSort.LastSession => "g.last_session",
        // Q10: a group has a set of rigs, not one value. The group's alphabetically first raw
        // rig string is stable, computable in the same aggregate pass, and matches what the
        // Equipment column shows when a group has one rig. It sorts on raw rather than canonical
        // names, since the alias fold happens in C# after the page slice.
        TargetListingSort.Equipment => "lower(g.equipment_sort)",
        _ => "g.last_session",
    };

    // ---- step 6: the second pass, over the page's groups only --------------------------

    private static Enrichment ReadEnrichment(
        SqliteConnection connection,
        string baseFilter,
        List<RawRow> rows,
        SqlParameters parameters,
        AliasMap map)
    {
        var keyList = string.Join(", ", rows.Select(row => parameters.Add(row.GroupKey)));
        var targetIds = rows.Where(row => row.TargetId is not null).Select(row => row.TargetId!.Value).ToList();
        var targetIdClause = targetIds.Count == 0
            ? "1 = 0"
            : $"id IN ({string.Join(", ", targetIds.Select(id => parameters.Add(id)))})";

        // The same base filter as the page query, so palette, equipment and sessions describe the
        // filtered set rather than the whole group. The metric HAVING is not repeated: the key
        // list already restricts this to groups that passed it.
        var scope =
            $"""
            FROM images i
            LEFT JOIN targets t ON t.id = i.resolved_target_id
            WHERE {baseFilter}
              AND {SqlFragments.GroupKeyExpression} IN ({keyList})
            """;

        using var command = connection.CreateCommand();
        // Spec 12.2's Filters column (the fifth result set below) needs the intersection of this
        // pass's set 1 (filter distribution, no date) and set 3 (sessions, no filter): one more
        // result set on the same command, not a second round trip. Re-rolling set 1's own grouping
        // to add session_date was refused (task6.md 5.1): it would change the Palette column's own
        // aggregation, which 38 TargetListingQueryTests cases cover.
        command.CommandText =
            $"""
            SELECT {SqlFragments.GroupKeyExpression} AS gk, i.filter_used, count(*), sum(coalesce(i.exposure_time, 0))
            {scope}
            GROUP BY gk, i.filter_used;

            SELECT DISTINCT {SqlFragments.GroupKeyExpression} AS gk, i.telescope, i.camera
            {scope};

            SELECT {SqlFragments.GroupKeyExpression} AS gk, i.session_date, count(*), sum(coalesce(i.exposure_time, 0))
            {scope}
              AND i.session_date IS NOT NULL
            GROUP BY gk, i.session_date
            ORDER BY i.session_date DESC;

            SELECT id, aliases FROM targets WHERE {targetIdClause};

            SELECT {SqlFragments.GroupKeyExpression} AS gk, i.session_date, i.filter_used, count(*), sum(coalesce(i.exposure_time, 0))
            {scope}
              AND i.session_date IS NOT NULL
            GROUP BY gk, i.session_date, i.filter_used;
            """;
        parameters.ApplyTo(command);

        var palette = new Dictionary<string, Dictionary<string, FilterBadgeAccumulator>>(StringComparer.Ordinal);
        var equipment = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var sessionRaw = new Dictionary<string, List<(DateOnly Date, int FrameCount, double IntegrationSeconds)>>(StringComparer.Ordinal);
        var aliasesByTarget = new Dictionary<Guid, IReadOnlyList<string>>();
        var sessionFilters = new Dictionary<string, Dictionary<DateOnly, Dictionary<string, FilterBadgeAccumulator>>>(StringComparer.Ordinal);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (!palette.TryGetValue(key, out var bucket))
            {
                palette[key] = bucket = new Dictionary<string, FilterBadgeAccumulator>(StringComparer.OrdinalIgnoreCase);
            }

            // Q14: null or empty filter_used folds to the canonical name "Unknown" with the
            // default colour, so the badge counts always sum to the group's frame count.
            var canonical = map.CanonicalFilter(SqlReaders.ReadText(reader, 1)) ?? UnknownFilterName;
            if (!bucket.TryGetValue(canonical, out var accumulator))
            {
                bucket[canonical] = accumulator = new FilterBadgeAccumulator(canonical);
            }

            accumulator.FrameCount += SqlReaders.ReadInt(reader, 2);
            accumulator.IntegrationSeconds += SqlReaders.ReadDouble(reader, 3);
        }

        reader.NextResult();
        while (reader.Read())
        {
            var telescope = map.CanonicalTelescope(SqlReaders.ReadText(reader, 1));
            var camera = map.CanonicalCamera(SqlReaders.ReadText(reader, 2));
            var rig = string.Join(" / ", new[] { telescope, camera }.Where(part => !string.IsNullOrWhiteSpace(part)));
            if (rig.Length == 0)
            {
                continue;
            }

            var key = reader.GetString(0);
            if (!equipment.TryGetValue(key, out var rigs))
            {
                equipment[key] = rigs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            rigs.Add(rig);
        }

        reader.NextResult();
        while (reader.Read())
        {
            // The statement's own WHERE keeps the NULL dates out, but nothing constrains the stored
            // text to the yyyy-MM-dd form, so a hand-edited or imported row can hold '' or a
            // timestamp. Such a row is skipped before its group gets a bucket, exactly as the fifth
            // result set below already skips one, so it is indistinguishable from a row the
            // statement never returned: the dashboard lists the target without that session rather
            // than throwing the whole listing read away (review P2-3, coordinator ruling).
            if (SqlReaders.ReadDate(reader, 1) is not { } date)
            {
                continue;
            }

            var key = reader.GetString(0);
            if (!sessionRaw.TryGetValue(key, out var raw))
            {
                sessionRaw[key] = raw = [];
            }

            raw.Add((date, SqlReaders.ReadInt(reader, 2), SqlReaders.ReadDouble(reader, 3)));
        }

        reader.NextResult();
        while (reader.Read())
        {
            aliasesByTarget[reader.GetGuid(0)] = SqlReaders.ParseAliases(SqlReaders.ReadText(reader, 1));
        }

        // The fifth result set: spec 12.2's Filters column. Deliberately not Q14's Unknown fold: a
        // frame with a null or empty filter_used contributes no name at all here, so a session
        // whose every frame lacks one ends with no bucket at all and renders an empty cell.
        reader.NextResult();
        while (reader.Read())
        {
            var date = SqlReaders.ReadDate(reader, 1);
            if (date is null)
            {
                continue;
            }

            var canonical = map.CanonicalFilter(SqlReaders.ReadText(reader, 2));
            if (canonical is null)
            {
                continue;
            }

            var key = reader.GetString(0);
            if (!sessionFilters.TryGetValue(key, out var byDate))
            {
                sessionFilters[key] = byDate = [];
            }

            if (!byDate.TryGetValue(date.Value, out var bucket))
            {
                byDate[date.Value] = bucket = new Dictionary<string, FilterBadgeAccumulator>(StringComparer.OrdinalIgnoreCase);
            }

            if (!bucket.TryGetValue(canonical, out var accumulator))
            {
                bucket[canonical] = accumulator = new FilterBadgeAccumulator(canonical);
            }

            accumulator.FrameCount += SqlReaders.ReadInt(reader, 3);
            accumulator.IntegrationSeconds += SqlReaders.ReadDouble(reader, 4);
        }

        // Sessions are finished only now that the fifth set has been read, so each SessionSummary
        // can carry its own night's filters at construction rather than being patched afterward.
        var sessions = new Dictionary<string, List<SessionSummary>>(StringComparer.Ordinal);
        foreach (var (key, raw) in sessionRaw)
        {
            sessionFilters.TryGetValue(key, out var byDate);
            var summaries = new List<SessionSummary>(raw.Count);
            foreach (var item in raw)
            {
                var filters = byDate is not null && byDate.TryGetValue(item.Date, out var bucket)
                    ? OrderedSessionFilters(bucket, map)
                    : (IReadOnlyList<FilterBadge>)[];
                summaries.Add(new SessionSummary(item.Date, item.FrameCount, item.IntegrationSeconds, filters));
            }

            sessions[key] = summaries;
        }

        return new Enrichment(palette, equipment, sessions, aliasesByTarget);
    }

    // Spec 12.2: "in the order the Filters tab stores them", which is AliasMap.ConfiguredFilters'
    // order, not the query's own encounter order and not alphabetical. A canonical name outside
    // that configured list (an unconfigured filter written straight through) sorts after every
    // configured one, alphabetically among themselves, rather than being dropped.
    private static IReadOnlyList<FilterBadge> OrderedSessionFilters(
        Dictionary<string, FilterBadgeAccumulator> bucket, AliasMap map)
    {
        var configured = map.ConfiguredFilters;
        return [.. bucket.Values
            .OrderBy(accumulator => ConfiguredIndex(configured, accumulator.CanonicalName))
            .ThenBy(accumulator => accumulator.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .Select(accumulator => new FilterBadge(
                accumulator.CanonicalName,
                map.FilterColor(accumulator.CanonicalName),
                accumulator.FrameCount,
                accumulator.IntegrationSeconds))];
    }

    private static int ConfiguredIndex(IReadOnlyList<string> configured, string canonical)
    {
        for (var i = 0; i < configured.Count; i++)
        {
            if (string.Equals(configured[i], canonical, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static TargetRow ToTargetRow(RawRow row, Enrichment enrichment, AliasMap map)
    {
        IReadOnlyList<FilterBadge> badges = enrichment.Palette.TryGetValue(row.GroupKey, out var bucket)
            ? [.. bucket.Values
                .OrderByDescending(accumulator => accumulator.FrameCount)
                .ThenBy(accumulator => accumulator.CanonicalName, StringComparer.OrdinalIgnoreCase)
                .Select(accumulator => new FilterBadge(
                    accumulator.CanonicalName,
                    map.FilterColor(accumulator.CanonicalName),
                    accumulator.FrameCount,
                    accumulator.IntegrationSeconds))]
            : [];

        IReadOnlyList<string> rigs = enrichment.Equipment.TryGetValue(row.GroupKey, out var equipment)
            ? [.. equipment]
            : [];

        IReadOnlyList<SessionSummary> sessions = enrichment.Sessions.TryGetValue(row.GroupKey, out var summaries)
            ? summaries
            : [];

        IReadOnlyList<string> aliases = row.TargetId is { } id && enrichment.Aliases.TryGetValue(id, out var stored)
            ? stored
            : [];

        return new TargetRow(
            row.GroupKey,
            row.TargetId,
            row.PrimaryName ?? SqlFragments.DisplayNameOf(row.GroupKey),
            row.CommonName,
            row.CatalogId,
            row.ObjectType,
            row.TargetId is null
                ? TargetListingCriteria.UnresolvedCategory
                : ObjectTypeCategories.Categorize(row.ObjectType),
            row.IntegrationSeconds,
            row.FrameCount,
            row.SessionCount,
            row.FirstSession,
            row.LastSession,
            badges,
            rigs,
            aliases,
            sessions);
    }

    private sealed record RawRow(
        string GroupKey,
        Guid? TargetId,
        double IntegrationSeconds,
        int FrameCount,
        int SessionCount,
        DateOnly? FirstSession,
        DateOnly? LastSession,
        string? PrimaryName,
        string? CommonName,
        string? CatalogId,
        string? ObjectType);

    private sealed record Enrichment(
        Dictionary<string, Dictionary<string, FilterBadgeAccumulator>> Palette,
        Dictionary<string, SortedSet<string>> Equipment,
        Dictionary<string, List<SessionSummary>> Sessions,
        Dictionary<Guid, IReadOnlyList<string>> Aliases);

    private sealed class FilterBadgeAccumulator(string canonicalName)
    {
        public string CanonicalName { get; } = canonicalName;
        public int FrameCount { get; set; }
        public double IntegrationSeconds { get; set; }
    }
}
