using System;
using System.Collections.Generic;
using System.Linq;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

/// <summary>
/// <see cref="AnalysisCache"/>: the keyed memo in front of <see cref="AnalysisQuery"/>, its one
/// key-building choke point, its 32 entry cap and its invalidation.
/// </summary>
/// <remarks>
/// <para>
/// Every case proves a hit by MUTATING THE DATABASE between two calls and asserting the second
/// answer is the stale one, which is the alternative <c>task3.md</c> section 7.10 names. A
/// counting fake is the other route and is not taken: <see cref="AnalysisQuery"/> is sealed with
/// no interface, as every other query in this namespace is, so a fake would have meant an
/// interface with one implementation for the sake of a test, and the mutation route proves the
/// same fact (the query ran once) against the shipped type.
/// </para>
/// <para>
/// The harness is <see cref="AnalysisQueryTests.Library"/>, which already wires the query, the
/// alias map, the statistics memo and the profile map together the way <c>AppHost</c> does.
/// </para>
/// </remarks>
public class AnalysisCacheTests
{
    private static readonly AnalysisFilter NoFilter =
        new(null, null, null, AnalysisGranularity.Frame, null, null);

    private static readonly DateOnly Night = new(2026, 1, 5);

    [Fact]
    public void TheSecondCallWithTheSameArguments_IsServedFromTheMemo()
    {
        using var library = new AnalysisQueryTests.Library();
        Seed(library, 6);

        var first = library.Cache.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter);
        Assert.Equal(6, first.TotalCount);

        Seed(library, 6);

