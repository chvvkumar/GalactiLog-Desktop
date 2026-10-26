using System.Globalization;
using GalactiLog.Core.Io;

namespace GalactiLog.Core.Wbpp;

/// <summary>The rule that decides where every copied folder lands in the staging folder
/// (design.md Part A, "The rule"). Pure string and Path work: no filesystem access.</summary>
public static class StagingLayout
{
    /// <summary>Dedupes the sources, finds the start folder and names each kept source's
    /// operation by its path from the start folder down: relative to the start folder's parent, or
    /// to the scan root when the root is the start folder (design.md Part A, "The rule").</summary>
    public static StagingPlan Plan(IReadOnlyList<CopySource> sources)
    {
        var kept = Dedupe(sources);
        if (kept.Count == 0)
        {
            return new StagingPlan(null, []);
        }

        var roots = new List<string>();
        foreach (var k in kept)
        {
            if (!roots.Any(r => Same(r, k.Source.ScanRoot)))
            {
                roots.Add(k.Source.ScanRoot);
            }
        }

        string? start = null;
        string[] entries;
        if (roots.Count == 1)
        {
            start = FindStart(roots[0], kept);
            var isRoot = Same(start, roots[0]);
            var baseFolder = isRoot ? roots[0] : Path.GetDirectoryName(start)!;
            entries = kept.Select(k => Relative(baseFolder, k.Source.SourcePath)).ToArray();
        }
        else
        {
            entries = kept.Select(k => Relative(k.Source.ScanRoot, k.Source.SourcePath)).ToArray();
            entries = ResolveCollisions(kept, entries);
        }

        var operations = kept
            .Select((k, i) => new CopyOperation(
                k.Source.Night, k.Source.SourcePath, entries[i], k.Files, k.Dirs.Count == 0 ? null : k.Dirs))
            .ToList();
        return new StagingPlan(start, operations);
    }

    private sealed record Kept(CopySource Source, List<string> Files, List<string> Dirs);

    private static bool Same(string a, string b) => PathConfinement.IsUnderOrEqual(a, b) && PathConfinement.IsUnderOrEqual(b, a);

    private static string Relative(string baseFolder, string path)
        => Path.TrimEndingDirectorySeparator(Path.GetRelativePath(baseFolder, path));

    // Same folder from several nights becomes one entry (first night wins). Excluded files and
    // directories both intersect: each night's lists are relative to that night, since another
    // night's frames are foreign to it, so the merged entry leaves out only what every night left
    // out. A folder nested under another source is dropped unless it sits under a directory that
    // source excludes.
    private static List<Kept> Dedupe(IReadOnlyList<CopySource> sources)
    {
        var groups = new List<Kept>();
        foreach (var s in sources)
        {
            var dirs = s.ExcludedRelativeDirectories ?? [];
            var g = groups.FirstOrDefault(x => Same(x.Source.SourcePath, s.SourcePath));
            if (g is null)
            {
                groups.Add(new Kept(s, [.. s.ExcludedRelativePaths.Distinct(StringComparer.OrdinalIgnoreCase)], [.. dirs.Distinct(StringComparer.OrdinalIgnoreCase)]));
                continue;
            }
            g.Files.RemoveAll(f => !s.ExcludedRelativePaths.Contains(f, StringComparer.OrdinalIgnoreCase));
            g.Dirs.RemoveAll(d => !dirs.Contains(d, StringComparer.OrdinalIgnoreCase));
        }

        return groups.Where(inner => !groups.Any(outer =>
            !ReferenceEquals(outer, inner)
            && PathConfinement.IsUnderOrEqual(outer.Source.SourcePath, inner.Source.SourcePath)
            && !outer.Dirs.Any(d => PathConfinement.IsUnderOrEqual(Path.Combine(outer.Source.SourcePath, d), inner.Source.SourcePath))))
            .ToList();
    }

    // The deepest folder holding every kept source, climbing from the first one. Falls back to the
    // scan root when the climb reaches the root or lands on a folder that is itself copied.
    private static string FindStart(string root, List<Kept> kept)
    {
        var candidate = Path.TrimEndingDirectorySeparator(kept[0].Source.SourcePath);
        while (true)
        {
            if (Same(candidate, root))
            {
                return root;
            }
            if (kept.All(k => PathConfinement.IsUnderOrEqual(candidate, k.Source.SourcePath)))
            {
                return kept.Any(k => Same(candidate, k.Source.SourcePath)) ? root : candidate;
            }
            var parent = Path.GetDirectoryName(candidate);
            if (parent is null)
            {
                return root;
            }
            candidate = parent;
        }
    }

    // Colliding entries take their night as a prefix; any that still collide get _2, _3 after the
    // first segment.
    private static string[] ResolveCollisions(List<Kept> kept, string[] entries)
    {
        var counts = entries.GroupBy(e => e, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var result = new string[entries.Length];
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = counts[entries[i]] > 1
                ? kept[i].Source.Night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "_" + entries[i]
                : entries[i];
            var final = entry;
            for (var n = 2; !taken.Add(final); n++)
            {
                var cut = entry.IndexOf(Path.DirectorySeparatorChar);
                final = cut < 0 ? entry + "_" + n : entry.Insert(cut, "_" + n);
            }
            result[i] = final;
        }
        return result;
    }
}
