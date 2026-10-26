using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.Core.Tests.Wbpp;

public class CalibrationClimbTests
{
    private static FolderLevel L(string relative, string[]? otherTargets = null, string[]? otherNights = null)
        => new(Path.Combine(@"D:\Astro", relative), 1, 0, otherTargets ?? [], otherNights ?? [], null);

    private static SessionLevels Levels(params FolderLevel[] levels)
        => new(new DateOnly(2026, 7, 1), @"D:\Astro", levels, 0, 0, 0, null);

    [Fact]
    public void StartLevel_IsTheShallowestNightPureLevel()
    {
        var levels = Levels(
            L("2025", otherNights: ["2025-01-01"]), L(@"2025\Date_X", otherTargets: ["Y"]),
            L(@"2025\Date_X\LIGHT"), L(@"2025\Date_X\LIGHT\Cam"), L(@"2025\Date_X\LIGHT\Cam\Target"),
            L(@"2025\Date_X\LIGHT\Cam\Target\Angle"));

        Assert.Equal(1, CalibrationClimb.StartLevel(levels, 5));
        Assert.False(CalibrationClimb.StartedAtFallback(levels));
    }

    [Fact]
    public void StartLevel_FallsBackToTheChosenParent_WhenNoLevelIsNightPure()
    {
        var other = new[] { "2025-01-01" };
        var levels = Levels(
            L("Okie", otherNights: other), L(@"Okie\ASIAIR", otherNights: other),
            L(@"Okie\ASIAIR\Plan", otherNights: other), L(@"Okie\ASIAIR\Plan\Light", otherNights: other),
            L(@"Okie\ASIAIR\Plan\Light\IC1848", otherNights: other));

        Assert.Equal(3, CalibrationClimb.StartLevel(levels, 4));
        Assert.Equal(0, CalibrationClimb.StartLevel(levels, 0));
        Assert.True(CalibrationClimb.StartedAtFallback(levels));
    }

    [Fact]
    public void AutomaticNext_ClimbsOnlyIntoFoldersWithNoOtherTarget()
    {
        var dateFirst = Levels(
            L("2025", otherTargets: ["Y"], otherNights: ["n"]), L(@"2025\Date_X", otherTargets: ["Y"]),
            L(@"2025\Date_X\LIGHT"));
        Assert.Null(CalibrationClimb.AutomaticNext(dateFirst, 1));

        var targetFirst = Levels(
            L("Target_Y", otherNights: ["n"]), L(@"Target_Y\LIGHT", otherNights: ["n"]), L(@"Target_Y\LIGHT\night"));
        Assert.Equal(1, CalibrationClimb.AutomaticNext(targetFirst, 2));
        Assert.Equal(0, CalibrationClimb.AutomaticNext(targetFirst, 1));
        Assert.Null(CalibrationClimb.AutomaticNext(targetFirst, 0));
    }

    [Fact]
    public void ManualNext_ClimbsOneLevelUntilTheScanRootChild()
    {
        var levels = Levels(L("a", otherTargets: ["Y"]), L(@"a\b", otherTargets: ["Y"]), L(@"a\b\c"));
        Assert.Equal(1, CalibrationClimb.ManualNext(levels, 2));
        Assert.Equal(0, CalibrationClimb.ManualNext(levels, 1));
        Assert.Null(CalibrationClimb.ManualNext(levels, 0));
    }

    [Fact]
    public void AtAFork_TheParentIsTheContainingLevel_NotTheSiblingLeaf()
    {
        // Depth-then-path order, every level holding another night, so StartLevel falls back.
        var other = new[] { "2026-10-17" };
        var levels = Levels(
            L("2026", otherNights: other),
            L(@"2026\Date_2026-10-18", otherNights: other),
            L(@"2026\Date_2026-10-18\LIGHT", otherNights: other),
            L(@"2026\Date_2026-10-18\LIGHT\ZWO ASI533MM Pro", otherNights: other),
            L(@"2026\Date_2026-10-18\LIGHT\ZWO ASI533MM Pro\Target_Pleiades", otherNights: other),
            L(@"2026\Date_2026-10-18\LIGHT\ZWO ASI533MM Pro\Target_Pleiades\Angle_45.18", otherNights: other),
            L(@"2026\Date_2026-10-18\LIGHT\ZWO ASI533MM Pro\Target_Pleiades\Angle_46.02", otherNights: other));

        Assert.Equal(4, CalibrationClimb.ParentOf(levels, 6));
        Assert.Equal(4, CalibrationClimb.StartLevel(levels, 6));
        Assert.Equal(4, CalibrationClimb.AutomaticNext(levels, 6));
        Assert.Equal(4, CalibrationClimb.ManualNext(levels, 6));
        Assert.Equal(4, CalibrationClimb.ManualNext(levels, 5));
        Assert.Null(CalibrationClimb.ParentOf(levels, 0));
        Assert.Null(CalibrationClimb.ManualNext(levels, 0));
    }
}
