using System.Text.RegularExpressions;
using GalactiLog.App.Tests.TestSupport;
using Xunit;

namespace GalactiLog.App.Tests.Services;

/// <summary>
/// The source-scan census for spec 12.13's export page (<c>task5a.md</c> section 10.11), in the
/// shape <c>ExportWriterContainmentTests</c> established: plain text passes over <c>src/**</c>
/// with comments stripped.
/// </summary>
/// <remarks>
/// <para>
/// This is the assertion half of the <c>FileSafetyTest</c> allowlist entry that landed with this
/// page. That entry says which files <b>may</b> name <c>AppWriter.BeginExport</c>; these cases say
/// which files <b>do</b>.
/// </para>
/// <para>
/// Every needle here has been seen to fire on a real offender (TRACKING item 31): a scan rule that
/// has never failed may be scanning nothing, which is the Phase 15A <c>HasColumnName</c> shape
/// where a verification grep assumed a call the codebase does not make and reported every column
/// missing. <see cref="EveryNeedleFiresOnItsOwnOffender"/> proves each one against text that
/// contains it.
/// </para>
/// </remarks>
public class WbppExportContainmentTests
{
    private const string Page = "WbppExportViewModel.cs";

    private static readonly string ViewModelFolder = Path.Combine(
        SourceScan.SrcRoot(), "GalactiLog.App", "ViewModels", "TargetDetail", "Wbpp");

    private static readonly string ViewFolder = Path.Combine(
        SourceScan.SrcRoot(), "GalactiLog.App", "Views", "TargetDetail", "Wbpp");

    private static string PageText() => TextOf(PagePath());

    private static string PagePath() => Path.Combine(ViewModelFolder, Page);

    private static string TextOf(string path) => SourceScan.StripComments(File.ReadAllText(path));

    /// <summary>Every view-model of this page, by enumerating its folder rather than from a
    /// hand-written list, so a seventh file joins the census by existing (review P2-4).</summary>
    private static IEnumerable<string> WbppViewModels() => FilesIn(ViewModelFolder);

    private static IEnumerable<string> FilesIn(string folder) => SourceScan
        .EnumerateSourceFiles()
        .Where(file => string.Equals(Path.GetDirectoryName(file), folder, StringComparison.OrdinalIgnoreCase))
        .OrderBy(file => file, StringComparer.Ordinal);

    [Fact]
    public void BeginExport_IsCalledByExactlyTwoFiles()
    {
        // The needle is the FileSafetyTest group's own, which matches a CALL and not a
        // declaration, so AppWriter.cs, which declares the member and never calls it, does not
        // appear. The allowlist still names all three, because the exemption is decided per file
        // and the declaring file is scanned like every other.
        var files = SourceScan.FilesMatching(@"\.BeginExport\s*\(", RegexOptions.None);

        Assert.Equal(new[] { "DiagnosticsService.cs", Page }, files);
    }

    [Fact]
    public void ThePageNamesBeginExportExactlyOnce()
    {
        Assert.Single(Regex.Matches(PageText(), @"\.BeginExport\s*\("));
    }

