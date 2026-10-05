namespace GalactiLog.Core.Aliases;

/// <summary>The one display order for filter names (polish ruling 5): L, R, G, B, SII, Ha, OIII,
/// then every other filter alphabetically. A name folds through <see cref="FilterCategory.Of(string?, IEnumerable{string}?)"/>,
/// so a nonstandard canonical takes its first folding alias's rank (polish wave 2 ruling 1).</summary>
public static class FilterOrder
{
    /// <summary>0 to 6 for the seven standard categories, 100 for everything else. Without
    /// <paramref name="map"/> only the name itself is folded.</summary>
    public static int Rank(string? name, AliasMap? map = null)
        => FilterCategory.Of(name, name is null || map is null ? null : map.ExpandFilter(name)) switch
        {
            "L" => 0,
            "R" => 1,
            "G" => 2,
            "B" => 3,
            "SII" => 4,
            "Ha" => 5,
            "OIII" => 6,
            _ => 100,
        };

    /// <summary>By <see cref="Rank"/>, then by name, ordinal and case insensitive. Built at the
    /// sort site from a map resolved once there, never on a construction path:
    /// <c>AliasMapCache.Current</c> can fall through to a settings read.</summary>
    public static IComparer<string?> Comparer(AliasMap? map) => Comparer<string?>.Create((left, right) =>
    {
        var byRank = Rank(left, map).CompareTo(Rank(right, map));
        return byRank != 0 ? byRank : StringComparer.OrdinalIgnoreCase.Compare(left, right);
    });
}
