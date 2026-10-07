using GalactiLog.Core.Settings;
using GalactiLog.Data.Entities;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GalactiLog.Data.Tests.Repositories;

// Phase 20 Task 2. Spec 5.19, 5.20 and 12.15's storage against a migrated temp database: the
// coalescing unique index of migration 0006, the two cascades and the SQL nullness of a scope's
// unset key parts are all real here, and none of them can be asserted against an in-memory fake.
public class CustomColumnRepositoryTests : IDisposable
{
    private readonly TestDatabaseHandle _db;
    private readonly CustomColumnRepository _repository;

    public CustomColumnRepositoryTests()
    {
        _db = TestDatabaseFactory.CreateMigratedDatabase();
        _repository = new CustomColumnRepository(new DatabaseConnectionString(_db.ConnectionString));
    }

    public void Dispose() => _db.Dispose();

    // ---- the migration ------------------------------------------------------------------

    // Case 1. Red against a migration missing either CreateTable, and against one that dropped any
    // of the six indexes: every name here is the spec's own.
    [Fact]
    public void Migrate_CreatesBothTablesAndSixIndexes()
    {
        var tables = Names("table");
        Assert.Contains("custom_columns", tables);
        Assert.Contains("custom_column_values", tables);

        var indexes = Names("index");
        Assert.Contains("ux_custom_columns_slug", indexes);
        Assert.Contains("ix_custom_columns_display_order", indexes);
        Assert.Contains("ix_custom_column_values_target", indexes);
        Assert.Contains("ix_custom_column_values_column", indexes);
        Assert.Contains("ix_custom_column_values_mosaic", indexes);
        Assert.Contains("uq_custom_column_value", indexes);
    }

    // Case 2. Red against the raw SQL statement being omitted from the migration: the duplicate
    // insert then succeeds too, and the surface shows one of two rows at random.
    [Fact]
    public void TheUniqueValueIndex_CoalescesTheFourKeyParts()
    {
        var column = Column("Priority", CustomColumnType.Text, CustomColumnScope.Target);
        var target = NewTarget("NGC 7000");

        // Two rows for the same column and target, differing only in session_date: both legal,
        // because the coalesced key differs. The dated one is seeded straight to the table, since
        // SetValue refuses a dated key on a target-scope column; this case is about the index, not
        // about the key-shape guard.
        Written(_repository.SetValue(column.Id, CustomValueKey.ForTarget(target), "one"));
        SeedValue(column.Id, target, Date(4), rig: null, "two");
        Assert.Equal(2L, Scalar<long>("SELECT count(*) FROM custom_column_values"));

        // A byte-for-byte duplicate of the first row's key, copied from the row itself so no case
        // here depends on how SQLite spells a Guid.
        var duplicate = Record.Exception(() => Execute("""
            INSERT INTO custom_column_values
              (id, column_id, target_id, mosaic_id, session_date, rig_label, value, updated_at)
            SELECT lower(hex(randomblob(16))), column_id, target_id, mosaic_id, session_date,
                   rig_label, 'three', updated_at
            FROM custom_column_values WHERE session_date IS NULL
            """));

        var failure = Assert.IsType<SqliteException>(duplicate);
        Assert.Contains("uq_custom_column_value", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2L, Scalar<long>("SELECT count(*) FROM custom_column_values"));
    }

    // Case 3, Phase 18 (ruling R2). The mosaic foreign key exists, and the three target-keyed
    // scopes still leave mosaic_id null.
    [Fact]
    public void MosaicId_CarriesItsForeignKey_AndTheTargetScopesLeaveItNull()
    {
        // Asked of SQLite directly rather than by parsing CREATE TABLE text, so a change in how EF
        // formats that statement cannot turn this case green by accident (review P3-2).
        var keyed = ForeignKeyColumns("custom_column_values");
        Assert.Contains("mosaic_id", keyed);
        Assert.Contains("column_id", keyed);
        Assert.Contains("target_id", keyed);

        var target = NewTarget("NGC 7000");
        Written(_repository.SetValue(
            Column("A", CustomColumnType.Text, CustomColumnScope.Target).Id,
            CustomValueKey.ForTarget(target), "a"));
        Written(_repository.SetValue(
            Column("B", CustomColumnType.Text, CustomColumnScope.Session).Id,
            CustomValueKey.ForSession(target, Date(4)), "b"));
        Written(_repository.SetValue(
            Column("C", CustomColumnType.Text, CustomColumnScope.Rig).Id,
            CustomValueKey.ForRig(target, Date(4), "Askar 120 / ASI2600MC"), "c"));

        Assert.Equal(3L, Scalar<long>("SELECT count(*) FROM custom_column_values"));
        Assert.Equal(3L, Scalar<long>("SELECT count(*) FROM custom_column_values WHERE mosaic_id IS NULL"));
    }

    // ---- Create -------------------------------------------------------------------------

    // Case 4. Red against a slug with no prefix, which is departure 1's whole point: a column named
    // "Name" must not shadow the dashboard's built-in `name` key.
    [Theory]
    [InlineData("Processed", "custom_processed")]
    [InlineData("Notes tag", "custom_notes_tag")]
    [InlineData("!!!", "custom_column")]
    public void Create_DerivesTheSlugWithThePrefix(string name, string slug)
    {
        var result = _repository.Create(name, CustomColumnType.Text, CustomColumnScope.Target, []);

        Written(result);
        Assert.Equal(slug, result.Column!.Slug);
    }

    // Case 5. Red against Unique returning Base: the second insert would then violate
    // ux_custom_columns_slug and throw instead of answering Written.
    [Fact]
    public void Create_ASecondColumnWhoseSlugCollides_GetsTheSuffix()
    {
        var first = _repository.Create("Note-tag", CustomColumnType.Text, CustomColumnScope.Target, []);
        var second = _repository.Create("Note tag", CustomColumnType.Text, CustomColumnScope.Target, []);

        Written(first);
        Written(second);
        Assert.Equal("custom_note_tag", first.Column!.Slug);
        Assert.Equal("custom_note_tag_2", second.Column!.Slug);
    }

    // Case 6. Red against the web's silent second column. Compared case insensitively after
    // trimming, and composed with the SUBMITTED name, not the stored one.
    [Fact]
    public void Create_ADuplicateName_IsRefusedWithTheSentence()
    {
        Written(_repository.Create("Priority", CustomColumnType.Text, CustomColumnScope.Target, []));

        var result = _repository.Create("  priority  ", CustomColumnType.Text, CustomColumnScope.Session, []);

        Assert.Equal(CustomWriteStatus.DuplicateName, result.Status);
        Assert.Equal("A column named \"priority\" already exists.", result.Message);
        Assert.Single(_repository.List());
    }