        // The query itself sees the new rows; the memo does not.
        Assert.Equal(12, library.Query.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter).TotalCount);
        Assert.Equal(6, library.Cache.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter).TotalCount);
    }

    [Fact]
    public void Invalidate_MakesTheNextCallRecompute()
    {
        using var library = new AnalysisQueryTests.Library();
        Seed(library, 6);

        Assert.Equal(6, library.Cache.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter).TotalCount);
        Seed(library, 6);
        library.Cache.Invalidate();

        // The one staleness handler in AppHost calls this from scan completion and from a general
        // settings save (ruling A6), and nothing else drops an entry.
        Assert.Equal(12, library.Cache.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter).TotalCount);
    }

    [Fact]
    public void NoTwoTabsShareAnEntry()
    {
        using var library = new AnalysisQueryTests.Library();
        Seed(library, 6);

        // Two tabs, one metric, one filter: two different shapes and not one served twice.
        var distribution = library.Cache.Distribution(AnalysisMetric.Hfr, NoFilter);
        var series = library.Cache.TimeSeries(AnalysisMetric.Hfr, NoFilter);
        Assert.NotNull(distribution);
        Assert.Equal(6, distribution!.Stats.Count);
        Assert.Equal(6, Assert.Single(series.Points).FrameCount);

        // The metric PAIR, both ways round: a discriminator that carried only one of them would
        // serve the same scatter for both axes orders.
        var forward = library.Cache.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter);
        var reverse = library.Cache.Correlation(AnalysisMetric.Hfr, AnalysisMetric.Humidity, NoFilter);
        Assert.NotEqual(forward.Points[0].X, reverse.Points[0].X);

        // The grouping, which is the component an implementer most easily leaves out.
        //
        // Seen red by dropping groupBy from the box plot discriminator, which returns the By
        // Filter answer for the By Month call and the two become equal.
        var byFilter = library.Cache.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Filter, NoFilter);
        var byMonth = library.Cache.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Month, NoFilter);
        Assert.Equal("L", Assert.Single(byFilter.Groups).GroupName);
        Assert.Equal("2026-01", Assert.Single(byMonth.Groups).GroupName);
    }

    [Fact]
    public void TwoCompareGroupPairsThatJoinToOneString_AnswerTheirOwnResults()
    {
        using var library = new AnalysisQueryTests.Library();

        // A group string is a catalogue value and not a vocabulary this application chooses: in
        // Filter mode it IS a raw images.filter_used value read from a file header, and in
        // Equipment mode it is the telescope and camera pair. A bar in one of them is therefore a
        // value, so the pairs ("R|G", "B") and ("R", "G|B") both join to "R|G|B".
        SeedFilter(library, "R|G", 4);
        SeedFilter(library, "B", 5);
        SeedFilter(library, "R", 6);
        SeedFilter(library, "G|B", 7);

        var first = library.Cache.Compare(AnalysisMetric.Hfr, CompareMode.Filter, "R|G", "B", null, null);
        var second = library.Cache.Compare(AnalysisMetric.Hfr, CompareMode.Filter, "R", "G|B", null, null);

        Assert.Equal("R|G", first.NameA);
        Assert.Equal("B", first.NameB);
        Assert.Equal(4, first.CountA);
        Assert.Equal(5, first.CountB);

        // Seen red against a discriminator that joined the two group strings with a bar: the
        // second call was served the first's CompareResult, so it answered NameA "R|G", NameB "B",
        // CountA 4 and CountB 5, which is the wrong two boxes and a verdict naming the other pair.
        Assert.Equal("R", second.NameA);
        Assert.Equal("G|B", second.NameB);
        Assert.Equal(6, second.CountA);
        Assert.Equal(7, second.CountB);

        // And the second pair still MEMOIZES, so the fix separates the two group strings rather
        // than making every Compare call a miss.
        SeedFilter(library, "R", 3);
        Assert.Equal(
            6,
            library.Cache.Compare(AnalysisMetric.Hfr, CompareMode.Filter, "R", "G|B", null, null).CountA);
    }

    [Fact]
    public void ANullResult_IsMemoizedLikeAnyOtherAnswer()
    {
        using var library = new AnalysisQueryTests.Library();
        Seed(library, 1);

        // "Not enough data" is as expensive to recompute as a figure and does not change until an
        // invalidation, so it is a HIT and not a miss. The dictionary's value slot is the nullable
        // wrapper that makes that expressible: presence is TryGetValue's answer, never the value's
        // nullness.
        Assert.Null(library.Cache.Distribution(AnalysisMetric.Hfr, NoFilter));

        Seed(library, 6);
        Assert.NotNull(library.Query.Distribution(AnalysisMetric.Hfr, NoFilter));
        Assert.Null(library.Cache.Distribution(AnalysisMetric.Hfr, NoFilter));

        library.Cache.Invalidate();
        Assert.NotNull(library.Cache.Distribution(AnalysisMetric.Hfr, NoFilter));
    }

    [Fact]
    public void ThereIsNoTimeToLive()
    {
        using var library = new AnalysisQueryTests.Library();
        Seed(library, 6);

        // Nothing is advanced, because there is nothing to advance: no clock is injected and none
        // exists. The case pins that fact, and its failure shape is someone adding a TTL later
        // because the web has one (its five minutes are for a cache shared between processes that
        // cannot see an ingest; this one can).
        Assert.Equal(6, library.Cache.Matrix(NoFilter).Cells
            .Single(cell => cell.X == AnalysisMetric.Humidity && cell.Y == AnalysisMetric.Hfr).NPoints);

        Seed(library, 6);

        Assert.Equal(6, library.Cache.Matrix(NoFilter).Cells
            .Single(cell => cell.X == AnalysisMetric.Humidity && cell.Y == AnalysisMetric.Hfr).NPoints);
    }

    [Fact]
    public void TheThreeTabsThatIgnoreGranularity_KeepOneEntryAndNotTwo()
    {
        using var library = new AnalysisQueryTests.Library();
        Seed(library, 6);

        var bySession = NoFilter with { Granularity = AnalysisGranularity.Session };

        Assert.Equal(6, Assert.Single(library.Cache.TimeSeries(AnalysisMetric.Hfr, NoFilter).Points).FrameCount);
        Assert.Equal(6, Assert.Single(library.Cache.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Filter, NoFilter).Groups).Count);
        Assert.Equal(6, library.Cache.Matrix(NoFilter).Cells
            .Single(cell => cell.X == AnalysisMetric.Humidity && cell.Y == AnalysisMetric.Hfr).NPoints);

        Seed(library, 6);

        // All three IGNORE granularity, so the key normalises it to Frame at this one choke point
        // and the session call is a HIT on the frame entry. Without that each would keep two byte
        // identical entries, one per granularity, against the 32 entry cap.
        Assert.Equal(6, Assert.Single(library.Cache.TimeSeries(AnalysisMetric.Hfr, bySession).Points).FrameCount);
        Assert.Equal(6, Assert.Single(library.Cache.BoxPlot(AnalysisMetric.Hfr, BoxPlotGrouping.Filter, bySession).Groups).Count);
        Assert.Equal(6, library.Cache.Matrix(bySession).Cells
            .Single(cell => cell.X == AnalysisMetric.Humidity && cell.Y == AnalysisMetric.Hfr).NPoints);

        // Correlation DOES honour granularity, so its two entries are two different answers.
        Assert.Equal(12, library.Cache.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter).TotalCount);
        Assert.Equal(1, library.Cache.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, bySession).TotalCount);
    }

    [Fact]
    public void TheMemoIsCappedAtThirtyTwoEntries_AndEvictsTheOldestInserted()
    {
        using var library = new AnalysisQueryTests.Library();
        Seed(library, 6);

        // The first entry, and the one the cap will push out.
        Assert.Equal(6, library.Cache.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter).TotalCount);

        // Thirty two further distinct keys, which is one more than the cap can hold beside it.
        var keys = AnalysisMetrics.X
            .SelectMany(x => AnalysisMetrics.Y, (x, y) => (X: x, Y: y))
            .Where(pair => pair.X != AnalysisMetric.Humidity || pair.Y != AnalysisMetric.Hfr)
            .Take(AnalysisCache.Capacity)
            .ToList();
        Assert.Equal(32, keys.Count);

        foreach (var (x, y) in keys)
        {
            library.Cache.Correlation(x, y, NoFilter);
        }

        Seed(library, 6);

        // The oldest INSERTED entry is gone, so this one recomputes and sees the new rows.
        Assert.Equal(12, library.Cache.Correlation(AnalysisMetric.Humidity, AnalysisMetric.Hfr, NoFilter).TotalCount);

        // And a recent one is still held, which is what makes the line above an eviction rather
        // than a cache that never hit at all.
        var (lastX, lastY) = keys[^1];
        Assert.Equal(6, library.Cache.Correlation(lastX, lastY, NoFilter).TotalCount);
    }

    private static void SeedFilter(AnalysisQueryTests.Library library, string filterUsed, int count)
        => library.AddFrames(Enumerable.Range(0, count)
            .Select(i => library.Frame(frame =>
            {
                frame.SessionDate = Night;
                frame.CaptureDate = Night.ToDateTime(new TimeOnly(22, 0));
                frame.Telescope = "Rig A";
                frame.Camera = "Cam A";
                frame.FilterUsed = filterUsed;
                frame.ArcsecPerPixel = 1.0;
                frame.MedianHfr = 1.0 + (i * 0.1);
            })));

    private static void Seed(AnalysisQueryTests.Library library, int count)
        => library.AddFrames(Enumerable.Range(0, count)
            .Select(i => library.Frame(frame =>
            {
                frame.SessionDate = Night;
                frame.CaptureDate = Night.ToDateTime(new TimeOnly(22, 0));
                frame.Telescope = "Rig A";
                frame.Camera = "Cam A";
                frame.FilterUsed = "L";
                frame.ArcsecPerPixel = 1.0;
                frame.MedianHfr = 1.0 + (i * 0.1);
                frame.Humidity = 40 + i;
                frame.Fwhm = 2.0 + (i * 0.1);
                frame.Eccentricity = 0.3 + (i * 0.01);
                frame.GuidingRmsArcsec = 0.4 + (i * 0.01);
                frame.GuidingRmsRaArcsec = 0.3 + (i * 0.01);
                frame.GuidingRmsDecArcsec = 0.2 + (i * 0.01);
                frame.DetectedStars = 800 + i;
                frame.AduMean = 1000 + i;
                frame.AduMedian = 1001 + i;
                frame.AduStdev = 100 + i;
                frame.WindSpeed = 5 + i;
                frame.AmbientTemp = 8 + i;
                frame.DewPoint = 3 + i;
                frame.Pressure = 1000 + i;
                frame.CloudCover = i;
                frame.FocuserTemp = 9 + i;
                frame.Airmass = 1.1 + (i * 0.01);
                frame.SensorTemp = -10 - i;
            })));
}
