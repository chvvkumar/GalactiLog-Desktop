using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Design;

namespace GalactiLog.Data;

// Design-time factory for `dotnet ef migrations add`/`list`. Not used at runtime; the real
// app never calls this type. Points at a throwaway file, since migrations are generated
// from the compiled model, not from the target file's content. Routed through
// DatabasePaths.BuildConnectionString like every other connection string in the solution
// (no second construction site), opened read-only: `dotnet ef` never needs to write here.
public sealed class GalactiLogContextFactory : IDesignTimeDbContextFactory<GalactiLogContext>
{
    public GalactiLogContext CreateDbContext(string[] args)
    {
        var dbPath = System.IO.Path.Combine(AppContext.BaseDirectory, "design-time.db");
        var connectionString = DatabasePaths.BuildConnectionString(dbPath, SqliteOpenMode.ReadOnly);
        var options = GalactiLogContextOptions.Create(connectionString, tracking: true);
        return new GalactiLogContext(options);
    }
}
