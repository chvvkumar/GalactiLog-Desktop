using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

/// <summary>
/// The roadmap's Phase 10 row 1 Verify clause for the Data half: every row count and size field
/// asserted against a seeded database.
/// </summary>
public class DiagnosticsQueryTests
{
    private const int SessionNoteCount = 3;
    private const int ActivityEventCount = 5;
    private const int MergeCandidateCount = 2;
    private const int ScanRunCount = 4;
    private const int PositiveCacheRows = 3;
    private const int NegativeCacheRows = 2;

    // Spec 12.8's order: the original seven, then the four guide-log tables of ruling F2, appended
    // rather than interleaved.
    private static readonly string[] SpecTables =
    [
        "images", "targets", "session_notes", "activity_events",
        "catalog_cache", "merge_candidates", "scan_runs",
        "phd2_logs", "phd2_sessions", "phd2_frames", "phd2_calibrations",
    ];

    private static DiagnosticsQuery Query(TestDatabaseHandle database)
        => new(new DatabaseConnectionString(database.ConnectionString));

    // The spec 18.2 fixture plus a handful of rows in each of the five tables it does not fill,
    // so every one of the seven keys has a non-zero count to assert against.
    private static void SeedEveryTable(string connectionString)
    {
        LibrarySeeder.Seed(connectionString);

        using var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(connectionString, tracking: true));

        for (var index = 0; index < SessionNoteCount; index++)
        {
            context.SessionNotes.Add(new SessionNote
            {
                Id = Guid.NewGuid(),
                TargetId = LibrarySeeder.Targets[0].Id,
                SessionDate = LibrarySeeder.FirstSessionDate.AddDays(index),
                Notes = "seeded note",
                UpdatedAt = DateTime.UtcNow,
            });
        }

