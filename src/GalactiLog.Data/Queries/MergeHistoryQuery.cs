using System.Text.Json;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>One row of spec 12.9's merge history, one per <c>merge_manifests</c> row.</summary>
/// <param name="ManifestId">What the undo names for an unresolved-name merge, which has no loser
/// target id.</param>
/// <param name="WinnerId">The surviving target the merge fed.</param>
/// <param name="WinnerName">That target's <c>primary_name</c>.</param>
/// <param name="LoserId">Null for an unresolved-name merge (spec 5.11 allows a null
/// <c>loser_id</c>).</param>
/// <param name="LoserName">The loser target's <c>primary_name</c> when <paramref name="LoserId"/>
/// is set, otherwise the payload's <c>source_name</c>: the unresolved <c>OBJECT</c> string the
/// merge absorbed. Null when neither is available, in which case the row reads "an unresolved
/// name".</param>
/// <param name="MergedAt">The merge time. <c>merge_manifests</c> has no <c>merged_at</c> column
/// and none is added: <c>created_at</c> is written inside the merge transaction, so it is the
/// merge time spec 12.9 asks for.</param>
/// <param name="MovedFrameCount">The length of the manifest payload's <c>moved_image_ids</c>
/// array: what the merge actually moved, not what the loser holds now. <c>-1</c> when the payload
/// could not be read, which the view renders as "unknown frames".</param>
public sealed record MergeHistoryRow(
    Guid ManifestId,
    Guid WinnerId,
    string WinnerName,
    Guid? LoserId,
    string? LoserName,
    DateTime MergedAt,
    int MovedFrameCount);

/// <summary>
/// Spec 12.9's merge history read: one row per <c>merge_manifests</c> row, newest first. The
/// same list renders in Settings and on Target detail, so there is one query with an optional
/// winner predicate rather than two.
/// </summary>
/// <remarks>
/// <para>
/// The history lists manifests, not accepted <c>merge_candidates</c> rows as the web source does
/// (questions.md Q8): a manifest is what <see cref="MergeRepository.Unmerge"/> consumes, and
/// every merge in this port writes one, both shapes included.
/// </para>
/// <para>
/// The payload is read through <see cref="MergeManifestPayload"/>, Task 2's type and the single
/// reader shape for that column. A payload that does not deserialize yields
/// <c>MovedFrameCount = -1</c> and a null loser name rather than failing the list, the same
/// defensive rule <c>SqlReaders.ParseAliases</c> follows: one unreadable row must not hide every
/// other merge from the undo it needs.
/// </para>
/// </remarks>
public sealed class MergeHistoryQuery(DatabaseConnectionString connectionString)
{
    /// <summary>Every manifest, newest first. The Settings list.</summary>
    public IReadOnlyList<MergeHistoryRow> All() => Read(null);

    /// <summary>The manifests whose winner is this target. Target detail's list: spec 12.9 puts
    /// the history "on Target detail", and the only history a target's page can mean is the
    /// merges that fed it.</summary>
    public IReadOnlyList<MergeHistoryRow> ForWinner(Guid winnerId) => Read(winnerId);

    // One statement, read-only, every operand bound. The winner join is inner because a manifest
    // whose winner row is gone has been cascade-deleted with it (spec 5.11); the loser join is
    // left because an unresolved-name merge has no loser row by design.
    private List<MergeHistoryRow> Read(Guid? winnerId)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        var parameters = new SqlParameters();
        var predicate = winnerId is { } id ? $"WHERE m.winner_id = {parameters.Add(id)}" : "";

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT m.id, m.winner_id, w.primary_name, m.loser_id, l.primary_name, m.created_at,
                   m.payload
            FROM merge_manifests m
            JOIN targets w ON w.id = m.winner_id
            LEFT JOIN targets l ON l.id = m.loser_id
            {predicate}
            ORDER BY m.created_at DESC, m.id;
            """;
        parameters.ApplyTo(command);

        var rows = new List<MergeHistoryRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var loserId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3);
            var payload = ParsePayload(SqlReaders.ReadText(reader, 6));

            rows.Add(new MergeHistoryRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                loserId,
                // Ruling Q8 and the Task 2 review ruling: the label is the loser target's name
                // for a target merge and the manifest's own source_name for the unresolved-name
                // shape. No merge_candidates lookup: the candidate row can be dismissed, deleted
                // or never have existed, and source_name is written by the merge itself.
                loserId is null ? payload?.SourceName : SqlReaders.ReadText(reader, 4),
                reader.GetDateTime(5),
                payload?.MovedImageIds is { } moved ? moved.Count : -1));
        }

        return rows;
    }

    private static MergeManifestPayload? ParsePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MergeManifestPayload>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