    // Case 7. Red against a create that stores an option-less dropdown, which SetValue could then
    // never write a value into at all.
    [Fact]
    public void Create_ADropdownWithNoOptions_IsRefused()
    {
        var result = _repository.Create("Grade", CustomColumnType.Dropdown, CustomColumnScope.Target, []);

        Assert.Equal(CustomWriteStatus.DropdownWithNoOptions, result.Status);
        Assert.Equal("Add at least one option before adding a dropdown column.", result.Message);
        Assert.Empty(_repository.List());
    }

    // Companion to case 7, so it is not vacuous: a dropdown WITH options creates, and the options
    // come back trimmed, in the user's own order.
    [Fact]
    public void Create_ADropdownWithOptions_KeepsTheirOrderAndTrimsThem()
    {
        var result = _repository.Create(
            "Grade", CustomColumnType.Dropdown, CustomColumnScope.Target, ["  High ", "Low"]);

        Written(result);
        Assert.Equal(new[] { "High", "Low" }, result.Column!.Options);
    }

    // Case 8, Phase 18 (ruling R2): the mosaic scope is the fourth legal applies_to.
    [Fact]
    public void Create_AMosaicScope_IsWritten()
    {
        var result = _repository.Create("Framing", CustomColumnType.Text, CustomColumnScope.Mosaic, []);

        Written(result);
        Assert.Equal(CustomColumnScope.Mosaic, Assert.Single(_repository.List()).Scope);
        Assert.Equal("mosaic", Scalar<string>("SELECT applies_to FROM custom_columns"));
    }

    [Fact]
    public void Create_AnEmptyName_IsRefused()
    {
        var result = _repository.Create("   ", CustomColumnType.Text, CustomColumnScope.Target, []);

        Assert.Equal(CustomWriteStatus.EmptyName, result.Status);
        Assert.Equal("Enter a name for the column.", result.Message);
    }

    [Fact]
    public void Create_ANamePast60Characters_IsRefused_AndExactly60IsAccepted()
    {
        Written(_repository.Create(new string('a', 60), CustomColumnType.Text, CustomColumnScope.Target, []));

        var result = _repository.Create(new string('b', 61), CustomColumnType.Text, CustomColumnScope.Target, []);

        Assert.Equal(CustomWriteStatus.NameTooLong, result.Status);
        Assert.Equal("Keep the column name to 60 characters or fewer.", result.Message);
        Assert.Single(_repository.List());
    }

    [Fact]
    public void Create_TwoOptionsEqualAfterTrimming_IsRefusedWithTheSecondSpelling()
    {
        var result = _repository.Create(
            "Grade", CustomColumnType.Dropdown, CustomColumnScope.Target, ["High", " high "]);

        Assert.Equal(CustomWriteStatus.DuplicateOption, result.Status);
        Assert.Equal("\"high\" is already an option.", result.Message);
    }

    // Options on a column that is not a dropdown are refused, so one list of options means the same
    // thing on Create as it does on Update. Red against a Create that stores the column and drops
    // the options on the floor, which leaves the caller believing they were kept.
    [Theory]
    [InlineData(CustomColumnType.Text)]
    [InlineData(CustomColumnType.Boolean)]
    public void Create_OptionsOnAColumnThatIsNotADropdown_IsRefused(CustomColumnType type)
    {
        var result = _repository.Create("Notes", type, CustomColumnScope.Target, ["High"]);

        Assert.Equal(CustomWriteStatus.OptionsOnNonDropdown, result.Status);
        Assert.Equal("Only a dropdown column can have options.", result.Message);
        Assert.Empty(_repository.List());
    }

    [Fact]
    public void Create_PutsEachNewColumnLast()
    {
        var first = Created("A");
        var second = Created("B");

        Assert.Equal(new[] { first, second }, _repository.List().Select(column => column.Id).ToList());
        Assert.Equal(new[] { 0, 1 }, _repository.List().Select(column => column.DisplayOrder).ToList());
    }

    [Fact]
    public void List_ReportsTheStoredValueCount()
    {
        var column = Column("Priority", CustomColumnType.Text, CustomColumnScope.Target);
        Written(_repository.SetValue(column.Id, CustomValueKey.ForTarget(NewTarget("A")), "one"));
        Written(_repository.SetValue(column.Id, CustomValueKey.ForTarget(NewTarget("B")), "two"));

        Assert.Equal(2, Assert.Single(_repository.List()).ValueCount);
    }

    // Spec 5.8.2's "a hand-edited document still opens" rule, applied to this column too, plus the
    // unknown-word skip. Red against a reader that throws on either, or that returns the two
    // unparsable rows as though their words meant something.
    [Fact]
    public void List_SkipsAnUnknownWordAndReadsAMalformedOptionsDocumentAsEmpty()
    {
        Column("Good", CustomColumnType.Dropdown, CustomColumnScope.Target, options: "{not json");
        Column("Bad type", CustomColumnType.Text, CustomColumnScope.Target, storedType: "colour");
        Column("Bad scope", CustomColumnType.Text, CustomColumnScope.Target, storedScope: "galaxy");

        var listed = Assert.Single(_repository.List());

        Assert.Equal("Good", listed.Name);
        Assert.Empty(listed.Options);
    }

    // ---- Update and Reorder --------------------------------------------------------------

    // Case 10. Red against the web's re-slug (custom_columns.py line 106 re-slugs on every rename,
    // orphaning every stored display.columns entry and every restored filter at once).
    [Fact]
    public void Update_KeepsTheSlugByteIdentical()
    {
        var created = _repository.Create("Processed", CustomColumnType.Text, CustomColumnScope.Target, []);
        Written(created);
        var slug = created.Column!.Slug;

        Written(_repository.Update(created.Column.Id, "Done", []));
        Assert.Equal(slug, Assert.Single(_repository.List()).Slug);

        Written(_repository.Update(created.Column.Id, "Finished for good", []));
        Assert.Equal(slug, Assert.Single(_repository.List()).Slug);
    }

    // The other half of the same rule: neither the type nor the scope moves on a rename.
    [Fact]
    public void Update_KeepsTheTypeAndTheScope()
    {
        var created = _repository.Create("Grade", CustomColumnType.Dropdown, CustomColumnScope.Session, ["High"]);
        Written(created);

        Written(_repository.Update(created.Column!.Id, "Quality", ["High", "Low"]));

        var listed = Assert.Single(_repository.List());
        Assert.Equal("Quality", listed.Name);
        Assert.Equal(CustomColumnType.Dropdown, listed.Type);
        Assert.Equal(CustomColumnScope.Session, listed.Scope);
        Assert.Equal(new[] { "High", "Low" }, listed.Options);
    }

