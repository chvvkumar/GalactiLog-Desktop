using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Tests.ViewModels.Wbpp;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.App.Views;
using GalactiLog.App.Views.TargetDetail.Wbpp;
using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.App.Tests.Views.Wbpp;

/// <summary>
/// Design-spec 18.3's view smoke tests for the export wizard window: it parses, lays out, hosts the
/// shared frame, binds each step's template, and refuses Escape and the title bar while committed
/// or copying. The behaviour is asserted on the view-models, in <c>WbppExportWizardTests</c>.
/// </summary>
public class WbppExportWindowTests
{
    private static readonly DateOnly N1 = new(2025, 3, 20);

    private const int PinnedWidth = 1000;

    private const int PinnedMinWidth = 860;

    private const int PinnedHeight = 780;

    private static string Ha(string file) => Path.Combine(
        Library.Root, "M31", N1.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Ha", file);

    private static string Markup() => File.ReadAllText(Path.Combine(
        SourceScan.SrcRoot(), "GalactiLog.App", "Views", "TargetDetail", "Wbpp", "WbppExportWindow.axaml"));

    private static ExportHarness Harness(Library? library = null, bool installPickers = true, bool parked = false,
        Func<string, IReadOnlyList<string>?>? listSubfolders = null, CalibrationWalk.Walker? walk = null)
        => OnUi(ExportHarness.Create(
            library ?? new Library().Frame(N1, Ha("a.fits")), [N1], installPickers: installPickers, parked: parked,
            listSubfolders: listSubfolders, walk: walk, post: UiThreadPost), parked);

    // A window binds the page, so every post lands on the UI thread, pumped by RunJobs and Until,
    // rather than writing a bound view-model from a pool thread.
    private static readonly Action<Action> UiThreadPost = action => Dispatcher.UIThread.Post(action);

    private static ExportHarness OnUi(ExportHarness harness, bool parked = false)
    {
        if (!parked)
        {
            Dispatcher.UIThread.RunJobs();   // the load's posted publish
        }

        return harness;
    }

    // A step template's elements sit in the template's own scope under StepHost, so they are
    // found in the visual tree, the way SetupWizardWindowTests finds them.
    private static T? InStepOrNull<T>(Control window, string name)
        where T : Control
        => window.Named<ContentControl>("StepHost").GetVisualDescendants().OfType<T>()
            .SingleOrDefault(control => control.Name == name);

    private static T InStep<T>(Control window, string name)
        where T : Control
        => InStepOrNull<T>(window, name) ?? throw new InvalidOperationException("No " + name + " in the step on screen.");

    private static WbppExportWizardViewModel Wizard(ExportHarness harness)
        => new(harness.Page, _ => Task.CompletedTask, post: action => action());

    private static WbppExportWindow Show(WbppExportWizardViewModel wizard, double? width = null)
    {
        var window = new WbppExportWindow { DataContext = wizard };
        if (width is { } value)
        {
            window.Width = value;
        }

        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    // RefuseClose is protected, so the case reaches it the way every other dialog's case does.
    private static bool Refuses(WbppExportWindow window, WindowCloseReason reason)
    {
        var method = typeof(WbppExportWindow).GetMethod(
            "RefuseClose",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return (bool)method!.Invoke(window, [reason])!;
    }

    private static async Task ToReviewAsync(ExportHarness harness, WbppExportWizardViewModel wizard)
    {
        harness.Page.StagingPath = harness.TempFolder("staging");
        harness.Page.CommitStagingCommand.Execute(null);
        for (var i = 0; i < 4; i++)
        {
            await wizard.NextCommand.ExecuteAsync(null);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void NoDisk(ExportHarness harness, Action? onEnumerate = null)
        => harness.Page.StagingIoFor = _ => new StagingIo(
            _ => new MemoryStream(),
            _ =>
            {
                onEnumerate?.Invoke();
                return [];
            },
            _ => null,
            _ => { },
            _ => new MemoryStream());

    // A failure here is the tree drawn without a tick on every row, or a tick on a shallower row
    // not cascading to the rows beneath it, or the session row and summary not following a tick.
    [AvaloniaFact]
    public void TheTreeIsOpenOnLoad_EveryRowCarriesATick_AndTickingAShallowerRowCascades()
    {
        var date = Path.Combine(Library.Root, "M31", N1.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        using var harness = Harness(listSubfolders: folder =>
            string.Equals(folder, date, StringComparison.OrdinalIgnoreCase) ? [Path.Combine(date, "FLAT"), Path.Combine(date, "Ha")] : []);
        var wizard = Wizard(harness);
        var window = Show(wizard);
        var session = harness.Page.Sessions[0];
        Until(() => session.Rows.All(r => !r.IsListing));

        Assert.Equal("Collapse", InStep<Button>(window, "TreeToggleButton").Content);
        Assert.Empty(window.GetVisualDescendants().OfType<RadioButton>());
        var ticks = Visible<CheckBox>(window, "TickBox");
        Assert.Equal(3, ticks.Count);   // M31, date, Ha: the date row is path and does not list
        Assert.Equal(2, session.ChosenIndex);
        Assert.Equal([false, false, true], ticks.Select(t => t.IsChecked == true));

        session.Rows[1].IsIncluded = true;   // the date row, which lists now it is copied
        Until(() => session.Rows[1].Children.Count == 2 && !session.Rows[1].IsListing);
        Assert.Equal(["FLAT", "Ha"], session.Rows[1].Children.Select(c => c.FolderName));
        Assert.Equal([false, true, true, true], Visible<CheckBox>(window, "TickBox").Select(t => t.IsChecked == true));

        session.Rows[1].Children.Single(c => c.FolderName == "FLAT").IsIncluded = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("1 folder skipped", InStep<TextBlock>(window, "SkippedChip").Text);
        Assert.Contains("after skipping 1 folder", InStep<TextBlock>(window, "NightSummary").Text, StringComparison.Ordinal);
        Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == FolderNodeViewModel.SkippedText && t.IsEffectivelyVisible);
        harness.Dispose();
        window.Close();
    }

    // A failure here is a window whose allotment moved, which every layout pin below depends on.
    [AvaloniaFact]
    public void Window_ProducesANonZeroLayoutAtItsOwnAllotment()
    {
        using var harness = Harness();
        using var wizard = Wizard(harness);
        var window = Show(wizard);

        Assert.Equal(PinnedWidth, window.Width);
        Assert.Equal(PinnedMinWidth, window.MinWidth);
        Assert.Equal(PinnedHeight, window.Height);
        Assert.True(window.Bounds.Width > 0);
        Assert.True(window.Bounds.Height > 0);
        Assert.IsAssignableFrom<ModalPageWindow<WbppExportWizardViewModel>>(window);
        Assert.Equal(WindowStartupLocation.CenterOwner, window.WindowStartupLocation);

        harness.Dispose();
        window.Close();
    }

    // A failure here is a window that hosts its own chrome instead of the shared frame, or a help
    // glyph that does not follow the step (R13, E3).
    [AvaloniaFact]
    public async Task TheFrameHostsTheStep_AndTheHelpGlyphFollowsIt()
    {
        using var harness = Harness();
        using var wizard = Wizard(harness);
        var window = Show(wizard);

        var frame = window.Named<WizardFrame>("Frame");
        var help = Assert.IsType<HelpButton>(frame.Named<ContentPresenter>("HelpSlotHost").Content);
        Assert.Equal("export.folders", help.Topic);
        Assert.Equal("Step 1 of 6: Folders to copy", frame.Named<TextBlock>("StepHeaderText").Text);
        Assert.NotNull(InStep<ItemsControl>(window, "SessionRows"));
        Assert.Equal("1 light frame", InStep<TextBlock>(window, "FooterFrameCount").Text);
        Assert.False(InStep<TextBlock>(window, "LevelsLoadingLine").IsVisible);

        await wizard.NextCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("export.quality", help.Topic);
        Assert.NotNull(InStep<ContentControl>(window, "QualityPanelRegion"));
        Assert.Null(InStepOrNull<ItemsControl>(window, "SessionRows"));

        harness.Dispose();
        window.Close();
    }

    // A failure here is a loading line that is permanent or absent, or a figure shown before the read.
    [AvaloniaFact]
    public void WhileTheLevelsAreBeingRead_TheLoadingLineShowsAndNextIsDisabled()
    {
        using var harness = Harness(parked: true);
        using var wizard = Wizard(harness);
        var window = Show(wizard);

        Assert.True(InStep<TextBlock>(window, "LevelsLoadingLine").IsVisible);
        Assert.Equal("", InStep<TextBlock>(window, "FooterFrameCount").Text);
        Assert.False(window.Named<Button>("NextButton").IsEffectivelyEnabled);

        // Not released: the case pins the loading state only. The settled state is pinned by
        // WbppExportWizardTests and by the other cases here.
        harness.Dispose();
        window.Close();
    }

    // A failure here is the chosen level's contamination cost hidden inside the closed tree.
    [AvaloniaFact]
    public void EveryLevelContaminated_DrawsTheChosenLevelsCostOnTheSessionRow()
    {
        var library = new Library()
            .Frame(N1, Ha("a.fits"))
            .Foreign(Ha("x.fits"), new DateOnly(2025, 3, 21));
        using var harness = Harness(library);
        using var wizard = Wizard(harness);
        var window = Show(wizard);

        var badge = InStep<TextBlock>(window, "ChosenOtherNightsBadge");
        Assert.True(badge.IsVisible);
        Assert.Equal("+1 other night", badge.Text);

        harness.Dispose();
        window.Close();
    }

    // A failure looks like the two path inks in a StackPanel, which makes the trimming inert and
    // draws a real N.I.N.A. path over the tree toggle button at the minimum width.
    [AvaloniaFact]
    public void AtTheMinimumWidth_ARealShapedPathElidesInsteadOfOverrunningTheRow()
    {
        var deep = Path.Combine(
            Library.Root, "2025-03-20", "M 31 mosaic panel two", "LIGHT", "Ha 3nm narrowband", "Angle_123.45", "a.fits");
        using var harness = Harness(new Library().Frame(N1, deep));
        using var wizard = Wizard(harness);
        var window = Show(wizard, PinnedMinWidth);

        var ownName = InStep<TextBlock>(window, "ChosenOwnNameText");
        var toggle = InStep<Button>(window, "TreeToggleButton");
        Assert.True(InStep<TextBlock>(window, "ChosenLeadingPathText").Bounds.Width > 0);

        var pathRight = ownName.TranslatePoint(new Point(ownName.Bounds.Width, 0), window)!.Value.X;
        var buttonLeft = toggle.TranslatePoint(default, window)!.Value.X;
        Assert.True(pathRight <= buttonLeft + 1, "the chosen path overruns the tree toggle button");

        harness.Dispose();
        window.Close();
    }

    // A failure here is a picker seam left on a page after its window closed, or a Browse button
    // greyed because the seam arrived after the binding read it (launched-app look B1).
    [AvaloniaFact]
    public async Task TheWindowInstallsBothPickerSeams_EnablesBrowse_AndClearsThemOnClose()
    {
        using var harness = Harness(installPickers: false);
        using var wizard = Wizard(harness);
        var window = Show(wizard);

        Assert.NotNull(harness.Page.ScriptDestinationPicker);
        Assert.NotNull(harness.Page.StagingFolderPicker);

        harness.Page.StagingPath = harness.TempFolder("staging");
        harness.Page.CommitStagingCommand.Execute(null);
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(InStep<Button>(window, "StagingPickerButton").IsEffectivelyEnabled);
        Assert.True(InStep<CheckBox>(window, "SubfolderCheckBox").IsChecked);

        harness.Dispose();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(harness.Page.ScriptDestinationPicker);
        Assert.Null(harness.Page.StagingFolderPicker);
    }

    // A failure here is a Commit reachable off the review step, or a review step whose Next moves on
    // without committing (R3, R18).
    [AvaloniaFact]
    public async Task TheCommitButton_ShowsOnTheReviewStepOnly_WithItsLabel()
    {
        using var harness = Harness();
        using var wizard = Wizard(harness);
        var window = Show(wizard);
        var commit = window.Named<Button>("CommitButton");

        Assert.False(commit.IsVisible);

        await ToReviewAsync(harness, wizard);

        Assert.True(commit.IsVisible);
        Assert.Equal("Copy 1 folder (1 frame)", commit.Content);
        Assert.False(commit.IsDefault);
        Assert.False(window.Named<Button>("NextButton").IsEffectivelyEnabled);
        Assert.Equal(ReviewStep.FileSafetyText, InStep<TextBlock>(window, "FileSafetyText").Text);

        harness.Dispose();
        window.Close();
    }

    // A failure here is an Escape that leaves the wizard open while nothing is committed.
    [AvaloniaFact]
    public void Escape_ClosesTheWizard_WhileNothingIsCommitted()
    {
        using var harness = Harness();
        using var wizard = Wizard(harness);
        var window = Show(wizard);
        var closed = false;
        wizard.CloseRequested += (_, _) => closed = true;

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.False(window.IsVisible);
        harness.Dispose();
    }

    // Blocks a copier pool thread until the case opens the gate; outside any test body, because
    // xUnit1031 is an error here.
    private static void Park(TaskCompletionSource entered, ManualResetEventSlim gate)
    {
        entered.TrySetResult();
        gate.Wait(TimeSpan.FromSeconds(30));
    }

    // A failure here is an Escape that is refused while the copy runs, or one that stops the copy.
    [AvaloniaFact]
    public async Task Escape_ClosesTheWindow_WhileTheCopyRuns()
    {
        using var harness = Harness();
        using var wizard = Wizard(harness);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = new ManualResetEventSlim(false);
        NoDisk(harness, () => Park(entered, gate));
        var window = Show(wizard);
        await ToReviewAsync(harness, wizard);
        var commit = wizard.CommitCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(wizard.IsBusy);
        var closed = false;
        wizard.CloseRequested += (_, _) => closed = true;

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.False(window.IsVisible);
        gate.Set();
        await commit.WaitAsync(TimeSpan.FromSeconds(30));
        harness.Dispose();
    }

    // A failure here is a result step that traps the user: committed and idle, Escape closes.
    [AvaloniaFact]
    public async Task Escape_ClosesTheResultStep()
    {
        using var harness = Harness();
        using var wizard = Wizard(harness);
        NoDisk(harness);
        var window = Show(wizard);
        await ToReviewAsync(harness, wizard);
        await wizard.CommitCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(wizard.IsCommitted);
        Assert.True(InStep<Button>(window, "OpenFolderButton").IsVisible);
        Assert.False(Refuses(window, WindowCloseReason.WindowClosing));
        var closed = false;
        wizard.CloseRequested += (_, _) => closed = true;

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.False(window.IsVisible);
        harness.Dispose();
    }

    // A failure here is a title bar that traps the user behind a running copy, or an idle wizard.
    [AvaloniaFact]
    public async Task RefuseClose_IsFalse_WhileTheCopyRuns()
    {
        using var harness = Harness();
        using var wizard = Wizard(harness);
        var window = Show(wizard);
        bool? refusedDuringCopy = null;
        NoDisk(harness, () => refusedDuringCopy = Refuses(window, WindowCloseReason.WindowClosing));

        Assert.False(Refuses(window, WindowCloseReason.WindowClosing));

        await ToReviewAsync(harness, wizard);
        await wizard.CommitCommand.ExecuteAsync(null);

        Assert.False(refusedDuringCopy);
        Assert.False(Refuses(window, WindowCloseReason.WindowClosing));

        harness.Dispose();
        window.Close();
    }

    // Review finding 8, decision 6's one remaining refusal. A failure here is a title-bar X or an
    // Escape that closes the window under a script commit's open save dialog.
    [AvaloniaFact]
    public async Task TheTitleBarAndEscape_AreRefused_WhileAScriptCommitRuns()
    {
        using var harness = Harness();
        using var wizard = Wizard(harness);
        var window = Show(wizard);
        var picked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Page.ScriptDestinationPicker = _ => picked.Task;
        await ToReviewAsync(harness, wizard);
        wizard.Method.IsScript = true;
        var commit = wizard.CommitCommand.ExecuteAsync(null);
        Assert.True(wizard.IsBusy);

        Assert.True(Refuses(window, WindowCloseReason.WindowClosing));
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);

        picked.SetResult(null);
        await commit.WaitAsync(TimeSpan.FromSeconds(30));
        harness.Dispose();
        window.Close();
    }

    // A failure here is a copy whose window gives no hint that closing it is safe.
    [AvaloniaFact]
    public async Task TheBackgroundHint_IsInsideTheCopyProgressPanel()
    {
        using var harness = Harness();
        using var wizard = Wizard(harness);
        var window = Show(wizard);
        await ToReviewAsync(harness, wizard);

        var hint = InStep<TextBlock>(window, "CopyBackgroundHint");

        Assert.Equal(ReviewStep.BackgroundCopyText, hint.Text);
        Assert.Contains(InStep<ProgressBar>(window, "CopyProgressBar"), ((Panel)hint.Parent!).Children);

        harness.Dispose();
        window.Close();
    }

    [Fact]
    public void TheMarkupDeclaresNoStyleOfItsOwn_AndHostsTheFrame()
    {
        var markup = Markup();

        Assert.DoesNotMatch(new Regex(@"<Style\s+Selector=""Button"), markup);
        Assert.DoesNotMatch(new Regex(@"<Style\s+Selector=""Border\."), markup);
        Assert.DoesNotContain("FontSize", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("IsDefault", markup, StringComparison.Ordinal);
        Assert.Contains(@"x:DataType=""wbpp:WbppExportWizardViewModel""", markup, StringComparison.Ordinal);
        Assert.Contains("<controls:WizardFrame", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSaveOptionsCarryTheStartLocationAndTheOverwritePrompt()
    {
        var options = WbppExportWindow.SaveOptions("wbpp_M_31.ps1", null);
        Assert.Equal("wbpp_M_31.ps1", options.SuggestedFileName);
        Assert.Equal("ps1", options.DefaultExtension);
        Assert.True(options.ShowOverwritePrompt);
        Assert.Equal(["*.ps1"], options.FileTypeChoices![0].Patterns!);

        var shell = WbppExportWindow.SaveOptions("wbpp_M_31.sh", null);
        Assert.Equal("sh", shell.DefaultExtension);
        Assert.Equal(["*.sh"], shell.FileTypeChoices![0].Patterns!);

        var report = WbppExportWindow.SaveOptions("copy_report_M_31.txt", null);
        Assert.Equal("txt", report.DefaultExtension);
        Assert.Equal(["*.txt"], report.FileTypeChoices![0].Patterns!);
    }

    // The night folder's calibration: a matched flat folder and one whose rotation differs.
    private static string NightDir => Path.Combine(Library.Root, "M31", N1.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static FakeDisk CalibrationDisk() => new FakeDisk()
        .Add(CalibrationFixtures.F(Ha("a.fits"), CalibrationType.Light, CalibrationFixtures.K(), night: N1, size: 1_000))
        .Add(CalibrationFixtures.F(Path.Combine(NightDir, "FLAT", "f1.fits"), CalibrationType.Flat, CalibrationFixtures.K(), night: N1))
        .Add(CalibrationFixtures.F(Path.Combine(NightDir, "FLAT139", "g1.fits"), CalibrationType.Flat,
            CalibrationFixtures.K(rot: 139.07), night: N1));

    private static void Until(Func<bool> condition)
        => Assert.True(SpinWait.SpinUntil(
            () =>
            {
                Dispatcher.UIThread.RunJobs();
                return condition();
            },
            TimeSpan.FromSeconds(10)));

    private static List<T> Visible<T>(Control window, string name)
        where T : Control
        => [.. window.GetVisualDescendants().OfType<T>().Where(c => c.Name == name && c.IsEffectivelyVisible)];

    // A failure here is the strip not drawn from the session's scan state, or a strip shown on a
    // page that has no walker.
    [AvaloniaFact]
    public void TheStrip_RendersTheScanState_AndHidesWithoutAWalker()
    {
        using (var bare = Harness())
        {
            using var bareWizard = Wizard(bare);
            var bareWindow = Show(bareWizard);
            Assert.Empty(Visible<StackPanel>(bareWindow, "CalibrationBlock"));
            bare.Dispose();
            bareWindow.Close();
        }

        using var harness = Harness(walk: CalibrationDisk().Walker);
        using var wizard = Wizard(harness);
        var window = Show(wizard);
        var session = harness.Page.Sessions[0];
        Until(() => session.ScanState == CalibrationScanState.Done && session.CalibrationRows.Count == 2);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(Visible<StackPanel>(window, "CalibrationBlock"));
        var sentence = Visible<TextBlock>(window, "ScanSentenceLine").Single();
        Assert.Equal(session.ScanSentence, sentence.Text);
        Assert.StartsWith("Scanned ", sentence.Text, StringComparison.Ordinal);
        Assert.Equal(session.CalibrationTally, Visible<TextBlock>(window, "CalibrationTally").Single().Text);
        Assert.Single(Visible<ItemsControl>(window, "LightSetLines"));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == session.LightSetLines[0] && t.IsEffectivelyVisible);
        var rescan = Visible<Button>(window, "RescanButton").Single();
        Assert.True(rescan.IsEffectivelyEnabled);
        Assert.Equal("Rescan", rescan.Content);
        Assert.Empty(Visible<Button>(window, "LookHigherButton"));
        Assert.Empty(Visible<TextBlock>(window, "NoCalibrationLine"));

        // It collapses with the tree.
        session.IsExpanded = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(Visible<StackPanel>(window, "CalibrationBlock"));

        harness.Dispose();
        window.Close();
    }

    // A failure here is a footer that hides the scan's state: no sizing marker while the walk runs,
    // or no breakdown once it lands.
    [AvaloniaFact]
    public void Footer_ShowsBreakdown_AndSizingWhileScanning()
    {
        var disk = CalibrationDisk();
        disk.Gate = new ManualResetEventSlim(false);
        try
        {
            using var harness = Harness(walk: disk.Walker);
            using var wizard = Wizard(harness);
            var window = Show(wizard);
            var session = harness.Page.Sessions[0];

            Assert.Equal(CalibrationScanState.Scanning, session.ScanState);
            Assert.EndsWith(", sizing", InStep<TextBlock>(window, "FooterSize").Text, StringComparison.Ordinal);
            Assert.Equal(session.ProgressText, Visible<TextBlock>(window, "HeaderScanLine").Single().Text);

            disk.Gate.Set();
            Until(() => session.ScanState == CalibrationScanState.Done
                && !harness.Page.SizeText.EndsWith(", sizing", StringComparison.Ordinal));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(harness.Page.SizeText, InStep<TextBlock>(window, "FooterSize").Text);
            var breakdown = Visible<TextBlock>(window, "FooterBreakdown").Single();
            Assert.Equal(harness.Page.BreakdownText, breakdown.Text);
            Assert.StartsWith("lights ", breakdown.Text, StringComparison.Ordinal);

            harness.Dispose();
            window.Close();
        }
        finally
        {
            disk.Gate.Set();
        }
    }

    // A failure here is a running scan with nothing moving beside the night's header, a strip that
    // repeats the line a screen lower, or a Rescan button that reads as pressable mid-scan.
    [AvaloniaFact]
    public void NightHeader_ShowsTheRunningScan_AndDropsItWhenTheScanCompletes()
    {
        var disk = CalibrationDisk();
        disk.Gate = new ManualResetEventSlim(false);
        try
        {
            using var harness = Harness(walk: disk.Walker);
            using var wizard = Wizard(harness);
            var window = Show(wizard);
            var session = harness.Page.Sessions[0];

            Assert.Equal(CalibrationScanState.Scanning, session.ScanState);
            var line = Visible<TextBlock>(window, "HeaderScanLine").Single();
            Assert.Equal(session.ProgressText, line.Text);
            Assert.StartsWith("Scanning files: reading ", line.Text, StringComparison.Ordinal);
            var bar = Visible<ProgressBar>(window, "HeaderScanBar").Single();
            Assert.True(bar.IsIndeterminate);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "ScanProgressLine");
            var rescan = Visible<Button>(window, "RescanButton").Single();
            Assert.False(rescan.IsEffectivelyEnabled);
            Assert.Equal("Scanning...", rescan.Content);

            disk.Gate.Set();
            Until(() => session.ScanState == CalibrationScanState.Done);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(Visible<TextBlock>(window, "HeaderScanLine"));
            Assert.Empty(Visible<ProgressBar>(window, "HeaderScanBar"));
            Assert.False(bar.IsIndeterminate);
            Assert.True(rescan.IsEffectivelyEnabled);
            Assert.Equal("Rescan", rescan.Content);

            harness.Dispose();
            window.Close();
        }
        finally
        {
            disk.Gate.Set();
        }
    }

    // A failure here is a review step that states the size without saying what it is made of.
    [AvaloniaFact]
    public async Task Review_ShowsBreakdown()
    {
        using var harness = Harness(walk: CalibrationDisk().Walker);
        using var wizard = Wizard(harness);
        var window = Show(wizard);
        var session = harness.Page.Sessions[0];
        Until(() => session.ScanState == CalibrationScanState.Done
            && !harness.Page.SizeText.EndsWith(", sizing", StringComparison.Ordinal));

        NoDisk(harness);
        await ToReviewAsync(harness, wizard);

        var breakdown = InStep<TextBlock>(window, "ReviewBreakdown");
        Assert.True(breakdown.IsEffectivelyVisible);
        Assert.Equal(harness.Page.BreakdownText, breakdown.Text);
        Assert.StartsWith("lights ", breakdown.Text, StringComparison.Ordinal);

        harness.Dispose();
        window.Close();
    }

    // A failure here is a calibration folder drawn as a plain row: no figures, no note, or the
    // expander shown on a leaf.
    [AvaloniaFact]
    public void CalibrationRows_AreDrawnInTheTree_WithTicksFiguresNotesAndNoExpanderOnLeaves()
    {
        using var harness = OnUi(ExportHarness.Create(Pleiades.Catalogue(), [Pleiades.N18], walk: Pleiades.Disk().Walker, post: UiThreadPost));
        using var wizard = Wizard(harness);
        var window = Show(wizard);
        var session = harness.Page.Sessions[0];
        Until(() => session.ScanState == CalibrationScanState.Done && session.CalibrationRows.Count == 2);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(10, Visible<CheckBox>(window, "TickBox").Count);   // six chain rows, FLAT, its camera row, two angle rows
        Assert.Equal(["2 of 2 files, " + MetricText.Bytes(200), "1 file, " + MetricText.Bytes(100)],
            Visible<TextBlock>(window, "CalibrationText").Where(t => t.DataContext is FolderNodeViewModel { Row: not null }).Select(t => t.Text));
        Assert.Equal(["same night", "rotation 96.26"], Visible<TextBlock>(window, "NoteTag").Select(t => t.Text));
        Assert.Empty(Visible<TextBlock>(window, "RowTag"));
        Assert.Equal("1 of 2 calibration folders ticked", Visible<TextBlock>(window, "CalibrationTally").Single().Text);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Calibration found");
        Assert.DoesNotContain(Visible<Button>(window, "ExpandButton"), b => b.DataContext is FolderNodeViewModel { IsLeaf: true });
        Assert.Contains(Visible<Button>(window, "ExpandButton"), b => b.DataContext is FolderNodeViewModel { FolderName: "FLAT" });

        // Two way: the box writes the row's tick, and the tally follows.
        var rejected = Visible<CheckBox>(window, "TickBox").Single(c => c.DataContext is FolderNodeViewModel { FolderName: "Angle_96.26" });
        rejected.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(session.CalibrationRows.Single(r => r.FolderName == "Angle_96.26").IsIncluded);
        Assert.Equal("2 of 2 calibration folders ticked", Visible<TextBlock>(window, "CalibrationTally").Single().Text);

        harness.Dispose();
        window.Close();
    }

    [Fact]
    public void Markup_NamesTheStripAndRowElements()
    {
        var markup = Markup();

        foreach (var name in new[]
        {
            "CalibrationBlock", "HeaderScanBar", "HeaderScanLine", "ScanSentenceLine", "CalibrationTally", "LightSetLines",
            "CalibrationWarnings", "NoCalibrationLine", "RescanButton", "LookHigherButton",
            "FooterBreakdown", "ReviewBreakdown", "ExpandButton", "TickBox", "CalibrationText", "NoteTag", "RowTag",
        })
        {
            Assert.Contains(@"x:Name=""" + name + @"""", markup, StringComparison.Ordinal);
        }

        foreach (var gone in new[] { "ScanProgressLine", @"Content=""Rescan""", "CalibrationHeading", @"Text=""Calibration found""", "ChainRadio", "ChosenPill", "CalibrationRows", "CopiesFromHereText", "IsChosenZone", "ShowsRadio", "ShowsTick" })
        {
            Assert.DoesNotContain(gone, markup, StringComparison.Ordinal);
        }

        Assert.Contains(
            @"Text=""Tick: copy this folder and everything inside it. Untick inside it to leave something out. +: list the folders inside.""",
            markup, StringComparison.Ordinal);
        Assert.Contains(@"IsVisible=""{Binding HasCalibrationBlock}""", markup, StringComparison.Ordinal);
        Assert.Contains(@"ToolTip.Tip=""{Binding IssuesTooltip}""", markup, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"x:Name=""ExpandButton""\s+[^>]*IsVisible=""\{Binding ShowsExpander\}"""), markup);
        Assert.Matches(new Regex(@"x:Name=""NoteTag""[^>]*ColorTextSecondary"), markup);
        Assert.Matches(new Regex(@"x:Name=""HeaderScanLine""[^>]*ColorAccent"), markup);
        Assert.Matches(new Regex(@"x:Name=""HeaderScanBar""[^>]*IsIndeterminate=""\{Binding IsScanning\}"""), markup);
        Assert.Matches(new Regex(@"x:Name=""RowTag""[^>]*ColorWarning"), markup);
        Assert.Matches(new Regex(@"x:Name=""CalibrationText""[^>]*ColorTextSecondary"), markup);
        Assert.Matches(new Regex(@"x:Name=""RescanButton""\s+[^>]*Classes=""quiet sm"""), markup);
        Assert.True(markup.IndexOf(@"x:Name=""NightSummary""", StringComparison.Ordinal)
            < markup.IndexOf(@"x:Name=""CalibrationBlock""", StringComparison.Ordinal));
        Assert.True(markup.IndexOf(@"x:Name=""FooterSize""", StringComparison.Ordinal)
            < markup.IndexOf(@"x:Name=""FooterBreakdown""", StringComparison.Ordinal));
    }
}
