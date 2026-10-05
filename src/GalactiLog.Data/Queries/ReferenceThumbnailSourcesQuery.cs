using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>One target needing a reference thumbnail, with the frames to render it from.</summary>
/// <param name="FramePaths">The target's most recent LIGHT frames that have a capture date
/// (spec 11.4), newest first, at most
/// <see cref="ReferenceThumbnailSourcesQuery.MaxFramesPerTarget"/> of them. Never empty: a target
/// with no such frame is not returned at all. More than one because whether a frame's pixels
/// decode is not expressible in SQL (questions.md Q13), so the pass falls back frame by frame at
/// render time, bounded (questions.md Q14).</param>
public sealed record ReferenceThumbnailSource(Guid TargetId, string PrimaryName, IReadOnlyList<string> FramePaths);

/// <summary>
/// Spec 11.4's source selection for the reference thumbnail pass. Read-only, every operand bound.
/// </summary>
/// <remarks>
/// <para>
/// Unlike every other query in this namespace it takes no group key: the pass is over targets, and
/// an <c>obj:</c> group has no <c>targets</c> row to store a path on. That is the one documented
/// exception in Phase 8 and it is stated here so a reviewer does not read it as an omission.
/// </para>
/// <para>
/// The web application's <c>generate_reference_thumbnails</c> also requires <c>ra</c> and
/// <c>dec</c> to be non-null, because it needs coordinates to query NASA SkyView. That condition
/// is deliberately absent (questions.md Q12): this port renders the target's own frames, and
/// keeping it would silently skip every unresolved target, which is the set most in need of a
/// visual identity.
/// </para>
/// </remarks>
public sealed class ReferenceThumbnailSourcesQuery(DatabaseConnectionString connectionString)
{
    /// <summary>Questions.md Q14's bound: at most three frames per target, newest first. The pass
    /// tries them in order and stops at the first that renders, so one corrupt file does not cost
    /// a target its thumbnail and a library of unreadable frames still costs at most three decode
    /// attempts per target rather than its whole history.</summary>
    public const int MaxFramesPerTarget = 3;

    /// <summary>
    /// The statement, exposed so the operand-binding test can read it rather than assert by
    /// review. Every operand is a parameter; nothing is interpolated but
    /// <see cref="SqlFragments.LightFrameOnly"/>, which is a constant of this assembly.
    /// </summary>
    /// <remarks>
    /// The target conditions live INSIDE the windowed subquery, not beside the outer join: the
    /// window is then computed over the eligible targets' frames only, rather than over every LIGHT
    /// frame in the library and discarded afterwards. On an ordinary scan that is the handful of
    /// targets a new session touched instead of all of them. One copy of each predicate, so the
    /// gate and the window cannot drift apart.
    /// <para>
    /// ponytail: still a partition sort over every frame of every eligible target, and a forced
    /// regeneration makes that the whole <c>images</c> table. Ceiling: one sort of a few hundred
    /// thousand rows, once per scan, against three image decodes per target. Upgrade path if it
    /// ever matters: a correlated <c>LIMIT 3</c> per target driven off the
    /// <c>(resolved_target_id, capture_date)</c> index.
    /// </para>
    /// </remarks>
    public const string Sql =
        $"""
        SELECT t.id, t.primary_name, f.file_path
        FROM (
            SELECT i.resolved_target_id AS target_id,
                   i.file_path AS file_path,
                   row_number() OVER (
                       PARTITION BY i.resolved_target_id
                       ORDER BY i.capture_date DESC, i.id DESC) AS attempt
            FROM images i
            JOIN targets needing ON needing.id = i.resolved_target_id
            WHERE i.{SqlFragments.LightFrameOnly}
              AND i.capture_date IS NOT NULL
              AND needing.merged_into_id IS NULL
              AND (@force = 1 OR needing.reference_thumbnail_path IS NULL)
        ) f
        JOIN targets t ON t.id = f.target_id
        WHERE f.attempt <= @limit
        ORDER BY t.primary_name, t.id, f.attempt;
        """;

    /// <param name="force">False is spec 11.4's work queue: targets with no
    /// <c>reference_thumbnail_path</c>. True is spec 12.7's "regenerate all", which Phase 9's
    /// Maintenance tab calls and nothing in Phase 8 does.</param>
    public IReadOnlyList<ReferenceThumbnailSource> Get(bool force)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Parameters.Add(new SqliteParameter("@limit", MaxFramesPerTarget));
        command.Parameters.Add(new SqliteParameter("@force", force ? 1 : 0));

        // The ORDER BY groups a target's rows together (by id, not by name: two targets that
        // share a primary name would otherwise interleave) and puts them newest first, so the
        // rows fold into one record per target in a single pass with no dictionary.
        var sources = new List<ReferenceThumbnailSource>();
        var frames = new List<string>();
        var currentId = Guid.Empty;
        var currentName = "";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var targetId = reader.GetGuid(0);
            if (frames.Count > 0 && targetId != currentId)
            {
                sources.Add(new ReferenceThumbnailSource(currentId, currentName, [.. frames]));
                frames.Clear();
            }

            currentId = targetId;
            currentName = reader.GetString(1);
            frames.Add(reader.GetString(2));
        }

        if (frames.Count > 0)
        {
            sources.Add(new ReferenceThumbnailSource(currentId, currentName, [.. frames]));
        }

        return sources;
    }
}
