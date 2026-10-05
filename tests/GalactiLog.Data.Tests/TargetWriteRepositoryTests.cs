using System.Text.Json;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Xunit;

namespace GalactiLog.Data.Tests;

// Phase 6 Task 3. The three database writes Target detail performs, against a migrated temp
// database: the partial unique index on primary_name is what makes the NameTaken case real, so
// these cannot be asserted against an in-memory fake.
public class TargetWriteRepositoryTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly TargetWriteRepository _repository;

    public TargetWriteRepositoryTests()
    {
        _db = TestDatabaseFactory.CreateMigratedDatabase();
        _repository = new TargetWriteRepository(new DatabaseConnectionString(_db.ConnectionString));
    }

    public void Dispose() => _db.Dispose();

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));

    private Guid AddTarget(string primaryName, string? notes = null)
    {
        using var context = Open();
        var target = new Target { Id = Guid.NewGuid(), PrimaryName = primaryName, Notes = notes };
        context.Targets.Add(target);
        context.SaveChanges();
        return target.Id;
    }

    private Target Read(Guid id)
    {
        using var context = Open();
        return context.Targets.Single(row => row.Id == id);
    }

    private SessionNote? ReadNote(Guid targetId, DateOnly date)
    {
        using var context = Open();
        return context.SessionNotes
            .SingleOrDefault(row => row.TargetId == targetId && row.SessionDate == date);
    }

    [Fact]
    public void Rename_SetsPrimaryNameAndNameLocked()
    {
        var id = AddTarget("M 31");

        var outcome = _repository.Rename(id, "Andromeda");

        Assert.Equal(RenameOutcome.Renamed, outcome);
        var target = Read(id);
        Assert.Equal("Andromeda", target.PrimaryName);

        // Spec 5.3: name_locked is set when the user renames a target, so later catalog
        // enrichment does not overwrite the choice.
        Assert.True(target.NameLocked);
    }

    [Fact]
    public void Rename_ToAnExistingName_ReportsNameTaken_AndChangesNothing()
    {
        var first = AddTarget("M 31");
        AddTarget("M 42");

        var outcome = _repository.Rename(first, "M 42");

        Assert.Equal(RenameOutcome.NameTaken, outcome);
        var target = Read(first);
        Assert.Equal("M 31", target.PrimaryName);
        Assert.False(target.NameLocked);
    }

    [Fact]
    public void Rename_UnknownTarget_ReportsNotFound()
        => Assert.Equal(RenameOutcome.NotFound, _repository.Rename(Guid.NewGuid(), "Anything"));

    [Fact]
    public void SaveTargetNotes_RoundTrips()
    {
        var id = AddTarget("M 31");

        _repository.SaveTargetNotes(id, "Two nights of Ha, guiding was poor on the second.");

        Assert.Equal("Two nights of Ha, guiding was poor on the second.", Read(id).Notes);
    }

    [Fact]
    public void SaveTargetNotes_Null_ClearsTheColumn()
    {
        var id = AddTarget("M 31", notes: "something");

        _repository.SaveTargetNotes(id, null);

        Assert.Null(Read(id).Notes);
    }

    [Fact]
    public void SaveSessionNotes_InsertsThenUpdates_TheSameRow()
    {
        var id = AddTarget("M 31");
        var date = new DateOnly(2025, 12, 7);

        _repository.SaveSessionNotes(id, date, "first");
        _repository.SaveSessionNotes(id, date, "second");

        using var context = Open();
        var rows = context.SessionNotes.Where(row => row.TargetId == id).ToList();

        // Unique on (target_id, session_date) per spec 5.9: the upsert must not insert a second
        // row for the same night.
        var row = Assert.Single(rows);
        Assert.Equal("second", row.Notes);
        Assert.Equal(date, row.SessionDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SaveSessionNotes_Blank_DeletesTheRow(string? blank)
    {
        var id = AddTarget("M 31");
        var date = new DateOnly(2025, 12, 7);
        _repository.SaveSessionNotes(id, date, "something");
        Assert.NotNull(ReadNote(id, date));

        _repository.SaveSessionNotes(id, date, blank);

        // A database delete, never a file one: this is what keeps the card's notes indicator
        // honest instead of showing a note that is an empty string.
        Assert.Null(ReadNote(id, date));
    }

    [Fact]
    public void SaveSessionNotes_Blank_WithNoRow_IsANoOp()
    {
        var id = AddTarget("M 31");
        var date = new DateOnly(2025, 12, 7);

        _repository.SaveSessionNotes(id, date, null);

        Assert.Null(ReadNote(id, date));
    }

    [Fact]
    public void SaveSessionNotes_StampsUpdatedAt()
    {
        var id = AddTarget("M 31");
        var date = new DateOnly(2025, 12, 7);
        var before = DateTime.UtcNow.AddSeconds(-1);

        _repository.SaveSessionNotes(id, date, "first");
        var inserted = ReadNote(id, date)!.UpdatedAt;

        Assert.InRange(inserted, before, DateTime.UtcNow.AddSeconds(1));

        _repository.SaveSessionNotes(id, date, "second");
        var updated = ReadNote(id, date)!.UpdatedAt;

        Assert.True(updated >= inserted, "an update must not move updated_at backwards");
    }

    // ---- the rename history's activity event (Phase 7 Task 6, questions.md Q11) ----------

    private List<ActivityEvent> RenameEvents()
    {
        using var context = Open();
        return [.. context.ActivityEvents.Where(row => row.EventType == "target_renamed")];
    }

    [Fact]
    public void Rename_EmitsATargetRenamedActivityEvent()
    {
        var id = AddTarget("M 31");

        _repository.Rename(id, "Andromeda");

        var evt = Assert.Single(RenameEvents());
        Assert.Equal("user_action", evt.Category);
        Assert.Equal("info", evt.Severity);
        Assert.Equal(id, evt.TargetId);
        Assert.Equal("Renamed \"M 31\" to \"Andromeda\"", evt.Message);
    }

    [Fact]
    public void Rename_EventCarriesThePreviousAndNewNames()
    {
        var id = AddTarget("M 31");

        _repository.Rename(id, "Andromeda");

        using var details = JsonDocument.Parse(Assert.Single(RenameEvents()).Details!);
        Assert.Equal("M 31", details.RootElement.GetProperty("previous_name").GetString());
        Assert.Equal("Andromeda", details.RootElement.GetProperty("new_name").GetString());
    }

    [Fact]
    public void Rename_NameTaken_EmitsNoEvent()
    {
        // The event is added to the same SaveChanges as the rename, so the rolled-back write
        // records nothing.
        var first = AddTarget("M 31");
        AddTarget("M 42");

        Assert.Equal(RenameOutcome.NameTaken, _repository.Rename(first, "M 42"));
        Assert.Empty(RenameEvents());
    }

    [Fact]
    public void Rename_NotFound_EmitsNoEvent()
    {
        Assert.Equal(RenameOutcome.NotFound, _repository.Rename(Guid.NewGuid(), "Anything"));
        Assert.Empty(RenameEvents());
    }

    // ---- P14A Task 6: spec 12.4's object type edit (PAR-009) ---------------------------------

    private List<ActivityEvent> ObjectTypeEvents()
    {
        using var context = Open();
        return
        [
            .. context.ActivityEvents
                .Where(row => row.EventType == TargetWriteRepository.ObjectTypeChangedEventType)
        ];
    }

    /// <summary>Spec 9.8's nine display categories plus Other, with the first SIMBAD code the
    /// table lists for each. Retyped here on purpose: a test that read the same table the code
    /// reads would assert nothing.</summary>
    public static TheoryData<string, string> CategoryCodes() => new()
    {
        { "Emission Nebula", "HII" },
        { "Reflection Nebula", "GNe" },
        { "Dark Nebula", "DNe" },
        { "Planetary Nebula", "PN" },
        { "Supernova Remnant", "SNR" },
        { "Galaxy", "G" },
        { "Open Cluster", "OpC" },
        { "Globular Cluster", "GlC" },
        { "Star", "*" },
        { "Other", "" },
    };

    [Theory]
    [MemberData(nameof(CategoryCodes))]
    public void SetObjectType_WritesTheCategoryAndTheFirstSimbadCode(string category, string code)
    {
        var id = AddTarget($"target for {category}");

        Assert.Equal(SetObjectTypeOutcome.Changed, _repository.SetObjectType(id, category));

        Assert.Equal(code, Read(id).ObjectType);
    }

    [Theory]
    [MemberData(nameof(CategoryCodes))]
    public void SetObjectType_RoundTripsThroughCategorize(string category, string code)
    {
        // The pin on the one place spec 9.8's table is written backwards: every code the write
        // stores has to categorize back to the category the user picked, or a correction made on
        // this page would move the target to a different Object Type pill on the dashboard.
        var id = AddTarget($"round trip for {category}");

        _repository.SetObjectType(id, category);

        var stored = Read(id).ObjectType;
        Assert.Equal(code, stored);
        Assert.Equal(category, ObjectTypeCategories.Categorize(stored));
    }

    [Fact]
    public void SetObjectType_Other_StillCategorizesAsOther()
    {
        // Spec 9.8: "anything mapped by no code is Other", so Other stores the empty string and
        // Categorize returns Other for it. The round trip is what has to hold, not the literal.
        var id = AddTarget("M 31");

        _repository.SetObjectType(id, "Other");

        Assert.Equal("", Read(id).ObjectType);
        Assert.Equal("Other", ObjectTypeCategories.Categorize(Read(id).ObjectType));
    }

    [Fact]
    public void SetObjectType_UnknownTarget_ReportsNotFound_AndWritesNothing()
    {
        var untouched = AddTarget("M 31");

        Assert.Equal(SetObjectTypeOutcome.NotFound, _repository.SetObjectType(Guid.NewGuid(), "Galaxy"));

        Assert.Null(Read(untouched).ObjectType);
    }

    [Fact]
    public void SetObjectType_EmitsAUserActionActivityEvent()
    {
        var id = AddTarget("M 31");

        _repository.SetObjectType(id, "Galaxy");

        var evt = Assert.Single(ObjectTypeEvents());
        Assert.Equal("user_action", evt.Category);
        Assert.Equal("info", evt.Severity);
        Assert.Equal("target_object_type_changed", evt.EventType);
        Assert.Equal(id, evt.TargetId);
    }

    [Fact]
    public void SetObjectType_TheEventCarriesTheOldAndNewValues()
    {
        var id = AddTarget("M 31");
        _repository.SetObjectType(id, "Emission Nebula");

        _repository.SetObjectType(id, "Galaxy");

        var evt = ObjectTypeEvents().Last();
        using var details = JsonDocument.Parse(evt.Details!);
        Assert.Equal("HII", details.RootElement.GetProperty("previous_object_type").GetString());
        Assert.Equal("Emission Nebula", details.RootElement.GetProperty("previous_category").GetString());
        Assert.Equal("G", details.RootElement.GetProperty("new_object_type").GetString());
        Assert.Equal("Galaxy", details.RootElement.GetProperty("new_category").GetString());
    }

    [Fact]
    public void SetObjectType_TheEventDetails_AreSnakeCase()
    {
        // HANDOFF section 4 rule 9. Asserted over the document's own property names, so a
        // camelCase serializer setting fails here rather than in a reader three phases later.
        var id = AddTarget("M 31");

        _repository.SetObjectType(id, "Galaxy");

        using var details = JsonDocument.Parse(Assert.Single(ObjectTypeEvents()).Details!);
        Assert.Equal(
            ["previous_object_type", "previous_category", "new_object_type", "new_category"],
            details.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void SetObjectType_NotFound_EmitsNoEvent()
    {
        Assert.Equal(SetObjectTypeOutcome.NotFound, _repository.SetObjectType(Guid.NewGuid(), "Galaxy"));
        Assert.Empty(ObjectTypeEvents());
    }

    [Fact]
    public void SetObjectType_LeavesNameLockedAlone()
    {
        // Spec 12.4: a user's type choice is not a name lock, so a later re-resolve overwrites the
        // object type the same way it overwrites every other catalogue column. Rename sets the
        // flag; this must not, in either direction.
        var unlocked = AddTarget("M 31");
        _repository.SetObjectType(unlocked, "Galaxy");
        Assert.False(Read(unlocked).NameLocked);

        var locked = AddTarget("M 42");
        _repository.Rename(locked, "Orion Nebula");
        Assert.True(Read(locked).NameLocked);

        _repository.SetObjectType(locked, "Emission Nebula");

        Assert.True(Read(locked).NameLocked);
    }

    [Fact]
    public void SetObjectType_LeavesEveryOtherCatalogueColumnAlone()
    {
        // Spec 12.4's "nothing else is written", made executable: a fully populated target, one
        // type change, and every other column compared against what it was.
        Guid id;
        using (var context = Open())
        {
            var target = new Target
            {
                Id = Guid.NewGuid(),
                PrimaryName = "M 31",
                CatalogId = "NGC 224",
                CatalogIdNormalized = "ngc224",
                CommonName = "Andromeda Galaxy",
                Aliases = """["NGC 224","Andromeda Galaxy"]""",
                Ra = 10.6847d,
                Dec = 41.2692d,
                ObjectType = "G",
                Constellation = "And",
                SizeMajor = 189.1d,
                SizeMinor = 61.7d,
                PositionAngle = 145d,
                VMag = 3.44d,
                SurfaceBrightness = 22.2d,
                SacDescription = "!!! Andromeda Gx; vB, eL, eE",
                SacNotes = "Naked eye object",
                Notes = "an existing note",
                ReferenceThumbnailPath = "reference/andromeda.jpg",
                NameLocked = true,
                UserDefined = true,
            };
            context.Targets.Add(target);
            context.SaveChanges();
            id = target.Id;
        }

        var before = Read(id);

        _repository.SetObjectType(id, "Emission Nebula");

        var after = Read(id);
        Assert.Equal("HII", after.ObjectType);
        Assert.Equal(before.PrimaryName, after.PrimaryName);
        Assert.Equal(before.CatalogId, after.CatalogId);
        Assert.Equal(before.CatalogIdNormalized, after.CatalogIdNormalized);
        Assert.Equal(before.CommonName, after.CommonName);
        Assert.Equal(before.Aliases, after.Aliases);
        Assert.Equal(before.Ra, after.Ra);
        Assert.Equal(before.Dec, after.Dec);
        Assert.Equal(before.Constellation, after.Constellation);
        Assert.Equal(before.SizeMajor, after.SizeMajor);
        Assert.Equal(before.SizeMinor, after.SizeMinor);
        Assert.Equal(before.PositionAngle, after.PositionAngle);
        Assert.Equal(before.VMag, after.VMag);
        Assert.Equal(before.SurfaceBrightness, after.SurfaceBrightness);
        Assert.Equal(before.SacDescription, after.SacDescription);
        Assert.Equal(before.SacNotes, after.SacNotes);
        Assert.Equal(before.Notes, after.Notes);
        Assert.Equal(before.ReferenceThumbnailPath, after.ReferenceThumbnailPath);
        Assert.Equal(before.MergedIntoId, after.MergedIntoId);
        Assert.Equal(before.MergedAt, after.MergedAt);
        Assert.Equal(before.NameLocked, after.NameLocked);
        Assert.Equal(before.UserDefined, after.UserDefined);
    }

    [Fact]
    public void SetObjectType_DoesNotWidenTheRenameHistoryQuery()
    {
        // The rename history filters activity_events on its own event_type, so a second
        // user_action type in the table must not appear in it (brief section 7).
        var id = AddTarget("M 31");
        _repository.Rename(id, "Andromeda");

        _repository.SetObjectType(id, "Galaxy");

        var history = new RenameHistoryQuery(new DatabaseConnectionString(_db.ConnectionString)).Recent();
        var row = Assert.Single(history);
        Assert.Equal("M 31", row.PreviousName);
        Assert.Equal("Andromeda", row.NewName);
    }
}
