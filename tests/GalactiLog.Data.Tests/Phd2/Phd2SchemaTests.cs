using System;
using System.Collections.Generic;
using System.Linq;
using GalactiLog.Data;
using GalactiLog.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace GalactiLog.Data.Tests.Phd2;

/// <summary>
/// The fourth migration: the four guide-log tables of spec 5.15 to 5.18 and the three
/// <c>scan_runs</c> counters of spec 5.13, which arrive together in one migration.
/// </summary>
/// <remarks>
/// Every case here states what a failure of it means for a user, not only what it asserts. The
/// column lists below are the spec's tables transcribed once; a renamed property that silently
/// renames a column fails here rather than in an ingest nobody is watching.
/// </remarks>
public class Phd2SchemaTests
{
    /// <summary>The migration immediately before this one. Rolling back to it is what a user who
    /// downgrades does.</summary>
    private const string PreviousMigration = "20260910010633_ImagesFilePathNoCase";

    private static readonly string[] Phd2Tables =
        ["phd2_logs", "phd2_sessions", "phd2_frames", "phd2_calibrations"];

    private static readonly string[] Phd2Counters = ["phd2_found", "phd2_ingested", "phd2_failed"];

    // "<name> <declared type> <1 when NOT NULL>", in the table's own column order, which is spec
    // 5.15 to 5.18's own order. phd2_sessions.ended_at_local is last in both orders and is meant to
    // be: the fifth migration appends it physically with AddColumn, and spec 5.16 lists it last for
    // exactly that reason, so it is not out of place and must not be moved back up beside
    // ended_at_utc.
    private static readonly Dictionary<string, string[]> ExpectedColumns = new(StringComparer.Ordinal)
    {
        ["phd2_logs"] =
        [
            "id TEXT 1", "file_path TEXT 1", "file_size INTEGER 0", "file_mtime REAL 0",
            "parse_status TEXT 1", "parse_error TEXT 0", "phd2_version TEXT 0",
            "log_version TEXT 0", "run_count INTEGER 1", "session_count INTEGER 1",
            "calibration_count INTEGER 1", "parsed_at TEXT 1",
        ],
        ["phd2_sessions"] =
        [
            "id TEXT 1", "log_id TEXT 1", "run_index INTEGER 1", "section_index INTEGER 1",
            "started_at_local TEXT 1", "started_at_utc TEXT 0", "ended_at_utc TEXT 0",
            "duration_s REAL 1", "session_date TEXT 0", "equipment_profile TEXT 0",
            "telescope TEXT 0", "pixel_scale_arcsec REAL 0", "focal_length_mm REAL 0",
            "guide_camera TEXT 0", "exposure_ms REAL 0", "mount_name TEXT 0",
            "dec_guide_mode TEXT 0", "algo_ra TEXT 0", "algo_dec TEXT 0", "min_move_ra REAL 0",
            "min_move_dec REAL 0", "aggression_ra REAL 0", "ortho_error_deg REAL 0",
            "last_cal_issue TEXT 0", "pier_side TEXT 0", "alt_deg REAL 0", "az_deg REAL 0",
            "dec_deg REAL 0", "hour_angle_hr REAL 0", "frame_count INTEGER 1",
            "drop_count INTEGER 1", "max_drop_run INTEGER 1", "unguided_seconds REAL 1",
            "rms_ra_arcsec REAL 0", "rms_dec_arcsec REAL 0", "rms_total_arcsec REAL 0",
            "rms_ra_filtered_arcsec REAL 0", "rms_dec_filtered_arcsec REAL 0",
            "rms_total_filtered_arcsec REAL 0", "peak_ra_arcsec REAL 0", "peak_dec_arcsec REAL 0",
            "snr_mean REAL 0", "snr_min REAL 0", "star_mass_mean REAL 0",
            "pulse_count_ra_west INTEGER 1", "pulse_count_ra_east INTEGER 1",
            "pulse_count_dec_north INTEGER 1", "pulse_count_dec_south INTEGER 1",
            "pulse_total_ms_ra INTEGER 1", "pulse_total_ms_dec INTEGER 1",
            "dither_count INTEGER 1", "settle_count INTEGER 1", "settle_failed_count INTEGER 1",
            "settle_median_s REAL 0", "star_lost_reasons TEXT 1", "events TEXT 1",
            "truncated INTEGER 1", "discarded_rows INTEGER 1", "ended_at_local TEXT 0",
        ],
        ["phd2_frames"] =
        [
            "id INTEGER 1", "session_id TEXT 1", "frame_index INTEGER 1", "time_offset REAL 1",
            "dx REAL 0", "dy REAL 0", "ra_raw REAL 0", "dec_raw REAL 0", "ra_guide REAL 0",
            "dec_guide REAL 0", "ra_duration_ms INTEGER 1", "ra_direction TEXT 1",
            "dec_duration_ms INTEGER 1", "dec_direction TEXT 1", "star_mass REAL 0", "snr REAL 0",
            "error_code INTEGER 0", "dropped INTEGER 1",
        ],
        ["phd2_calibrations"] =
        [
            "id TEXT 1", "log_id TEXT 1", "started_at_local TEXT 1", "started_at_utc TEXT 0",
            "session_date TEXT 0", "equipment_profile TEXT 0", "telescope TEXT 0",
            "pixel_scale_arcsec REAL 0", "focal_length_mm REAL 0", "guide_camera TEXT 0",
            "mount_name TEXT 0", "ra_guide_speed REAL 0", "dec_guide_speed REAL 0",
            "dec_deg REAL 0", "hour_angle_hr REAL 0", "pier_side TEXT 0", "alt_deg REAL 0",
            "az_deg REAL 0", "west_angle_deg REAL 0", "west_rate_px_s REAL 0",
            "west_parity TEXT 0", "north_angle_deg REAL 0", "north_rate_px_s REAL 0",
            "north_parity TEXT 0", "completed INTEGER 1", "steps TEXT 1",
        ],
    };