        for (var index = 0; index < ActivityEventCount; index++)
        {
            context.ActivityEvents.Add(new ActivityEvent
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-index),
                Severity = "info",
                Category = "scan",
                EventType = "scan_started",
                Message = "Scan started (manual)",
            });
        }

        for (var index = 0; index < MergeCandidateCount; index++)
        {
            context.MergeCandidates.Add(new MergeCandidate
            {
                Id = Guid.NewGuid(),
                SourceName = "candidate " + index,
                SourceImageCount = 1,
                Method = "token",
                CreatedAt = DateTime.UtcNow,
                Status = "pending",
            });
        }

        for (var index = 0; index < ScanRunCount; index++)
        {
            context.ScanRuns.Add(new ScanRun
            {
                StartedAt = DateTime.UtcNow.AddMinutes(-index),
                Trigger = "manual",
                State = "complete",
            });
        }

        for (var index = 0; index < PositiveCacheRows; index++)
        {
            context.CatalogCacheEntries.Add(new CatalogCacheEntry
            {
                Source = "simbad",
                Key = "positive " + index,
                Payload = "{}",
                Negative = false,
                FetchedAt = DateTime.UtcNow,
            });
        }

        for (var index = 0; index < NegativeCacheRows; index++)
        {
            context.CatalogCacheEntries.Add(new CatalogCacheEntry
            {
                Source = "simbad",
                Key = "negative " + index,
                Negative = true,
                FetchedAt = DateTime.UtcNow,
            });
        }

        context.SaveChanges();
    }

    [Fact]
    public void Database_RowCounts_MatchTheSeededRowsForEverySpecTable()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedEveryTable(database.ConnectionString);

        var counts = Query(database).Database().RowCounts;

        Assert.Equal(LibrarySeeder.FrameCount, counts["images"]);
        Assert.Equal(LibrarySeeder.TargetCount, counts["targets"]);
        Assert.Equal(SessionNoteCount, counts["session_notes"]);
        Assert.Equal(ActivityEventCount, counts["activity_events"]);
        Assert.Equal(PositiveCacheRows + NegativeCacheRows, counts["catalog_cache"]);
        Assert.Equal(MergeCandidateCount, counts["merge_candidates"]);
        Assert.Equal(ScanRunCount, counts["scan_runs"]);
    }

    [Fact]
    public void Database_RowCounts_HasExactlyTheSpecTables()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();

        var counts = Query(database).Database().RowCounts;

        // The ordered list is the contract; the dictionary is only the lookup, and a
        // Dictionary's enumeration order is unspecified (review finding M1). A further table is
        // therefore a deliberate change to RowCountTables rather than a silent one.
        Assert.Equal(SpecTables, DiagnosticsQuery.RowCountTables);
        Assert.Equal(SpecTables.Length, counts.Count);
        Assert.All(SpecTables, table => Assert.True(counts.ContainsKey(table), $"Missing row count: {table}"));
    }

    [Fact]
    public void Database_FileBytes_IsTheSizeOfTheDatabaseFile()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedEveryTable(database.ConnectionString);

        var diagnostics = Query(database).Database();

        Assert.True(File.Exists(diagnostics.FilePath), $"The reported path does not exist: {diagnostics.FilePath}");
        Assert.Equal(new FileInfo(diagnostics.FilePath).Length, diagnostics.FileBytes);
        Assert.True(diagnostics.FileBytes > 0);
    }

    [Fact]
    public void Database_WalBytes_IsZero_WhenNoWalFileExists()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();

        // Checked BEFORE the read, not after. Pooling is off for a test database, so the
        // migration's connection released its handle and SQLite removed the WAL; the diagnostics
        // read then opens the file again and leaves a zero-length sidecar of its own behind,
        // which a read-only connection cannot clean up on close.
        var path = DatabasePaths.DataSourceOf(database.ConnectionString);
        Assert.False(File.Exists(path + "-wal"));

        var diagnostics = Query(database).Database();

        // Spec ruling Q9: nothing uncheckpointed is 0 bytes, not "unavailable".
        Assert.Equal(0, diagnostics.WalBytes);
    }

    [Fact]
    public void Database_WalBytes_IsPositive_AfterAnUncheckpointedWrite()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();

        // The WAL only survives while a connection holds the file open, so the write connection
        // stays open across the read.
        using var holder = new SqliteConnection(database.ConnectionString);
        holder.Open();
        using (var command = holder.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO activity_events (timestamp, severity, category, event_type, message) " +
                "VALUES ('2025-01-01T00:00:00Z', 'info', 'scan', 'scan_started', 'x');";
            command.ExecuteNonQuery();
        }

        var diagnostics = Query(database).Database();

        Assert.True(diagnostics.WalBytes > 0, $"Expected a non-empty WAL, saw {diagnostics.WalBytes} bytes.");
    }

    [Fact]
    public void Database_PageCount_MatchesPragmaPageCount()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedEveryTable(database.ConnectionString);

        long expected;
        using (var connection = new SqliteConnection(database.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA page_count;";
            expected = Convert.ToInt64(command.ExecuteScalar());
        }

        Assert.Equal(expected, Query(database).Database().PageCount);
    }

    [Fact]
    public void Database_OnAnEmptyMigratedDatabase_ReturnsZeroForEveryRowCount()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();

        var counts = Query(database).Database().RowCounts;

        Assert.All(SpecTables, table => Assert.Equal(0, counts[table]));
    }

    /// <summary>
    /// Spec 12.8's "PHD2 data size" (ruling F2) says which of the two methods produced it. Whether
    /// the shipped native library exposes <c>dbstat</c> cannot be settled by reading, so the field
    /// is asserted to name one of the two branches rather than to name a chosen one.
    /// </summary>
    [Fact]
    public void Database_Phd2Bytes_NamesTheMethodThatProducedIt()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();

        var diagnostics = Query(database).Database();

        Assert.Contains(
            diagnostics.Phd2BytesMethod,
            new[] { DiagnosticsQuery.MeasuredMethod, DiagnosticsQuery.EstimatedMethod });
        Assert.True(diagnostics.Phd2Bytes >= 0);
    }

    /// <summary>
    /// The name filter the measured branch hands <c>dbstat</c> covers the four guide-log tables
    /// <b>and their ten indexes</b>, which is what spec 12.8 defines the figure to include.
    /// </summary>
    /// <remarks>
    /// Applied to <c>sqlite_master</c>, whose <c>name</c> column holds exactly the names
    /// <c>dbstat.name</c> reports, because <c>dbstat</c> is not exposed by this machine's shipped
    /// library and the measured branch therefore never runs here. A failure looks like the figure
    /// reporting the four tables' data pages alone, so the two indexes over the volume table are
    /// silently missing from the one field a reader checks before asking why the database grew,
    /// and nothing anywhere says so.
    /// </remarks>
    [Fact]
    public void Phd2NameFilter_MatchesTheFourTablesAndAllTenIndexes()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE (" + DiagnosticsQuery.Phd2NameFilter +
            ") AND name NOT LIKE 'sqlite_%' ORDER BY name;";

        var matched = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                matched.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
            new[]
            {
                "ix_phd2_calibrations_log_id",
                "ix_phd2_calibrations_session_date",
                "ix_phd2_calibrations_started_at_utc",
                "ix_phd2_frames_session_frame",
                "ix_phd2_frames_session_time",
                "ix_phd2_logs_file_path",
                "ix_phd2_sessions_log_id",
                "ix_phd2_sessions_session_date",
                "ix_phd2_sessions_started_at_utc",
                "ix_phd2_sessions_telescope_session_date",
                "phd2_calibrations",
                "phd2_frames",
                "phd2_logs",
                "phd2_sessions",
            },
            matched);
    }

    /// <summary>
    /// The estimate's denominator covers every table whose pages are in its numerator, the shipped
    /// catalogues included.
    /// </summary>
    /// <remarks>
    /// The numerator is the whole file's page count and the denominator was the eleven tables spec
    /// 12.8 reports, which leaves out <c>openngc_catalog</c> and <c>static_catalog_entries</c>.
    /// A failure looks like a library with a shipped catalogue and a handful of guide logs
    /// reporting very nearly the whole database as PHD2 data, which is the opposite of what a
    /// reader checking why the database grew needs.
    /// </remarks>
    [Fact]
    public void Database_Phd2Bytes_IsNotInflatedByTheShippedCatalogue()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        const int catalogueRows = 5000;
        using (var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(database.ConnectionString, tracking: true)))
        {
            for (var index = 0; index < catalogueRows; index++)
            {
                context.OpenNgcCatalogEntries.Add(new OpenNgcCatalogEntry { Name = "NGC " + index });
            }

            SeedOneGuideLog(context, frameCount: 10);
            context.SaveChanges();
        }

        var diagnostics = Query(database).Database();

        // The shipped catalogue is the overwhelming majority of the rows and of the file, so the
        // guide-log share must be a small fraction of it. Half is a deliberately loose bar: the
        // defect this pins reported the whole file.
        Assert.True(
            diagnostics.Phd2Bytes * 2 < diagnostics.FileBytes,
            $"PHD2 data size {diagnostics.Phd2Bytes} is at least half of the {diagnostics.FileBytes} "
            + $"byte file, on a library holding {catalogueRows} shipped catalogue rows and one guide log.");
    }

    /// <summary>
    /// The figure grows with the guide-log corpus, and a library with no guide logs reports less
    /// than one with them.
    /// </summary>
    /// <remarks>
    /// The empty figure is asserted as strictly below the seeded one rather than as exactly 0,
    /// because 0 holds on the estimated branch alone: SQLite gives every table and every index a
    /// root page as soon as it is created, so <c>dbstat</c> on an empty library reports fourteen
    /// pages rather than none. The exact zero is asserted only where it is true, which is what
    /// keeps this case from turning red for the wrong reason on a machine whose shipped library
    /// exposes <c>dbstat</c>.
    /// </remarks>
    [Fact]
    public void Database_Phd2Bytes_IsLowerWithNoGuideLogs_AndGrowsWithThem()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedEveryTable(database.ConnectionString);

        var empty = Query(database).Database();
        if (empty.Phd2BytesMethod == DiagnosticsQuery.EstimatedMethod)
        {
            Assert.Equal(0, empty.Phd2Bytes);
        }

        using (var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(database.ConnectionString, tracking: true)))
        {
            SeedOneGuideLog(context, frameCount: 500);
            context.SaveChanges();
        }

        var seeded = Query(database).Database();
        Assert.True(
            seeded.Phd2Bytes > empty.Phd2Bytes,
            $"PHD2 data size did not grow with the corpus: {empty.Phd2Bytes} bytes empty, "
            + $"{seeded.Phd2Bytes} bytes after one log, one session and 500 frames.");
    }

    // One log, one session and frameCount frames, added to the caller's context but not saved:
    // the caller decides what else rides in the same SaveChanges.
    private static void SeedOneGuideLog(GalactiLogContext context, int frameCount)
    {
        var logId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        context.Phd2Logs.Add(new Phd2Log
        {
            Id = logId,
            FilePath = @"C:\Astro\guide\PHD2_GuideLog.txt",
            ParseStatus = "ok",
            ParsedAt = DateTime.UtcNow,
        });
        context.Phd2Sessions.Add(new Phd2Session
        {
            Id = sessionId,
            LogId = logId,
            StartedAtLocal = new DateTime(2026, 1, 1, 22, 0, 0, DateTimeKind.Unspecified),
        });
        for (var index = 0; index < frameCount; index++)
        {
            context.Phd2Frames.Add(new Phd2Frame
            {
                SessionId = sessionId,
                FrameIndex = index,
                TimeOffset = index * 2.0,
            });
        }
    }

    [Fact]
    public void ResolverCache_SplitsPositiveAndNegativeRows()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        SeedEveryTable(database.ConnectionString);

        var cache = Query(database).ResolverCache();

        Assert.Equal(PositiveCacheRows, cache.CachePositive);
        Assert.Equal(NegativeCacheRows, cache.CacheNegative);
    }

    [Fact]
    public void ResolverCache_CacheExpired_CountsOnlyNegativeRowsOlderThanTheSevenDayTtl()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        using (var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(database.ConnectionString, tracking: true)))
        {
            context.CatalogCacheEntries.AddRange(
                new CatalogCacheEntry
                {
                    Source = "simbad",
                    Key = "expired negative",
                    Negative = true,
                    FetchedAt = DateTime.UtcNow.AddDays(-8),
                },
                new CatalogCacheEntry
                {
                    Source = "simbad",
                    Key = "fresh negative",
                    Negative = true,
                    FetchedAt = DateTime.UtcNow.AddDays(-1),
                },
                new CatalogCacheEntry
                {
                    Source = "simbad",
                    Key = "old positive",
                    Payload = "{}",
                    Negative = false,
                    FetchedAt = DateTime.UtcNow.AddDays(-8),
                });
            context.SaveChanges();
        }

        var cache = Query(database).ResolverCache();

        // A positive row never expires (spec 9.6), so the eight-day-old positive is not counted.
        Assert.Equal(1, cache.CacheExpired);
        Assert.Equal(2, cache.CacheNegative);
        Assert.Equal(1, cache.CachePositive);
    }

    [Fact]
    public void ResolverCache_CacheExpired_IsASubsetOfCacheNegative()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        using (var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(database.ConnectionString, tracking: true)))
        {
            for (var index = 0; index < 4; index++)
            {
                context.CatalogCacheEntries.Add(new CatalogCacheEntry
                {
                    Source = "simbad",
                    Key = "row " + index,
                    Negative = true,
                    FetchedAt = DateTime.UtcNow.AddDays(index < 3 ? -30 : -1),
                });
            }

            context.SaveChanges();
        }

        var cache = Query(database).ResolverCache();

        Assert.True(cache.CacheExpired <= cache.CacheNegative);
        Assert.Equal(3, cache.CacheExpired);
        Assert.Equal(4, cache.CacheNegative);
    }

    [Fact]
    public void SqliteVersion_IsANonEmptyDottedString()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();

        var version = Query(database).SqliteVersion();

        // Nullable by design: the Data layer reports what SQLite said and leaves the "unknown"
        // fallback to DiagnosticsService, which owns that literal (review finding M5).
        Assert.NotNull(version);
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Contains('.', version);
    }

    // Phase 10 verification finding: after a clean shutdown the database was 4 KB beside a 3.3 MB
    // galactilog.db-wal, where Phase 9 saw the write-ahead log checkpointed and removed at exit.
    //
    // This case is deliberately NOT built on TestDatabaseFactory: that fixture turns pooling off,
    // which is the very setting under test. It uses a real file under a temp directory and the
    // production connection string, pooled and read-write, exactly as AppHost builds it.
    [Fact]
    public void DiagnosticsRead_LeavesNoWriteAheadLogBehindTheLastWriteConnection()
    {
        var directory = Directory.CreateTempSubdirectory("galactilog-wal-").FullName;
        try
        {
            var path = Path.Combine(directory, DatabasePaths.DatabaseFileName);
            var connectionString = DatabasePaths.BuildConnectionString(path);

            using (var context = new GalactiLogContext(
                GalactiLogContextOptions.Create(connectionString, tracking: true)))
            {
                context.Database.Migrate();
                context.ActivityEvents.Add(new ActivityEvent
                {
                    Timestamp = DateTime.UtcNow,
                    Severity = "info",
                    Category = "scan",
                    EventType = "scan_started",
                    Message = "Scan started (manual)",
                });
                context.SaveChanges();
            }

            var query = new DiagnosticsQuery(new DatabaseConnectionString(connectionString));
            query.Database();
            query.ResolverCache();
            query.SqliteVersion();

            // What process exit does to the pooled read-write handles. Scoped to this connection
            // string's pool group, never ClearAllPools, which would reach into whatever other test
            // classes are running (the reason TestDatabaseFactory states at length).
            using (var probe = new SqliteConnection(connectionString))
            {
                SqliteConnection.ClearPool(probe);
            }

            // SQLite checkpoints and removes the write-ahead log when the LAST connection to the
            // file closes, and a read-only connection cannot perform that closing checkpoint. A
            // pooled read-only handle left open by a diagnostics read therefore outlives the last
            // writer and strands the log, which is what the verification agent observed.
            var wal = path + "-wal";
            Assert.True(
                !File.Exists(wal) || new FileInfo(wal).Length == 0,
                $"A diagnostics read stranded the write-ahead log: {wal} is "
                + $"{(File.Exists(wal) ? new FileInfo(wal).Length : 0)} bytes after the last writer closed.");
        }
        finally
        {
            // Best effort. A connection this test is complaining about still holds the file open
            // when the assertion above fails, and a throwing teardown would replace the failure
            // that matters with an IOException about a directory.
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void EveryMember_OpensReadOnly_AndCannotCreateADatabase()
    {
        using var database = TestDatabaseFactory.CreateMigratedDatabase();
        var query = Query(database);
        var path = DatabasePaths.DataSourceOf(database.ConnectionString);

        // The database is gone: an unplugged drive, or a user who deleted it between the window
        // opening and the Diagnostics page refreshing.
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }

        // A diagnostics read is the read a support bundle is built from, so it must not be able
        // to create or migrate anything (spec 2.1.2, review finding M4). Mode=ReadOnly is what
        // makes that structural: each member throws instead of quietly creating an empty file.
        Assert.ThrowsAny<Exception>(() => query.Database());
        Assert.ThrowsAny<Exception>(() => query.ResolverCache());
        Assert.ThrowsAny<Exception>(() => query.SqliteVersion());
        Assert.False(File.Exists(path), "A read-only diagnostics query created a database file.");
    }
}
