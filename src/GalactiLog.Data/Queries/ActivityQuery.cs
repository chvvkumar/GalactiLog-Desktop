using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>
/// Spec 12.6's three activity filters. Every field is optional and null means "no filter", which
/// is the "all" pill in the view.
/// </summary>
/// <param name="Severity">One of <see cref="ActivityQuery.ValidSeverities"/>. An unrecognized
/// value is dropped rather than rejected, matching
/// <c>backend/app/api/activity.py::list_activity</c>, which filters the requested list down to
/// the valid set and treats an empty result as no filter at all.</param>
/// <param name="Category">One of <see cref="ActivityQuery.ValidCategories"/>, under the same
/// drop-the-unknown rule. The web's <c>mosaic</c> category is not in the port (spec 5.12,
/// 19.1).</param>
/// <param name="Search">Spec 12.6's free-text search over the message. Null or blank means no
/// text filter. The web has no such parameter on this endpoint; spec 12.6 asks for one, so the
/// port adds it as a bound <c>LIKE</c> and nothing else.</param>
public sealed record ActivityFilters(
    string? Severity = null,
    string? Category = null,
    string? Search = null);

/// <summary>
/// The keyset position a page continues from: the last row of the previous page. Rows strictly
/// older than this point come back, where "older" is the pair comparison
/// <c>(timestamp &lt; Timestamp) OR (timestamp = Timestamp AND id &lt; Id)</c>.
/// </summary>
/// <remarks>
/// <para>
/// A typed record rather than the web's base64 blob. <c>_encode_cursor</c> exists because the
/// value crosses an HTTP boundary and has to survive a query string; here it crosses a method
/// call inside one process. Encoding it would add a parse-failure path (the web's
/// <c>_decode_cursor</c> swallows every exception and silently restarts at page one) with nothing
/// to gain.
/// </para>
/// <para>
/// The pair, not the id alone: several events of one scan are written in the same
/// <c>SaveChanges</c> batch and <c>ActivityRepository.Emit</c> stamps <c>DateTime.UtcNow</c> per
/// event, so two rows sharing a timestamp is a matter of clock resolution rather than a freak
/// case. A cursor on <c>timestamp</c> alone loses or repeats rows across such a tie; the pair is
/// what makes paging total. Phase 10's log viewer reuses this contract.
/// </para>
/// </remarks>
public sealed record ActivityCursor(DateTime Timestamp, int Id);

/// <summary>One <c>activity_events</c> row, every column of spec 5.12 except the dropped web
/// <c>actor</c>.</summary>
/// <param name="Timestamp">UTC. The column is written by <c>ActivityRepository.Emit</c> from
/// <c>DateTime.UtcNow</c> and <c>SqliteDataReader.GetDateTime</c> hands it back with an
/// unspecified kind, so it is pinned to <see cref="DateTimeKind.Utc"/> here and every consumer
/// can convert without guessing.</param>
/// <param name="Details">The raw JSON document, or null. Parsed by the view layer, never here: a
/// details payload is free-form per event type (spec 10.9) and this query has no business knowing
/// the shapes.</param>
/// <param name="ParentId">Null for a top-level row. Always null for a row in
/// <see cref="ActivityPage.Rows"/> and always set for a row in
/// <see cref="ActivityPage.Children"/>.</param>
public sealed record ActivityRow(
    int Id,
    DateTime Timestamp,
    string Severity,
    string Category,
    string EventType,
    string Message,
    string? Details,
    Guid? TargetId,
    int? DurationMs,
    int? ParentId);

/// <summary>One page of spec 12.6's feed.</summary>
/// <param name="Rows">Top-level events only, newest first.</param>
/// <param name="Children">The sub-events of the rows on this page, keyed by parent id and ordered
/// oldest first. A parent with no children has no entry. Children are never paginated and their
/// total can exceed <c>limit</c>, matching the web.</param>
/// <param name="NextCursor">Non-null only when the page came back exactly full, matching the
/// web's <c>next_cursor if len(rows) == limit else None</c>. A full final page therefore costs one
/// extra empty request, which is the price of not counting the remainder on every call.</param>
/// <param name="Total">The full filtered count, computed WITHOUT the cursor predicate, matching
/// the web. It is "how many events match these filters", not "how many are left".</param>
public sealed record ActivityPage(
    IReadOnlyList<ActivityRow> Rows,
    IReadOnlyDictionary<int, IReadOnlyList<ActivityRow>> Children,
    ActivityCursor? NextCursor,
    int Total);

