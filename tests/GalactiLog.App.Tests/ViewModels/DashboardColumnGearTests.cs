using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 14B Task 6, ruling D4: "one ColumnPickerViewModel instance shown in two places, the
// dashboard gear flyout and the Display tab; both write display.columns.dashboard through
// DisplayColumnWriter." task6.md 7.1 found four of D4's clauses already true before this task
// touched anything (AppHost.cs 1515 to 1520 already hands ColumnPickerViewModel.ForDashboard the
// live TargetListViewModel.Columns and ToggleColumnCommand); these cases pin them at the
// view-model layer, the same types AppHost wires, so a later phase cannot quietly break them.
public class DashboardColumnGearTests
{
    private sealed class FakeDisplayStore
    {
        public DisplaySettings Current { get; private set; } = new();
        public int Saves { get; private set; }
        public DisplaySettings Get() => Current;
        public void Save(DisplaySettings value)
        {
            Current = value;
            Saves++;
        }
    }

    private static (TargetListViewModel List, ColumnPickerViewModel Picker, FakeDisplayStore Store) Create()
    {
        var store = new FakeDisplayStore();
        var writer = new DisplayColumnWriter(store.Get, store.Save, NullLogger.Instance);
        var list = new TargetListViewModel(store.Get(), writer, 50);

        // The exact production wiring, AppHost.cs 1515 to 1520: the gear flyout binds straight to
        // TargetListViewModel.Columns and ToggleColumnCommand, and so does the Display tab's
        // picker, over the one live list.
        var picker = ColumnPickerViewModel.ForDashboard(list.Columns, column => list.ToggleColumnCommand.Execute(column));

        return (list, picker, store);
    }

    [Fact]
    public void TheGear_AndTheDisplayTab_ShareOneColumnList()
    {
        var (list, picker, _) = Create();

        Assert.Same(list.Columns, picker.Columns);
    }

    [Fact]
    public void TogglingInTheGear_IsReflectedOnTheDisplayTab_WithNoReload()
    {
        var (list, picker, _) = Create();
        var equipment = list.Columns.Single(column => column.Key == "equipment");
        Assert.True(equipment.IsVisible);

        // The gear flyout's checkbox raises ToggleColumnCommand directly.
        list.ToggleColumnCommand.Execute(equipment);

        // Same object, no rebuild: the picker's own row already reads false.
        Assert.False(picker.Columns.Single(column => column.Key == "equipment").IsVisible);
        Assert.Same(equipment, picker.Columns.Single(column => column.Key == "equipment"));
    }

    [Fact]
    public void TogglingOnTheDisplayTab_IsReflectedInTheGear()
    {
        var (list, picker, _) = Create();
        var equipment = picker.Columns.Single(column => column.Key == "equipment");
        Assert.True(equipment.IsVisible);

        // The Display tab's own checkbox raises the picker's ToggleCommand, which calls back
        // into TargetListViewModel.ToggleColumn through the delegate ForDashboard was given.
        picker.ToggleCommand.Execute(equipment);

        Assert.False(list.Columns.Single(column => column.Key == "equipment").IsVisible);
        Assert.DoesNotContain(list.VisibleColumns, column => column.Key == "equipment");
    }

    [Fact]
    public async Task BothSurfaces_WriteDashboardColumnsThroughTheWriter()
    {
        var (list, picker, store) = Create();

        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "equipment"));
        await list.PendingPersist;
        Assert.DoesNotContain("equipment", store.Current.Columns[DisplaySettings.DashboardTableId]);

        picker.ToggleCommand.Execute(picker.Columns.Single(column => column.Key == "designation"));
        await list.PendingPersist;
        Assert.DoesNotContain("designation", store.Current.Columns[DisplaySettings.DashboardTableId]);
    }

    [Fact]
    public void TheNameColumn_CannotBeHiddenInEitherSurface()
    {
        var (list, picker, _) = Create();
        var nameFromGear = list.Columns.Single(column => column.Key == "name");
        var nameFromPicker = picker.Columns.Single(column => column.Key == "name");

        list.ToggleColumnCommand.Execute(nameFromGear);
        Assert.True(nameFromGear.IsVisible);

        picker.ToggleCommand.Execute(nameFromPicker);
        Assert.True(nameFromPicker.IsVisible);
    }

    [Fact]
    public void TheNameColumn_CanBeTurnedBackOnInEitherSurface()
    {
        // Ruling Q5: name cannot be turned off, but a name that arrives already off (an
        // externally written display.columns list) can be turned back on, in either surface.
        var (list, _, _) = Create();
        var nameFromGear = list.Columns.Single(column => column.Key == "name");
        nameFromGear.IsVisible = false;
        list.ToggleColumnCommand.Execute(nameFromGear);
        Assert.True(nameFromGear.IsVisible);

        var (_, picker2, _) = Create();
        var nameFromPicker = picker2.Columns.Single(column => column.Key == "name");
        nameFromPicker.IsVisible = false;
        picker2.ToggleCommand.Execute(nameFromPicker);
        Assert.True(nameFromPicker.IsVisible);
    }

    [Fact]
    public void TheGear_DoesNotRaiseChanged_AndCostsNoRoundTrip()
    {
        // TargetListViewModel.Changed drives the dashboard's debounced re-query; hiding a column
        // is a rendering change and must not cost one (task6.md 7.3).
        var (list, _, _) = Create();
        var changed = 0;
        list.Changed += (_, _) => changed++;

        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "equipment"));

        Assert.Equal(0, changed);
    }

    [Fact]
    public void TheGear_DoesNotReorderColumns()
    {
        // Column order is always DisplaySettings.DefaultColumns[DashboardTableId]'s order; the
        // stored list (and a toggle) contributes visibility only (task6.md 7.3).
        var (list, _, _) = Create();
        var before = list.Columns.Select(column => column.Key).ToArray();

        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "equipment"));
        list.ToggleColumnCommand.Execute(list.Columns.Single(column => column.Key == "equipment"));

        Assert.Equal(before, list.Columns.Select(column => column.Key));
    }
}
