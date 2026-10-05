using GalactiLog.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests;

public class PragmaTests
{
    [Fact]
    public void FreshConnection_HasWalJournalMode()
    {
        var value = QueryPragma("PRAGMA journal_mode;");

        Assert.Equal("wal", value, ignoreCase: true);
    }

    [Fact]
    public void FreshConnection_HasBusyTimeout5000()
    {
        var value = QueryPragma("PRAGMA busy_timeout;");

        Assert.Equal("5000", value);
    }

    [Fact]
    public void FreshConnection_HasForeignKeysOn()
    {
        var value = QueryPragma("PRAGMA foreign_keys;");

        Assert.Equal("1", value);
    }

    // Opens the pragma-check connection through GalactiLogContext.Database.OpenConnection()
    // rather than a bare SqliteConnection.Open(), so the open goes through EF Core's
    // relational connection pipeline and PragmaConnectionInterceptor actually runs.
    // GetDbConnection() followed by a raw connection.Open() bypasses that pipeline entirely.
    private static string QueryPragma(string pragmaSql)
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(db.ConnectionString, tracking: true));

        context.Database.OpenConnection();
        var connection = context.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = pragmaSql;
        var result = command.ExecuteScalar();
        return result?.ToString() ?? "";
    }
}
