using GalactiLog.Core.Io;

namespace GalactiLog.Core.Wbpp;

/// <summary>Turns matched calibration rows and the user's ticks into the copy's per-file excludes
/// and totals over the tick model's sources. Pure logic, no I/O.</summary>
public static class CalibrationScan
{
    private static readonly CalibrationType[] CalibrationTypes =
        [CalibrationType.Flat, CalibrationType.DarkFlat, CalibrationType.Dark, CalibrationType.Bias];

    /// <summary>The longest <c>Path.GetRelativePath(folder, frame.FilePath)</c> length over the
    /// frames under <paramref name="folder"/>, 0 when none is under it.</summary>
    public static int LongestRelative(string folder, IReadOnlyList<ScannedFrame> frames)
        => frames.Where(f => PathConfinement.IsUnderOrEqual(folder, f.FilePath))
            .Select(f => Path.GetRelativePath(folder, f.FilePath).Length)
            .DefaultIfEmpty(0)
            .Max();

    /// <summary>The calibration frames the lights copy must skip in rows it copies in place over
    /// several sources, for every row whose folder <see cref="FolderLevels.Covers"/> says is
    /// copied: every calibration frame of a row whose decision is unticked (a folder that holds
    /// the night's lights is copied for them, and its rejected calibration stays behind), and the
    /// unselected frames of a ticked row that selects some (one that selects none copies them
    /// all).</summary>
    public static IReadOnlyList<ScannedFrame> InPlaceExcludes(
        IReadOnlyList<ChosenLevel> sources, IReadOnlyList<(CalibrationRow Row, bool Ticked)> rows)
        => [.. rows.Where(r => FolderLevels.Covers(sources, r.Row.Folder))
            .SelectMany(r => r.Ticked ? r.Row.Frames.Where(f => !f.Selected && r.Row.SelectedCount > 0) : r.Row.Frames)
            .Select(f => f.Frame)
            .Where(f => CalibrationTypes.Contains(f.Type))];

    /// <summary>The files the totals count over several sources: per ticked row its selected
    /// calibration frames (all of them when none is selected), then every Other or Light frame in
    /// <paramref name="allFrames"/> that <see cref="FolderLevels.Covers"/> says is copied.
    /// <see cref="Tally"/> sums them; a page hands it several nights' lists at once.</summary>
    public static IReadOnlyList<ScannedFrame> Counted(
        IReadOnlyList<ChosenLevel> sources, IReadOnlyList<(CalibrationRow Row, bool Ticked)> rows,
        IReadOnlyList<ScannedFrame> allFrames)
        =>
        [
            .. rows.Where(r => r.Ticked)
                .SelectMany(r => r.Row.SelectedCount > 0 ? r.Row.Frames.Where(f => f.Selected) : r.Row.Frames)
                .Select(f => f.Frame)
                .Where(f => CalibrationTypes.Contains(f.Type)),
            .. allFrames.Where(f => f.Type is CalibrationType.Other or CalibrationType.Light
                                    && FolderLevels.Covers(sources, f.FilePath)),
        ];

    /// <summary>The totals over <see cref="Counted"/> files, each file counted once by path
    /// (ignoring case), so a folder two nights share is counted once: per calibration type, and
    /// every Other or Light file as other.</summary>
    public static CalibrationTotals Tally(IEnumerable<ScannedFrame> counted)
    {
        var files = counted.DistinctBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
        (int, long) Sum(Func<CalibrationType, bool> type)
            => (files.Count(f => type(f.Type)), files.Where(f => type(f.Type)).Sum(f => f.Size));
        var (fc, fb) = Sum(t => t == CalibrationType.Flat);
        var (dfc, dfb) = Sum(t => t == CalibrationType.DarkFlat);
        var (dc, db) = Sum(t => t == CalibrationType.Dark);
        var (bc, bb) = Sum(t => t == CalibrationType.Bias);
        var (oc, ob) = Sum(t => t is CalibrationType.Other or CalibrationType.Light);
        return new CalibrationTotals(fc, fb, dfc, dfb, dc, db, bc, bb, oc, ob);
    }

    /// <summary>The totals over several sources: <see cref="Tally"/> of <see cref="Counted"/>.
    /// </summary>
    public static CalibrationTotals Totals(
        IReadOnlyList<ChosenLevel> sources, IReadOnlyList<(CalibrationRow Row, bool Ticked)> rows,
        IReadOnlyList<ScannedFrame> allFrames)
        => Tally(Counted(sources, rows, allFrames));
}
