using System.Text.Json;
using System.Text.Json.Serialization;
using GalactiLog.Core.Io;
using GalactiLog.Core.Metadata;
using GalactiLog.Core.Settings;

namespace GalactiLog.Core.Scanning;

// Port of backend/app/services/scan_filters.py's ScanFilterConfig (design-spec 10.1, 10.2).
// Pure, disk-agnostic matching logic: it never touches the filesystem except for the
// Exists/DirectoryExists probe inside TestPath's "auto" kind resolution, which only runs
// after path confinement has already succeeded.
public sealed record ScanFilterConfig
{
    [JsonPropertyName("include_paths")] public IReadOnlyList<string> IncludePaths { get; init; } = [];
    [JsonPropertyName("exclude_paths")] public IReadOnlyList<string> ExcludePaths { get; init; } = [];
    [JsonPropertyName("name_rules")] public IReadOnlyList<NameRule> NameRules { get; init; } = [];

    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public static readonly ScanFilterConfig Empty = new();

    /// <summary>
    /// The five folder names the setup wizard's default exclude rules carry (spec 12.1), and the
    /// id prefix it gives them.
    /// </summary>
    /// <remarks>
    /// They live here, beside <see cref="IsOnlyTheSeededRules"/>, rather than in the wizard's own
    /// step, because the notice predicate and the wizard have to agree about what "the seeded
    /// five" are and a second copy of the list is how they would stop agreeing (design-lessons
    /// rule 2). <c>ScanOptionsStepViewModel.ExcludeDefaults</c> reads this one.
    /// </remarks>
    public static readonly IReadOnlyList<string> SeededExcludeNames =
        ["masters", "WBPP", "calibrated", "WORK_AREA", "PixInsight"];

    /// <summary>The id prefix of a wizard-seeded exclude rule.</summary>
    public const string SeededExcludeIdPrefix = "setup-exclude-";

    /// <summary>The five rules the setup wizard seeds, in order.</summary>
    public static IReadOnlyList<NameRule> SeededRules()
        => [.. SeededExcludeNames.Select(name => new NameRule
        {
            Id = SeededExcludeIdPrefix + name,
            Action = "exclude",
            Type = "substring",
            Pattern = name,
            Target = "folder",
            Enabled = true,
        })];

    /// <summary>
    /// Spec 12.2's scan filter notice condition (PAR-014): the stored filters are still only what
    /// the setup wizard seeded. True when <see cref="IncludePaths"/> and <see cref="ExcludePaths"/>
    /// are both empty and <see cref="NameRules"/> is exactly <see cref="SeededRules"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one computation behind both surfaces, the Dashboard's notice and the Library tab's, so
    /// spec 12.2's "the two surfaces cannot disagree" is structural rather than conventional. A
    /// static predicate on this record rather than a service: every other predicate over these
    /// three lists already lives here, and an interface with one implementation would buy nothing.
    /// </para>
    /// <para>
    /// Compared on the whole rule, not on the id: a user who edits a seeded rule's pattern, retargets
    /// it or turns it off has made a decision, so the notice goes (spec-writer question 7). Order
    /// matters for nothing, so the comparison is set-shaped over the six fields; <c>NameRule</c>'s
    /// <c>ExtensionData</c> is deliberately not compared, because a round trip through the settings
    /// document can leave it null on one side and empty on the other.
    /// </para>
    /// </remarks>
    public static bool IsOnlyTheSeededRules(ScanFilterConfig filters)
    {
        ArgumentNullException.ThrowIfNull(filters);

        if (filters.IncludePaths.Count != 0 || filters.ExcludePaths.Count != 0) return false;

        var seeded = SeededRules();
        if (filters.NameRules.Count != seeded.Count) return false;

        return seeded.All(expected => filters.NameRules.Any(actual => SameRule(expected, actual)));
    }

    /// <summary>
    /// The notice condition both surfaces read (spec 12.2, polish wave 1 ruling 1): the stored
    /// rules are still only the seeded five AND the user has not reviewed them on either surface.
    /// </summary>
    public static bool ShowsSetupNotice(GeneralSettings general)
    {
        ArgumentNullException.ThrowIfNull(general);
        return !general.ScanFiltersReviewed && IsOnlyTheSeededRules(general.ScanFilters);
    }