/// <summary>
/// Spec 12.6's read: a reverse-chronological page of top-level <c>activity_events</c> with
/// keyset pagination on <c>(timestamp, id)</c>, plus the sub-events of the rows on that page.
/// Mirrors <c>backend/app/api/activity.py::list_activity</c>. Read-only.
/// </summary>
/// <remarks>
/// <para>
/// House rules (spec 4.3): it lives in <c>GalactiLog.Data.Queries</c>, takes the DI
/// <see cref="DatabaseConnectionString"/>, writes nothing and binds every operand through
/// <see cref="SqlParameters"/>. It takes no group key, unlike <c>TargetDetailQuery</c> and
/// <c>SessionDetailQuery</c>: an activity event belongs to the whole library, not to a dashboard
/// grouping, and <c>target_id</c> is a nullable annotation on some rows rather than a scope.
/// </para>
/// <para>
/// Nothing here writes or deletes a row. <c>ActivityRepository</c> is the one writer and the one
/// deleter (spec 10.9), and the page's "prune now" action calls
/// <c>ActivityRepository.PruneRetention</c>.
/// </para>
/// </remarks>
public sealed class ActivityQuery(DatabaseConnectionString connectionString)
{
    /// <summary>The web's <c>Query(50, ge=1, le=200)</c> default.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The web's <c>ge=1</c>.</summary>
    public const int MinLimit = 1;

    /// <summary>The web's <c>le=200</c>, and <c>ActivityFilterParams.cap_limit</c>.</summary>
    public const int MaxLimit = 200;

    /// <summary>Spec 5.12's severity vocabulary, the web's <c>_VALID_SEVERITIES</c>.</summary>
    public static readonly IReadOnlyList<string> ValidSeverities = ["info", "warning", "error"];

    /// <summary>Spec 5.12's category vocabulary, the web's <c>_VALID_CATEGORIES</c> minus
    /// <c>mosaic</c> (spec 19.1 defers mosaics and spec 5.12's list omits the category).</summary>
    public static readonly IReadOnlyList<string> ValidCategories =
        ["scan", "rebuild", "thumbnail", "enrichment", "migration", "user_action", "system"];

    private static readonly HashSet<string> SeveritySet = new(ValidSeverities, StringComparer.Ordinal);

    private static readonly HashSet<string> CategorySet = new(ValidCategories, StringComparer.Ordinal);

    // Spec 5.12's columns, in one place, so the page read and the child read cannot drift into
    // different ordinals.
    private const string Columns =
        "id, timestamp, severity, category, event_type, message, details, target_id, duration_ms, parent_id";

    /// <summary>
    /// One page of the feed.
    /// </summary>
    /// <param name="filters">Severity, category and free text. Unknown severities and categories
    /// are dropped rather than rejected.</param>
    /// <param name="before">The previous page's <see cref="ActivityPage.NextCursor"/>, or null for
    /// the first page.</param>
    /// <param name="limit">Clamped to <see cref="MinLimit"/>..<see cref="MaxLimit"/> rather than
    /// validated: the caller is a UI control, and a page size out of range is a bug to correct
    /// silently, not a reason to fail a read of the log.</param>
    public ActivityPage Page(
        ActivityFilters filters, ActivityCursor? before = null, int limit = DefaultLimit)
    {
        var pageSize = Math.Clamp(limit, MinLimit, MaxLimit);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        // One deferred read transaction across all three statements, the shape StatsQuery
        // established: without it a scan committing between the page read and the count yields a
        // total that disagrees with the rows, and a child read that misses a sub-event the page
        // read implies. Deferred, so this takes a read lock at the first statement and never a
        // write lock.
        using var snapshot = connection.BeginTransaction(deferred: true);

        var rows = ReadPage(connection, filters, before, pageSize);
        var total = ReadTotal(connection, filters);
        var children = ReadChildren(connection, rows);

        snapshot.Commit();

        // The web's rule, verbatim: a cursor only when the page came back exactly full.
        var nextCursor = rows.Count == pageSize && rows.Count > 0
            ? new ActivityCursor(rows[^1].Timestamp, rows[^1].Id)
            : null;

        return new ActivityPage(rows, children, nextCursor, total);
    }

