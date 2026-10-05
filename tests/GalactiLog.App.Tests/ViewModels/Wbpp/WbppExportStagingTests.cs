using System.Globalization;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Wbpp;

/// <summary>
/// Spec 12.13's staging folder: required, remembered at once, confined in both directions against
/// the scan roots and this export's own chosen source folders, and re-checked when a level moves
/// (<c>task5a.md</c> sections 10.6 and 10.12).
/// </summary>
public class WbppExportStagingTests
{
    private static readonly DateOnly N1 = new(2025, 3, 20);

    private const string Outside = @"C:\p16staging";

    private const string ShortStaging = @"C:\s";

    private static string Ha(DateOnly night, string file) => Path.Combine(
        Library.Root, "M31", night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Ha", file);

    private static Library OneNight() => new Library()
        .Frame(N1, Ha(N1, "a.fits"))
        .Frame(N1, Ha(N1, "b.fits"));

    // ------------------------------------------------------------------ unset

    [Fact]
    public void Unset_DisablesGenerateWithTheLiteralReason_AndOpensTheDisclosure()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1]);

        Assert.False(harness.Page.GenerateCommand.CanExecute(WbppScriptType.PowerShell));
        Assert.Equal("Choose a staging folder first", harness.Page.GenerateDisabledReason);
        Assert.False(harness.Page.IsGenerateAvailable);
        Assert.True(harness.Page.IsSettingsOpen);
    }

    [Fact]
    public void ALegalCommit_ClearsTheReasonAndRaisesItAsAChange()
    {
        // A failure looks like an expression property no site ever raises PropertyChanged for, so
        // the footer keeps "Choose a staging folder first" on screen for the rest of the visit
        // beside a Generate that is now enabled. It is the first thing a user does on this page.
        using var harness = ExportHarness.Create(OneNight(), [N1]);
        var raised = new List<string?>();
        harness.Page.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        harness.Page.StagingPath = Outside;
        harness.Page.CommitStagingCommand.Execute(null);

        // Null, not empty: the reason is bound to ToolTip.Tip as well as to the footer line, and
        // Avalonia shows a tooltip for any non-null Tip, so an empty string pops an empty box over
        // an enabled Generate. The same rule is written this way on the other side of the flyout,
        // in TargetDetailViewModel.NoNightCheckedHint.
        Assert.Null(harness.Page.GenerateDisabledReason);
        Assert.True(harness.Page.IsGenerateAvailable);
        Assert.Contains(nameof(WbppExportViewModel.GenerateDisabledReason), raised);
        Assert.Contains(nameof(WbppExportViewModel.IsGenerateAvailable), raised);
    }

    [Fact]
    public void Unset_LeavesTheRestOfThePageWorkingNormally()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1]);

        // The levels, the filter and the totals are all readable before a folder exists.
        Assert.NotEmpty(harness.Page.Sessions[0].Rows);
        Assert.Equal(2, harness.Page.Totals!.FrameCount);
        Assert.Equal("2 light frames", harness.Page.FrameCountText);
    }

    [Fact]
    public void AStagingFolderSetOnOpen_LeavesTheDisclosureClosed()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1], storedStaging: Outside);

        Assert.False(harness.Page.IsSettingsOpen);
        Assert.True(harness.Page.GenerateCommand.CanExecute(WbppScriptType.PowerShell));
    }

    // ------------------------------------------------------------------ remembered at once

    [Fact]
    public void AFirstChoiceThroughThePicker_WritesTheKeyInExactlyOneMutateGeneral()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1]);
        harness.PickedFolder = Outside;

        harness.Page.PickStagingFolderCommand.Execute(null);

        // Without Save as defaults being pressed at all.
        Assert.Equal(1, harness.MutateCalls);
        Assert.Equal(Outside, WbppSettingsRead.StagingPath(harness.General.WbppStagingPathDocument));
        Assert.Equal(Outside, harness.Page.StagingPath);
        Assert.True(harness.Page.GenerateCommand.CanExecute(WbppScriptType.PowerShell));
    }

    [Fact]
    public void ATypedValueCommittedOnFocusLoss_WritesTheSameWay()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1]);

        harness.Page.StagingPath = Outside;
        harness.Page.CommitStagingCommand.Execute(null);

        Assert.Equal(1, harness.MutateCalls);
        Assert.Equal(Outside, WbppSettingsRead.StagingPath(harness.General.WbppStagingPathDocument));
    }

    [Fact]
    public void ACommitThatDoesNotChangeTheValueWritesNothing()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1], storedStaging: Outside);

        harness.Page.CommitStagingCommand.Execute(null);
        harness.Page.CommitStagingCommand.Execute(null);

        Assert.Equal(0, harness.MutateCalls);
    }

    [Theory]
    [InlineData(Outside + @"\")]
    [InlineData("C:/p16staging")]
    public void TheSameFolderRespelledIsNotAChangeAndWritesNothing(string respelled)
    {
        // A failure looks like the raw strings being compared, so the same folder re-typed with a
        // trailing separator or with forward slashes writes wbpp_staging_path again and raises a
        // settings-changed event for nothing.
        using var harness = ExportHarness.Create(OneNight(), [N1], storedStaging: Outside);

        harness.Page.StagingPath = respelled;
        harness.Page.CommitStagingCommand.Execute(null);

        Assert.Equal(0, harness.MutateCalls);
        Assert.Equal("", harness.Page.StagingError);
        Assert.Equal(Outside, harness.Page.StagingFolderText);
    }

    [Fact]
    public void ACancelledFolderPickerWritesNothingAndChangesNothing()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1], storedStaging: Outside);
        harness.PickedFolder = null;

        harness.Page.PickStagingFolderCommand.Execute(null);

        Assert.Equal(0, harness.MutateCalls);
        Assert.Equal(Outside, harness.Page.StagingPath);
    }

    [Fact]
    public void WithNoPickerInstalled_ThePickCommandIsANoOp()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1], installPickers: false);

        // RelayCommand.Execute ignores CanExecute, so the guard is in the body as well.
        Assert.False(harness.Page.PickStagingFolderCommand.CanExecute(null));
        harness.Page.PickStagingFolderCommand.Execute(null);
        Assert.Equal(0, harness.MutateCalls);
    }

    // ------------------------------------------------------------------ the scan root rule

    [Theory]
    [InlineData(@"C:\p16lib\staged")]
    [InlineData(@"C:\p16lib")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\p16lib\staged\")]
    [InlineData("C:/p16lib/staged")]
    [InlineData(@"C:\p16other\..\p16lib\staged")]
    public void AFolderUnderEqualToOrContainingAScanRoot_IsRefusedWithTheScanRootSentence(string folder)
    {
        // A failure looks like the containment test running in one direction only, which lets a
        // user stage into the parent of their library and have the next scan catalogue every
        // staged copy as a second set of frames.
        //
        // The last three rows are the spellings a value that is never brought to a canonical full
        // path slips through on: PathConfinement.IsUnderOrEqual documents both arguments as
        // Path.GetFullPath results and its second clause is a prefix test, so a forward-slash form
        // or a dot-dot segment through a sibling folder reads as a folder outside the library and
        // is stored, and the generator's own guard then compares the same spelling.
        using var harness = ExportHarness.Create(OneNight(), [N1]);

        harness.Page.StagingPath = folder;
        harness.Page.CommitStagingCommand.Execute(null);

        Assert.Equal(WbppExportViewModel.ScanRootRefusal, harness.Page.StagingError);
        Assert.Equal(0, harness.MutateCalls);
        Assert.Equal(folder, harness.Page.StagingPath);
        Assert.False(harness.Page.GenerateCommand.CanExecute(WbppScriptType.PowerShell));
    }

    [Theory]
    [InlineData(@"C:\p16library")]
    [InlineData(@"C:\p16lib2")]
    public void AFolderWhoseNameMerelyStartsWithAScanRootIsNotRefused(string folder)
    {
        // The other half of the same rule, and the one a hand-rolled prefix test gets wrong:
        // C:\p16library is not inside C:\p16lib, so refusing it would refuse a legitimate staging
        // folder and leave the user no way to proceed. The canonical forms are compared at a path
        // component boundary, which is PathConfinement's own rule and never a bare StartsWith.
        using var harness = ExportHarness.Create(OneNight(), [N1]);

        harness.Page.StagingPath = folder;
        harness.Page.CommitStagingCommand.Execute(null);

        Assert.Equal("", harness.Page.StagingError);
        Assert.True(harness.Page.GenerateCommand.CanExecute(WbppScriptType.PowerShell));
        Assert.Equal(folder, harness.Page.StagingFolderText);
    }

    [Fact]
    public void TheScanRootsAreReadFreshAtEveryCommit()
    {
        // Made against the roots as configured at the moment of the commit, not against a value
        // captured when the page opened: a root added while the page is open is tested against.
        using var harness = ExportHarness.Create(OneNight(), [N1]);

        harness.Page.StagingPath = Outside;
        harness.Page.CommitStagingCommand.Execute(null);
        Assert.Equal("", harness.Page.StagingError);

        harness.SetScanRoots(Library.Root, Outside);
        harness.Page.CommitStagingCommand.Execute(null);

        Assert.Equal(WbppExportViewModel.ScanRootRefusal, harness.Page.StagingError);
    }

    // ------------------------------------------------------------------ the source folder rule

    [Fact]
    public void AFolderInsideAChosenSourceFolder_IsRefusedWithTheSourceSentence()
    {
        // A failure looks like one sentence serving both rules, which tells a user to choose a
        // folder outside their library when the folder already is outside it and the real problem
        // is that it is inside the folder being copied.
        using var harness = ExportHarness.Create(OneNight(), [N1]);
        var source = harness.Page.Sessions[0].Chosen!.Level.Path;

        // The root is removed from around the source, which is the configuration the second rule
        // exists for and the reason spec 12.13 states and tests it in its own right: a source no
        // current root covers.
        harness.SetScanRoots(@"C:\p16other");

        harness.Page.StagingPath = Path.Combine(source, "staged");
        harness.Page.CommitStagingCommand.Execute(null);

        Assert.Equal(WbppExportViewModel.SourceRefusal, harness.Page.StagingError);
        Assert.Equal(0, harness.MutateCalls);
        Assert.False(harness.Page.GenerateCommand.CanExecute(WbppScriptType.PowerShell));
    }

    [Fact]
    public void AFolderInsideAChosenSourceFolderSpelledWithForwardSlashes_IsRefusedTheSameWay()
    {
        // The same slip as the scan-root rows: without a canonical full path the second clause
        // reads a forward-slash spelling as a folder outside every source.
        using var harness = ExportHarness.Create(OneNight(), [N1]);
        var source = harness.Page.Sessions[0].Chosen!.Level.Path;
        harness.SetScanRoots(@"C:\p16other");

        harness.Page.StagingPath = Path.Combine(source, "staged").Replace('\\', '/');
        harness.Page.CommitStagingCommand.Execute(null);

        Assert.Equal(WbppExportViewModel.SourceRefusal, harness.Page.StagingError);
        Assert.Equal(0, harness.MutateCalls);
    }

    [Fact]
    public void AFolderEqualToAChosenSourceFolder_IsRefusedTheSameWay()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1]);
        var source = harness.Page.Sessions[0].Chosen!.Level.Path;
        harness.SetScanRoots(@"C:\p16other");

        harness.Page.StagingPath = source;
        harness.Page.CommitStagingCommand.Execute(null);

        Assert.Equal(WbppExportViewModel.SourceRefusal, harness.Page.StagingError);
    }

    [Fact]
    public void AFolderContainingAChosenSourceFolder_IsRefusedTheSameWay()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1]);
        harness.SetScanRoots(@"C:\p16other");

        harness.Page.StagingPath = @"C:\p16lib\M31";
        harness.Page.CommitStagingCommand.Execute(null);

        Assert.Equal(WbppExportViewModel.SourceRefusal, harness.Page.StagingError);
    }

    [Fact]
    public void AStoredValueThatHasComeToBreachTheRule_IsReportedOnOpenAndNotCleared()
    {
        using var harness = ExportHarness.Create(
            OneNight(), [N1], storedStaging: @"C:\p16lib\staged");

        Assert.Equal(WbppExportViewModel.ScanRootRefusal, harness.Page.StagingError);
        Assert.False(harness.Page.GenerateCommand.CanExecute(WbppScriptType.PowerShell));

        // Reported, never silently cleared: the value is still in the document and still in the
        // field, so the user can see what it was and correct it.
        Assert.Equal(0, harness.MutateCalls);
        Assert.Equal(
            @"C:\p16lib\staged",
            WbppSettingsRead.StagingPath(harness.General.WbppStagingPathDocument));
        Assert.Equal(@"C:\p16lib\staged", harness.Page.StagingPath);
    }

    [Fact]
    public void AValueLegalUnderOnePick_IsRefusedAfterThePickMovesShallower()
    {
        // A failure looks like the check being made once at commit and never re-run, which lets a
        // level change leave a staging folder inside the very folder the script is about to copy,
        // so the copy reads what it is writing.
        using var harness = ExportHarness.Create(OneNight(), [N1]);
        harness.SetScanRoots(@"C:\p16other");

        // Legal under the default pick, which is the Ha leaf: the folder is its sibling.
        var staging = Path.Combine(Library.Root, "M31", "2025-03-20", "OIII", "staged");
        harness.Page.StagingPath = staging;
        harness.Page.CommitStagingCommand.Execute(null);
        Assert.Equal("", harness.Page.StagingError);
        Assert.True(harness.Page.GenerateCommand.CanExecute(WbppScriptType.PowerShell));

        var writesBefore = harness.MutateCalls;

        // Moving the pick up to the date folder puts the staging folder inside the source.
        harness.Page.Sessions[0].Rows.Single(row => row.FolderName == "2025-03-20").IsChosen = true;

        Assert.Equal(WbppExportViewModel.SourceRefusal, harness.Page.StagingError);
        Assert.False(harness.Page.GenerateCommand.CanExecute(WbppScriptType.PowerShell));

        // The stored key is not cleared on this path either.
        Assert.Equal(writesBefore, harness.MutateCalls);
        Assert.Equal(staging, WbppSettingsRead.StagingPath(harness.General.WbppStagingPathDocument));
    }

    // ------------------------------------------------------------------ the exclusion patterns

    [Fact]
    public void APatternCarryingARefusedCharacter_IsRefusedAndTheListKeepsItsContents()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1]);
        var before = harness.Page.ExclusionsText;

        harness.Page.ExclusionsText = before + Environment.NewLine + "bad$name";

        Assert.Equal(
            "A folder pattern cannot contain a quote, a dollar sign, a backtick or a line break",
            harness.Page.ExclusionError);
        Assert.Equal(before, harness.Page.ExclusionsText);
    }

    [Theory]
    [InlineData("bad\"name")]
    [InlineData("bad$name")]
    [InlineData("bad`name")]
    [InlineData("bad\\name")]
    [InlineData("bad’name")]
    public void EachRefusedCharacterIsRefused(string pattern)
    {
        // The last two rows are the shapes a narrower copy of the rule admits: the Task 4 security
        // review's set is ScriptGenerator.IsRefusedPatternChar and it refuses a backslash and
        // U+2018 to U+201F as well. A pattern carrying either is accepted by a three-character
        // test, written into wbpp_exclusions by Save as defaults, and then dropped with no
        // sentence by WbppSettingsRead.Exclusions at the next page open, so the folder the user
        // meant to exclude is copied and the page says nothing.
        using var harness = ExportHarness.Create(OneNight(), [N1]);
        var before = harness.Page.ExclusionsText;

        harness.Page.ExclusionsText = pattern;

        Assert.True(harness.Page.HasExclusionError);
        Assert.Equal(before, harness.Page.ExclusionsText);
    }

    [Fact]
    public void TheFieldsOwnLineBreaksStayLegal()
    {
        // The refused set is tested per line, because the field's own separator is a line feed:
        // testing the whole text against a set that includes CR and LF would refuse every
        // multi-line list, which is every list.
        using var harness = ExportHarness.Create(OneNight(), [N1]);

        harness.Page.ExclusionsText = "WBPP" + Environment.NewLine + "finals";

        Assert.False(harness.Page.HasExclusionError);
        Assert.Equal(new[] { "WBPP", "finals" }, harness.Page.Exclusions);
    }

    [Fact]
    public void TheDefaultPatternsComeFromTheReaderAndAreNeverTypedHere()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1]);

        Assert.Equal(WbppSettingsRead.Exclusions(null), harness.Page.Exclusions);
    }

    [Fact]
    public void SaveAsDefaults_WritesTheExclusionsInOneMutateGeneral_AndLeavesTheStagingKeyAlone()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1], storedStaging: Outside);
        harness.Page.SavedConfirmationDuration = TimeSpan.Zero;
        harness.Page.ExclusionsText = "WBPP" + Environment.NewLine + "finals";

        SaveDefaults(harness.Page);

        Assert.Equal(1, harness.MutateCalls);
        Assert.Equal(Outside, WbppSettingsRead.StagingPath(harness.General.WbppStagingPathDocument));
        Assert.Equal(
            new[] { "WBPP", "finals" },
            WbppSettingsRead.Exclusions(harness.General.WbppExclusionsDocument));
        Assert.False(harness.Page.IsSavedConfirmed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"C:\p16lib\staged")]
    public void SaveAsDefaultsOverABlankedOrRefusedStagingValue_LeavesTheStoredPathStanding(
        string typed)
    {
        // A failure looks like a remembered staging folder disappearing with no sentence: the
        // button carries no CanExecute and no IsEnabled binding, so it is pressable in exactly this
        // state, and a user who blanks the field or types a folder inside their library, edits an
        // exclusion pattern and presses Save as defaults loses the stored path. Commit is the
        // key's writer on every accepted change and deliberately never clears it, so this command
        // has nothing to add for that key.
        using var harness = ExportHarness.Create(OneNight(), [N1], storedStaging: Outside);
        harness.Page.SavedConfirmationDuration = TimeSpan.Zero;

        harness.Page.StagingPath = typed;
        harness.Page.CommitStagingCommand.Execute(null);
        Assert.False(harness.Page.IsGenerateAvailable);

        harness.Page.ExclusionsText = "WBPP";
        SaveDefaults(harness.Page);

        Assert.Equal(Outside, WbppSettingsRead.StagingPath(harness.General.WbppStagingPathDocument));
        Assert.Equal(new[] { "WBPP" }, WbppSettingsRead.Exclusions(harness.General.WbppExclusionsDocument));
    }

    [Fact]
    public void TheSavedConfirmationRunsForAboutASecondByDefault()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1]);

        Assert.Equal(TimeSpan.FromSeconds(1), harness.Page.SavedConfirmationDuration);
    }

    // The command wait lives out of a test body, because xUnit1031 is an error here.
    private static void SaveDefaults(WbppExportViewModel page)
        => page.SaveAsDefaultsCommand.ExecuteAsync(null).Wait(TimeSpan.FromSeconds(30));

    // ------------------------------------------------------------------ 10.12 the 260 ceiling

    [Fact]
    public void AtTwoHundredFiftyNine_NothingIsShown_AndAtTwoHundredSixtyTheSentenceIs()
    {
        using var harness = ExportHarness.Create(OneNight(), [N1], storedStaging: ShortStaging);

        // The figure is Task 2's member. This case pads the staging root until the page's own
        // measurement reaches the boundary rather than recomputing a destination length here.
        PadStagingTo(harness, 259);
        Assert.Equal(259, harness.Page.LongestDestinationLength);
        Assert.False(harness.Page.HasLongPathWarning);
        Assert.Equal("", harness.Page.LongPathText);

        PadStagingTo(harness, 260);
        Assert.Equal(260, harness.Page.LongestDestinationLength);
        Assert.True(harness.Page.HasLongPathWarning);
        Assert.StartsWith(
            "The longest file this copy would create is 260 characters. Windows refuses a path of "
            + "260 characters or more unless long paths are enabled",
            harness.Page.LongPathText,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "Choose a shorter staging folder, or a deeper folder for the nights with the longest "
            + "paths.",
            harness.Page.LongPathText,
            StringComparison.Ordinal);

        // It never disables Generate: the ceiling depends on a machine setting this application
        // cannot read from here, and a user who has enabled long paths is entitled to proceed.
        Assert.True(harness.Page.GenerateCommand.CanExecute(WbppScriptType.PowerShell));
    }

    [Fact]
    public void AStagingFolderChangeAloneTurnsTheWarningOn()
    {
        // A failure looks like the figure being computed once at load, which shows a clean page to
        // a user who then lengthens their staging path.
        using var harness = ExportHarness.Create(OneNight(), [N1], storedStaging: ShortStaging);

        PadStagingTo(harness, 200);
        Assert.False(harness.Page.HasLongPathWarning);

        PadStagingTo(harness, 300);
        Assert.True(harness.Page.HasLongPathWarning);
    }

    [Fact]
    public void ALevelChangeAloneTurnsTheWarningOn()
    {
        // A shallower level lengthens what sits under it, with no staging change at all. A failure
        // looks like a clean page shown to a user who then picks the level that breaks their copy.
        var deep = Path.Combine(
            Library.Root, "M31", "2025-03-20", "Ha", new string('d', 60), "a.fits");
        using var harness = ExportHarness.Create(
            new Library().Frame(N1, deep), [N1], storedStaging: ShortStaging);

        var session = harness.Page.Sessions[0];
        var shallow = session.Rows.Single(row => row.FolderName == "M31");
        var deepest = session.Rows[^1];

        deepest.IsChosen = true;
        var deepFigure = harness.Page.LongestDestinationLength;
        shallow.IsChosen = true;
        Assert.True(harness.Page.LongestDestinationLength > deepFigure);

        deepest.IsChosen = true;
        PadStagingTo(harness, 259);
        Assert.False(harness.Page.HasLongPathWarning);

        shallow.IsChosen = true;
        Assert.True(harness.Page.HasLongPathWarning);
    }

    // Pads the staging root until the page's own measured longest destination equals
    // <paramref name="total"/>. The arithmetic stays Task 2's: this reads the figure back and adds
    // the difference rather than composing a destination path or taking its length.
    private static void PadStagingTo(ExportHarness harness, int total)
    {
        harness.Page.StagingPath = harness.Page.StagingPath
            + new string('x', Math.Max(0, total - harness.Page.LongestDestinationLength));
        harness.Page.CommitStagingCommand.Execute(null);
    }
}