    private static bool SameRule(NameRule a, NameRule b)
        => string.Equals(a.Id, b.Id, StringComparison.Ordinal)
            && string.Equals(a.Action, b.Action, StringComparison.Ordinal)
            && string.Equals(a.Type, b.Type, StringComparison.Ordinal)
            && string.Equals(a.Pattern, b.Pattern, StringComparison.Ordinal)
            && string.Equals(a.Target, b.Target, StringComparison.Ordinal)
            && a.Enabled == b.Enabled;

    public enum PathKind { Auto, File, Folder }

    public sealed record TestPathResult(string Verdict, IReadOnlyList<string> MatchedRuleIds);

    // Trust-boundary validation (spec 2.3, 10.2): every include/exclude path must resolve
    // under at least one entry of scanRoots; every rule's action/type/target must be one of
    // the documented values with a non-empty pattern; every regex (and glob-derived)
    // pattern must compile. Throws ScanFilterValidationException naming the first problem
    // found -- not an aggregate, one bad row is enough to stop a scan.
    public void Validate(IReadOnlyList<string> scanRoots)
    {
        // Review item 1: reject a non-fully-qualified entry BEFORE any Path.GetFullPath
        // call, for every scan root as well as every include/exclude path -- a relative
        // segment is exactly what GetFullPath would otherwise silently resolve against the
        // process's current directory, which is not a trust-boundary check at all.
        var roots = scanRoots.Select(root => RequireFullyQualified(root, "general.scan_roots")).ToList();
        var includePaths = IncludePaths.Select(path => RequireFullyQualified(path, "scan_filters.include_paths")).ToList();
        var excludePaths = ExcludePaths.Select(path => RequireFullyQualified(path, "scan_filters.exclude_paths")).ToList();

        // FIXER LIST F10 (Task 5 review minor 1). Two UI surfaces refused a duplicate or nested
        // root at the moment it was typed, and this validator did not know the rule at all, so a
        // nested pair reaching the document any other way -- a hand-edited settings file, an older
        // build, removing the outer root and adding it back in a different order -- saved cleanly
        // and then walked and ingested every file under the inner root twice. The rule lives here
        // now, on the write path every document goes through, and both surfaces read the sentence
        // from <see cref="RefuseScanRoot"/> rather than writing one each.
        for (var i = 0; i < scanRoots.Count; i++)
        {
            if (RefuseScanRoot(scanRoots[i], [.. scanRoots.Take(i)]) is { } refusal)
            {
                throw new ScanFilterValidationException(refusal);
            }
        }

        for (var i = 0; i < IncludePaths.Count; i++)
        {
            if (!roots.Any(root => PathConfinement.IsUnderOrEqual(root, includePaths[i])))
            {
                throw new ScanFilterValidationException(
                    $"scan_filters.include_paths entry '{IncludePaths[i]}' is outside every configured library folder.");
            }
        }

        for (var i = 0; i < ExcludePaths.Count; i++)
        {
            if (!roots.Any(root => PathConfinement.IsUnderOrEqual(root, excludePaths[i])))
            {
                throw new ScanFilterValidationException(
                    $"scan_filters.exclude_paths entry '{ExcludePaths[i]}' is outside every configured library folder.");
            }
        }

        foreach (var rule in NameRules)
        {
            if (rule.Action is not ("include" or "exclude"))
                throw new ScanFilterValidationException($"scan_filters.name_rules entry '{rule.Id}' has an invalid action '{rule.Action}'.");
            if (rule.Type is not ("glob" or "substring" or "regex"))
                throw new ScanFilterValidationException($"scan_filters.name_rules entry '{rule.Id}' has an invalid type '{rule.Type}'.");
            if (rule.Target is not ("file" or "folder"))
                throw new ScanFilterValidationException($"scan_filters.name_rules entry '{rule.Id}' has an invalid target '{rule.Target}'.");
            if (rule.Pattern.Length == 0)
                throw new ScanFilterValidationException($"scan_filters.name_rules entry '{rule.Id}' has an empty pattern.");

            try
            {
                NameRuleMatcher.EnsureCompilable(rule);
            }
            catch (ArgumentException ex)
            {
                throw new ScanFilterValidationException(
                    $"scan_filters.name_rules entry '{rule.Id}' has an uncompilable pattern: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Whether one scan root may join a set of scan roots, and the one sentence to show when it
    /// may not (FIXER LIST F10, and the Task 9 review's minor 2).
    /// </summary>
    /// <param name="candidate">The root being added or checked. Trimmed here.</param>
    /// <param name="existingRoots">The roots it would join. Entries that are not valid paths are
    /// skipped: they carry their own refusal, and reporting a second one about them would say
    /// nothing new.</param>
    /// <returns>Null when the candidate may be added; otherwise the refusal sentence.</returns>
    /// <remarks>
    /// <para>
    /// The rule is spec 10.1's: a scan root is an absolute path, and each root carries its own
    /// confinement boundary, so a root that is a parent or a child of another root makes the
    /// walker enumerate and ingest the same file twice.
    /// </para>
    /// <para>
    /// A string rather than an exception because the two UI surfaces that enforce it -- the
    /// Settings Library tab and the setup wizard's first step -- refuse the gesture inline as the
    /// user makes it, and both must say the same thing. <see cref="Validate"/> turns the same
    /// sentence into a <see cref="ScanFilterValidationException"/> on the write path, which is the
    /// enforcement point behind both of them (design-lessons rule 2).
    /// </para>
    /// <para>
    /// Touches no filesystem: <c>Path.GetFullPath</c> is a pure string normalization here.
    /// </para>
    /// </remarks>
    public static string? RefuseScanRoot(string candidate, IReadOnlyList<string> existingRoots)
    {
        ArgumentNullException.ThrowIfNull(existingRoots);

        var trimmed = (candidate ?? "").Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            return $"'{trimmed}' is not a full path. A library folder must be an absolute path.";
        }

        string full;
        try
        {
            full = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return $"'{trimmed}' is not a valid path: {ex.Message}";
        }

        foreach (var existing in existingRoots)
        {
            if (TryFullPath(existing) is not { } existingFull)
            {
                continue;
            }

            if (!PathConfinement.IsUnderOrEqual(existingFull, full)
                && !PathConfinement.IsUnderOrEqual(full, existingFull))
            {
                continue;
            }

            return string.Equals(existingFull, full, StringComparison.OrdinalIgnoreCase)
                ? $"'{trimmed}' is already a library folder."
                : $"'{trimmed}' overlaps the library folder '{existing}'. Library folders may not be nested.";
        }

        return null;
    }

    /// <summary>Normalizes a path for comparison, or null when it is not one this application can
    /// use. The same order <see cref="RequireFullyQualified"/> uses, for the same trust-boundary
    /// reason (spec 2.3).</summary>
    public static string? TryFullPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    // Review items 1 and 2. Path.IsPathFullyQualified runs first and touches no filesystem
    // and never throws, so a relative, drive-relative ("C:foo"), or empty entry is rejected
    // by name before Path.GetFullPath ever sees it. The remaining Path.GetFullPath call can
    // still throw for an otherwise-qualified string with invalid characters (an embedded
    // NUL, for example): that is caught and translated too, so a malformed
    // general.scan_roots/scan_filters entry always surfaces as ScanFilterValidationException,
    // never as a raw framework exception out of SettingsStore.SaveGeneral.
    private static string RequireFullyQualified(string path, string fieldName)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ScanFilterValidationException($"{fieldName} entry '{path}' must be a fully qualified absolute path.");
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            throw new ScanFilterValidationException($"{fieldName} entry '{path}' is not a valid path: {ex.Message}");
        }
    }

    // spec 10.2 "Effective roots". Pure string transform, no filesystem access.
    public IReadOnlyList<string> EffectiveRoots(IReadOnlyList<string> scanRoots)
    {
        if (IncludePaths.Count == 0) return scanRoots;

        var full = IncludePaths.Select(Path.GetFullPath).ToList();
        var result = new List<string>();
        foreach (var path in full)
        {
            if (result.Any(r => PathsEqual(r, path))) continue; // exact duplicate already kept
            var nested = full.Any(other => !PathsEqual(other, path) && PathConfinement.IsUnderOrEqual(other, path));
            if (!nested) result.Add(path);
        }
        return result;
    }

    // spec 10.2 should_walk_dir. dirPath and root are ALREADY fully resolved (symlinks
    // followed) absolute paths -- resolving them is FileWalker's job (Task 3), not this
    // record's. Directory-level fallback matches Python exactly: a dir that cannot be
    // related to root still gets its own name checked as a single-segment fallback rather
    // than being excluded outright (folder pruning is defense-in-depth and must work even
    // for a dir reached through an include_path deep in the tree).
    public bool ShouldWalkDir(string dirPath, string root)
    {
        if (ExcludePaths.Any(p => PathConfinement.IsUnderOrEqual(Path.GetFullPath(p), dirPath))) return false;

        var segments = TryRelativeSegments(dirPath, root, includeLast: true) ?? [Path.GetFileName(dirPath)];

        foreach (var rule in NameRules)
        {
            if (rule.Target != "folder" || rule.Action != "exclude") continue;
            if (segments.Any(s => NameRuleMatcher.Matches(rule, s))) return false;
        }
        return true;
    }

    // spec 10.2 should_include_file. Six-step order, verbatim:
    // 1. exclude_paths -> excluded.
    // 2. include_paths narrowing (only when IncludePaths non-empty) -> excluded if not under one.
    // 3. relative-to-root split into segments (ancestor folders) + filename. UNLIKE
    //    ShouldWalkDir, a file that cannot be related to root is excluded outright here --
    //    no single-segment fallback for files. This is the one place a followed symlink
    //    that lands outside root is rejected (spec 10.1), provided FileWalker passed in the
    //    already-resolved real path.
    // 4. exclude name rules: file rule matching filename, or folder rule matching any
    //    segment -> excluded.
    // 5. include narrowing per target type: enabled file-include rules exist and none match
    //    filename -> excluded; enabled folder-include rules exist and none match any
    //    segment -> excluded.
    // 6. otherwise included.
    public bool ShouldIncludeFile(string filePath, string root)
    {
        if (ExcludePaths.Any(p => PathConfinement.IsUnderOrEqual(Path.GetFullPath(p), filePath))) return false;
        if (IncludePaths.Count > 0 && !IncludePaths.Any(p => PathConfinement.IsUnderOrEqual(Path.GetFullPath(p), filePath))) return false;

        var segments = TryRelativeSegments(filePath, root, includeLast: false);
        if (segments is null) return false;
        var filename = Path.GetFileName(filePath);

        foreach (var rule in NameRules)
        {
            if (rule.Action != "exclude") continue;
            if (rule.Target == "file" && NameRuleMatcher.Matches(rule, filename)) return false;
            if (rule.Target == "folder" && segments.Any(s => NameRuleMatcher.Matches(rule, s))) return false;
        }

        var fileIncludes = EnabledIncludeRules(target: "file");
        var folderIncludes = EnabledIncludeRules(target: "folder");
        if (fileIncludes.Count > 0 && !fileIncludes.Any(r => NameRuleMatcher.Matches(r, filename))) return false;
        if (folderIncludes.Count > 0 && !folderIncludes.Any(r => segments.Any(s => NameRuleMatcher.Matches(r, s)))) return false;

        return true;
    }

    // spec 10.2 test_path. Confinement uses Path.GetFullPath ONLY (no symlink resolution,
    // no disk access) against EffectiveRoots(scanRoots) -- tried in order, first containing
    // root wins -- before any Exists/IsDirectory check, so the tool cannot be used to probe
    // for the existence of arbitrary host paths. Kind resolution (auto) touches disk only
    // AFTER confinement succeeds. Rule evaluation after that mirrors
    // ShouldIncludeFile/ShouldWalkDir's segment/filename split, but collects every matching
    // include rule id (not just a first hit) and returns matched exclude-rule ids too.
    /// <summary>
    /// The one admission test for a single already-existing file path (review item 10): a
    /// supported frame format AND included by the spec 10.2 filter decision, with no disk
    /// access (the kind is stated). The watcher's debounce batch and the coordinator's
    /// targeted ingest both route through here, so a file one of them accepts is never one
    /// the other silently drops.
    /// </summary>
    public bool AcceptsFile(string path, IReadOnlyList<string> scanRoots)
        => FrameReader.IsSupported(path)
            && TestPath(path, scanRoots, PathKind.File).Verdict == "included";

    public TestPathResult TestPath(string path, IReadOnlyList<string> scanRoots, PathKind kind = PathKind.Auto)
    {
        string full;
        string root;
        try
        {
            full = Path.GetFullPath(path);
            // Review escalation 4: EffectiveRoots(scanRoots) -- include_paths when set,
            // else scanRoots themselves -- are what TryRelativeSegments below measures
            // folder/file segments against. This is deliberate, matching FileWalker (Task
            // 3): once include_paths narrows the effective roots, "relative to root" means
            // relative to the narrowed root, not the original scan root, for every
            // consumer of a root, not just confinement. scan_filters.py has only one
            // fits_root and does not need this distinction.
            var roots = EffectiveRoots(scanRoots);
            var found = roots.FirstOrDefault(r => PathConfinement.IsUnderOrEqual(Path.GetFullPath(r), full));
            if (found is null) return new TestPathResult("excluded_by_path", []);
            root = Path.GetFullPath(found);
        }
        catch (Exception)
        {
            // A malformed path (invalid characters, etc.) never reaches the filesystem;
            // it is reported the same as an out-of-root path rather than propagating.
            return new TestPathResult("excluded_by_path", []);
        }

        if (ExcludePaths.Any(p => PathConfinement.IsUnderOrEqual(Path.GetFullPath(p), full)))
            return new TestPathResult("excluded_by_path", []);

        var isFolder = kind switch
        {
            PathKind.File => false,
            PathKind.Folder => true,
            _ => UserFiles.DirectoryExists(full) ? true
                : UserFiles.Exists(full) ? false
                : string.IsNullOrEmpty(Path.GetExtension(full)),
        };

        var segments = TryRelativeSegments(full, root, includeLast: isFolder) ?? [];
        var filename = isFolder ? "" : Path.GetFileName(full);

        foreach (var rule in NameRules)
        {
            if (rule.Action != "exclude") continue;
            if (!isFolder && rule.Target == "file" && NameRuleMatcher.Matches(rule, filename))
                return new TestPathResult("excluded_by_rule", [rule.Id]);
            if (rule.Target == "folder" && segments.Any(s => NameRuleMatcher.Matches(rule, s)))
                return new TestPathResult("excluded_by_rule", [rule.Id]);
        }

        var fileIncludes = EnabledIncludeRules(target: "file");
        var folderIncludes = EnabledIncludeRules(target: "folder");
        var fileHits = isFolder
            ? new List<string>()
            : fileIncludes.Where(r => NameRuleMatcher.Matches(r, filename)).Select(r => r.Id).ToList();
        var folderHits = folderIncludes.Where(r => segments.Any(s => NameRuleMatcher.Matches(r, s))).Select(r => r.Id).ToList();

        if (!isFolder && fileIncludes.Count > 0 && fileHits.Count == 0)
            return new TestPathResult("excluded_by_missing_include", []);
        if (folderIncludes.Count > 0 && folderHits.Count == 0)
            return new TestPathResult("excluded_by_missing_include", []);

        return new TestPathResult("included", fileHits.Concat(folderHits).ToList());
    }

    // spec 10.2 step 5: "If any ENABLED file/folder include rules exist..." -- a disabled
    // include rule must not count toward "an include rule exists," or disabling the only
    // include rule would still exclude everything (the exact web-application bug this port
    // fixes, per the divergence note in NameRuleMatcher).
    private List<NameRule> EnabledIncludeRules(string target)
        => NameRules.Where(r => r.Enabled && r.Action == "include" && r.Target == target).ToList();

    private static bool PathsEqual(string a, string b)
        => string.Equals(TrimSep(a), TrimSep(b), StringComparison.OrdinalIgnoreCase);

    private static string TrimSep(string p) => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // null when path is not under root; otherwise the path components between root and the
    // leaf (folder case includes the leaf itself, file case excludes the filename --
    // callers split accordingly, matching Python's rel.parts[:-1] vs rel.parts).
    private static string[]? TryRelativeSegments(string path, string root, bool includeLast)
    {
        var fullRoot = Path.GetFullPath(root);
        var rel = Path.GetRelativePath(fullRoot, path);
        if (rel == ".") return [];
        if (rel.StartsWith("..") || Path.IsPathRooted(rel)) return null;

        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return includeLast || parts.Length == 0 ? parts : parts[..^1];
    }
}
