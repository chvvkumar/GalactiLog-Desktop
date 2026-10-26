using GalactiLog.Core.Io;

namespace GalactiLog.Core.Wbpp;

/// <summary>Turns matched calibration rows and the user's ticks into copy operations and totals.
/// Pure logic, no I/O.</summary>
public static class CalibrationScan
{
    private static readonly CalibrationType[] CalibrationTypes =
        [CalibrationType.Flat, CalibrationType.DarkFlat, CalibrationType.Dark, CalibrationType.Bias];

    public static string TypeFolderName(CalibrationType type) => type switch
    {
        CalibrationType.Flat => "FLAT",
        CalibrationType.DarkFlat => "DARKFLAT",
        CalibrationType.Dark => "DARK",
        CalibrationType.Bias => "BIAS",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Only calibration types have a folder."),
    };

    /// <summary>The type folder, then the row folder's path below the scanned level, with that
    /// path's first segment dropped when it already names the type folder.</summary>
    public static string DestinationSubfolder(string scannedLevel, string folder, CalibrationType type)
    {
        var typeFolder = TypeFolderName(type);
        var relative = Path.GetRelativePath(scannedLevel, folder);
        if (relative == ".") return typeFolder;

        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var rest = segments.Length > 0 && segments[0].Equals(typeFolder, StringComparison.OrdinalIgnoreCase)
            ? segments[1..]
            : segments;
        return rest.Length == 0
            ? typeFolder
            : typeFolder + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, rest);
    }

    public static IReadOnlyList<CopyOperation> Operations(
        DateOnly night, string entryName, string scannedLevel, IReadOnlyList<(CalibrationRow Row, bool Ticked)> rows)
    {
        var ops = new List<CopyOperation>();
        foreach (var (row, _) in rows.Where(r => r.Ticked).OrderBy(r => r.Row.Folder, StringComparer.OrdinalIgnoreCase))
        {
            var anySelected = row.SelectedCount > 0;
            var held = row.Types;
            foreach (var type in CalibrationTypes.Where(held.Contains))
            {
                var excluded = row.Frames
                    .Where(f => f.Frame.Type != type || (anySelected && !f.Selected))
                    .Select(f => Path.GetFileName(f.Frame.FilePath))
                    .ToList();
                ops.Add(new CopyOperation(night, row.Folder, entryName, excluded, null, DestinationSubfolder(scannedLevel, row.Folder, type)));
            }
        }
        return ops;
    }

    /// <summary>Rows strictly under the chosen level: the edge rule's directory-exclude list.</summary>
    public static IReadOnlyList<FolderLevel> OwnedFolders(string chosenLevelPath, IReadOnlyList<CalibrationRow> rows) =>
        rows.Where(r => PathConfinement.IsUnderOrEqual(chosenLevelPath, r.Folder)
                        && !Path.TrimEndingDirectorySeparator(r.Folder).Equals(
                            Path.TrimEndingDirectorySeparator(chosenLevelPath), StringComparison.OrdinalIgnoreCase))
            .Select(r => new FolderLevel(r.Folder, 0, 0, [], [], r.TotalBytes))
            .ToList();

    public static CalibrationTotals Totals(
        string chosenLevelPath, IReadOnlyList<(CalibrationRow Row, bool Ticked)> rows,
        IReadOnlyList<ScannedFrame> allFrames, IReadOnlyList<string> skippedFolders)
    {
        var counted = rows.Where(r => r.Ticked)
            .SelectMany(r => r.Row.SelectedCount > 0 ? r.Row.Frames.Where(f => f.Selected) : r.Row.Frames)
            .Select(f => f.Frame)
            .Where(f => f.Type != CalibrationType.Light && f.Type != CalibrationType.Other)
            .ToList();
        var other = allFrames
            .Where(f => f.Type == CalibrationType.Other
                        && PathConfinement.IsUnderOrEqual(chosenLevelPath, f.FilePath)
                        && !skippedFolders.Any(s => PathConfinement.IsUnderOrEqual(s, f.FilePath)))
            .ToList();

        (int, long) Sum(CalibrationType t) => (counted.Count(f => f.Type == t), counted.Where(f => f.Type == t).Sum(f => f.Size));
        var (fc, fb) = Sum(CalibrationType.Flat);
        var (dfc, dfb) = Sum(CalibrationType.DarkFlat);
        var (dc, db) = Sum(CalibrationType.Dark);
        var (bc, bb) = Sum(CalibrationType.Bias);
        return new CalibrationTotals(fc, fb, dfc, dfb, dc, db, bc, bb, other.Count, other.Sum(f => f.Size));
    }
}
