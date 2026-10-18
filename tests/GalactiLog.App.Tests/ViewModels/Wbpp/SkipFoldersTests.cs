using System.Globalization;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Wbpp;

/// <summary>
/// The skip panel on step 1 of the export wizard: the lazy subfolder tree under a night's chosen
/// folder, the skipped levels it puts on <see cref="ChosenLevel.SkippedFolders"/>, and the totals
/// wording the skips can produce. The lister is a fake; nothing here reads a directory.
/// </summary>
public class SkipFoldersTests
{
    private static readonly DateOnly N1 = new(2025, 3, 20);

    private static readonly string NightFolder = Path.Combine(
        Library.Root, "M31", N1.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static readonly string Oiii = Path.Combine(NightFolder, "OIII");

    private static readonly string Deeper = Path.Combine(Oiii, "deeper");

    private static string Ha(DateOnly night, string file) => Path.Combine(
        Library.Root, "M31", night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Ha", file);

    // The page lists on Task.Run and the harness posts inline, so the result lands off-thread.
    // The wait lives out of a test body, because xUnit1031 is an error here.
    private static void Settle(SubfolderRowViewModel row)
        => Assert.True(SpinWait.SpinUntil(() => !row.IsListing, TimeSpan.FromSeconds(10)));

    private static void ChooseNightFolder(SessionLevelViewModel session)
        => session.ChosenIndex = session.Rows.Single(r => r.Level.Path == NightFolder).Index;

    private static SessionLevelViewModel OpenOnNight(ExportHarness h)
    {
        var session = h.Page.Sessions[0];
        ChooseNightFolder(session);
        session.ToggleSkipCommand.Execute(null);
        Settle(session.SkipRoot!);
        return session;
    }

    private static IReadOnlyList<string> SkippedPaths(SessionLevelViewModel session)
        => [.. (session.Chosen!.SkippedFolders ?? []).Select(level => level.Path)];

    private static void Generate(WbppExportViewModel page)
        => page.GenerateCommand.ExecuteAsync(WbppScriptType.PowerShell).Wait(TimeSpan.FromSeconds(30));

    [Fact]
    public void WithNoLister_TheSkipPanelIsNotOffered()
    {
        using var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1]);
        var session = h.Page.Sessions[0];
        Assert.False(session.CanSkip);
        Assert.Empty(session.Chosen!.SkippedFolders ?? []);
    }

    [Fact]
    public void OpeningThePanel_ListsTheChosenFoldersChildrenOnce_WithCatalogueFigures()
    {
        var night = NightFolder;
        var calls = new List<string>();
        var library = new Library().Frame(N1, Ha(N1, "a.fits"), 100).Frame(N1, Path.Combine(night, "OIII", "b.fits"), 200);
        using var h = ExportHarness.Create(library, [N1], listSubfolders: folder =>
        {
            calls.Add(folder);
            return string.Equals(folder, night, StringComparison.OrdinalIgnoreCase)
                ? [Path.Combine(night, "Flats"), Path.Combine(night, "Ha"), Path.Combine(night, "OIII")]
                : [];
        });
        var session = h.Page.Sessions[0];
        session.ChosenIndex = session.Rows.Single(r => r.Level.Path == night).Index;

        session.ToggleSkipCommand.Execute(null);
        Settle(session.SkipRoot!);
        session.ToggleSkipCommand.Execute(null);
        session.ToggleSkipCommand.Execute(null);

        Assert.Equal([night], calls);
        var rows = session.SkipRoot!.Children;
        Assert.Equal(["Flats", "Ha", "OIII"], rows.Select(r => r.FolderName));
        Assert.Equal([SubfolderRowViewModel.NotCataloguedText, "1 light frame", "1 light frame"], rows.Select(r => r.FrameCountText));
        Assert.All(rows, r => Assert.True(r.IsIncluded));
        Assert.Equal("Folders inside " + session.ChosenOwnName + ". Untick a folder to leave it out.", session.SkipHeadingText);
    }

    [Fact]
    public void UntickingAFolder_DeductsItFromTheFooter_StatesTheSkip_AndWithdrawsTheScript()
    {
        var library = new Library().Frame(N1, Ha(N1, "a.fits"), 100).Frame(N1, Path.Combine(Oiii, "b.fits"), 200);
        using var h = ExportHarness.Create(library, [N1], listSubfolders: folder =>
            string.Equals(folder, NightFolder, StringComparison.OrdinalIgnoreCase)
                ? [Path.Combine(NightFolder, "Ha"), Oiii]
                : []);
        var session = OpenOnNight(h);
        h.Page.StagingPath = h.TempFolder("staging");
        h.Page.CommitStagingCommand.Execute(null);
        h.Page.SavedConfirmationDuration = TimeSpan.Zero;
        h.Destination = h.ScriptPath("wbpp.ps1");
        Generate(h.Page);
        Assert.True(h.Page.HasScript);
        Assert.Equal("2 light frames", h.Page.FrameCountText);

        session.SkipRoot!.Children.Single(r => r.FolderName == "OIII").IsIncluded = false;

        Assert.Equal("1 light frame", h.Page.FrameCountText);
        Assert.Equal(WbppPathText.Bytes(100), h.Page.SizeText);
        Assert.Equal("Skips 1 folder", session.SkippedText);
        Assert.True(session.HasSkippedText);
        Assert.Equal([Oiii], SkippedPaths(session));
        Assert.False(h.Page.HasScript);
    }

