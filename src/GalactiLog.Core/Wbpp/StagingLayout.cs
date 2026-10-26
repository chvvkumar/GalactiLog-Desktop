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

    // One operation per kept folder (first night wins). A folder nested under another source is
    // dropped into that source's operation unless every source holding it excludes it as a
    // directory. Each source's excludes are relative to that source, and another night's frames
    // are foreign to it, so the operation leaves a file or directory out only when every member
    // source that covers it (whose folder holds it) excludes it; a path only one source covers
    // keeps that source's verdict. A source inside a directory the operation leaves out is not a
    // member: it stays its own operation.
    private static List<Kept> Dedupe(IReadOnlyList<CopySource> sources)
    {
        var folders = new List<CopySource>();
        foreach (var s in sources)
        {
            if (!folders.Any(f => Same(f.SourcePath, s.SourcePath)))
            {
                folders.Add(s);
            }
        }

        var kept = folders.Where(f => !sources.Any(s =>
            !Same(s.SourcePath, f.SourcePath)
            && PathConfinement.IsUnderOrEqual(s.SourcePath, f.SourcePath)
            && !ExcludesDirectory(s, f.SourcePath))).ToList();

        // A source joins the innermost kept folder that holds it.
        string KeptFolderOf(CopySource s) => kept
            .Where(k => PathConfinement.IsUnderOrEqual(k.SourcePath, s.SourcePath))
            .MaxBy(k => Path.TrimEndingDirectorySeparator(k.SourcePath).Length)!.SourcePath;

        return kept.Select(k =>
        {
            var members = sources.Where(s => Same(KeptFolderOf(s), k.SourcePath))
                .Select(s => (Source: s, Files: s.ExcludedRelativePaths.ToHashSet(StringComparer.OrdinalIgnoreCase)))
                .ToList();
            bool Excludes((CopySource Source, HashSet<string> Files) m, string path, bool isFile)
                => (isFile && m.Files.Contains(Path.GetRelativePath(m.Source.SourcePath, path))) || ExcludesDirectory(m.Source, path);
            List<string> Survivors(Func<CopySource, IEnumerable<string>> list, bool isFile) => members
                .SelectMany(m => list(m.Source).Select(rel => Path.Combine(m.Source.SourcePath, rel)))
                .Where(abs => members.All(m => !PathConfinement.IsUnderOrEqual(m.Source.SourcePath, abs) || Excludes(m, abs, isFile)))
                .Select(abs => Path.GetRelativePath(k.SourcePath, abs))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new Kept(k, Survivors(m => m.ExcludedRelativePaths, isFile: true), Survivors(m => m.ExcludedRelativeDirectories ?? [], isFile: false));
        }).ToList();
    }

    // A source excludes a file when the file is one of its excluded files or lies under one of its
    // excluded directories; it excludes a directory only by the second test.
    private static bool ExcludesDirectory(CopySource s, string path)
        => (s.ExcludedRelativeDirectories ?? []).Any(d => PathConfinement.IsUnderOrEqual(Path.Combine(s.SourcePath, d), path));

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

    // Entries collide when they are the same path or one nests inside the other (whole segments,
    // case-insensitive), since both would write one folder tree. Colliding entries take their
    // night as a prefix; any that still collide get _2, _3 after the first segment.
    private static string[] ResolveCollisions(List<Kept> kept, string[] entries)
    {
        static bool Collide(string a, string b) => PathConfinement.IsUnderOrEqual(a, b) || PathConfinement.IsUnderOrEqual(b, a);
        var result = new string[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries.Where((e, j) => j != i && Collide(e, entries[i])).Any()
                ? kept[i].Source.Night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "_" + entries[i]
                : entries[i];
            var final = entry;
            for (var n = 2; result.Take(i).Any(r => Collide(r, final)); n++)
            {
                var cut = entry.IndexOf(Path.DirectorySeparatorChar);
                final = cut < 0 ? entry + "_" + n : entry.Insert(cut, "_" + n);
            }
            result[i] = final;
        }
        return result;
    }
}
