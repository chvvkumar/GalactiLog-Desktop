using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GalactiLog.Data.Maintenance;

/// <summary>
/// Spec 12.7's "reset database with a typed confirmation". Deletes every catalogued row and
/// re-applies the migrations, leaving an empty schema at the current migration version. The
/// user's settings survive; the shipped catalogues survive.
/// </summary>
/// <remarks>
/// <para>
/// <b>It deletes rows. It never deletes the database file.</b> Spec 2.1 does permit deleting
/// inside the app data directory, but the database file is open with WAL and a live connection
/// pool: deleting it would need a restart, would re-run the migrations on the next start, and
/// could leave a stale <c>-wal</c> and <c>-shm</c> beside a missing <c>.db</c>. Deleting rows
/// through one transaction is the version that cannot leave a half-deleted file behind
/// (<c>questions.md</c> Q28). Nothing in this file opens, creates, moves or removes a path.
/// </para>
/// <para>
/// <b>One transaction.</b> The tables below carry foreign keys to one another and
/// <c>PRAGMA foreign_keys</c> is ON (see <c>PragmaConnectionInterceptor</c>), so the order is a
/// dependency order and a failure part way through must leave the database exactly as it was
/// rather than half emptied. The transaction is what makes that true; the order is what keeps the
/// statements from failing in the first place.
/// </para>
/// <para>
/// <b>What survives, and why</b> (<c>questions.md</c> Q29, ruled as recommended):
/// <c>user_settings</c> is kept, because a reset is about catalogued data and not about the user's
/// configuration: clearing it would take the scan roots, the observer location and the thumbnail
/// cache path with it and reopen the setup wizard on the next start. <c>openngc_catalog</c> and
/// <c>static_catalog_entries</c> are kept, because they are shipped content rather than user data
/// and because <c>CatalogSeeder.LoadIfNeeded</c> is guarded by a per-table check plus
/// <c>general.catalogs_loaded_version</c>: clearing them without also clearing that flag would
/// leave the resolver with no catalogue at all.
/// </para>
/// <para>
/// <c>catalog_cache</c> <b>is</b> cleared, positive rows included. Spec 9.6 says positive rows are
/// "Cleared only by the reset-database action", which names this as the one thing that clears
/// them.
/// </para>
/// <para>
/// This type deletes every row in the database, which is a category of its own: it is not a fourth
/// App-layer writer of <c>targets</c> rows (<c>TRACKING.md</c> section 6 item 14), and the
/// <c>merge_manifests</c> rule that item 15 states for the rebuild does not apply here, because a
/// reset clears the manifests deliberately and says so in the confirmation the user typed.
/// </para>
/// <para>
/// It runs under the resolution lease and is refused while a scan is running. A reset that deleted
/// the rows a scan writer is mid-transaction on is the one failure mode worth designing out.
/// </para>
/// </remarks>
/// <param name="connectionString">The DI <c>DatabaseConnectionString.Value</c>. This service opens
/// its own short-lived contexts, like <c>ActivityRepository</c>.</param>
/// <param name="tryBeginResolution">Takes the resolution lease, or returns null when a scan is
/// running or another pass holds it. Required, not optional, for the reason
/// <c>UnresolvedRetry</c>'s is: the gate lives at the choke point so no caller can forget it
/// (design-lessons rule 2).</param>
/// <param name="logger">Optional. A refusal is logged; nothing here is caught and swallowed.</param>
public sealed class DatabaseReset(
    string connectionString,
    Func<IDisposable?> tryBeginResolution,
    ILogger? logger = null)
{
    /// <summary>
    /// Dependency order, children first. <c>activity_events</c> is self-referencing through
    /// <c>parent_id</c>, so it goes first as a whole table rather than relying on the cascade to
    /// order it. <c>merge_manifests</c> and <c>merge_candidates</c> reference <c>targets</c>;
    /// <c>session_notes</c> and <c>target_catalog_memberships</c> reference <c>targets</c>;
    /// <c>images</c> references <c>targets</c>; <c>targets</c>, <c>scan_runs</c> and
    /// <c>catalog_cache</c> reference nothing.
    /// <para>
    /// The list is the contract: it is what the confirmation dialog itemizes and what
    /// <c>DatabaseResetTests</c> asserts is empty afterwards. Adding a table to the schema without
    /// adding it here leaves rows behind after a reset, which is why it is public.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> ClearedTables =
    [
        "activity_events",
        // The four guide-log tables of spec 5.15 to 5.18, children first. They are scan output in
        // the same sense `images` is, so a reset that left them would leave a guiding history for
        // frames that no longer exist. `phd2_calibrations` and `phd2_sessions` reference
        // `phd2_logs`; `phd2_frames` references `phd2_sessions`; none references anything else.
        "phd2_frames",
        "phd2_sessions",
        "phd2_calibrations",
        "phd2_logs",
        // The two custom-column tables of spec 5.19 and 5.20, children first.
        // `custom_column_values` references `custom_columns` and `targets`; `custom_columns`
        // references nothing. The values are catalogue annotations keyed to targets and nights, so
        // a reset that left them would leave notes on frames that no longer exist, and the
        // definitions go with them: a column whose every value is gone is not a setting.
        "custom_column_values",
        "custom_columns",
        // The four mosaic tables of spec 5.22 to 5.25 (Phase 18), children first: nights reference
        // panels and targets, panels reference mosaics, and the custom values that reference
        // mosaics are already gone. Mosaics are built from catalogued nights, so a reset that left
        // them would keep panels naming targets that no longer exist.
        "mosaic_panel_sessions",
        "mosaic_panels",
        "mosaics",
        "mosaic_suggestions",
        "merge_manifests",
        "merge_candidates",
        "session_notes",
        "target_catalog_memberships",
        "images",
        // Spec 5.21: the calibration frames a scan skipped. Scan output like `images`, so a reset
        // that left them would keep skipped rows beside an empty catalogue.
        "skipped_files",
        "targets",
        "scan_runs",
        "catalog_cache",
    ];

    /// <summary>The tables a reset deliberately leaves alone. Stated as data rather than as prose
    /// so the confirmation text and the test read from the same list.</summary>
    public static readonly IReadOnlyList<string> KeptTables =
    [
        "user_settings",
        "openngc_catalog",
        "static_catalog_entries",
    ];

    /// <summary>Why a run ended. <see cref="ResetStatus.ScanInProgress"/> means nothing was
    /// deleted at all.</summary>
    public enum ResetStatus { Completed, ScanInProgress }

    /// <summary>What one run did.</summary>
    /// <param name="TablesCleared">How many of <see cref="ClearedTables"/> the run emptied.</param>
    /// <param name="RowsDeleted">Rows removed across all of them, for the summary line and the
    /// activity event's <c>affected</c> key.</param>
    /// <param name="SchemaRecreated">True once <c>Database.Migrate()</c> has run cleanly over the
    /// emptied database, which is what "recreates an empty schema" means here.</param>
    /// <param name="Status">Why the run ended.</param>
    public sealed record ResetOutcome(
        int TablesCleared,
        int RowsDeleted,
        bool SchemaRecreated,
        ResetStatus Status = ResetStatus.Completed);

    /// <param name="ct">Checked before the transaction opens. Once the delete has started it runs
    /// to completion: a partly reset database is not a state this action may leave behind.</param>
    public ResetOutcome Run(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var lease = tryBeginResolution();
        if (lease is null)
        {
            logger?.LogInformation("Reset database refused: a scan is already running");
            return new ResetOutcome(0, 0, false, ResetStatus.ScanInProgress);
        }

        var rowsDeleted = 0;
        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true)))
        {
            context.Database.OpenConnection();
            var connection = context.Database.GetDbConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var table in ClearedTables)
            {
                if (table == TargetsTable)
                {
                    // Review finding I1. targets carries a self-referencing foreign key,
                    // FK_targets_targets_merged_into_id, declared ON DELETE RESTRICT, and
                    // PragmaConnectionInterceptor turns foreign_keys ON. SQLite enforces RESTRICT
                    // immediately and per row rather than at statement or transaction end, and the
                    // truncate optimization does not apply to a table that is the parent of an
                    // enabled foreign key, so the delete walks the rows one at a time. Any library
                    // holding an un-undone merge has the loser's merged_into_id pointing at the
                    // winner, and reaching the winner first raises SQLITE_CONSTRAINT and rolls the
                    // whole reset back.
                    //
                    // Breaking the self-reference first is the fix, inside the same transaction so
                    // a failure anywhere after it still leaves the database exactly as it was. It
                    // deletes nothing and is not counted: every one of these rows is deleted by the
                    // very next statement.
                    Execute(connection, transaction, "UPDATE targets SET merged_into_id = NULL;");
                }

                // The table names are this file's own constants, never caller input, so there is
                // no operand to bind: SQLite cannot parameterize an identifier in any case. Raw
                // commands rather than ExecuteSqlRaw, so the statements share one explicit
                // transaction and so EF's raw-SQL analyzer has no composed string to complain
                // about.
                rowsDeleted += Execute(connection, transaction, "DELETE FROM " + table + ";");
            }

            transaction.Commit();
        }

        // A no-op on an already-migrated database, and the point of running it: it is what turns
        // "the rows are gone" into "the schema is intact and at the current version", which is the
        // roadmap's "recreates an empty schema" rather than a hope. A fresh context, because the
        // one above has just committed and Migrate opens its own connection anyway.
        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true)))
        {
            context.Database.Migrate();
        }

        // Emitted AFTER the delete, on a fresh context, and this is not a bug: a rebuild_started
        // written before the transaction is one of the rows the transaction deletes, so the pair
        // would arrive in an empty log with only its second half. Written here, both halves are
        // the first two rows of the new log and they say what emptied it. Spec 10.9's last
        // paragraph: an event raised outside a scan is written by its own caller on a short-lived
        // context, which is exactly what ActivityRepository.EmitStandalone is.
        //
        // The Maintenance tab therefore emits nothing of its own for this action; every other
        // action's pair is written by the tab.
        var activity = new ActivityRepository(connectionString);
        activity.EmitStandalone(
            category: "rebuild", severity: "info", eventType: "rebuild_started",
            message: "Reset database: every catalogued row was deleted",
            details: new { action = ResetActionToken, affected = rowsDeleted });
        activity.EmitStandalone(
            category: "rebuild", severity: "info", eventType: "rebuild_complete",
            message: $"Reset database: {rowsDeleted} row{(rowsDeleted == 1 ? "" : "s")} deleted "
                + $"from {ClearedTables.Count} tables; settings and catalogues kept",
            details: new { action = ResetActionToken, affected = rowsDeleted });

        return new ResetOutcome(ClearedTables.Count, rowsDeleted, SchemaRecreated: true);
    }

    /// <summary>Spec 10.9's <c>action</c> detail key for this maintenance action. Held here rather
    /// than only on the tab, because this type writes its own events.</summary>
    public const string ResetActionToken = "reset_database";

    /// <summary>The one table in <see cref="ClearedTables"/> that is the parent of an enabled
    /// foreign key pointing at itself. Named so the guard above cannot drift from the list.</summary>
    private const string TargetsTable = "targets";

    private static int Execute(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }
}
