using System.Globalization;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Wbpp;

/// <summary>
/// Step 1 of the export wizard as one folder tree per night: the chain rows with their radios,
/// the disk rows, the zones the chosen row draws, the ticks under it, and the skipped levels those
/// ticks put on <see cref="ChosenLevel.SkippedFolders"/>. The lister is a fake; nothing here reads
/// a directory.
/// </summary>
public class FolderTreeTests
{
    private static readonly DateOnly N1 = new(2025, 3, 20);

    private static readonly string M31 = Path.Combine(Library.Root, "M31");

    private static readonly string Date = Path.Combine(M31, N1.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static readonly string HaFolder = Path.Combine(Date, "Ha");

    private static readonly string Oiii = Path.Combine(Date, "OIII");

    private static readonly string Flat = Path.Combine(Date, "FLAT");

    private static readonly string Deeper = Path.Combine(Flat, "deeper");

    private static string Ha(DateOnly night, string file) => Path.Combine(
        Library.Root, "M31", night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Ha", file);

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // The page lists on Task.Run and the harness posts inline, so the results land off-thread.
    // The wait lives out of a test body, because xUnit1031 is an error here.
    private static void Settle(SessionLevelViewModel session)
        => Assert.True(SpinWait.SpinUntil(() => session.Rows.All(r => !r.IsListing), TimeSpan.FromSeconds(10)));

    private static void Settle(FolderNodeViewModel row)
        => Assert.True(SpinWait.SpinUntil(() => !row.IsListing, TimeSpan.FromSeconds(10)));

    private static FolderNodeViewModel Row(SessionLevelViewModel session, string path)
        => session.Rows.Single(r => Is(r.Level.Path, path));

    private static FolderNodeViewModel Child(FolderNodeViewModel row, string name)
        => row.Children.Single(c => c.FolderName == name);

    private static IReadOnlyList<string> SkippedPaths(SessionLevelViewModel session)
        => [.. (session.Chosen!.SkippedFolders ?? []).Select(level => level.Path)];

    private static void Generate(WbppExportViewModel page)
        => page.GenerateCommand.ExecuteAsync(WbppScriptType.PowerShell).Wait(TimeSpan.FromSeconds(30));

    [Fact]
    public void OnLoad_EveryChainRowIsExpanded_AndOnlyTheCopiedChainDirectoriesAreListed_EachOnce()
    {
        var calls = new List<string>();
        using var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: folder =>
        {
            lock (calls)
            {
                calls.Add(folder);
            }

            return Is(folder, Date) ? [Flat, HaFolder] : [];
        });
        var session = h.Page.Sessions[0];
        Settle(session);

        Assert.True(session.IsExpanded);
        Assert.All(session.Rows, r => Assert.True(r.IsExpanded));

        // The default pick is the leaf, Ha: the rows above it are open, but their disk children
        // are hidden, so they are not read.
        lock (calls)
        {
            Assert.Equal([HaFolder], calls);
        }

        Assert.Equal("M31", session.Roots.Single().FolderName);
        Assert.Same(session.Rows[0], session.Rows[1].Parent);
        Assert.Same(session.Rows[1], session.Rows[2].Parent);

        // Choosing the root brings every ancestor into the chosen or copied zone: each lists once.
        session.ChosenIndex = session.Rows[0].Index;
        Settle(session);
        lock (calls)
        {
            Assert.Equal([M31, Date, HaFolder], calls.Order(StringComparer.OrdinalIgnoreCase));
        }

        // Leaving the zone and entering it again reads nothing twice.
        session.ChosenIndex = session.Rows[2].Index;
        session.ChosenIndex = session.Rows[0].Index;
        Settle(session);
        lock (calls)
        {
            Assert.Equal([M31, Date, HaFolder], calls.Order(StringComparer.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void AChainChildIsMergedIntoTheListingInPlace_AndKeptWhenTheDiskDoesNotListIt()
    {
        using (var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: folder =>
            Is(folder, Date) ? [Flat, HaFolder, Oiii] : []))
        {
            var session = h.Page.Sessions[0];
            var date = Row(session, Date);
            session.ChosenIndex = date.Index;
            Settle(session);

            Assert.Equal(["FLAT", "Ha", "OIII"], date.Children.Select(c => c.FolderName));
            Assert.Same(session.Rows[2], date.Children.Single(c => c.FolderName == "Ha"));
            Assert.Equal(1, date.Children.Count(c => c.FolderName == "Ha"));
        }

        using (var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: folder =>
            Is(folder, Date) ? [Flat] : []))
        {
            var session = h.Page.Sessions[0];
            var date = Row(session, Date);
            session.ChosenIndex = date.Index;
            Settle(session);

            Assert.Equal(["FLAT", "Ha"], date.Children.Select(c => c.FolderName));
            Assert.True(Child(date, "Ha").IsChain);
        }
    }

    [Fact]
    public void ForkedLevels_ShowBothLeavesUnderOneParent_WithRadios()
    {
        var library = new Library().Frame(N1, Ha(N1, "a.fits")).Frame(N1, Path.Combine(Oiii, "b.fits"));
        using var h = ExportHarness.Create(library, [N1], listSubfolders: _ => []);
        var session = h.Page.Sessions[0];
        Settle(session);

        var date = Row(session, Date);
        var ha = Row(session, HaFolder);
        var oiii = Row(session, Oiii);
        Assert.Same(date, ha.Parent);
        Assert.Same(date, oiii.Parent);
        Assert.True(ha.IsChain);
        Assert.True(oiii.IsChain);
        Assert.True(ha.ShowsRadio);
        Assert.True(oiii.ShowsRadio);
        Assert.Equal(2, date.ChainChildren.Count);
    }

    [Fact]
    public void Zones_FollowTheChosenRow()
    {
        using var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: folder =>
            Is(folder, Date) ? [Flat, HaFolder] : []);
        var session = h.Page.Sessions[0];
        var m31 = Row(session, M31);
        var date = Row(session, Date);
        var ha = Row(session, HaFolder);

        session.ChosenIndex = date.Index;
        Settle(session);
        var flat = Child(date, "FLAT");
        Assert.Equal(FolderZone.NotCopied, m31.Zone);
        Assert.Equal(FolderZone.Chosen, date.Zone);
        Assert.Equal(FolderZone.Copied, ha.Zone);
        Assert.Equal(FolderZone.Copied, flat.Zone);
        Assert.True(flat.IsVisible);
        Assert.False(m31.ShowsTick);
        Assert.False(date.ShowsTick);
        Assert.True(ha.ShowsTick);
        Assert.True(flat.ShowsTick);
        Assert.True(m31.ShowsRadio);
        Assert.True(date.ShowsRadio);
        Assert.True(ha.ShowsRadio);
        Assert.False(flat.ShowsRadio);

        session.ChosenIndex = ha.Index;
        Assert.Equal(FolderZone.NotCopied, date.Zone);
        Assert.Equal(FolderZone.Hidden, flat.Zone);
        Assert.False(flat.IsVisible);
        Assert.Equal(FolderZone.Chosen, ha.Zone);
        Assert.False(ha.ShowsTick);

        session.ChosenIndex = m31.Index;
        Assert.Equal(FolderZone.Chosen, m31.Zone);
        Assert.Equal(FolderZone.Copied, date.Zone);
        Assert.Equal(FolderZone.Copied, ha.Zone);
        Assert.Equal(FolderZone.Copied, flat.Zone);
    }

    [Fact]
    public void UntickingATickRow_CountsOnlyWhenUnderTheChosenRow_AndSurvivesAPickChange()
    {
        using var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: folder =>
            Is(folder, Date) ? [Flat, HaFolder] : []);
        var session = h.Page.Sessions[0];
        Settle(session);
        var date = Row(session, Date);
        session.ChosenIndex = date.Index;
        Settle(session);
        var flat = Child(date, "FLAT");

        flat.IsIncluded = false;
        Assert.Equal("1 folder skipped", session.SkippedText);
        Assert.True(session.HasSkippedText);
        Assert.True(flat.IsSkipped);
        Assert.Equal([Flat], SkippedPaths(session));

        session.ChosenIndex = Row(session, HaFolder).Index;
        Assert.Equal("", session.SkippedText);
        Assert.False(session.HasSkippedText);
        Assert.Null(session.Chosen!.SkippedFolders);
        Assert.False(flat.IsIncluded);

        session.ChosenIndex = date.Index;
        Assert.Equal("1 folder skipped", session.SkippedText);
    }

    [Fact]
    public void UntickingAParent_HidesItsChildren_AndOnlyTheParentReachesChosen()
    {
        using var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: folder =>
            Is(folder, Date) ? [Flat]
            : Is(folder, Flat) ? [Deeper]
            : []);
        var session = h.Page.Sessions[0];
        Settle(session);
        var date = Row(session, Date);
        session.ChosenIndex = date.Index;
        Settle(session);
        var flat = Child(date, "FLAT");

        flat.IsExpanded = true;
        Settle(flat);
        var deeper = flat.Children.Single();
        Assert.True(flat.ShowsChildren);
        Assert.Equal(FolderZone.Copied, deeper.Zone);

        deeper.IsIncluded = false;
        flat.IsIncluded = false;

        Assert.False(flat.ShowsChildren);
        Assert.Equal([Flat], SkippedPaths(session));

        flat.IsIncluded = true;

        Assert.True(flat.ShowsChildren);
        Assert.Equal([Deeper], SkippedPaths(session));
        Assert.False(deeper.IsIncluded);
    }

