using System.Threading;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Settings;

// Phase 20 Task 6c: ColumnPickerViewModel.ForLedger, spec 12.4 and 12.15's "Nights ledger
// columns", plus the "Built-in"/"Custom" Groups split every picker shares (spec 12.15, ruling C4).
// No database: a picker is pure delegates and in-memory records over CustomColumnDefinition,
// exactly like the frame table's own ForFrames.
public class LedgerColumnPickerTests
{
    private static int _nextId;

    private static CustomColumnDefinition Def(
        string name, CustomColumnScope scope, int order = 0)
    {
        var slug = CustomColumnSlug.Base(name) + "_" + Interlocked.Increment(ref _nextId);
        return new CustomColumnDefinition(
            Guid.NewGuid(), name, slug, CustomColumnType.Text, scope, [], order, DateTime.UtcNow, 0);
    }

    private static (Func<DisplaySettings> Get, Action<DisplaySettings> Save, DisplayColumnWriter Writer)
        NewWriter(DisplaySettings? seed = null)
    {
        var current = seed ?? new DisplaySettings();
        DisplaySettings Get() => current;
        void Save(DisplaySettings value) => current = value;
        return (Get, Save, new DisplayColumnWriter(Get, Save));
    }

    // ---- the third factory ---------------------------------------------------------------

    [Fact]
    public async Task ForLedger_WritesTheLedgerTableId()
    {
        // Red against a copy of ForFrames that kept "frames": the toggle would silently rewrite
        // the frame table's own list instead of the ledger's.
        var (get, _, writer) = NewWriter();
        var session = Def("Notes", CustomColumnScope.Session);
        var picker = ColumnPickerViewModel.ForLedger(get(), writer, [session]);

        picker.ToggleCommand.Execute(picker.Columns[0]);
        await writer.Pending;

        Assert.NotNull(writer.LastWritten(DisplaySettings.LedgerTableId));
        Assert.Null(writer.LastWritten(DisplaySettings.FramesTableId));
    }

    [Fact]
    public void ForLedger_HoldsOnlySessionScopeColumns()
    {
        var (get, _, writer) = NewWriter();
        var target = Def("Priority", CustomColumnScope.Target);
        var session = Def("Notes", CustomColumnScope.Session);
        var rig = Def("Rig note", CustomColumnScope.Rig);

        var picker = ColumnPickerViewModel.ForLedger(get(), writer, [target, session, rig]);

        var slug = Assert.Single(picker.Columns).Key;
        Assert.Equal(session.Slug, slug);
    }

    [Fact]
    public void ForLedger_IsEmptyOnAFreshProfile()
    {
        // User choice 3: a custom column ships off on the ledger. Not the picker's own row count
        // (a column still gets a row so it can be switched on) but its visibility.
        var (get, _, writer) = NewWriter();
        var session = Def("Notes", CustomColumnScope.Session);

        var picker = ColumnPickerViewModel.ForLedger(get(), writer, [session]);

        var column = Assert.Single(picker.Columns);
        Assert.False(column.IsVisible);
    }

    [Fact]
    public async Task ForLedger_SeedsFromLastWrittenBeforeTheDisplayDocument()
    {
        // Red against a seed from the stored snapshot alone: a second live picker built after the
        // first one's click would show the hidden state the document still carries, then its own
        // first toggle would revert the first click.
        var (get, _, writer) = NewWriter();
        var session = Def("Notes", CustomColumnScope.Session);

        writer.Write(DisplaySettings.LedgerTableId, [session.Slug]);

        // The stored document is untouched (Write only queues), so a picker seeded from it alone
        // would show the column off.
        var picker = ColumnPickerViewModel.ForLedger(get(), writer, [session]);

        Assert.True(Assert.Single(picker.Columns).IsVisible);
        await writer.Pending;
    }

    [Fact]
    public async Task ForLedger_AdoptsAnotherSurfacesToggleThroughChanged()
    {
        var (get, _, writer) = NewWriter();
        var session = Def("Notes", CustomColumnScope.Session);

        var first = ColumnPickerViewModel.ForLedger(get(), writer, [session]);
        var second = ColumnPickerViewModel.ForLedger(get(), writer, [session]);

        first.ToggleCommand.Execute(first.Columns[0]);
        await writer.Pending;

        Assert.True(second.Columns[0].IsVisible);

        second.Dispose();

        first.ToggleCommand.Execute(first.Columns[0]);
        await writer.Pending;

        // second stopped following after Dispose, so it still shows the state from before the
        // second toggle.
        Assert.True(second.Columns[0].IsVisible);
    }