    // Spec 5.16, 5.17 and 5.18 name nine indexes and the port adds one more: the unique index on
    // phd2_logs.file_path, which spec 5.15 requires but does not name, given the ix_ name so the
    // Diagnostics size probe's LIKE 'phd2%' covers it too.
    private static readonly Dictionary<string, string[]> ExpectedIndexes = new(StringComparer.Ordinal)
    {
        ["phd2_logs"] = ["ix_phd2_logs_file_path"],
        ["phd2_sessions"] =
        [
            "ix_phd2_sessions_log_id", "ix_phd2_sessions_session_date",
            "ix_phd2_sessions_started_at_utc", "ix_phd2_sessions_telescope_session_date",
        ],
        ["phd2_frames"] = ["ix_phd2_frames_session_frame", "ix_phd2_frames_session_time"],
        ["phd2_calibrations"] =
        [
            "ix_phd2_calibrations_log_id", "ix_phd2_calibrations_session_date",
            "ix_phd2_calibrations_started_at_utc",
        ],
    };

    /// <summary>
    /// The migration applies and rolls back on a fresh database (spec 5.15, spec 18.1).
    /// </summary>
    /// <remarks>
    /// A failure looks like <c>Down</c> leaving a table or an index behind, so a user who
    /// downgrades and upgrades again hits "table phd2_logs already exists" and cannot start.
    /// </remarks>
    [Fact]
    public void Migration_AppliesAndRollsBack_OnAFreshDatabase()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();

        var applied = TableNames(database.ConnectionString);
        Assert.All(Phd2Tables, table => Assert.Contains(table, applied));
        Assert.All(Phd2Counters, column => Assert.Contains(column, ColumnNames(database.ConnectionString, "scan_runs")));

        using (var context = Open(database))
        {
            context.Database.GetService<IMigrator>().Migrate(PreviousMigration);
        }

        var rolledBack = TableNames(database.ConnectionString);
        Assert.All(Phd2Tables, table => Assert.DoesNotContain(table, rolledBack));

        // Every index goes with its table. An index left behind is what makes the re-upgrade fail.
        var indexes = IndexNames(database.ConnectionString);
        Assert.DoesNotContain(indexes, name => name.StartsWith("ix_phd2", StringComparison.Ordinal));

        var scanRunColumns = ColumnNames(database.ConnectionString, "scan_runs");
        Assert.All(Phd2Counters, column => Assert.DoesNotContain(column, scanRunColumns));

        // And it goes back up cleanly, which is the half a user who downgrades and changes their
        // mind actually exercises.
        using (var context = Open(database))
        {
            context.Database.Migrate();
        }