    [Fact]
    public void ATickChange_RefreshesTheFooterAndTheNightSummary_AndWithdrawsTheScript()
    {
        var library = new Library().Frame(N1, Ha(N1, "a.fits"), 100).Frame(N1, Path.Combine(Oiii, "b.fits"), 200);
        using var h = ExportHarness.Create(library, [N1], listSubfolders: folder =>
            Is(folder, Date) ? [HaFolder, Oiii] : []);
        var session = h.Page.Sessions[0];
        session.ChosenIndex = Row(session, Date).Index;
        Settle(session);
        h.Page.StagingPath = h.TempFolder("staging");
        h.Page.CommitStagingCommand.Execute(null);
        h.Page.SavedConfirmationDuration = TimeSpan.Zero;
        h.Destination = h.ScriptPath("wbpp.ps1");
        Generate(h.Page);
        Assert.True(h.Page.HasScript);
        Assert.Equal("2 light frames", h.Page.FrameCountText);

        var oiii = Row(session, Oiii);
        Assert.True(oiii.ShowsTick);
        oiii.IsIncluded = false;

        Assert.Equal("1 light frame", h.Page.FrameCountText);
        Assert.Equal(MetricText.Bytes(100), h.Page.SizeText);
        Assert.False(h.Page.HasScript);
        Assert.Equal(
            "This night copies 1 light frame, " + MetricText.Bytes(100) + " after skipping 1 folder",
            session.SummaryText);
    }

