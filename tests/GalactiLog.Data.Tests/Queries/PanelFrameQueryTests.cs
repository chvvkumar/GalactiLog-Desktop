using GalactiLog.Core.Mosaics;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// Phase 19A Task 2. Spec 11.4's best frame per panel per filter through the membership join of
// spec 5.24, the available filters and the default filter, and spec 12.17's preview tile.
public class PanelFrameQueryTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();
    private readonly AliasMapCache _aliases;
    private readonly MosaicRepository _repository;
    private readonly PanelFrameQuery _query;

    public PanelFrameQueryTests()
    {
        _aliases = new AliasMapCache(new SettingsStore(new SettingsRepository(_db.ConnectionString)));
        _repository = new MosaicRepository(new DatabaseConnectionString(_db.ConnectionString));
        _query = new PanelFrameQuery(new DatabaseConnectionString(_db.ConnectionString), _aliases);
    }

    public void Dispose()
    {
        _aliases.Dispose();
        _db.Dispose();
    }

    private static DateOnly Night(int day) => new(2026, 3, day);

    private Guid NewTarget(string name) => LibrarySeeder.AddTarget(_db.ConnectionString, name).Id;

    private Guid Frame(
        Guid target, int day, string? label, string? filter, int? stars = null, double? hfr = null,
        string imageType = "LIGHT", double exposure = LibrarySeeder.ExposureSeconds, string? path = null,
        DateTime? capture = null, bool noCapture = false)
        => LibrarySeeder.AddFrame(_db.ConnectionString, target, Night(day), image =>
        {
            image.PanelLabel = label;
            image.FilterUsed = filter;
            image.ImageType = imageType;
            image.DetectedStars = stars;
            image.MedianHfr = hfr;
            image.ExposureTime = exposure;
            if (path is not null) image.FilePath = path;
            if (capture is not null) image.CaptureDate = capture;
            if (noCapture) image.CaptureDate = null;
        }).Id;

    private (Guid Mosaic, Guid First, Guid Second) TwoPanels(string name, Guid target)
    {
        var mosaic = _repository.Create(name);
        var first = _repository.AddPanel(mosaic, "Panel 1");
        var second = _repository.AddPanel(mosaic, "Panel 2");
        _repository.IncludeNight(first, target, Night(1), "Panel 1");
        _repository.IncludeNight(second, target, Night(1), "Panel 2");
        return (mosaic, first, second);
    }

    [Fact]
    public void ForMosaic_PicksTheBestFramePerPanelPerFilter_ThroughTheJoin()
    {
        var target = NewTarget("NGC 7000");
        var a = Frame(target, 1, "Panel 1", "Ha", stars: 300, hfr: 1.5);
        Frame(target, 1, "Panel 1", "Ha", stars: 100, hfr: 3.0);
        Frame(target, 2, "Panel 1", "Ha", stars: 200, hfr: 2.0);
        Frame(target, 1, "Panel 1", "OIII", stars: 200, hfr: 2.0);
        var d = Frame(target, 1, "Panel 1", "OIII", stars: 250, hfr: 1.8);
        Frame(target, 1, "Panel 2", "Ha", stars: 150, hfr: 2.5);
        var g = Frame(target, 1, "Panel 2", "Ha", stars: 180, hfr: 2.2);
        // Excluded: an available-only night, a dark frame, a label no included row holds, no filter.
        Frame(target, 3, "Panel 1", "Ha", stars: 1000, hfr: 0.5);
        Frame(target, 1, "Panel 1", "Ha", stars: 2000, hfr: 0.4, imageType: "DARK");
        Frame(target, 1, "Panel 3", "Ha", stars: 3000, hfr: 0.3);
        Frame(target, 1, "Panel 1", null, stars: 4000, hfr: 0.2);

        var (mosaic, first, second) = TwoPanels("North America", target);
        _repository.IncludeNight(first, target, Night(2), "Panel 1");
        _repository.RemoveNight(first, target, Night(3), "Panel 1");

        var set = _query.ForMosaic(mosaic);

        Assert.Equal(["Ha", "OIII"], set.AvailableFilters);
        var ha = set.BestByPanel[first]["Ha"];
        Assert.Equal((a, "Ha", 0.825), (ha.ImageId, ha.Filter, Math.Round(ha.Score, 9)));
        Assert.EndsWith(".fits", ha.FilePath);
        Assert.Equal(a, set.BestByPanel[first]["ha"].ImageId);
        Assert.Equal(d, set.BestByPanel[first]["OIII"].ImageId);
        Assert.Equal(g, set.BestByPanel[second]["Ha"].ImageId);
        Assert.False(set.BestByPanel[second].ContainsKey("OIII"));
        Assert.Equal("Ha", set.DefaultFilter);
    }

    // Ties: the most recent capture_date first, a null date last, then the ordinally first path.
    [Fact]
    public void ForMosaic_BreaksScoreTiesByCaptureDate_ThenPath()
    {
        var target = NewTarget("M31");
        Frame(target, 1, "Panel 1", "Ha");
        var later = Frame(target, 2, "Panel 1", "Ha");
        var at = new DateTime(2026, 3, 1, 23, 0, 0);
        Frame(target, 1, "Panel 2", "Ha", path: @"C:\b.fits", capture: at);
        var first = Frame(target, 1, "Panel 2", "Ha", path: @"C:\a.fits", capture: at);
        Frame(target, 1, "Panel 2", "Ha", path: @"C:\0.fits", noCapture: true);

        var (mosaic, one, two) = TwoPanels("M31 mosaic", target);
        _repository.IncludeNight(one, target, Night(2), "Panel 1");

        var set = _query.ForMosaic(mosaic);

        Assert.Equal(later, set.BestByPanel[one]["Ha"].ImageId);
        Assert.Equal(first, set.BestByPanel[two]["Ha"].ImageId);
    }

    // Summed exposure_time descending, ties by name ordinal case insensitive.
    [Fact]
    public void ForMosaic_OrdersAvailableFiltersByIntegration_ThenName()
    {
        var target = NewTarget("IC 1396");
        Frame(target, 1, "Panel 1", "SII", exposure: 60);
        Frame(target, 1, "Panel 1", "SII", exposure: 60);
        Frame(target, 1, "Panel 1", "Ha", exposure: 60);
        Frame(target, 1, "Panel 1", "Ha", exposure: 60);
        Frame(target, 1, "Panel 1", "OIII", exposure: 600);

        var (mosaic, _, _) = TwoPanels("IC 1396 mosaic", target);

        Assert.Equal(["OIII", "Ha", "SII"], _query.ForMosaic(mosaic).AvailableFilters);
    }

    // Panel 1's top frame scores 0.5 (no metric); Panel 2's OIII frame scores 0.675, so the default
    // is OIII although Ha is the most integrated filter.
    [Fact]
    public void ForMosaic_DefaultFilter_FollowsTheBestTopFrame()
    {
        var target = NewTarget("NGC 6888");
        Frame(target, 1, "Panel 1", "Ha");
        Frame(target, 1, "Panel 1", "Ha");
        Frame(target, 1, "Panel 1", "Ha");
        Frame(target, 1, "Panel 2", "Ha", stars: 100);
        Frame(target, 1, "Panel 2", "OIII", stars: 200);

        var (mosaic, _, _) = TwoPanels("Crescent", target);

        var set = _query.ForMosaic(mosaic);

        Assert.Equal(["Ha", "OIII"], set.AvailableFilters);
        Assert.Equal("OIII", set.DefaultFilter);
    }

    // Every metric missing: every top frame scores 0.5 and the first panel in sort_order wins the
    // tie, so the default is its OIII, not the most integrated Ha.
    [Fact]
    public void ForMosaic_DefaultFilter_EveryMetricMissing_FirstPanelWinsTheTie()
    {
        var target = NewTarget("NGC 6960");
        Frame(target, 1, "Panel 1", "OIII");
        Frame(target, 1, "Panel 1", "OIII");
        Frame(target, 1, "Panel 2", "Ha");
        Frame(target, 1, "Panel 2", "Ha");
        Frame(target, 1, "Panel 2", "Ha");

        var (mosaic, _, second) = TwoPanels("Veil", target);

        var set = _query.ForMosaic(mosaic);

        Assert.Equal(["Ha", "OIII"], set.AvailableFilters);
        Assert.Equal("OIII", set.DefaultFilter);
        Assert.Equal(0.5, set.BestByPanel[second]["Ha"].Score, 1e-9);
    }

    [Fact]
    public void ForMosaic_NoFrames_GivesNoFiltersAndNoDefault()
    {
        var mosaic = _repository.Create("Empty");
        var panel = _repository.AddPanel(mosaic, "Panel 1");

        var set = _query.ForMosaic(mosaic);

        Assert.Empty(set.AvailableFilters);
        Assert.Null(set.DefaultFilter);
        Assert.Empty(set.BestByPanel[panel]);
    }

    [Fact]
    public void ForSuggestionEntry_PicksOverEveryFilter_OnTheLabelAndNightsOnly()
    {
        var target = NewTarget("NGC 7000");
        Frame(target, 1, "Panel 1", "Ha", stars: 100);
        var best = Frame(target, 2, "Panel 1", "OIII", stars: 300);
        Frame(target, 1, "Panel 2", "Ha", stars: 1000);
        Frame(target, 3, "Panel 1", "Ha", stars: 2000);
        Frame(target, 1, "Panel 1", "Ha", stars: 3000, imageType: "DARK");

        var frame = _query.ForSuggestionEntry(target, "panel 1", [Night(1), Night(2)]);

        Assert.Equal((best, "OIII"), (frame!.ImageId, frame.Filter));
        Assert.Null(_query.ForSuggestionEntry(target, "Panel 9", [Night(1)]));
        Assert.Null(_query.ForSuggestionEntry(target, "Panel 1", []));
    }

    // Phase 19B Task 3. Spec 11.6's geometry read: the six images columns by image id, one read.
    [Fact]
    public void Geometry_ReadsTheSixColumnsById_NullsKept_UnknownIdsAbsent()
    {
        var target = NewTarget("NGC 7000");
        var full = LibrarySeeder.AddFrame(_db.ConnectionString, target, Night(1), image =>
        {
            image.RaDeg = 314.75;
            image.DecDeg = 44.5;
            image.WidthPx = 6248;
            image.ArcsecPerPixel = 1.51;
            image.RotatorPosition = 92.5;
            image.PierSide = "East";
        }).Id;
        var bare = LibrarySeeder.AddFrame(_db.ConnectionString, target, Night(1), image =>
        {
            image.RaDeg = null;
            image.DecDeg = null;
            image.WidthPx = null;
            image.ArcsecPerPixel = null;
            image.RotatorPosition = null;
            image.PierSide = null;
        }).Id;
        var unknown = Guid.NewGuid();

        var geometry = _query.Geometry([full, bare, unknown]);

        Assert.Equal(2, geometry.Count);
        Assert.Equal(new PanelGeometry(314.75, 44.5, 6248, 1.51, 92.5, "East"), geometry[full]);
        Assert.Equal(new PanelGeometry(null, null, null, null, null, null), geometry[bare]);
        Assert.False(geometry.ContainsKey(unknown));
        Assert.Empty(_query.Geometry([]));
    }
}
