using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.Diagnostics;
using GalactiLog.App.Views;
using GalactiLog.Core.Diagnostics;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Spec 18.3's view smoke scope for spec 12.8's log viewer. Every case builds its page through the
// fixture's awaiting form: an AvaloniaFact runs on the headless UI thread and that thread is a
// pool thread, so a blocking wait at construction can inline the first read onto it (TRACKING
// section 2 item 8).
public class LogViewerViewTests
{
    private static (Window Window, LogViewerView View) Show(LogViewerViewModel page)
    {
        var view = new LogViewerView { DataContext = page };
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    [AvaloniaFact]
    public async Task Constructs_AndLaysOutNonZero()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile("galactilog-20260915.log", LogViewerFixture.Entry("10:00:00.000"));

        using var page = await fixture.CreateAsync();
        var (_, view) = Show(page);

        Assert.True(view.Bounds.Width > 0);
        Assert.True(view.Bounds.Height > 0);
    }

    [Fact]
    public void View_DeclaresXDataType()
    {
        // Compiled bindings are on repository-wide (AvaloniaUseCompiledBindingsByDefault), and a
        // view without x:DataType silently falls back to reflection bindings.
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.App", "Views", "LogViewerView.axaml"));

        Assert.Contains(@"x:DataType=""diagnostics:LogViewerViewModel""", markup, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task View_UsesAVirtualizingStackPanel()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile(
            "galactilog-20260915.log",
            [.. Enumerable.Range(0, 60).Select(index => LogViewerFixture.Entry(
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture, "10:00:{0:00}.000", index),
                message: "m" + index))]);

        using var page = await fixture.CreateAsync();
        var (_, view) = Show(page);

        // The roadmap says virtualized. A bounded scroller is what gives the panel a viewport to
        // virtualise against, the same shape FrameTableView settled on.
        Assert.Contains(view.GetVisualDescendants(), visual => visual is VirtualizingStackPanel);
    }

    [AvaloniaFact]
    public async Task View_RendersTheEmptyState_WhenThereAreNoFiles()
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync();
        var (_, view) = Show(page);
        Dispatcher.UIThread.RunJobs();

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();

