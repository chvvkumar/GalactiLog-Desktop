using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Views.Setup;
using Xunit;
using Xunit.Abstractions;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke tests for spec 12.1's setup wizard: the window parses, lays out
// and binds against a populated view-model, each of the five steps renders through its own
// DataTemplate, and the two dismissals the web's onClose={() => {}} forbids are refused.
public class SetupWizardWindowTests(ITestOutputHelper output)
{
    private static SetupWizardWindow Show(SetupWizardViewModelTestFactory.Harness harness)
    {
        var window = new SetupWizardWindow { DataContext = harness.ViewModel, Width = 760, Height = 620 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    // The window refuses every close but the view-model's own, so a test ends the wizard the way a
    // user does. Awaited, because the completion path writes through the store on a background
    // thread before it raises CloseRequested.
    private static async Task Close(SetupWizardViewModelTestFactory.Harness harness, Window window)
    {
        await harness.ViewModel.SkipSetupCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
    }

    private static IReadOnlyList<string> VisibleTexts(Control view)
        => [.. view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    [AvaloniaFact]
    public async Task SetupWizardWindow_Constructs_AndRendersTheFirstStep()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var window = Show(harness);

        Assert.True(window.Bounds.Width > 0);
        Assert.True(window.Bounds.Height > 0);
        Assert.Contains("Step 1 of 5: Scan folders", VisibleTexts(window));
        Assert.Contains("No folders chosen yet.", VisibleTexts(window));

        await Close(harness, window);
    }

    [AvaloniaFact]
    public async Task SetupWizardWindow_RendersEveryStep()
    {
        using var harness = SetupWizardViewModelTestFactory.Create(
            timezones: () => ["Etc/UTC", "Europe/London"],
            localTimezoneId: () => "Europe/London");
        var window = Show(harness);
        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);

        for (var step = 0; step < harness.ViewModel.Steps.Count; step++)
        {
            Dispatcher.UIThread.RunJobs();

            var host = window.Named<ContentControl>("StepHost");
            Assert.Same(harness.ViewModel.CurrentStep, host.Content);
            Assert.Contains(
                $"Step {step + 1} of 5: {harness.ViewModel.CurrentStep.Title}",
                VisibleTexts(window));

            // The step's own template realized, not an empty presenter.
            Assert.NotEmpty(host.GetVisualDescendants().OfType<TextBlock>());

            if (step == harness.ViewModel.Steps.Count - 1)
            {
                break;
            }

            // The blank-longitude nudge consumes one press; loop until the step actually moves.
            var before = harness.ViewModel.StepIndex;
            var guard = 0;
            while (harness.ViewModel.StepIndex == before && guard++ < 4)
            {
                await harness.ViewModel.NextCommand.ExecuteAsync(null);
            }
        }

        await harness.ViewModel.FinishCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
    }

    // Spec 12.1 step 2's data location (Phase 10 Task 9). The step is reached by advancing from
    // step 1; nothing here touches disk.
    [AvaloniaFact]
    public async Task SetupWizardWindow_StorageStep_RendersTheDataLocationBoxAndItsBrowseButton()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var window = Show(harness);
        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);
        await harness.ViewModel.NextCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Step 2 of 5: Storage locations", VisibleTexts(window));

        var host = window.Named<ContentControl>("StepHost");
        var box = host.GetVisualDescendants().OfType<TextBox>().First(control => control.Name == "DataRootBox");
        var browse = host.GetVisualDescendants().OfType<Button>().First(control => control.Name == "BrowseDataRootButton");

        Assert.Equal(SetupWizardViewModelTestFactory.DataRoot, box.Text);
        Assert.True(browse.IsEffectivelyVisible);
        Assert.Contains(VisibleTexts(window), text => text.Contains("uninstalling GalactiLog does not remove it"));

