using System.Globalization;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

/// <summary>
/// Spec 18.1's Stats query row: "every field of the stats response against a seeded fixture with
/// known totals". Every field gets its own named case rather than one omnibus assertion, and the
/// cases that need a shape <see cref="LibrarySeeder"/> does not have (a zero metric, a null raw
/// equipment name, an eccentricity-source tie) build their own small library instead of bending
/// the fixture.
/// </summary>
public class StatsQueryTests
{
    private static readonly DateOnly Day = new(2025, 3, 1);
    private static readonly DateOnly NextDay = new(2025, 3, 2);

    // ---- overview ----------------------------------------------------------------------

    [Fact]
    public void Overview_TotalIntegration_IsTheSeededTotal()
    {
        using var library = StatsLibrary.Seeded();

        Assert.Equal(LibrarySeeder.TotalIntegrationSeconds, library.Query.Get().Overview.TotalIntegrationSeconds);
    }

    [Fact]
    public void Overview_TotalFrames_CountsEveryLightFrame_IncludingThoseWithNoCaptureDate()
    {
        using var library = StatsLibrary.Seeded();
        library.AddFrame(LibrarySeeder.Targets[0].Id, Day, image => image.CaptureDate = null);

        var overview = library.Query.Get().Overview;

        // total_frames is the one overview figure with no capture-date filter, deliberately, and
        // the integration total beside it still has one.
        Assert.Equal(LibrarySeeder.FrameCount + 1, overview.TotalFrames);
        Assert.Equal(LibrarySeeder.TotalIntegrationSeconds, overview.TotalIntegrationSeconds);
    }

    [Fact]
    public void Overview_TargetCount_CountsDistinctResolvedTargets()
    {
        using var library = StatsLibrary.Seeded();

        Assert.Equal(LibrarySeeder.TargetCount, library.Query.Get().Overview.TargetCount);
    }

    [Fact]
    public void Overview_ExcludesFramesWhoseTargetIsMergedAway()
    {
        using var library = StatsLibrary.Empty();
        var winner = library.AddTarget("M 31");
        var loser = library.AddTarget("NGC 224", target => target.MergedIntoId = winner.Id);
        library.AddFrame(winner.Id, Day);
        library.AddFrame(loser.Id, Day);
        library.AddFrame(loser.Id, Day);

        var overview = library.Query.Get().Overview;

        Assert.Equal(1, overview.TotalFrames);
        Assert.Equal(1, overview.TargetCount);
    }

    [Fact]
    public void Overview_IncludesUnresolvedFrames()
    {
        using var library = StatsLibrary.Empty();
        library.AddFrame(null, Day);

        var overview = library.Query.Get().Overview;

        // The merged_ok guard is an OR: a frame with no resolved target has no targets row to
        // check and must still count.
        Assert.Equal(1, overview.TotalFrames);
        Assert.Equal(0, overview.TargetCount);
    }

