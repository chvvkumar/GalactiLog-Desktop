namespace GalactiLog.Core.Wbpp;

/// <summary>One catalogued image path considered for a WBPP export (core-shapes.md section 4).
/// <paramref name="FileSize"/> is null when the row's own size is unknown.</summary>
public sealed record WbppFramePath(Guid ImageId, string FilePath, long? FileSize);

/// <summary>One <c>images</c> row as the contamination index consumes it (core-shapes.md section
/// 4). Covers every row, not only LIGHT frames with a session date, because the size index sums
/// over all of them.</summary>
public sealed record WbppCataloguePath(
    string FilePath,
    string TargetKey,
    string TargetName,
    DateOnly? Night,
    bool IsLight,
    long? FileSize);

/// <summary>One folder a night's frames could be exported from (core-shapes.md section 4).
/// <see cref="Path"/> is the absolute native folder path; there is no second, relative form of it
/// in this phase.</summary>
/// <param name="OtherTargets">Display names of other targets whose frames also lie under this
/// folder.</param>
/// <param name="OtherNights">Other nights' frames lying under this folder, as invariant
/// <c>yyyy-MM-dd</c> strings because the folder picker presents and sorts them as text.</param>
/// <param name="SubtreeBytes">The total size of every catalogued file under this folder, of any
/// target and any frame type, filled by <c>FolderLevels.SubtreeBytes</c>; null when any one of
/// those rows has a null size.</param>
public sealed record FolderLevel(
    string Path,
    int DepthFromRoot,
    int FrameCount,
    IReadOnlyList<string> OtherTargets,
    IReadOnlyList<string> OtherNights,
    long? SubtreeBytes)
{
    /// <summary>True when another target's or another night's frames also lie under this folder.
    /// Derived from <see cref="OtherTargets"/> and <see cref="OtherNights"/> alone, so it cannot be
    /// set to disagree with them (<c>Phd2SessionSummary.Gated</c> is the house precedent).</summary>
    public bool IsContaminated => OtherTargets.Count > 0 || OtherNights.Count > 0;
}

/// <summary>Why a session's folder levels could not be built, when they could not
/// (core-shapes.md section 4).</summary>
public enum LevelsUnavailable { NoFrames, NoScanRoot, SeveralScanRoots, FramesInRootItself }

/// <summary>One night's candidate export folders (core-shapes.md section 4). A non-null
/// <see cref="Unavailable"/> means <see cref="Levels"/> is empty, <see cref="ScanRoot"/> is
/// <c>""</c> and <see cref="DefaultLevelIndex"/> means nothing; no caller may index
/// <see cref="Levels"/> without checking <see cref="Unavailable"/> first.</summary>
/// <param name="FramesWithoutRoot">The night's frames lying under no configured scan root, which
/// contribute no chain, no level and no count.</param>
/// <param name="FramesInRootItself">The night's frames whose parent is the scan root itself, which
/// have no ancestor chain and so lie under no level, exactly as the Python's
/// <c>compute_ancestor_chain</c> leaves them. A night with some such frames and others deeper keeps
/// its levels, so this figure is what accounts for a level count below the night's own
/// <see cref="TotalFrameCount"/>; a night where every frame is in that state reports
/// <see cref="LevelsUnavailable.FramesInRootItself"/> instead and has no levels at all.</param>
public sealed record SessionLevels(
    DateOnly Night,
    string ScanRoot,
    IReadOnlyList<FolderLevel> Levels,
    int DefaultLevelIndex,
    int TotalFrameCount,
    int FramesWithoutRoot,
    LevelsUnavailable? Unavailable,
    int FramesInRootItself = 0);

/// <summary>One night's picked export folder (core-shapes.md section 4).</summary>
public sealed record ChosenLevel(DateOnly Night, FolderLevel Level);

/// <summary>One night's worth of a WBPP export script's copy step (core-shapes.md section 4).
/// <paramref name="ExcludedRelativePaths"/> carries native <c>\</c> separators, relative to
/// <paramref name="SourcePath"/>.</summary>
public sealed record CopyOperation(
    DateOnly Night,
    string SourcePath,
    string EntryName,
    IReadOnlyList<string> ExcludedRelativePaths);

/// <summary>The export's summary counts, over the chosen levels and their excluded frames
/// (core-shapes.md section 4). <paramref name="SizeBytes"/> is null when any counted file's size
/// is unknown.</summary>
public sealed record ExportTotals(int FrameCount, int FolderCount, long? SizeBytes);

/// <summary>A frame's measured figure the quality filter grades (core-shapes.md section 6).</summary>
public enum WbppMetric { Hfr, Ecc, Fwhm, Stars, Rms }

/// <summary>A raw constraint's comparison, the port of the web's <c>"lte"</c> and <c>"gte"</c>
/// (core-shapes.md section 6).</summary>
public enum ConstraintOp { AtMost, AtLeast }

