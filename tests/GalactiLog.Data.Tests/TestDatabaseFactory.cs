using System;
using System.IO;
using GalactiLog.Core.Catalogs;
using GalactiLog.Data;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Tests;

public static class TestDatabaseFactory
{
    // Six migrations cost 180 to 290 ms per case alone (350 to 615 ms under the assembly's own
    // parallel load) and the catalog seed another 1.5 s, so both are paid once per process into
    // a template file that every case then copies. The template's last connection is closed
    // (Pooling=False, so there is no pool to clear) and its write-ahead log checkpointed away
    // before the first copy, so each copy is one self-contained file that opens clean.
    private static readonly Lazy<string> MigratedTemplate = new(() => BuildTemplate(seedCatalogs: false));
    private static readonly Lazy<string> SeededTemplate = new(() => BuildTemplate(seedCatalogs: true));

    // A migrated, empty database: a copy of the process-wide template. Disposing the handle
    // deletes the copy plus its -wal/-shm sidecars, so repeated test runs leave no
    // galactilog-test-*.db files in the OS temp directory (these are test-only writes via plain
    // File I/O in the test project, which FileSafetyTest does not scan).
    public static TestDatabaseHandle CreateMigratedDatabase() => CopyOf(MigratedTemplate.Value);

    // The same, with the bundled catalogs already loaded through CatalogSeeder.LoadIfNeeded,
    // for the classes that would otherwise seed 20k rows in every constructor.
    public static TestDatabaseHandle CreateSeededDatabase() => CopyOf(SeededTemplate.Value);

    // Migrates a fresh file for this one handle, for the cases whose subject is the migration
    // itself (MigrationTests, DatabaseResetTests).
    public static TestDatabaseHandle CreateFreshMigratedDatabase()
    {
        var (path, connectionString) = NewFile();
        Migrate(connectionString);
        return new TestDatabaseHandle(path, connectionString);
    }

    private static (string Path, string ConnectionString) NewFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"galactilog-test-{Guid.NewGuid():N}.db");

        // Pooling=False, for test databases only. The production connection string is pooled
        // and stays that way; what changes here is the teardown contract.
        //
        // Microsoft.Data.Sqlite's connection pool is a PROCESS-WIDE cache keyed by connection
        // string, and xUnit runs test classes in parallel. A pooled connection keeps the file
        // handle open after Close, so this handle's Dispose could not delete its own file
        // without first emptying that pool -- and the only call that reliably did so,
        // SqliteConnection.ClearAllPools(), empties EVERY class's pool, not just this one.
        // Clearing a pool calls ReclaimLeakedConnections, which deactivates connections
        // belonging to whichever other classes happen to be mid-test; that surfaced as
        // "SQLite Error 5: database is locked" thrown out of an unrelated class's Dispose or
        // query (observed in 1 full assembly run in 5). SqliteConnection.ClearPool is scoped
        // but only clears a pool group the connection has actually been opened into, so it
        // silently under-cleared and left files locked.
        //
        // With pooling off there is no shared cache to reach into and no clearing step at all:
        // every Close releases its file handle immediately, so Dispose just deletes. One test
        // class can no longer perturb another's connections by construction.
        var connectionString = new SqliteConnectionStringBuilder(DatabasePaths.BuildConnectionString(path))
        {
            Pooling = false,
        }.ToString();

        return (path, connectionString);
    }

    private static void Migrate(string connectionString)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString, tracking: true));
        context.Database.Migrate();
    }

    private static string BuildTemplate(bool seedCatalogs)
    {
        var (path, connectionString) = NewFile();
        Migrate(connectionString);
        if (seedCatalogs)
        {
            new CatalogSeeder(connectionString, new SettingsStore(new SettingsRepository(connectionString)))
                .LoadIfNeeded(StaticCatalogLoader.ResolveCatalogsDirectory());
        }

        // Fold the log back into the main file. The last close does this on its own, but the
        // copies below must never race a -wal that is still being written, so it is explicit
        // and checked.
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }

        if (File.Exists(path + "-wal"))
        {
            throw new InvalidOperationException($"The template database still has a write-ahead log: {path}-wal");
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { File.Delete(path); } catch (IOException) { }
        };
        return path;
    }

    private static TestDatabaseHandle CopyOf(string template)
    {
        var (path, connectionString) = NewFile();
        File.Copy(template, path);
        return new TestDatabaseHandle(path, connectionString);
    }
}

public sealed class TestDatabaseHandle(string path, string connectionString) : IDisposable
{
    public string ConnectionString { get; } = connectionString;

    public void Dispose()
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }
}