        Assert.True(page.IsEmpty);
        Assert.Contains(LogViewerViewModel.NoFilesMessage, texts);
    }

    [AvaloniaFact]
    public async Task View_HasTwoSeparateLevelPickers()
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync(initialCaptureLevel: LogLineLevel.Information);
        var (_, view) = Show(page);

        var pickers = view.GetVisualDescendants().OfType<ComboBox>().ToList();

        Assert.Equal(2, pickers.Count);

        var show = Assert.Single(pickers, picker => picker.Name == "ShowLevelPicker");
        var capture = Assert.Single(pickers, picker => picker.Name == "CaptureLevelPicker");

        // Not the same control and not the same value: "Show" narrows what is displayed, "Capture"
        // is general.log_level and changes what Serilog records from now on (spec 12.7).
        Assert.NotSame(show, capture);
        Assert.Equal(LogLineLevel.Verbose, show.SelectedItem);
        Assert.Equal(LogLineLevel.Information, capture.SelectedItem);

        page.MinimumLevel = LogLineLevel.Error;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(LogLineLevel.Error, show.SelectedItem);
        Assert.Equal(LogLineLevel.Information, capture.SelectedItem);
        Assert.Equal("Information", fixture.Settings.GetGeneral().LogLevel);
    }

    // Phase 14B Task 7 (PAR-012, PAR-016). Section 6's four cardinality assertions restated inside
    // this file, so they fail in the task-filtered run rather than only in the full suite.

    [AvaloniaFact]
    public async Task View_StillHasExactlyTwoComboBoxes()
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync();
        var (_, view) = Show(page);

        var pickers = view.GetVisualDescendants().OfType<ComboBox>().ToList();

        Assert.Equal(2, pickers.Count);
    }

    [AvaloniaFact]
    public async Task View_TheThreeEditorsAreBesideTheCaptureLevel()
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync();
        var (_, view) = Show(page);

        var capturePicker = view.GetControl<ComboBox>("CaptureLevelPicker");
        var activityBox = view.GetControl<NumericUpDown>("ActivityRetentionBox");
        var appLogBox = view.GetControl<NumericUpDown>("AppLogRetentionBox");
        var maxRowsBox = view.GetControl<NumericUpDown>("AppLogMaxRowsBox");

        Assert.NotNull(capturePicker);
        Assert.NotNull(activityBox);
        Assert.NotNull(appLogBox);
        Assert.NotNull(maxRowsBox);

        // Every retention editor is a NumericUpDown, never a ComboBox (section 7.1).
        Assert.IsType<NumericUpDown>(activityBox);
        Assert.IsType<NumericUpDown>(appLogBox);
        Assert.IsType<NumericUpDown>(maxRowsBox);
    }

    [AvaloniaFact]
    public async Task View_TheActionBandHasSaveAndClear()
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync();
        var (_, view) = Show(page);

        Assert.NotNull(view.GetControl<Button>("SaveLogAsButton"));
        Assert.NotNull(view.GetControl<Button>("ClearLogButton"));
    }

    [AvaloniaFact]
    public async Task View_TheClearNotice_NamesWhatWillGo()
    {
        using var fixture = new LogViewerFixture();
        fixture.WriteLogFile("galactilog-20260910.log", LogViewerFixture.Entry("09:00:00.000"));
        using var page = await fixture.CreateAsync();
        var (_, view) = Show(page);

        var notice = view.GetControl<TextBlock>("ClearLogNoticeText");
        Assert.Equal(page.ClearLogNotice, notice.Text);
        Assert.NotNull(notice.Text);
    }

    [AvaloniaFact]
    public void View_DeclaresNoCornerRadius()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.App", "Views", "LogViewerView.axaml"));

        Assert.DoesNotContain("CornerRadius", markup, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task View_StillHasItsThreeHelpGlyphs()
    {
        using var fixture = new LogViewerFixture();
        using var page = await fixture.CreateAsync();
        var (_, view) = Show(page);

        var topics = view.GetVisualDescendants().OfType<HelpButton>().Select(b => b.Topic).ToList();

        Assert.Contains("diagnostics.log-viewer", topics);
        Assert.Contains("logviewer.show", topics);
        Assert.Contains("logviewer.capture", topics);
        Assert.Equal(3, topics.Count);
    }

    // Verification E2. The save dialog used to carry no start location at all, so it opened
    // wherever any picker in the process was last used; in the launched session that was a scan
    // root, and five default-named log files landed in the fixture library. The options are built
    // apart from the dialog so this can read what they carry, and the folder they carry is the one
    // DocumentsFolderAsync answers with, never a path this application composed.
    [AvaloniaFact]
    public async Task SaveLogAs_TheSaveOptions_OpenAtTheFolderTheyAreGiven()
    {
        var window = new Window { Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var documents = TopLevel.GetTopLevel(window)?.StorageProvider is { } storage
            ? await SaveDialogStart.DocumentsAsync(storage)
            : null;

        var options = LogViewerView.SaveOptions(documents);

        Assert.Same(documents, options.SuggestedStartLocation);
        Assert.Equal("Save log as", options.Title);
        Assert.Equal("txt", options.DefaultExtension);
        Assert.True(options.ShowOverwritePrompt);
        Assert.EndsWith(".txt", options.SuggestedFileName, StringComparison.Ordinal);

        // The field exists and is the dialog's, so a start location that is null is the platform's
        // own default rather than "the last folder any picker used".
        Assert.Null(LogViewerView.SaveOptions(null).SuggestedStartLocation);
    }

    // The source half, because a headless storage provider answers no well-known folder and so
    // cannot show which one production asks for: Documents, through the provider's own API, and
    // not a path composed from Environment.GetFolderPath or from any setting this application
    // stores (a scan root is exactly such a setting).
    [AvaloniaFact]
    public void SaveLogAs_AsksTheProviderForDocuments_NotForAComposedPath()
    {
        // Comments stripped first: the remarks on DocumentsFolderAsync name the two things this
        // deliberately does not do, and a note about them is not a call to them.
        var source = SourceScan.StripComments(File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.App", "Views", "LogViewerView.axaml.cs")));

        Assert.Contains("SaveDialogStart.DocumentsAsync", source, StringComparison.Ordinal);
        Assert.Contains("SuggestedStartLocation = startLocation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetFolderPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ScanRoots", source, StringComparison.Ordinal);

        // The folder itself is resolved once, in the shared spine both dialogs reach.
        var spine = SourceScan.StripComments(File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "GalactiLog.App", "Views", "SaveDialogStart.cs")));

        Assert.Contains("WellKnownFolder.Documents", spine, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetFolderPath", spine, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GalactiLog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("GalactiLog.sln not found above test output directory.");
    }
}