    // The Update half of the same rule, and the reason both halves are needed: an Update that
    // silently ignored the options renamed the column anyway, so a form that sent options to a text
    // column was told its whole submission was accepted. Red against that ignore, on both the status
    // and the rename it let through.
    [Fact]
    public void Update_OptionsOnAColumnThatIsNotADropdown_IsRefusedAndWritesNothing()
    {
        var column = Created("Notes");

        var result = _repository.Update(column, "Renamed", ["High"]);

        Assert.Equal(CustomWriteStatus.OptionsOnNonDropdown, result.Status);
        Assert.Equal("Only a dropdown column can have options.", result.Message);

        var listed = Assert.Single(_repository.List());
        Assert.Equal("Notes", listed.Name);
        Assert.Empty(listed.Options);
    }

    // An empty list is not a list of options, so the rename a surface sends with no options at all
    // still lands on a column that is not a dropdown.
    [Fact]
    public void Update_AnEmptyOptionListOnAColumnThatIsNotADropdown_StillRenames()
    {
        var column = Created("Notes");

        Written(_repository.Update(column, "Renamed", ["", "   "]));

        Assert.Equal("Renamed", Assert.Single(_repository.List()).Name);
    }

    // Case 11. Departure 10; the web has no check at all and leaves those two values outside their
    // own option set. Red against an update that drops the option regardless.
    [Fact]
    public void Update_RemovingAnOptionStillInUse_IsRefusedAndNamesTheCount()
    {
        var column = Dropdown("Grade", "High", "Low");
        Written(_repository.SetValue(column, CustomValueKey.ForTarget(NewTarget("A")), "Low"));
        Written(_repository.SetValue(column, CustomValueKey.ForTarget(NewTarget("B")), "Low"));

        var result = _repository.Update(column, "Grade", ["High"]);

        Assert.Equal(CustomWriteStatus.OptionStillUsed, result.Status);
        Assert.Equal("2 values still use this option. Change them first, or delete the column.", result.Message);
        Assert.Equal(new[] { "High", "Low" }, Assert.Single(_repository.List()).Options);
    }

    // Ruling C14: the sentence reads properly at one. One branch, one case.
    [Fact]
    public void Update_RemovingAnOptionUsedOnce_ReadsInTheSingular()
    {
        var column = Dropdown("Grade", "High", "Low");
        Written(_repository.SetValue(column, CustomValueKey.ForTarget(NewTarget("A")), "Low"));

        var result = _repository.Update(column, "Grade", ["High"]);

        Assert.Equal("1 value still uses this option. Change it first, or delete the column.", result.Message);
    }

    // Case 12, so case 11 is not vacuous.
    [Fact]
    public void Update_RemovingAnUnusedOption_Succeeds()
    {
        var column = Dropdown("Grade", "High", "Low");
        Written(_repository.SetValue(column, CustomValueKey.ForTarget(NewTarget("A")), "High"));

        Written(_repository.Update(column, "Grade", ["High"]));

        Assert.Equal(new[] { "High" }, Assert.Single(_repository.List()).Options);
    }

    [Fact]
    public void Update_ADuplicateNameOnAnotherColumn_IsRefused_AndItsOwnNameIsNot()
    {
        Created("Priority");
        var second = Created("Grade");

        Assert.Equal(CustomWriteStatus.DuplicateName, _repository.Update(second, "priority", []).Status);

        // The duplicate-name test excludes this column's own row, so renaming it to itself lands.
        Written(_repository.Update(second, "Grade", []));
    }

    [Fact]
    public void Update_AMissingColumn_AnswersColumnNotFoundWithNoSentence()
    {
        var result = _repository.Update(Guid.NewGuid(), "Priority", []);

        Assert.Equal(CustomWriteStatus.ColumnNotFound, result.Status);
        Assert.Null(result.Message);
    }

    // Case 9. Red against the web's shape (CustomColumnsTab.tsx lines 129 to 137 writes
    // display_order + 1 on the moved row alone): two columns then come to share an order, so the
    // distinctness assertion fails and the second press below moves nothing.
    [Fact]
    public void Reorder_SwapsBothRowsAndKeepsTheOrdersDenseAndDistinct()
    {
        var a = Created("A");
        var b = Created("B");
        var c = Created("C");

        Written(_repository.Reorder(b, up: true));

        var listed = _repository.List();
        Assert.Equal(new[] { b, a, c }, listed.Select(column => column.Id).ToList());
        Assert.Equal(new[] { 0, 1, 2 }, listed.Select(column => column.DisplayOrder).Order().ToList());
        Assert.Equal(3, listed.Select(column => column.DisplayOrder).Distinct().Count());

        // The first row moving up writes nothing.
        Written(_repository.Reorder(b, up: true));
        Assert.Equal(new[] { b, a, c }, _repository.List().Select(column => column.Id).ToList());
    }

    [Fact]
    public void Reorder_ALastRowMovingDown_WritesNothing()
    {
        var a = Created("A");
        var b = Created("B");

        Written(_repository.Reorder(b, up: false));

        Assert.Equal(new[] { a, b }, _repository.List().Select(column => column.Id).ToList());
    }

    [Fact]
    public void Reorder_AColumnThatIsNotThere_WritesNothing()
    {
        var a = Created("A");

        Written(_repository.Reorder(Guid.NewGuid(), up: true));

        Assert.Equal(new[] { a }, _repository.List().Select(column => column.Id).ToList());
    }

    // Case 13. Red against a missing cascade in the fluent model: the two values would survive
    // their definition and the surviving row below would not be the only one.
    [Fact]
    public void Delete_CascadesEveryValue()
    {
        var column = Column("Priority", CustomColumnType.Text, CustomColumnScope.Target);
        var other = Column("Grade", CustomColumnType.Text, CustomColumnScope.Target);
        Written(_repository.SetValue(column.Id, CustomValueKey.ForTarget(NewTarget("A")), "one"));
        Written(_repository.SetValue(column.Id, CustomValueKey.ForTarget(NewTarget("B")), "two"));
        Written(_repository.SetValue(other.Id, CustomValueKey.ForTarget(NewTarget("C")), "kept"));

        Written(_repository.Delete(column.Id));

        using var context = Open();
        Assert.Equal(other.Id, Assert.Single(context.CustomColumnValues.ToList()).ColumnId);
    }

    // ---- SetValue -------------------------------------------------------------------------

