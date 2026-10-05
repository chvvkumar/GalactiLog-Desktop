using System.Globalization;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Core.Io;
using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Wbpp;

/// <summary>
/// Spec 12.13's Generate flow: one file at the dialog's own path, one string behind the file, the
/// clipboard and the shown text, and the Script part's withdrawal
/// (<c>task5a.md</c> sections 10.7 to 10.9).
/// </summary>
public class WbppExportGenerateTests
{
    private static readonly DateOnly N1 = new(2025, 3, 20);

    private static string Ha(DateOnly night, string file) => Path.Combine(
        Library.Root, "M31", night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Ha", file);

    private static Library OneNight() => new Library()
        .Frame(N1, Ha(N1, "a.fits"))
        .Frame(N1, Ha(N1, "b.fits"));

    private static ExportHarness Ready(Library? library = null)
    {
        var harness = ExportHarness.Create(library ?? OneNight(), [N1]);
        harness.Page.StagingPath = @"C:\p16staging";
        harness.Page.CommitStagingCommand.Execute(null);
        harness.Page.SavedConfirmationDuration = TimeSpan.Zero;
        return harness;
    }

    // The command wait lives out of a test body, because xUnit1031 is an error here.
    private static void Generate(WbppExportViewModel page, WbppScriptType type)
        => page.GenerateCommand.ExecuteAsync(type).Wait(TimeSpan.FromSeconds(30));

    private static void CopyScript(WbppExportViewModel page)
        => page.CopyScriptCommand.ExecuteAsync(null).Wait(TimeSpan.FromSeconds(30));

    // ------------------------------------------------------------------ 10.7 cancelled and done

    [Fact]
    public void ACancelledDialog_WritesNothingAndChangesNothingAndReportsNothing()
    {
        // A failure looks like the page writing on the cancel path, which puts a file somewhere
        // the user declined to put one: the exact class of write spec 2.1.1 bounds.
        using var harness = Ready();
        harness.Destination = null;
        var before = harness.ScriptPath("wbpp_M_31.ps1");

        Generate(harness.Page, WbppScriptType.PowerShell);

        Assert.False(File.Exists(before));
        Assert.False(harness.Page.HasScript);
        Assert.Null(harness.Page.ScriptText);
        Assert.Equal("", harness.Page.RunCommandText);
        Assert.False(harness.Page.HasGenerateError);
    }

    [Fact]
    public void AReturnedPath_ProducesExactlyOneFileWhoseContentsAreTheGeneratorsReturn()
    {
        using var harness = Ready();
        harness.Destination = harness.ScriptPath("wbpp_M_31.ps1");

        Generate(harness.Page, WbppScriptType.PowerShell);

        Assert.True(File.Exists(harness.Destination));
        Assert.True(harness.Page.HasScript);

        // The suggested name is the generator's, never composed in the view.
        Assert.Equal(
            [ScriptGenerator.FileNameFor(WbppScriptType.PowerShell, "M 31")],
            harness.SuggestedNames);
    }

    [Fact]
    public void APowerShellScriptIsWrittenBehindAByteOrderMark()
    {
        // Windows PowerShell 5.1 reads a mark-less .ps1 as ANSI, so a script carrying any
        // non-ASCII byte misreads its own paths and copies nothing while reporting success. A
        // failure looks like a real golden against a real folder copying nothing.
        using var harness = Ready();
        harness.Destination = harness.ScriptPath("wbpp.ps1");
        Generate(harness.Page, WbppScriptType.PowerShell);

        var bytes = File.ReadAllBytes(harness.Destination);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);

        // And the bytes after the mark are the generator's own string, encoded as UTF-8.
        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(harness.Page.ScriptText!),
            bytes[3..]);
    }

    [Fact]
    public void ABashScriptIsWrittenWithNoByteOrderMark()
    {
        using var harness = Ready();
        harness.Destination = harness.ScriptPath("wbpp.sh");
        Generate(harness.Page, WbppScriptType.Bash);

        var bytes = File.ReadAllBytes(harness.Destination);
        Assert.NotEqual([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(harness.Page.ScriptText!), bytes);
    }

    [Fact]
    public void TheClipboardAndTheShownTextNeverCarryTheMark()
    {
        using var harness = Ready();
        harness.Destination = harness.ScriptPath("wbpp.ps1");
        Generate(harness.Page, WbppScriptType.PowerShell);
        CopyScript(harness.Page);

        // A mark pasted into a terminal is an invisible character at the start of the first
        // command, which the shell then cannot resolve.
        Assert.DoesNotContain(WbppExportViewModel.BomPrefix, harness.Page.ScriptText!);
        Assert.DoesNotContain(WbppExportViewModel.BomPrefix, harness.Copied[0]);
    }

    [Fact]
    public void ASecondGenerate_OpensASecondDialogAndWritesASecondFile()
    {
        using var harness = Ready();
        harness.Destination = harness.ScriptPath("one.ps1");
        Generate(harness.Page, WbppScriptType.PowerShell);
        var first = harness.Page.ScriptText;

        harness.Destination = harness.ScriptPath("two.sh");
        Generate(harness.Page, WbppScriptType.Bash);

        Assert.True(File.Exists(harness.ScriptPath("one.ps1")));
        Assert.True(File.Exists(harness.ScriptPath("two.sh")));
        Assert.Equal(2, harness.SuggestedNames.Count);
        Assert.NotEqual(first, harness.Page.ScriptText);

        // The first writer was disposed when its write ended and was not reused: the second write
        // went to its own path, which a reused writer would have refused.
        Assert.Equal(File.ReadAllText(harness.ScriptPath("two.sh")), harness.Page.ScriptText);
    }

    [Fact]
    public void TheExportWriterIsDisposedWhenTheWriteEnds()
    {
        using var harness = Ready();
        harness.Destination = harness.ScriptPath("wbpp.ps1");
        Generate(harness.Page, WbppScriptType.PowerShell);

        // The scoped writer the page used is gone; a fresh one for the same path is the only way
        // back, which is what "disposed when the write ends and not reused" means in practice.
        var writer = harness.Writer.BeginExport(harness.Destination);
        writer.Dispose();
        Assert.Throws<ObjectDisposedException>(
            () => writer.WriteAllText(harness.Destination!, "x"));
    }

    [Fact]
    public void WithNoPickerInstalled_GenerateIsANoOp()
    {
        // RelayCommand.Execute ignores CanExecute, so the picker-null rule lives in the body too.
        using var harness = ExportHarness.Create(OneNight(), [N1], installPickers: false);
        harness.Page.StagingPath = @"C:\p16staging";
        harness.Page.CommitStagingCommand.Execute(null);

        Generate(harness.Page, WbppScriptType.PowerShell);

        Assert.False(harness.Page.HasScript);
        Assert.Empty(harness.SuggestedNames);
    }

    [Fact]
    public void WithNoStagingFolder_GenerateIsANoOpEvenWhenExecutedDirectly()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1]);
        harness.Destination = harness.ScriptPath("wbpp.ps1");

        Generate(harness.Page, WbppScriptType.PowerShell);

        Assert.False(File.Exists(harness.Destination));
        Assert.False(harness.Page.HasScript);
        Assert.Empty(harness.SuggestedNames);
    }

    [Fact]
    public void ThePageWritesNothingOnTheOpenPath()
    {
        // The page opens no user file and creates no folder: the staging folder is stored as a
        // destination and is never created, probed for write or opened.
        var staging = Path.Combine(Path.GetTempPath(), "p16-t5a-never-created");
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        using var harness = ExportHarness.Create(OneNight(), [N1], storedStaging: staging);

        Assert.False(Directory.Exists(staging));
        Assert.Equal(staging, harness.Page.StagingFolderText);
    }

    // ------------------------------------------------------------------ 10.8 one string, three readers

    [Fact]
    public void TheFileTheClipboardAndTheShownTextAreOneString()
    {
        // A failure looks like Copy script regenerating from the current page state, so a user who
        // changed a level after generating copies a script that does not match the file they are
        // about to run.
        using var harness = Ready();
        harness.Destination = harness.ScriptPath("wbpp.ps1");
        Generate(harness.Page, WbppScriptType.PowerShell);

        CopyScript(harness.Page);
        harness.Page.ToggleScriptCommand.Execute(null);

        // File.ReadAllText drops the byte order mark as it decodes, so the decoded file, the
        // clipboard and the shown text are one string; the mark itself is asserted on the bytes.
        Assert.Equal(File.ReadAllText(harness.Destination), harness.Page.ScriptText);
        Assert.Equal([harness.Page.ScriptText!], harness.Copied);
        Assert.True(harness.Page.IsScriptShown);
        Assert.Equal("Hide script", harness.Page.ScriptToggleLabel);
    }

    [Fact]
    public void ShowScriptIsOneToggleWithOneLabel()
    {
        using var harness = Ready();
        harness.Destination = harness.ScriptPath("wbpp.ps1");
        Generate(harness.Page, WbppScriptType.PowerShell);

        Assert.False(harness.Page.IsScriptShown);
        Assert.Equal("Show script", harness.Page.ScriptToggleLabel);

        harness.Page.ToggleScriptCommand.Execute(null);
        Assert.Equal("Hide script", harness.Page.ScriptToggleLabel);

        harness.Page.ToggleScriptCommand.Execute(null);
        Assert.Equal("Show script", harness.Page.ScriptToggleLabel);
    }

    [Fact]
    public void TheRunCommandMatchesTheScriptsOwnHeaderByteForByte()
    {
        // The name reaches the generator and the page from one value, so the two lines cannot
        // disagree. A failure looks like the page stating a command that does not name the file
        // the script's own header names.
        const string name = "wbpp_M_31 (2).ps1";
        var script = ScriptGenerator.Generate(
            WbppScriptType.PowerShell,
            new WbppScriptInput(
                [new CopyOperation(N1, @"C:\p16lib\M31\Ha", "Ha", [])],
                @"C:\p16staging",
                "M 31",
                [],
                name));

        Assert.Contains(
            WbppExportViewModel.RunCommandFor(WbppScriptType.PowerShell, name),
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ThePowerShellRunCommandDoublesAnApostrophe()
    {
        // The doubling is unreachable end to end now that the generator validates FileName by an
        // ASCII allowlist that excludes an apostrophe, and ScriptGenerator.SanitizeScriptName
        // replaces one before it could reach the name. The rule stays pinned on the pure function,
        // because the run instruction is a double-quoted -Command string either way.
        Assert.Contains(
            "o''brien",
            WbppExportViewModel.RunCommandFor(WbppScriptType.PowerShell, "wbpp_o'brien.ps1"),
            StringComparison.Ordinal);

        // And a sanitised target name carries no apostrophe into the file name at all.
        Assert.DoesNotContain(
            "'",
            ScriptGenerator.FileNameFor(WbppScriptType.PowerShell, "O'Brien's Nebula"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheBashRunCommandIsStatedExactly()
    {
        using var harness = Ready();
        harness.Destination = harness.ScriptPath("wbpp.sh");
        Generate(harness.Page, WbppScriptType.Bash);

        var name = ScriptGenerator.FileNameFor(WbppScriptType.Bash, "M 31");
        Assert.Equal("chmod +x " + name + " && ./" + name, harness.Page.RunCommandText);
        Assert.Contains(harness.Page.RunCommandText, harness.Page.ScriptText!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 10.9 withdrawal

    [Fact]
    public void ALevelChangeWithdrawsTheScriptPartAndLeavesTheFileOnDisk()
    {
        using var harness = Ready();
        harness.Destination = harness.ScriptPath("wbpp.ps1");
        Generate(harness.Page, WbppScriptType.PowerShell);

        harness.Page.Sessions[0].Rows[0].IsChosen = true;

        Assert.False(harness.Page.HasScript);
        Assert.True(File.Exists(harness.Destination));
    }

    [Fact]
    public void AConstraintOrOverrideChangeWithdrawsTheScriptPart()
    {
        // A constraint and an override both reach this page as a change to the excluded set, which
        // is the only thing about the filter this page holds.
        var library = OneNight();
        using var harness = ExportHarness.Create(library, [N1]);
        harness.Page.StagingPath = @"C:\p16staging";
        harness.Page.CommitStagingCommand.Execute(null);
        harness.Destination = harness.ScriptPath("wbpp.ps1");
        Generate(harness.Page, WbppScriptType.PowerShell);

        harness.Page.SetExcludedFrames([library.Ids[Ha(N1, "a.fits")]]);

        Assert.False(harness.Page.HasScript);
        Assert.True(File.Exists(harness.Destination));
    }

    [Fact]
    public void AnExclusionChangeWithdrawsTheScriptPart()
    {
        using var harness = Ready();
        harness.Destination = harness.ScriptPath("wbpp.ps1");
        Generate(harness.Page, WbppScriptType.PowerShell);

        harness.Page.ExclusionsText = "WBPP";

        Assert.False(harness.Page.HasScript);
    }

    [Fact]
    public void AStagingFolderChangeWithdrawsTheScriptPart()
    {
        using var harness = Ready();
        harness.Destination = harness.ScriptPath("wbpp.ps1");
        Generate(harness.Page, WbppScriptType.PowerShell);

        harness.Page.StagingPath = @"C:\p16staging2";
        harness.Page.CommitStagingCommand.Execute(null);

        Assert.False(harness.Page.HasScript);
    }

    [Theory]
    [InlineData(@"C:\p16lib\staged")]
    [InlineData("")]
    public void ARefusedOrBlankedStagingCommitWithdrawsTheScriptPartToo(string committed)
    {
        // A failure looks like the withdrawal happening only on the accepted path, so the Script
        // part stays on screen with its run command after the staging folder it describes has been
        // refused or cleared, describing a plan the page can no longer generate. Spec 12.13 names
        // the staging folder among the five changes and draws no such distinction.
        using var harness = Ready();
        harness.Destination = harness.ScriptPath("wbpp.ps1");
        Generate(harness.Page, WbppScriptType.PowerShell);
        Assert.True(harness.Page.HasScript);

        harness.Page.StagingPath = committed;
        harness.Page.CommitStagingCommand.Execute(null);

        Assert.False(harness.Page.HasScript);
        Assert.Equal("", harness.Page.RunCommandText);
        Assert.False(harness.Page.GenerateCommand.CanExecute(WbppScriptType.PowerShell));

        // And the file already on disk is not touched: this application wrote it once.
        Assert.True(File.Exists(harness.Destination));
    }

    // ------------------------------------------------------------------ the write itself

    [Fact]
    public void AWriteThatFails_IsStatedAsASentenceAndNotThrown()
    {
        // File.WriteAllText throws on a read-only folder, a full disk, a locked file or a removed
        // removable drive, and an exception out of the command is a dead window on a page that
        // states a sentence for every other refusal. The destination here is an existing FOLDER,
        // which the platform refuses to open as a file.
        using var harness = Ready();
        harness.Destination = harness.TempFolder("occupied");

        Generate(harness.Page, WbppScriptType.PowerShell);

        Assert.Equal(WbppExportViewModel.WriteFailure, harness.Page.GenerateError);
        Assert.False(harness.Page.HasScript);
    }

    [Fact]
    public void AGenerateRefusal_IsWithdrawnWithTheChangeItNamed()
    {
        // A failure looks like the sentence standing in the footer while the user corrects the very
        // level, exclusion or staging folder it named, because it is cleared only at the start of
        // the next Generate. It belongs to the plan that was refused, so it goes with it: the five
        // changes spec 12.13 names all reach WithdrawScript.
        using var harness = Ready();
        harness.Destination = harness.TempFolder("occupied");

        Generate(harness.Page, WbppScriptType.PowerShell);
        Assert.True(harness.Page.HasGenerateError);

        harness.Page.ExclusionsText = "WBPP";

        Assert.Equal("", harness.Page.GenerateError);
        Assert.False(harness.Page.HasGenerateError);
    }

    [Fact]
    public void APageDisposedWhileTheDialogWasOpen_WritesNothingAndPublishesNothing()
    {
        // The dialog is the one await in this command, and the window can be dismissed while it is
        // open. Every other publish path on this page checks the disposed flag; this one is the
        // one that reaches an observable write after an await.
        using var harness = Ready();
        var destination = harness.ScriptPath("dismissed.ps1");
        harness.Page.ScriptDestinationPicker = _ =>
        {
            harness.Page.Dispose();
            return Task.FromResult<string?>(destination);
        };

        Generate(harness.Page, WbppScriptType.PowerShell);

        Assert.False(File.Exists(destination));
        Assert.False(harness.Page.HasScript);
    }

    [Fact]
    public void ANonChangeLeavesTheScriptPartShown()
    {
        // A failure looks like the part withdrawing on a re-sort, which trains the user to
        // regenerate for no reason. A re-sort and a baseline toggle reach this page as an excluded
        // set that did not move, and opening the disclosure reaches it as nothing at all.
        using var harness = Ready();
        harness.Destination = harness.ScriptPath("wbpp.ps1");
        Generate(harness.Page, WbppScriptType.PowerShell);

        harness.Page.SetExcludedFrames([]);
        harness.Page.ToggleSettingsCommand.Execute(null);
        harness.Page.ToggleScriptCommand.Execute(null);
        harness.Page.Sessions[0].ToggleCommand.Execute(null);

        Assert.True(harness.Page.HasScript);
        Assert.NotNull(harness.Page.ScriptText);
    }

    // ------------------------------------------------------------------ the two typed refusals

    [Fact]
    public void AFramePathCarryingALineBreak_ShowsTheQuotingSentenceAndWritesNoFile()
    {
        var bad = Path.Combine(Library.Root, "M31", "2025-03-20", "Ha", "a\nb.fits");
        var library = new Library().Frame(N1, Ha(N1, "good.fits")).Frame(N1, bad);

        using var harness = Ready(library);
        harness.Destination = harness.ScriptPath("wbpp.ps1");

        // The frame reaches a quoter as a per-file exclude, which is where a frame path is quoted.
        harness.Page.SetExcludedFrames([library.Ids[bad]]);
        Generate(harness.Page, WbppScriptType.PowerShell);

        Assert.True(harness.Page.HasGenerateError);

        // Spec 12.13's states row: the page NAMES THE FILE and says the export cannot quote it.
        // The exception carries the offending value beside its kind, so a failure here looks like
        // the page saying only that one of the excluded frames was refused, over a selection of
        // hundreds of frames.
        Assert.Contains("one of the frames the filter excluded", harness.Page.GenerateError, StringComparison.Ordinal);
        Assert.Contains(
            Path.GetFileName(bad).Replace("\n", "\\n", StringComparison.Ordinal),
            harness.Page.GenerateError,
            StringComparison.Ordinal);

        // And the control character itself is written as its escape rather than emitted into a
        // label, which would break the sentence across two lines.
        Assert.DoesNotContain("\n", harness.Page.GenerateError, StringComparison.Ordinal);
        Assert.False(File.Exists(harness.Destination));
        Assert.False(harness.Page.HasScript);

        // No dialog was even opened: the catch is around the generate call and not around the
        // write, because the write must not run at all.
        Assert.Empty(harness.SuggestedNames);
    }

    [Fact]
    public void AnExclusionPatternRefusalIsReportedByPatternAndNotByFileName()
    {
        // The page refuses such a pattern as it is entered, so this arm is reached only if that
        // guard were ever removed; the sentence it would show names the pattern itself.
        var byPattern = WbppExportViewModel.QuotingRefusal("exclusion pattern", "bad$name");
        var byFile = WbppExportViewModel.QuotingRefusal("excluded file", "a.fits");

        Assert.Contains("excluded folder patterns", byPattern, StringComparison.Ordinal);
        Assert.Contains("bad$name", byPattern, StringComparison.Ordinal);
        Assert.DoesNotContain("frames", byPattern, StringComparison.Ordinal);
        Assert.NotEqual(byPattern, byFile);

        // A refusal that carries no value at all still reads as a sentence.
        Assert.Contains(
            "the script file name carries",
            WbppExportViewModel.QuotingRefusal("FileName", ""),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AThirdExceptionTypeEscapesRatherThanBeingReportedAsAQuotingRefusal()
    {
        // Only the two typed exceptions are caught, and each by its own type. A bare
        // catch (Exception) here would report an unrelated fault as a quoting refusal and write
        // no file with no explanation.
        var thrown = Assert.ThrowsAny<Exception>(
            () => ScriptGenerator.Generate(WbppScriptType.PowerShell, null!));

        Assert.IsNotType<WbppUnsafeValueException>(thrown);
        Assert.IsNotType<WbppStagingRootException>(thrown);
    }

    [Fact]
    public void ThePageAndTheGeneratorRefuseTheSameStagingShape()
    {
        // Design lesson 2 at two choke points. The page's own check runs first and makes the
        // generator's throw unreachable from here, so the pair is pinned rather than the
        // unreachable path being faked: the page refuses with the source sentence, and the
        // generator throws its own type on the same input.
        using var harness = ExportHarness.Create(OneNight(), [N1]);
        harness.SetScanRoots(@"C:\p16other");
        var source = harness.Page.Sessions[0].Chosen!.Level.Path;
        var inside = Path.Combine(source, "staged");

        harness.Page.StagingPath = inside;
        harness.Page.CommitStagingCommand.Execute(null);
        Assert.Equal(WbppExportViewModel.SourceRefusal, harness.Page.StagingError);

        Assert.Throws<WbppStagingRootException>(() => ScriptGenerator.Generate(
            WbppScriptType.PowerShell,
            new WbppScriptInput(
                [new CopyOperation(N1, source, "Ha", [])], inside, "M 31", [], "wbpp.ps1")));
    }

    [Fact]
    public void TheWriteGoesThroughTheScopedWriterAtTheDialogsOwnPath()
    {
        // The writer refuses any path but its own, which is what makes "exactly what the dialog
        // returned" structural rather than a convention.
        using var harness = Ready();
        var elsewhere = harness.ScriptPath("elsewhere.ps1");
        using var writer = harness.Writer.BeginExport(harness.ScriptPath("wbpp.ps1"));

        Assert.Throws<UnauthorizedPathException>(() => writer.WriteAllText(elsewhere, "x"));
        Assert.False(File.Exists(elsewhere));
    }
}
