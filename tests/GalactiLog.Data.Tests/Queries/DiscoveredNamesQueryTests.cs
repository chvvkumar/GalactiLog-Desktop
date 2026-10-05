using System.Reflection;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// Spec 12.7's discovered-name lists (Phase 9 Task 7). Questions.md Q25: no LIGHT filter, unlike
// almost every other aggregate in the application, because a raw name that appears only on
// calibration frames still needs grouping.
public class DiscoveredNamesQueryTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();

    private static readonly DateOnly SessionDate = new(2025, 3, 15);

    public void Dispose() => _db.Dispose();

    private string Cs => _db.ConnectionString;

    private DiscoveredNamesQuery Query() => new(new DatabaseConnectionString(Cs));

    private void AddFrame(string? filterUsed, string? camera, string? telescope, string imageType = "LIGHT")
        => LibrarySeeder.AddFrame(Cs, targetId: null, SessionDate, image =>
        {
            image.FilterUsed = filterUsed;
            image.Camera = camera;
            image.Telescope = telescope;
            image.ImageType = imageType;
        });

    [Fact]
    public void Filters_ReturnsDistinctRawNamesWithCounts_OrderedByCountDescending()
    {
        AddFrame("Ha", null, null);
        AddFrame("Ha", null, null);
        AddFrame("ha", null, null);
        AddFrame("OIII", null, null);
        AddFrame("OIII", null, null);
        AddFrame("OIII", null, null);

        var rows = Query().Read(DiscoveredNameColumn.Filters);

        Assert.Equal(3, rows.Count);
        Assert.Equal("OIII", rows[0].Name);
        Assert.Equal(3, rows[0].FrameCount);
        Assert.Equal("Ha", rows[1].Name);
        Assert.Equal(2, rows[1].FrameCount);
        Assert.Equal("ha", rows[2].Name);
        Assert.Equal(1, rows[2].FrameCount);
    }

    [Fact]
    public void Cameras_ReturnsDistinctRawNames()
    {
        AddFrame(null, "ASI2600MM", null);
        AddFrame(null, "ASI2600MM", null);
        AddFrame(null, "ASI294MC", null);

        var rows = Query().Read(DiscoveredNameColumn.Cameras);

        Assert.Equal(2, rows.Count);
        Assert.Equal("ASI2600MM", rows[0].Name);
        Assert.Equal(2, rows[0].FrameCount);
        Assert.Equal("ASI294MC", rows[1].Name);
        Assert.Equal(1, rows[1].FrameCount);
    }

    [Fact]
    public void Telescopes_ReturnsDistinctRawNames()
    {
        AddFrame(null, null, "RC8");
        AddFrame(null, null, "FRA600");
        AddFrame(null, null, "FRA600");

        var rows = Query().Read(DiscoveredNameColumn.Telescopes);

        Assert.Equal(2, rows.Count);
        Assert.Equal("FRA600", rows[0].Name);
        Assert.Equal("RC8", rows[1].Name);
    }

    [Fact]
    public void SkipsNullValues()
    {
        AddFrame(null, null, null);
        AddFrame("Ha", null, null);

        var rows = Query().Read(DiscoveredNameColumn.Filters);

        var row = Assert.Single(rows);
        Assert.Equal("Ha", row.Name);
    }

    [Fact]
    public void IncludesCalibrationFrames()
    {
        // No LIGHT filter (Q25): a name that appears only on a DARK frame still shows up, with a
        // count that is a frame count, not a light-frame count.
        AddFrame("Ha", null, null, imageType: "DARK");
        AddFrame("Ha", null, null, imageType: "FLAT");

        var row = Assert.Single(Query().Read(DiscoveredNameColumn.Filters));

        Assert.Equal("Ha", row.Name);
        Assert.Equal(2, row.FrameCount);
    }

    [Fact]
    public void ColumnIsChosenFromAFixedEnum_NotInterpolated()
    {
        // Assert by construction: Read takes DiscoveredNameColumn, a fixed three-value enum, and
        // has no string-typed overload a caller could pass a raw column name through.
        var method = typeof(DiscoveredNamesQuery).GetMethod(nameof(DiscoveredNamesQuery.Read), BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);
        var parameter = Assert.Single(method!.GetParameters());
        Assert.Equal(typeof(DiscoveredNameColumn), parameter.ParameterType);
        Assert.True(parameter.ParameterType.IsEnum);
        Assert.Equal(3, Enum.GetValues(parameter.ParameterType).Length);
        Assert.DoesNotContain(
            typeof(DiscoveredNamesQuery).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            m => m.Name == nameof(DiscoveredNamesQuery.Read) && m.GetParameters().Any(p => p.ParameterType == typeof(string)));
    }
}
