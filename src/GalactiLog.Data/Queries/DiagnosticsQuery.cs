using System.Globalization;
using GalactiLog.Core.Io;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>Spec 12.8's Database group.</summary>
/// <param name="FilePath">The database file, from the connection string's data source.</param>
/// <param name="FileBytes">The size of that file, or 0 when it does not exist.</param>
/// <param name="WalBytes">The size of the write-ahead log beside it. <b>0 is the ordinary
/// reading, not an error and not "unavailable"</b> (coordinator ruling Q9): SQLite deletes the
/// WAL on a clean close, so a fresh process routinely has none.</param>
/// <param name="PageCount"><c>PRAGMA page_count</c>.</param>
/// <param name="RowCounts">One entry per table, keyed by the snake_case table name, which is also
/// spec 16.3's <c>row_counts</c> key. A dictionary is a lookup and carries no order of its own:
/// the spec 12.8 order is <see cref="DiagnosticsQuery.RowCountTables"/>, and every renderer and
/// serializer of these counts iterates that list and indexes in here (review finding M1).</param>
/// <param name="Phd2Bytes">Spec 12.8's "PHD2 data size" (ruling F2): the total bytes the four
/// guide-log tables and their indexes occupy. <c>phd2_frames</c> is the volume table (spec 5.17)
/// and this is the field a reader checks before asking why the database grew.</param>
/// <param name="Phd2BytesMethod">How <paramref name="Phd2Bytes"/> was arrived at:
/// <see cref="DiagnosticsQuery.MeasuredMethod"/> when SQLite's <c>dbstat</c> answered, or
/// <see cref="DiagnosticsQuery.EstimatedMethod"/> when the shipped library does not expose it and
/// the figure came from the page count instead. A reader who cannot tell a measured figure from an
/// estimate has a figure they cannot use, which is why the method travels with the number rather
/// than being inferred from it.</param>
public sealed record DatabaseDiagnostics(
    string FilePath, long FileBytes, long WalBytes, long PageCount,
    IReadOnlyDictionary<string, long> RowCounts,
    long Phd2Bytes, string Phd2BytesMethod);

/// <summary>Spec 12.8's Resolver group's three database figures.</summary>
/// <param name="CachePositive"><c>catalog_cache</c> rows with <c>negative = 0</c>.</param>
/// <param name="CacheNegative"><c>catalog_cache</c> rows with <c>negative = 1</c>.</param>
/// <param name="CacheExpired">Negative rows older than
/// <see cref="CatalogCacheRepository.NegativeTtl"/>. <b>A subset of
/// <paramref name="CacheNegative"/>, not a fourth disjoint bucket</b>: positive plus negative is
/// the row count, and adding this to them double counts.</param>
public sealed record ResolverCacheDiagnostics(
    long CachePositive, long CacheNegative, long CacheExpired);

/// <summary>
/// Spec 12.8's Database, Resolver and Unresolved groups: everything on that page that SQLite
/// answers. Read-only. The App-layer fields (versions, paths, watcher state, the progress
/// envelope, the log ring) are composed by <c>DiagnosticsService</c>, which is the one composer.
/// </summary>
/// <remarks>
/// House rules (spec 4.3): it lives in <c>GalactiLog.Data.Queries</c>, takes the DI
/// <see cref="DatabaseConnectionString"/>, writes nothing and binds every operand through
/// <see cref="SqlParameters"/>. The Unresolved group reuses <see cref="UnresolvedNamesQuery"/>
/// rather than adding a second grouping here.
/// </remarks>
public sealed class DiagnosticsQuery(DatabaseConnectionString connectionString)
{
    /// <summary>The prefix every guide-log table of spec 5.15 to 5.18 carries. The one place the
    /// four tables are recognised as a group: <see cref="RowCountStatements"/> is the only list of
    /// their names, and the estimate's row share reads them through this.</summary>
    private const string Phd2Prefix = "phd2";

    /// <summary><see cref="DatabaseDiagnostics.Phd2BytesMethod"/> when <c>dbstat</c> answered.
    /// </summary>
    public const string MeasuredMethod = "measured";

    /// <summary><see cref="DatabaseDiagnostics.Phd2BytesMethod"/> when it did not, and the figure
    /// is the page-count estimate below.</summary>
    public const string EstimatedMethod = "estimated";