    // Case 14. Red against a key built by hand that writes '' for an unset part: the nullness is
    // read through sqlite3's own column values, not through the entity, so an empty string is
    // distinguishable from a null.
    [Fact]
    public void SetValue_UpsertsOnEachOfTheThreeKeyShapes()
    {
        var target = NewTarget("NGC 7000");
        const string Label = "Askar 120 / ASI2600MC";

        Upserts(
            Column("T", CustomColumnType.Text, CustomColumnScope.Target).Id,
            CustomValueKey.ForTarget(target), nights: 0, rigs: 0);
        Upserts(
            Column("S", CustomColumnType.Text, CustomColumnScope.Session).Id,
            CustomValueKey.ForSession(target, Date(4)), nights: 1, rigs: 0);
        Upserts(
            Column("R", CustomColumnType.Text, CustomColumnScope.Rig).Id,
            CustomValueKey.ForRig(target, Date(4), Label), nights: 1, rigs: 1);

        void Upserts(Guid columnId, CustomValueKey key, int nights, int rigs)
        {
            Written(_repository.SetValue(columnId, key, "first"));
            Written(_repository.SetValue(columnId, key, "second"));

            // One row, not two: the second write found the first through the four key parts.
            Assert.Equal(1L, Scalar<long>("SELECT count(*) FROM custom_column_values"));
            Assert.Equal("second", Scalar<string>("SELECT value FROM custom_column_values"));

            Assert.Equal(0L, NullCount("target_id"));
            Assert.Equal(1L, NullCount("mosaic_id"));
            Assert.Equal(1L - nights, NullCount("session_date"));
            Assert.Equal(1L - rigs, NullCount("rig_label"));

            // Each shape is read against an otherwise empty table, so no assertion above needs a
            // Guid spelled into its own command text.
            Execute("DELETE FROM custom_column_values");
        }

        long NullCount(string part)
            => Scalar<long>($"SELECT count(*) FROM custom_column_values WHERE {part} IS NULL");
    }

    // Case 15. Departure 6 and user choices 11 and 12. Red against storing an empty string, which
    // is what the web does and which would make an emptied cell and a never-filled cell two
    // different states in the filter.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SetValue_AnEmptyOrWhitespaceValue_DeletesTheRow(string? cleared)
    {
        var target = NewTarget("NGC 7000");
        var text = Column("Notes", CustomColumnType.Text, CustomColumnScope.Target);
        var dropdown = Dropdown("Grade", "High");

        Written(_repository.SetValue(text.Id, CustomValueKey.ForTarget(target), "typed"));
        Written(_repository.SetValue(dropdown, CustomValueKey.ForTarget(target), "High"));
        Assert.Equal(2L, Scalar<long>("SELECT count(*) FROM custom_column_values"));

        var first = _repository.SetValue(text.Id, CustomValueKey.ForTarget(target), cleared);
        var second = _repository.SetValue(dropdown, CustomValueKey.ForTarget(target), cleared);

        Assert.Equal(CustomWriteStatus.Deleted, first.Status);
        Assert.Equal(CustomWriteStatus.Deleted, second.Status);
        Assert.Null(first.Message);
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM custom_column_values"));
    }

    // Clearing a slot that holds nothing is not an error and writes nothing.
    [Fact]
    public void SetValue_ClearingASlotThatHoldsNothing_AnswersDeleted()
    {
        var column = Column("Notes", CustomColumnType.Text, CustomColumnScope.Target);

        var result = _repository.SetValue(column.Id, CustomValueKey.ForTarget(NewTarget("A")), "");

        Assert.Equal(CustomWriteStatus.Deleted, result.Status);
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM custom_column_values"));
    }

