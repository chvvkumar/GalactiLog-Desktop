using GalactiLog.Core.Io;
using GalactiLog.Core.Metadata;

namespace GalactiLog.Core.Scanning;

// One discovered file with the disk stats needed for delta classification (spec 10.3 step 2).
public sealed record DiscoveredFile(string Path, long FileSize, double FileMtimeUnixSeconds);

public enum FileClassification { New, Changed, Unchanged }

// Port of scanner.py's _walk_supported_files/scan_directory discovery loop (design-spec
// 10.3 steps 1-2). Pure and disk-touching but DB-free: FileWalker has no EF reference and
// never queries the database itself (the known-file map is caller-supplied).
//
// Skips and rejections come back as data, never through ILogger or an exception -- no
// ILogger in Core, matching FrameReader.TryRead's rejectionReason convention. The caller
// (Task 5's ScanCoordinator) logs whatever onWarning hands it.
public static class FileWalker
{
    private const int ProgressInterval = 50;

    /// <summary>
    /// The delta-skip tolerance of spec 10.3 step 2, in seconds. Public because the guide-log
    /// pass of spec 10.3 step 5 applies the SAME tolerance to <c>phd2_logs</c> rows: network
    /// filesystems and archive tools round modification times, and a sub-second difference is
    /// never a real edit on either side. One declaration, so the two cannot drift.
    /// </summary>
    public const double MtimeToleranceSeconds = 1.0;

    // Spec 7.6's discovery rule, ^PHD2_GuideLog_.*\.txt$ matched case-insensitively, written as
    // the sanctioned StartsWith/EndsWith pair rather than a Regex: the two cannot overlap (the
    // prefix is 14 characters and the suffix 4, and no 17-character-or-shorter string can carry
    // both), and a pair has no end-of-string-before-a-newline subtlety to reason about.
    private const string GuideLogPrefix = "PHD2_GuideLog_";
    private const string GuideLogSuffix = ".txt";

    // Second guard against a pathological (or malicious) reparse-point loop: a directory
    // junction/symlink can point at an ANCESTOR of itself inside a root -- e.g. root\A\loop
    // -> root -- which the outside-every-root confinement check below does not catch,
    // because the target is still inside a root, just not a new one. The `visited` real-path
    // set below is the primary guard; this is a depth cap for any remaining pathological
    // nesting (real or reparse-induced) that a stack-recursive walk should not chase forever.
    private const int MaxDepth = 64;

    // Walks every entry under `effectiveRoots` (already computed via
    // ScanFilterConfig.EffectiveRoots) depth-first, applying `filters` per spec 10.2.
    //
    // An iterator method, not a visitor: callers drive it with foreach, or materialize with
    // .ToList() when the complete discovered-path set is needed (Task 5's classification
    // pass, Task 6's orphan pruner).
    //
    // `allConfinementRoots` is every configured scan_root (not just the effective ones being
    // walked this call) -- spec 10.1: "a followed link that lands outside every root is
    // skipped" means the whole configured set, not only the one being walked.
    //
    // Cancellation is checked once per discovered file-system entry (spec 10.5 checkpoint
    // 1). `onProgress` fires every 50 discovered files and once more with the final count
    // once the whole walk completes (matching scan_directory's trailing on_progress call) --
    // which requires the caller to fully enumerate the result for the trailing call to fire.
    // `onWarning` fires once per inaccessible directory or entry, carrying the message as
    // plain data.
    //
    // `onGuideLog` is spec 7.6's guide-log callback and spec 10.3 step 5 item 1's "the walk is
    // not repeated": a PHD2 guide log is reported through it and is NOT yielded from the
    // iterator. Yielding one would make it a discovered file for classification, the header
    // read, the frame-side orphan set and scan_runs.discovered, all of which would be wrong. It
    // is trailing and optional so that every existing call site keeps building, and a caller
    // that passes none discovers no guide log at all, exactly as before this parameter existed.
    public static IEnumerable<DiscoveredFile> Walk(
        IReadOnlyList<string> effectiveRoots,
        IReadOnlyList<string> allConfinementRoots,
        ScanFilterConfig filters,
        Action<int>? onProgress,
        Action<string>? onWarning,
        CancellationToken ct,
        Action<DiscoveredFile>? onGuideLog = null)
    {
        // Real (resolved) directory paths already entered during this call, so a reparse
        // point that leads back to one of them -- a root, or any directory already walked
        // through another path -- is detected and skipped instead of recursed into forever.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedRoots = effectiveRoots.Select(Path.GetFullPath).ToList();
        foreach (var r in normalizedRoots) visited.Add(r);

        var count = 0;
        foreach (var normalizedRoot in normalizedRoots)
        {
            foreach (var file in WalkDir(normalizedRoot, normalizedRoot, allConfinementRoots, filters, onWarning, onGuideLog, visited, depth: 0, ct))
            {
                count++;
                yield return file;
                if (count % ProgressInterval == 0) onProgress?.Invoke(count);
            }
        }
        onProgress?.Invoke(count);
    }

