using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Views.Settings;
using GalactiLog.Core.Scanning;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.LibraryTabViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// Design-spec 18.3's view smoke tests for spec 12.7's Library tab: it parses, lays out, and binds
// against a populated view-model. Compiled bindings already turn a binding-path typo into a build
// error; these catch the rest (a missing resource, a template that cannot realize, an empty state
// that never renders).
public class LibraryTabViewTests
{
    private static Window Show(Control view)
    {
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static IReadOnlyList<string> VisibleTexts(Control view)
        => [.. view.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text ?? "")];

    private static IReadOnlyList<string> ButtonTexts(Control view)
        => [.. view.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.IsEffectivelyVisible)
            .Select(button => button.Content as string ?? "")];

    [AvaloniaFact]
    public async Task LibraryTabView_Constructs_AndLaysOut()
    {
        using var harness = await Factory
            .Create(general => general with
            {
                ScanRoots = [Factory.Root],
                ScanFilters = general.ScanFilters with
                {
                    IncludePaths = [Path.Combine(Factory.Root, "2025")],
                    ExcludePaths = [Path.Combine(Factory.Root, "rejected")],
                    NameRules = [Factory.Rule()],
                },
            })
            .SettleAsync();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);

        var texts = VisibleTexts(view);
        Assert.Contains("Library folders", texts);
        Assert.Contains("Include paths", texts);
        Assert.Contains("Exclude paths", texts);
        Assert.Contains("Name rules", texts);
        Assert.Contains("Test a path", texts);
        Assert.Contains("Scanning", texts);
        Assert.Contains("Manual scan", texts);
        Assert.Contains(Factory.Root, texts);
        Assert.Contains(Path.Combine(Factory.Root, "2025"), texts);
        Assert.Contains(Path.Combine(Factory.Root, "rejected"), texts);
    }

    [AvaloniaFact]
    public async Task LibraryTabView_EmptyLists_RenderTheirEmptyState()
    {
        using var harness = await Factory.Create().SettleAsync();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        Assert.True(view.GetControl<TextBlock>("NoScanRootsText").IsEffectivelyVisible);
        Assert.True(view.GetControl<TextBlock>("NoIncludePathsText").IsEffectivelyVisible);
        Assert.True(view.GetControl<TextBlock>("NoExcludePathsText").IsEffectivelyVisible);
        Assert.True(view.GetControl<TextBlock>("NoNameRulesText").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task LibraryTabView_AFailedRead_RendersOneLineInPlaceOfTheControls()
    {
        using var harness = await Factory
            .Create(loadThrows: new InvalidOperationException("no database"))
            .SettleAsync();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        // Spec 12.10: a failure is reported, never a silent empty page.
        Assert.True(view.GetControl<TextBlock>("LoadFailedText").IsEffectivelyVisible);

        // Review finding I1 and minor 9: "in place of" the controls, not above them. A live
        // editor over a document that failed to read shows four empty lists, and one edit would
        // have made Save write those empty lists over the stored ones.
        foreach (var section in new[]
                 {
                     "ScanRootsSection", "IncludePathsSection", "ExcludePathsSection",
                     "NameRulesSection", "TestPathSection", "ScanOptionsSection", "ManualScanSection",

                     // Phase 14B Task 5: spec 12.2's notice carries a Review button, so it is
                     // bound by the same rule, and ShowScanFilterNotice is false unless IsReady.
                     "ScanFilterNoticeBanner",
                 })
        {
            Assert.False(
                view.GetControl<Control>(section).IsEffectivelyVisible,
                $"{section} is still visible after a failed read.");
        }

        Assert.Empty(ButtonTexts(view));
        Assert.False(harness.ViewModel.IsReady);
    }

    [AvaloniaFact]
    public async Task LibraryTabView_AChangeMadeElsewhereWhileDirty_RendersTheNotice()
    {
        using var harness = await Factory
            .Create(general => general with { ScanRoots = [Factory.Root] }, followGeneralChanged: true)
            .SettleAsync();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        Assert.False(view.GetControl<ContentControl>("StoredValuesNoticeBanner").IsEffectivelyVisible);

        harness.ViewModel.AddScanRoot(Factory.SecondRoot);
        harness.SaveElsewhere(general => general with
        {
            ScanFilters = general.ScanFilters with { NameRules = [Factory.Rule("wizard-rule")] },
        });
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.GetControl<ContentControl>("StoredValuesNoticeBanner").IsEffectivelyVisible);

        // FIXER LIST F13: the banner names the reason, and this one really is another writer.
        Assert.Contains(
            VisibleTexts(view),
            text => text.Contains("changed elsewhere", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task LibraryTabView_AnOutOfRootIncludePath_RendersItsError()
    {
        using var harness = await Factory
            .Create(general => general with { ScanRoots = [Factory.Root] })
            .SettleAsync();
        harness.ViewModel.AddIncludePath(@"E:\Somewhere\Else");
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        var texts = VisibleTexts(view);
        Assert.Contains(@"E:\Somewhere\Else", texts);
        Assert.Contains(texts, text => text.Contains("must be inside one of the library folders"));
    }

    [AvaloniaFact]
    public async Task LibraryTabView_TheTestPathBox_RendersTheVerdictAndTheDecidingRule()
    {
        using var harness = await Factory
            .Create(general => general with
            {
                ScanRoots = [Factory.Root],
                ScanFilters = general.ScanFilters with { NameRules = [Factory.Rule()] },
            })
            .SettleAsync();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        harness.ViewModel.TestPath.Path = Path.Combine(Factory.Root, "2025", "M31_bad.fits");
        harness.ViewModel.TestPath.TestCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var texts = VisibleTexts(view);
        Assert.Contains("Skipped: matched an exclude rule", texts);
        Assert.Contains("A name rule with action=exclude matched.", texts);
        Assert.Contains("exclude glob on file: *_bad.fits", texts);

        // The saved-configuration warning is on screen, because that is what the box tests.
        Assert.Contains(texts, text => text.Contains("Unsaved changes are not used"));
    }

    [AvaloniaFact]
    public async Task LibraryTabView_RendersTheIntervalPresetsAndTheThreeCheckboxes()
    {
        using var harness = await Factory.Create().SettleAsync();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        var intervals = view.GetControl<ComboBox>("AutoScanIntervalBox");
        Assert.Equal(harness.ViewModel.IntervalOptions, intervals.ItemsSource);
        Assert.Same(harness.ViewModel.SelectedInterval, intervals.SelectedItem);

        Assert.True(view.GetControl<CheckBox>("IncludeCalibrationCheckBox").IsEffectivelyVisible);
        Assert.True(view.GetControl<CheckBox>("AutoScanEnabledCheckBox").IsEffectivelyVisible);
        Assert.True(view.GetControl<CheckBox>("WatcherEnabledCheckBox").IsEffectivelyVisible);

        Assert.Contains("Scan library", ButtonTexts(view));
        Assert.Contains("Stop", ButtonTexts(view));
        Assert.Contains("Add rule", ButtonTexts(view));
        Assert.Contains("Save rules", ButtonTexts(view));
        Assert.Contains("Revert", ButtonTexts(view));
    }

    // ---- Phase 14B Task 5 ---------------------------------------------------------------------

    private static string ViewSource() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "GalactiLog.App", "Views", "Settings", "LibraryTabView.axaml"));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GalactiLog.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    // The nearest ancestor carrying this x:Name, or a failed assertion naming what was looked for.
    private static Control Ancestor(Control control, string name)
    {
        var current = control.GetVisualParent();
        while (current is not null)
        {
            if (current is Control candidate && candidate.Name == name)
            {
                return candidate;
            }

            current = current.GetVisualParent();
        }

        Assert.Fail($"{control.Name} is not inside {name}.");
        return control;
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    [AvaloniaFact]
    public async Task View_TheScopePair_IsInTheManualScanSection()
    {
        using var harness = await Factory.Create().SettleAsync();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        var all = view.GetControl<RadioButton>("ScanScopeAllFramesRadio");
        var light = view.GetControl<RadioButton>("ScanScopeLightOnlyRadio");

        Assert.Equal("All frames", all.Content);
        Assert.Equal("Light frames only", light.Content);
        Assert.Equal(all.GroupName, light.GroupName);
        Assert.False(string.IsNullOrEmpty(all.GroupName));

        // Spec 12.7 puts the pair in the scan control, not in the Scanning section, whose
        // IncludeCalibrationCheckBox stays the stored key's editor and does not move.
        Assert.Equal("ManualScanSection", Ancestor(all, "ManualScanSection").Name);
        Assert.Equal("ManualScanSection", Ancestor(light, "ManualScanSection").Name);
        Assert.Equal(
            "ScanOptionsSection",
            Ancestor(view.GetControl<CheckBox>("IncludeCalibrationCheckBox"), "ScanOptionsSection").Name);
    }

    [AvaloniaFact]
    public async Task View_TheCheckBoxReadsTheRuledSentence()
    {
        using var harness = await Factory.Create().SettleAsync();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        var box = view.GetControl<CheckBox>("ForceOrphanCleanupCheckBox");

        // Ruling D2, verbatim.
        Assert.Equal("Remove catalogue rows for missing files past the safety limit", box.Content);
        Assert.Equal("ManualScanSection", Ancestor(box, "ManualScanSection").Name);
    }

    [AvaloniaFact]
    public async Task View_TheHelpParagraph_IsBothSentencesVerbatim()
    {
        using var harness = await Factory.Create().SettleAsync();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        var texts = VisibleTexts(view);
        Assert.Contains(
            "GalactiLog deletes database rows only. No file, folder or image on your disk is ever "
            + "moved, renamed or deleted.",
            texts);
        Assert.Contains(
            "Without this box, a scan that finds half or more of a root's catalogued files missing "
            + "skips that root's cleanup and records it in the Activity feed, on the assumption that "
            + "a drive or share is unreachable rather than that the frames are gone.",
            texts);

        // Body text beside the checkbox, not a HelpButton flyout: U1 adds no help topic, and
        // ruling C1 places glyphs beside headings rather than beside controls.
        Assert.True(view.GetControl<TextBlock>("ForceOrphanCleanupHelpText").IsEffectivelyVisible);
        Assert.True(view.GetControl<TextBlock>("ForceOrphanCleanupLimitText").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task View_TheNotice_SitsAboveTheRuleEditor()
    {
        using var harness = await Factory
            .Create(general => general with
            {
                ScanFilters = general.ScanFilters with { NameRules = ScanFilterConfig.SeededRules() },
            })
            .SettleAsync();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        var notice = view.GetControl<ContentControl>("ScanFilterNoticeBanner");
        Assert.True(notice.IsEffectivelyVisible);
        Assert.Contains("callout", notice.Classes);
        Assert.DoesNotContain("warn", notice.Classes);
        Assert.Equal("Review", view.GetControl<Button>("ScanFilterNoticeReviewButton").Content);

        var source = ViewSource();
        Assert.True(
            source.IndexOf("x:Name=\"ScanFilterNoticeBanner\"", StringComparison.Ordinal)
            < source.IndexOf("x:Name=\"NameRulesSection\"", StringComparison.Ordinal),
            "the notice must be declared above the rule editor");
    }

    // Section 8.3's hazard as this task's own case, so it fails in a filtered run rather than in
    // the full suite: the notice carries a Button and must be invisible on a failed read.
    [AvaloniaFact]
    public async Task View_AFailedRead_StillRendersNoVisibleButton()
    {
        using var harness = await Factory
            .Create(
                general => general with
                {
                    ScanFilters = general.ScanFilters with { NameRules = ScanFilterConfig.SeededRules() },
                },
                loadThrows: new InvalidOperationException("no database"))
            .SettleAsync();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        Assert.False(harness.ViewModel.IsReady);
        Assert.False(harness.ViewModel.ShowScanFilterNotice);
        Assert.False(view.GetControl<ContentControl>("ScanFilterNoticeBanner").IsEffectivelyVisible);
        Assert.Empty(ButtonTexts(view));
    }

    // The one mechanical measure of the Observing Ledger vocabulary move (roadmap section 0):
    // Views/TargetDetail/TargetDetailView.axaml already returns zero and so does this file now.
    [AvaloniaFact]
    public async Task View_DeclaresNoCornerRadius()
    {
        using var harness = await Factory.Create().SettleAsync();
        Assert.NotNull(harness.ViewModel);

        var source = ViewSource();
        Assert.DoesNotContain("CornerRadius", source, StringComparison.Ordinal);

        // And no section card: the five repeated card attributes went with it. DESIGN.md section 8
        // refuses card chrome and nested bordered containers.
        Assert.DoesNotContain("ColorBgSurface", source, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task View_SectionHeadingsUseTheTypeTiers()
    {
        using var harness = await Factory.Create().SettleAsync();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        foreach (var heading in new[]
                 {
                     "Library folders", "Include paths", "Exclude paths", "Name rules", "Test a path",
                     "Scanning", "Manual scan",
                 })
        {
            var block = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == heading);
            Assert.Contains("t-label", block.Classes);
            Assert.Contains("section", block.Classes);

            // And the heading row sits on the shared hairline rule, which is what carries
            // containment now that the card is gone.
            var rule = block.GetVisualAncestors().OfType<Border>()
                .FirstOrDefault(border => border.Classes.Contains("rule"));
            Assert.True(rule is not null, $"the '{heading}' heading is not on a Border.rule.");
        }

        // The hand-set weight and ink they replaced are gone from the file.
        Assert.DoesNotContain("FontWeight=\"SemiBold\"", ViewSource(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task View_TheStoredValuesBanner_IsTheSharedCallout()
    {
        using var harness = await Factory
            .Create(general => general with { ScanRoots = [Factory.Root] }, followGeneralChanged: true)
            .SettleAsync();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        var banner = view.GetControl<ContentControl>("StoredValuesNoticeBanner");
        Assert.Contains("callout", banner.Classes);
        Assert.Contains("warn", banner.Classes);

        // The hand-rolled brushes it used to set inline are gone with the class.
        var source = ViewSource();
        Assert.DoesNotContain("ColorWarningCalloutFill", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ColorWarningCalloutBorder", source, StringComparison.Ordinal);
    }

    // HelpPlacementCensusTest owns the census; this is the local proof that the vocabulary move
    // did not lose a glyph out of a restructured heading row.
    [AvaloniaFact]
    public async Task View_StillHasItsHelpGlyphs()
    {
        using var harness = await Factory.Create().SettleAsync();
        Assert.NotNull(harness.ViewModel);

        var source = ViewSource();
        foreach (var topic in new[]
                 {
                     "settings.library.scan-roots", "settings.library.include-paths",
                     "settings.library.exclude-paths", "settings.library.name-rules",
                     "settings.library.test-path", "settings.library.scanning",
                     "settings.library.manual-scan", "settings.library.setup",
                 })
        {
            Assert.Contains($"Topic=\"{topic}\"", source, StringComparison.Ordinal);
        }

        // Still eight, and no ninth after Phase 15A Task 6: the new "Read PHD2 guide logs"
        // checkbox sits under this section's existing settings.library.scanning glyph, exactly as
        // "Include calibration frames" does. U1 adds no help topic for the scan filter notice or
        // the scan control either (the brief's "six" undercounts what the tab carries at HEAD).
        Assert.Equal(8, CountOccurrences(source, "<controls:HelpButton"));
    }

    // ---- Phase 15A Task 6 -----------------------------------------------------------------------

    [AvaloniaFact]
    public async Task View_ShowsThePhd2ScanEnabledCheckboxBelowCalibrationInTheScanningSection()
    {
        using var harness = await Factory.Create().SettleAsync();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        var box = view.GetControl<CheckBox>("Phd2ScanEnabledCheckBox");
        Assert.Equal("Read PHD2 guide logs", box.Content);
        Assert.Equal("ScanOptionsSection", Ancestor(box, "ScanOptionsSection").Name);
        Assert.True(box.IsChecked);

        var texts = VisibleTexts(view);
        Assert.Contains(
            "Guide logs named PHD2_GuideLog_*.txt found under your library folders are read for "
            + "guiding measurements. Turning this off stops future scans reading them and deletes "
            + "nothing. While it is off, a frame whose file changes loses the guiding figure its "
            + "guide log gave it the next time that frame is scanned, and turning this back on "
            + "restores the figure at the following scan.",
            texts);
        Assert.True(view.GetControl<TextBlock>("Phd2ScanEnabledHelpText").IsEffectivelyVisible);

        var calibration = view.GetControl<CheckBox>("IncludeCalibrationCheckBox");
        var source = ViewSource();
        Assert.True(
            source.IndexOf("x:Name=\"IncludeCalibrationCheckBox\"", StringComparison.Ordinal)
            < source.IndexOf("x:Name=\"Phd2ScanEnabledCheckBox\"", StringComparison.Ordinal),
            "the PHD2 checkbox must be declared below the calibration checkbox");
        Assert.NotNull(calibration);
    }

    [AvaloniaFact]
    public async Task View_AFailedRead_StillHidesThePhd2Checkbox()
    {
        using var harness = await Factory
            .Create(loadThrows: new InvalidOperationException("no database"))
            .SettleAsync();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);

        Assert.False(view.GetControl<CheckBox>("Phd2ScanEnabledCheckBox").IsEffectivelyVisible);
    }

    // Phase 15B Task 5c: the Statistics page's Guiding empty notice offers "Enable guide log
    // scanning", and the shell asks this tab for the switch before the view exists, so the request
    // is consumed on attach.
    [AvaloniaFact]
    public async Task View_OnAttach_ConsumesThePendingGuideLogSwitchRequest()
    {
        using var harness = await Factory.Create().SettleAsync();
        harness.ViewModel.RequestGuideLogSwitchInView();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);
        Dispatcher.UIThread.RunJobs();

        // Consumed by the attach, so the next visit to the tab is an ordinary visit rather than a
        // second scroll the user did not ask for.
        Assert.False(harness.ViewModel.ConsumeGuideLogSwitchInViewRequest());
        Assert.True(view.GetControl<CheckBox>("Phd2ScanEnabledCheckBox").IsEffectivelyVisible);
    }

    // ---- Phase 15B Task 5c, review P2-2: the first visit -------------------------------------
    //
    // A first visit is the case both routes into this tab actually produce, and it is the one the
    // case above cannot reach: it settles the tab before the view is built, so IsReady is already
    // true at attach and the waiting branch never runs. Held open here by giving the tab a post
    // seam that queues instead of running, which is exactly what a read still on the pool looks
    // like: every section stays collapsed behind IsVisible="{Binding IsReady}" and a BringIntoView
    // issued at attach scrolls against a control with no rect, at any dispatcher priority.

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static (Factory.Harness Harness, List<Action> Queued) HeldOpen()
    {
        var queued = new List<Action>();
        var harness = Factory.Create(post: action => queued.Add(action));

        // The read itself has run; what is held is the publish it posted.
        harness.ViewModel.PendingLoad?.Wait(Budget);
        return (harness, queued);
    }

    private static void ReleaseLoad(List<Action> queued)
    {
        foreach (var action in queued.ToArray())
        {
            action();
        }

        queued.Clear();

        // Twice: the first pass lets the layout run against the sections the publish made visible,
        // the second runs the scroll this view posts at Loaded priority behind it.
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void View_OnAFirstVisit_ScrollsToTheGuideLogSwitchOnceTheTabIsReady()
    {
        var (harness, queued) = HeldOpen();
        using var _ = harness;
        harness.ViewModel.RequestGuideLogSwitchInView();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        Show(view);
        Dispatcher.UIThread.RunJobs();

        // The state a first visit attaches in: nothing to scroll to, and nothing scrolled.
        Assert.False(harness.ViewModel.IsReady);
        Assert.False(view.GetControl<CheckBox>("Phd2ScanEnabledCheckBox").IsEffectivelyVisible);
        Assert.Equal(0d, ScrollAssertions.Scroller(view).Offset.Y);

        ReleaseLoad(queued);

        Assert.True(harness.ViewModel.IsReady);
        Assert.True(
            ScrollAssertions.IsInView(
                view.GetControl<CheckBox>("Phd2ScanEnabledCheckBox"), ScrollAssertions.Scroller(view)),
            "the switch is below the fold on this tab, so a scroll issued before the tab was ready "
            + "leaves it off screen");
    }

    [AvaloniaFact]
    public void View_DetachedBeforeTheTabIsReady_LeavesNoWaitBehind()
    {
        var (harness, queued) = HeldOpen();
        using var _ = harness;
        harness.ViewModel.RequestGuideLogSwitchInView();
        var view = new LibraryTabView { DataContext = harness.ViewModel };
        var window = Show(view);
        Dispatcher.UIThread.RunJobs();

        // Away and back, which is what leaving the Settings page before its first read lands does.
        // The second attach consumes nothing, because the request was consumed by the first.
        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        window.Content = view;
        Dispatcher.UIThread.RunJobs();

        ReleaseLoad(queued);

        // A wait the detach failed to drop would fire here and scroll a visit that asked for
        // nothing.
        Assert.Equal(0d, ScrollAssertions.Scroller(view).Offset.Y);
    }
}
