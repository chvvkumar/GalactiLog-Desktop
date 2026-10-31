using GalactiLog.Data;
using Microsoft.Data.Sqlite;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// One migrated, catalogue-seeded database, built once per test process by a real
/// <see cref="AppHost.Build(string?, bool, HttpMessageHandler?, Action{TimeSpan, CancellationToken}?)"/>
/// and copied into each host test's fresh app data root before that test builds its own host.
/// </summary>
/// <remarks>
/// Catalogue seeding is about half a second of every first <c>AppHost.Build</c> over an empty root
/// (OpenNGC plus five static catalogues parsed and inserted), and it was most of the time of every
/// host-building class in this assembly. With this copy in place the test's Build takes the
/// second-start path: migration and seeding find their work done and return. The first-start
/// path itself stays covered by the cases that opt out and build over an empty root:
/// <c>AppHostTests.Build_MigratesDatabase</c>, <c>Build_SeedsCatalogsOnFreshOverrideRoot</c>,
/// <c>Build_SeedingIsNoOpOnASecondBuildAgainstTheSameRoot</c> and
/// <c>Build_OnAFreshProfileWithNoDatabaseYet_Succeeds</c>.
/// Callers hold the assembly's serial execution (AssemblyInfo.cs), so the lazy build never races
/// another host.
/// </remarks>
public static class SeededAppData
{
    private static readonly Lazy<string> Template = new(BuildTemplate);

    /// <summary>Copies the seeded database into <paramref name="root"/> unless it already holds one.</summary>
    public static void CopyInto(string root)
    {
        var target = Path.Combine(root, DatabasePaths.DatabaseFileName);
        if (!File.Exists(target))
        {
            File.Copy(Template.Value, target);
        }
    }

    private static string BuildTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), "GalactiLogSeededTemplate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        };

        Serilog.Log.CloseAndFlush();
        AppHost.Build(root, cliMode: false).Dispose();
        Serilog.Log.CloseAndFlush();
        // Closing the last pooled connection checkpoints the WAL into the main file, so the one
        // file copied above is the whole database.
        SqliteConnection.ClearAllPools();

        var path = Path.Combine(root, DatabasePaths.DatabaseFileName);
        if (File.Exists(path + "-wal"))
        {
            throw new InvalidOperationException($"{path}-wal survived the pool clear; the template would be incomplete.");
        }

        return path;
    }
}
