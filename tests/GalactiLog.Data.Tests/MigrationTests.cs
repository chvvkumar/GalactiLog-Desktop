using System.Collections.Generic;
using GalactiLog.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
        // Migration 0008 (Mosaics), spec 5.22 to 5.25.
        "mosaics",
        "mosaic_panels",
        "mosaic_panel_sessions",
        "mosaic_suggestions",
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

    // Migration 0008 (Mosaics, spec 5.22 to 5.25). The custom_column_values rebuild that adds the
    // mosaic foreign key must not lose the raw SQL index of migration 0006, and the session
    // index must fold a null frame label so two null-label rows of one triple collide.
    [Fact]
    public void Migrate_Mosaics_KeepsTheCustomValueIndex_AndCreatesTheMosaicIndexes()
    {
        using var db = TestDatabaseFactory.CreateFreshMigratedDatabase();

        var indexes = QueryNames(db.ConnectionString, "index");
        foreach (var name in new[]
        {
            "uq_custom_column_value", "ix_custom_column_values_mosaic", "ux_mosaics_name",
            "ux_mosaic_panels_mosaic_label", "ux_mosaic_panel_sessions_panel_target_date_label",
            "ix_mosaic_panel_sessions_target_date", "ix_mosaic_suggestions_status",
            "ix_mosaic_suggestions_dedup_signature", "ix_images_panel_label",
        })
        {
            Assert.Contains(name, indexes);
        }

        AssertIndexSqlContains(db.ConnectionString, "uq_custom_column_value", "coalesce(\"mosaic_id\", '')");
        AssertIndexSqlContains(db.ConnectionString, "ux_mosaic_panel_sessions_panel_target_date_label",
            "coalesce(\"frame_label\", '') COLLATE NOCASE");
        Assert.Contains("REFERENCES \"mosaics\"", TableSql(db.ConnectionString, "custom_column_values"),
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void Migrate_Mosaics_RollsBackAndReapplies()
    {
        using var db = TestDatabaseFactory.CreateFreshMigratedDatabase();
        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(db.ConnectionString, tracking: true)))
        {
            context.GetService<IMigrator>().Migrate("20261005022536_SkippedFiles");
        }

        var tables = QueryNames(db.ConnectionString, "table");
        Assert.DoesNotContain("mosaics", tables);
        Assert.DoesNotContain("mosaic_panel_sessions", tables);
        Assert.Contains("uq_custom_column_value", QueryNames(db.ConnectionString, "index"));
        Assert.DoesNotContain("panel_label", TableSql(db.ConnectionString, "images"), System.StringComparison.Ordinal);
        Assert.DoesNotContain("mosaics", TableSql(db.ConnectionString, "custom_column_values"), System.StringComparison.Ordinal);

        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(db.ConnectionString, tracking: true)))
        {
            context.Database.Migrate();
        }

        Assert.Equal(ExpectedTables, QueryNames(db.ConnectionString, "table"));
        Assert.Contains("uq_custom_column_value", QueryNames(db.ConnectionString, "index"));
    }

    // Spec 5.22: deleting a mosaic cascades to its panels, through them to their nights, and to
    // its mosaic-scope custom values.
    [Fact]
    public void DeletingAMosaic_CascadesToPanelsSessionsAndCustomValues()
    {
        using var db = TestDatabaseFactory.CreateFreshMigratedDatabase();
        var target = LibrarySeeder.AddTarget(db.ConnectionString, "NGC 7000");
        var mosaic = System.Guid.NewGuid();
        var panel = System.Guid.NewGuid();
        var column = System.Guid.NewGuid();
        using (var context = new GalactiLogContext(GalactiLogContextOptions.Create(db.ConnectionString, tracking: true)))
        {
            context.Mosaics.Add(new Entities.Mosaic { Id = mosaic, Name = "NGC 7000" });
            context.MosaicPanels.Add(new Entities.MosaicPanel { Id = panel, MosaicId = mosaic, PanelLabel = "Panel 1" });
            context.MosaicPanelSessions.Add(new Entities.MosaicPanelSession
            {
                Id = System.Guid.NewGuid(), PanelId = panel, TargetId = target.Id,
                SessionDate = new System.DateOnly(2026, 3, 1), Status = "included",
            });
            context.CustomColumns.Add(new Entities.CustomColumn
            {
                Id = column, Name = "Status", Slug = "custom_status", ColumnType = "text", AppliesTo = "mosaic",
            });
            context.CustomColumnValues.Add(new Entities.CustomColumnValue
            {
                Id = System.Guid.NewGuid(), ColumnId = column, MosaicId = mosaic, Value = "framing",
            });
            context.SaveChanges();
        }

        Execute(db.ConnectionString, "DELETE FROM mosaics");

        Assert.Equal(0L, Count(db.ConnectionString, "mosaic_panels"));
        Assert.Equal(0L, Count(db.ConnectionString, "mosaic_panel_sessions"));
        Assert.Equal(0L, Count(db.ConnectionString, "custom_column_values"));
        Assert.Equal(1L, Count(db.ConnectionString, "custom_columns"));
    }

    private static void Execute(string connectionString, string sql)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        context.Database.ExecuteSqlRaw(sql);
    }

    private static long Count(string connectionString, string table)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
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
