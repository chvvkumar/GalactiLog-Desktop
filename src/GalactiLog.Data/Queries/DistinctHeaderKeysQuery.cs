using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>
/// The distinct top-level FITS header keys present in the library, for the dashboard's header key
/// combo box (spec 12.2, 12.3).
/// </summary>
public sealed class DistinctHeaderKeysQuery(DatabaseConnectionString connectionString)
{
    /// <summary>Every distinct top-level key present in any LIGHT frame's raw_headers, sorted,
    /// filtered to keys that pass the 12.3 gate (a key the builder would drop is not offered).
    /// <paramref name="maxKeys"/> bounds how many keys <see cref="Load"/> returns; the gate is
    /// applied in SQL before <c>DISTINCT</c>/<c>ORDER BY</c>/<c>LIMIT</c> run, so a corrupt
    /// library's invalid keys cannot consume cap slots that a valid key would otherwise get.
    /// </summary>
    public IReadOnlyList<string> Load(int maxKeys = 500)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        using var command = connection.CreateCommand();
        // ponytail: a full json_each scan over every LIGHT frame, acceptable at single-imaging-PC
        // scale and called once when the filter panel opens, not per keystroke (spec 12.2's
        // group-key query already scans the same table). Upgrade path if it ever matters: cache
        // for the life of the window and refresh after a scan.
        //
        // json_valid/json_type guard json_each against a scalar or malformed raw_headers value:
        // both predicates reference only i, so SQLite filters images before invoking json_each
        // on any row's raw_headers, rather than json_each throwing mid-scan on a bad value.
        //
        // The GLOB/length pair mirrors HeaderQueryBuilder.IsValidKey's ^[A-Za-z0-9_-]{1,20}$ gate
        // in SQL (GLOB has no repetition quantifier, so the character class and the length are two
        // separate predicates) so an invalid key is excluded before DISTINCT/ORDER/LIMIT, not
        // after -- otherwise a library with more invalid raw keys than maxKeys could crowd every
        // valid key out of the capped result. IsValidKey is still applied to what comes back, as a
        // second gate against the two rules ever drifting apart.
        command.CommandText =
            $"""
            SELECT DISTINCT k.key
            FROM images i, json_each(i.raw_headers) k
            WHERE i.{SqlFragments.LightFrameOnly}
              AND i.raw_headers IS NOT NULL
              AND json_valid(i.raw_headers)
              AND json_type(i.raw_headers) = 'object'
              AND length(k.key) BETWEEN 1 AND 20
              AND k.key NOT GLOB '*[^A-Za-z0-9_-]*'
            ORDER BY k.key
            LIMIT @limit;
            """;
        command.Parameters.Add(new SqliteParameter("@limit", maxKeys));

        var keys = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (HeaderQueryBuilder.IsValidKey(key))
            {
                keys.Add(key);
            }
        }

        // FIXER LIST F15. SQLite's ORDER BY on a BINARY-collated column is byte order, so every
        // upper-case key sorts before every lower-case one and the panel offering "AIRMASS,
        // OBJECT, airmass" reads as two unrelated lists. The raw header panel orders the same
        // keys OrdinalIgnoreCase then Ordinal (FrameHeadersQuery.ParseRawHeaders), and the two
        // surfaces show the same keys, so they order them the same way. Sorted here rather than
        // in SQL because ORDER BY must stay the collation the LIMIT caps on: the cap has to take
        // a deterministic set of keys, and NOCASE would make which keys survive it depend on
        // case folding.
        return [.. keys
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key, StringComparer.Ordinal)];
    }
}