    /// <summary>
    /// What identifies a guide-log table or index in <c>dbstat</c>, and the one place that
    /// membership is expressed as SQL.
    /// </summary>
    /// <remarks>
    /// <b>Two patterns, not one.</b> Spec 12.8's figure is the bytes the four tables
    /// <b>and their indexes</b> occupy, <c>dbstat</c> reports a row per page with <c>name</c>
    /// holding the table or index that page belongs to, and every one of the ten guide-log indexes
    /// is named <c>ix_phd2_...</c>, which does not begin with <c>phd2</c>. A single
    /// <c>LIKE 'phd2%'</c> therefore reports the four tables' data pages alone and silently drops
    /// every index, the two over the volume table included (review P2-1). The earlier claim that
    /// one prefix covered both is recorded in the brief and in questions-b Q1 and is false in both;
    /// those sentences are Task 8's to correct.
    /// <para>
    /// Exposed so a case can apply it to <c>sqlite_master</c>, whose <c>name</c> column holds the
    /// same names. That is the only way this predicate is checkable at all on a build with no
    /// <c>dbstat</c>, which is every build this project currently ships.
    /// </para>
    /// </remarks>
    public const string Phd2NameFilter = "name LIKE 'phd2%' OR name LIKE 'ix_phd2%'";

    // One statement rather than one per table. Probed by try and catch rather than through
    // pragma_module_list, which is itself behind a compile option and would need a fallback of its
    // own (coordinator ruling, questions-b Q1).

    private const string Phd2SizeSql =
        "SELECT COALESCE(sum(pgsize), 0) FROM dbstat WHERE " + Phd2NameFilter + ";";

    /// <summary>
    /// Tables whose pages are in the estimate's numerator but whose rows spec 12.8 does not report,
    /// so they are counted in its denominator and nowhere else.
    /// </summary>
    /// <remarks>
    /// The numerator is the whole file's page count, which includes the shipped catalogues. With
    /// them out of the denominator, a library with guide logs and few images drives the ratio
    /// toward 1 and the field reports very nearly the whole database, the shipped catalogues
    /// included, as PHD2 data (review P2-3). These names deliberately do <b>not</b> join
    /// <see cref="RowCountStatements"/>: that list is spec 12.8's reported set and its order, and
    /// moving it would move every consumer's expected list.
    /// </remarks>
    private static readonly string[] UnreportedRowCountStatements =
    [
        "SELECT COUNT(*) FROM openngc_catalog;",
        "SELECT COUNT(*) FROM static_catalog_entries;",
    ];

    // One statement per table rather than one UNION ALL: the count of a table is a scan either
    // way, and readable statements are what a reviewer checks against spec 12.8's list. Written
    // out rather than composed from a table name, so no identifier is interpolated into SQL text.
    // The four guide-log tables are APPENDED in spec 12.8's own order (ruling F2); the existing
    // seven do not move, or every consumer's expected list moves with them.
    private static readonly (string Table, string Sql)[] RowCountStatements =
    [
        ("images", "SELECT COUNT(*) FROM images;"),
        ("targets", "SELECT COUNT(*) FROM targets;"),
        ("session_notes", "SELECT COUNT(*) FROM session_notes;"),
        ("activity_events", "SELECT COUNT(*) FROM activity_events;"),
        ("catalog_cache", "SELECT COUNT(*) FROM catalog_cache;"),
        ("merge_candidates", "SELECT COUNT(*) FROM merge_candidates;"),
        ("scan_runs", "SELECT COUNT(*) FROM scan_runs;"),
        ("phd2_logs", "SELECT COUNT(*) FROM phd2_logs;"),
        ("phd2_sessions", "SELECT COUNT(*) FROM phd2_sessions;"),
        ("phd2_frames", "SELECT COUNT(*) FROM phd2_frames;"),
        ("phd2_calibrations", "SELECT COUNT(*) FROM phd2_calibrations;"),
    ];

    /// <summary>The tables spec 12.8's Database group reports, in its table order. Snake_case,
    /// which is also spec 16.3's <c>row_counts</c> key set.</summary>
    public static IReadOnlyList<string> RowCountTables { get; } =
        [.. RowCountStatements.Select(statement => statement.Table)];

    /// <summary>Spec 12.8's Database group. rowCounts keys are the table names, in the spec's
    /// order.</summary>
    public DatabaseDiagnostics Database()
    {
        var path = DatabasePaths.DataSourceOf(connectionString.Value);

        using var context = OpenReadOnly();
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        // One deferred read transaction across every statement, the shape ActivityQuery.Page and
        // StatsQuery established. Without it the seven counts are from seven different instants
        // and a reader blames the disagreement on the arithmetic. Deferred, so this takes a read
        // lock at the first statement and never a write lock.
        using var snapshot = connection.BeginTransaction(deferred: true);

        var pageCount = ScalarLong(connection, "PRAGMA page_count;");

        var rowCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (table, sql) in RowCountStatements)
        {
            rowCounts[table] = ScalarLong(connection, sql);
        }

        var (phd2Bytes, phd2Method) = Phd2Size(connection, pageCount, rowCounts);

        snapshot.Commit();

