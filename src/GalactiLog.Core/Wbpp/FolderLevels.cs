using System.Globalization;
using GalactiLog.Core.Io;

namespace GalactiLog.Core.Wbpp;

/// <summary>
/// Every folder a catalogued file sits under, answered once for the whole library so a session's
/// levels never rebuild it. Port of the <c>folder_occupants</c> loop inside
/// <c>compute_session_levels</c> in the web's <c>wbpp_export.py</c>, hoisted to one pass.
/// Holds no file system state: it is built from path strings and sizes the database already has.
/// </summary>
public sealed class ContaminationIndex
{
    // Longest first, so RootOf answers with the deepest configured root containing a path.
    // ScanFilterConfig.RefuseScanRoot already forbids nested roots; the longest-match rule means a
    // library that predates that refusal still answers sensibly.
    private readonly List<string> roots;

    // Keyed case-insensitively: one folder on Windows can be catalogued under two spellings if a
    // scan root was re-added with different case, and the first spelling seen is the one the level
    // reports. The facts are the only allocation here that scales with the library, which is why a
    // folder holds one occupant entry per (target, night) rather than one per frame.
    private readonly Dictionary<string, FolderFacts> folders;

    // Keyed by TargetKeyComparer, the one target key rule, so a GUID key's two spellings are one
    // entry while two obj: keys differing only in case stay two.
    private readonly Dictionary<string, string> targetNames;

    private ContaminationIndex(List<string> roots)
    {
        this.roots = roots;
        folders = new Dictionary<string, FolderFacts>(StringComparer.OrdinalIgnoreCase);
        targetNames = new Dictionary<string, string>(TargetKeyComparer.Instance);
    }

