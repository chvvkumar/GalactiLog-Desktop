namespace GalactiLog.Core.Aliases;

/// <summary>One suggested grouping of raw names that likely name the same filter, camera or
/// telescope (spec 12.7). <see cref="Names"/> is sorted ordinally, matching
/// <c>settings.py::_group_by_similarity</c>'s <c>sorted(members)</c>.</summary>
public sealed record SuggestionGroup(
    IReadOnlyList<string> Names,
    IReadOnlyDictionary<string, int> Counts);

/// <summary>
/// Port of the web's <c>backend/app/api/settings.py</c> similarity grouping, verbatim: three
/// string rules with no edit distance, and a union-find over every pair. A pure Core type,
/// because it is string logic with no database and no UI (collision map: "the alias suggestion
/// grouper (SuggestionGrouper), owner Task 7, reused by Task 7 twice, filters and equipment").
/// </summary>
public static class SuggestionGrouper
{
    /// <summary>
    /// Port of <c>settings.py::_are_similar</c>. Deliberately no edit distance: an edit-distance
    /// rule would group "ASI533MC" with "ASI533MM" and "Askar40" with "Askar140", which is the
    /// exact false-positive pair the web's own comment names.
    /// </summary>
    /// <remarks>
    /// Case folding is <see cref="string.ToLowerInvariant"/>, matching Python's <c>.lower()</c>
    /// on the ASCII equipment and filter names this sees; a culture-aware comparison is
    /// deliberately not used here, because the source does none either.
    /// </remarks>
    public static bool AreSimilar(string a, string b)
    {
        var la = a.ToLowerInvariant();
        var lb = b.ToLowerInvariant();

        // 1. Case-insensitive exact match.
        if (string.Equals(la, lb, StringComparison.Ordinal))
        {
            return true;
        }

        // 2. Normalized match: strip exactly `_`, `-` and space, and nothing else.
        var na = Normalize(a);
        var nb = Normalize(b);
        if (string.Equals(na, nb, StringComparison.Ordinal))
        {
            return true;
        }

        // 3. Containment, only when both strings are at least 4 characters (the guard that
        // keeps "Ha" out of "Ha3").
        if (la.Length >= 4 && lb.Length >= 4 && (la.Contains(lb, StringComparison.Ordinal) || lb.Contains(la, StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    // The web's `_normalize_for_comparison`: lowercase, then strip `_`, `-` and space. Nothing
    // else is stripped.
    private static string Normalize(string name)
        => name.ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");

    /// <summary>
    /// Port of <c>settings.py::_group_by_similarity</c>: union-find over every pair of names,
    /// path-halving <c>find</c>, <c>union</c> setting <c>parent[py] = px</c>, matching the
    /// source exactly. Returns only groups of two or more members; a singleton is not a
    /// suggestion.
    /// </summary>
    /// <remarks>
    /// The resulting groups are order-dependent only through which member becomes the union-find
    /// root, and each group's <see cref="SuggestionGroup.Names"/> is sorted regardless, so the
    /// port's output is deterministic whatever order <paramref name="counts"/> enumerates in.
    /// </remarks>
    public static IReadOnlyList<SuggestionGroup> Group(IReadOnlyDictionary<string, int> counts)
    {
        var names = counts.Keys.ToList();
        var parent = new Dictionary<string, string>(names.Count, StringComparer.Ordinal);
        foreach (var name in names)
        {
            parent[name] = name;
        }

        string Find(string x)
        {
            while (!string.Equals(parent[x], x, StringComparison.Ordinal))
            {
                // Path halving: point x at its grandparent, exactly as the Python `while` loop
                // does, before moving on.
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(string x, string y)
        {
            var px = Find(x);
            var py = Find(y);
            if (!string.Equals(px, py, StringComparison.Ordinal))
            {
                parent[py] = px;
            }
        }

        for (var i = 0; i < names.Count; i++)
        {
            for (var j = i + 1; j < names.Count; j++)
            {
                if (!string.Equals(Find(names[i]), Find(names[j]), StringComparison.Ordinal)
                    && AreSimilar(names[i], names[j]))
                {
                    Union(names[i], names[j]);
                }
            }
        }

        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var root = Find(name);
            if (!groups.TryGetValue(root, out var members))
            {
                members = [];
                groups[root] = members;
            }

            members.Add(name);
        }

        var result = new List<SuggestionGroup>();
        foreach (var members in groups.Values)
        {
            if (members.Count < 2)
            {
                continue;
            }

            var sorted = members.OrderBy(name => name, StringComparer.Ordinal).ToList();
            result.Add(new SuggestionGroup(sorted, sorted.ToDictionary(name => name, name => counts[name], StringComparer.Ordinal)));
        }

        return result;
    }

    /// <summary>
    /// Port of <c>_group_already_merged</c>: true when every member of the group already
    /// appears, by exact string match, as a canonical name or as an alias in
    /// <paramref name="knownNames"/>.
    /// </summary>
    public static bool AlreadyMerged(SuggestionGroup group, IReadOnlySet<string> knownNames)
        => group.Names.All(knownNames.Contains);

    /// <summary>
    /// Port of <c>_group_is_dismissed</c>: true when the group's sorted name list is present in
    /// <paramref name="dismissed"/>. <see cref="SuggestionGroup.Names"/> is already sorted, so
    /// this compares it directly against each stored entry, which
    /// <c>SettingsStore.SaveDismissedSuggestions</c>'s caller is responsible for storing sorted
    /// too (questions.md Q: the web's <c>PUT /settings/dismissed-suggestions</c> sorts each
    /// inner list before storing it).
    /// </summary>
    public static bool IsDismissed(SuggestionGroup group, IReadOnlyList<IReadOnlyList<string>> dismissed)
    {
        foreach (var entry in dismissed)
        {
            if (entry.Count == group.Names.Count && entry.SequenceEqual(group.Names, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
