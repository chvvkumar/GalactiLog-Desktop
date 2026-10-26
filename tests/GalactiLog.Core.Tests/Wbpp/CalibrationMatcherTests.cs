using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.Core.Tests.Wbpp;

public class CalibrationMatcherTests
{
    private static readonly DateOnly N = new(2025, 10, 15);

    private static FrameKeys K(string? cam = "ZWO ASI2600MM Pro", string? filter = "Ha", double? bin = 1, double? gain = 110,
        double? offset = 50, string? tel = "SQA55", double? rot = 128.85, string? readout = null) =>
        new(cam, filter, bin, gain, offset, tel, rot, readout);

    private static ScannedFrame F(string path, CalibrationType type, FrameKeys keys, double? exp = 0.01, DateOnly? night = null, long size = 100) =>
        new(path, size, type, keys, exp, night);

    [Fact]
    public void LightSets_GroupByNormalisedKeys_AndRoundRotation()
    {
        var sets = CalibrationMatcher.LightSets([K(filter: "Ha "), K(filter: "ha", rot: 128.91), K(filter: "Oiii")]);
        Assert.Equal(2, sets.Count);
        Assert.Equal(2, sets[0].FrameCount);
    }

    [Fact]
    public void KeysMatch_HardKeysMustAgree_SoftKeysOnlyWhenBothPresent()
    {
        Assert.True(CalibrationMatcher.KeysMatch(K(), K(tel: null, rot: null, readout: null)));
        Assert.False(CalibrationMatcher.KeysMatch(K(), K(gain: 100)));
        Assert.False(CalibrationMatcher.KeysMatch(K(), K(offset: null)));
        Assert.False(CalibrationMatcher.KeysMatch(K(), K(tel: "Askar140APO_0.8x")));
    }

    [Fact]
    public void KeysMatch_MissingOnBothSidesIsEqual()
    {
        Assert.True(CalibrationMatcher.KeysMatch(K(filter: null), K(filter: null)));
    }

    [Fact]
    public void Match_RotatorCompareUsesTheShortestArc()
    {
        var m = CalibrationMatcher.Match(CalibrationMatcher.LightSets([K(rot: 359.6)]), N, [F(@"W:\FLAT\a.fits", CalibrationType.Flat, K(rot: 0.3), night: N)]);
        Assert.True(m.Rows.Single().Frames.Single().Selected);
    }

    [Fact]
    public void Match_PrefersSameNight_ElseNearestInEitherDirection()
    {
        var frames = new[]
        {
            F(@"W:\F1\a.fits", CalibrationType.Flat, K(), night: N.AddDays(-6)),
            F(@"W:\F2\a.fits", CalibrationType.Flat, K(), night: N.AddDays(3)),
        };
        var m = CalibrationMatcher.Match(CalibrationMatcher.LightSets([K()]), N, frames);
        Assert.Equal(["3 days later"], m.Rows.Where(r => r.SelectedCount > 0).Select(r => r.Note));
        var same = CalibrationMatcher.Match(CalibrationMatcher.LightSets([K()]), N, [.. frames, F(@"W:\F3\a.fits", CalibrationType.Flat, K(), night: N)]);
        Assert.Equal([@"W:\F3"], same.Rows.Where(r => r.SelectedCount > 0).Select(r => r.Folder));
    }

    [Fact]
    public void Match_FlatDarksFollowSelectedFlats_ByExposureAndKeys()
    {
        var frames = new[]
        {
            F(@"W:\FLAT\a.fits", CalibrationType.Flat, K(), exp: 0.01014, night: N),
            F(@"W:\DF\a.fits", CalibrationType.DarkFlat, K(filter: null, tel: null, rot: null), exp: 0.01, night: N),
            F(@"W:\DF\b.fits", CalibrationType.DarkFlat, K(filter: null), exp: 1.0, night: N),
        };
        var m = CalibrationMatcher.Match(CalibrationMatcher.LightSets([K()]), N, frames);
        var df = m.Rows.Single(r => r.Folder == @"W:\DF");
        Assert.Equal(1, df.SelectedCount);
        Assert.True(df.DefaultTicked);
    }

