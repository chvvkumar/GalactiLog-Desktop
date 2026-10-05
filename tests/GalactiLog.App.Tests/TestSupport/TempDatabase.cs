using GalactiLog.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// A fresh migrated SQLite file in the OS temp directory, deleted with its sidecars on
/// <see cref="Dispose"/>. The one temp-database type in App.Tests (F8, design-lessons rule 1):
/// three hand-rolled copies of this had grown, each with its own path, its own pooling decision
/// and its own teardown. Seeding stays with the caller: what the rows are is what differs between
/// them.
/// </summary>
/// <remarks>
/// <para>
/// Pooling is off, for the reason <c>TestDatabaseFactory</c> in Data.Tests states at length:
/// Microsoft.Data.Sqlite's pool is process-wide and keyed by connection string, so a pooled
/// connection keeps the file handle open past <c>Close</c> and <see cref="Dispose"/> could not
/// delete its own file without emptying it. The only call that reliably empties it,
/// <c>SqliteConnection.ClearAllPools</c>, empties every other class's pool as well and
/// deactivates live connections belonging to whatever is mid-test. With pooling off there is no
/// shared cache and no clearing step: every close releases its handle, so dispose just deletes.
/// </para>
/// <para>
/// Test-only plain file I/O, which <c>FileSafetyTest</c> does not scan (it scans <c>src/**</c>).
/// </para>
/// </remarks>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _path;

    /// <param name="prefix">Names the temp file, so a leftover is traceable to the suite that
    /// made it.</param>
    /// <param name="directory">Where the file goes. Defaults to the OS temp directory. Phase 10
    /// Task 1 passes an app data root, because the Diagnostics Paths group asserts that the
    /// database file sits under it exactly as <c>AppHost</c> places it in production.</param>
    public TempDatabase(string prefix = "galactilog-app-test", string? directory = null)
    {
        _path = Path.Combine(directory ?? Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.db");
        ConnectionString = new SqliteConnectionStringBuilder(DatabasePaths.BuildConnectionString(_path))
        {
            Pooling = false,
        }.ToString();

        using var context = Open(tracking: true);
        context.Database.Migrate();
    }

    public string ConnectionString { get; }

    /// <summary>Adds rows and saves them. The caller's seed runs against a tracking context.</summary>
    public void Seed(Action<GalactiLogContext> seed)
    {
        using var context = Open(tracking: true);
        seed(context);
        context.SaveChanges();
    }

    /// <summary>Reads through a fresh no-tracking context, so an assertion never sees a cached
    /// entity a writer has since changed.</summary>
    public T Read<T>(Func<GalactiLogContext, T> read)
    {
        using var context = Open(tracking: false);
        return read(context);
    }

    public void Dispose()
    {
        foreach (var candidate in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    private GalactiLogContext Open(bool tracking)
        => new(GalactiLogContextOptions.Create(ConnectionString, tracking));
}