    /// <summary>
    /// Spec 7.6's guide-log rule, and the ONE place it lives: a name matching
    /// <c>^PHD2_GuideLog_.*\.txt$</c>, case-insensitively. Port of <c>scanner.PHD2_LOG_RE</c> and
    /// <c>scanner.is_phd2_guide_log</c>.
    /// </summary>
    /// <param name="fileName">A file name, or a full path: the directory part is dropped first,
    /// so a caller holding either can ask.</param>
    /// <remarks>
    /// The sibling <c>PHD2_DebugLog_*</c> files never match, because the test anchors on the
    /// <c>PHD2_GuideLog_</c> prefix. That is not incidental: a debug log is multi-megabyte and
    /// unstructured, and reading one would be a visible fault.
    /// </remarks>
    public static bool IsGuideLog(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return name.StartsWith(GuideLogPrefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(GuideLogSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<DiscoveredFile> WalkDir(
        string dir, string effectiveRoot, IReadOnlyList<string> allRoots, ScanFilterConfig filters,
        Action<string>? onWarning, Action<DiscoveredFile>? onGuideLog, HashSet<string> visited,
        int depth, CancellationToken ct)
    {
        if (depth > MaxDepth)
        {
            onWarning?.Invoke($"Skipping directory beyond max depth {MaxDepth}: {dir}");
            yield break;
        }

        List<FileSystemInfo>? entries = null;
        try
        {
            entries = UserFiles.EnumerateFileSystemEntries(dir).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            onWarning?.Invoke($"Skipping inaccessible directory {dir}: {ex.Message}");
        }
        if (entries is null) yield break;

        // Attributes, size and last-write time all come from the listing the enumeration
        // already did; nothing below stats an entry again, and nothing calls Refresh().
        foreach (var info in entries)
        {
            ct.ThrowIfCancellationRequested();

            var entry = info.FullName;
            var attrs = info.Attributes;

            if (attrs.HasFlag(FileAttributes.Directory))
            {
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    var resolved = UserFiles.ResolveDirectoryLinkTarget(entry);
                    if (resolved is null) continue; // broken link: nothing to walk into
                    if (!allRoots.Any(r => PathConfinement.IsUnderOrEqual(Path.GetFullPath(r), resolved)))
                    {
                        continue; // lands outside every configured root (spec 10.1)
                    }
                    // A reparse point landing INSIDE a root can still loop -- e.g.
                    // root\A\loop -> root, or two junctions pointing at each other -- which
                    // the outside-every-root check above does not catch. Guard on the
                    // resolved real path, not the logical one, since that is what actually
                    // repeats.
                    if (!visited.Add(resolved))
                    {
                        onWarning?.Invoke($"Skipping already-visited directory reached via reparse point {entry} -> {resolved}");
                        continue;
                    }
                    // Keep walking through the LOGICAL path (`entry`), not the resolved
                    // target: ShouldWalkDir/ShouldIncludeFile compute ancestor segments
                    // relative to `effectiveRoot`, and the logical path is what stays under
                    // it. Windows transparently follows the junction/symlink when the
                    // recursive call next enumerates `entry`'s contents.
                }
                if (!filters.ShouldWalkDir(entry, effectiveRoot)) continue;
                foreach (var f in WalkDir(entry, effectiveRoot, allRoots, filters, onWarning, onGuideLog, visited, depth + 1, ct))
                {
                    yield return f;
                }
            }
            else
            {
                // A guide log is a candidate INSTEAD of FrameReader.IsSupported and, like a
                // frame, only after filters.ShouldIncludeFile: spec 7.6's "the include and
                // exclude filters of section 10.2 apply to a guide log exactly as they apply to
                // a frame, because it is the same walk".
                var guideLog = onGuideLog is not null && IsGuideLog(entry);
                if (!guideLog && !FrameReader.IsSupported(entry)) continue;
                if (!filters.ShouldIncludeFile(entry, effectiveRoot)) continue;

                var file = (FileInfo)info;
                var discovered = new DiscoveredFile(entry, file.Length, ToUnixSeconds(file.LastWriteTimeUtc));

                // The same stat the frame branch already took, handed on: the delta skip of
                // spec 10.3 step 5 item 2 needs both the size and the modification time, and a
                // second stat per file would double the walk's syscalls on a library with many
                // logs. Reported, never yielded.
                if (guideLog)
                {
                    onGuideLog!(discovered);
                    continue;
                }

                yield return discovered;
            }
        }
    }

    // spec 10.3 step 2. `known` maps file_path -> (file_size, file_mtime) loaded from
    // `images` by the caller (ScanCoordinator, Task 5); FileWalker never queries the
    // database itself. A null stored value skips that half of the comparison rather than
    // counting as a mismatch (matches scan_directory: `if stored_size is not None and ...
    // or stored_mtime is not None and ...`).
    public static FileClassification Classify(
        DiscoveredFile file, IReadOnlyDictionary<string, (long? Size, double? Mtime)> known)
    {
        if (!known.TryGetValue(file.Path, out var existing)) return FileClassification.New;
        if (existing.Size is { } size && size != file.FileSize) return FileClassification.Changed;
        if (existing.Mtime is { } mtime && Math.Abs(mtime - file.FileMtimeUnixSeconds) > MtimeToleranceSeconds)
            return FileClassification.Changed;
        return FileClassification.Unchanged;
    }

    private static double ToUnixSeconds(DateTime utc) => (utc - DateTime.UnixEpoch).TotalSeconds;
}
