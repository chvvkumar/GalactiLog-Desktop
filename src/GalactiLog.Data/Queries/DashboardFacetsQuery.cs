using System.Globalization;
using GalactiLog.Core.Aliases;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>One Filters pill: a canonical filter name, its configured colour, and how many LIGHT
/// frames in the library carry it.</summary>
public sealed record FilterFacet(string CanonicalName, string Color, int FrameCount);

/// <summary>What the dashboard filter panel can actually offer, given what the library holds
/// (spec 12.2). Task 6 unions these with the canonical names configured in settings.</summary>
public sealed record DashboardFacets(
    IReadOnlyList<FilterFacet> Filters,
    IReadOnlyList<string> Cameras,
    IReadOnlyList<string> Telescopes);

/// <summary>
/// The small companion to <see cref="TargetListingQuery"/>: the distinct filters, cameras and
/// telescopes present on LIGHT frames, folded to canonical names through the alias map.
/// </summary>
public sealed class DashboardFacetsQuery(DatabaseConnectionString connectionString, AliasMapCache aliases)
{
    public DashboardFacets Load()
    {
        var map = aliases.Current;

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT filter_used, count(*) FROM images WHERE {SqlFragments.LightFrameOnly} GROUP BY filter_used;
            SELECT DISTINCT camera FROM images WHERE {SqlFragments.LightFrameOnly};
            SELECT DISTINCT telescope FROM images WHERE {SqlFragments.LightFrameOnly};
            """;

        var filters = new Dictionary<string, (string Name, int Count)>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // Q14's "Unknown" bucket is a palette badge, not a pill: a user cannot filter on it,
            // so a null or empty filter_used contributes no facet.
            var canonical = map.CanonicalFilter(reader.IsDBNull(0) ? null : reader.GetString(0));
            if (canonical is null)
            {
                continue;
            }

            var count = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
            filters[canonical] = filters.TryGetValue(canonical, out var existing)
                ? (existing.Name, existing.Count + count)
                : (canonical, count);
        }

        reader.NextResult();
        var cameras = ReadCanonicalNames(reader, map.CanonicalCamera);

        reader.NextResult();
        var telescopes = ReadCanonicalNames(reader, map.CanonicalTelescope);

        return new DashboardFacets(
            [.. filters.Values
                .OrderBy(facet => facet.Name, StringComparer.OrdinalIgnoreCase)
                .Select(facet => new FilterFacet(facet.Name, map.FilterColor(facet.Name), facet.Count))],
            cameras,
            telescopes);
    }

    private static IReadOnlyList<string> ReadCanonicalNames(SqliteDataReader reader, Func<string?, string?> canonicalize)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var canonical = canonicalize(reader.IsDBNull(0) ? null : reader.GetString(0));
            if (canonical is not null)
            {
                names.Add(canonical);
            }
        }

        return [.. names];
    }
}
