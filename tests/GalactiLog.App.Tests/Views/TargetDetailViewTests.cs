using GalactiLog.App.Tests.TestSupport;
using static GalactiLog.App.Tests.TestSupport.TargetPartHost;
using Avalonia;
using GalactiLog.App.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using Xunit.Abstractions;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke tests for the Target detail page: it parses, lays out, and binds
// against a populated view-model, and a row whose field is absent takes itself out of the layout.
// Compiled bindings already turn a binding-path typo into a build error; these catch the rest (a
// missing resource, a template that cannot realize, a ratio key bound to FontSize).
public class TargetDetailViewTests
{
    /// <summary>Shows the page with the Details drawer open, which is where the header block, the
    /// target notes and the merge history live since Phase 12. A closed <c>SplitView</c> pane is
    /// not laid out, so anything asserting on the drawer's content opens it first.</summary>
    private static Window ShowWithDetails(TargetDetailView view)
    {
        var window = Show(view);
        ((TargetDetailViewModel)view.DataContext!).IsDetailsOpen = true;
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void TargetDetailView_Constructs_AndRendersAPopulatedViewModel()
    {
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        ShowWithDetails(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);

        var texts = VisibleTexts(view);
        Assert.Contains("M 31", texts);
        Assert.Contains("Galaxy", texts);
        Assert.Contains("And", texts);
        Assert.Contains("00:42:44.3", texts);
        Assert.Contains("+41:16:09", texts);
        Assert.Contains("189.1' x 61.7'", texts);
        Assert.Contains("145 deg", texts);
        Assert.Contains("Naked eye object", texts);
        Assert.Contains("NGC 224", texts);
        Assert.Contains("Messier", texts);

        // The log line, as its runs, and the ledger's target row beneath it. "12.4 h" and "148"
        // used to come from the uppercase stat grid, which Phase 12 deleted.
        Assert.Contains("12.4 h", texts);
        Assert.Contains("148", texts);
        Assert.Contains("All 2 nights", texts);
        Assert.Contains(texts, text => text.Contains("23 frames without a plate scale"));
        Assert.Contains(texts, text => text.Contains("measured by header"));

        // The notes box is a real two-way editor over the autosave field, now inside the drawer.
        var notes = view.Named<TextBox>("NotesTextBox");
        Assert.Equal("an existing note", notes.Text);

        // The trend chart part still hosts the cross-session chart; the ledger replaced the session
        // accordion, one row per night, newest first.
        Assert.Same(
            harness.ViewModel.TargetChart,
            view.Named<ContentControl>("TargetChartRegion").Content);
        Assert.Same(
            harness.ViewModel.Sessions,
            view.Named<ListBox>("NightsLedger").ItemsSource);
        Assert.Equal(2, harness.ViewModel.Sessions.Count);
        Assert.Contains("2025-12-07", texts);
    }

    [AvaloniaFact]
    public void TargetDetailView_EveryTextBlock_RendersAtAReadableSize()
    {
        // Scales.axaml's FontSize* keys are ratios (0.500 to 0.786), not point sizes: binding one
        // to FontSize renders text at well under a pixel. Nothing in this view sets FontSize, and
        // this is what fails if someone binds one of those keys.
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        ShowWithDetails(view);

        var blocks = view.GetVisualDescendants().OfType<TextBlock>().ToList();
        Assert.NotEmpty(blocks);
        Assert.All(blocks, block => Assert.True(
            block.FontSize >= 8,
            $"TextBlock '{block.Text}' renders at {block.FontSize}."));
    }

    [AvaloniaFact]
    public void TargetDetailView_TemplatedCommandBindings_Resolve()
    {
        // The page's own actions. Compiled bindings check the path but not that the control
        // actually got a command object, which is the difference between a wired button and a
        // silently dead one.
        using var harness = Factory.Create().Settle();
        harness.ViewModel.BeginRenameCommand.Execute(null);
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        Show(view);

        // The loop is split by P12 R10: two of the page's actions are still buttons, and the
        // three rare ones moved into the overflow flyout. Nothing else about the assertion
        // changed.
        //
        // P14A Task 4 (spec 12.4): Copy frame list is disabled while no night is checked, so this
        // case checks one before asserting that every button's command will run.
        harness.ViewModel.Sessions[0].IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        foreach (var name in new[]
                 {
                     "BackButton", "DetailsButton",
                     "RevealFolderButton",
                 })
        {
            var button = view.Named<Button>(name);
            Assert.NotNull(button.Command);

            // IsEffectivelyEnabled, not IsEnabled: Avalonia folds a command's CanExecute into
            // IsEnabledCore and leaves the locally set IsEnabled alone, so IsEnabled would be
            // true for a button whose command refuses to run.
            Assert.True(button.IsEffectivelyEnabled, $"{name} must be enabled for a resolved target");
        }

        foreach (var name in new[] { "RenameButton", "MergeButton", "ReResolveButton" })
        {
            var item = MenuItemFromOverflow(view, name);
            Assert.NotNull(item.Command);
            Assert.True(item.IsEffectivelyEnabled, $"{name} must be enabled for a resolved target");
        }

        // Phase 16 (spec 12.4): Copy frame list is an entry of the Export flyout now rather than a
        // button on the row. The same rule, on the control that carries the command today.
        Assert.True(view.Named<Button>("ExportButton").IsEffectivelyEnabled);
        foreach (var name in new[] { "CopyFrameListMenuItem", "ExportForStackingMenuItem" })
        {
            var item = MenuItemFromExport(view, name);
            Assert.NotNull(item.Command);
            Assert.True(item.IsEffectivelyEnabled, $"{name} must be enabled for a resolved target");
        }

        // The inline rename editor and its two commands appear only in edit mode.
        var editor = view.Named<StackPanel>("RenameEditor");
        Assert.True(editor.IsVisible);
        Assert.All(
            editor.GetVisualDescendants().OfType<Button>(),
            button => Assert.NotNull(button.Command));

        // And the displayed name yields to the editor rather than sitting beside it.
        Assert.False(view.Named<TextBlock>("PrimaryName").IsVisible);
    }

    [AvaloniaFact]
    public void TargetDetailView_MissingTarget_RendersTheCallout()
    {
        using var harness = Factory.Create(get: _ => null).Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        Show(view);

        // Ruling Q4: the callout names the group key, the Back button is still there, and the
        // header block and totals row are gone rather than blank.
        Assert.True(view.Named<ContentControl>("MissingCallout").IsVisible);
        Assert.Contains(VisibleTexts(view), text => text.Contains(Factory.ResolvedGroupKey));
        Assert.True(view.Named<Button>("BackButton").IsVisible);
        Assert.False(view.Named<Border>("HeaderBlock").IsVisible);
        Assert.False(view.Named<Border>("TotalsRow").IsVisible);
    }

    [AvaloniaFact]
    public void TargetDetailView_HidesRowsWhoseFieldIsAbsent()
    {
        var sparse = Factory.PopulatedHeader() with
        {
            Aliases = [],
            SacDescription = null,
            SacNotes = null,
            PositionAngle = null,
            CatalogMemberships = [],
        };

        using var populated = Factory.Create().Settle();
        var populatedView = new TargetDetailView { DataContext = populated.ViewModel };
        ShowWithDetails(populatedView);
        var populatedTexts = VisibleTexts(populatedView);
        Assert.Contains("SAC description", populatedTexts);
        Assert.Contains("Aliases", populatedTexts);
        Assert.Contains("Catalogs", populatedTexts);
        Assert.Contains("Position angle", populatedTexts);

        using var harness = Factory.Create(get: _ => Factory.PopulatedDetail(header: sparse)).Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        ShowWithDetails(view);
        var texts = VisibleTexts(view);

        // A target with no SAC text shows no empty SAC row, and the rows that do have values are
        // untouched.
        Assert.DoesNotContain("SAC description", texts);
        Assert.DoesNotContain("SAC notes", texts);
        Assert.DoesNotContain("Aliases", texts);
        Assert.DoesNotContain("Catalogs", texts);
        Assert.DoesNotContain("Position angle", texts);
        Assert.Contains("Constellation", texts);
        Assert.Contains("00:42:44.3", texts);
    }

    // FIXER LIST item 13. The affordance was hidden in Phase 6 because ruling Q12's re-resolve
    // could only report the stage a resolved target already had; Phase 7 Task 7 built the
    // re-enrichment writer and flipped the one constant that decides it. This is the assertion
    // that flipped with it.
    [AvaloniaFact]
    public void TargetDetailView_RendersTheReResolveButton()
    {
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        Show(view);

        Assert.True(TargetDetailViewModel.ReResolveAvailable);
        var item = MenuItemFromOverflow(view, "ReResolveButton");
        Assert.True(item.IsVisible);
        Assert.NotNull(item.Command);
        Assert.True(item.IsEffectivelyEnabled);

        // The other three page actions are unaffected, two as buttons and one as a menu item.
        Assert.True(MenuItemFromOverflow(view, "RenameButton").IsVisible);
        Assert.True(view.Named<Button>("ExportButton").IsVisible);
        Assert.True(view.Named<Button>("RevealFolderButton").IsVisible);
    }

    [AvaloniaFact]
    public void TargetDetailView_UnresolvedGroup_DisablesTheTargetOnlyActions()
    {
        // withMergeHistory, so the empty region below comes from the obj: group-key rule under
        // test rather than from a factory that was never asked for a history at all.
        using var harness = Factory.Create(
            get: _ => Factory.PopulatedDetail(header: Factory.UnresolvedHeader()),
            groupKey: Factory.UnresolvedGroupKey,
            withMergeHistory: true).Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        ShowWithDetails(view);

        // No targets row, so rename, re-resolve and the notes box are off; the two actions that
        // only need a frame path stay on. The notes box keeps its name and its IsEnabled binding
        // and is a plain container now: the drawer is the disclosure.
        Assert.False(MenuItemFromOverflow(view, "RenameButton").IsEffectivelyEnabled);
        Assert.False(MenuItemFromOverflow(view, "ReResolveButton").IsEffectivelyEnabled);
        Assert.False(view.Named<StackPanel>("NotesBox").IsEnabled);
        Assert.True(view.Named<Button>("RevealFolderButton").IsEffectivelyEnabled);

        // P14A Task 4 (spec 12.4): Copy frame list is disabled while nothing is checked, whatever
        // the header resolved to, and enabled by a check on any night. Phase 16 moved the entry
        // point into the Export flyout, so the same rule is asserted on the entry that carries
        // the command; the Export button itself is never disabled, because a flyout with two
        // disabled entries is what says why.
        Assert.True(view.Named<Button>("ExportButton").IsEffectivelyEnabled);
        Assert.False(MenuItemFromExport(view, "CopyFrameListMenuItem").IsEffectivelyEnabled);
        harness.ViewModel.Sessions[0].IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(MenuItemFromExport(view, "CopyFrameListMenuItem").IsEffectivelyEnabled);

        // Phase 7 Task 5: no targets row, so no merge and no history region either.
        Assert.False(MenuItemFromOverflow(view, "MergeButton").IsEffectivelyEnabled);
        Assert.Null(view.Named<ContentControl>("MergeHistoryRegion").Content);
    }

    // ---- Phase 7 Task 5 ----------------------------------------------------------------

    [AvaloniaFact]
    public void TargetDetailView_RendersTheMergeHistorySection()
    {
        using var harness = Factory.Create(withMergeHistory: true).Settle();
        harness.History = [Factory.HistoryRow()];
        harness.ViewModel.MergeHistory!.Reload();
        harness.SettleHistory();

        var view = new TargetDetailView { DataContext = harness.ViewModel };
        ShowWithDetails(view);

        // The region resolves App.axaml's template for MergeHistoryViewModel, the same one the
        // Settings Targets tab uses.
        Assert.Same(harness.ViewModel.MergeHistory, view.Named<ContentControl>("MergeHistoryRegion").Content);

        var texts = VisibleTexts(view);
        Assert.Contains("Merge history", texts);
        Assert.Contains("NGC 224", texts);
        Assert.Contains("148 frames", texts);
        Assert.Contains("2025-03-04 05:06", texts);
    }

    [AvaloniaFact]
    public void TargetDetailView_StillLaysOutNonZero()
    {
        // Design-spec 18.3's smoke shape: the page still parses, lays out and binds.
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        Show(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
    }

    [AvaloniaFact]
    public void TargetDetailView_OnAFreshProfile_OpensInQuestionModes()
    {
        // A failure looks like a fresh profile opening any layout other than Question Modes.
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        Show(view);

        Assert.IsType<GalactiLog.App.Views.TargetDetail.Layouts.ModesLayoutView>(
            view.Named<ContentControl>("LayoutHost").Content);
    }

    // ---- Phase 12: the ledger, the drawer and the overflow flyout ----------------------

    [AvaloniaFact]
    public void TargetDetailView_DetailsDrawer_OpensAndClosesByCommand()
    {
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        Show(view);

        var drawer = view.Named<SplitView>("DetailsDrawer");
        Assert.False(drawer.IsPaneOpen);

        harness.ViewModel.ToggleDetailsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(drawer.IsPaneOpen);

        harness.ViewModel.ToggleDetailsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(drawer.IsPaneOpen);
    }

    [AvaloniaFact]
    public void TargetDetailView_DetailsDrawer_HoldsTheLitNightsSessionNotes_UnderTheTargetNotes()
    {
        // The session notes moved from the lanes to the drawer (night pane round): red if the
        // drawer has no Session notes header with its help glyph, the box is above the target
        // notes, or typing into it does not reach the lit night's own autosave field.
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        ShowWithDetails(view);
        var pane = view.Named<StackPanel>("DetailsPaneContent");
        var night = harness.ViewModel.ReviewSession!;

        var section = view.Named<StackPanel>("SessionNotesSection");
        Assert.True(section.IsEffectivelyVisible, "the Session notes section is hidden with a night lit");
        Assert.Contains(section.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Session notes");
        Assert.Contains(section.GetVisualDescendants().OfType<HelpButton>(), glyph => glyph.Topic == "target.session-notes");
        var target = view.Named<TextBox>("NotesTextBox");
        var box = view.Named<TextBox>("SessionNotesTextBox");
        Assert.True(box.TranslatePoint(default, pane)!.Value.Y > target.TranslatePoint(default, pane)!.Value.Y, "the session notes are above the target notes");

        box.Text = "typed into the drawer";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("typed into the drawer", night.Notes!.Text);
    }

    [AvaloniaFact]
    public void TargetDetailView_DetailsDrawer_TheCloseCrossShutsIt_AndTheDetailsChevronFlips()
    {
        // Red if the pane has no close control at its top, the cross leaves the pane open, or the
        // Details button's chevron points the same way open and shut.
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        Show(view);
        var drawer = view.Named<SplitView>("DetailsDrawer");
        var shut = view.Named<Avalonia.Controls.Shapes.Path>("DetailsClosedChevron");
        var open = view.Named<Avalonia.Controls.Shapes.Path>("DetailsOpenChevron");
        Assert.True(shut.IsVisible && !open.IsVisible, "the chevron does not point at the shut drawer");

        harness.ViewModel.IsDetailsOpen = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(drawer.IsPaneOpen);
        Assert.True(open.IsVisible && !shut.IsVisible, "the chevron did not flip for the open drawer");
        var cross = view.Named<Button>("CloseDetailsButton");
        var pane = view.Named<StackPanel>("DetailsPaneContent");
        Assert.True(cross.IsEffectivelyVisible, "the close cross is hidden");
        Assert.True(cross.TranslatePoint(default, pane)!.Value.Y < 30d, "the close cross is not at the top of the pane");
        Assert.IsType<Avalonia.Controls.Shapes.Path>(cross.Content);

        cross.Command!.Execute(cross.CommandParameter);
        Dispatcher.UIThread.RunJobs();
        Assert.False(harness.ViewModel.IsDetailsOpen);
        Assert.False(drawer.IsPaneOpen);
        Assert.True(shut.IsVisible && !open.IsVisible, "the chevron did not flip back");
    }

    [AvaloniaFact]
    public void TargetDetailView_DetailsDrawer_OpensOnCtrlD()
    {
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        var window = Show(view);

        // Through the real input stack rather than a synthesised routed event: a KeyBinding fires
        // on the bubble from the focused element, so the page has to hold focus first.
        view.Focusable = true;
        view.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.ViewModel.IsDetailsOpen);
        Assert.True(view.Named<SplitView>("DetailsDrawer").IsPaneOpen);
    }

    [AvaloniaFact]
    public void TargetDetailView_Escape_ClosesTheDrawerBeforeItGoesBack()
    {
        // Ruling Q11's ordering, which a KeyBinding cannot express: Escape closes the drawer when
        // the drawer is open, and goes back otherwise.
        using var harness = Factory.Create().Settle();
        var backs = 0;
        harness.ViewModel.BackRequested += (_, _) => backs++;
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        Show(view);
        harness.ViewModel.IsDetailsOpen = true;
        Dispatcher.UIThread.RunJobs();

        Escape(view);
        Assert.False(harness.ViewModel.IsDetailsOpen);
        Assert.Equal(0, backs);

        Escape(view);
        Assert.Equal(1, backs);
    }

    private static void Escape(TargetDetailView view)
    {
        view.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Source = view,
            Key = Key.Escape,
        });
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TargetDetailView_KeyBindings_NameCommandsThatExist()
    {
        // Ruling Q11's two modified shortcuts. Escape lives in OnKeyDown and Alt+Left is
        // MainWindow's, beside the mouse back button.
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        Show(view);

        Assert.Equal(2, view.KeyBindings.Count);
        Assert.All(view.KeyBindings, binding => Assert.NotNull(binding.Command));
    }

    [AvaloniaFact]
    public void TargetDetailView_TheTwoFrequentActions_AreButtons()
    {
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        Show(view);

        // P14A Task 4 (spec 12.4): the entry is disabled while no night is checked, so the
        // enablement is asserted after a check. Phase 16: the two frequent actions are Export and
        // Reveal folder; polish wave 9 moved Export into the Nights ledger. Export carries a flyout
        // rather than a command of its own, so what is commanded is each of its two entries.
        harness.ViewModel.Sessions[0].IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        foreach (var name in new[] { "ExportButton", "RevealFolderButton" })
        {
            var button = view.Named<Button>(name);
            Assert.True(button.IsVisible);
            Assert.True(button.IsEffectivelyEnabled);
        }

        Assert.NotNull(view.Named<Button>("RevealFolderButton").Command);
        foreach (var name in new[] { "CopyFrameListMenuItem", "ExportForStackingMenuItem" })
        {
            var item = MenuItemFromExport(view, name);
            Assert.True(item.IsEffectivelyEnabled);
            Assert.NotNull(item.Command);
        }
    }

    [AvaloniaFact]
    public void TargetDetailView_DetailsDrawer_CarriesTheArcsecondHfrMean()
    {
        // Coordinator ruling 1, re-ruled. The ledger carries HFR in pixels and has no tenth
        // column, so spec 12.4's arcsecond mean lives in the drawer beside the plate-scale facts
        // it derives from, with its excluded-frame disclosure under it.
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        ShowWithDetails(view);

        var value = view.Named<TextBlock>("MeanHfrArcsecValue");
        Assert.True(value.IsEffectivelyVisible);
        Assert.Equal("1.85 arcsec", value.Text);

        var texts = VisibleTexts(view);
        Assert.Contains("Mean HFR (arcsec)", texts);
        Assert.Contains("1.85 arcsec", texts);
        Assert.Contains("23 frames without a plate scale", texts);
    }

    [AvaloniaFact]
    public void TargetDetailView_Escape_InATextBox_BelongsToTheBox()
    {
        // Review P3-8, coordinator ruling 6. The page's Escape handler must not navigate off the
        // page while the notes box or the rename editor has the key: the editor's own cancel wins.
        using var harness = Factory.Create().Settle();
        var backs = 0;
        harness.ViewModel.BackRequested += (_, _) => backs++;
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        ShowWithDetails(view);

        var notes = view.Named<TextBox>("NotesTextBox");
        notes.Focus();
        Dispatcher.UIThread.RunJobs();

        notes.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Source = notes,
            Key = Key.Escape,
        });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, backs);
        Assert.True(harness.ViewModel.IsDetailsOpen);
    }

    // ---- P14A Task 6: spec 12.4's object type edit (PAR-009) ---------------------------------

    [AvaloniaFact]
    public void TargetDetailView_TheObjectTypeRow_CarriesAPencil()
    {
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        ShowWithDetails(view);

        var pencil = view.Named<Button>("ObjectTypeEditButton");
        Assert.True(pencil.IsEffectivelyVisible);
        Assert.True(pencil.Command?.CanExecute(null));
        Assert.False(view.Named<ComboBox>("ObjectTypeEditor").IsEffectivelyVisible);
        Assert.True(view.Named<TextBlock>("ObjectTypeValue").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void TargetDetailView_AnUnresolvedGroup_ShowsNoPencil()
    {
        // Spec 12.4: an obj: group has no targets row to write, so the pencil is absent.
        using var harness = Factory.Create(
            get: _ => Factory.PopulatedDetail(header: Factory.UnresolvedHeader()),
            groupKey: Factory.UnresolvedGroupKey).Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        ShowWithDetails(view);

        Assert.False(view.Named<Button>("ObjectTypeEditButton").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void TargetDetailView_BeginEdit_SwapsInTheComboBox()
    {
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        ShowWithDetails(view);

        view.Named<Button>("ObjectTypeEditButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var editor = view.Named<ComboBox>("ObjectTypeEditor");
        Assert.True(editor.IsEffectivelyVisible);
        Assert.False(view.Named<TextBlock>("ObjectTypeValue").IsEffectivelyVisible);

        // The pencil goes with the value it was beside (Task 6 review P3): it offered to open an
        // editor that is already open.
        Assert.False(view.Named<Button>("ObjectTypeEditButton").IsEffectivelyVisible);

        // The box carries spec 9.8's ten entries, opens unseeded, and shows the target's own
        // category as its placeholder, so choosing that entry is a selection and closes the box.
        Assert.Equal(10, editor.ItemCount);
        Assert.Null(editor.SelectedItem);
        Assert.Equal("Galaxy", editor.PlaceholderText);
    }

    [AvaloniaFact]
    public void TargetDetailView_Escape_CancelsTheEdit()
    {
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        var window = ShowWithDetails(view);

        view.Named<Button>("ObjectTypeEditButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var editor = view.Named<ComboBox>("ObjectTypeEditor");
        editor.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(harness.ViewModel.Header!.IsEditingObjectType);
        Assert.False(editor.IsEffectivelyVisible);
        Assert.Empty(harness.ObjectTypeWrites);

        // The key belongs to the box, so the page's own Escape did not also close the drawer or
        // navigate back.
        Assert.True(harness.ViewModel.IsDetailsOpen);
    }

    [AvaloniaFact]
    public void TargetDetailView_Escape_WithFocusOffTheBox_StillCancelsTheEdit()
    {
        // Review P2-1. The state a user is actually in after clicking the pencil: focus on the
        // pencil, not in the box. Escape then bubbles past the box to the page's own handler,
        // whose text-editor guard covers a TextBox and not a ComboBox, and the drawer closed with
        // the editor still open. Focus is put back on the pencil deliberately, because the fix's
        // first half now focuses the box on open and would otherwise hide the second half.
        //
        // The focus is moved onto the page itself rather than back onto the pencil: since the Task
        // 6 review P3 fix the pencil leaves the screen while its own editor is open, so it is not
        // a place the focus can be. What the case needs is only that the focus is not in the box,
        // which is the state a pencil click leaves behind.
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel, Focusable = true };
        var window = ShowWithDetails(view);

        var pencil = view.Named<Button>("ObjectTypeEditButton");
        pencil.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var editor = view.Named<ComboBox>("ObjectTypeEditor");
        Assert.True(editor.IsEffectivelyVisible);
        Assert.False(pencil.IsEffectivelyVisible);

        view.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.False(editor.IsFocused);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(harness.ViewModel.Header!.IsEditingObjectType);
        Assert.False(editor.IsEffectivelyVisible);
        Assert.Empty(harness.ObjectTypeWrites);

        // The edit is what Escape spent itself on, so the drawer is still open and the page did
        // not go Back.
        Assert.True(harness.ViewModel.IsDetailsOpen);
    }

    [AvaloniaFact]
    public void TargetDetailView_BeginEdit_PutsTheKeyboardOnTheBox()
    {
        // Review P2-1, first half: the box takes focus when the pencil opens it, so Escape reaches
        // the box's own handler rather than bubbling to the page.
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        ShowWithDetails(view);

        view.Named<Button>("ObjectTypeEditButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.Named<ComboBox>("ObjectTypeEditor").IsFocused);
    }

    [AvaloniaFact]
    public void TargetDetailView_ThePencil_IsADrawnPath()
    {
        // DESIGN.md, Refused: no Unicode glyph standing in for an icon. The pencil is a stroked
        // Path in the page's own one stroke weight, like every chevron on this page.
        using var harness = Factory.Create().Settle();
        var view = new TargetDetailView { DataContext = harness.ViewModel };
        ShowWithDetails(view);

        var pencil = view.Named<Button>("ObjectTypeEditButton");
        Assert.Empty(pencil.GetVisualDescendants().OfType<TextBlock>());

        var path = Assert.Single(pencil.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());
        Assert.NotNull(path.Data);
        Assert.NotNull(path.Stroke);
        Assert.Null(path.Fill);
        Assert.Equal(1.6d, path.StrokeThickness, 3);
    }
}