    [Fact]
    public void Overview_RigSessionCount_IsDistinctNightTimesTelescopeTimesCamera()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, Rig("RC8", "ASI2600MM"));
        library.AddFrame(target.Id, Day, Rig("RC8", "ASI2600MM"));
        library.AddFrame(target.Id, Day, Rig("FRA600", "ASI294MC"));

        var overview = library.Query.Get().Overview;

        // One night, two rigs: two rig-sessions, not one. The web source carries audit note
        // AUD-019 saying this is deliberately not a count of imaging nights.
        Assert.Equal(2, overview.RigSessionCount);
    }

    [Fact]
    public void Overview_FirstAndLastSessionDate_AreTheSeededBounds()
    {
        using var library = StatsLibrary.Seeded();

        var overview = library.Query.Get().Overview;

        Assert.Equal(LibrarySeeder.FirstSessionDate, overview.FirstSessionDate);
        Assert.Equal(LibrarySeeder.LastSessionDate, overview.LastSessionDate);
    }

    [Fact]
    public void Overview_IgnoresCalibrationFrames()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 13");
        library.AddFrame(target.Id, Day);
        library.AddFrame(target.Id, Day, image => image.ImageType = "DARK");
        library.AddFrame(target.Id, Day, image => image.ImageType = "FLAT");

        var overview = library.Query.Get().Overview;

        Assert.Equal(1, overview.TotalFrames);
        Assert.Equal(LibrarySeeder.ExposureSeconds, overview.TotalIntegrationSeconds);
    }

    // ---- equipment inventory -----------------------------------------------------------

    [Fact]
    public void Cameras_OneRowPerCanonicalName_OrderedByFrameCountDescending()
    {
        using var library = StatsLibrary.Seeded();

        var cameras = library.Query.Get().Cameras;

        var expected = Enumerable.Range(0, LibrarySeeder.Rigs.Count)
            .OrderByDescending(rig => LibrarySeeder.FramesPerRig[rig])
            .ToList();
        Assert.Equal(expected.Count, cameras.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(LibrarySeeder.Rigs[expected[i]].Camera, cameras[i].Name);
            Assert.Equal(LibrarySeeder.FramesPerRig[expected[i]], cameras[i].FrameCount);
            Assert.Equal(
                LibrarySeeder.FramesPerRig[expected[i]] * LibrarySeeder.ExposureSeconds,
                cameras[i].IntegrationSeconds);
            Assert.Equal(LibrarySeeder.TargetsPerRig[expected[i]], cameras[i].TargetCount);
        }
    }

    [Fact]
    public void Telescopes_OneRowPerCanonicalName()
    {
        using var library = StatsLibrary.Seeded();

        var telescopes = library.Query.Get().Telescopes;

        Assert.Equal(LibrarySeeder.Rigs.Count, telescopes.Count);
        foreach (var (index, rig) in LibrarySeeder.Rigs.Index())
        {
            var row = telescopes.Single(item => item.Name == rig.Telescope);
            Assert.Equal(LibrarySeeder.FramesPerRig[index], row.FrameCount);
        }
    }

    [Fact]
    public void Inventory_FoldsAliasesOntoOneCanonicalRow_AndMarksItGrouped()
    {
        using var library = StatsLibrary.Empty();
        library.ConfigureEquipment("RC8", "ASI2600MM");
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, Rig("RC8", "ASI2600MM"));
        library.AddFrame(target.Id, Day, Rig("GSO RC8", "ZWO ASI2600MM Pro"));

        var response = library.Query.Get();

        var camera = Assert.Single(response.Cameras);
        Assert.Equal("ASI2600MM", camera.Name);
        Assert.Equal(2, camera.FrameCount);
        Assert.True(camera.Grouped);
        Assert.True(Assert.Single(response.Telescopes).Grouped);
    }

    [Fact]
    public void Inventory_NotGrouped_WhenOnlyOneRawNameFoldedIn()
    {
        using var library = StatsLibrary.Empty();
        library.ConfigureEquipment("RC8", "ASI2600MM");
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, Rig("GSO RC8", "ZWO ASI2600MM Pro"));
        library.AddFrame(target.Id, Day, Rig("GSO RC8", "ZWO ASI2600MM Pro"));

        var camera = Assert.Single(library.Query.Get().Cameras);

        Assert.Equal("ASI2600MM", camera.Name);
        Assert.False(camera.Grouped);
    }

    [Fact]
    public void Inventory_Nights_CountsDistinctSessionDates()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, Rig("RC8", "ASI2600MM"));
        library.AddFrame(target.Id, Day, Rig("RC8", "ASI2600MM"));
        library.AddFrame(target.Id, NextDay, Rig("RC8", "ASI2600MM"));

        var camera = Assert.Single(library.Query.Get().Cameras);

        Assert.Equal(2, camera.Nights);
        Assert.Equal(3, camera.FrameCount);
    }

    [Fact]
    public void Inventory_AvgSessionSeconds_IsIntegrationOverNights()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, Rig("RC8", "ASI2600MM"));
        library.AddFrame(target.Id, Day, Rig("RC8", "ASI2600MM"));
        library.AddFrame(target.Id, NextDay, Rig("RC8", "ASI2600MM"));

        var camera = Assert.Single(library.Query.Get().Cameras);

        Assert.Equal(3 * LibrarySeeder.ExposureSeconds / 2, camera.AvgSessionSeconds!.Value, 6);
    }

    [Fact]
    public void Inventory_AvgSessionSeconds_IsNull_WhenThereAreNoNights()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, image =>
        {
            Rig("RC8", "ASI2600MM")(image);
            image.SessionDate = null;
        });

        var camera = Assert.Single(library.Query.Get().Cameras);

        Assert.Equal(0, camera.Nights);
        Assert.Null(camera.AvgSessionSeconds);
    }

    [Fact]
    public void Inventory_MedianFwhm_IgnoresZeroAndNullValues_AndCountsOnlyTheOnesItUsed()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image => image.Fwhm = 2.0);
        AddMetric(library, target.Id, image => image.Fwhm = 4.0);
        AddMetric(library, target.Id, image => image.Fwhm = 0d);
        AddMetric(library, target.Id, image => image.Fwhm = null);

        var camera = Assert.Single(library.Query.Get().Cameras);

        // nullif(fwhm, 0): a stored zero is "not measured", exactly as a null is.
        Assert.Equal(3.0, camera.MedianFwhmArcsec!.Value, 6);
        Assert.Equal(2, camera.FwhmFrameCount);
        Assert.Equal(4, camera.FrameCount);
    }

    [Fact]
    public void Inventory_MedianGuidingRms_IgnoresZeroValues()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image => image.GuidingRmsArcsec = 0.4);
        AddMetric(library, target.Id, image => image.GuidingRmsArcsec = 0.6);
        AddMetric(library, target.Id, image => image.GuidingRmsArcsec = 0d);

        var camera = Assert.Single(library.Query.Get().Cameras);

        Assert.Equal(0.5, camera.MedianGuidingRmsArcsec!.Value, 6);
    }

    [Fact]
    public void Inventory_SkipsFramesWithANullRawName()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, image => image.Telescope = "RC8");

        var response = library.Query.Get();

        Assert.Empty(response.Cameras);
        Assert.Single(response.Telescopes);
    }

    // ---- equipment performance ---------------------------------------------------------

    [Fact]
    public void Performance_OneRowPerCanonicalTelescopeAndCameraPair_OrderedByFrameCountDescending()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, Rig("FRA600", "ASI294MC"));
        library.AddFrame(target.Id, Day, Rig("RC8", "ASI2600MM"));
        library.AddFrame(target.Id, Day, Rig("RC8", "ASI2600MM"));

        var performance = library.Query.Get().EquipmentPerformance;

        Assert.Equal(2, performance.Count);
        Assert.Equal("RC8", performance[0].Telescope);
        Assert.Equal("ASI2600MM", performance[0].Camera);
        Assert.Equal(2, performance[0].FrameCount);
        Assert.Equal("FRA600", performance[1].Telescope);
        Assert.Equal(1, performance[1].FrameCount);
    }

    [Fact]
    public void Performance_MediansAreTheCanonicalGroupMedians_NotABlendOfTheFilterMedians()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        foreach (var value in new[] { 1.0, 2.0, 3.0 })
        {
            AddMetric(library, target.Id, image =>
            {
                image.FilterUsed = "L";
                image.MedianHfr = value;
                image.Fwhm = value;
            });
        }

        AddMetric(library, target.Id, image =>
        {
            image.FilterUsed = "R";
            image.MedianHfr = 10.0;
            image.Fwhm = 10.0;
        });

        var response = library.Query.Get();
        var combo = Assert.Single(response.EquipmentPerformance);

        // The pooled median over {1, 2, 3, 10} is 2.5. The web's weighted_median_approx over the
        // per-filter medians ((2.0, 3 frames), (10.0, 1 frame)) returns 2.0, and a plain average
        // of those medians returns 6.0. Only the pooled figure agrees with the inventory, which is
        // the entire reason _query_equipment_mad exists.
        Assert.Equal(2.5, combo.MedianHfr!.Value, 6);
        Assert.NotEqual(2.0, combo.MedianHfr!.Value);
        Assert.NotEqual(6.0, combo.MedianHfr!.Value);

        // And the consequence that grouping exists for, in the source's own words: "the FWHM here
        // equals the Inventory figure". The two filters carry different FWHM distributions, so a
        // per-filter blend would report 2.0 here and 2.5 in the inventory and the two tables on the
        // page would disagree about one rig.
        var camera = Assert.Single(response.Cameras);
        Assert.Equal(2.5, combo.MedianFwhm!.Value, 6);
        Assert.Equal(camera.MedianFwhmArcsec!.Value, combo.MedianFwhm!.Value, 6);
    }

    [Fact]
    public void Performance_MedianGuidingRms_IgnoresZeroAndNullValues()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image => image.GuidingRmsArcsec = 0.4);
        AddMetric(library, target.Id, image => image.GuidingRmsArcsec = 0.6);
        AddMetric(library, target.Id, image => image.GuidingRmsArcsec = 0d);
        AddMetric(library, target.Id, image => image.GuidingRmsArcsec = null);

        var response = library.Query.Get();
        var combo = Assert.Single(response.EquipmentPerformance);

        // Spec 13's equipment comparison chart needs this per combination, which is a category the
        // two inventory lists cannot express. Same nullif(guiding_rms_arcsec, 0) rule as theirs, so
        // a single-rig library reports one number on both surfaces.
        Assert.Equal(0.5, combo.MedianGuidingRmsArcsec!.Value, 6);
        Assert.Equal(
            Assert.Single(response.Cameras).MedianGuidingRmsArcsec!.Value,
            combo.MedianGuidingRmsArcsec!.Value,
            6);
    }

    [Fact]
    public void Performance_BestHfr_IsTheMinimumNonZeroValue()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image => image.MedianHfr = 0d);
        AddMetric(library, target.Id, image => image.MedianHfr = 1.5);
        AddMetric(library, target.Id, image => image.MedianHfr = 2.0);

        var combo = Assert.Single(library.Query.Get().EquipmentPerformance);

        Assert.Equal(1.5, combo.BestHfr!.Value, 6);
    }

    [Fact]
    public void Performance_Mad_IsTheRawMedianAbsoluteDeviation_WithNoScaling()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        foreach (var hfr in new[] { 1.0, 2.0, 3.0, 4.0 })
        {
            AddMetric(library, target.Id, image => image.MedianHfr = hfr);
        }

        var combo = Assert.Single(library.Query.Get().EquipmentPerformance);

        // Median 2.5, absolute deviations {1.5, 0.5, 0.5, 1.5}, median 1.0. The 1.4826 consistency
        // scaling that would make this 1.4826 is deliberately absent everywhere in the port.
        Assert.Equal(1.0, combo.MadHfr!.Value, 6);
    }

    [Fact]
    public void Performance_MadsRoundToThreeDecimals_AndMediansToTwo()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image => image.MedianHfr = 1.11111);
        AddMetric(library, target.Id, image => image.MedianHfr = 2.22222);

        var combo = Assert.Single(library.Query.Get().EquipmentPerformance);

        // Median 1.666665 rounds to 1.67; the MAD of 0.555555 rounds to 0.556.
        Assert.Equal(1.67, combo.MedianHfr!.Value, 6);
        Assert.Equal(0.556, combo.MadHfr!.Value, 6);
    }

    [Fact]
    public void Performance_AvgSessionSeconds_IsIntegrationOverDistinctSessions()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, Rig("RC8", "ASI2600MM"));
        library.AddFrame(target.Id, Day, Rig("RC8", "ASI2600MM"));
        library.AddFrame(target.Id, NextDay, Rig("RC8", "ASI2600MM"));

        var combo = Assert.Single(library.Query.Get().EquipmentPerformance);

        Assert.Equal(3 * LibrarySeeder.ExposureSeconds / 2, combo.AvgSessionSeconds!.Value, 6);
    }

    [Fact]
    public void Performance_Grouped_WhenTwoRawTelescopesFoldOntoOneCanonicalName()
    {
        using var library = StatsLibrary.Empty();
        library.ConfigureEquipment("RC8", "ASI2600MM");
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, Rig("RC8", "ASI2600MM"));
        library.AddFrame(target.Id, Day, Rig("GSO RC8", "ASI2600MM"));

        var combo = Assert.Single(library.Query.Get().EquipmentPerformance);

        Assert.Equal("RC8", combo.Telescope);
        Assert.Equal(2, combo.FrameCount);
        Assert.True(combo.Grouped);
    }

    [Fact]
    public void Performance_FilterBreakdown_OrderedByFrameCountDescending()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image => image.FilterUsed = "R");
        foreach (var _ in Enumerable.Range(0, 3))
        {
            AddMetric(library, target.Id, image => image.FilterUsed = "L");
        }

        var breakdown = Assert.Single(library.Query.Get().EquipmentPerformance).FilterBreakdown;

        Assert.Equal(new[] { "L", "R" }, breakdown.Select(entry => entry.FilterName));
        Assert.Equal(3, breakdown[0].FrameCount);
        Assert.Equal(3 * LibrarySeeder.ExposureSeconds, breakdown[0].IntegrationSeconds);
    }

    [Fact]
    public void Performance_FrameWithNoFilter_CountsInTheComboTotals_ButInNoFilterEntry()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image => image.FilterUsed = "L");
        AddMetric(library, target.Id, image => image.FilterUsed = null);

        var combo = Assert.Single(library.Query.Get().EquipmentPerformance);

        Assert.Equal(2, combo.FrameCount);
        Assert.Equal(2 * LibrarySeeder.ExposureSeconds, combo.IntegrationSeconds);
        var filter = Assert.Single(combo.FilterBreakdown);
        Assert.Equal("L", filter.FilterName);
        Assert.Equal(1, filter.FrameCount);
    }

    [Fact]
    public void Performance_SkipsFramesWithANullTelescopeOrCamera()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, image => image.Telescope = "RC8");
        library.AddFrame(target.Id, Day, image => image.Camera = "ASI2600MM");

        Assert.Empty(library.Query.Get().EquipmentPerformance);
    }

    // ---- filter usage, top targets, timelines --------------------------------------------

    [Fact]
    public void FilterUsage_SumsPerCanonicalFilter_OrderedByIntegrationDescending()
    {
        using var library = StatsLibrary.Empty();
        library.Settings.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["L"] = new() { Color = "#cccccc", Aliases = ["Lum"] },
        });
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, image => image.FilterUsed = "L");
        library.AddFrame(target.Id, Day, image => image.FilterUsed = "Lum");
        library.AddFrame(target.Id, Day, image => image.FilterUsed = "Ha");

        var usage = library.Query.Get().FilterUsage;

        Assert.Equal(new[] { "L", "Ha" }, usage.Select(entry => entry.FilterName));
        Assert.Equal(2 * LibrarySeeder.ExposureSeconds, usage[0].IntegrationSeconds);
        Assert.Equal(LibrarySeeder.ExposureSeconds, usage[1].IntegrationSeconds);
    }

    [Fact]
    public void FilterUsage_SkipsFramesWithNoFilter()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, image => image.FilterUsed = "L");
        library.AddFrame(target.Id, Day);

        var entry = Assert.Single(library.Query.Get().FilterUsage);

        Assert.Equal("L", entry.FilterName);
        Assert.Equal(LibrarySeeder.ExposureSeconds, entry.IntegrationSeconds);
    }

    [Fact]
    public void TopTargets_OrderedByIntegrationDescending_LimitedToTwenty()
    {
        using var library = StatsLibrary.Empty();
        for (var i = 0; i < StatsQuery.TopTargetLimit + 2; i++)
        {
            var exposure = 100d + i;
            var target = library.AddTarget($"T{i:D2}");
            library.AddFrame(target.Id, Day, image => image.ExposureTime = exposure);
        }

        var top = library.Query.Get().TopTargets;

        Assert.Equal(StatsQuery.TopTargetLimit, top.Count);
        Assert.Equal($"T{StatsQuery.TopTargetLimit + 1:D2}", top[0].PrimaryName);
        Assert.Equal(100d + StatsQuery.TopTargetLimit + 1, top[0].IntegrationSeconds);
        Assert.True(top[0].IntegrationSeconds > top[^1].IntegrationSeconds);
    }

    [Fact]
    public void TopTargets_ExcludesUnresolvedFrames()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day);
        library.AddFrame(null, Day);

        var entry = Assert.Single(library.Query.Get().TopTargets);

        Assert.Equal("M 42", entry.PrimaryName);
        Assert.Equal(LibrarySeeder.ExposureSeconds, entry.IntegrationSeconds);
    }

    [Fact]
    public void TimelineMonthly_GroupsBySessionDateMonth_AscendingByLabel()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, new DateOnly(2025, 3, 1));
        library.AddFrame(target.Id, new DateOnly(2025, 3, 28));
        library.AddFrame(target.Id, new DateOnly(2025, 1, 4));

        var monthly = library.Query.Get().TimelineMonthly;

        Assert.Equal(new[] { "2025-01", "2025-03" }, monthly.Select(entry => entry.Period));
        Assert.Equal(2 * LibrarySeeder.ExposureSeconds, monthly[1].IntegrationSeconds);
    }

    [Fact]
    public void TimelineDaily_GroupsBySessionDate()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day);
        library.AddFrame(target.Id, Day);
        library.AddFrame(target.Id, NextDay);

        var daily = library.Query.Get().TimelineDaily;

        Assert.Equal(new[] { "2025-03-01", "2025-03-02" }, daily.Select(entry => entry.Period));
        Assert.Equal(2 * LibrarySeeder.ExposureSeconds, daily[0].IntegrationSeconds);
    }

    [Fact]
    public void TimelineWeekly_UsesIsoYearAndIsoWeek_ZeroPaddedToTwoDigits()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        var monday = new DateOnly(2025, 1, 13);
        library.AddFrame(target.Id, monday);
        library.AddFrame(target.Id, monday.AddDays(2));

        var entry = Assert.Single(library.Query.Get().TimelineWeekly);

        // Two digits, so ImagingTimeline.tsx's "2025-W03" parser still sees the shape
        // to_char(session_date, 'IYYY-"W"IW') produced.
        Assert.Equal("2025-W03", entry.Period);
        Assert.Equal(2 * LibrarySeeder.ExposureSeconds, entry.IntegrationSeconds);
    }

    [Fact]
    public void TimelineWeekly_ADateInEarlyJanuary_LandsInThePreviousIsoYear()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        var newYearsDay = new DateOnly(2021, 1, 1);
        library.AddFrame(target.Id, newYearsDay);

        var entry = Assert.Single(library.Query.Get().TimelineWeekly);

        var midnight = newYearsDay.ToDateTime(TimeOnly.MinValue);
        var expected = string.Create(
            CultureInfo.InvariantCulture,
            $"{ISOWeek.GetYear(midnight):D4}-W{ISOWeek.GetWeekOfYear(midnight):D2}");
        Assert.Equal("2020-W53", expected);
        Assert.Equal(expected, entry.Period);
    }

    [Fact]
    public void Timelines_IgnoreFramesWithNoSessionDate()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, image => image.SessionDate = null);

        var response = library.Query.Get();

        Assert.Empty(response.TimelineMonthly);
        Assert.Empty(response.TimelineWeekly);
        Assert.Empty(response.TimelineDaily);
    }

    // ---- rigs per night ------------------------------------------------------------------

    [Fact]
    public void RigsPerNight_CountsDistinctCanonicalRigsPerSessionDate()
    {
        using var library = StatsLibrary.Empty();
        library.ConfigureEquipment("RC8", "ASI2600MM");
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, Rig("RC8", "ASI2600MM"));
        library.AddFrame(target.Id, Day, Rig("GSO RC8", "ZWO ASI2600MM Pro"));
        library.AddFrame(target.Id, Day, Rig("FRA600", "ASI294MC"));
        library.AddFrame(target.Id, NextDay, Rig("RC8", "ASI2600MM"));

        var rigs = library.Query.Get().RigsPerNight;

        // The two spellings of the first rig fold onto one canonical rig, so the night holds two.
        Assert.Equal(2, rigs[Day]);
        Assert.Equal(1, rigs[NextDay]);
    }

    // ---- data quality ---------------------------------------------------------------------

    [Fact]
    public void DataQuality_AvgHfr_IsTheMeanOfEveryNonNullMedianHfr()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image => image.MedianHfr = 2.0);
        AddMetric(library, target.Id, image => image.MedianHfr = 3.0);
        AddMetric(library, target.Id, image => image.MedianHfr = null);

        var quality = library.Query.Get().DataQuality;

        Assert.Equal(2.5, quality.AvgHfr!.Value, 6);
    }

    [Fact]
    public void DataQuality_BestHfr_IsTheMinimum()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image => image.MedianHfr = 2.0);
        AddMetric(library, target.Id, image => image.MedianHfr = 1.25);

        Assert.Equal(1.25, library.Query.Get().DataQuality.BestHfr!.Value, 6);
    }

    [Fact]
    public void DataQuality_ArcsecFigures_UseOnlyPlateScaledFrames()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image =>
        {
            image.MedianHfr = 2.0;
            image.ArcsecPerPixel = 1.5;
        });
        AddMetric(library, target.Id, image =>
        {
            image.MedianHfr = 4.0;
            image.ArcsecPerPixel = 1.5;
        });
        AddMetric(library, target.Id, image =>
        {
            image.MedianHfr = 100.0;
            image.ArcsecPerPixel = null;
        });

        var quality = library.Query.Get().DataQuality;

        Assert.Equal(4.5, quality.AvgHfrArcsec!.Value, 6);
        Assert.Equal(3.0, quality.BestHfrArcsec!.Value, 6);
        Assert.Equal(1, quality.UnscaledFrameCount);
    }

    [Fact]
    public void DataQuality_UnscaledFrameCount_IsTheSeededConstant()
    {
        using var library = StatsLibrary.Seeded();

        Assert.Equal(
            LibrarySeeder.FramesWithoutPlateScale,
            library.Query.Get().DataQuality.UnscaledFrameCount);
    }

    [Fact]
    public void DataQuality_AvgEccentricity_UsesTheModalSourceOnly()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        foreach (var value in new[] { 0.30, 0.40 })
        {
            AddMetric(library, target.Id, image =>
            {
                image.Eccentricity = value;
                image.EccentricitySource = LibrarySeeder.EccentricitySourceHeader;
            });
        }

        AddMetric(library, target.Id, image =>
        {
            image.Eccentricity = 0.90;
            image.EccentricitySource = LibrarySeeder.EccentricitySourceMinority;
        });

        var quality = library.Query.Get().DataQuality;

        // Spec 7.2: the minority source measures eccentricity differently, so it is excluded
        // rather than averaged in. The pooled mean of all three would be 0.53.
        Assert.Equal(LibrarySeeder.EccentricitySourceHeader, quality.EccentricitySource);
        Assert.Equal(0.35, quality.AvgEccentricity!.Value, 6);
        Assert.Equal(1, quality.EccentricityExcludedCount);
    }

    [Fact]
    public void DataQuality_EccentricityExcludedCount_IsTheSeededMinorityCount()
    {
        using var library = StatsLibrary.Seeded();

        var quality = library.Query.Get().DataQuality;

        Assert.Equal(LibrarySeeder.EccentricitySourceHeader, quality.EccentricitySource);
        Assert.Equal(
            LibrarySeeder.FramesWithMinorityEccentricitySource,
            quality.EccentricityExcludedCount);
    }

    [Fact]
    public void DataQuality_ModalSourceTie_PrefersTheNonNullSource_ThenOrdinalByName()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image =>
        {
            image.Eccentricity = 0.5;
            image.EccentricitySource = null;
        });
        AddMetric(library, target.Id, image =>
        {
            image.Eccentricity = 0.5;
            image.EccentricitySource = "header";
        });
        AddMetric(library, target.Id, image =>
        {
            image.Eccentricity = 0.5;
            image.EccentricitySource = "csv";
        });

        // Three sources, one frame each: the null source sorts last and "csv" sorts before
        // "header", so the tie goes to "csv".
        Assert.Equal("csv", library.Query.Get().DataQuality.EccentricitySource);
    }

    [Fact]
    public void DataQuality_NoEccentricityAtAll_ReportsNoSourceAndANullExcludedCount()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image => image.MedianHfr = 2.0);

        var quality = library.Query.Get().DataQuality;

        // The web's pooled-average fallback is structurally unreachable in this port rather than
        // untested: the per-source grouping and the pooled average draw from the same set, so the
        // only way to have no source to pool on is to have no eccentricity value at all, and then
        // the pooled mean is null too. The excluded count is null rather than zero because nothing
        // was excluded from a pool that does not exist.
        Assert.Null(quality.EccentricitySource);
        Assert.Null(quality.AvgEccentricity);
        Assert.Null(quality.EccentricityExcludedCount);
    }

    [Fact]
    public void DataQuality_PixelHistogram_UsesTheEightWebBucketBoundaries()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image => image.MedianHfr = 0.5);
        AddMetric(library, target.Id, image => image.MedianHfr = 2.0);
        AddMetric(library, target.Id, image => image.MedianHfr = 2.4999);
        AddMetric(library, target.Id, image => image.MedianHfr = 6.0);

        var buckets = library.Query.Get().DataQuality.HfrPixelHistogram;

        Assert.Equal(
            new[] { "0.0-1.0", "1.0-1.5", "1.5-2.0", "2.0-2.5", "2.5-3.0", "3.0-4.0", "4.0-5.0", "5.0+" },
            buckets.Select(bucket => bucket.Label));
        Assert.Equal(1, buckets[0].FrameCount);
        Assert.Equal(2, buckets[3].FrameCount);
        Assert.Equal(1, buckets[7].FrameCount);
    }

    [Fact]
    public void DataQuality_PixelHistogram_LastBucketIsLabelledFivePointZeroPlus()
    {
        using var library = StatsLibrary.Empty();

        var last = library.Query.Get().DataQuality.HfrPixelHistogram[^1];

        Assert.Equal("5.0+", last.Label);
        Assert.Equal(5.0, last.Low);
        Assert.Equal(100d, last.High);
    }

    [Fact]
    public void DataQuality_PixelHistogram_KeepsZeroCountBuckets()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image => image.MedianHfr = 2.0);

        var buckets = library.Query.Get().DataQuality.HfrPixelHistogram;

        // The web source drops empty buckets; the port keeps the axis whole (questions.md Q4).
        Assert.Equal(8, buckets.Count);
        Assert.Equal(7, buckets.Count(bucket => bucket.FrameCount == 0));
    }

    [Fact]
    public void DataQuality_ArcsecHistogram_HasSixteenHalfArcsecondBucketsPlusAnOverflow()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image =>
        {
            image.MedianHfr = 2.0;
            image.ArcsecPerPixel = 1.0;
        });

        var buckets = library.Query.Get().DataQuality.HfrArcsecHistogram;

        Assert.Equal(17, buckets.Count);
        Assert.Equal("0.0-0.5", buckets[0].Label);
        Assert.Equal("7.5-8.0", buckets[15].Label);
        Assert.Equal(0.5, buckets[1].Low);
        // 2.0 arcseconds lands in [2.0, 2.5), which is index 4.
        Assert.Equal(1, buckets[4].FrameCount);
        Assert.Equal(16, buckets.Count(bucket => bucket.FrameCount == 0));
    }

    [Fact]
    public void DataQuality_ArcsecHistogram_OverflowIsEightPointZeroPlus()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image =>
        {
            image.MedianHfr = 20.0;
            image.ArcsecPerPixel = 1.0;
        });

        var overflow = library.Query.Get().DataQuality.HfrArcsecHistogram[^1];

        Assert.Equal("8.0+", overflow.Label);
        Assert.Equal(8.0, overflow.Low);
        Assert.Null(overflow.High);
        Assert.Equal(1, overflow.FrameCount);
    }

    [Fact]
    public void DataQuality_AValueOfExactlyZero_IsNotTurnedIntoNull()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        AddMetric(library, target.Id, image =>
        {
            image.MedianHfr = 0d;
            image.ArcsecPerPixel = 1.0;
            image.Eccentricity = 0d;
            image.EccentricitySource = LibrarySeeder.EccentricitySourceHeader;
        });

        var quality = library.Query.Get().DataQuality;

        // The web rounds with `round(x, 2) if x else None`, a falsy test that turns an exact 0.0
        // into "not measured". That is a defect, not a rule (questions.md Q9).
        Assert.Equal(0d, quality.AvgHfr!.Value);
        Assert.Equal(0d, quality.BestHfr!.Value);
        Assert.Equal(0d, quality.AvgHfrArcsec!.Value);
        Assert.Equal(0d, quality.BestHfrArcsec!.Value);
        Assert.Equal(0d, quality.AvgEccentricity!.Value);
    }

    // ---- storage and ingest history --------------------------------------------------------

    [Fact]
    public void Storage_FitsBytesCatalogued_SumsEveryImageFileSize_IncludingCalibration()
    {
        using var library = StatsLibrary.Empty();
        var target = library.AddTarget("M 42");
        library.AddFrame(target.Id, Day, image => image.FileSize = 1000);
        library.AddFrame(target.Id, Day, image =>
        {
            image.ImageType = "DARK";
            image.FileSize = 250;
        });
        library.AddFrame(target.Id, Day, image => image.FileSize = null);

        Assert.Equal(1250, library.Query.Get().Storage.FitsBytesCatalogued);
    }

    [Fact]
    public void Storage_ThumbnailCacheBytes_IsZero_WhenNoDelegateIsSupplied()
    {
        using var library = StatsLibrary.Empty();

        Assert.Equal(0, library.Query.Get().Storage.ThumbnailCacheBytes);
    }

    [Fact]
    public void Storage_ThumbnailCacheBytes_ComesFromTheSuppliedDelegate()
    {
        using var library = StatsLibrary.Empty();

        Assert.Equal(4242, library.Query.Get(() => 4242).Storage.ThumbnailCacheBytes);
    }

    [Fact]
    public void Storage_DatabaseBytes_IsTheDatabaseFilePlusItsWriteAheadLog()
    {
        using var library = StatsLibrary.Seeded();

        // Checkpointed first so the seeded pages are in the database file rather than still in the
        // log, which is what makes page_count * page_size and the file's own length the same
        // number and lets this assert against the file rather than against the implementation's
        // own pragma.
        using (var context = new GalactiLogContext(
            GalactiLogContextOptions.Create(library.ConnectionString, tracking: true)))
        {
            context.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");
        }

        var reported = library.Query.Get().Storage.DatabaseBytes;

        var wal = library.DatabasePath + "-wal";
        var expected = new FileInfo(library.DatabasePath).Length
            + (File.Exists(wal) ? new FileInfo(wal).Length : 0);

        Assert.Equal(expected, reported);
        Assert.True(reported > 0);
    }

    [Fact]
    public void IngestHistory_SumsNewFilesPerCompletedRunDate_NewestThirty_ReturnedOldestFirst()
    {
        using var library = StatsLibrary.Empty();
        var first = new DateTime(2025, 1, 1, 20, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < StatsQuery.IngestHistoryDays + 2; i++)
        {
            library.AddScanRun(first.AddDays(i), "complete", newFiles: i + 1);
        }

        // A second completed run on the newest day: the chart's row is the day's total, not one
        // row per run.
        library.AddScanRun(first.AddDays(StatsQuery.IngestHistoryDays + 1).AddHours(2), "complete", newFiles: 5);

        var history = library.Query.Get().IngestHistory;

        Assert.Equal(StatsQuery.IngestHistoryDays, history.Count);
        Assert.Equal(DateOnly.FromDateTime(first.AddDays(2)), history[0].Date);
        Assert.Equal(DateOnly.FromDateTime(first.AddDays(StatsQuery.IngestHistoryDays + 1)), history[^1].Date);
        Assert.Equal(StatsQuery.IngestHistoryDays + 2 + 5, history[^1].FilesAdded);
        Assert.True(history[0].Date < history[^1].Date);
    }

    [Fact]
    public void IngestHistory_IgnoresCancelledAndFailedRuns()
    {
        using var library = StatsLibrary.Empty();
        var day = new DateTime(2025, 2, 3, 21, 0, 0, DateTimeKind.Utc);
        library.AddScanRun(day, "complete", newFiles: 7);
        library.AddScanRun(day.AddHours(1), "cancelled", newFiles: 100);
        library.AddScanRun(day.AddHours(2), "failed", newFiles: 100);
        library.AddScanRun(day.AddHours(3), "running", newFiles: 100);

        var entry = Assert.Single(library.Query.Get().IngestHistory);

        Assert.Equal(DateOnly.FromDateTime(day), entry.Date);
        Assert.Equal(7, entry.FilesAdded);
    }

    // ---- shared fixture helpers ------------------------------------------------------------

    /// <summary>A frame carrying an equipment pair, so a metric case lands in exactly one
    /// inventory row and one performance row.</summary>
    internal static Action<Image> Rig(string telescope, string camera)
        => image =>
        {
            image.Telescope = telescope;
            image.Camera = camera;
        };

    /// <summary>One frame on <see cref="Day"/> with the default rig plus the caller's metric
    /// values, which is the shape every median, MAD and histogram case needs.</summary>
    private static void AddMetric(StatsLibrary library, Guid targetId, Action<Image> configure)
        => library.AddFrame(targetId, Day, image =>
        {
            Rig("RC8", "ASI2600MM")(image);
            configure(image);
        });
}

