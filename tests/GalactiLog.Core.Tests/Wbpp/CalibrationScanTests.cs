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
    public void LongestRelative_IsTheLongestPathUnderTheFolder_AndZeroWhenNoneIsUnderIt()
    {
        ScannedFrame S(string p) => new(p, 1, CalibrationType.Flat, Keys, null, N);
        var frames = new[] { S(@"W:\N\FLAT\f1.fits"), S(@"W:\N\FLAT\sub\longer_name.fits"), S(@"W:\N\FLAT2\x.fits") };

        Assert.Equal(@"sub\longer_name.fits".Length, CalibrationScan.LongestRelative(@"W:\N\FLAT", frames));
        Assert.Equal(0, CalibrationScan.LongestRelative(@"W:\N\DARK", frames));
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
