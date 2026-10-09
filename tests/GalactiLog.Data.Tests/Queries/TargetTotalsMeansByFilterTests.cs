using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// The Integration tab's Overall metrics: one row per filter of target-wide means, beside the All
// frames row the same helper computes, so the two cannot drift.
public class TargetTotalsMeansByFilterTests
{
    [Fact]
    public void Get_MeansByFilter_AreMeansPerCanonicalFilter()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var query = Query(db.ConnectionString, out var dispose);
        using var _ = dispose;

        var target = LibrarySeeder.AddTarget(db.ConnectionString, "Means");
        Add(db.ConnectionString, target.Id, frame => { frame.FilterUsed = "Ha"; frame.MedianHfr = 2.0; frame.Fwhm = 3.10; frame.GuidingRmsArcsec = 0.41; frame.DetectedStars = 1100; });
        Add(db.ConnectionString, target.Id, frame => { frame.FilterUsed = "Ha"; frame.MedianHfr = 3.0; frame.Fwhm = 4.90; frame.GuidingRmsArcsec = 0.83; frame.DetectedStars = 1900; });
        Add(db.ConnectionString, target.Id, frame => { frame.FilterUsed = "OIII"; frame.MedianHfr = 4.0; });
        Add(db.ConnectionString, target.Id, frame => { frame.FilterUsed = "o3"; frame.MedianHfr = 5.0; });
        Add(db.ConnectionString, target.Id, frame => { frame.FilterUsed = null; frame.MedianHfr = 11.0; });

        var totals = query.Get(target.Id.ToString())!.Totals;

        Assert.Equal(2, totals.MeansByFilter.Count);
        var ha = totals.MeansByFilter["Ha"];
        Assert.Equal(2.5, ha.Hfr!.Value, 10);
        Assert.Null(ha.Eccentricity);
        Assert.Equal(4.0, ha.Fwhm!.Value, 10);
        Assert.Equal(0.62, ha.GuidingRmsArcsec!.Value, 10);
        Assert.Equal(1500d, ha.DetectedStars!.Value, 10);
        // "o3" is OIII's alias, so both OIII frames pool, and the lookup ignores case.
        Assert.Equal(4.5, totals.MeansByFilter["oiii"].Hfr!.Value, 10);
        // The filterless frame is in no bucket but still in the All frames mean.
        Assert.Equal(5.0, totals.AvgHfr!.Value, 10);
    }

    [Fact]
    public void Get_MeansByFilter_EccentricityPoolsTheTargetsModalSource()
    {
        // A per-filter modal vote would pick "pixinsight" for OIII alone and make its row
        // incomparable with Ha's and with All frames.
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var query = Query(db.ConnectionString, out var dispose);
        using var _ = dispose;

        var target = LibrarySeeder.AddTarget(db.ConnectionString, "Pooled");
        Add(db.ConnectionString, target.Id, frame => { frame.FilterUsed = "Ha"; frame.Eccentricity = 0.30; frame.EccentricitySource = "header"; });
        Add(db.ConnectionString, target.Id, frame => { frame.FilterUsed = "Ha"; frame.Eccentricity = 0.40; frame.EccentricitySource = "header"; });
        Add(db.ConnectionString, target.Id, frame => { frame.FilterUsed = "OIII"; frame.Eccentricity = 0.50; frame.EccentricitySource = "header"; });
        Add(db.ConnectionString, target.Id, frame => { frame.FilterUsed = "OIII"; frame.Eccentricity = 0.90; frame.EccentricitySource = "pixinsight"; });

        var totals = query.Get(target.Id.ToString())!.Totals;

        Assert.Equal(0.50, totals.MeansByFilter["OIII"].Eccentricity!.Value, 10);
        Assert.Equal(0.35, totals.MeansByFilter["Ha"].Eccentricity!.Value, 10);
        Assert.Equal(0.40, totals.AvgEccentricity!.Value, 10);
    }

    [Fact]
    public void Get_MeansByFilter_AFilterWithNoMetricHasNulls()
    {
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var query = Query(db.ConnectionString, out var dispose);
        using var _ = dispose;

        var target = LibrarySeeder.AddTarget(db.ConnectionString, "Bare");
        Add(db.ConnectionString, target.Id, frame => { frame.FilterUsed = "Ha"; frame.MedianHfr = 2.0; });
        Add(db.ConnectionString, target.Id, frame => frame.FilterUsed = "SII");

        var totals = query.Get(target.Id.ToString())!.Totals;

        Assert.Equal(new MetricMeans(null, null, null, null, null), totals.MeansByFilter["SII"]);
    }

    private static TargetDetailQuery Query(string connectionString, out IDisposable dispose)
    {
        var settings = new SettingsStore(new SettingsRepository(connectionString));
        var aliases = new AliasMapCache(settings);
        settings.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["OIII"] = new() { Color = "#3ba7ff", Aliases = ["O3"] },
        });
        dispose = aliases;
        return new TargetDetailQuery(
            new DatabaseConnectionString(connectionString),
            aliases,
            () => settings.GetGeneral().Phd2ProfileMap);
    }

    private static void Add(string connectionString, Guid targetId, Action<Image> configure)
        => LibrarySeeder.AddFrame(connectionString, targetId, new DateOnly(2025, 3, 1), configure);
}
