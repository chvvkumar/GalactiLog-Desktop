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
        DateOnly night, string entryName, string scannedLevel, IReadOnlyList<(CalibrationRow Row, bool Ticked)> rows,
        IReadOnlyList<ScannedFrame> allFrames)
    {
        var ops = new List<CopyOperation>();
        foreach (var (row, _) in rows.Where(r => r.Ticked).OrderBy(r => r.Row.Folder, StringComparer.OrdinalIgnoreCase))
        {
            var anySelected = row.SelectedCount > 0;
            // The copier recurses, so child folders are excluded: a row is only the files directly in its folder.
            var childDirs = allFrames
                .Where(f => PathConfinement.IsUnderOrEqual(row.Folder, f.FilePath) && !DirectlyIn(row.Folder, f.FilePath))
                .Select(f => Path.GetRelativePath(row.Folder, f.FilePath).Split(Path.DirectorySeparatorChar)[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Files directly in the folder that are not row frames (unparsable headers, sidecars) are
            // never counted by the footer, so no operation copies them.
            var inRow = new HashSet<string>(row.Frames.Select(f => f.Frame.FilePath), StringComparer.OrdinalIgnoreCase);
            var strays = allFrames
                .Where(f => !inRow.Contains(f.FilePath) && DirectlyIn(row.Folder, f.FilePath))
                .Select(f => Path.GetFileName(f.FilePath))
                .ToList();
            var held = row.Types;
            foreach (var type in CalibrationTypes.Where(held.Contains))
            {
                if (anySelected && !row.Frames.Any(f => f.Frame.Type == type && f.Selected)) continue;
                var excluded = row.Frames
                    .Where(f => f.Frame.Type != type || (anySelected && !f.Selected))
                    .Select(f => Path.GetFileName(f.Frame.FilePath))
                    .Concat(strays)
                    .ToList();
                ops.Add(new CopyOperation(night, row.Folder, entryName, excluded, childDirs.Count == 0 ? null : childDirs, DestinationSubfolder(scannedLevel, row.Folder, type)));
            }
        }
        return ops;
    }

    /// <summary>True for a row the lights operation copies in place: its folder lies under or equal
    /// to the chosen level and under none of that operation's skipped folders (the user's unticked
    /// folders and the folders the block owns). Such a row gets no calibration operation of its own;
    /// its unwanted frames are excluded one by one instead (<see cref="InPlaceExcludes"/>).</summary>
    public static bool CopiedInPlace(string chosenLevelPath, CalibrationRow row, IReadOnlyCollection<string> skippedFolders)
        => PathConfinement.IsUnderOrEqual(chosenLevelPath, row.Folder)
           && !skippedFolders.Any(skipped => PathConfinement.IsUnderOrEqual(skipped, row.Folder));

    /// <summary>True when <paramref name="path"/> sits directly in <paramref name="folder"/>.</summary>
    private static bool DirectlyIn(string folder, string path)
        => Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? "").Equals(
            Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase);

    /// <summary>The calibration frames the lights operation must skip in rows copied in place: the
    /// unselected ones of a ticked row (none when the row selects nothing, which copies them all),
    /// and every one of an unticked row.</summary>
    public static IReadOnlyList<ScannedFrame> InPlaceExcludes(
        string chosenLevelPath, IReadOnlyList<(CalibrationRow Row, bool Ticked)> rows, IReadOnlyCollection<string> skippedFolders)
        => [.. rows.Where(r => CopiedInPlace(chosenLevelPath, r.Row, skippedFolders))
            .SelectMany(r => r.Row.Frames.Where(f => !r.Ticked || (r.Row.SelectedCount > 0 && !f.Selected)))
            .Select(f => f.Frame)
            .Where(f => CalibrationTypes.Contains(f.Type))];

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
            // A LIGHT the caller does not know as catalogued is uncatalogued, and the lights
            // operation copies it like any other file.
            .Where(f => f.Type is CalibrationType.Other or CalibrationType.Light
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