    [Fact]
    public void Match_DarksAndBias_AreListedUnticked()
    {
        var m = CalibrationMatcher.Match(CalibrationMatcher.LightSets([K()]), N,
            [F(@"W:\DARK\a.fits", CalibrationType.Dark, K(filter: null), exp: 420), F(@"W:\BIAS\a.fits", CalibrationType.Bias, K(filter: null), exp: 0)]);
        Assert.All(m.Rows, r => { Assert.False(r.DefaultTicked); Assert.Equal(0, r.SelectedCount); });
        Assert.Equal("bias, not selected", m.Rows.Single(r => r.Folder == @"W:\BIAS").Note);
        Assert.Equal("darks, not selected", m.Rows.Single(r => r.Folder == @"W:\DARK").Note);
    }

    [Fact]
    public void Match_AnUnmatchedFlatRow_NamesTheFirstMismatchingKey()
    {
        var m = CalibrationMatcher.Match(CalibrationMatcher.LightSets([K()]), N, [F(@"W:\F\a.fits", CalibrationType.Flat, K(rot: 139.07), night: N)]);
        Assert.Equal("rotation 139.07", m.Rows.Single().Note);
        Assert.False(m.Rows.Single().DefaultTicked);
    }

    [Fact]
    public void Match_WarnsPerFilterWithoutAFlat_AndReportsCompleteness()
    {
        var m = CalibrationMatcher.Match(
            CalibrationMatcher.LightSets([K(filter: "Ha"), K(filter: "Oiii"), K(cam: "ZWO ASI533MC Pro", filter: null)]), N,
            [F(@"W:\F\a.fits", CalibrationType.Flat, K(filter: "Ha"), night: N)]);
        Assert.Equal(["No flats found for Oiii on ZWO ASI2600MM Pro", "No flats found for ZWO ASI533MC Pro, no filter"], m.MissingFlatWarnings);
        Assert.False(m.EveryFilterHasAFlat);
    }

    [Fact]
    public void Match_LightAndOtherFrames_NeverFormRows()
    {
        var m = CalibrationMatcher.Match(CalibrationMatcher.LightSets([K()]), N,
            [F(@"W:\L\a.fits", CalibrationType.Light, K()), F(@"W:\L\a.json", CalibrationType.Other, K())]);
        Assert.Empty(m.Rows);
    }

    [Fact]
    public void Match_ARejectedFlat_IsExplainedAgainstTheLightSetWithItsFilter()
    {
        var sets = CalibrationMatcher.LightSets([K(filter: "Ha"), K(filter: "Oiii")]);
        var m = CalibrationMatcher.Match(sets, N, [F(@"W:\FLAT\o.fits", CalibrationType.Flat, K(filter: " oiii", rot: 139.07), night: N)]);
        Assert.Equal("rotation 139.07", m.Rows.Single().Note);
    }

    [Fact]
    public void Match_TwoLightSetsOfOneFilterAtTwoAngles_EachRowSelectsOnlyItsOwnAngle()
    {
        var sets = CalibrationMatcher.LightSets([K(rot: 128.85), K(rot: 139.07)]);
        var frames = new[]
        {
            F(@"W:\FLAT\A128\a.fits", CalibrationType.Flat, K(rot: 128.85), night: N),
            F(@"W:\FLAT\A128\b.fits", CalibrationType.Flat, K(rot: 128.9), night: N),
            F(@"W:\FLAT\A139\a.fits", CalibrationType.Flat, K(rot: 139.07), night: N),
            F(@"W:\FLAT\A139\b.fits", CalibrationType.Flat, K(rot: 139.0), night: N),
            F(@"W:\FLAT\A128\stray.fits", CalibrationType.Flat, K(rot: 150), night: N),
        };
        var m = CalibrationMatcher.Match(sets, N, frames);
        Assert.Equal(2, m.Rows.Count);
        foreach (var row in m.Rows)
        {
            var angle = row.Folder.EndsWith("A128", StringComparison.Ordinal) ? 128.85 : 139.07;
            Assert.All(row.Frames, f => Assert.Equal(
                Math.Abs(f.Frame.Keys.RotatorAngle!.Value - angle) <= CalibrationMatcher.RotatorToleranceDegrees, f.Selected));
            Assert.Equal(2, row.SelectedCount);
        }
        // No flat of one angle matches the other angle's set.
        Assert.False(CalibrationMatcher.KeysMatch(sets[0].Keys, frames[2].Keys));
        Assert.False(CalibrationMatcher.KeysMatch(sets[1].Keys, frames[0].Keys));
    }
}
