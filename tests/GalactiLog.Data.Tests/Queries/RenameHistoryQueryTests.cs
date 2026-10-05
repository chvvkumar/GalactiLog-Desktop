using System.Text.Json;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.Data.Tests.Queries;

// Phase 7 Task 6. Spec 12.7's rename history, read from the target_renamed user_action events
// TargetWriteRepository.Rename writes (questions.md Q11). There is no rename_history table, so
// these seed activity_events directly to pin the reader's contract independently of the writer.
public class RenameHistoryQueryTests : IDisposable
{
    private readonly TestDatabaseHandle _db = TestDatabaseFactory.CreateMigratedDatabase();

    private static readonly DateTime Noon = new(2025, 3, 4, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _db.Dispose();

    private string Cs => _db.ConnectionString;

    private RenameHistoryQuery Query() => new(new DatabaseConnectionString(Cs));

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(Cs, tracking: true));

    private void AddRenameEvent(
        Guid? targetId, string previousName, string newName, DateTime? timestamp = null,
        string? rawDetails = null, string eventType = RenameHistoryQuery.RenameEventType)
    {
        using var context = Open();
        context.ActivityEvents.Add(new ActivityEvent
        {
            Timestamp = timestamp ?? Noon,
            Severity = "info",
            Category = "user_action",
            EventType = eventType,
            Message = $"Renamed \"{previousName}\" to \"{newName}\"",
            Details = rawDetails ?? JsonSerializer.Serialize(
                new Dictionary<string, string> { ["previous_name"] = previousName, ["new_name"] = newName }),
            TargetId = targetId,
        });
        context.SaveChanges();
    }

    [Fact]
    public void Recent_ReturnsOneRowPerRename()
    {
        var first = LibrarySeeder.AddTarget(Cs, "Andromeda");
        var second = LibrarySeeder.AddTarget(Cs, "Orion");
        AddRenameEvent(first.Id, "M 31", "Andromeda");
        AddRenameEvent(second.Id, "M 42", "Orion");

        // A non-rename user_action event must not appear in the list.
        AddRenameEvent(first.Id, "M 31", "Andromeda", eventType: "retry_unresolved");

        Assert.Equal(2, Query().Recent().Count);
    }

    [Fact]
    public void Recent_IsNewestFirst()
    {
        var target = LibrarySeeder.AddTarget(Cs, "Andromeda");
        AddRenameEvent(target.Id, "M 31", "Older", Noon.AddHours(-2));
        AddRenameEvent(target.Id, "Older", "Andromeda", Noon);

        var rows = Query().Recent();

        Assert.Equal(["Andromeda", "Older"], rows.Select(row => row.NewName));
        Assert.Equal(Noon, rows[0].Timestamp);
    }

    [Fact]
    public void Recent_ReadsThePreviousAndNewNamesFromTheDetails()
    {
        var target = LibrarySeeder.AddTarget(Cs, "Andromeda");
        AddRenameEvent(target.Id, "M 31", "Andromeda");

        var row = Assert.Single(Query().Recent());

        Assert.Equal("M 31", row.PreviousName);
        Assert.Equal("Andromeda", row.NewName);
        Assert.Equal(target.Id, row.TargetId);
    }

    [Fact]
    public void Recent_JoinsTheTargetsCurrentName()
    {
        // Renamed twice: the older row's new_name is no longer what the target is called, and the
        // join is what tells the user where that name ended up.
        var target = LibrarySeeder.AddTarget(Cs, "Andromeda");
        AddRenameEvent(target.Id, "M 31", "Older", Noon.AddHours(-2));

        var row = Assert.Single(Query().Recent());

        Assert.Equal("Older", row.NewName);
        Assert.Equal("Andromeda", row.CurrentName);
    }

    [Fact]
    public void Recent_DeletedTarget_StillReturnsTheRename()
    {
        var target = LibrarySeeder.AddTarget(Cs, "Andromeda");
        AddRenameEvent(target.Id, "M 31", "Andromeda");

        using (var context = Open())
        {
            context.Targets.Remove(context.Targets.Single(row => row.Id == target.Id));
            context.SaveChanges();
        }

        // Spec 5.12: the FK is ON DELETE SET NULL, so the record of the rename survives its
        // target.
        var row = Assert.Single(Query().Recent());

        Assert.Null(row.TargetId);
        Assert.Null(row.CurrentName);
        Assert.Equal("M 31", row.PreviousName);
        Assert.Equal("Andromeda", row.NewName);
    }

    [Fact]
    public void Recent_MalformedDetails_SkipsTheRow()
    {
        var target = LibrarySeeder.AddTarget(Cs, "Andromeda");
        AddRenameEvent(target.Id, "M 31", "Andromeda", rawDetails: "{not json");
        AddRenameEvent(target.Id, "M 31", "Andromeda", rawDetails: "{\"previous_name\":\"M 31\"}");
        AddRenameEvent(target.Id, "M 31", "Andromeda", Noon.AddHours(-1));

        // One unreadable document must not hide every other rename.
        var row = Assert.Single(Query().Recent());

        Assert.Equal("M 31", row.PreviousName);
    }

    [Fact]
    public void Recent_RespectsTheLimit()
    {
        var target = LibrarySeeder.AddTarget(Cs, "Andromeda");
        for (var i = 0; i < 5; i++)
        {
            AddRenameEvent(target.Id, $"Name {i}", $"Name {i + 1}", Noon.AddMinutes(i));
        }

        Assert.Equal(2, Query().Recent(limit: 2).Count);
        Assert.Equal(5, Query().Recent().Count);
    }
}
