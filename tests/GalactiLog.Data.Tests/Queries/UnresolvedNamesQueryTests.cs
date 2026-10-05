using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// Phase 7 Task 6. Spec 9.7's "distinct unresolved names with their frame counts", read for spec
// 12.7's Targets tab and spec 12.8's Diagnostics group. The statement is
// SqlFragments.UnresolvedObjectCounts, so these also pin the fragment's contract for the third
// caller: LIGHT frames only, no resolved frames, no empty or missing OBJECT card, most frames
// first.
public class UnresolvedNamesQueryTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();

    private static readonly DateOnly SessionDate = new(2025, 3, 15);

    public void Dispose() => _db.Dispose();

    private string Cs => _db.ConnectionString;

    private UnresolvedNamesQuery Query() => new(new DatabaseConnectionString(Cs));

    private void AddFrames(string? rawHeaders, int count, string imageType = "LIGHT", Guid? targetId = null)
    {
        for (var i = 0; i < count; i++)
        {
            LibrarySeeder.AddFrame(Cs, targetId, SessionDate, image =>
            {
                image.RawHeaders = rawHeaders;
                image.ImageType = imageType;
            });
        }
    }

    private void AddNamedFrames(string objectName, int count, string imageType = "LIGHT", Guid? targetId = null)
        => AddFrames(LibrarySeeder.RawHeadersWithObject(objectName), count, imageType, targetId);

    [Fact]
    public void All_ReturnsOneRowPerDistinctObjectName()
    {
        AddNamedFrames("Zzyzx Blob 42", 3);
        AddNamedFrames("Barnard's Loop field", 2);

        var rows = Query().All();

        Assert.Equal(2, rows.Count);
        Assert.Equal(3, rows.Single(row => row.Name == "Zzyzx Blob 42").FrameCount);
        Assert.Equal(2, rows.Single(row => row.Name == "Barnard's Loop field").FrameCount);
    }

    [Fact]
    public void All_CountsLightFramesOnly()
    {
        AddNamedFrames("Zzyzx Blob 42", 2);
        AddNamedFrames("Zzyzx Blob 42", 5, imageType: "DARK");

        var row = Assert.Single(Query().All());

        Assert.Equal(2, row.FrameCount);
    }

    [Fact]
    public void All_ExcludesResolvedFrames()
    {
        var target = LibrarySeeder.AddTarget(Cs, "M 31");
        AddNamedFrames("M31_mosaic", 4, targetId: target.Id);
        AddNamedFrames("Zzyzx Blob 42", 1);

        var row = Assert.Single(Query().All());

        Assert.Equal("Zzyzx Blob 42", row.Name);
    }

    [Fact]
    public void All_ExcludesEmptyAndMissingObjectCards()
    {
        // Spec 9.7 groups these under obj:__uncategorized__ on the dashboard, but they carry no
        // name the retry could look up, so they are deliberately absent from this list.
        AddNamedFrames("", 3);
        AddFrames("{}", 2);
        AddFrames(null, 2);
        AddNamedFrames("Zzyzx Blob 42", 1);

        var row = Assert.Single(Query().All());

        Assert.Equal("Zzyzx Blob 42", row.Name);
    }

    [Fact]
    public void All_ReadsANumericObjectCard()
    {
        // An unquoted numeric card makes json_extract yield a SQLite number, not TEXT (Phase 5
        // fix F1). The name and its group key must still come back as the string the dashboard
        // groups under.
        AddFrames("{\"OBJECT\":7331}", 3);

        var row = Assert.Single(Query().All());

        Assert.Equal("7331", row.Name);
        Assert.Equal("obj:7331", row.GroupKey);
        Assert.Equal(3, row.FrameCount);
    }

    [Fact]
    public void All_IsMostFramesFirst()
    {
        AddNamedFrames("one frame", 1);
        AddNamedFrames("nine frames", 9);
        AddNamedFrames("four frames", 4);

        var rows = Query().All();

        Assert.Equal(["nine frames", "four frames", "one frame"], rows.Select(row => row.Name));
    }

    [Fact]
    public void All_GroupKeyIsTheObjPrefixedName()
    {
        AddNamedFrames("Zzyzx Blob 42", 1);

        var row = Assert.Single(Query().All());

        Assert.Equal("obj:Zzyzx Blob 42", row.GroupKey);
    }

    [Fact]
    public void All_EmptyLibrary_ReturnsAnEmptyList() => Assert.Empty(Query().All());
}
