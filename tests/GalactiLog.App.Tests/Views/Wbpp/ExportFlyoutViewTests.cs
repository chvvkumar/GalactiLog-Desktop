using GalactiLog.App.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.App.Views.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Wbpp;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Wbpp;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views.Wbpp;

/// <summary>
/// Spec 12.4's Export flyout on the Target detail page's identity line (Phase 16 Task 5b): the
/// button, its two entries in order, their commands, the enablement that follows the checked set
/// and the sentence they carry while nothing is checked.
/// </summary>
public class ExportFlyoutViewTests
{
    private static Window Show(TargetDetailView view)
    {
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>The flyout's declared items, read after <c>ShowAt</c>. A MenuFlyout's items live in
    /// a popup root rather than in the view's own visual tree, so a descendant walk finds
    /// none.</summary>
    private static IReadOnlyList<MenuItem> ExportEntries(TargetDetailView view, string buttonName = "ExportButton")
    {
        var button = view.Named<Button>(buttonName);
        Assert.NotNull(button.Flyout);
        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        return [.. flyout.Items.OfType<MenuItem>()];
    }

    [AvaloniaFact]
    public void AllThreeEntries_AppendTheCheckedNightCount_ToTheirOwnLabel()
    {
        // Spec 12.4, the web's actions menu: "Copy frame list (2)" once nights are checked and the
        // bare label at zero. The count is nights and never frames. A failure is a literal header,
        // which is a label that says the same thing whatever the ledger's selection column holds.
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        Show(view);

        harness.ViewModel.Sessions[0].IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            ["Copy frame list (1)", "Export for stacking (1)", "AstroBin CSV (1)"],
            ExportEntries(view).Select(entry => entry.Header));

        harness.ViewModel.Sessions[1].IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            ["Copy frame list (2)", "Export for stacking (2)", "AstroBin CSV (2)"],
            ExportEntries(view).Select(entry => entry.Header));

        harness.ViewModel.ToggleAllNightsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            ["Copy frame list", "Export for stacking", "AstroBin CSV"],
            ExportEntries(view).Select(entry => entry.Header));
    }

    [AvaloniaFact]
    public void BothEntries_FollowTheCheckedSet_AndCarryTheSentenceWhileNothingIsChecked()
    {
        // A failure here is the two entries sharing one enablement computed once at load, so the
        // flyout opens correctly the first time and never updates after a check.
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        Show(view);

        // IsEffectivelyEnabled, not IsEnabled: Avalonia folds a command's CanExecute into
        // IsEnabledCore and leaves the locally set IsEnabled alone (TargetDetailViewTests' own
        // note at the templated-bindings case).
        foreach (var entry in ExportEntries(view))
        {
            Assert.False(entry.IsEffectivelyEnabled, $"{entry.Name} must be disabled with nothing checked");
            Assert.Equal("Select one or more nights first", ToolTip.GetTip(entry));
        }

        harness.ViewModel.Sessions[0].IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        foreach (var entry in ExportEntries(view))
        {
            Assert.True(entry.IsEffectivelyEnabled, $"{entry.Name} must be enabled once a night is checked");
            Assert.Null(ToolTip.GetTip(entry));
        }
    }

    [AvaloniaFact]
    public void TheExportButton_OpensItsFlyoutFromTheKeyboard_AndCloses()
    {
        // Task 5b review P3. W14 replaced one Tab stop that ran its command on Enter, so the
        // keyboard path has to survive the move: the button takes focus, Enter on it opens the
        // flyout with both entries in it, and the flyout closes again.
        //
        // What this case deliberately does not assert is where focus lands afterwards. Measured in
        // this harness: an Escape raised at the window does not reach the flyout at all, and after
        // Hide() the focused element is the identity line's help glyph rather than the button.
        // Both are Avalonia's own popup focus handling and neither is reachable from this page's
        // markup, so pinning either would pin the framework (return line for the coordinator).
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        var window = Show(view);

        var button = view.Named<Button>("ExportButton");
        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        Assert.True(button.Focus());
        Dispatcher.UIThread.RunJobs();
        Assert.Same(button, TopLevel.GetTopLevel(button)?.FocusManager?.GetFocusedElement());

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(flyout.IsOpen);
        Assert.Equal(
            ["CopyFrameListMenuItem", "ExportForStackingMenuItem", "AstroBinCsvMenuItem"],
            flyout.Items.OfType<MenuItem>().Select(entry => entry.Name));

        flyout.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.False(flyout.IsOpen);
    }

    [AvaloniaFact]
    public void TheOverflowFlyout_HoldsItsFourStandingItems_AndNoSendSubmenus()
    {
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        Show(view);

        var overflow = view.Named<Button>("OverflowButton");
        var flyout = Assert.IsType<MenuFlyout>(overflow.Flyout);
        flyout.ShowAt(overflow);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            ["RenameButton", "MergeButton", "ReResolveButton", "CreateMosaicMenuItem"],
            flyout.Items.OfType<MenuItem>().Select(item => item.Name));
        // The two sends moved to the night heading, where the night's own pointing is what they carry.
        Assert.Empty(flyout.Items.OfType<Separator>());
    }

    [AvaloniaFact]
    public void AQualityPanelViewModel_InAContentSlot_RendersItsView_NotATypeName()
    {
        // The export page holds its panel in an object-typed slot, so what turns the view-model
        // into a view is App.axaml's DataTemplate and nothing else. Asserted on a plain
        // ContentControl rather than on the export window, because the template is the
        // application's and applies to every slot alike; a missing one renders the type name.
        using var panel = new QualityPanelViewModel(
            [],
            WbppQualityByRig.DefaultRigKey,
            () => new GeneralSettings(),
            mutate => mutate(new GeneralSettings()));

        var slot = new ContentControl { Content = panel };
        var window = new Window { Width = 800, Height = 600, Content = slot };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(slot.GetVisualDescendants().OfType<QualityPanelView>());
        Assert.DoesNotContain(
            slot.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == typeof(QualityPanelViewModel).FullName);
    }
}