        await Close(harness, window);
    }

    [AvaloniaFact]
    public async Task Escape_DoesNotCloseTheWizard()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var window = Show(harness);

        var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
        window.RaiseEvent(escape);
        Dispatcher.UIThread.RunJobs();

        // Handled at the window, which is what stops a modal Window's default Escape dismissal.
        Assert.True(escape.Handled);
        Assert.True(window.IsVisible);
        Assert.False(harness.ViewModel.IsFinished);
        Assert.False(harness.Stored.SetupComplete);

        // Only Escape is swallowed; the window is not deaf. F7 rather than Tab, which the
        // platform's own focus navigation handles whatever this window does.
        var other = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F7 };
        window.RaiseEvent(other);
        Assert.False(other.Handled);

        await Close(harness, window);
    }

    [AvaloniaFact]
    public async Task ACloseRequest_IsSuppressed_UntilTheViewModelAsks()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var window = Show(harness);

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible);
        Assert.False(harness.Stored.SetupComplete);

        await Close(harness, window);

        Assert.False(window.IsVisible);
        Assert.True(harness.Stored.SetupComplete);
    }

    // Review Important finding 3: only the user's own dismissal is refused. An OS shutdown, an
    // application shutdown and an owner-window close go through, because a wizard that vetoes
    // those keeps the process alive and Windows reports the application as blocking the session
    // from ending.
    [AvaloniaTheory]
    [InlineData(WindowCloseReason.OSShutdown)]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    [InlineData(WindowCloseReason.OwnerWindowClosing)]
    public async Task AShutdownClose_IsNotVetoed(WindowCloseReason reason)
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var window = Show(harness);

        // The user's own dismissal is still refused, which is the rule the wizard exists to hold.
        Assert.True(RaiseClosing(window, WindowCloseReason.WindowClosing).Cancel);
        Assert.False(RaiseClosing(window, reason).Cancel);
        Assert.True(window.IsVisible);

        await Close(harness, window);
    }

    // Moved to TestSupport/WindowClosingProbe at FIXER LIST F20: three dialogs declare a close
    // policy now, and one probe serves all three suites.
    private static WindowClosingEventArgs RaiseClosing(Window window, WindowCloseReason reason)
        => WindowClosingProbe.RaiseClosing(window, reason);

    [AvaloniaFact]
    public async Task TheFooter_ShowsNextUntilTheLastStep_AndFinishOnIt()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var window = Show(harness);

        var next = window.Named<Button>("NextButton");
        var finish = window.Named<Button>("FinishButton");
        var skip = window.Named<Button>("SkipSetupButton");
        var back = window.Named<Button>("BackButton");

        Assert.True(next.IsEffectivelyVisible);
        Assert.False(finish.IsEffectivelyVisible);
        Assert.True(skip.IsEffectivelyVisible);

        // The roadmap's first named assertion, reaching the button: no folder, no Next. Back is
        // disabled on the first step.
        Assert.False(next.IsEffectivelyEnabled);
        Assert.False(back.IsEffectivelyEnabled);

        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);
        Dispatcher.UIThread.RunJobs();
        Assert.True(next.IsEffectivelyEnabled);

        await Close(harness, window);
    }

    // ModalHost's no-owner path (HANDOFF.md section 7): a wizard with no window to open in has
    // decided nothing, and the page is never built, so there is nothing to dispose either.
    [AvaloniaFact]
    public async Task SetupWizardService_WithNoOwnerWindow_ReturnsFalse_AndBuildsNoPage()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var created = 0;
        var service = new SetupWizardService(
            () =>
            {
                created++;
                return harness.ViewModel;
            },
            new ModalHost(() => null));

        Assert.False(await service.ShowAsync());
        Assert.Equal(0, created);
    }

    // The whole path: the service builds the window on the one host, over a real owner, and the
    // page is disposed through the host's cleanup once the wizard closes itself.
    [AvaloniaFact]
    public async Task SetupWizardService_OverAnOwner_ShowsTheWizard_AndDisposesThePageOnClose()
    {
        var owner = new Window { Width = 1280, Height = 900 };
        owner.Show();
        Dispatcher.UIThread.RunJobs();

        using var harness = SetupWizardViewModelTestFactory.Create();
        var service = new SetupWizardService(() => harness.ViewModel, new ModalHost(() => owner));

        var shown = service.ShowAsync();
        Dispatcher.UIThread.RunJobs();

        await harness.ViewModel.SkipSetupCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(await shown);
        Assert.True(harness.Stored.SetupComplete);
        Assert.True(harness.FirstScan.IsDisposed);

        owner.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ---- Phase 14B Task 9: the wizard's move into the Observing Ledger vocabulary -----------
    // DESIGN.md sections 4 (control vocabulary), 5 (spacing and geometry), 6 (patterns) and 8
    // (the refusal list). The two mechanical cases are source scans, in the shape
    // NightStripTests.NightStripSource_ContainsNoColourLiteral already uses, because a
    // CornerRadius or a Style inherited from a shared style is correct and a visual-tree walk
    // cannot tell it from a local one.

    private static string ReadSource()
        => File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "Views", "Setup", "SetupWizardWindow.axaml"));

    [AvaloniaFact]
    public void Window_DeclaresNoCornerRadius()
    {
        var source = ReadSource();
        Assert.DoesNotContain("CornerRadius", source, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Window_DeclaresNoStyleOfItsOwn()
    {
        // The window declares no <Style> block of its own: every idiom it uses (rule, callout,
        // the four type tiers, sm, mono, num) is a class the shared Theme/Controls.axaml already
        // carries. ControlStyleScanTest's own needle walk covers this file too; this is the pin
        // specific to Task 9's move.
        var source = ReadSource();
        Assert.DoesNotContain("<Style", source, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Window_TheFolderRows_AreRuleSeparated_NotCards()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var window = Show(harness);
        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);
        Dispatcher.UIThread.RunJobs();

        var host = window.Named<ContentControl>("StepHost");
        var list = host.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name == "FolderList");

        // The ItemsControl's own template root is a Border too (its Background and BorderBrush
        // are unset, so it carries neither class), which is why this looks for the row's Border
        // by its class rather than asserting there is exactly one Border under the whole list.
        var row = Assert.Single(
            list.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("rule"));

        // Alignment and a 1 pixel rule carry containment (DESIGN.md section 8): the folder row is
        // Border.rule, never a filled, bordered card.
        //
        // Phase 14B fixer, fixer list item 57 (task9-review P3). This used to assert only that the
        // row does not carry the callout class, so a regression that re-added Background and
        // BorderThickness to the same element passed. What "not a card" means is the refusal
        // itself: no fill, and a rule on one edge rather than a box.
        Assert.DoesNotContain("callout", row.Classes);
        Assert.Null(row.Background);
        Assert.Equal(0d, row.CornerRadius.TopLeft);
        Assert.True(
            row.BorderThickness.Left == 0
                && row.BorderThickness.Right == 0
                && (row.BorderThickness.Top == 0) != (row.BorderThickness.Bottom == 0),
            $"The folder row draws a box ({row.BorderThickness}) rather than a single rule.");

        await Close(harness, window);
    }

    // Fix pass, review finding P2-1: TextBlock.num sets TextAlignment="Right" (DESIGN.md's data-ink
    // class for a table's numeric cells), so FreeSpaceText took Classes="t-label" alone rather than
    // "t-label num", or the free-space sentence rendered flush right in a vertical StackPanel while
    // every sibling text block in the step stays flush left.
    [AvaloniaFact]
    public async Task Window_TheFreeSpaceReadout_StaysLeftAligned_NotRightAlignedLikeATableCell()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var window = Show(harness);
        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);
        Dispatcher.UIThread.RunJobs();
        await harness.ViewModel.NextCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // FreeSpaceText sits inside the step 2 DataTemplate, not the window's own compiled
        // NameScope, so it is reached by name over the step host's visual descendants rather than
        // through window.GetControl, the same way SetupWizardWindow_StorageStep_... reaches
        // DataRootBox and BrowseDataRootButton.
        var host = window.Named<ContentControl>("StepHost");
        var freeSpace = host.GetVisualDescendants().OfType<TextBlock>().First(control => control.Name == "FreeSpaceText");
        Assert.NotEqual(TextAlignment.Right, freeSpace.TextAlignment);

        await Close(harness, window);
    }

    [AvaloniaFact]
    public async Task Window_TheStepErrorPanel_IsTheSharedCallout()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var window = Show(harness);

        var panel = window.Named<Border>("StepErrorPanel");
        Assert.Contains("callout", panel.Classes);
        Assert.Contains("warn", panel.Classes);

        await Close(harness, window);
    }

    [AvaloniaFact]
    public async Task Window_TheStepHeading_UsesTheTypeTiers()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var window = Show(harness);

        var heading = window.Named<TextBlock>("StepHeaderText");
        Assert.Contains("t-label", heading.Classes);
        Assert.Contains("section", heading.Classes);

        await Close(harness, window);
    }

    [AvaloniaFact]
    public async Task Window_StillPaintsThePageBackground()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var window = Show(harness);

        // BrushPageBackground is a LinearGradientBrush in every shipped theme (Theme/Themes/*),
        // not a solid colour, and a DynamicResource lookup resolves to the exact same instance
        // the theme dictionary holds, so reference equality is the correct, brush-kind-agnostic
        // check.
        Assert.True(Avalonia.Application.Current!.TryFindResource("BrushPageBackground", out var resource));
        var expected = Assert.IsAssignableFrom<IBrush>(resource);
        Assert.Same(expected, window.Background);

        await Close(harness, window);
    }

    [AvaloniaFact]
    public async Task Window_StillCarriesItsBoundHelpGlyph()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var window = Show(harness);

        var glyph = Assert.Single(window.GetVisualDescendants().OfType<HelpButton>());
        Assert.Equal(harness.ViewModel.HelpTopicId, glyph.Topic);

        await Close(harness, window);
    }

    [AvaloniaFact]
    public async Task Window_EveryTextBlock_RendersAtAReadableSize()
    {
        using var harness = SetupWizardViewModelTestFactory.Create(
            timezones: () => ["Etc/UTC", "Europe/London"],
            localTimezoneId: () => "Europe/London");
        var window = Show(harness);
        harness.Folders.AddFolder(SetupWizardViewModelTestFactory.Folder);

        for (var step = 0; step < harness.ViewModel.Steps.Count; step++)
        {
            Dispatcher.UIThread.RunJobs();

            var blocks = window.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.NotEmpty(blocks);
            Assert.All(blocks, block => Assert.True(
                block.FontSize >= 8,
                $"TextBlock '{block.Text}' renders at {block.FontSize}."));

            if (step == harness.ViewModel.Steps.Count - 1)
            {
                break;
            }

            var before = harness.ViewModel.StepIndex;
            var guard = 0;
            while (harness.ViewModel.StepIndex == before && guard++ < 4)
            {
                await harness.ViewModel.NextCommand.ExecuteAsync(null);
            }
        }

        await harness.ViewModel.FinishCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
    }

    // Section 5.1 item 5: the footer is a Grid ColumnDefinitions="Auto,*,Auto,Auto" whose three
    // Auto columns must fit inside the window's own MinWidth of 560, less the page's 20 pixel side
    // gutters. Phase 14B fixer, fixer list item 56: the four buttons no longer carry Classes="sm"
    // either, so this measurement is at the default button metrics and is the case that would
    // catch the footer outgrowing the window because of it.
    [AvaloniaFact]
    public async Task Window_TheFooterStillFitsTheMinimumWidth()
    {
        using var harness = SetupWizardViewModelTestFactory.Create();
        var window = new SetupWizardWindow
        {
            DataContext = harness.ViewModel,
            Width = 560,
            Height = 480,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var skip = window.Named<Button>("SkipSetupButton");
        var footer = Assert.IsType<Grid>(skip.Parent);
        footer.Measure(Size.Infinity);

        var contentWidth = window.MinWidth - 40; // Grid.Margin="20" on the page root, both sides
        output.WriteLine(
            $"Footer desired width at MinWidth {window.MinWidth}: {footer.DesiredSize.Width}, "
            + $"content area: {contentWidth}.");

        Assert.True(
            footer.DesiredSize.Width <= contentWidth,
            $"Footer desired width {footer.DesiredSize.Width} exceeds the content area "
            + $"{contentWidth} at the window's MinWidth of {window.MinWidth}.");

        await Close(harness, window);
    }
}
