using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

/// <summary>
/// Spec 12.5's calendar query: one row per imaging night in a user-controlled range, grouped on
/// <c>session_date</c> alone. Separate from <see cref="StatsQueryTests"/> because it is the one
/// part of the statistics read that is not memoized and takes arguments.
/// </summary>
public class StatsCalendarQueryTests
{
    private static readonly DateOnly First = new(2025, 3, 1);
    private static readonly DateOnly Second = new(2025, 3, 2);
    private static readonly DateOnly Third = new(2025, 3, 3);

    [Fact]
    public void Calendar_OneRowPerImagingNightInRange_Inclusive()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, First.AddDays(-1));
        library.AddFrame(target.Id, First);
        library.AddFrame(target.Id, Second);
        library.AddFrame(target.Id, Third);
        library.AddFrame(target.Id, Third.AddDays(1));

        var entries = library.Query.Calendar(First, Third);

        // A night imaged with two rigs is still one cell, which is deliberately not the
        // rig-session grouping the overview counts, and both bounds are inclusive.
        Assert.Equal(new[] { First, Second, Third }, entries.Select(entry => entry.Date));
    }

    [Fact]
    public void Calendar_CountsDistinctTargetsAndAllFrames()
    {
        using var library = StatsLibrary.Empty();
        var first = library.AddTarget("M 42");
        var second = library.AddTarget("M 31");
        library.AddFrame(first.Id, First, StatsQueryTests.Rig("RC8", "ASI2600MM"));
        library.AddFrame(first.Id, First, StatsQueryTests.Rig("FRA600", "ASI294MC"));
        library.AddFrame(second.Id, First);
        library.AddFrame(null, First);

        var entry = Assert.Single(library.Query.Calendar(First, First));

        Assert.Equal(4, entry.FrameCount);
        Assert.Equal(2, entry.TargetCount);
        Assert.Equal(4 * LibrarySeeder.ExposureSeconds, entry.IntegrationSeconds);
    }

    [Fact]
    public void Calendar_IgnoresCalibrationFramesAndFramesWithNoSessionDate()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, First);
        library.AddFrame(target.Id, First, image => image.ImageType = "FLAT");
        library.AddFrame(target.Id, First, image => image.SessionDate = null);

        var entry = Assert.Single(library.Query.Calendar(First, Third));

        Assert.Equal(1, entry.FrameCount);
        Assert.Equal(LibrarySeeder.ExposureSeconds, entry.IntegrationSeconds);
    }

    [Fact]
    public void Calendar_BindsBothBounds()
    {
        // Structural first: the statement's only interpolation is the LIGHT filter, a constant of
        // the Data assembly, and both bounds are parameters.
        Assert.Contains("@p0", StatsQuery.CalendarSql, StringComparison.Ordinal);
        Assert.Contains("@p1", StatsQuery.CalendarSql, StringComparison.Ordinal);
        Assert.DoesNotContain("2025", StatsQuery.CalendarSql, StringComparison.Ordinal);

        // Then behaviourally: a bound that would break out of a quoted literal is compared as a
        // value and simply matches nothing.
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, First);

        Assert.Empty(library.Query.Calendar(Second, Third));
        Assert.Single(library.Query.Calendar(First, First));
    }
}
