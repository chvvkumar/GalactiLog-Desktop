using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>One raw name found anywhere in the library and how many frames carry it, before any
/// alias folding (spec 12.7's Filters and Equipment discovered lists).</summary>
public sealed record DiscoveredNameRow(string Name, int FrameCount);

/// <summary>Which <c>images</c> column a discovered-names read counts. A fixed three-value enum,
/// never a caller-supplied string, so the column name in the generated SQL can never be
/// interpolated from user text (brief: "chosen from a fixed three-value enum, never interpolated
/// from a caller string").</summary>
public enum DiscoveredNameColumn
{
    Filters,
    Cameras,
    Telescopes,
}

/// <summary>
/// Spec 12.7's discovered-name lists: every distinct raw <c>filter_used</c>, <c>camera</c> or
/// <c>telescope</c> value in the library with its frame count, most frames first. Read-only.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="DashboardFacetsQuery"/> rather than an extension of it
/// (design-lessons rule 1 considered and rejected): that query is LIGHT-frame-only and folds raw
/// names to canonical through the alias map inside the SQL projection, because the dashboard
/// pills must never show a raw alias. This read is the opposite on both points. Questions.md Q25:
/// the web's <c>GET /settings/discovered/{section}</c> carries <strong>no</strong> LIGHT filter,
/// because the discovered list exists so the user can group every raw name that appears anywhere
/// in the library, including one that appears only on calibration frames; the count is then
/// "frames", not "light frames". And the alias fold has to happen in C#, never in SQL built from
/// user-supplied alias text (brief: "Alias folding happens in C#, never as SQL built from user
/// text"), so this query returns the raw names and leaves the folding and re-sorting to the
/// caller (the Filters and Equipment tab view-models), exactly as the web's endpoint folds in
/// Python after an ungrouped SQL read. A fourth reader of the same three columns would be the
/// alternative design-lessons rule 1 warns about; the two readers differ in every one of their
/// three behaviours (frame filter, alias folding, and what "not found" means), so they are kept
/// separate on purpose rather than merged into one parameterized query with two modes.
/// </para>
/// </remarks>
public sealed class DiscoveredNamesQuery(DatabaseConnectionString connectionString)
{
    /// <summary>Every distinct raw value of the column <paramref name="column"/> names, with its
    /// frame count, ordered by count descending. No LIGHT filter (questions.md Q25).</summary>
    public IReadOnlyList<DiscoveredNameRow> Read(DiscoveredNameColumn column)
    {
        var columnName = ColumnName(column);

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        using var command = connection.CreateCommand();
        // columnName comes only from the switch below, never from a caller string, so there is
        // no operand here that user text could reach: the WHERE clause binds nothing because it
        // has nothing to bind (compare SqlFragments.LightFrameOnly, the sibling query's own
        // constant-only fragment).
        command.CommandText =
            $"""
            SELECT {columnName}, count(id) FROM images
            WHERE {columnName} IS NOT NULL
            GROUP BY {columnName}
            ORDER BY count(id) DESC;
            """;

        var rows = new List<DiscoveredNameRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            var name = reader.GetString(0);
            var count = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            rows.Add(new DiscoveredNameRow(name, count));
        }

        return rows;
    }

    private static string ColumnName(DiscoveredNameColumn column) => column switch
    {
        DiscoveredNameColumn.Filters => "filter_used",
        DiscoveredNameColumn.Cameras => "camera",
        DiscoveredNameColumn.Telescopes => "telescope",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Unrecognized discovered-name column."),
    };
}
