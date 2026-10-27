using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.Core.Tests.Wbpp;

public class CalibrationScanTests
{
    private static readonly DateOnly N = new(2025, 10, 15);
    private static readonly FrameKeys Keys = new("Cam", "Ha", 1, 110, 50, "SQA55", 128.85, null);

    private static FrameMatch M(string path, CalibrationType type, bool selected, long size = 100) =>
        new(new ScannedFrame(path, size, type, Keys, 0.01, N), selected, "");

    private static CalibrationRow Row(string folder, params FrameMatch[] frames) =>
        new(folder, frames, frames.Any(f => f.Selected), "");

    [Fact]
    public void CopySources_OnePerTickedRow_WithTheUnselectedFramesExcluded()
    {
        var row = Row(@"W:\N\FLAT",
            M(@"W:\N\FLAT\f1.fits", CalibrationType.Flat, true), M(@"W:\N\FLAT\f2.fits", CalibrationType.Flat, true),
            M(@"W:\N\FLAT\f3.fits", CalibrationType.Flat, false), M(@"W:\N\FLAT\df1.fits", CalibrationType.DarkFlat, true));

        var source = Assert.Single(CalibrationScan.CopySources(N, @"W:\", [(row, true)], []));

        Assert.Equal(N, source.Night);
        Assert.Equal(@"W:\", source.ScanRoot);
        Assert.Equal(@"W:\N\FLAT", source.SourcePath);
        Assert.Equal(["f3.fits"], source.ExcludedRelativePaths);
        Assert.Null(source.ExcludedRelativeDirectories);
    }

    [Fact]
    public void CopySources_ExcludeUnselectedFilesInsideATickedRow()
    {
        var row = Row(@"W:\N\FLAT\Cam",
            M(@"W:\N\FLAT\Cam\a_128.85.fits", CalibrationType.Flat, true), M(@"W:\N\FLAT\Cam\b_128.85.fits", CalibrationType.Flat, true),
            M(@"W:\N\FLAT\Cam\c_270.00.fits", CalibrationType.Flat, false));

        Assert.Equal(["c_270.00.fits"], Assert.Single(CalibrationScan.CopySources(N, @"W:\", [(row, true)], [])).ExcludedRelativePaths);
    }

    [Fact]
    public void CopySources_ATickedRowWithNoSelection_CopiesEveryCalibrationFrame_AndLeavesLightsAndOthers()
    {
        var row = Row(@"W:\N\Dark",
            M(@"W:\N\Dark\d1.fits", CalibrationType.Dark, false), M(@"W:\N\Dark\d2.fits", CalibrationType.Dark, false),
            M(@"W:\N\Dark\b1.fits", CalibrationType.Bias, false), M(@"W:\N\Dark\l1.fits", CalibrationType.Light, false),
            M(@"W:\N\Dark\o1.fits", CalibrationType.Other, false));

        Assert.Equal(["l1.fits", "o1.fits"], Assert.Single(CalibrationScan.CopySources(N, @"W:\", [(row, true)], [])).ExcludedRelativePaths);
    }

    [Fact]
    public void CopySources_ExcludeTheRowFoldersFilesThatAreNotInTheRow()
    {
        // An unparsable header or a sidecar beside the flats is scanned as Other and never joins the
        // row, so the footer does not count it; the operation must not copy it either.
        var row = Row(@"W:\N\FLAT", M(@"W:\N\FLAT\f1.fits", CalibrationType.Flat, true));
        var all = new[]
        {
            new ScannedFrame(@"W:\N\FLAT\f1.fits", 100, CalibrationType.Flat, Keys, 0.01, N),
            new ScannedFrame(@"W:\N\FLAT\broken.fits", 100, CalibrationType.Other, Keys, null, null),
            new ScannedFrame(@"W:\N\FLAT\notes.txt", 5, CalibrationType.Other, Keys, null, null),
            new ScannedFrame(@"W:\N\Other\x.fits", 5, CalibrationType.Other, Keys, null, null),
        };
        var op = Assert.Single(CalibrationScan.CopySources(N, @"W:\", [(row, true)], all));
        Assert.Equal(["broken.fits", "notes.txt"], op.ExcludedRelativePaths);
    }

    [Fact]
    public void CopiedInPlace_ForARowUnderOrAtTheChosenLevel_UnderNoSkippedFolder()
    {
        var row = Row(@"W:\T\2025-10-15", M(@"W:\T\2025-10-15\f1.fits", CalibrationType.Flat, true));
        // The chosen folder itself, lights directly in it or only in a subfolder.
        Assert.True(CalibrationScan.CopiedInPlace(@"W:\T\2025-10-15", row, []));
        Assert.True(CalibrationScan.CopiedInPlace(@"W:\T", row, []));
        Assert.True(CalibrationScan.CopiedInPlace(@"W:\T", row, [@"W:\T\2025-10-14"]));
        // Outside the chosen level, or left out of the lights operation whole.
        Assert.False(CalibrationScan.CopiedInPlace(@"W:\T\2025-10-15\Ha", row, []));
        Assert.False(CalibrationScan.CopiedInPlace(@"W:\T", row, [@"W:\T\2025-10-15"]));
        Assert.False(CalibrationScan.CopiedInPlace(@"W:\", row, [@"W:\T"]));
    }

    [Fact]
    public void InPlaceExcludes_UnselectedFramesOfATickedRow_AllOfAnUntickedOne_NothingElsewhere()
    {
        const string Dir = @"W:\T\2025-10-15";
        var mixed = Row(Dir,
            M(Dir + @"\f1.fits", CalibrationType.Flat, true), M(Dir + @"\f2.fits", CalibrationType.Flat, false));
        var none = Row(Dir, M(Dir + @"\f1.fits", CalibrationType.Flat, false), M(Dir + @"\f2.fits", CalibrationType.Flat, false));
        var elsewhere = Row(@"W:\T\FLAT", M(@"W:\T\FLAT\g.fits", CalibrationType.Flat, false));
        var owned = Row(Dir + @"\FLAT", M(Dir + @"\FLAT\h.fits", CalibrationType.Flat, false));

        Assert.Equal([Dir + @"\f2.fits"], CalibrationScan.InPlaceExcludes(Dir, [(mixed, true), (elsewhere, false)], []).Select(f => f.FilePath));
        Assert.Equal([Dir + @"\f1.fits", Dir + @"\f2.fits"], CalibrationScan.InPlaceExcludes(Dir, [(mixed, false)], []).Select(f => f.FilePath));
        Assert.Empty(CalibrationScan.InPlaceExcludes(Dir, [(none, true)], []));
        // A row in a skipped (owned) folder has its own operation, so nothing is excluded for it.
        Assert.Empty(CalibrationScan.InPlaceExcludes(Dir, [(owned, false)], [Dir + @"\FLAT"]));
    }

    [Fact]
    public void Totals_CountAnUncataloguedLightUnderTheChosenLevelAsOther()
    {
        var all = new[]
        {
            new ScannedFrame(@"W:\N\LIGHT\stray.fits", 7, CalibrationType.Light, Keys, null, N),
            new ScannedFrame(@"W:\N\LIGHT\notes.txt", 3, CalibrationType.Other, Keys, null, null),
            new ScannedFrame(@"W:\N\skip\x.fits", 9, CalibrationType.Light, Keys, null, N),
            new ScannedFrame(@"W:\Elsewhere\y.fits", 11, CalibrationType.Light, Keys, null, N),
        };
        var totals = CalibrationScan.Totals(@"W:\N", [], all, [@"W:\N\skip"]);
        Assert.Equal(2, totals.OtherCount);
        Assert.Equal(10, totals.OtherBytes);
    }

    [Fact]
    public void CopySources_UntickedRows_ProduceNothing()
    {
        var row = Row(@"W:\N\FLAT", M(@"W:\N\FLAT\f1.fits", CalibrationType.Flat, true));
        Assert.Empty(CalibrationScan.CopySources(N, @"W:\", [(row, false)], []));
    }

    [Fact]
    public void CopySources_AreOrderedByFolder()
    {
        var b = Row(@"W:\N\B", M(@"W:\N\B\f.fits", CalibrationType.Flat, true));
        var a = Row(@"W:\N\A", M(@"W:\N\A\b.fits", CalibrationType.Bias, true), M(@"W:\N\A\d.fits", CalibrationType.Dark, true));

        Assert.Equal([@"W:\N\A", @"W:\N\B"], CalibrationScan.CopySources(N, @"W:\", [(b, true), (a, true)], []).Select(s => s.SourcePath));
    }

    [Fact]
    public void CopySources_ExcludeChildDirectoriesOfTheRowFolder()
    {
        var row = Row(@"W:\FLAT", M(@"W:\FLAT\a.fits", CalibrationType.Flat, true));
        ScannedFrame S(string p) => new(p, 1, CalibrationType.Flat, Keys, null, N);
        var all = new[] { S(@"W:\FLAT\a.fits"), S(@"W:\FLAT\old\a.fits"), S(@"W:\FLAT\rejects\deep\b.fits"), S(@"W:\FLAT\Old\c.fits"), S(@"W:\FLAT2\d.fits") };
        var op = Assert.Single(CalibrationScan.CopySources(N, @"W:\", [(row, true)], all));
        Assert.Equal(["old", "rejects"], op.ExcludedRelativeDirectories);
    }

    [Fact]
    public void CopySources_ExcludeAnUnselectedFrameOfAnotherType()
    {
        var row = Row(@"W:\N\FLAT", M(@"W:\N\FLAT\f1.fits", CalibrationType.Flat, true), M(@"W:\N\FLAT\df1.fits", CalibrationType.DarkFlat, false));

        Assert.Equal(["df1.fits"], Assert.Single(CalibrationScan.CopySources(N, @"W:\", [(row, true)], [])).ExcludedRelativePaths);
    }

    [Fact]
    public void LongestRelative_IsTheLongestPathUnderTheFolder_AndZeroWhenNoneIsUnderIt()
    {
        ScannedFrame S(string p) => new(p, 1, CalibrationType.Flat, Keys, null, N);
        var frames = new[] { S(@"W:\N\FLAT\f1.fits"), S(@"W:\N\FLAT\sub\longer_name.fits"), S(@"W:\N\FLAT2\x.fits") };

        Assert.Equal(@"sub\longer_name.fits".Length, CalibrationScan.LongestRelative(@"W:\N\FLAT", frames));
        Assert.Equal(0, CalibrationScan.LongestRelative(@"W:\N\DARK", frames));
    }

    [Fact]
    public void OwnedFolders_ListsRowsUnderTheChosenLevel_OnlyWhenStrictlyUnder()
    {
        var rows = new[]
        {
            Row(@"W:\N", M(@"W:\N\a.fits", CalibrationType.Flat, true, 5)),
            Row(@"W:\N\FLAT", M(@"W:\N\FLAT\a.fits", CalibrationType.Flat, true, 7), M(@"W:\N\FLAT\b.fits", CalibrationType.Flat, true, 8)),
            Row(@"W:\N2\FLAT", M(@"W:\N2\FLAT\a.fits", CalibrationType.Flat, true)),
        };
        var one = Assert.Single(CalibrationScan.OwnedFolders(@"W:\N\", rows));
        Assert.Equal(@"W:\N\FLAT", one.Path);
        Assert.Equal(15, one.SubtreeBytes);
    }

    [Fact]
    public void Totals_SumSelectedPerType_AndOtherFilesUnderTheChosenLevelOutsideSkips()
    {
        var flats = Row(@"W:\N\FLAT",
            M(@"W:\N\FLAT\f1.fits", CalibrationType.Flat, true, 10), M(@"W:\N\FLAT\f2.fits", CalibrationType.Flat, false, 20),
            M(@"W:\N\FLAT\df.fits", CalibrationType.DarkFlat, true, 30));
        var darks = Row(@"W:\N\DARK", M(@"W:\N\DARK\d1.fits", CalibrationType.Dark, false, 40), M(@"W:\N\DARK\d2.fits", CalibrationType.Dark, false, 50));
        var unticked = Row(@"W:\N\BIAS", M(@"W:\N\BIAS\b.fits", CalibrationType.Bias, false, 60));
        ScannedFrame O(string p, long s) => new(p, s, CalibrationType.Other, Keys, null, N);
        var all = new[]
        {
            O(@"W:\N\x.fits", 1), O(@"W:\N\Skip\y.fits", 2), O(@"W:\N\sub\z.fits", 4), O(@"W:\Elsewhere\w.fits", 8),
        };
        var t = CalibrationScan.Totals(@"W:\N", [(flats, true), (darks, true), (unticked, false)], all, [@"W:\N\Skip"]);
        Assert.Equal(new CalibrationTotals(1, 10, 1, 30, 2, 90, 0, 0, 2, 5), t);
        Assert.Equal(6, t.Count);
        Assert.Equal(135, t.Bytes);
    }

    private const string Date18 = @"W:\Astro\Incoming\2026\Date_2026-10-18";
    private const string Flats = Date18 + @"\FLAT\ZWO ASI533MM Pro\Angle_45.18";
    private const string Flats96 = Date18 + @"\FLAT\ZWO ASI533MM Pro\Angle_96.26";
    private const string Borrowed = @"W:\Astro\Incoming\2026\Date_2026-10-15\FLAT\ZWO ASI533MM Pro\Angle_45.18";

    private static ChosenLevel Source(string path, params string[] skipped)
        => new(N, new FolderLevel(path, 2, 0, [], [], 0), skipped.Length == 0 ? null : [.. skipped.Select(s => new FolderLevel(s, 5, 0, [], [], 0))]);

    [Fact]
    public void InPlaceExcludes_OverSources_TrimsEveryCoveredTickedRowToItsSelection()
    {
        var flats = Row(Flats, M(Flats + @"\f1.fits", CalibrationType.Flat, true), M(Flats + @"\f2.fits", CalibrationType.Flat, true), M(Flats + @"\h1.fits", CalibrationType.Flat, false));
        var rejected = Row(Flats96, M(Flats96 + @"\g1.fits", CalibrationType.Flat, true), M(Flats96 + @"\g2.fits", CalibrationType.Flat, false));
        var borrowed = Row(Borrowed, M(Borrowed + @"\b1.fits", CalibrationType.Flat, true), M(Borrowed + @"\b2.fits", CalibrationType.Flat, false));
        var sources = new[] { Source(Date18, Flats96), Source(Borrowed) };

        var excluded = CalibrationScan.InPlaceExcludes(sources, [(flats, true), (rejected, true), (borrowed, true)]).Select(f => f.FilePath);

        // h1 is trimmed inside the date source, b2 inside the borrowed source; the rejected row lies under an excluded directory and is not covered.
        Assert.Equal([Flats + @"\h1.fits", Borrowed + @"\b2.fits"], excluded);
        Assert.Empty(CalibrationScan.InPlaceExcludes(sources, [(flats, false)]));
        Assert.Empty(CalibrationScan.InPlaceExcludes(sources, [(Row(Flats, M(Flats + @"\f1.fits", CalibrationType.Flat, false)), true)]));
    }

    [Fact]
    public void Totals_OverSources_CountTickedRowsOnce_AndOtherFilesUnderAnySourceOutsideItsExcludedDirectories()
    {
        var flats = Row(Flats, M(Flats + @"\f1.fits", CalibrationType.Flat, true), M(Flats + @"\f2.fits", CalibrationType.Flat, true), M(Flats + @"\h1.fits", CalibrationType.Flat, false));
        var rejected = Row(Flats96, M(Flats96 + @"\g1.fits", CalibrationType.Flat, false));
        var borrowed = Row(Borrowed, M(Borrowed + @"\b1.fits", CalibrationType.Flat, true), M(Borrowed + @"\b2.fits", CalibrationType.Flat, false));
        ScannedFrame Other(string path, long size) => new(path, size, CalibrationType.Other, Keys, null, null);
        var all = new List<ScannedFrame>(flats.Frames.Concat(rejected.Frames).Concat(borrowed.Frames).Select(f => f.Frame))
        {
            Other(Date18 + @"\LIGHT\ZWO ASI533MM Pro\Target_Pleiades\Angle_45.18\L1.json", 50),
            Other(Flats96 + @"\notes.txt", 7),
            Other(@"W:\Astro\Incoming\2026\Date_2026-10-15\readme.txt", 11),
            Other(Borrowed + @"\b.xml", 9),
        };

        var totals = CalibrationScan.Totals([Source(Date18, Flats96), Source(Borrowed)], [(flats, true), (rejected, false), (borrowed, true)], all);

        Assert.Equal(3, totals.FlatCount);
        Assert.Equal(300, totals.FlatBytes);
        Assert.Equal(0, totals.DarkFlatCount);
        Assert.Equal(2, totals.OtherCount);
        Assert.Equal(59, totals.OtherBytes);
    }
}
