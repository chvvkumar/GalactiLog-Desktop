using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// The header's per-filter integration line needs the sum per filter, keyed by the same canonical name FiltersUsed carries.
public class TargetTotalsIntegrationByFilterTests
{
    [Fact]
    public void Get_IntegrationSecondsByFilter_SumsPerCanonicalFilter_AndLeavesFilterlessFramesOut()
    {
        // A failure is a bucket for the filterless frame, a per-frame count in place of seconds,
        // or "o3" landing beside "OIII" instead of inside it.
        using var db = TestDatabaseFactory.CreateMigratedDatabase();
        var settings = new SettingsStore(new SettingsRepository(db.ConnectionString));
        using var aliases = new AliasMapCache(settings);
        settings.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["OIII"] = new() { Color = "#3ba7ff", Aliases = ["O3"] },
        });
        var query = new TargetDetailQuery(
            new DatabaseConnectionString(db.ConnectionString),
            aliases,
            () => settings.GetGeneral().Phd2ProfileMap);

        var day = new DateOnly(2025, 3, 1);
        var target = LibrarySeeder.AddTarget(db.ConnectionString, "Bars");
        LibrarySeeder.AddFrame(db.ConnectionString, target.Id, day, frame => { frame.FilterUsed = "Ha"; frame.ExposureTime = 300d; });
        LibrarySeeder.AddFrame(db.ConnectionString, target.Id, day, frame => { frame.FilterUsed = "Ha"; frame.ExposureTime = 180d; });
        LibrarySeeder.AddFrame(db.ConnectionString, target.Id, day, frame => { frame.FilterUsed = "OIII"; frame.ExposureTime = 120d; });
        LibrarySeeder.AddFrame(db.ConnectionString, target.Id, day, frame => { frame.FilterUsed = "o3"; frame.ExposureTime = 30d; });
        LibrarySeeder.AddFrame(db.ConnectionString, target.Id, day, frame => { frame.FilterUsed = null; frame.ExposureTime = 60d; });

        var detail = query.Get(target.Id.ToString());
        Assert.NotNull(detail);

        var byFilter = detail.Totals.IntegrationSecondsByFilter;
        Assert.Equal(2, byFilter.Count);
        Assert.Equal(480d, byFilter["Ha"]);
        Assert.Equal(150d, byFilter["OIII"]);
        // The filterless frame is in no bucket but still in the total.
        Assert.Equal(690d, detail.Totals.IntegrationSeconds);
    }
}
