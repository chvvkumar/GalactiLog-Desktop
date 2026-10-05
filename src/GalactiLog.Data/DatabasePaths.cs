using Microsoft.Data.Sqlite;

namespace GalactiLog.Data;

public static class DatabasePaths
{
    public const string DatabaseFileName = "galactilog.db";

    public static string BuildConnectionString(string databaseFilePath, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
        => new SqliteConnectionStringBuilder
        {
            DataSource = databaseFilePath,
            Mode = mode,
        }.ToString();

    // A read-only view of an existing connection string: same file, Mode=ReadOnly, so a read
    // path cannot create, migrate or write anything. Built here like every other connection
    // string in the solution (spec 2.1.2); FileSafetyTest allowlists SqliteConnectionStringBuilder
    // for this file alone.
    //
    // Pooling is off, and that is not a performance choice: A READ-ONLY HANDLE MUST NEVER BE THE
    // LAST ONE OPEN ON THE FILE. SQLite checkpoints the write-ahead log and removes it when the
    // last connection to a database closes, and a read-only connection cannot perform that closing
    // checkpoint. Microsoft.Data.Sqlite pools by connection string and keeps a pooled handle open
    // after the caller has closed it, so a pooled read-only handle outlives the last writer and
    // strands the log: the Phase 10 verification pass found galactilog.db at 4 KB beside a 3.3 MB
    // galactilog.db-wal after a clean shutdown of both the CLI and the GUI. With pooling off the
    // handle is released at the end of each read, and the last connection on the file is always a
    // read-write one that can checkpoint.
    //
    // The cost is one file open per read, which these paths can afford: they are the CLI's
    // single-row --json payload, the scan-run history reads, and one diagnostics refresh.
    public static string AsReadOnly(string connectionString)
        => new SqliteConnectionStringBuilder(connectionString)
        {
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

    /// <summary>
    /// The database file path a connection string points at. Spec 12.8's Database group reports
    /// it, and spec 17.2's Paths group repeats it. Here rather than at the call site because
    /// <c>SqliteConnectionStringBuilder</c> is allowlisted for this file alone (FileSafetyTest's
    /// connection-string pattern group).
    /// </summary>
    public static string DataSourceOf(string connectionString)
        => new SqliteConnectionStringBuilder(connectionString).DataSource;

    /// <summary>
    /// Task 7 (PAR-012, questions.md Q7, coordinator ruling on the migration-order escalation).
    /// A best-effort, pre-migration read of <c>user_settings.general</c>, for the one caller that
    /// has to read a stored value before the database is known to exist or to match the current
    /// schema: <c>AppHost</c> needs <c>general.app_log_retention_days</c> to build the Serilog
    /// sink, and the sink has to exist before <c>Database.Migrate()</c> runs so a migration
    /// failure is still logged (HEAD's ordering, restored).
    /// </summary>
    /// <remarks>
    /// No migration and no EF model build happen here; this opens a bare, pooling-off, read-only
    /// connection directly, exactly like <see cref="AsReadOnly"/>'s callers, and is allowlisted to
    /// this file by the same <c>FileSafetyTest</c> group <see cref="SqliteConnectionStringBuilder"/>
    /// already is. A missing file, a missing table, a missing row, a locked file or a malformed
    /// database all return null rather than throwing: on a fresh profile the database does not
    /// exist yet, and this read must never be the reason startup logging goes dark.
    /// </remarks>
    public static string? TryReadStoredGeneralJson(string databaseFilePath)
    {
        if (!File.Exists(databaseFilePath))
        {
            return null;
        }

        try
        {
            using var connection = new SqliteConnection(AsReadOnly(BuildConnectionString(databaseFilePath)));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT general FROM user_settings LIMIT 1;";
            return command.ExecuteScalar() as string;
        }
        catch (Exception)
        {
            // Deliberately broad: a hand-edited or mid-migration database can fail in ways this
            // best-effort peek has no business enumerating, and the caller's documented fallback
            // covers all of them the same way.
            return null;
        }
    }
}

/// <summary>
/// The application database's connection string as a DI-resolvable value (FIXER LIST 5).
/// AppHost builds it once from the authorized app data root and registers it, so no other
/// component has to re-derive the same expression from an AppWriter.
/// </summary>
public sealed record DatabaseConnectionString(string Value);
