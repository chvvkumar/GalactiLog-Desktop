using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>One rename, read from <c>activity_events</c> (spec 5.12; questions.md Q11).</summary>
/// <param name="Timestamp">When the rename was written, in UTC, as
/// <c>TargetWriteRepository.Rename</c> stamped it.</param>
/// <param name="TargetId">Null when the target has since been deleted: the FK is ON DELETE SET
/// NULL (spec 5.12), so the row survives as a record of the rename.</param>
/// <param name="PreviousName">The <c>primary_name</c> the target carried before.</param>
/// <param name="NewName">The <c>primary_name</c> the user chose.</param>
/// <param name="CurrentName">The target's <c>primary_name</c> now, or null when the target is
/// gone. It differs from <paramref name="NewName"/> when the target was renamed again later.
/// </param>
public sealed record RenameHistoryRow(
    DateTime Timestamp, Guid? TargetId, string PreviousName, string NewName, string? CurrentName);

/// <summary>
/// Spec 12.7's rename history. There is no <c>rename_history</c> table in spec 5 and the web
/// application has no rename history at all, so this reads the <c>target_renamed</c>
/// <c>user_action</c> events <c>TargetWriteRepository.Rename</c> writes (questions.md Q11).
/// Read-only.
/// </summary>
/// <remarks>
/// The list is therefore bounded by <c>general.activity_retention_days</c> (spec 5.12, 90 days by
/// default): the activity prune eventually removes old renames and they leave this list with
/// them. That is accepted rather than worked around, because the alternative is a second durable
/// record of the same action.
/// </remarks>
public sealed class RenameHistoryQuery(DatabaseConnectionString connectionString)
{
    /// <param name="limit">Newest first. Default 50, matching the Diagnostics page's error list,
    /// so a long-lived library does not render an unbounded list.</param>
    public IReadOnlyList<RenameHistoryRow> Recent(int limit = 50)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        var parameters = new SqlParameters();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT e.timestamp, e.target_id, e.details, t.primary_name
            FROM activity_events e
            LEFT JOIN targets t ON t.id = e.target_id
            WHERE e.event_type = {parameters.Add(RenameEventType)}
            ORDER BY e.timestamp DESC, e.id DESC
            LIMIT {parameters.Add(limit)};
            """;
        parameters.ApplyTo(command);

        var rows = new List<RenameHistoryRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // A row whose details cannot be read is skipped rather than thrown on: one
            // hand-edited document must not hide every other rename, the same defensive rule
            // SqlReaders.ParseAliases and MergeHistoryQuery follow.
            var details = ParseDetails(SqlReaders.ReadText(reader, 2));
            if (details is not { PreviousName: { } previous, NewName: { } current })
            {
                continue;
            }

            rows.Add(new RenameHistoryRow(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                previous,
                current,
                SqlReaders.ReadText(reader, 3)));
        }

        return rows;
    }

    /// <summary>The <c>event_type</c> <c>TargetWriteRepository.Rename</c> writes. One constant, so
    /// the writer and this reader cannot drift.</summary>
    public const string RenameEventType = "target_renamed";

    private static RenameDetails? ParseDetails(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RenameDetails>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record RenameDetails(
        [property: JsonPropertyName("previous_name")] string? PreviousName,
        [property: JsonPropertyName("new_name")] string? NewName);
}
