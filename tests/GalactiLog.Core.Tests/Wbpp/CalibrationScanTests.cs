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

    [Theory]
    [InlineData(@"W:\A\Date_X", @"W:\A\Date_X\FLAT\Cam\Angle_1", CalibrationType.Flat, @"FLAT\Cam\Angle_1")]
    [InlineData(@"W:\A\Plan", @"W:\A\Plan\Flat", CalibrationType.DarkFlat, @"DARKFLAT\Flat")]
    [InlineData(@"W:\A\Plan", @"W:\A\Plan\Flat", CalibrationType.Flat, "FLAT")]
    [InlineData(@"W:\A\Date_X", @"W:\A\Date_X", CalibrationType.Bias, "BIAS")]
    public void DestinationSubfolder_FollowsTheTypeThenTheRelativePath(string level, string folder, CalibrationType type, string expected)
        => Assert.Equal(expected, CalibrationScan.DestinationSubfolder(level, folder, type));

    [Fact]
    public void Operations_OnePerTickedRowPerType_WithUnselectedAndOtherTypeFilesExcluded()
    {
        var row = Row(@"W:\N\FLAT",
            M(@"W:\N\FLAT\f1.fits", CalibrationType.Flat, true), M(@"W:\N\FLAT\f2.fits", CalibrationType.Flat, true),
            M(@"W:\N\FLAT\f3.fits", CalibrationType.Flat, false), M(@"W:\N\FLAT\df1.fits", CalibrationType.DarkFlat, true));
        var ops = CalibrationScan.Operations(N, "E", @"W:\N", [(row, true)], []);
        Assert.Equal(2, ops.Count);
        Assert.Equal("FLAT", ops[0].DestinationSubfolder);
        Assert.Equal(["f3.fits", "df1.fits"], ops[0].ExcludedRelativePaths);
        Assert.Equal(@"DARKFLAT\FLAT", ops[1].DestinationSubfolder);
        Assert.Equal(["f1.fits", "f2.fits", "f3.fits"], ops[1].ExcludedRelativePaths);
        Assert.All(ops, o => { Assert.Equal(@"W:\N\FLAT", o.SourcePath); Assert.Equal("E", o.EntryName); Assert.Null(o.ExcludedRelativeDirectories); });
    }

    [Fact]
    public void Operations_ExcludesUnselectedFilesInsideATickedRow()
    {
        var row = Row(@"W:\N\FLAT\Cam",
            M(@"W:\N\FLAT\Cam\a_128.85.fits", CalibrationType.Flat, true), M(@"W:\N\FLAT\Cam\b_128.85.fits", CalibrationType.Flat, true),
            M(@"W:\N\FLAT\Cam\c_270.00.fits", CalibrationType.Flat, false));
        var op = Assert.Single(CalibrationScan.Operations(N, "E", @"W:\N", [(row, true)], []));
        Assert.Equal(["c_270.00.fits"], op.ExcludedRelativePaths);
        Assert.Equal(@"FLAT\Cam", op.DestinationSubfolder);
    }

    [Fact]
    public void Operations_ATickedRowWithNoSelection_CopiesAllItsFramesOfEachType()
    {
        var row = Row(@"W:\N\Dark",
            M(@"W:\N\Dark\d1.fits", CalibrationType.Dark, false), M(@"W:\N\Dark\d2.fits", CalibrationType.Dark, false),
            M(@"W:\N\Dark\b1.fits", CalibrationType.Bias, false), M(@"W:\N\Dark\l1.fits", CalibrationType.Light, false),
            M(@"W:\N\Dark\o1.fits", CalibrationType.Other, false));
        var ops = CalibrationScan.Operations(N, "E", @"W:\N", [(row, true)], []);
        Assert.Equal(2, ops.Count);
        Assert.Equal("DARK", ops[0].DestinationSubfolder);
        Assert.Equal(["b1.fits", "l1.fits", "o1.fits"], ops[0].ExcludedRelativePaths);
        Assert.Equal(@"BIAS\Dark", ops[1].DestinationSubfolder);
        Assert.Equal(["d1.fits", "d2.fits", "l1.fits", "o1.fits"], ops[1].ExcludedRelativePaths);
    }

    [Fact]
    public void Operations_ExcludeTheRowFoldersFilesThatAreNotInTheRow()
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
        var op = Assert.Single(CalibrationScan.Operations(N, "E", @"W:\N", [(row, true)], all));
        Assert.Equal(["broken.fits", "notes.txt"], op.ExcludedRelativePaths);
    }

    [Fact]
    public void Operations_UntickedRows_ProduceNothing()
    {
        var row = Row(@"W:\N\FLAT", M(@"W:\N\FLAT\f1.fits", CalibrationType.Flat, true));
        Assert.Empty(CalibrationScan.Operations(N, "E", @"W:\N", [(row, false)], []));
    }

    [Fact]
    public void Operations_AreOrderedByFolderThenType()
    {
        var b = Row(@"W:\N\B", M(@"W:\N\B\f.fits", CalibrationType.Flat, true));
        var a = Row(@"W:\N\A", M(@"W:\N\A\b.fits", CalibrationType.Bias, true), M(@"W:\N\A\d.fits", CalibrationType.Dark, true));
        var ops = CalibrationScan.Operations(N, "E", @"W:\N", [(b, true), (a, true)], []);
        Assert.Equal([@"W:\N\A", @"W:\N\A", @"W:\N\B"], ops.Select(o => o.SourcePath));
        Assert.Equal([@"DARK\A", @"BIAS\A", @"FLAT\B"], ops.Select(o => o.DestinationSubfolder));
    }

    [Fact]
    public void Operations_ExcludeChildDirectoriesOfTheRowFolder()
    {
        var row = Row(@"W:\FLAT", M(@"W:\FLAT\a.fits", CalibrationType.Flat, true));
        ScannedFrame S(string p) => new(p, 1, CalibrationType.Flat, Keys, null, N);
        var all = new[] { S(@"W:\FLAT\a.fits"), S(@"W:\FLAT\old\a.fits"), S(@"W:\FLAT\rejects\deep\b.fits"), S(@"W:\FLAT\Old\c.fits"), S(@"W:\FLAT2\d.fits") };
        var op = Assert.Single(CalibrationScan.Operations(N, "E", @"W:\", [(row, true)], all));
        Assert.Equal(["old", "rejects"], op.ExcludedRelativeDirectories);
    }

    [Fact]
    public void Operations_SkipATypeWithNoSelectedFrameWhenOtherTypesAreSelected()
    {
        var row = Row(@"W:\N\FLAT",
            M(@"W:\N\FLAT\f1.fits", CalibrationType.Flat, true), M(@"W:\N\FLAT\df1.fits", CalibrationType.DarkFlat, false));
        var op = Assert.Single(CalibrationScan.Operations(N, "E", @"W:\N", [(row, true)], []));
        Assert.Equal("FLAT", op.DestinationSubfolder);
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
}