    // Case 16. The check box never sends anything else; a hand-driven caller does.
    [Fact]
    public void SetValue_ABooleanOutsideTheTwoLiterals_IsRefused()
    {
        var column = Column("Processed", CustomColumnType.Boolean, CustomColumnScope.Target);

        var result = _repository.SetValue(column.Id, CustomValueKey.ForTarget(NewTarget("A")), "yes");

        Assert.Equal(CustomWriteStatus.NotABoolean, result.Status);
        Assert.Equal("A checkbox column stores only yes or no.", result.Message);
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM custom_column_values"));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void SetValue_TheTwoBooleanLiterals_AreStored(string literal)
    {
        var column = Column("Processed", CustomColumnType.Boolean, CustomColumnScope.Target);

        Written(_repository.SetValue(column.Id, CustomValueKey.ForTarget(NewTarget("A")), literal));

        Assert.Equal(literal, Scalar<string>("SELECT value FROM custom_column_values"));
    }

    // Case 17. Ordinal and case sensitive: an option "High" refuses "high".
    [Fact]
    public void SetValue_ADropdownValueOutsideTheOptions_IsRefused()
    {
        var column = Dropdown("Grade", "High", "Low");
        var target = NewTarget("A");

        var outside = _repository.SetValue(column, CustomValueKey.ForTarget(target), "Middling");
        Assert.Equal(CustomWriteStatus.NotAnOption, outside.Status);
        Assert.Equal("\"Middling\" is not one of this column's options.", outside.Message);

        var wrongCase = _repository.SetValue(column, CustomValueKey.ForTarget(target), "high");
        Assert.Equal(CustomWriteStatus.NotAnOption, wrongCase.Status);
        Assert.Equal("\"high\" is not one of this column's options.", wrongCase.Message);

        Written(_repository.SetValue(column, CustomValueKey.ForTarget(target), "High"));
    }

    // Case 18. Departure 4: the web skips its membership check when the option list is empty
    // (custom_columns.py line 154 requires col.dropdown_options to be truthy) and stores anything
    // sent. Red against that truthiness guard.
    [Fact]
    public void SetValue_OnAnOptionlessDropdown_RefusesEveryValue()
    {
        var column = Column("Grade", CustomColumnType.Dropdown, CustomColumnScope.Target, options: null);

        var result = _repository.SetValue(column.Id, CustomValueKey.ForTarget(NewTarget("A")), "anything");

        Assert.Equal(CustomWriteStatus.OptionlessDropdown, result.Status);
        Assert.Equal("This column has no options yet. Add one on the Custom Columns tab.", result.Message);
        Assert.Equal(0L, Scalar<long>("SELECT count(*) FROM custom_column_values"));
    }

    // Case 19. Boundary on both sides.
    [Fact]
    public void SetValue_ATextValuePast500Characters_IsRefused()
    {
        var column = Column("Notes", CustomColumnType.Text, CustomColumnScope.Target);
        var target = NewTarget("A");

        Written(_repository.SetValue(column.Id, CustomValueKey.ForTarget(target), new string('a', 500)));

        var result = _repository.SetValue(column.Id, CustomValueKey.ForTarget(target), new string('a', 501));

        Assert.Equal(CustomWriteStatus.ValueTooLong, result.Status);
        Assert.Equal("Keep the value to 500 characters or fewer.", result.Message);
        Assert.Equal(new string('a', 500), Scalar<string>("SELECT value FROM custom_column_values"));
    }

    // Case 20.
    [Fact]
    public void SetValue_TrimsTheStoredTextValue()
    {
        var column = Column("Notes", CustomColumnType.Text, CustomColumnScope.Target);

        Written(_repository.SetValue(column.Id, CustomValueKey.ForTarget(NewTarget("A")), "  typed  "));

        Assert.Equal("typed", Scalar<string>("SELECT value FROM custom_column_values"));
    }

    [Fact]
    public void SetValue_AMissingColumn_AnswersColumnNotFoundWithNoSentence()
    {
        var result = _repository.SetValue(Guid.NewGuid(), CustomValueKey.ForTarget(NewTarget("A")), "one");

        Assert.Equal(CustomWriteStatus.ColumnNotFound, result.Status);
        Assert.Null(result.Message);
    }

    // Fix pass 1, P2-1. One case per scope: a key whose four parts are not the ones its column's
    // scope names is refused and nothing is written. Red against a SetValue that writes whatever
    // four parts it was handed, which stores a row no read returns, no surface can clear, and which
    // still counts in ValueCount and in Update's option census.
    [Fact]
    public void SetValue_ATargetScopeColumnGivenANightOrARig_IsRefused()
    {
        var column = Column("T", CustomColumnType.Text, CustomColumnScope.Target);
        var target = NewTarget("A");

        Refuses(column.Id, CustomValueKey.ForSession(target, Date(4)), "one");
        Refuses(column.Id, CustomValueKey.ForRig(target, Date(4), "Askar 120 / ASI2600MC"), "one");

        // The shape its scope does name still writes, so the case is not vacuous.
        Written(_repository.SetValue(column.Id, CustomValueKey.ForTarget(target), "one"));
    }

    [Fact]
    public void SetValue_ASessionScopeColumnGivenNoNightOrARig_IsRefused()
    {
        var column = Column("S", CustomColumnType.Text, CustomColumnScope.Session);
        var target = NewTarget("A");

        Refuses(column.Id, CustomValueKey.ForTarget(target), "one");
        Refuses(column.Id, CustomValueKey.ForRig(target, Date(4), "Askar 120 / ASI2600MC"), "one");

        Written(_repository.SetValue(column.Id, CustomValueKey.ForSession(target, Date(4)), "one"));
    }

    [Fact]
    public void SetValue_ARigScopeColumnGivenNoRigOrNoNight_IsRefused()
    {
        var column = Column("R", CustomColumnType.Text, CustomColumnScope.Rig);
        var target = NewTarget("A");
        const string Label = "Askar 120 / ASI2600MC";

        Refuses(column.Id, CustomValueKey.ForTarget(target), "one");
        Refuses(column.Id, CustomValueKey.ForSession(target, Date(4)), "one");

        // A rig label is judged set or unset and is never trimmed into shape, so a blank one is
        // refused rather than stored or repaired.
        Refuses(column.Id, new CustomValueKey(target, MosaicId: null, Date(4), "   "), "one");
        Refuses(column.Id, new CustomValueKey(target, MosaicId: null, SessionDate: null, Label), "one");

        Written(_repository.SetValue(column.Id, CustomValueKey.ForRig(target, Date(4), Label), "one"));
    }

    [Fact]
    public void SetValue_AKeyWithNoTarget_IsRefused()
    {
        var column = Column("T", CustomColumnType.Text, CustomColumnScope.Target);

        Refuses(column.Id, default, "one");
    }

    // Fix pass 1, P2-2. `CustomValueKey`'s positional constructor is public, so a mosaic id can be
    // put in a key from any surface. The slot lookup compares it and the insert hard-codes null, so
    // without this refusal the write would land in a DIFFERENT slot than the caller named, silently
    // or as an uncaught unique-index violation. Red against a SetValue with no key-shape guard.
    [Fact]
    public void SetValue_AKeyCarryingAMosaicId_IsRefusedOnEveryScope()
    {
        var target = NewTarget("A");
        var mosaic = Guid.NewGuid();

        Refuses(
            Column("T", CustomColumnType.Text, CustomColumnScope.Target).Id,
            new CustomValueKey(target, mosaic, SessionDate: null, RigLabel: null), "one");
        Refuses(
            Column("S", CustomColumnType.Text, CustomColumnScope.Session).Id,
            new CustomValueKey(target, mosaic, Date(4), RigLabel: null), "one");
        Refuses(
            Column("R", CustomColumnType.Text, CustomColumnScope.Rig).Id,
            new CustomValueKey(target, mosaic, Date(4), "Askar 120 / ASI2600MC"), "one");
    }

    // A mis-shaped key cannot clear either: a caller that cannot name the slot cannot empty it.
    [Fact]
    public void SetValue_AMisShapedKeyClearingAValue_IsRefusedAndDeletesNothing()
    {
        var column = Column("T", CustomColumnType.Text, CustomColumnScope.Target);
        var target = NewTarget("A");
        Written(_repository.SetValue(column.Id, CustomValueKey.ForTarget(target), "kept"));

        var result = _repository.SetValue(column.Id, CustomValueKey.ForSession(target, Date(4)), "");

        Assert.Equal(CustomWriteStatus.KeyDoesNotMatchScope, result.Status);
        Assert.Null(result.Message);
        Assert.Equal("kept", Scalar<string>("SELECT value FROM custom_column_values"));
    }

    // ---- the mosaic scope (Phase 18, spec 5.20 and 12.15) ------------------------------------

    private Guid NewMosaic(string name)
        => new MosaicRepository(new DatabaseConnectionString(_db.ConnectionString)).Create(name);

    [Fact]
    public void SetValue_TheMosaicScope_RoundTripsThroughBothReaders_AndClears()
    {
        var column = Column("Framing", CustomColumnType.Text, CustomColumnScope.Mosaic);
        var mosaic = NewMosaic("NGC 7000");
        var other = NewMosaic("IC 1396");

        Written(_repository.SetValue(column.Id, CustomValueKey.ForMosaic(mosaic), " 0 degrees "));
        Written(_repository.SetValue(column.Id, CustomValueKey.ForMosaic(other), "90 degrees"));

        var row = Assert.Single(_repository.ValuesForMosaic(mosaic));
        Assert.Equal((column.Id, CustomValueKey.ForMosaic(mosaic), "0 degrees"), (row.ColumnId, row.Key, row.Value));
        Assert.Equal(2, _repository.ValuesForMosaics([mosaic, other]).Count);
        Assert.Empty(_repository.ValuesForMosaics([]));
        Assert.Equal(1L, Scalar<long>("SELECT count(*) FROM custom_column_values WHERE target_id IS NULL AND session_date IS NULL AND rig_label IS NULL AND mosaic_id IS NOT NULL AND value = '90 degrees'"));

        Assert.Equal(CustomWriteStatus.Deleted, _repository.SetValue(column.Id, CustomValueKey.ForMosaic(mosaic), "").Status);
        Assert.Empty(_repository.ValuesForMosaic(mosaic));
    }

    // Spec 5.20's key rule for the fourth scope: a mosaic key carries a mosaic id and nothing
    // else, and a target-keyed scope refuses a key that names a mosaic.
    [Fact]
    public void SetValue_AMosaicKeyCarryingATargetANightOrNoMosaic_IsRefused()
    {
        var column = Column("Framing", CustomColumnType.Text, CustomColumnScope.Mosaic);
        var mosaic = NewMosaic("NGC 7000");
        var target = NewTarget("A");

        Refuses(column.Id, new CustomValueKey(target, mosaic, SessionDate: null, RigLabel: null), "one");
        Refuses(column.Id, new CustomValueKey(null, mosaic, Date(4), RigLabel: null), "one");
        Refuses(column.Id, new CustomValueKey(null, mosaic, SessionDate: null, "Askar 120 / ASI2600MC"), "one");
        Refuses(column.Id, CustomValueKey.ForTarget(target), "one");
        Refuses(column.Id, default, "one");
        Refuses(Column("T", CustomColumnType.Text, CustomColumnScope.Target).Id, CustomValueKey.ForMosaic(mosaic), "one");
    }

    // The readers are scoped by the owning column, so a mosaic reader never returns a target row
    // and the target readers never return a mosaic row.
    [Fact]
    public void TheMosaicAndTargetReaders_DoNotSeeEachOthersRows()
    {
        var target = NewTarget("A");
        var mosaic = NewMosaic("NGC 7000");
        Written(_repository.SetValue(Column("T", CustomColumnType.Text, CustomColumnScope.Target).Id, CustomValueKey.ForTarget(target), "t"));
        Written(_repository.SetValue(Column("M", CustomColumnType.Text, CustomColumnScope.Mosaic).Id, CustomValueKey.ForMosaic(mosaic), "m"));

        Assert.Equal("m", Assert.Single(_repository.ValuesForMosaics([mosaic])).Value);
        Assert.Equal("t", Assert.Single(_repository.TargetValues([target])).Value);
        Assert.Empty(_repository.ValuesForTarget(target));
    }

    // ---- the two reads ---------------------------------------------------------------------

    // Case 21. Red against a read keyed on target_id alone: the three excluded rows below are one
    // per exclusion. The third is the one only the scope join catches, a dateless row under a
    // session-scope column: the null key parts alone do not tell it from a target-scope row, and
    // SetValue does not refuse a key whose shape disagrees with its column's scope, so it is
    // reachable and the dashboard would draw it in a column it does not belong to.
    [Fact]
    public void TargetValues_ReturnsOnlyTargetScopeRows_ForTheGivenIds()
    {
        var wanted = NewTarget("A");
        var other = NewTarget("B");
        var targetScope = Column("T", CustomColumnType.Text, CustomColumnScope.Target);
        var sessionScope = Column("S", CustomColumnType.Text, CustomColumnScope.Session);

        Written(_repository.SetValue(targetScope.Id, CustomValueKey.ForTarget(wanted), "wanted"));
        Written(_repository.SetValue(sessionScope.Id, CustomValueKey.ForSession(wanted, Date(4)), "a night"));
        Written(_repository.SetValue(targetScope.Id, CustomValueKey.ForTarget(other), "another target"));

        // Seeded straight to the table, because SetValue now refuses a key whose shape disagrees
        // with its column's scope. The case keeps its meaning: a hand-edited catalogue can still
        // hold a dateless row under a session-scope column, and the scope join is the only thing
        // that tells it from a target-scope row.
        SeedValue(sessionScope.Id, wanted, night: null, rig: null, "session column, no night");

        var row = Assert.Single(_repository.TargetValues([wanted]));

        Assert.Equal("wanted", row.Value);
        Assert.Equal(targetScope.Id, row.ColumnId);
        Assert.Equal(CustomValueKey.ForTarget(wanted), row.Key);
    }

    [Fact]
    public void TargetValues_AnEmptyIdSet_ReturnsAnEmptyList()
        => Assert.Empty(_repository.TargetValues([]));

    // Case 22.
    [Fact]
    public void ValuesForTarget_ReturnsSessionAndRigRowsAndNoTargetScopeRow()
    {
        var target = NewTarget("A");
        const string Label = "Askar 120 / ASI2600MC";
        var targetScope = Column("T", CustomColumnType.Text, CustomColumnScope.Target);
        var sessionScope = Column("S", CustomColumnType.Text, CustomColumnScope.Session);
        var rigScope = Column("R", CustomColumnType.Text, CustomColumnScope.Rig);

        Written(_repository.SetValue(targetScope.Id, CustomValueKey.ForTarget(target), "target"));
        Written(_repository.SetValue(sessionScope.Id, CustomValueKey.ForSession(target, Date(4)), "session"));
        Written(_repository.SetValue(rigScope.Id, CustomValueKey.ForRig(target, Date(4), Label), "rig"));
        Written(_repository.SetValue(sessionScope.Id, CustomValueKey.ForSession(NewTarget("B"), Date(4)), "elsewhere"));

        var rows = _repository.ValuesForTarget(target);

        Assert.Equal(new[] { "rig", "session" }, rows.Select(row => row.Value).Order(StringComparer.Ordinal).ToList());
        Assert.DoesNotContain(rows, row => row.ColumnId == targetScope.Id);
        Assert.Equal(CustomValueKey.ForRig(target, Date(4), Label), rows.Single(row => row.Value == "rig").Key);
    }

    // ---- RewriteRigLabels ---------------------------------------------------------------

    // Case 23. Red against a rewrite keyed on the telescope half alone: the camera-only rename of
    // the third row would then leave its label where it was.
    [Fact]
    public void RewriteRigLabels_MovesEveryMatchingLabelInOneTransaction()
    {
        var target = NewTarget("A");
        var column = Column("R", CustomColumnType.Text, CustomColumnScope.Rig);

        Rig(column.Id, target, 4, "Askar 120 / ASI2600MC");   // both halves rename
        Rig(column.Id, target, 5, "Askar 120 / ASI533MC");    // the telescope half alone
        Rig(column.Id, target, 6, "RedCat 51 / ASI2600MC");   // the camera half alone
        Rig(column.Id, target, 7, "RedCat 51 / ASI533MC");    // neither, through an identity pair
        Rig(column.Id, target, 8, "Unknown / ASI071MC");      // neither

        var moved = _repository.RewriteRigLabels(
            // The RedCat pair's old and new name are equal, so it contributes no label move.
            new Dictionary<string, string> { ["Askar 120"] = "Askar FMA180", ["RedCat 51"] = "RedCat 51" },
            new Dictionary<string, string> { ["ASI2600MC"] = "ASI2600MC Pro" });

        Assert.Equal(3, moved);
        Assert.Equal(
            new[]
            {
                "Askar FMA180 / ASI2600MC Pro",
                "Askar FMA180 / ASI533MC",
                "RedCat 51 / ASI2600MC Pro",
                "RedCat 51 / ASI533MC",
                "Unknown / ASI071MC",
            },
            Labels());
    }

    // The literal "Unknown" is part of the spelling SessionDetailQuery builds (line 418): a frame
    // with no telescope contributes "Unknown / ASI2600MC", and a rename never produces or consumes
    // it. Red against a rewrite that read the word as a missing half and dropped or replaced it.
    [Fact]
    public void RewriteRigLabels_LeavesTheUnknownHalfAlone()
    {
        var target = NewTarget("A");
        var column = Column("R", CustomColumnType.Text, CustomColumnScope.Rig);
        Rig(column.Id, target, 4, "Unknown / ASI2600MC");

        var moved = _repository.RewriteRigLabels(
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["ASI2600MC"] = "ASI533MC" });

        Assert.Equal(1, moved);
        Assert.Equal(new[] { "Unknown / ASI533MC" }, Labels());
    }