/// <summary>Which <c>FrameQuality</c> baseline the panel's cell colours grade against. Never read
/// by the verdict path itself, which is absolute thresholds over the frame's own values
/// (core-shapes.md section 6).</summary>
public enum QualityBaseline { Session, Rig }

/// <summary>A frame's quality verdict, the port of the web's <c>"pass"</c>, <c>"fail"</c> and
/// <c>"unmeasured"</c> (core-shapes.md section 6).</summary>
public enum Verdict { Copy, Exclude, Unmeasured }

/// <summary>One user-set threshold on one metric (core-shapes.md section 6). <paramref
/// name="Value"/> is null and <paramref name="Enabled"/> is false for an unset row.</summary>
public sealed record RawConstraint(WbppMetric Metric, ConstraintOp Op, double? Value, bool Enabled);

/// <summary>One frame as the quality filter reads it (core-shapes.md section 6).</summary>
/// <param name="Rig">The port's one spelling of the rig label, copied from
/// <c>SessionDetailQuery.FrameRow.Rig</c>. Never null or empty; <c>"Unknown / Unknown"</c> is a
/// legitimate value.</param>
/// <param name="Telescope">Kept only for the cell-grading side's <c>FrameQuality.GroupKey</c>;
/// never used to build a rig key.</param>
/// <param name="Camera">Kept only for the cell-grading side's <c>FrameQuality.GroupKey</c>; never
/// used to build a rig key.</param>
public sealed record WbppFrame(
    Guid ImageId,
    DateOnly Night,
    string FilePath,
    string FileName,
    DateTime? CaptureDate,
    string? FilterUsed,
    string Rig,
    string? Telescope,
    string? Camera,
    double? MedianHfr,
    double? Eccentricity,
    double? Fwhm,
    int? DetectedStars,
    double? GuidingRmsArcsec);

/// <summary>One metric that failed a frame's constraint, with its already-formatted text
/// (core-shapes.md section 6).</summary>
public sealed record MetricFailure(WbppMetric Metric, string Text);

/// <summary>One frame's verdict and, when excluded, why (core-shapes.md section 6).
/// <paramref name="FailedBy"/> is null and <paramref name="Failures"/> is empty on a
/// <see cref="Verdict.Copy"/> or <see cref="Verdict.Unmeasured"/> verdict.</summary>
public sealed record FrameVerdict(
    WbppFrame Frame,
    Verdict Verdict,
    string? FailedBy,
    IReadOnlyList<MetricFailure> Failures);

/// <summary>The selection's summary counts (core-shapes.md section 6). <paramref name="Copy"/>,
/// <paramref name="Exclude"/> and <paramref name="Unmeasured"/> always sum to <paramref
/// name="Total"/>; <paramref name="Overridden"/> is reported beside them and is not part of that
/// sum.</summary>
public sealed record QualityTotals(int Total, int Copy, int Exclude, int Unmeasured, int Overridden);

/// <summary>One rig's stored quality filter state (core-shapes.md section 6, user ruling 2 at the
/// 12.13 gate).</summary>
public sealed record WbppQualityState(
    bool Enabled,
    QualityBaseline Baseline,
    IReadOnlyList<RawConstraint> Constraints)
{
    /// <summary>Off, <see cref="QualityBaseline.Session"/>, no constraints: the value a rig with no
    /// stored entry reads as.</summary>
    public static WbppQualityState Default { get; } = new(false, QualityBaseline.Session, []);
}

/// <summary>The quality filter state of every rig, keyed by <c>WbppFrame.Rig</c> under
/// <see cref="DefaultRigKey"/> for a rigless selection (core-shapes.md section 6). Declared
/// <c>partial</c> so <c>WbppSettingsRead.cs</c> can add <c>For</c>, whose blank-key normalisation and
/// lookup fallback are real logic, without a second edit to this file.</summary>
public sealed partial record WbppQualityByRig(IReadOnlyDictionary<string, WbppQualityState> Rigs)
{
    /// <summary>The rig key a rigless selection, or a selection with no frame at all, stores and
    /// reads under.</summary>
    public const string DefaultRigKey = "default";
}

/// <summary>Which shell a generated WBPP export script targets (core-shapes.md section 7).</summary>
public enum WbppScriptType { PowerShell, Bash }

/// <summary>Everything <c>ScriptGenerator.Generate</c> needs to render one script
/// (core-shapes.md section 7). <paramref name="StagingRoot"/> is required and never derived; the
/// generator holds no default.</summary>
public sealed record WbppScriptInput(
    IReadOnlyList<CopyOperation> Operations,
    string StagingRoot,
    string TargetName,
    IReadOnlyList<string> Exclusions,
    string FileName);
