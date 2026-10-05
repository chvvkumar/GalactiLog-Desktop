using System.Text.RegularExpressions;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Core.Io;
using Xunit;

namespace GalactiLog.App.Tests.Services;

/// <summary>
/// The roadmap's Phase 10 row 3 Verify line, second and third clauses: the scoped writer refuses
/// any other path and refuses everything after disposal, and no code path calls
/// <c>ExportBundle</c> with a path a dialog did not return.
/// </summary>
/// <remarks>
/// <para>
/// The three source scans are plain text passes with a <see cref="Regex"/>, exactly as
/// <c>FileSafetyTest</c> does, and they resolve the <c>src</c> directory the same way. No Roslyn
/// dependency: a regex pass is enough for a codebase this size.
/// </para>
/// <para>
/// The four writer cases assert behaviour that already exists in <c>AppWriter.cs</c>. They are
/// written here because Phase 1 shipped <c>BeginExport</c> with no caller and therefore no
/// end-to-end coverage. <c>AppWriter.cs</c> is not edited to make them pass.
/// </para>
/// </remarks>
public class ExportWriterContainmentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("galactilog-exportwriter-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private AppWriter Writer() => new(_root);

    // ---------------------------------------------------------------- the source-scan census

    [Fact]
    public void ExportBundle_IsCalledFromExactlyOneViewModelCommand()
    {
        // Case-insensitive, because the one caller reaches the member through the
        // Action<string> seam the page takes (`exportBundle(destination)`), exactly as it reaches
        // the snapshot through a Func<DiagnosticsSnapshot>. AppHost binds the method group with
        // no argument list, so it is a binding rather than a call site and does not match.
        var files = SourceScan.FilesMatching(@"ExportBundle\s*\(", RegexOptions.IgnoreCase);

        Assert.Equal(
            new[] { "DiagnosticsService.cs", "DiagnosticsViewModel.cs" },
            files);
    }

    [Fact]
    public void DiagnosticsViewModel_ComposesNoExportPath()
    {
        var text = SourceScan.StripComments(File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "ViewModels", "Diagnostics", "DiagnosticsViewModel.cs")));

        foreach (var forbidden in new[] { "Path.Combine", "Path.GetFullPath", "AppDataRoot" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }

        // No suggested file name lives here either: it belongs to the dialog options in the
        // view's code-behind. The only path this view-model holds is the one the seam returned.
        Assert.DoesNotMatch(new Regex("\"[^\"]*\\.json\""), text);
    }

    [Fact]
    public void LogViewerViewModel_ComposesNoExportPath()
    {
        // The Save log as twin of DiagnosticsViewModel_ComposesNoExportPath above (PAR-016): the
        // only path this view-model ever holds is the one SaveLogAsDestinationPicker returned.
        var text = SourceScan.StripComments(File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "ViewModels", "Diagnostics", "LogViewerViewModel.cs")));

        foreach (var forbidden in new[] { "Path.Combine", "Path.GetFullPath", "AppDataRoot" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SaveFilePicker_HasExactlyThreeExportPathSources()
    {
        // Phase 14B Task 7 (PAR-016) adds a second, equally sanctioned save dialog: spec 12.8 step
        // 1 names the platform save dialog as the source of an export path, and it says that once
        // for the diagnostics bundle and again for "Save log as", not once for the whole
        // application. LogViewerView.axaml.cs is that second file; DiagnosticsView.axaml.cs stays
        // the bundle's. Renamed from SaveFilePicker_IsTheOnlyExportPathSource, which the log
        // viewer's own second source falsified (review finding, cheap rename).
        //
        // Phase 16 Task 5a adds the third and last: spec 12.13's export script, whose window
        // resolves the start location to the committed staging folder, which the page's own
        // confinement rule can never let be a scan root. The set stays EXACT and NAMED: a fourth
        // save dialog fails this rather than shipping without a start location, which is how five
        // default-named log files landed in a fixture library.
        var files = SourceScan.FilesMatching(@"SaveFilePickerAsync", RegexOptions.None);

        Assert.Equal(
            new[] { "DiagnosticsView.axaml.cs", "LogViewerView.axaml.cs", "WbppExportWindow.axaml.cs" },
            files);

        // Verification E2, ruled over both seams. A save dialog with no SuggestedStartLocation
        // opens wherever any picker in the process was last used, and the only other pickers here
        // are the Library tab's and the setup wizard's folder pickers, so in practice that is a
        // SCAN ROOT: five default-named log files landed in a fixture library that way. The rule
        // is enforced at the census rather than remembered at each call site, so a third save
        // dialog fails this rather than shipping without one (design-lessons rule 2). A source
        // read, because neither picker is reachable headless: the headless top level offers no
        // storage provider at all, so both return null before any options are built (TRACKING
        // section 6 item 29's shape).
        foreach (var file in files)
        {
            // Resolved by file name through the source walk rather than by a composed
            // Views/<file> path: the third source lives at Views/TargetDetail/Wbpp/, and a
            // composed path would throw there and take the loop with it.
            var source = SourceScan.StripComments(File.ReadAllText(SourceFileNamed(file)));

            Assert.Contains("SuggestedStartLocation = startLocation", source, StringComparison.Ordinal);
            Assert.Contains("SaveDialogStart.DocumentsAsync", source, StringComparison.Ordinal);
        }

        // And the folder itself is resolved in exactly one place, so the two dialogs cannot start
        // disagreeing about where a save begins (design-lessons rule 1: this was the second
        // occurrence of the pattern and it became a shared spine rather than a copy).
        Assert.Equal(
            new[] { "SaveDialogStart.cs" },
            SourceScan.FilesMatching(@"WellKnownFolder\.Documents", RegexOptions.None));
    }

    [Fact]
    public void DestinationPicker_IsInstalledByTheViewThatOwnsIt()
    {
        // Each dialog seam is a settable property, because the view-model is built before any view
        // exists. That widens the surface, so its assignment sites are censused like every other
        // seam here (review finding F2).
        //
        // One EXACT NAMED set per seam, rather than one set over the shared "DestinationPicker"
        // substring: three exact sets still fail when a further file assigns any of them, and they
        // additionally fail when the wrong file assigns the wrong seam, which the substring form
        // cannot see. The bundle and the log seams take two files each, the view that installs and
        // clears the picker and the view-model whose constructor assigns the parameter; spec
        // 12.13's export page takes one, because its constructor takes no picker at all.
        // The fourth needle is the page's folder picker, which no "DestinationPicker" needle
        // catches at all and which was therefore censused by nothing.
        var seams = new[]
        {
            (Needle: @"\bDestinationPicker\s*=",
                Files: new[] { "DiagnosticsView.axaml.cs", "DiagnosticsViewModel.cs" }),
            (Needle: @"SaveLogAsDestinationPicker\s*=",
                Files: new[] { "LogViewerView.axaml.cs", "LogViewerViewModel.cs" }),
            (Needle: @"ScriptDestinationPicker\s*=",
                Files: new[] { "WbppExportWindow.axaml.cs" }),
            (Needle: @"StagingFolderPicker\s*=",
                Files: new[] { "WbppExportWindow.axaml.cs" }),
        };

        // Compared as one value rather than four assertions in a row, so a further file assigning
        // any seam fails this case rather than being hidden behind the first one that still holds.
        Assert.Equal(
            seams.Select(seam => seam.Files),
            seams.Select(seam => SourceScan.FilesMatching(seam.Needle, RegexOptions.None)));
    }

    // The one file under src/** with this name. A census element is resolved through the source
    // walk rather than by a composed directory path, so a file that moves fails the census it
    // belongs to instead of throwing out of an unrelated assertion.
    private static string SourceFileNamed(string fileName)
        => SourceScan.EnumerateSourceFiles()
            .Single(path => Path.GetFileName(path).Equals(fileName, StringComparison.Ordinal));

    // ---------------------------------------------------------------- the scoped writer

    [Fact]
    public void ExportWriter_RefusesAPathOtherThanItsDestination()
    {
        var destination = Path.Combine(_root, "bundle.json");
        var other = Path.Combine(_root, "elsewhere.json");

        using var writer = Writer().BeginExport(destination);

        Assert.Throws<UnauthorizedPathException>(() => writer.WriteAllText(other, "{}"));
        Assert.False(File.Exists(other));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void ExportWriter_RefusesAPathUnderTheAppDataRoot_WhenThatIsNotItsDestination()
    {
        var appWriter = Writer();
        var destination = Path.Combine(_root, "bundle.json");
        var underAppData = appWriter.ResolveAppDataPath("galactilog.db");

        using var writer = appWriter.BeginExport(destination);

        // AuthorizeExact is exact, not "is under an authorized root": the scoped writer is valid
        // for one path and the app data root is not it.
        Assert.Throws<UnauthorizedPathException>(() => writer.WriteAllText(underAppData, "{}"));
        Assert.False(File.Exists(underAppData));
    }

    [Fact]
    public void ExportWriter_RefusesEveryWriteAfterDisposal()
    {
        var destination = Path.Combine(_root, "bundle.json");
        var writer = Writer().BeginExport(destination);
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.WriteAllText(destination, "{}"));
        Assert.Throws<ObjectDisposedException>(() => writer.WriteAllBytes(destination, [1, 2, 3]));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void ExportWriter_ComparesPathsCaseInsensitively_AndNormalizesRelativeSegments()
    {
        var destination = Path.Combine(_root, "bundle.json");
        var spelledDifferently = Path.Combine(_root, "sub", "..", "BUNDLE.JSON");

        using var writer = Writer().BeginExport(destination);
        writer.WriteAllText(spelledDifferently, "{}");

        Assert.Equal("{}", File.ReadAllText(destination));
        Assert.Throws<UnauthorizedPathException>(
            () => writer.WriteAllText(Path.Combine(_root, "sub", "bundle.json"), "{}"));
    }
}
