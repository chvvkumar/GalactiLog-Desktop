using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GalactiLog.Data;

// Applies the five startup pragmas from design-spec 5.1 on every connection open, in one
// batched command. Microsoft.Data.Sqlite executes semicolon-separated statements in one
// ExecuteNonQuery call sequentially.
public sealed class PragmaConnectionInterceptor : DbConnectionInterceptor
{
    private const string PragmaSql =
        "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; " +
        "PRAGMA foreign_keys=ON; PRAGMA journal_size_limit=67108864;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Apply(connection);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = PragmaSql;
        command.ExecuteNonQuery();
    }
}