    [Fact]
    public void UntickingAParent_HidesItsChildren_AndOnlyTheParentReachesChosen()
    {
        using var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: folder =>
            string.Equals(folder, NightFolder, StringComparison.OrdinalIgnoreCase) ? [Oiii]
            : string.Equals(folder, Oiii, StringComparison.OrdinalIgnoreCase) ? [Deeper]
            : []);
        var session = OpenOnNight(h);
        var oiii = session.SkipRoot!.Children.Single();
        oiii.ToggleExpandCommand.Execute(null);
        Settle(oiii);
        var deeper = oiii.Children.Single();
        Assert.True(oiii.ShowsChildren);

        deeper.IsIncluded = false;
        oiii.IsIncluded = false;

        Assert.False(oiii.ShowsChildren);
        Assert.Equal([Oiii], SkippedPaths(session));

        oiii.IsIncluded = true;

        Assert.True(oiii.ShowsChildren);
        Assert.Equal([Deeper], SkippedPaths(session));
        Assert.False(deeper.IsIncluded);
    }

    [Fact]
    public void ChangingTheLevel_ClearsTheSkipsAndClosesThePanel()
    {
        using var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: folder =>
            string.Equals(folder, NightFolder, StringComparison.OrdinalIgnoreCase) ? [Oiii] : []);
        var session = OpenOnNight(h);
        session.SkipRoot!.Children.Single().IsIncluded = false;
        Assert.Equal("Skips 1 folder", session.SkippedText);

        session.ChosenIndex = session.Rows.First(r => r.Level.Path != NightFolder).Index;

        Assert.NotEqual(NightFolder, session.SkipRoot!.Level.Path);
        Assert.Equal(session.Chosen!.Level.Path, session.SkipRoot.Level.Path);
        Assert.False(session.IsSkipOpen);
        Assert.Empty(session.Chosen.SkippedFolders ?? []);
        Assert.Equal("", session.SkippedText);
        Assert.False(session.HasSkippedText);
    }

    [Fact]
    public void AnUnreadableFolder_ReadsAsUnreadable_AndAnEmptyOneAsEmpty()
    {
        using (var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: _ => null))
        {
            var root = OpenOnNight(h).SkipRoot!;
            Assert.Equal(SubfolderRowViewModel.UnreadableText, root.ChildrenText);
            Assert.True(root.HasChildrenText);
            Assert.Empty(root.Children);
        }

        using (var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: _ => []))
        {
            var root = OpenOnNight(h).SkipRoot!;
            Assert.Equal(SubfolderRowViewModel.NoFoldersText, root.ChildrenText);
            Assert.True(root.HasChildrenText);
        }
    }

    [Fact]
    public async Task SkippingEveryFrameFolder_StatesTheSkipSentence_NotTheFilterOne()
    {
        using var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: folder =>
            string.Equals(folder, NightFolder, StringComparison.OrdinalIgnoreCase) ? [Path.Combine(NightFolder, "Ha")] : []);
        var jobs = new JobRegistry(action => action());
        using var wizard = new WbppExportWizardViewModel(
            h.Page,
            _ => Task.CompletedTask,
            new StagingCopyService(jobs, (_, _, _, _) => { }),
            openFolder: _ => { },
            runScript: _ => { },
            post: action => action());
        var session = OpenOnNight(h);

        session.SkipRoot!.Children.Single().IsIncluded = false;

        Assert.Equal(WbppExportViewModel.SkipsEmptyWarning, h.Page.EmptyExportText);

        h.Page.StagingPath = h.TempFolder("staging");
        h.Page.CommitStagingCommand.Execute(null);
        for (var i = 0; i < 4; i++)
        {
            await wizard.NextCommand.ExecuteAsync(null);
        }

        Assert.True(wizard.IsReviewStep);
        Assert.Contains(WbppExportViewModel.SkipsEmptyWarning, wizard.Review.Blocks);
    }

    [Fact]
    public void QuotingRefusal_NamesASkippedFolder()
    {
        Assert.Contains("one of the skipped folders", WbppExportViewModel.QuotingRefusal("excluded folder", "x"), StringComparison.Ordinal);
    }
}
