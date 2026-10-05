using GalactiLog.Core.Aliases;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

/// <summary>The night by filter rows and the frame points of
/// <see cref="TargetDetailQuery"/>, pinned by their invariants against the session rows and the
/// totals the same query already returns.</summary>
public sealed class TargetDetailQueryNightFilterTests : IDisposable
{
    private static readonly DateOnly Night = new(2025, 3, 1);

    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();
    private readonly TargetDetailQuery _query;

    public TargetDetailQueryNightFilterTests()
    {
        var settings = new SettingsStore(new SettingsRepository(_db.ConnectionString));
        _query = new TargetDetailQuery(
            new DatabaseConnectionString(_db.ConnectionString),
            new AliasMapCache(settings),
            () => settings.GetGeneral().Phd2ProfileMap);
    }

    public void Dispose() => _db.Dispose();

    private TargetDetail Detail(Guid targetId)
    {
        var detail = _query.Get(targetId.ToString());
        Assert.NotNull(detail);
        return detail;
    }

    private void Frame(Guid targetId, Action<Image> configure, DateOnly? night = null)
        => LibrarySeeder.AddFrame(_db.ConnectionString, targetId, night ?? Night, configure);

    // A failure is a night whose filter rows lose or double count seconds or frames.
    [Fact]
    public void Seeded_filter_rows_of_a_night_sum_to_that_night()
    {
        LibrarySeeder.Seed(_db.ConnectionString);
        foreach (var seeded in LibrarySeeder.Targets)
        {
            var detail = Detail(seeded.Id);
            Assert.Equal(
                detail.Sessions.Select(s => s.SessionDate).Order(),
                detail.NightFilters.Select(r => r.SessionDate).Distinct().Order());
            foreach (var session in detail.Sessions)
            {
                var rows = detail.NightFilters.Where(r => r.SessionDate == session.SessionDate).ToList();
                Assert.Equal(session.IntegrationSeconds, rows.Sum(r => r.IntegrationSeconds), 6);
                Assert.Equal(session.FrameCount, rows.Sum(r => r.FrameCount));
                Assert.Equal(session.FrameCount, rows.Sum(r => r.Exposures.Sum(e => e.Frames)));
            }
        }
    }

