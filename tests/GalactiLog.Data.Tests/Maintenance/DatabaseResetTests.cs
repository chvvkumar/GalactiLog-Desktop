using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Maintenance;
using GalactiLog.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.Data.Tests.Maintenance;

// The roadmap's Phase 9 row 7 second Verify clause: "reset database recreates an empty schema".
// Plus questions.md Q28 (rows, never the file) and Q29 (what is kept).
//
// No file is created, moved or deleted by anything under test here. The one test that touches the
// filesystem reads the database file's identity before and after, to prove it is the same file.
public class DatabaseResetTests : IDisposable
{
    private readonly TestDatabaseHandle _db;

    public DatabaseResetTests() => _db = TestDatabaseFactory.CreateFreshMigratedDatabase();

    public void Dispose() => _db.Dispose();

    private sealed class Lease : IDisposable
    {
        public void Dispose() { }
    }

    private DatabaseReset Make(Func<IDisposable?>? tryBeginResolution = null)
        => new(_db.ConnectionString, tryBeginResolution ?? (() => new Lease()), NullLogger.Instance);

    private GalactiLogContext Open(bool tracking = false)
        => new(GalactiLogContextOptions.Create(_db.ConnectionString, tracking));

    private string DatabaseFilePath()
    {
        var match = Regex.Match(_db.ConnectionString, @"Data Source=([^;]+)");
        Assert.True(match.Success, "no Data Source in the test connection string");
        return match.Groups[1].Value.Trim();
    }

    // Every table the reset clears, plus every table it keeps, with at least one row each.
    private void SeedEverything()
    {
        var target = LibrarySeeder.AddTarget(_db.ConnectionString, "NGC 7331");
        var loser = LibrarySeeder.AddTarget(_db.ConnectionString, "NGC7331 field");
        LibrarySeeder.AddFrame(_db.ConnectionString, target.Id, new DateOnly(2025, 3, 15));
        LibrarySeeder.AddMergeCandidate(_db.ConnectionString, "NGC7331 field");
        LibrarySeeder.AddSessionNote(_db.ConnectionString, target.Id, new DateOnly(2025, 3, 15), "clear");

        using var context = Open(tracking: true);

        // Review finding I1: an un-undone merge leaves loser.merged_into_id pointing at the winner,
        // and targets carries a self-referencing FK with ON DELETE RESTRICT which SQLite enforces
        // per row. Without this line the seed never exercises it and a reset that rolls the whole
        // transaction back on any library with a merge in it passes the suite.
        context.Targets.Single(row => row.Id == loser.Id).MergedIntoId = target.Id;
        context.Targets.Single(row => row.Id == loser.Id).MergedAt = DateTime.UtcNow;

        context.MergeManifests.Add(new MergeManifest
        {
            Id = Guid.NewGuid(),
            WinnerId = target.Id,
            LoserId = loser.Id,
            Payload = "{}",
            CreatedAt = DateTime.UtcNow,
        });
        context.TargetCatalogMemberships.Add(new TargetCatalogMembership
        {
            TargetId = target.Id,
            CatalogName = "Messier",
            CatalogNumber = "31",
        });
        context.ScanRuns.Add(new ScanRun
        {
            StartedAt = DateTime.UtcNow,
            Trigger = "manual",
            State = "complete",
        });
        context.ActivityEvents.Add(new ActivityEvent
        {
            Timestamp = DateTime.UtcNow,
            Category = "scan",
            EventType = "scan_started",
            Message = "seeded",
        });
        context.CatalogCacheEntries.Add(new CatalogCacheEntry
        {
            Source = "simbad",
            Key = "m 31",
            Payload = "{\"name\":\"M 31\"}",
            Negative = false,
            FetchedAt = DateTime.UtcNow,
        });
        context.CatalogCacheEntries.Add(new CatalogCacheEntry
        {
            Source = "resolver",
            Key = "zzyzx blob 42",
            Payload = null,
            Negative = true,
            FetchedAt = DateTime.UtcNow,
        });
        context.OpenNgcCatalogEntries.Add(new OpenNgcCatalogEntry { Name = "NGC 7331" });
        context.StaticCatalogEntries.Add(new StaticCatalogEntry
        {
            CatalogName = "Messier",
            CatalogNumber = "31",
            Payload = "{}",
        });
        context.SaveChanges();

        // Through the real settings write path, which owns the single user_settings row and its
        // id = 1 check constraint.
        new SettingsStore(new SettingsRepository(_db.ConnectionString))
            .SaveGeneral(new GeneralSettings { ScanRoots = [@"C:\Astro"] });
    }