    // Fix pass 1, the fold rule, ruled by the coordinator. Folding two telescope names into one
    // lands two stored rig values on one slot. The newest is kept and the other is deleted, inside
    // the same transaction. Red against a rewrite with no fold handling: `SaveChanges` throws
    // uq_custom_column_value, the whole rewrite rolls back, and because the rename pairs are
    // rebuilt from the loaded names on every save the SAME collision fails again on every later
    // save, so those values never reach a surface again.
    [Fact]
    public void RewriteRigLabels_AFoldWithACollision_KeepsTheNewestAndLeavesOneRowInTheSlot()
    {
        var target = NewTarget("A");
        var column = Column("R", CustomColumnType.Text, CustomColumnScope.Rig);
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Same column, same target, same night: after RedCat 51 becomes Askar 120 these are one
        // slot. The RedCat row is the newer of the two.
        SeedValue(column.Id, target, Date(4), "Askar 120 / ASI2600MC", "the older value", stamp);
        var newest = SeedValue(column.Id, target, Date(4), "RedCat 51 / ASI2600MC", "the newest value", stamp.AddHours(1));

        // A row of the same column on another night is not in the slot and is untouched.
        SeedValue(column.Id, target, Date(5), "RedCat 51 / ASI2600MC", "another night", stamp);

        var changed = _repository.RewriteRigLabels(
            new Dictionary<string, string> { ["RedCat 51"] = "Askar 120" },
            new Dictionary<string, string>());

        // One row rewritten on the other night, one rewritten in the slot, one deleted by the fold.
        Assert.Equal(3, changed);

        using var context = Open();
        var rows = context.CustomColumnValues.ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("Askar 120 / ASI2600MC", row.RigLabel));

