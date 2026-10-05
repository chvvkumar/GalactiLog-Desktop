using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>One row of spec 12.9's candidate list.</summary>
/// <param name="Id">The <c>merge_candidates</c> row id, which the dismiss and accept actions
/// name.</param>
/// <param name="SourceName">Spec 5.10's <c>source_name</c>: the unresolved <c>OBJECT</c> string,
/// or another target's <c>primary_name</c> for a Pass 2 duplicate.</param>
/// <param name="SourceImageCount">Spec 12.9's frame count for that source name.</param>
/// <param name="SuggestedTargetId">The suggested winner's id, or null for an orphan candidate.
/// Returned even when <paramref name="SuggestedTargetName"/> is null, so the accept action can
/// report "the suggested target is gone" rather than silently doing nothing.</param>
/// <param name="SuggestedTargetName">The suggested winner's <c>primary_name</c>, or null for an
/// orphan candidate, which has no suggestion at all.</param>
/// <param name="SimilarityScore">Spec 5.10's 0 to 1 score.</param>
/// <param name="Method">Spec 5.10's method verbatim: <c>simbad</c>, <c>trigram</c>,
/// <c>orphan</c>, or <c>duplicate</c> from Pass 2 (questions.md Q4). Rendered as written; the
/// view does not map it to a friendlier label, because the reason text is already the human
/// sentence.</param>
/// <param name="ReasonText">Spec 5.10's human-readable justification.</param>
/// <param name="CreatedAt">When the duplicate detection pass wrote the row.</param>
public sealed record MergeCandidateRow(
    Guid Id,
    string SourceName,
    int SourceImageCount,
    Guid? SuggestedTargetId,
    string? SuggestedTargetName,
    double SimilarityScore,
    string Method,
    string? ReasonText,
    DateTime CreatedAt);

/// <summary>
/// Spec 12.9's candidate list read. Pending rows only: an accepted or dismissed row is history,
/// and spec 12.9 says "one row per pending <c>merge_candidates</c> row".
/// </summary>
/// <remarks>
/// There is no group key here, and there is deliberately no group-key parameter: a candidate is
/// not a dashboard group. The group-key rule applies to queries that name a dashboard row; this
/// one names a <c>merge_candidates</c> row by id.
/// </remarks>
public sealed class MergeCandidateQuery(DatabaseConnectionString connectionString)
{
    /// <summary>
    /// Every pending candidate, newest first, ties broken by <c>source_name</c> ordinal so the
    /// list does not reshuffle between identical reads.
    /// </summary>
    /// <remarks>
    /// The join's <c>merged_into_id IS NULL</c> predicate matters: a candidate can outlive its
    /// suggested target being merged away, and a row that names a merged-away winner must show no
    /// suggestion rather than a name the dashboard no longer offers.
    /// </remarks>
    public IReadOnlyList<MergeCandidateRow> Pending()
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        using var command = connection.CreateCommand();

        // No operand here comes from the caller: the status literal is a literal, and there is
        // nothing else to bind.
        command.CommandText =
            """
            SELECT c.id, c.source_name, c.source_image_count, c.suggested_target_id, t.primary_name,
                   c.similarity_score, c.method, c.reason_text, c.created_at
            FROM merge_candidates c
            LEFT JOIN targets t ON t.id = c.suggested_target_id AND t.merged_into_id IS NULL
            WHERE c.status = 'pending'
            ORDER BY c.created_at DESC, c.source_name;
            """;

        var rows = new List<MergeCandidateRow>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new MergeCandidateRow(
                reader.GetGuid(0),
                reader.GetString(1),
                SqlReaders.ReadInt(reader, 2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                SqlReaders.ReadText(reader, 4),
                SqlReaders.ReadDouble(reader, 5),
                reader.GetString(6),
                SqlReaders.ReadText(reader, 7),
                reader.GetDateTime(8)));
        }

        return rows;
    }
}
