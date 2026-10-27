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
    /// several sources: the unselected frames of every ticked row whose folder
    /// <see cref="FolderLevels.Covers"/> says is copied, none when the row selects nothing (which
    /// copies them all). An unticked row contributes nothing: under the tree model it is never
    /// covered.</summary>
    public static IReadOnlyList<ScannedFrame> InPlaceExcludes(
        IReadOnlyList<ChosenLevel> sources, IReadOnlyList<(CalibrationRow Row, bool Ticked)> rows)
        => [.. rows.Where(r => r.Ticked && r.Row.SelectedCount > 0 && FolderLevels.Covers(sources, r.Row.Folder))
            .SelectMany(r => r.Row.Frames.Where(f => !f.Selected))
            .Select(f => f.Frame)
            .Where(f => CalibrationTypes.Contains(f.Type))];

    private static CalibrationTotals Tally(IReadOnlyList<(CalibrationRow Row, bool Ticked)> rows, IReadOnlyList<ScannedFrame> other)
    {
        var counted = rows.Where(r => r.Ticked)
            .SelectMany(r => r.Row.SelectedCount > 0 ? r.Row.Frames.Where(f => f.Selected) : r.Row.Frames)
            .Select(f => f.Frame)
            .Where(f => f.Type != CalibrationType.Light && f.Type != CalibrationType.Other)
            .ToList();

        (int, long) Sum(CalibrationType t) => (counted.Count(f => f.Type == t), counted.Where(f => f.Type == t).Sum(f => f.Size));
        var (fc, fb) = Sum(CalibrationType.Flat);
        var (dfc, dfb) = Sum(CalibrationType.DarkFlat);
        var (dc, db) = Sum(CalibrationType.Dark);
        var (bc, bb) = Sum(CalibrationType.Bias);
        return new CalibrationTotals(fc, fb, dfc, dfb, dc, db, bc, bb, other.Count, other.Sum(f => f.Size));
    }

    /// <summary>The totals over several sources: per type, the ticked rows' selected frames (all
    /// their frames when none is selected), counted once; <c>Other</c> is every Other or Light
    /// frame in <paramref name="allFrames"/> that <see cref="FolderLevels.Covers"/> says is
    /// copied.</summary>
    public static CalibrationTotals Totals(
        IReadOnlyList<ChosenLevel> sources, IReadOnlyList<(CalibrationRow Row, bool Ticked)> rows,
        IReadOnlyList<ScannedFrame> allFrames)
        => Tally(rows, [.. allFrames.Where(f => f.Type is CalibrationType.Other or CalibrationType.Light
                                                && FolderLevels.Covers(sources, f.FilePath))]);
}