    private int CountOf(string table)
    {
        using var context = Open();
        context.Database.OpenConnection();
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT count(*) FROM " + table + ";";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    // ---- the roadmap's second named assertion ------------------------------------------------

    [Fact]
    public void Run_RecreatesAnEmptySchema()
    {
        SeedEverything();

        var outcome = Make().Run(CancellationToken.None);

        Assert.Equal(DatabaseReset.ResetStatus.Completed, outcome.Status);
        Assert.True(outcome.SchemaRecreated);
        Assert.Equal(DatabaseReset.ClearedTables.Count, outcome.TablesCleared);
        Assert.True(outcome.RowsDeleted > 0);

        foreach (var table in DatabaseReset.ClearedTables)
        {
            // activity_events is emptied with the rest and then holds exactly the two rows the
            // reset writes about itself: they are written after the delete, deliberately, because
            // a rebuild_started written before it would be one of the rows deleted.
            var expected = table == "activity_events" ? 2 : 0;
            Assert.Equal(expected, CountOf(table));
        }

        // The schema is intact and at the current version: Migrate is a no-op with nothing
        // pending, which is what "recreates an empty schema" means for a row-level reset.
        using var context = Open(tracking: true);
        Assert.Empty(context.Database.GetPendingMigrations());
        context.Database.Migrate();
        Assert.Empty(context.Database.GetPendingMigrations());
    }

    [Fact]
    public void Run_KeepsUserSettings()
    {
        SeedEverything();

        Make().Run(CancellationToken.None);

        var general = new SettingsStore(new SettingsRepository(_db.ConnectionString)).GetGeneral();
        Assert.Equal(new[] { @"C:\Astro" }, general.ScanRoots);
        Assert.Equal(1, CountOf("user_settings"));
    }

    [Fact]
    public void Run_KeepsTheShippedCatalogues()
    {
        SeedEverything();

        Make().Run(CancellationToken.None);

        foreach (var table in DatabaseReset.KeptTables)
        {
            Assert.True(CountOf(table) > 0, $"{table} should have been kept");
        }
    }

    [Fact]
    public void Run_ClearsPositiveCatalogCacheRows()
    {
        // Spec 9.6: positive rows are "Cleared only by the reset-database action", which names
        // this as the one thing that clears them.
        SeedEverything();
        Assert.Equal(
            CatalogCacheRepository.CacheHit.Positive,
            new CatalogCacheRepository(_db.ConnectionString).Get("simbad", "m 31").Kind);

        Make().Run(CancellationToken.None);

        Assert.Equal(0, CountOf("catalog_cache"));
    }

    [Fact]
    public void Run_ClearsEveryTableInOneTransaction()
    {
        // The failure is injected by dropping a table the delete reaches late, so the statements
        // before it have already run when the transaction rolls back. Nothing may survive that
        // rollback: a half-emptied database is the state this action must never leave behind.
        SeedEverything();
        using (var context = Open(tracking: true))
        {
            context.Database.ExecuteSqlRaw("DROP TABLE catalog_cache");
        }

        Assert.ThrowsAny<Exception>(() => Make().Run(CancellationToken.None));

        Assert.Equal(1, CountOf("images"));
        Assert.Equal(2, CountOf("targets"));
        Assert.Equal(1, CountOf("merge_manifests"));
        Assert.Equal(1, CountOf("session_notes"));
        Assert.Equal(1, CountOf("activity_events"));
    }

    [Fact]
    public void Run_DeletesNoFile()
    {
        SeedEverything();
        var path = DatabaseFilePath();
        Assert.True(File.Exists(path));
        var created = File.GetCreationTimeUtc(path);

        Make().Run(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(created, File.GetCreationTimeUtc(path));
    }

    [Fact]
    public void Run_WhileAScanIsRunning_IsRefused_AndTouchesNothing()
    {
        SeedEverything();

        var outcome = Make(() => null).Run(CancellationToken.None);

        Assert.Equal(DatabaseReset.ResetStatus.ScanInProgress, outcome.Status);
        Assert.False(outcome.SchemaRecreated);
        Assert.Equal(0, outcome.TablesCleared);
        Assert.Equal(0, outcome.RowsDeleted);

        Assert.Equal(1, CountOf("images"));
        Assert.Equal(2, CountOf("targets"));
        Assert.Equal(1, CountOf("activity_events"));
    }

    // ---- spec 10.9 --------------------------------------------------------------------------

    [Fact]
    public void Run_EmitsRebuildStartedAndRebuildComplete_AfterTheDelete()
    {
        SeedEverything();

        Make().Run(CancellationToken.None);

        using var context = Open();
        var events = context.ActivityEvents.OrderBy(row => row.Id).ToList();

        // Exactly two rows: the seeded scan_started went with everything else, so these are the
        // first two entries of the new log and they say what emptied it.
        Assert.Equal(2, events.Count);
        Assert.Equal("rebuild_started", events[0].EventType);
        Assert.Equal("rebuild_complete", events[1].EventType);
        Assert.All(events, row => Assert.Equal("rebuild", row.Category));
        Assert.All(events, row => Assert.Equal("info", row.Severity));

        foreach (var row in events)
        {
            using var details = JsonDocument.Parse(row.Details!);
            Assert.Equal(
                new[] { "action", "affected" },
                details.RootElement.EnumerateObject().Select(property => property.Name).Order().ToList());
            Assert.Equal("reset_database", details.RootElement.GetProperty("action").GetString());
        }
    }

    [Fact]
    public void Run_TheEmittedEventsSurvive()
    {
        SeedEverything();

        Make().Run(CancellationToken.None);

        using var context = Open();
        Assert.Single(context.ActivityEvents, row => row.EventType == "rebuild_complete");
    }

    [Fact]
    public void Run_OnAnAlreadyEmptyDatabase_IsANoOpAndDoesNotThrow()
    {
        var first = Make().Run(CancellationToken.None);
        Assert.Equal(0, first.RowsDeleted);

        // The second run has the first run's two activity rows to delete and nothing else.
        var second = Make().Run(CancellationToken.None);
        Assert.Equal(DatabaseReset.ResetStatus.Completed, second.Status);
        Assert.Equal(2, second.RowsDeleted);
        Assert.True(second.SchemaRecreated);
    }

    [Fact]
    public void ClearedAndKeptTables_CoverEveryTableInTheSchema()
    {
        // The reset's contract is the two lists, and a table added to the schema without being
        // added to one of them would silently survive a reset. This is the check that fails when
        // that happens.
        using var context = Open();
        context.Database.OpenConnection();
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";

        var tables = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        // EF's own migrations-history and migrations-lock tables are not application data and
        // belong to neither list. Clearing the history would make Migrate re-run every migration.
        var accounted = DatabaseReset.ClearedTables
            .Concat(DatabaseReset.KeptTables)
            .Concat(["__EFMigrationsHistory", "__EFMigrationsLock"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unaccounted = tables.Where(table => !accounted.Contains(table)).ToList();
        Assert.True(
            unaccounted.Count == 0,
            "tables in neither DatabaseReset list: " + string.Join(", ", unaccounted));
    }
}
