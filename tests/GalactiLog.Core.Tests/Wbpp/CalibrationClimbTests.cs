using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.Core.Tests.Wbpp;

public class CalibrationClimbTests
{
    private static FolderLevel L(string path, string[]? otherTargets = null, string[]? otherNights = null)
        => new(path, 1, 0, otherTargets ?? [], otherNights ?? [], null);

    private static SessionLevels Levels(params FolderLevel[] levels)
        => new(new DateOnly(2026, 7, 1), @"D:\Astro", levels, 0, 0, 0, null);

    [Fact]
    public void StartLevel_IsTheShallowestNightPureLevel()
    {
        var levels = Levels(
            L("2025", otherNights: ["2025-01-01"]), L("Date_X", otherTargets: ["Y"]),
            L("LIGHT"), L("Cam"), L("Target"), L("Angle"));

        Assert.Equal(1, CalibrationClimb.StartLevel(levels, 5));
        Assert.False(CalibrationClimb.StartedAtFallback(levels));
    }

    [Fact]
    public void StartLevel_FallsBackToTheChosenParent_WhenNoLevelIsNightPure()
    {
        var other = new[] { "2025-01-01" };
        var levels = Levels(
            L("Okie", otherNights: other), L("ASIAIR", otherNights: other), L("Plan", otherNights: other),
            L("Light", otherNights: other), L("IC1848", otherNights: other));

        Assert.Equal(3, CalibrationClimb.StartLevel(levels, 4));
        Assert.Equal(0, CalibrationClimb.StartLevel(levels, 0));
        Assert.True(CalibrationClimb.StartedAtFallback(levels));
    }

    [Fact]
    public void AutomaticNext_ClimbsOnlyIntoFoldersWithNoOtherTarget()
    {
        var dateFirst = Levels(L("2025", otherTargets: ["Y"], otherNights: ["n"]), L("Date_X", otherTargets: ["Y"]), L("LIGHT"));
        Assert.Null(CalibrationClimb.AutomaticNext(dateFirst, 1));

        var targetFirst = Levels(
            L("Target_Y", otherNights: ["n"]), L("LIGHT", otherNights: ["n"]), L("night"));
        Assert.Equal(1, CalibrationClimb.AutomaticNext(targetFirst, 2));
        Assert.Equal(0, CalibrationClimb.AutomaticNext(targetFirst, 1));
        Assert.Null(CalibrationClimb.AutomaticNext(targetFirst, 0));
    }

    [Fact]
    public void ManualNext_ClimbsOneLevelUntilTheScanRootChild()
    {
        var levels = Levels(L("a", otherTargets: ["Y"]), L("b", otherTargets: ["Y"]), L("c"));
        Assert.Equal(1, CalibrationClimb.ManualNext(levels, 2));
        Assert.Equal(0, CalibrationClimb.ManualNext(levels, 1));
        Assert.Null(CalibrationClimb.ManualNext(levels, 0));
    }
}