/// <summary>
/// A migrated database plus the alias map cache and the query under test, shared by the three
/// stats test classes. Mirrors <c>SessionDetailQueryTests.Library</c>: one place that knows how a
/// stats fixture is assembled, so a new case adds frames rather than wiring.
/// </summary>
internal sealed class StatsLibrary : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly AliasMapCache _aliases;

    private StatsLibrary(TestDatabaseHandle db, string path)
    {
        _db = db;
        DatabasePath = path;
        Settings = new SettingsStore(new SettingsRepository(db.ConnectionString));
        _aliases = new AliasMapCache(Settings);
        var connectionString = new DatabaseConnectionString(db.ConnectionString);
        Query = new StatsQuery(
            connectionString,
            _aliases,
            new GuidingStatsQuery(connectionString, _aliases, () => Settings.GetGeneral().Phd2ProfileMap));
    }

    public string ConnectionString => _db.ConnectionString;

    /// <summary>The database file itself, for the one assertion that measures it.</summary>
    public string DatabasePath { get; }

    public SettingsStore Settings { get; }

    public StatsQuery Query { get; }

    public static StatsLibrary Empty()
    {
        var db = TestDatabaseFactory.CreateMigratedDatabase();
        return new StatsLibrary(db, new SqliteConnectionStringBuilder(db.ConnectionString).DataSource);
    }

    public static StatsLibrary Seeded()
    {
        var library = Empty();
        LibrarySeeder.Seed(library.ConnectionString);
        return library;
    }

    public Target AddTarget(string primaryName, Action<Target>? configure = null)
        => LibrarySeeder.AddTarget(ConnectionString, primaryName, configure);

    public Image AddFrame(Guid? targetId, DateOnly sessionDate, Action<Image>? configure = null)
        => LibrarySeeder.AddFrame(ConnectionString, targetId, sessionDate, configure);

    /// <summary>Configures one canonical telescope and one canonical camera with the alias
    /// spellings the grouping cases use, and lets <see cref="AliasMapCache"/> rebuild through the
    /// event it already subscribes to.</summary>
    public void ConfigureEquipment(string telescope, string camera)
        => Settings.SaveEquipment(new EquipmentSettings
        {
            Telescopes = new Dictionary<string, EquipmentItemSettings>
            {
                [telescope] = new() { Aliases = ["GSO " + telescope] },
            },
            Cameras = new Dictionary<string, EquipmentItemSettings>
            {
                [camera] = new() { Aliases = ["ZWO " + camera + " Pro"] },
            },
        });

    /// <summary>One <c>scan_runs</c> row with a chosen start time and state, which
    /// <c>ScanRunRepository.Start</c> cannot express because it stamps <c>UtcNow</c>.</summary>
    public void AddScanRun(DateTime startedAt, string state, int newFiles)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(ConnectionString, tracking: true));
        context.ScanRuns.Add(new ScanRun
        {
            StartedAt = startedAt,
            FinishedAt = startedAt.AddMinutes(1),
            Trigger = "manual",
            State = state,
            NewFiles = newFiles,
            Completed = newFiles,
        });
        context.SaveChanges();
    }

    public void Dispose()
    {
        _aliases.Dispose();
        _db.Dispose();
    }
}