    private static List<ActivityRow> ReadPage(
        SqliteConnection connection, ActivityFilters filters, ActivityCursor? before, int pageSize)
    {
        var parameters = new SqlParameters();
        using var command = connection.CreateCommand();
        command.CommandText = BuildPageStatement(parameters, filters, before, pageSize);
        parameters.ApplyTo(command);

        var rows = new List<ActivityRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    /// <summary>
    /// Task 7's rail badge count (PAR-017, spec 12.6): top-level events strictly newer than
    /// <paramref name="since"/>, ignoring every filter, the search included, because the badge is
    /// not a filtered view. Null means "never opened": every top-level event counts.
    /// </summary>
    public int CountUnseen(DateTime? since)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        var parameters = new SqlParameters();
        using var command = connection.CreateCommand();
        var clause = since is { } cutoff
            ? $"parent_id IS NULL AND timestamp > {parameters.Add(cutoff)}"
            : "parent_id IS NULL";
        command.CommandText = $"SELECT COUNT(*) FROM activity_events WHERE {clause};";
        parameters.ApplyTo(command);

        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static int ReadTotal(SqliteConnection connection, ActivityFilters filters)
    {
        var parameters = new SqlParameters();
        using var command = connection.CreateCommand();
        command.CommandText = BuildTotalStatement(parameters, filters);
        parameters.ApplyTo(command);

        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static Dictionary<int, IReadOnlyList<ActivityRow>> ReadChildren(
        SqliteConnection connection, List<ActivityRow> rows)
    {
        var children = new Dictionary<int, IReadOnlyList<ActivityRow>>();

        // An empty page runs no child statement at all: an `IN ()` list is not valid SQLite and a
        // read that can return nothing is a round trip to skip, not to guard against.
        if (rows.Count == 0)
        {
            return children;
        }

        var parameters = new SqlParameters();
        using var command = connection.CreateCommand();
        command.CommandText = BuildChildStatement(parameters, [.. rows.Select(row => row.Id)]);
        parameters.ApplyTo(command);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var child = ReadRow(reader);
            if (child.ParentId is not { } parentId)
            {
                continue;
            }

            if (children.TryGetValue(parentId, out var existing))
            {
                ((List<ActivityRow>)existing).Add(child);
            }
            else
            {
                children[parentId] = new List<ActivityRow> { child };
            }
        }

        return children;
    }

    // Internal rather than private so ActivityQueryTests can assert the statement's shape
    // directly: that every operand is a bound @p name and no filter value reaches the text.
    internal static string BuildPageStatement(
        SqlParameters parameters, ActivityFilters filters, ActivityCursor? before, int pageSize)
    {
        var where = BuildFilterClause(parameters, filters);

        if (before is { } cursor)
        {
            // The two-part keyset comparison, the web's
            // `(timestamp < cursor_ts) | ((timestamp == cursor_ts) & (id < cursor_id))`. The
            // timestamp is bound twice rather than reused, because SqlParameters names
            // positionally and a shared name would be a second contract to remember.
            where +=
                $" AND (timestamp < {parameters.Add(cursor.Timestamp)}"
                + $" OR (timestamp = {parameters.Add(cursor.Timestamp)}"
                + $" AND id < {parameters.Add(cursor.Id)}))";
        }

        return $"""
            SELECT {Columns}
            FROM activity_events
            WHERE {where}
            ORDER BY timestamp DESC, id DESC
            LIMIT {parameters.Add(pageSize)};
            """;
    }

    // The same predicates as the page WITHOUT the cursor clause, which is the web's rule: `total`
    // is computed before the cursor predicate is applied, so it is the full filtered count and not
    // the count of what is left to page through.
    internal static string BuildTotalStatement(SqlParameters parameters, ActivityFilters filters)
        => $"""
            SELECT COUNT(*)
            FROM activity_events
            WHERE {BuildFilterClause(parameters, filters)};
            """;

    // ONE statement for the whole page's children, with every id bound as its own parameter. Never
    // one query per parent: a page of 50 scan events would otherwise be 51 round trips.
    //
    // The children are deliberately NOT filtered by severity, category or search, and are not
    // paginated, matching the web: a filter on the feed selects which scans are listed, and an
    // expanded scan then shows what it actually did. Filtering the children too would show a scan
    // whose expansion is empty.
    internal static string BuildChildStatement(SqlParameters parameters, IReadOnlyList<int> parentIds)
    {
        var names = string.Join(", ", parentIds.Select(id => parameters.Add(id)));
        return $"""
            SELECT {Columns}
            FROM activity_events
            WHERE parent_id IN ({names})
            ORDER BY timestamp ASC, id ASC;
            """;
    }

    // parent_id IS NULL is always present: spec 12.6's list is top-level events only, and the
    // children arrive through expansion. Everything else is optional and bound.
    private static string BuildFilterClause(SqlParameters parameters, ActivityFilters filters)
    {
        var clauses = new List<string> { "parent_id IS NULL" };

        if (Canonical(filters.Severity) is { } severity && SeveritySet.Contains(severity))
        {
            clauses.Add($"severity = {parameters.Add(severity)}");
        }

        if (Canonical(filters.Category) is { } category && CategorySet.Contains(category))
        {
            clauses.Add($"category = {parameters.Add(category)}");
        }

        if (Trimmed(filters.Search) is { } search)
        {
            // The term is bound and the wildcards are concatenated in SQL, so nothing the user
            // types reaches the statement text. SQLite's LIKE is case-insensitive for ASCII by
            // default, which is close enough to the web's casefolded containment for a log search
            // box; it is NOT case-insensitive for non-ASCII letters, which is a known and accepted
            // limit here. A term containing % or _ matches as a wildcard rather than as a literal,
            // which is also what the web's ilike does, and is acceptable for a free-text box over a
            // log: escaping it would surprise a user who typed % on purpose.
            clauses.Add($"message LIKE '%' || {parameters.Add(search)} || '%'");
        }

        return string.Join(" AND ", clauses);
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // The stored vocabulary is lowercase, so a filter arriving in another case is folded rather
    // than dropped. An unrecognized value still falls out at the set check above.
    private static string? Canonical(string? value)
        => Trimmed(value)?.ToLowerInvariant();

    private static ActivityRow ReadRow(SqliteDataReader reader)
        => new(
            SqlReaders.ReadInt(reader, 0),
            DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            SqlReaders.ReadText(reader, 6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7),
            SqlReaders.ReadNullableInt(reader, 8),
            SqlReaders.ReadNullableInt(reader, 9));
}