        var survivor = Assert.Single(rows, row => row.SessionDate == Date(4));
        Assert.Equal(newest, survivor.Id);
        Assert.Equal("the newest value", survivor.Value);
        Assert.Equal("another night", Assert.Single(rows, row => row.SessionDate == Date(5)).Value);
    }

    // The same fold with nothing to collide with deletes nothing, so the case above is not a
    // licence to delete on every rename.
    [Fact]
    public void RewriteRigLabels_AFoldWithNoCollision_DeletesNothing()
    {
        var target = NewTarget("A");
        var column = Column("R", CustomColumnType.Text, CustomColumnScope.Rig);

        // Two names folding into one, but on different nights, and a third row on a different
        // column: no two rows share a slot afterwards.
        var other = Column("R2", CustomColumnType.Text, CustomColumnScope.Rig);
        Rig(column.Id, target, 4, "Askar 120 / ASI2600MC");
        Rig(column.Id, target, 5, "RedCat 51 / ASI2600MC");
        Rig(other.Id, target, 4, "RedCat 51 / ASI2600MC");

        var changed = _repository.RewriteRigLabels(
            new Dictionary<string, string> { ["RedCat 51"] = "Askar 120" },
            new Dictionary<string, string>());

        Assert.Equal(2, changed);
        Assert.Equal(3L, Scalar<long>("SELECT count(*) FROM custom_column_values"));
        Assert.Equal(
            new[] { "Askar 120 / ASI2600MC", "Askar 120 / ASI2600MC", "Askar 120 / ASI2600MC" },
            Labels());
    }

    // A rename chain: A becomes B while B becomes C, in one save. Each row moves once, and no row
    // is folded, because the two destinations are different slots. Red against a rewrite that
    // applies the map to a label it has already rewritten.
    [Fact]
    public void RewriteRigLabels_ARenameChain_MovesEachRowOnce()
    {
        var target = NewTarget("A");
        var column = Column("R", CustomColumnType.Text, CustomColumnScope.Rig);
        Rig(column.Id, target, 4, "Askar 120 / ASI2600MC");
        Rig(column.Id, target, 5, "Askar FMA180 / ASI2600MC");

        var changed = _repository.RewriteRigLabels(
            new Dictionary<string, string> { ["Askar 120"] = "Askar FMA180", ["Askar FMA180"] = "RedCat 51" },
            new Dictionary<string, string>());

        Assert.Equal(2, changed);
        Assert.Equal(new[] { "Askar FMA180 / ASI2600MC", "RedCat 51 / ASI2600MC" }, Labels());
    }

    // A swap: each row lands on the other name exactly once and neither is folded.
    [Fact]
    public void RewriteRigLabels_ASwap_LandsEachRowOnTheOtherName()
    {
        var target = NewTarget("A");
        var column = Column("R", CustomColumnType.Text, CustomColumnScope.Rig);
        Rig(column.Id, target, 4, "Askar 120 / ASI2600MC");
        Rig(column.Id, target, 5, "RedCat 51 / ASI2600MC");

        var changed = _repository.RewriteRigLabels(
            new Dictionary<string, string> { ["Askar 120"] = "RedCat 51", ["RedCat 51"] = "Askar 120" },
            new Dictionary<string, string>());

        Assert.Equal(2, changed);
        Assert.Equal(new[] { "Askar 120 / ASI2600MC", "RedCat 51 / ASI2600MC" }, Labels());
        Assert.Equal("value", Scalar<string>("SELECT value FROM custom_column_values WHERE session_date = '2026-01-04'"));
    }

    [Fact]
    public void RewriteRigLabels_NoMatchingLabel_WritesNothing()
    {
        var target = NewTarget("A");
        var column = Column("R", CustomColumnType.Text, CustomColumnScope.Rig);
        Rig(column.Id, target, 4, "Askar 120 / ASI2600MC");

        Assert.Equal(0, _repository.RewriteRigLabels(
            new Dictionary<string, string>(), new Dictionary<string, string>()));
        Assert.Equal(0, _repository.RewriteRigLabels(
            new Dictionary<string, string> { ["Nothing stored"] = "Still nothing" },
            new Dictionary<string, string>()));

        Assert.Equal(new[] { "Askar 120 / ASI2600MC" }, Labels());
    }

    // ---- the Changed event -----------------------------------------------------------------

    // Ruling C22's own contract, both halves in one case, because the halves are only meaningful
    // against each other. Red against a repository with no raise at all (the event never fires and
    // every count reads 0) and red against a raise placed in SetValue as well (the value writes
    // take the total past 5, and the second half's figure moves).
    [Fact]
    public void ADefinitionWrite_RaisesChangedOnce_AndAValueWriteRaisesNone()
    {
        var raises = 0;
        _repository.Changed += (_, _) => raises++;

        var column = Created("Priority");
        Assert.Equal(1, raises);

        Written(_repository.Update(column, "Priority note", []));
        Assert.Equal(2, raises);

        var second = Created("Processed");
        Assert.Equal(3, raises);

        Written(_repository.Reorder(second, up: true));
        Assert.Equal(4, raises);

        // Every value write, in every shape the surfaces make: a write, an overwrite, and a clear
        // that deletes the row. None of them is a definition change, and an event here would rebuild
        // a row under the keystroke that made it.
        var target = NewTarget("NGC 7000");
        Written(_repository.SetValue(column, CustomValueKey.ForTarget(target), "one"));
        Written(_repository.SetValue(column, CustomValueKey.ForTarget(target), "two"));
        Assert.Equal(CustomWriteStatus.Deleted, _repository.SetValue(column, CustomValueKey.ForTarget(target), null).Status);
        Assert.Equal(4, raises);

        Written(_repository.Delete(column));
        Assert.Equal(5, raises);
    }

    // The second half of "after a definition write": a write that wrote nothing announces nothing.
    // Red against a raise placed at the top of each method rather than after its commit, and red
    // against one placed before Reorder's early return.
    [Fact]
    public void ARefusedWriteAndAReorderWithNowhereToGo_RaiseNothing()
    {
        var only = Created("Priority");

        var raises = 0;
        _repository.Changed += (_, _) => raises++;

        // A first row asked to move up, and a last row asked to move down: both answer Written and
        // both wrote nothing.
        Written(_repository.Reorder(only, up: true));
        Written(_repository.Reorder(only, up: false));
        Assert.Equal(0, raises);

        // A refusal at each of the three definition writers.
        Assert.Equal(
            CustomWriteStatus.DuplicateName,
            _repository.Create("Priority", CustomColumnType.Text, CustomColumnScope.Target, []).Status);
        Assert.Equal(CustomWriteStatus.EmptyName, _repository.Update(only, "   ", []).Status);
        Assert.Equal(CustomWriteStatus.ColumnNotFound, _repository.Delete(Guid.NewGuid()).Status);
        Assert.Equal(0, raises);
    }

    // The out-of-band door, for the one writer that does not pass through this repository: the
    // database reset truncates both tables itself. Red against a repository that keeps the raise
    // private, which does not compile, and against one whose event has no public announce at all.
    [Fact]
    public void NotifyDefinitionsChanged_RaisesTheSameEvent()
    {
        var raises = 0;
        _repository.Changed += (_, _) => raises++;

        _repository.NotifyDefinitionsChanged();

        Assert.Equal(1, raises);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static DateOnly Date(int day) => new(2026, 1, day);

    private GalactiLogContext Open()
        => new(GalactiLogContextOptions.Create(_db.ConnectionString, tracking: true));

    private Guid NewTarget(string primaryName)
        => LibrarySeeder.AddTarget(_db.ConnectionString, primaryName).Id;

    private Guid Created(string name)
    {
        var result = _repository.Create(name, CustomColumnType.Text, CustomColumnScope.Target, []);
        Written(result);
        return result.Column!.Id;
    }

    private Guid Dropdown(string name, params string[] options)
    {
        var result = _repository.Create(name, CustomColumnType.Dropdown, CustomColumnScope.Target, options);
        Written(result);
        return result.Column!.Id;
    }

    private void Rig(Guid columnId, Guid targetId, int day, string label)
        => Written(_repository.SetValue(columnId, CustomValueKey.ForRig(targetId, Date(day), label), "value"));

    /// <summary>A value row written straight to the table, so a case can seed a shape the
    /// repository's own <c>SetValue</c> refuses, or an exact <c>updated_at</c>.</summary>
    private Guid SeedValue(
        Guid columnId, Guid? targetId, DateOnly? night, string? rig, string value, DateTime? updatedAt = null)
    {
        using var context = Open();
        var row = new CustomColumnValue
        {
            Id = Guid.NewGuid(),
            ColumnId = columnId,
            TargetId = targetId,
            MosaicId = null,
            SessionDate = night,
            RigLabel = rig,
            Value = value,
            UpdatedAt = updatedAt ?? DateTime.UtcNow,
        };

        context.CustomColumnValues.Add(row);
        context.SaveChanges();
        return row.Id;
    }

    /// <summary>A row written straight to the table, so a case can seed a stored word or an
    /// options document the repository's own <c>Create</c> would refuse.</summary>
    private CustomColumn Column(
        string name,
        CustomColumnType type,
        CustomColumnScope scope,
        string? options = null,
        string? storedType = null,
        string? storedScope = null)
    {
        using var context = Open();
        var row = new CustomColumn
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = CustomColumnSlug.Base(name) + "_" + Guid.NewGuid().ToString("N")[..4],
            ColumnType = storedType ?? CustomColumnSlug.Word(type),
            AppliesTo = storedScope ?? CustomColumnSlug.Word(scope),
            DropdownOptions = options,
            DisplayOrder = context.CustomColumns.Count(),
            CreatedAt = DateTime.UtcNow,
        };

        context.CustomColumns.Add(row);
        context.SaveChanges();
        return row;
    }

    private List<string> Labels()
    {
        using var context = Open();
        return context.CustomColumnValues
            .Where(row => row.RigLabel != null)
            .Select(row => row.RigLabel!)
            .ToList()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private HashSet<string> Names(string type)
    {
        using var connection = new SqliteConnection(_db.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name NOT LIKE 'sqlite_%'";
        command.Parameters.AddWithValue("$type", type);

        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>The <c>from</c> column of every foreign key SQLite itself reports for a table.
    /// </summary>
    private HashSet<string> ForeignKeyColumns(string table)
    {
        using var connection = new SqliteConnection(_db.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list('{table}')";

        var columns = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(reader.GetOrdinal("from")));
        }

        return columns;
    }

    private T Scalar<T>(string sql)
    {
        using var connection = new SqliteConnection(_db.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection(_db.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Written(CustomWriteResult result)
    {
        Assert.Equal(CustomWriteStatus.Written, result.Status);
        Assert.Null(result.Message);
    }

    /// <summary>The key-shape refusal: no sentence, and nothing written.</summary>
    private void Refuses(Guid columnId, CustomValueKey key, string value)
    {
        var before = Scalar<long>("SELECT count(*) FROM custom_column_values");
        var result = _repository.SetValue(columnId, key, value);

        Assert.Equal(CustomWriteStatus.KeyDoesNotMatchScope, result.Status);
        Assert.Null(result.Message);
        Assert.False(result.Ok);
        Assert.Equal(before, Scalar<long>("SELECT count(*) FROM custom_column_values"));
    }
}