        Assert.All(Phd2Tables, table => Assert.Contains(table, TableNames(database.ConnectionString)));
    }

    /// <summary>
    /// Every column of every one of the four tables, with the spec's name, type and nullability,
    /// in the spec's order.
    /// </summary>
    /// <remarks>
    /// A failure looks like a column silently renamed by a property rename, so the ingest writes a
    /// value nothing reads and the correlation's <c>started_at_utc</c> range scan finds no session.
    /// </remarks>
    [Theory]
    [InlineData("phd2_logs")]
    [InlineData("phd2_sessions")]
    [InlineData("phd2_frames")]
    [InlineData("phd2_calibrations")]
    public void EveryTable_HasExactlyTheSpecColumns(string table)
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();

        Assert.Equal(ExpectedColumns[table], ColumnShapes(database.ConnectionString, table));
    }

    /// <summary>The column counts spec 5.15 to 5.18 themselves carry, asserted as numbers so a
    /// transcription that dropped a row from the list above is visible as a count.</summary>
    [Fact]
    public void EveryTable_HasTheSpecColumnCount()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();

        Assert.Equal(12, ColumnShapes(database.ConnectionString, "phd2_logs").Count);
        Assert.Equal(59, ColumnShapes(database.ConnectionString, "phd2_sessions").Count);
        Assert.Equal(18, ColumnShapes(database.ConnectionString, "phd2_frames").Count);
        Assert.Equal(26, ColumnShapes(database.ConnectionString, "phd2_calibrations").Count);
    }

    /// <summary>
    /// Every index spec 5.16, 5.17 and 5.18 names exists, by name, and no other one does.
    /// </summary>
    /// <remarks>
    /// A failure looks like <c>ix_phd2_sessions_started_at_utc</c> being absent or auto-named, so
    /// the correlation's 24 hour lookback scans the whole session table on every night of every
    /// pass. Nothing fails; it just gets slower as the corpus grows, which is why the name is
    /// pinned rather than the behaviour.
    /// </remarks>
    [Theory]
    [InlineData("phd2_logs")]
    [InlineData("phd2_sessions")]
    [InlineData("phd2_frames")]
    [InlineData("phd2_calibrations")]
    public void EveryTable_HasExactlyTheSpecIndexes(string table)
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();

        // Ordered so the comparison reads as a set; SQLite lists indexes newest first.
        Assert.Equal(
            ExpectedIndexes[table],
            IndexNamesOf(database.ConnectionString, table).OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// The cascade works in all three directions: log to session, session to frame, log to
    /// calibration.
    /// </summary>
    /// <remarks>
    /// A failure looks like the delete throwing a foreign key violation, or leaving orphan frame
    /// rows that no path ever deletes, so the volume table grows without bound and the orphan drop
    /// of spec 10.3 step 4 frees nothing.
    /// </remarks>
    [Fact]
    public void DeletingALog_CascadesToItsSessionsFramesAndCalibrations()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        var logId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        using (var context = Open(database))
        {
            context.Phd2Logs.Add(Log(logId, @"C:\Astro\guide\PHD2_GuideLog_1.txt"));
            context.Phd2Sessions.Add(Session(sessionId, logId));
            context.Phd2Frames.Add(Frame(sessionId, 1));
            context.Phd2Calibrations.Add(Calibration(logId));
            context.SaveChanges();
        }

        using (var context = Open(database))
        {
            context.Phd2Logs.Where(log => log.Id == logId).ExecuteDelete();
        }

        using (var context = Open(database))
        {
            Assert.Empty(context.Phd2Logs);
            Assert.Empty(context.Phd2Sessions);
            Assert.Empty(context.Phd2Frames);
            Assert.Empty(context.Phd2Calibrations);
        }
    }

    /// <summary>
    /// <c>phd2_logs.file_path</c> is unique, and unique case-insensitively.
    /// </summary>
    /// <remarks>
    /// A failure looks like the same log catalogued twice under two spellings, so a rescan
    /// re-parses it every time and the orphan drop deletes one of the two rows.
    /// </remarks>
    [Fact]
    public void LogFilePath_IsUnique_AndCaseInsensitive()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        const string path = @"C:\Astro\guide\PHD2_GuideLog_2026-01-01.txt";

        using (var context = Open(database))
        {
            context.Phd2Logs.Add(Log(Guid.NewGuid(), path));
            context.SaveChanges();
        }

        Assert.ThrowsAny<DbUpdateException>(() =>
        {
            using var context = Open(database);
            context.Phd2Logs.Add(Log(Guid.NewGuid(), path));
            context.SaveChanges();
        });

        Assert.ThrowsAny<DbUpdateException>(() =>
        {
            using var context = Open(database);
            context.Phd2Logs.Add(Log(Guid.NewGuid(), path.ToUpperInvariant()));
            context.SaveChanges();
        });
    }

    /// <summary>
    /// The three <c>scan_runs</c> counters default to 0 on a row written without them.
    /// </summary>
    /// <remarks>
    /// A failure looks like an INSERT from the existing writer failing on a non-null column with
    /// no default, so every scan in the application stops recording runs. The insert below is
    /// deliberately raw SQL naming only the pre-existing columns: that is the statement an older
    /// writer emits, and an entity insert would hide the defect by sending all three values.
    /// </remarks>
    [Fact]
    public void ScanRunCounters_DefaultToZero_OnARowWrittenWithoutThem()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();

        using (var connection = new SqliteConnection(database.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO scan_runs (started_at, trigger, state, discovered, new_files, " +
                "changed_files, completed, failed, skipped_calibration, removed) " +
                "VALUES ('2026-01-01 00:00:00', 'manual', 'complete', 0, 0, 0, 0, 0, 0, 0);";
            command.ExecuteNonQuery();
        }

        using var context = Open(database);
        var run = Assert.Single(context.ScanRuns);
        Assert.Equal(0, run.Phd2Found);
        Assert.Equal(0, run.Phd2Ingested);
        Assert.Equal(0, run.Phd2Failed);
    }

    /// <summary>
    /// Spec 5.13's closing paragraph: a <c>scan_runs</c> row written by an earlier version reads as
    /// a run that found no guide logs after the upgrade.
    /// </summary>
    /// <remarks>
    /// This is a different mechanism from the column's <c>DEFAULT 0</c> clause, which the case
    /// above exercises: this one is <c>AddColumn</c>'s backfill of rows that already existed when
    /// the column was added, and the row therefore has to be written while the database is at the
    /// third migration. A failure looks like an upgraded library reporting null or garbage counters
    /// for every run it recorded before this version, which is the one claim in this phase that
    /// touches a database a user already has.
    /// </remarks>
    [Fact]
    public void ScanRunCounters_ReadZero_OnARowThatExistedBeforeTheMigration()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();

        using (var context = Open(database))
        {
            context.Database.GetService<IMigrator>().Migrate(PreviousMigration);
        }

        // Written at the third migration, so the three columns do not exist yet.
        var scanRunColumns = ColumnNames(database.ConnectionString, "scan_runs");
        Assert.All(Phd2Counters, column => Assert.DoesNotContain(column, scanRunColumns));
        using (var connection = new SqliteConnection(database.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO scan_runs (started_at, trigger, state, discovered, new_files, " +
                "changed_files, completed, failed, skipped_calibration, removed) " +
                "VALUES ('2026-01-01 00:00:00', 'scheduler', 'complete', 7, 3, 2, 7, 0, 1, 0);";
            command.ExecuteNonQuery();
        }

        using (var context = Open(database))
        {
            context.Database.Migrate();
        }

        using var reader = Open(database);
        var run = Assert.Single(reader.ScanRuns);
        Assert.Equal(0, run.Phd2Found);
        Assert.Equal(0, run.Phd2Ingested);
        Assert.Equal(0, run.Phd2Failed);

        // The row the user already had is otherwise untouched by the upgrade.
        Assert.Equal("scheduler", run.Trigger);
        Assert.Equal(7, run.Discovered);
    }

    /// <summary>
    /// <c>started_at_local</c> round trips as the same naive wall clock, unshifted, and comes back
    /// with <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    /// <remarks>
    /// Spec 5.16 calls that column a naive wall clock precisely because it must never be
    /// converted. This project's habit at every other read edge is
    /// <c>DateTime.SpecifyKind(value, DateTimeKind.Utc)</c>, in <c>ActivityQuery</c> and six
    /// view-models, and applying it to this column by reflex would shift every stored session by
    /// the machine's offset, silently. A failure looks like a night's guiding appearing hours away
    /// from the frames it belongs to, with nothing in the suite to say why. Both columns are
    /// asserted, because the UTC column holds a UTC wall clock and is equally not to be shifted.
    /// </remarks>
    [Fact]
    public void SessionTimestamps_RoundTripAsTheSameWallClock_WithNoConversion()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        var logId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var local = new DateTime(2026, 3, 14, 22, 47, 13, DateTimeKind.Unspecified);
        var utc = new DateTime(2026, 3, 15, 5, 47, 13, DateTimeKind.Utc);

        using (var context = Open(database))
        {
            context.Phd2Logs.Add(Log(logId, @"C:\Astro\guide\PHD2_GuideLog_roundtrip.txt"));
            var session = Session(sessionId, logId);
            session.StartedAtLocal = local;
            session.StartedAtUtc = utc;
            context.Phd2Sessions.Add(session);
            context.SaveChanges();
        }

        using var reader = Open(database);
        var stored = Assert.Single(reader.Phd2Sessions);

        // Wall clock for wall clock, to the second, on both columns.
        Assert.Equal(local, stored.StartedAtLocal);
        Assert.Equal(utc.Ticks, stored.StartedAtUtc!.Value.Ticks);

        // And no kind is invented on the way back. Microsoft.Data.Sqlite writes a DateTime's wall
        // clock as TEXT, discards its Kind and reads it back Unspecified, which is why the local
        // column is safe and why the UTC column's kind is pinned at the read edge by its reader
        // rather than here. What ships: neither entity property declares a value converter, and
        // neither is given one.
        Assert.Equal(DateTimeKind.Unspecified, stored.StartedAtLocal.Kind);
        Assert.Equal(DateTimeKind.Unspecified, stored.StartedAtUtc.Value.Kind);
    }

    internal static Phd2Log Log(Guid id, string filePath) => new()
    {
        Id = id,
        FilePath = filePath,
        FileSize = 1024,
        FileMtime = 1_700_000_000d,
        ParseStatus = "ok",
        RunCount = 1,
        SessionCount = 1,
        CalibrationCount = 1,
        ParsedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
    };

    internal static Phd2Session Session(Guid id, Guid logId) => new()
    {
        Id = id,
        LogId = logId,
        RunIndex = 0,
        SectionIndex = 0,
        StartedAtLocal = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Unspecified),
        StartedAtUtc = new DateTime(2026, 1, 2, 3, 0, 0, DateTimeKind.Utc),
        SessionDate = new DateOnly(2026, 1, 1),
        DurationS = 3600,
        FrameCount = 120,
    };

    internal static Phd2Frame Frame(Guid sessionId, int index) => new()
    {
        SessionId = sessionId,
        FrameIndex = index,
        TimeOffset = index * 2.5,
        RaRaw = 0.1,
        DecRaw = -0.2,
    };

    internal static Phd2Calibration Calibration(Guid logId) => new()
    {
        Id = Guid.NewGuid(),
        LogId = logId,
        StartedAtLocal = new DateTime(2026, 1, 1, 21, 0, 0, DateTimeKind.Unspecified),
        Completed = true,
    };

    internal static GalactiLogContext Open(TestDatabaseHandle database)
        => new(GalactiLogContextOptions.Create(database.ConnectionString, tracking: true));

    private static List<string> ColumnShapes(string connectionString, string table)
        => TableInfo(connectionString, table, reader =>
            $"{reader.GetString(1)} {reader.GetString(2)} {reader.GetInt32(3)}");

    private static List<string> ColumnNames(string connectionString, string table)
        => TableInfo(connectionString, table, reader => reader.GetString(1));

    private static List<string> TableInfo(
        string connectionString, string table, Func<SqliteDataReader, string> project)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        // PRAGMA table_info takes no parameter binding, so the name is validated against the
        // table list this file declares rather than interpolated from anything a caller supplies.
        Assert.Contains(table, new[] { "scan_runs" }.Concat(Phd2Tables));
        command.CommandText = $"PRAGMA table_info({table});";

        var rows = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(project(reader));
        }

        return rows;
    }

    private static List<string> TableNames(string connectionString)
        => MasterNames(connectionString, "table");

    private static List<string> IndexNames(string connectionString)
        => MasterNames(connectionString, "index");

    private static List<string> IndexNamesOf(string connectionString, string table)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = $table " +
            "AND name NOT LIKE 'sqlite_%';";
        command.Parameters.AddWithValue("$table", table);

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static List<string> MasterNames(string connectionString, string type)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = $type AND name NOT LIKE 'sqlite_%';";
        command.Parameters.AddWithValue("$type", type);

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
