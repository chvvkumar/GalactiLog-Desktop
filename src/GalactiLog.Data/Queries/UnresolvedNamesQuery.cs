using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>One distinct unresolved <c>OBJECT</c> string and its LIGHT frame count (spec 9.7,
/// 12.7, 12.8).</summary>
/// <param name="Name">The raw <c>OBJECT</c> card value, as the frames carry it.</param>
/// <param name="GroupKey"><c>obj:&lt;name&gt;</c>, so a row can open the dashboard group it names
/// without the view rebuilding the prefix.</param>
/// <param name="FrameCount">LIGHT frames only, which is the same quantity a
/// <c>merge_candidates</c> row's <c>source_image_count</c> carries.</param>
/// <remarks>
/// Frames with an empty or missing <c>OBJECT</c> are deliberately absent from this list: the
/// grouping fragment's <c>obj &lt;&gt; ''</c> and <c>IS NOT NULL</c> guards drop them, and
/// <c>obj:__uncategorized__</c> is not a name that can be retried. Those frames still group on the
/// dashboard (spec 9.7), they simply have no name for the retry to look up.
/// </remarks>
public sealed record UnresolvedNameRow(string Name, string GroupKey, int FrameCount);

/// <summary>
/// Spec 9.7's "distinct unresolved names with their frame counts", read for spec 12.7's Targets
/// tab and spec 12.8's Diagnostics group. Read-only.
/// </summary>
/// <remarks>
/// The read is <c>UnresolvedObjects.Read</c> over <c>SqlFragments.UnresolvedObjectCounts</c>, the
/// constant Task 1 extracted, so this list, the dashboard's search dropdown, the dedup pass and
/// the retry cannot disagree about which names are unresolved. No second grouping and no second
/// reader is written here.
/// </remarks>
public sealed class UnresolvedNamesQuery(DatabaseConnectionString connectionString)
{
    /// <summary>Most frames first, ties by name, which is the order
    /// <c>SqlFragments.UnresolvedObjectCounts</c> already produces.</summary>
    public IReadOnlyList<UnresolvedNameRow> All()
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        // One reader for all three callers (UnresolvedObjects), so this list, the dedup pass
        // and the retry cannot disagree about which names are unresolved.
        var rows = UnresolvedObjects.Read(connection)
            .Select(row => new UnresolvedNameRow(
                row.Name, SqlFragments.UnresolvedKeyPrefix + row.Name, row.FrameCount))
            .ToList();

        return rows;
    }
}