        return new DatabaseDiagnostics(
            path, SizeOf(path), SizeOf(path + "-wal"), pageCount, rowCounts, phd2Bytes, phd2Method);
    }

    /// <summary>
    /// Spec 12.8's "PHD2 data size" (ruling F2), inside the same deferred read transaction the
    /// counts use so the two describe one instant.
    /// </summary>
    /// <remarks>
    /// <c>dbstat</c> needs <c>SQLITE_ENABLE_DBSTAT_VTAB</c> at compile time and the shipped
    /// <c>e_sqlite3</c> build may or may not carry it, which cannot be settled by reading. So it is
    /// asked, and a <see cref="SqliteException"/> alone is caught: a missing virtual table is
    /// "no such table: dbstat" and nothing else here is expected to throw. The fallback is the page
    /// count weighted by the guide-log share of the counted rows, and the returned method says
    /// which of the two a reader is looking at. It is a rough estimate, deliberately so: a
    /// <c>phd2_frames</c> row is far narrower than an <c>images</c> row, and the word "estimated"
    /// is what keeps the number from being read as a measurement.
    /// </remarks>
    private static (long Bytes, string Method) Phd2Size(
        SqliteConnection connection, long pageCount, IReadOnlyDictionary<string, long> rowCounts)
    {
        try
        {
            return (ScalarLong(connection, Phd2SizeSql), MeasuredMethod);
        }
        catch (SqliteException)
        {
            // Every table whose pages the numerator counts, not only the eleven spec 12.8 reports,
            // or the shipped catalogue inflates every estimate (review P2-3).
            var total = rowCounts.Values.Sum()
                + UnreportedRowCountStatements.Sum(sql => ScalarLong(connection, sql));
            if (total <= 0)
            {
                return (0, EstimatedMethod);
            }

            var phd2Rows = rowCounts
                .Where(entry => entry.Key.StartsWith(Phd2Prefix, StringComparison.Ordinal))
                .Sum(entry => entry.Value);
            var pageSize = ScalarLong(connection, "PRAGMA page_size;");
            return ((long)(pageSize * pageCount * ((double)phd2Rows / total)), EstimatedMethod);
        }
    }

    /// <summary>Spec 12.8's Resolver group's three database figures.</summary>
    public ResolverCacheDiagnostics ResolverCache()
    {
        using var context = OpenReadOnly();
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        using var snapshot = connection.BeginTransaction(deferred: true);

        var positive = ScalarLong(connection, "SELECT COUNT(*) FROM catalog_cache WHERE negative = 0;");
        var negative = ScalarLong(connection, "SELECT COUNT(*) FROM catalog_cache WHERE negative = 1;");

        // The TTL is read from the repository that owns it (ruling Q8), so the constant 7 lives
        // in exactly one file.
        var parameters = new SqlParameters();
        var cutoff = parameters.Add(DateTime.UtcNow - CatalogCacheRepository.NegativeTtl);
        var expired = ScalarLong(
            connection,
            $"SELECT COUNT(*) FROM catalog_cache WHERE negative = 1 AND fetched_at < {cutoff};",
            parameters);

        snapshot.Commit();

        return new ResolverCacheDiagnostics(positive, negative, expired);
    }

    /// <summary>The SQLite library version behind this process, for spec 12.8's Versions group.
    /// Its own member rather than a field on <see cref="DatabaseDiagnostics"/>, which stays a
    /// record of exactly the Database group's fields.</summary>
    /// <returns>The version string, or null when SQLite answered with nothing. The caller applies
    /// its own fallback text: <c>DiagnosticsService.Unknown</c> is an App-layer constant and this
    /// layer does not spell the word a second time (review finding M5).</returns>
    public string? SqliteVersion()
    {
        using var context = OpenReadOnly();
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return command.ExecuteScalar() as string;
    }

    // Read-only, like ScanRunRepository's read members: a diagnostics read is the read a support
    // bundle is built from, and it must not be able to create, migrate or write anything
    // (spec 2.1.2, review finding M4). DatabasePaths.AsReadOnly is the one place the mode is
    // applied, and it also turns pooling off, because a read-only handle left open in the pool
    // outlives the last writer and strands the write-ahead log; read that member's comment before
    // changing anything here.
    private GalactiLogContext OpenReadOnly()
        => new(GalactiLogContextOptions.Create(DatabasePaths.AsReadOnly(connectionString.Value)));

    // Through UserFiles, never `new FileInfo`: the database file is under the app data root and
    // UserFiles is the read choke point (spec 2.1.1). A missing file is 0 bytes, which for the
    // WAL is the ordinary reading (ruling Q9).
    private static long SizeOf(string path)
        => UserFiles.Exists(path) ? UserFiles.GetFileInfo(path).Length : 0;

    private static long ScalarLong(SqliteConnection connection, string sql, SqlParameters? parameters = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        parameters?.ApplyTo(command);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
