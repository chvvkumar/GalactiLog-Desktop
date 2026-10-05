using System.Collections.Generic;
using GalactiLog.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests;

public class MigrationTests
{
    private static readonly HashSet<string> ExpectedTables = new()
    {
        "images",
        "targets",
        "target_catalog_memberships",
        "openngc_catalog",
        "static_catalog_entries",
        "catalog_cache",
        "user_settings",
        "session_notes",
        "merge_candidates",
        "merge_manifests",
        "activity_events",
        "scan_runs",
        // Migration 0004 (Phd2), spec 5.15 to 5.18.
        "phd2_logs",
        "phd2_sessions",
        "phd2_frames",
        "phd2_calibrations",
        // Migration 0006 (CustomColumns), spec 5.19 to 5.20.
        "custom_columns",
        "custom_column_values",
        // Migration 0007 (SkippedFiles), spec 5.21.
        "skipped_files",
        "__EFMigrationsHistory",
        // EF Core 10 adds this internal table to serialize concurrent migration runs.
        // Not part of design-spec 5; a runtime detail of the migrations infrastructure.
        "__EFMigrationsLock",
    };

    private static readonly string[] ExpectedIndexNames =
    {
        "IX_images_file_path",
        "IX_targets_primary_name",
        "IX_targets_catalog_id_normalized",
        "IX_target_catalog_memberships_target_id_catalog_name",
        "IX_session_notes_target_id_session_date",
    };

    [Fact]
    public void Migrate_OnEmptyFile_CreatesExpectedTables()
    {
        using var db = TestDatabaseFactory.CreateFreshMigratedDatabase();
        var connectionString = db.ConnectionString;

        var tables = QueryNames(connectionString, "table");

        Assert.Equal(ExpectedTables, tables);
    }

    [Fact]
    public void Migrate_OnEmptyFile_CreatesExpectedIndexes()
    {
        using var db = TestDatabaseFactory.CreateFreshMigratedDatabase();
        var connectionString = db.ConnectionString;

        var indexes = QueryNames(connectionString, "index");

        foreach (var expected in ExpectedIndexNames)
        {
            Assert.Contains(expected, indexes);
        }

        // The unique indexes must actually be unique in sqlite_master's "sql" column.
        AssertIndexIsUnique(connectionString, "IX_images_file_path");
        AssertIndexIsUnique(connectionString, "IX_targets_primary_name");
        AssertIndexIsUnique(connectionString, "IX_targets_catalog_id_normalized");
        AssertIndexIsUnique(connectionString, "IX_target_catalog_memberships_target_id_catalog_name");
        AssertIndexIsUnique(connectionString, "IX_session_notes_target_id_session_date");

        // Migration 0002 (PartialUniqueTargetIndexes, review ruling item 13): the two unique
        // indexes on `targets` are PARTIAL on merged_into_id IS NULL, so a merged-away target
        // never blocks re-creating an active target with the same designation (spec 5.3).
        AssertIndexSqlContains(connectionString, "IX_targets_primary_name",
            "WHERE merged_into_id IS NULL");
        AssertIndexSqlContains(connectionString, "IX_targets_catalog_id_normalized",
            "WHERE catalog_id_normalized IS NOT NULL AND merged_into_id IS NULL");
    }

    // Migration 0003 (ImagesFilePathNoCase, phase 4 review item 1). SQLite cannot alter a
    // column in place, so EF rebuilds the table; this asserts both halves of that rebuild
    // landed -- the collation on the column and the unique index recreated over it.
    [Fact]
    public void Migrate_OnEmptyFile_GivesImagesFilePathTheNoCaseCollation()
    {
        using var db = TestDatabaseFactory.CreateFreshMigratedDatabase();

        var tableSql = TableSql(db.ConnectionString, "images");
        Assert.NotNull(tableSql);
        Assert.Contains("\"file_path\" TEXT COLLATE NOCASE", tableSql, System.StringComparison.OrdinalIgnoreCase);

        var indexes = QueryNames(db.ConnectionString, "index");
        Assert.Contains("IX_images_file_path", indexes);
        AssertIndexIsUnique(db.ConnectionString, "IX_images_file_path");
    }

    // Migration 0007 (SkippedFiles, spec 5.21). A failure here means the writer's upsert lookup
    // and the known set disagree on a case-only path variant, so a skipped frame is read again
    // on every scan, which is the cost the table exists to remove.
    [Fact]
    public void Migrate_OnEmptyFile_GivesSkippedFilesFilePathTheNoCaseCollation()
    {
        using var db = TestDatabaseFactory.CreateFreshMigratedDatabase();

        var tableSql = TableSql(db.ConnectionString, "skipped_files");
        Assert.NotNull(tableSql);
        Assert.Contains("\"file_path\" TEXT COLLATE NOCASE", tableSql, System.StringComparison.OrdinalIgnoreCase);
    }

    private static string? TableSql(string connectionString, string tableName)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        return (string?)command.ExecuteScalar();
    }

    [Fact]
    public void Migrate_RunTwice_IsIdempotent()
    {
        using var db = TestDatabaseFactory.CreateFreshMigratedDatabase();
        var connectionString = db.ConnectionString;

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        var exception = Record.Exception(() => context.Database.Migrate());
        Assert.Null(exception);

        var tables = QueryNames(connectionString, "table");
        var indexes = QueryNames(connectionString, "index");

        Assert.Equal(ExpectedTables, tables);
        foreach (var expected in ExpectedIndexNames)
        {
            Assert.Contains(expected, indexes);
        }
    }

    private static HashSet<string> QueryNames(string connectionString, string type)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name NOT LIKE 'sqlite_%'";
        command.Parameters.AddWithValue("$type", type);

        var names = new HashSet<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static void AssertIndexSqlContains(string connectionString, string indexName, string fragment)
    {
        var sql = IndexSql(connectionString, indexName);
        Assert.NotNull(sql);
        Assert.Contains(fragment, sql, System.StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertIndexIsUnique(string connectionString, string indexName)
    {
        var sql = IndexSql(connectionString, indexName);
        Assert.NotNull(sql);
        Assert.Contains("UNIQUE", sql, System.StringComparison.OrdinalIgnoreCase);
    }

    private static string? IndexSql(string connectionString, string indexName)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name";
        command.Parameters.AddWithValue("$name", indexName);
        return (string?)command.ExecuteScalar();
    }
}