    /// <summary>
    /// Walks every catalogue row once and records, per folder under a scan root, which
    /// (target, night) pairs occupy it, the total size of every catalogued file beneath it and the
    /// longest path any of those files has relative to it. A row under no scan root is skipped by
    /// all three: it is not exportable, and it contaminates and weighs nothing.
    /// </summary>
    /// <param name="scanRoots">The configured scan roots, as the page read them, in any spelling.
    /// This is the one home of scan root normalisation for the whole phase: a blank or whitespace
    /// entry is dropped (<c>Path.GetFullPath</c> throws <see cref="ArgumentException"/> on one),
    /// surrounding whitespace is trimmed, the rest are taken to their <c>Path.GetFullPath</c> form
    /// and a trailing separator is removed unless the root is an entire drive or share, which is
    /// its separator (<c>AppWriter.IsDriveOrShareRoot</c>, the same rule
    /// <see cref="PathConfinement"/> uses). No caller normalises them a second time.</param>
    /// <param name="catalogue">Every <c>images</c> row, streamed; nothing is materialised.
    /// <c>FilePath</c> is expected to be a <c>Path.GetFullPath</c> result already, which is what
    /// <c>WbppPathsQuery</c> hands over: a row still carrying forward separators resolves to no
    /// root and drops out of all three accumulators with no signal.</param>
    public static ContaminationIndex Build(
        IReadOnlyList<string> scanRoots,
        IEnumerable<WbppCataloguePath> catalogue)
    {
        var index = new ContaminationIndex(
            [.. scanRoots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => NormalizeRoot(root.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(root => root.Length)]);

        foreach (var row in catalogue)
        {
            var root = index.RootOf(row.FilePath);
            if (root is null)
            {
                continue;
            }

            // Contamination stays a property of the dated LIGHT rows, which is the Python's own
            // source statement; the size total and the longest relative path are properties of the
            // whole folder (ruling R4), so every row feeds those two.
            var occupies = row.IsLight && row.Night is not null;
            if (occupies)
            {
                // The first display name seen for a target key wins, so a later row cannot rename a
                // level's contamination list halfway through the pass.
                index.targetNames.TryAdd(row.TargetKey, row.TargetName);
            }

            foreach (var folder in FolderLevels.AncestorChain(row.FilePath, root))
            {
                if (!index.folders.TryGetValue(folder, out var facts))
                {
                    facts = new FolderFacts();
                    index.folders.Add(folder, facts);
                }

                if (occupies)
                {
                    facts.Occupants.Add((row.TargetKey, row.Night!.Value));
                }

                if (row.FileSize is long size)
                {
                    facts.Bytes += size;
                }
                else
                {
                    facts.BytesUnknown = true;
                }

                // A chain folder always lies strictly under a scan root, so it never carries a
                // trailing separator and the file's path relative to it is the remainder past one
                // separator. Ruling R7 needs only that remainder's length, which no allocation is
                // required to measure.
                var relative = row.FilePath.Length - folder.Length - 1;
                if (relative > facts.LongestRelative)
                {
                    facts.LongestRelative = relative;
                }
            }
        }

        return index;
    }

    /// <summary>The longest configured scan root containing <paramref name="framePath"/>, or null
    /// when the frame lies under none of them.</summary>
    public string? RootOf(string framePath)
    {
        foreach (var root in roots)
        {
            if (PathConfinement.IsUnderOrEqual(root, framePath))
            {
                return root;
            }
        }

        return null;
    }

    // A drive or share root IS its trailing separator ("D:\" trimmed to "D:" names the process's
    // current directory on D:, an entirely different place), so it is the one shape that keeps it.
    private static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        return AppWriter.IsDriveOrShareRoot(full)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    // The one target key rule of this file, used at every dictionary, set and equality that holds
    // one. Ordinal, with a single exception: a key that parses whole as a GUID is compared
    // case-insensitively against another key that also does.
    //
    // Both halves are load-bearing. images.resolved_target_id is stored upper cased while a Guid
    // prints lower cased, so without the exception a caller holding either spelling of its own
    // target would see it as another target and read every level as contaminated. But
    // resolved_target_id and the obj: concatenation are BINARY collated and the dashboard groups by
    // them, so obj:m31 and obj:M31 are two real groups today; folding the whole key would hide each
    // from the other's contamination list. A GUID key and an obj: key are never equal either way.
    internal sealed class TargetKeyComparer : IEqualityComparer<string>
    {
        internal static readonly TargetKeyComparer Instance = new();

        public bool Equals(string? left, string? right)
            => Guid.TryParse(left, out var leftId) && Guid.TryParse(right, out var rightId)
                ? leftId == rightId
                : string.Equals(left, right, StringComparison.Ordinal);

        // A GUID key hashes by its canonical form, so two spellings of one id land together; every
        // other key hashes ordinally. A GUID-parsing key and a non-parsing one are never ordinally
        // equal, so the two schemes cannot disagree with Equals.
        public int GetHashCode(string value)
            => Guid.TryParse(value, out var id) ? id.GetHashCode() : StringComparer.Ordinal.GetHashCode(value);
    }

    // One occupant is a (target key, night) pair. A default tuple comparer would compare the key
    // ordinally and record one GUID target under two spellings as two occupants.
    internal sealed class OccupantComparer : IEqualityComparer<(string TargetKey, DateOnly Night)>
    {
        internal static readonly OccupantComparer Instance = new();

        public bool Equals((string TargetKey, DateOnly Night) left, (string TargetKey, DateOnly Night) right)
            => left.Night == right.Night && TargetKeyComparer.Instance.Equals(left.TargetKey, right.TargetKey);

        public int GetHashCode((string TargetKey, DateOnly Night) value)
            => HashCode.Combine(TargetKeyComparer.Instance.GetHashCode(value.TargetKey), value.Night);
    }

    // What one folder accumulated over the single build pass.
    internal sealed class FolderFacts
    {
        internal HashSet<(string TargetKey, DateOnly Night)> Occupants { get; } = new(OccupantComparer.Instance);

        internal long Bytes { get; set; }

        internal bool BytesUnknown { get; set; }

        internal int LongestRelative { get; set; }
    }

    // Null when no catalogue row sits under the folder at all.
    internal FolderFacts? Facts(string folder) => folders.GetValueOrDefault(folder);

    // The display name the catalogue gave a target key, falling back to the key itself.
    internal string DisplayName(string targetKey) => targetNames.GetValueOrDefault(targetKey, targetKey);
}

/// <summary>
/// The folder level computation for a WBPP export: which folder a night should be copied from,
/// what that folder drags along and how big it is. Port of <c>compute_ancestor_chain</c>,
/// <c>compute_session_levels</c>, <c>pick_default_level</c>, <c>subtree_bytes</c>,
/// <c>disambiguate_staging_names</c> and <c>map_excluded_to_ops</c> in the web's
/// <c>wbpp_export.py</c>. Nothing here opens a file, walks a directory or asks the file system
/// anything.
/// </summary>
/// <remarks>
/// The Python works on POSIX container paths, where a bare component split and a prefix test are
/// correct. None of that is correct here. Which comparer each operation uses, once, so no member
/// invents a second answer:
/// <list type="table">
/// <item><term>Normalising an incoming path</term><description><c>Path.GetFullPath</c> once, at
/// the query boundary, before any comparison. Pure string work; it touches no disk.</description></item>
/// <item><term>Is a frame under a folder</term><description><see cref="PathConfinement.IsUnderOrEqual"/>.
/// Nothing else, anywhere in this file.</description></item>
/// <item><term>Folder dictionary keys</term><description><see cref="StringComparer.OrdinalIgnoreCase"/>.
/// One folder on Windows can be catalogued under two spellings; the first spelling seen is the one
/// the level reports.</description></item>
/// <item><term>Target key equality</term><description><c>ContaminationIndex.TargetKeyComparer</c>:
/// ordinal, except that a key parsing whole as a <see cref="Guid"/> is compared case-insensitively
/// against another key that also does. <c>images.resolved_target_id</c> is stored upper cased while
/// a <see cref="Guid"/> prints lower cased, so a purely ordinal rule would make a night see its own
/// target as another target and read every level as contaminated; but that column and the
/// <c>obj:</c> concatenation are binary collated and the dashboard groups by them, so <c>obj:m31</c>
/// and <c>obj:M31</c> are two real groups and a case-folding rule would hide each from the other's
/// contamination list.</description></item>
/// <item><term>Sorting the contamination lists</term><description><see cref="StringComparer.Ordinal"/>,
/// which is Python's own <c>sorted()</c> over <c>str</c>.</description></item>
/// <item><term>Level ordering</term><description>Ruling R1: depth ascending, then the path under
/// <see cref="StringComparer.OrdinalIgnoreCase"/>. The Python sorts a set by slash count alone, so
/// its equal-depth siblings follow the interpreter's hash seed; this order is total.</description></item>
/// <item><term>Staging name collisions</term><description><see cref="StringComparer.OrdinalIgnoreCase"/>,
/// because the names become folders in one staging directory on Windows.</description></item>
/// <item><term>Relative path for a per-file exclude</term><description><c>Path.GetRelativePath</c>,
/// which yields <c>\</c> separators.</description></item>
/// </list>
/// A dot in a folder name is not an extension: <c>Angle_71.61</c> is a whole folder name and
/// nothing here reaches for the extension members of <c>Path</c>.
/// </remarks>
public static class FolderLevels
{
    /// <summary>
    /// The folders from the first level under <paramref name="scanRoot"/> down to and including the
    /// frame's own parent, shallowest first. Port of <c>compute_ancestor_chain</c>; the scan root
    /// itself is never in the list. A frame whose parent is the scan root, or which lies under no
    /// part of it, returns an empty chain.
    /// </summary>
    public static IReadOnlyList<string> AncestorChain(string framePath, string scanRoot)
    {
        var chain = new List<string>();
        var parent = Path.GetDirectoryName(framePath);

        while (!string.IsNullOrEmpty(parent) && !SameFolder(parent, scanRoot))
        {
            if (!PathConfinement.IsUnderOrEqual(scanRoot, parent))
            {
                return [];
            }

            chain.Add(parent);
            parent = Path.GetDirectoryName(parent);
        }

        if (string.IsNullOrEmpty(parent))
        {
            return [];
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// One night's candidate export folders, with the default already picked. Port of
    /// <c>compute_session_levels</c> plus <c>pick_default_level</c>, answered together because the
    /// default is a property of the level list.
    /// </summary>
    /// <param name="targetKey">The page's own group key. The Python infers the current target from
    /// the occupants of an arbitrary one of the deepest folders; the port is handed it.</param>
    public static SessionLevels ForSession(
        DateOnly night,
        string targetKey,
        IReadOnlyList<WbppFramePath> frames,
        ContaminationIndex index)
    {
        if (frames.Count == 0)
        {
            return new SessionLevels(night, "", [], 0, 0, 0, LevelsUnavailable.NoFrames);
        }

        // A frame under no configured scan root contributes no chain, no level and no count, but it
        // never empties the night: that is what a user meets the first time they remove a root.
        var resolved = new List<(WbppFramePath Frame, string Root)>(frames.Count);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var withoutRoot = 0;
        foreach (var frame in frames)
        {
            var root = index.RootOf(frame.FilePath);
            if (root is null)
            {
                withoutRoot++;
                continue;
            }

            resolved.Add((frame, root));
            roots.Add(root);
        }

        if (resolved.Count == 0)
        {
            return new SessionLevels(night, "", [], 0, frames.Count, withoutRoot, LevelsUnavailable.NoScanRoot);
        }

        if (roots.Count > 1)
        {
            return new SessionLevels(night, "", [], 0, frames.Count, withoutRoot, LevelsUnavailable.SeveralScanRoots);
        }

        // A frame whose parent IS the scan root has an empty chain, exactly as the Python's
        // compute_ancestor_chain leaves it, so it lies under no level and is counted by none of
        // them. The night keeps its levels; this figure is what accounts for the shortfall against
        // TotalFrameCount, which nothing else would explain. A resolved frame's chain is empty for
        // no other reason, since it is known to be under its root.
        var scanRoot = resolved[0].Root;
        var inRootItself = 0;
        var union = new Dictionary<string, (string Path, int Depth)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (frame, root) in resolved)
        {
            var chain = AncestorChain(frame.FilePath, root);
            if (chain.Count == 0)
            {
                inRootItself++;
            }

            for (var depth = 1; depth <= chain.Count; depth++)
            {
                union.TryAdd(chain[depth - 1], (chain[depth - 1], depth));
            }
        }

        if (union.Count == 0)
        {
            return new SessionLevels(
                night, scanRoot, [], 0, frames.Count, withoutRoot, LevelsUnavailable.FramesInRootItself, inRootItself);
        }

        var levels = union.Values
            .OrderBy(folder => folder.Depth)
            .ThenBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
            .Select(folder => Describe(folder.Path, folder.Depth, night, targetKey, resolved, index))
            .ToList();

        return new SessionLevels(
            night, scanRoot, levels, PickDefaultLevel(levels), frames.Count, withoutRoot, null, inRootItself);
    }

    /// <summary>
    /// The index of the deepest level that holds every frame and is not contaminated, falling back
    /// to the deepest level holding every frame. Port of <c>pick_default_level</c>, unchanged. An
    /// empty list answers 0, which means nothing.
    /// </summary>
    public static int PickDefaultLevel(IReadOnlyList<FolderLevel> levels)
    {
        if (levels.Count == 0)
        {
            return 0;
        }

        var most = levels.Max(level => level.FrameCount);

        for (var i = levels.Count - 1; i >= 0; i--)
        {
            if (levels[i].FrameCount == most && !levels[i].IsContaminated)
            {
                return i;
            }
        }

        for (var i = levels.Count - 1; i >= 0; i--)
        {
            if (levels[i].FrameCount == most)
            {
                return i;
            }
        }

        // The Python's own unreachable guard: some level always attains the maximum, so the second
        // pass always returns. Ported as it stands, and no case can make it fire.
        return levels.Count - 1;
    }

    /// <summary>
    /// The total size of every catalogued file under <paramref name="folder"/>, of any target and
    /// any frame type (ruling R4). Null the moment one contributing row has an unknown size: never
    /// coalesced, never summed around, never reported partial, because understating by an unknown
    /// amount in front of a user about to commit to a copy is worse than saying nothing. A folder
    /// no catalogue row sits under totals 0, because zero files is a known quantity.
    /// </summary>
    public static long? SubtreeBytes(string folder, ContaminationIndex index)
    {
        var facts = index.Facts(folder);
        if (facts is null)
        {
            return 0;
        }

        return facts.BytesUnknown ? null : facts.Bytes;
    }

    /// <summary>
    /// The length of the longest path the copy would write for this level (ruling R7): the staging
    /// root with any trailing separator trimmed, one separator, the entry name, one separator, and
    /// the longest path relative to the level among the catalogued files under it. Pure arithmetic
    /// over strings; it touches no disk, and the script generator knows nothing about it.
    /// </summary>
    public static int LongestDestinationLength(
        ChosenLevel chosen,
        string stagingRoot,
        string entryName,
        ContaminationIndex index)
        => TrimTrailingSeparator(stagingRoot).Length
            + 1
            + entryName.Length
            + 1
            + (index.Facts(chosen.Level.Path)?.LongestRelative ?? 0);

    /// <summary>
    /// The staging folder name for each chosen level, in the order given. Port of
    /// <c>disambiguate_staging_names</c>: colliding basenames are all prefixed with their own
    /// night as <c>yyyy-MM-dd_</c>. The returned list is then made distinct under
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> with a <c>_2</c>, <c>_3</c> suffix
    /// (ruling R6), because the prefix can itself collide with a folder already named that way and
    /// the Python's answer there is two copy operations writing into one folder.
    /// The suffix is the first free number at that position in the list, so which entry takes which
    /// number depends on the order of <paramref name="chosen"/>; only the distinctness of the whole
    /// list is guaranteed. Seeding the taken set up front instead would rename the first occurrence
    /// too, which is worse.
    /// </summary>
    public static IReadOnlyList<string> StagingNames(IReadOnlyList<ChosenLevel> chosen)
    {
        var bases = chosen.Select(level => BaseName(level.Level.Path)).ToList();

        // Counted case-insensitively, unlike the Python's Counter: the names become folders in one
        // staging directory on Windows, where HA and Ha are one folder and a case-sensitive count
        // would hand the user a silent merge.
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in bases)
        {
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(chosen.Count);
        for (var i = 0; i < bases.Count; i++)
        {
            var name = counts[bases[i]] > 1
                ? chosen[i].Night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "_" + bases[i]
                : bases[i];

            var candidate = name;
            var suffix = 1;
            while (!taken.Add(candidate))
            {
                suffix++;
                candidate = name + "_" + suffix.ToString(CultureInfo.InvariantCulture);
            }

            names.Add(candidate);
        }

        return names;
    }

    /// <summary>
    /// The excluded frames that actually lie under their own night's chosen level. Port of
    /// <c>excludedUnderSelectedLevels</c>: a night's excluded frames are tested only against that
    /// night's level, so a frame the filter dropped in <c>OIII</c> is not deducted from an
    /// <c>Ha</c> copy that never touches it. A night with no chosen level contributes nothing.
    /// <paramref name="chosen"/> carries at most one level per night, which is what the page picks;
    /// two levels sharing a night would return that night's excluded frames twice and
    /// <see cref="Totals"/> would deduct them twice.
    /// </summary>
    public static IReadOnlyList<WbppFramePath> ExcludedUnderLevels(
        IReadOnlyList<ChosenLevel> chosen,
        IReadOnlyDictionary<DateOnly, IReadOnlyList<WbppFramePath>> excludedByNight)
    {
        var under = new List<WbppFramePath>();
        foreach (var level in chosen)
        {
            if (!excludedByNight.TryGetValue(level.Night, out var excluded))
            {
                continue;
            }

            under.AddRange(
                excluded.Where(frame => PathConfinement.IsUnderOrEqual(level.Level.Path, frame.FilePath)));
        }

        return under;
    }

    /// <summary>
    /// One copy operation per chosen level, in the order given, with its staging entry name and its
    /// per-file excludes. Port of <c>map_excluded_to_ops</c> plus
    /// <c>disambiguate_staging_names</c>: each excluded frame is assigned to the chosen level whose
    /// path is its longest containing prefix, and a frame matching no chosen level is dropped.
    /// This is the single input the script generator renders from.
    /// </summary>
    public static IReadOnlyList<CopyOperation> CopyOperations(
        IReadOnlyList<ChosenLevel> chosen,
        IReadOnlyList<WbppFramePath> excludedUnderLevels)
    {
        var names = StagingNames(chosen);
        var perLevel = chosen.Select(_ => new List<string>()).ToList();

        foreach (var frame in excludedUnderLevels)
        {
            var best = -1;
            var bestLength = -1;
            for (var i = 0; i < chosen.Count; i++)
            {
                var path = chosen[i].Level.Path;
                if (PathConfinement.IsUnderOrEqual(path, frame.FilePath) && path.Length > bestLength)
                {
                    best = i;
                    bestLength = path.Length;
                }
            }

            if (best < 0)
            {
                continue;
            }

            perLevel[best].Add(Path.GetRelativePath(chosen[best].Level.Path, frame.FilePath));
        }

        return [.. chosen.Select((level, i) => new CopyOperation(level.Night, level.Level.Path, names[i], perLevel[i]))];
    }

    /// <summary>
    /// The export's summary counts. Port of the web modal's <c>frameCount</c> and <c>sizeBytes</c>
    /// memos. <see cref="ExportTotals.SizeBytes"/> is null the moment one chosen level's own total
    /// or one excluded frame's size is unknown.
    /// </summary>
    public static ExportTotals Totals(
        IReadOnlyList<ChosenLevel> chosen,
        IReadOnlyList<WbppFramePath> excludedUnderLevels)
    {
        var frameCount = chosen.Sum(level => level.Level.FrameCount) - excludedUnderLevels.Count;

        long? size = 0;
        foreach (var level in chosen)
        {
            size = level.Level.SubtreeBytes is long bytes && size is long running ? running + bytes : null;
        }

        foreach (var frame in excludedUnderLevels)
        {
            size = frame.FileSize is long bytes && size is long running ? running - bytes : null;
        }

        return new ExportTotals(frameCount, chosen.Count, size);
    }

    // One folder's contamination lists, frame count and size, all read for the same path.
    private static FolderLevel Describe(
        string path,
        int depth,
        DateOnly night,
        string targetKey,
        List<(WbppFramePath Frame, string Root)> resolved,
        ContaminationIndex index)
    {
        var count = resolved.Count(entry => PathConfinement.IsUnderOrEqual(path, entry.Frame.FilePath));
        var facts = index.Facts(path);

        // Excluded by group key and then printed as a display name, which is ruled departure 4: the
        // key is the right thing to compare. Two distinct keys that display the same text, a
        // resolved target beside its unresolved obj: twin, therefore put the page's own name into
        // its own list; that is a labelling question for the page, not a computation defect here.
        IReadOnlyList<string> otherTargets = facts is null
            ? []
            : [.. facts.Occupants
                .Where(occupant => !ContaminationIndex.TargetKeyComparer.Instance.Equals(occupant.TargetKey, targetKey))
                .Select(occupant => index.DisplayName(occupant.TargetKey))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];

        IReadOnlyList<string> otherNights = facts is null
            ? []
            : [.. facts.Occupants
                .Where(occupant => occupant.Night != night)
                .Select(occupant => occupant.Night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];

        return new FolderLevel(path, depth, count, otherTargets, otherNights, SubtreeBytes(path, index));
    }

    // Mutual containment is folder equality, tolerant of a trailing separator on either side and of
    // a drive or share root, without a second rule of its own.
    private static bool SameFolder(string left, string right)
        => PathConfinement.IsUnderOrEqual(left, right) && PathConfinement.IsUnderOrEqual(right, left);

    /// <summary>A folder's own name: the staging entry name for a chosen folder, and the one home
    /// for that rule. The application's row and tree text reaches it rather than splitting a path
    /// again, so a staging entry name and the name shown beside it cannot differ.</summary>
    public static string BaseName(string path)
    {

        var trimmed = TrimTrailingSeparator(path);
        var name = Path.GetFileName(trimmed);
        if (name.Length > 0)
        {
            return name;
        }

        // A last-segment split, not a containment test and not a membership rule: design lesson 1's
        // one containment rule is untouched by it, and every alternative reaches for a file system
        // type. Both root shapes are their own path root, so GetFileName answers nothing for either. A
        // share root keeps its share name, which is the Python's own rsplit answer. A drive root
        // has no usable last segment at all ("D:\" trims to "D:", which is not a legal folder
        // name), so it is named after the drive letter alone; neither ever yields the empty
        // string, which would make the staging destination the staging root itself.
        var separator = trimmed.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return separator >= 0 ? trimmed[(separator + 1)..] : trimmed.TrimEnd(':');
    }

    private static string TrimTrailingSeparator(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
