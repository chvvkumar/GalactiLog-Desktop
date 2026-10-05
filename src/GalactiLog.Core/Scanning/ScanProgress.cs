namespace GalactiLog.Core.Scanning;

// The scan progress envelope (spec 10.4). Immutable, and safe to hand between threads: the
// coordinator raises it from whatever thread reached the reporting point, and the App
// subscriber marshals to the Avalonia dispatcher (spec 4.2).
//
// `Percent` is derived at read time and never stored, so a phase with no fixed total
// (TotalSteps = 0) reports 0 rather than dividing by zero; its running count lives in
// `Message`.
public sealed record ScanProgress(string Task, int Step, int TotalSteps, string Message)
{
    public double Percent => TotalSteps > 0 ? 100.0 * Step / TotalSteps : 0.0;
}

// The closed task vocabulary of spec 10.4, in the order a full scan emits them.
// `prune_orphans` and `prune_activity` are deliberately separate names: one shared `prune`
// made a status bar reading "prune 12/400" ambiguous.
public static class ScanTaskNames
{
    public const string Discovery = "discovery";
    public const string Classify = "classify";
    public const string Ingest = "ingest";
    public const string PruneOrphans = "prune_orphans";

    /// <summary>Reading and storing discovered PHD2 guide logs (spec 10.3 step 5). Its
    /// <c>TotalSteps</c> is the discovered guide-log count, and a pass that found none still
    /// emits it once with 0, so a reader can tell "the pass ran and found nothing" from "the
    /// pass did not run". Emitted only when <c>general.phd2_scan_enabled</c> is true.</summary>
    public const string Phd2Ingest = "phd2_ingest";

    /// <summary>Filling frame guiding RMS from the stored guide frames (spec 7.6). Emitted only
    /// when <c>general.phd2_scan_enabled</c> is true.</summary>
    public const string Phd2Correlate = "phd2_correlate";

    public const string Dedup = "dedup";
    public const string RefThumbnails = "ref_thumbnails";
    public const string PruneActivity = "prune_activity";

    public static readonly IReadOnlyList<string> All =
    [
        Discovery, Classify, Ingest, PruneOrphans, Phd2Ingest, Phd2Correlate, Dedup,
        RefThumbnails, PruneActivity,
    ];
}