    [Fact]
    public async Task ASlugWhoseColumnWasDeleted_IsNotInTheNextWrittenList()
    {
        // Red against a picker that carried the stored list forward verbatim (a second definition
        // list of its own), which would keep resurrecting a dead slug on every later write.
        var (get, _, writer) = NewWriter();
        var kept = Def("Notes", CustomColumnScope.Session, order: 0);
        var deleted = Def("Old", CustomColumnScope.Session, order: 1);

        writer.Write(DisplaySettings.LedgerTableId, [kept.Slug, deleted.Slug]);
        await writer.Pending;

        // The column catalogue no longer carries "deleted": the picker is rebuilt from the live
        // definitions alone, the way ApplyDisplayDocument rebuilds it on this tab's own reload.
        var picker = ColumnPickerViewModel.ForLedger(get(), writer, [kept]);

        Assert.Single(picker.Columns);

        picker.ToggleCommand.Execute(picker.Columns[0]);
        await writer.Pending;

        var written = writer.LastWritten(DisplaySettings.LedgerTableId)!;
        Assert.DoesNotContain(deleted.Slug, written);
    }

    // ---- Groups, shared by every factory ---------------------------------------------------

    [Fact]
    public void TheGroups_PutBuiltInFirstAndCustomSecond()
    {
        // ForDashboard is the one factory a test can hand a raw, mixed column list to: the split
        // is generic (CustomColumnSlug.IsCustom(column.Key)), not a Nights-ledger-only rule.
        var custom = new ColumnViewModel("custom_priority", "Priority", isVisible: true);
        var builtIn = new ColumnViewModel("name", "Name", isVisible: true, canHide: false);
        var picker = ColumnPickerViewModel.ForDashboard([builtIn, custom], _ => { });

        Assert.Equal(2, picker.Groups.Count);
        Assert.Equal("Built-in", picker.Groups[0].Heading);
        Assert.Same(builtIn, Assert.Single(picker.Groups[0].Columns));
        Assert.Equal("Custom", picker.Groups[1].Heading);
        Assert.Same(custom, Assert.Single(picker.Groups[1].Columns));
        Assert.True(picker.ShowGroupHeadings);
    }

    [Fact]
    public void TheCustomGroup_IsAbsentWithNoCustomColumn()
    {
        // Red against a picker that always shows two headings, which would change every existing
        // picker's screen on a library with no custom column at all.
        var builtIn = new ColumnViewModel("name", "Name", isVisible: true, canHide: false);
        var picker = ColumnPickerViewModel.ForDashboard([builtIn], _ => { });

        var group = Assert.Single(picker.Groups);
        Assert.Equal("Built-in", group.Heading);
        Assert.False(picker.ShowGroupHeadings);
    }

    [Fact]
    public void TheGroups_HoldTheSameColumnInstancesAsColumns()
    {
        // Red against a projection that copies: a toggle raised through a group's row would then
        // leave Columns' own instance untouched, and vice versa.
        var custom = new ColumnViewModel("custom_priority", "Priority", isVisible: true);
        var builtIn = new ColumnViewModel("name", "Name", isVisible: true, canHide: false);
        var picker = ColumnPickerViewModel.ForDashboard([builtIn, custom], _ => { });

        Assert.Same(picker.Columns[0], picker.Groups[0].Columns[0]);
        Assert.Same(picker.Columns[1], picker.Groups[1].Columns[0]);
    }

    [Fact]
    public void TheEmptyPicker_SaysNoCustomColumnsYet()
    {
        var (get, _, writer) = NewWriter();
        var picker = ColumnPickerViewModel.ForLedger(get(), writer, []);

        Assert.True(picker.IsEmpty);
        Assert.Empty(picker.Groups);
        Assert.Equal("No custom columns yet.", ColumnPickerViewModel.EmptyMessage);
    }
}