    [Fact]
    public void TheWbppViewModelsNameNoSystemTextJsonMember()
    {
        // Seam-review ruling 7: all four wbpp keys are read through the tolerant readers Task 3a
        // owns, and this page never parses a settings value itself. A failure looks like a second,
        // differently tolerant reader growing beside the one the settings cases pin. The folder
        // entire, so QualityPanelViewModel.cs's own "no System.Text.Json member is named here"
        // sentence is held by a case rather than by its author (review P2-4).
        foreach (var file in WbppViewModels())
        {
            var text = TextOf(file);
            foreach (var forbidden in new[]
                     {
                         "System.Text.Json", "JsonElement", "TryGetProperty", "JsonSerializer",
                         "GetProperty", "ValueKind",
                     })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TheWbppViewModelsComposeNoSecondRigLabel()
    {
        // Seam-review ruling 1: FrameRow.Rig is the port's one spelling and this page copies it
        // through. A failure looks like a rig key composed here that disagrees with the label the
        // rest of the target page shows, which splits one rig's stored filter across two slots.
        foreach (var file in WbppViewModels())
        {
            var text = TextOf(file);
            Assert.DoesNotContain("\" / \"", text, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"\$""\{\s*telescope", RegexOptions.IgnoreCase), text);
            Assert.DoesNotContain("Telescope", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Camera", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePageComposesNoExportPath()
    {
        // The path comes from the picker and from nowhere else: no Path.Combine into a
        // destination, no well-known folder, and no literal file name for the write. The suggested
        // name is ScriptGenerator.FileNameFor's.
        var text = PageText();

        foreach (var forbidden in new[]
                 {
                     "Path.Combine", "Environment.GetFolderPath", "SpecialFolder",
                     "Path.GetTempPath", "AppDataRoot",
                 })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }

        Assert.DoesNotMatch(new Regex("\"[^\"]*\\.ps1\""), text);
        Assert.DoesNotMatch(new Regex("\"[^\"]*\\.sh\""), text);
    }

    [Fact]
    public void NoFileOfThisPageNamesADestructiveVerbOrAnyOtherIo()
    {
        // The application copies nothing: the script the user runs performs the copy. Departure
        // from the brief's literal needle list, and the reason is in the assertion itself: bare
        // "Copy" occurs in FolderLevels.CopyOperations and in the page's own Copy script command,
        // both of which the brief requires, so the needles are call shapes rather than words.
        //
        // The scan runs over both Wbpp folders entire and over the dialog service, not over the
        // page alone: FileSafetyTest forbids only write-capable calls, so a File.Exists or a
        // Directory.EnumerateFiles probe on a user path in any file of this page would otherwise
        // fail no case in the solution. There is no offender today; this closes the second-copy
        // gap for every file of the page rather than for three of them (review P2-4).
        foreach (var file in EveryFileOfThisPage())
        {
            var text = TextOf(file);
            foreach (var forbidden in new[]
                     {
                         "File.", "Directory.", "StreamWriter", "FileInfo", "DirectoryInfo",
                     })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
            }

            foreach (var verb in new[] { "Move", "Copy", "Delete", "Rename" })
            {
                Assert.DoesNotMatch(new Regex(@"(?<![A-Za-z])" + verb + @"\s*\("), text);
            }
        }
    }

    /// <summary>Every source file of this page: both <c>Wbpp</c> folders entire, by enumeration,
    /// plus the dialog service that opens the save picker for it. The two code-behinds and the
    /// three view-models the census used to miss are in here by construction, and so is the next
    /// file added to either folder (review P2-4).</summary>
    private static IEnumerable<string> EveryFileOfThisPage() => FilesIn(ViewModelFolder)
        .Concat(FilesIn(ViewFolder))
        .Append(Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Services", "WbppExportDialogService.cs"));

    [Fact]
    public void TheCensusedSetIsBothFoldersAndNotAnEmptyWalk()
    {
        // An enumeration that resolved to a folder that is not there passes every census above
        // vacuously, which is the Phase 15A HasColumnName shape this file's remarks name. These
        // three are the members that must be in the set whatever else joins it.
        Assert.Contains(Page, WbppViewModels().Select(Path.GetFileName));
        Assert.Contains("QualityPanelViewModel.cs", WbppViewModels().Select(Path.GetFileName));
        Assert.Contains("WbppExportWindow.axaml.cs", EveryFileOfThisPage().Select(Path.GetFileName));

        // Every censused path is a file on disk, so no member of the set is silently skipped.
        Assert.All(EveryFileOfThisPage(), path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public void ThePageNamesNoActivityRepositoryAndNoActivityWrite()
    {
        // Spec 12.13: the page writes no activity event on any path, the same rule Copy frame list
        // follows.
        var text = PageText();

        foreach (var forbidden in new[] { "ActivityRepository", "ActivityEvent", "RecordActivity" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePageComputesNoDestinationPathLength()
    {
        // Ruling R7's arithmetic is FolderLevels.LongestDestinationLength's, per chosen level with
        // that level's own entry name, and the page takes the maximum. A failure looks like the
        // page pairing the longest entry name with the longest relative path across levels, which
        // reports a destination no copy would ever create.
        var text = PageText();

        Assert.Contains("FolderLevels.LongestDestinationLength", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"Path\.\w+\([^)]*\)\.Length"), text);
    }

    [Fact]
    public void ThePageDeclaresNoConfigureAwaitFalse()
    {
        // Every await in this view-model stays on the captured context. A continuation off the UI
        // thread reaching an observable write or a CanExecute notification is the Phase 14B Clear
        // log crash, which the headless harness does not reproduce.
        Assert.DoesNotContain("ConfigureAwait(false)", PageText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageMakesNoSecondPathContainmentTest()
    {
        // One rule, PathConfinement.IsUnderOrEqual, in both directions. A new StartsWith over a
        // path or a separator-appending prefix test here is a design lesson 1 finding.
        var text = PageText();

        Assert.Contains("PathConfinement.IsUnderOrEqual", text, StringComparison.Ordinal);
        Assert.DoesNotContain("StartsWith", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectorySeparatorChar", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryNeedleFiresOnItsOwnOffender()
    {
        // TRACKING item 31. Each needle is proven against source text that contains what it hunts,
        // so none of them ships inert.
        Assert.Matches(new Regex(@"\.BeginExport\s*\("), "using var w = writer.BeginExport(path);");
        Assert.Contains("Directory.", "foreach (var f in Directory.EnumerateFiles(root))", StringComparison.Ordinal);
        Assert.Contains("File.", "if (File.Exists(path))", StringComparison.Ordinal);
        Assert.Matches(new Regex(@"(?<![A-Za-z])Copy\s*\("), "File.Copy(a, b);");
        Assert.Matches(new Regex(@"(?<![A-Za-z])Delete\s*\("), "Directory.Delete(a);");
        Assert.Matches(new Regex(@"(?<![A-Za-z])Move\s*\("), "File.Move(a, b);");
        Assert.Matches(new Regex(@"(?<![A-Za-z])Rename\s*\("), "store.Rename(a);");
        Assert.Matches(new Regex(@"Path\.\w+\([^)]*\)\.Length"), "var n = Path.Combine(a, b).Length;");
        Assert.Matches(new Regex(@"\$""\{\s*telescope", RegexOptions.IgnoreCase), "var r = $\"{telescope} / {camera}\";");

        // And the words that must not fire on the page's own legitimate members.
        Assert.DoesNotMatch(
            new Regex(@"(?<![A-Za-z])Copy\s*\("),
            "FolderLevels.CopyOperations(chosen, excluded);");
        Assert.DoesNotMatch(new Regex(@"(?<![A-Za-z])Copy\s*\("), "CopyScriptCommand;");
    }
}
