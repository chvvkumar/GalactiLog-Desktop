using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Data.Queries;
using Xunit;
using static GalactiLog.App.Tests.TestSupport.TargetPartHost;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

/// <summary>P25 R1: the ledger's three row gestures (plain, Ctrl, Shift) and the box, headless,
/// against <c>SelectedSession</c> and <c>SelectedNights</c>.</summary>
public class NightsLedgerGestureTests
{
    private static readonly DateOnly Middle = new(2025, 6, 15);

    private static Factory.Harness ThreeNights()
        => Factory.Create(get: _ => Factory.PopulatedDetail(
            sessions: [Factory.Session(Factory.LastSession), Factory.Session(Middle), Factory.Session(Factory.FirstSession)])).Settle();

    private static (NightsLedgerPart View, Window Window) Shown(TargetDetailViewModel page)
    {
        var view = new NightsLedgerPart { DataContext = page };
        var window = Show(view);
        return (view, window);
    }

    // The press lands on the row's date cell, which is the row itself and not the box.
    private static void PressRow(NightsLedgerPart view, Window window, int index, RawInputModifiers modifiers = RawInputModifiers.None)
        => Click(window, CellAt(LedgerRowAt(view, index), 1), modifiers);

    [AvaloniaFact]
    public void CtrlPress_TogglesTheRowsCheck_AndLeavesTheLitRow()
    {
        using var harness = Factory.Create().Settle();
        var (view, window) = Shown(harness.ViewModel);
        var lit = harness.ViewModel.Sessions[0];

        PressRow(view, window, 1, RawInputModifiers.Control);
        Assert.True(harness.ViewModel.Sessions[1].IsChecked);
        Assert.Same(lit, harness.ViewModel.SelectedSession);
        Assert.Equal([Factory.FirstSession], harness.ViewModel.SelectedNights);

        PressRow(view, window, 1, RawInputModifiers.Control);
        Assert.False(harness.ViewModel.Sessions[1].IsChecked);
        Assert.Same(lit, harness.ViewModel.SelectedSession);
    }

    [AvaloniaFact]
    public void CtrlPress_OnTheLitRow_TogglesItsOwnCheck()
    {
        using var harness = Factory.Create().Settle();
        var (view, window) = Shown(harness.ViewModel);
        var lit = harness.ViewModel.Sessions[0];

        PressRow(view, window, 0, RawInputModifiers.Control);

        Assert.True(lit.IsChecked);
        Assert.Same(lit, harness.ViewModel.SelectedSession);
    }

    [AvaloniaFact]
    public void ShiftPress_ChecksTheRangeFromTheLitRow_AndLeavesIt()
    {
        using var harness = ThreeNights();
        var (view, window) = Shown(harness.ViewModel);
        var lit = harness.ViewModel.Sessions[0];

        PressRow(view, window, 2, RawInputModifiers.Shift);

        Assert.All(harness.ViewModel.Sessions, card => Assert.True(card.IsChecked));
        Assert.Same(lit, harness.ViewModel.SelectedSession);
        Assert.Equal(3, harness.ViewModel.SelectedNights.Count);
    }

    [AvaloniaFact]
    public void ShiftPress_LeavesRowsOutsideTheRangeAlone()
    {
        // The lit row is the middle one; a Shift press on the newest row checks two, and the
        // oldest row keeps the check it already had.
        using var harness = ThreeNights();
        var (view, window) = Shown(harness.ViewModel);
        harness.ViewModel.SelectedSession = harness.ViewModel.Sessions[1];
        harness.ViewModel.Sessions[2].IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        PressRow(view, window, 0, RawInputModifiers.Shift);

        Assert.All(harness.ViewModel.Sessions, card => Assert.True(card.IsChecked));
        Assert.Same(harness.ViewModel.Sessions[1], harness.ViewModel.SelectedSession);
    }

    [AvaloniaFact]
    public void ShiftPress_WithNoLitRow_ChecksThePressedRowAlone()
    {
        using var harness = ThreeNights();
        var (view, window) = Shown(harness.ViewModel);
        harness.ViewModel.SelectedSession = null;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(harness.ViewModel.SelectedSession);

        PressRow(view, window, 1, RawInputModifiers.Shift);

        Assert.Equal([Middle], harness.ViewModel.SelectedNights);
        Assert.Null(harness.ViewModel.SelectedSession);
    }

    [AvaloniaFact]
    public void PlainPress_MovesTheLitRow_AndClearsTheChecks()
    {
        using var harness = Factory.Create().Settle();
        var (view, window) = Shown(harness.ViewModel);
        PressRow(view, window, 1, RawInputModifiers.Control);
        Assert.True(harness.ViewModel.Sessions[1].IsChecked);

        PressRow(view, window, 1);

        Assert.Same(harness.ViewModel.Sessions[1], harness.ViewModel.SelectedSession);
        Assert.All(harness.ViewModel.Sessions, card => Assert.False(card.IsChecked));
        Assert.Empty(harness.ViewModel.SelectedNights);
    }

    [AvaloniaFact]
    public void APressOnTheBox_Toggles_AndLeavesTheLitRow()
    {
        using var harness = Factory.Create().Settle();
        var (view, window) = Shown(harness.ViewModel);
        var lit = harness.ViewModel.Sessions[0];
        var box = LedgerRowAt(view, 1).Children.OfType<CheckBox>().Single();

        Click(window, box);

        Assert.True(box.IsChecked);
        Assert.True(harness.ViewModel.Sessions[1].IsChecked);
        Assert.Same(lit, harness.ViewModel.SelectedSession);
    }
}
