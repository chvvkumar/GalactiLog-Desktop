using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

public class DashboardFacetsQueryTests
{
    private static readonly DateOnly Day = new(2025, 3, 1);

    private sealed class Fixture : IDisposable
    {
        private readonly TestDatabaseHandle _db;
        private readonly AliasMapCache _aliases;

        public Fixture()
        {
            _db = TestDatabaseFactory.CreateMigratedDatabase();
            Settings = new SettingsStore(new SettingsRepository(_db.ConnectionString));
            _aliases = new AliasMapCache(Settings);
            Query = new DashboardFacetsQuery(new DatabaseConnectionString(_db.ConnectionString), _aliases);
        }

        public string ConnectionString => _db.ConnectionString;
        public SettingsStore Settings { get; }
        public DashboardFacetsQuery Query { get; }

        public void Dispose()
        {
            _aliases.Dispose();
            _db.Dispose();
        }
    }

    [Fact]
    public void Load_ReturnsDistinctCanonicalFilters_WithFrameCounts()
    {
        using var fixture = new Fixture();
        fixture.Settings.SaveFilters(new Dictionary<string, FilterSetting>
        {
            ["Ha"] = new() { Color = "#ff4d4d", Aliases = ["H-alpha"] },
        });
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "IC 1805");
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day, frame => frame.FilterUsed = "Ha");
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day, frame => frame.FilterUsed = "h-alpha");
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day, frame => frame.FilterUsed = "OIII");
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day, frame => frame.FilterUsed = null);

        var facets = fixture.Query.Load();

        Assert.Equal(["Ha", "OIII"], facets.Filters.Select(facet => facet.CanonicalName));
        Assert.Equal(2, facets.Filters.Single(facet => facet.CanonicalName == "Ha").FrameCount);
        Assert.Equal("#ff4d4d", facets.Filters.Single(facet => facet.CanonicalName == "Ha").Color);
        // P13 R2a: "OIII" has no stored colour and is not a configured filter at all, so it no
        // longer reads the grey. It folds to the OIII category and takes the seeded blue through
        // the one spine, AliasMap.FilterColor, which is what this query already calls.
        Assert.Equal("#3a8fd4", facets.Filters.Single(facet => facet.CanonicalName == "OIII").Color);
    }

    [Fact]
    public void Load_FoldsRawEquipmentNamesToCanonical()
    {
        using var fixture = new Fixture();
        fixture.Settings.SaveEquipment(new EquipmentSettings
        {
            Cameras = new Dictionary<string, EquipmentItemSettings> { ["ASI2600MM Pro"] = new() { Aliases = ["ZWO ASI2600MM"] } },
            Telescopes = new Dictionary<string, EquipmentItemSettings> { ["RC8"] = new() { Aliases = ["GSO RC8"] } },
        });
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "NGC 891");
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day, frame =>
        {
            frame.Camera = "zwo asi2600mm";
            frame.Telescope = "gso rc8";
        });
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day, frame =>
        {
            frame.Camera = "ASI2600MM Pro";
            frame.Telescope = "FRA600";
        });

        var facets = fixture.Query.Load();

        Assert.Equal(["ASI2600MM Pro"], facets.Cameras);
        Assert.Equal(["FRA600", "RC8"], facets.Telescopes);
    }

    [Fact]
    public void Load_IgnoresNonLightFrames()
    {
        using var fixture = new Fixture();
        var target = LibrarySeeder.AddTarget(fixture.ConnectionString, "M 106");
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day, frame => frame.FilterUsed = "L");
        LibrarySeeder.AddFrame(fixture.ConnectionString, target.Id, Day, frame =>
        {
            frame.ImageType = "FLAT";
            frame.FilterUsed = "Ha";
            frame.Camera = "CalibrationCam";
            frame.Telescope = "CalibrationScope";
        });

        var facets = fixture.Query.Load();

        Assert.Equal(["L"], facets.Filters.Select(facet => facet.CanonicalName));
        Assert.Empty(facets.Cameras);
        Assert.Empty(facets.Telescopes);
    }

    [Fact]
    public void Load_EmptyLibrary_ReturnsEmptyLists()
    {
        using var fixture = new Fixture();

        var facets = fixture.Query.Load();

        Assert.Empty(facets.Filters);
        Assert.Empty(facets.Cameras);
        Assert.Empty(facets.Telescopes);
    }

    [Fact]
    public void Load_OnTheSeededLibrary_ReturnsItsFiveFiltersAndTwoRigs()
    {
        using var fixture = new Fixture();
        LibrarySeeder.Seed(fixture.ConnectionString);

        var facets = fixture.Query.Load();

        Assert.Equal(LibrarySeeder.Filters.Order(), facets.Filters.Select(facet => facet.CanonicalName));
        Assert.Equal(LibrarySeeder.FrameCount, facets.Filters.Sum(facet => facet.FrameCount));
        Assert.Equal(LibrarySeeder.Rigs.Select(rig => rig.Camera).Order(), facets.Cameras);
        Assert.Equal(LibrarySeeder.Rigs.Select(rig => rig.Telescope).Order(), facets.Telescopes);
    }
}
