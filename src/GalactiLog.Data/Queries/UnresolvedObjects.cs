using Microsoft.Data.Sqlite;

namespace GalactiLog.Data.Queries;

/// <summary>
/// The two operations every unresolved-name caller performs: read the distinct names with their
/// LIGHT frame counts, and move one name's frames onto a target.
/// </summary>
/// <remarks>
/// Three callers had their own copy of each (<c>UnresolvedNamesQuery</c>,
/// <c>DuplicateDetector</c>, <c>UnresolvedRetry</c>), which is the second-occurrence rule the
/// review applied: the group-key expression in the assignment must match the listing query byte
/// for byte, and a fourth hand-rolled reader would eventually disagree with
/// <see cref="SqlFragments.UnresolvedObjectCounts"/> about which names are unresolved. It lives
/// beside <see cref="SqlFragments"/> because both halves are that fragment plus the one statement
/// built on <see cref="SqlFragments.GroupKeyExpression"/>; a caller supplies the connection, so
/// nothing here opens or owns one.
/// </remarks>
internal static class UnresolvedObjects
{
    /// <summary>
    /// <see cref="SqlFragments.UnresolvedObjectCounts"/>, most frames first then by name. A row
    /// whose name reads back empty is skipped: it cannot be looked up and it has no group of its
    /// own (spec 9.7 puts those frames under <c>obj:__uncategorized__</c>).
    /// </summary>
    public static List<(string Name, int FrameCount)> Read(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SqlFragments.UnresolvedObjectCounts + ";";

        var names = new List<(string, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // ReadText, not GetString: json_extract yields a SQLite number for an unquoted
            // numeric OBJECT card (Phase 5 fix F1).
            var name = SqlReaders.ReadText(reader, 0);
            if (!string.IsNullOrEmpty(name))
            {
                names.Add((name, SqlReaders.ReadInt(reader, 1)));
            }
        }

        return names;
    }

    /// <summary>
    /// Points every unassigned LIGHT frame carrying this <c>OBJECT</c> string at
    /// <paramref name="targetId"/>, and returns how many moved.
    /// </summary>
    /// <remarks>
    /// SQLite cannot alias the table of an UPDATE and <see cref="SqlFragments.GroupKeyExpression"/>
    /// is written against the alias <c>i</c>, so the subselect is what lets this reuse the one
    /// expression the listing and both detail queries already use. Comparing against the group key
    /// rather than against <c>json_extract</c> directly is what keeps an unquoted numeric OBJECT
    /// card matching (Phase 5 fix F1).
    /// </remarks>
    public static int AssignFrames(SqliteConnection connection, string name, Guid targetId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            UPDATE images SET resolved_target_id = @target
            WHERE id IN (
                SELECT i.id FROM images i
                WHERE i.resolved_target_id IS NULL
                  AND i.{SqlFragments.LightFrameOnly}
                  AND {SqlFragments.GroupKeyExpression} = @key
            );
            """;
        command.Parameters.Add(new SqliteParameter("@target", targetId));
        command.Parameters.Add(new SqliteParameter("@key", SqlFragments.UnresolvedKeyPrefix + name));
        return command.ExecuteNonQuery();
    }
}