    // A failure is a filter whose nights disagree with the totals bar, or a key the bar cannot find.
    [Fact]
    public void Seeded_filter_rows_sum_to_the_totals_by_filter()
    {
        LibrarySeeder.Seed(_db.ConnectionString);
        foreach (var seeded in LibrarySeeder.Targets)
        {
            var detail = Detail(seeded.Id);
            var byFilter = detail.NightFilters
                .GroupBy(r => r.Filter)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.IntegrationSeconds));
            Assert.Equal(detail.Totals.IntegrationSecondsByFilter.Keys.Order(), byFilter.Keys.Order());
            foreach (var (filter, seconds) in detail.Totals.IntegrationSecondsByFilter)
            {
                Assert.Equal(seconds, byFilter[filter], 6);
            }
        }
    }

    // A failure is a frame point missing or duplicated, out of capture order, or a night whose
    // points give a different median from its ledger row (eccentricity included: the minority
    // source night of target 3 must pool only the modal source).
    [Fact]
    public void Seeded_frame_points_are_every_frame_in_capture_order_with_the_night_medians()
    {
        LibrarySeeder.Seed(_db.ConnectionString);
        foreach (var seeded in LibrarySeeder.Targets)
        {
            var detail = Detail(seeded.Id);
            Assert.Equal(detail.Sessions.Sum(s => s.FrameCount), detail.NightFrames.Count);
            Assert.Equal(detail.NightFrames.Select(p => p.CaptureUtc).Order(), detail.NightFrames.Select(p => p.CaptureUtc));
            foreach (var session in detail.Sessions)
            {
                var points = detail.NightFrames.Where(p => p.SessionDate == session.SessionDate).ToList();
                Assert.Equal(session.MedianHfr, Statistics.Median(points.Select(p => p.Hfr)));
                Assert.Equal(session.MedianEccentricity, Statistics.Median(points.Select(p => p.Eccentricity)));
                Assert.Equal(session.MedianFwhm, Statistics.Median(points.Select(p => p.Fwhm)));
                Assert.Equal(session.MedianGuidingRmsArcsec, Statistics.Median(points.Select(p => p.GuidingRms)));
                Assert.Equal(session.MedianDetectedStars, Statistics.Median(points.Select(p => p.DetectedStars)));
            }
        }
    }

    // A failure is a row median taken over the whole night instead of the filter's own frames.
    [Fact]
    public void Medians_are_per_filter()
    {
        var target = LibrarySeeder.AddTarget(_db.ConnectionString, "Per filter").Id;
        foreach (var (hfr, minute) in new[] { (2.0, 0), (3.0, 1), (4.0, 2) })
        {
            Frame(target, i =>
            {
                i.FilterUsed = "Ha";
                i.CaptureDate = Night.ToDateTime(new TimeOnly(22, minute));
                i.MedianHfr = hfr;
                i.Eccentricity = hfr / 10;
                i.EccentricitySource = "header";
                i.Fwhm = hfr + 1;
                i.GuidingRmsArcsec = hfr / 4;
                i.DetectedStars = (int)(hfr * 100);
            });
        }

        Frame(target, i =>
        {
            i.FilterUsed = "OIII";
            i.CaptureDate = Night.ToDateTime(new TimeOnly(23, 0));
            i.MedianHfr = 9.0;
            i.Eccentricity = 0.9;
            i.EccentricitySource = "header";
            i.Fwhm = 10.0;
            i.GuidingRmsArcsec = 2.0;
            i.DetectedStars = 50;
        });

        var ha = Assert.Single(Detail(target).NightFilters, r => r.Filter == "Ha");
        Assert.Equal(3.0, ha.MedianHfr);
        Assert.Equal(0.3, ha.MedianEccentricity!.Value, 9);
        Assert.Equal(4.0, ha.MedianFwhm);
        Assert.Equal(0.75, ha.MedianGuidingRms);
        Assert.Equal(300.0, ha.MedianDetectedStars);
    }

    // A failure is a metric no frame of the filter carries reported as zero instead of null.
    [Fact]
    public void A_metric_with_no_value_is_null_not_zero()
    {
        var target = LibrarySeeder.AddTarget(_db.ConnectionString, "No values").Id;
        Frame(target, i => i.FilterUsed = "Ha");
        Frame(target, i => i.FilterUsed = "Ha");

        var row = Assert.Single(Detail(target).NightFilters);
        Assert.Null(row.MedianHfr);
        Assert.Null(row.MedianEccentricity);
        Assert.Null(row.MedianFwhm);
        Assert.Null(row.MedianGuidingRms);
        Assert.Null(row.MedianDetectedStars);
    }

    // A failure is lengths merged, miscounted, or out of ascending order.
    [Fact]
    public void Exposure_counts_are_per_length_ascending_and_sum_to_the_frames()
    {
        var target = LibrarySeeder.AddTarget(_db.ConnectionString, "Lengths").Id;
        foreach (var seconds in new[] { 300.0, 120.0, 300.0, 600.0, 300.0 })
        {
            Frame(target, i => { i.FilterUsed = "Ha"; i.ExposureTime = seconds; });
        }

        var row = Assert.Single(Detail(target).NightFilters);
        Assert.Equal([new ExposureCount(120, 1), new ExposureCount(300, 3), new ExposureCount(600, 1)], row.Exposures);
        Assert.Equal(row.FrameCount, row.Exposures.Sum(e => e.Frames));
        Assert.Equal(1620, row.IntegrationSeconds);
    }

    // A failure is a minority source eccentricity pooled into the filter's median or a frame point.
    [Fact]
    public void Eccentricity_pools_the_night_modal_source_only()
    {
        var target = LibrarySeeder.AddTarget(_db.ConnectionString, "Sources").Id;
        foreach (var e in new[] { 0.2, 0.3, 0.4 })
        {
            Frame(target, i => { i.FilterUsed = "Ha"; i.Eccentricity = e; i.EccentricitySource = "header"; });
        }

        Frame(target, i => { i.FilterUsed = "Ha"; i.Eccentricity = 0.9; i.EccentricitySource = "ellipticity"; });
        Frame(target, i => { i.FilterUsed = "Ha"; i.Eccentricity = 0.95; i.EccentricitySource = "ellipticity"; });

        var detail = Detail(target);
        Assert.Equal(0.3, Assert.Single(detail.NightFilters).MedianEccentricity!.Value, 9);
        Assert.Equal(2, detail.NightFrames.Count(p => p.Eccentricity is null));
    }

    // A failure is a row or point keyed by its own night's casing, which an ordinal lookup in the
    // totals dictionary cannot find.
    [Fact]
    public void Rows_and_points_carry_the_totals_key_whatever_the_casing()
    {
        var target = LibrarySeeder.AddTarget(_db.ConnectionString, "Casing").Id;
        Frame(target, i => i.FilterUsed = "Ha");
        Frame(target, i => i.FilterUsed = "HA", Night.AddDays(1));

        var detail = Detail(target);
        var key = Assert.Single(detail.Totals.IntegrationSecondsByFilter.Keys);
        Assert.Equal([key, key], detail.NightFilters.Select(r => r.Filter));
        Assert.Equal([key, key], detail.NightFrames.Select(p => p.Filter));
    }

    // A failure is nights oldest first, or filters in ordinal order ("Ha" before "b").
    [Fact]
    public void Rows_are_nights_newest_first_then_filters_by_name_ignoring_case()
    {
        var target = LibrarySeeder.AddTarget(_db.ConnectionString, "Order").Id;
        Frame(target, i => i.FilterUsed = "Ha");
        Frame(target, i => i.FilterUsed = "b");
        Frame(target, i => i.FilterUsed = "Ha", Night.AddDays(1));

        Assert.Equal(
            [(Night.AddDays(1), "Ha"), (Night, "b"), (Night, "Ha")],
            Detail(target).NightFilters.Select(r => (r.SessionDate, r.Filter)));
    }

    // A failure is a filterless frame dropped from the points or put in a row, or an undated frame
    // in either.
    [Fact]
    public void A_filterless_frame_is_a_point_with_an_empty_filter_and_in_no_row()
    {
        var target = LibrarySeeder.AddTarget(_db.ConnectionString, "No filter").Id;
        Frame(target, i => i.FilterUsed = "Ha");
        Frame(target, i => i.FilterUsed = null);
        Frame(target, i => { i.FilterUsed = "Ha"; i.SessionDate = null; });

        var detail = Detail(target);
        var row = Assert.Single(detail.NightFilters);
        Assert.Equal(1, row.FrameCount);
        Assert.Equal(["", "Ha"], detail.NightFrames.Select(p => p.Filter).Order());
    }
}