    [Fact]
    public void AnUnreadableChainDirectory_StatesUnreadable_AndStillShowsItsChainChild()
    {
        using var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: folder =>
            Is(folder, Date) ? null : []);
        var session = h.Page.Sessions[0];
        var date = Row(session, Date);
        var ha = Row(session, HaFolder);
        session.ChosenIndex = date.Index;
        Settle(session);

        Assert.Equal(FolderNodeViewModel.UnreadableText, date.ChildrenText);
        Assert.True(date.HasChildrenText);
        Assert.Equal(["Ha"], date.Children.Select(c => c.FolderName));
        Assert.Equal(FolderNodeViewModel.NoFoldersText, ha.ChildrenText);
    }

    [Fact]
    public void WithNoLister_TheChainStillShows_AndNothingIsListed()
    {
        using var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1]);
        var session = h.Page.Sessions[0];

        Assert.Equal(3, session.Rows.Count);
        Assert.All(session.Rows, r => Assert.True(r.IsExpanded));
        Assert.All(session.Rows, r => Assert.Equal(r.ChainChildren, r.Children));
        Assert.All(session.Rows, r => Assert.False(r.IsListing));
        Assert.Equal("", session.SkippedText);
        Assert.Null(session.Chosen!.SkippedFolders);
    }

    [Fact]
    public void AnUnavailableNight_HasNoOpenTree_AndNoRows()
    {
        using (var h = ExportHarness.Create(new Library().EmptyNight(N1), [N1]))
        {
            var session = h.Page.Sessions[0];
            Assert.False(session.HasLevels);
            Assert.False(session.IsExpanded);
            Assert.Empty(session.Roots);
        }

        var library = new Library().Frame(N1, Ha(N1, "a.fits"));
        library.ScanRoots.Add(Library.SecondRoot);
        library.Frame(N1, Path.Combine(Library.SecondRoot, "M31", "Ha", "b.fits"));
        using (var h = ExportHarness.Create(library, [N1]))
        {
            var session = h.Page.Sessions[0];
            Assert.False(session.HasLevels);
            Assert.False(session.IsExpanded);
            Assert.Empty(session.Roots);
        }
    }

    [Fact]
    public void UntickingAChainRowUnderTheChosenOne_ThenChoosingIt_SkipsNothing()
    {
        var library = new Library().Frame(N1, Ha(N1, "a.fits")).Frame(N1, Ha(N1, "b.fits"));
        using var h = ExportHarness.Create(library, [N1], listSubfolders: folder =>
            Is(folder, Date) ? [HaFolder] : []);
        var session = h.Page.Sessions[0];
        var ha = Row(session, HaFolder);
        session.ChosenIndex = Row(session, Date).Index;
        Settle(session);

        ha.IsIncluded = false;
        Assert.Equal([HaFolder], SkippedPaths(session));

        session.ChosenIndex = ha.Index;

        Assert.Null(session.Chosen!.SkippedFolders);
        Assert.False(ha.ShowsTick);
        Assert.False(ha.IsSkipped);
        Assert.Equal("2 light frames", h.Page.FrameCountText);
    }

    [Fact]
    public void CollapseLabel_FollowsTheTreeState()
    {
        using var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1]);
        var session = h.Page.Sessions[0];

        Assert.True(session.IsExpanded);
        Assert.Equal("Collapse", session.ToggleLabel);

        session.ToggleCommand.Execute(null);

        Assert.False(session.IsExpanded);
        Assert.Equal("Expand", session.ToggleLabel);
    }

    // Carried over from the skip panel's suite: the skip sentence is still reachable, now by
    // unticking the one chain row under the chosen folder.
    [Fact]
    public async Task SkippingEveryFrameFolder_StatesTheSkipSentence_NotTheFilterOne()
    {
        using var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: folder =>
            Is(folder, Date) ? [HaFolder] : []);
        var jobs = new JobRegistry(action => action());
        using var wizard = new WbppExportWizardViewModel(
            h.Page,
            _ => Task.CompletedTask,
            new StagingCopyService(jobs, (_, _, _, _) => { }),
            openFolder: _ => { },
            runScript: _ => { },
            post: action => action());
        var session = h.Page.Sessions[0];
        session.ChosenIndex = Row(session, Date).Index;
        Settle(session);

        Row(session, HaFolder).IsIncluded = false;

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
