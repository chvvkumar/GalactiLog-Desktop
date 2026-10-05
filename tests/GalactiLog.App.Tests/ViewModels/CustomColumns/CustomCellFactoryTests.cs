using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.CustomColumnTestFactory;

namespace GalactiLog.App.Tests.ViewModels.CustomColumns;

// Phase review target P2-3. Four surfaces built a cell and only two reconciled one, so the same
// value in the same kind of cell survived a refresh on the Target page and lost the last keystrokes
// on the dashboard. The decision is here now, for all four.
public class CustomCellFactoryTests
{
    private static readonly Guid Target = Guid.Parse("10000000-0000-0000-0000-000000000000");
    private static readonly CustomValueKey Key = CustomValueKey.ForTarget(Target);

    private static CustomCellGroup Reconcile(
        CustomCellGroup existing,
        IReadOnlyList<CustomColumnDefinition> columns,
        CustomValueKey? key = null,
        Func<CustomColumnDefinition, string?>? stored = null,
        Func<Guid, CustomValueKey, string?, CustomWriteResult>? write = null)
        => CustomCellFactory.Reconcile(
            existing,
            columns,
            key ?? Key,
            "NGC 7000",
            stored ?? (_ => null),
            write ?? ((_, _, _) => Factory.Written),
            Factory.Instant,
            Factory.Inline);

    [Fact]
    public void TheSameColumnsOnTheSameKey_KeepTheCellsAndAdoptTheStoredValues()
    {
        // The reuse rule, which is what makes typing survive a republish on every surface. A failure
        // is a replacement group: the cells are then new instances, so whatever was in the editor is
        // gone and the caret with it.
        var done = Factory.Boolean("Done");
        var first = Reconcile(CustomCellGroup.Empty, [done]);
        var cell = Assert.Single(first.Cells);
        Assert.False(cell.IsChecked);

        var second = Reconcile(first, [done], stored: _ => CustomColumnSlug.True);

        Assert.Same(first, second);
        Assert.Same(cell, Assert.Single(second.Cells));
        Assert.True(cell.IsChecked);
    }

    [Fact]
    public void ARenamedColumn_RebuildsAndDisposesTheOldGroup()
    {
        // A rename has to reach the label and an option edit has to reach the dropdown, so a
        // definition that is not the same definition is a rebuild rather than a reseed.
        var log = new Factory.WriteLog();
        var before = Factory.Define("Done", CustomColumnType.Boolean, CustomColumnScope.Target, [], order: 0);
        var after = before with { Name = "Finished" };
        var first = Reconcile(CustomCellGroup.Empty, [before], write: log.Write);

        var second = Reconcile(first, [after], write: log.Write);

        Assert.NotSame(first, second);
        Assert.Equal("Finished", Assert.Single(second.Cells).Label);

        // The old group is disposed by the reconcile itself, so no caller owes that. A disposed
        // cell writes nothing, which is what a row left holding both groups would otherwise do.
        first.Cells[0].IsChecked = true;
        Assert.Empty(log.Calls);
    }

    [Fact]
    public void AnotherKey_RebuildsRatherThanReseeding()
    {
        // A cell's key is fixed for its life, so reseeding one slot's values into cells keyed on
        // another would send the next keystroke to the wrong row. A failure here is a group that
        // compares columns alone, which is what a per-night or per-rig surface would hit first.
        var done = Factory.Boolean("Done");
        var first = Reconcile(CustomCellGroup.Empty, [done]);

        var second = Reconcile(first, [done], key: CustomValueKey.ForTarget(Guid.NewGuid()));

        Assert.NotSame(first, second);
    }

    [Fact]
    public void NoKey_OrNoWriteDelegate_DrawsNoCell()
    {
        // The guards every surface used to write out for itself: an unresolved obj: group has no
        // target id, and a page built with no write delegate has nothing to write through. A cell
        // that silently writes nothing is worse than a cell that is not drawn.
        var done = Factory.Boolean("Done");

        // Both call the factory directly: the local helper defaults a null key to a real one, which
        // is exactly the guard under test.
        Assert.Empty(CustomCellFactory.Reconcile(
            CustomCellGroup.Empty, [done], key: null, "NGC 7000", _ => null, (_, _, _) => Factory.Written).Cells);
        Assert.Empty(CustomCellFactory.Reconcile(
            CustomCellGroup.Empty, [done], Key, "NGC 7000", _ => null, write: null).Cells);
    }

    [Fact]
    public void NoColumns_AnswersTheSharedEmptyGroup()
    {
        // So no caller writes a null check or builds an empty list of its own, and the every-day
        // library with no custom column allocates nothing per row.
        Assert.Same(CustomCellGroup.Empty, Reconcile(CustomCellGroup.Empty, []));
    }
}
