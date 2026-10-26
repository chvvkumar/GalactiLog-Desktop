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

    private static readonly DateOnly N2 = new(2025, 3, 21);

    private static readonly string Light = Path.Combine(Date, "LIGHT");

    private static void Until(Func<bool> condition)
        => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)));

    // Own frames under Date\LIGHT\Target_A, another target's under Date\LIGHT\Target_M 45.
    private static ExportHarness ForeignTargetHarness() => ExportHarness.Create(
        new Library()
            .Frame(N1, Path.Combine(Light, "Target_A", "a.fits"))
            .Foreign(Path.Combine(Light, "Target_M 45", "m.fits"), N1, "obj:M 45", "M 45"),
        [N1],
        listSubfolders: folder => Is(folder, Light)
            ? [Path.Combine(Light, "Target_A"), Path.Combine(Light, "Target_M 45")]
            : []);

    [Fact]
    public void AFolderHoldingOnlyAnotherTargetsLights_StartsUnticked_WithTheTargetsName()
    {
        using var h = ForeignTargetHarness();
        var session = h.Page.Sessions[0];
        session.ChosenIndex = Row(session, Date).Index;
        Settle(session);

        var foreign = Child(Row(session, Light), "Target_M 45");
        Assert.False(foreign.IsIncluded);
        Assert.Equal("M 45's lights", foreign.ReasonTag);
        Assert.True(foreign.HasReasonTag);
        Assert.Equal("M 45's lights", foreign.RowTag);
        Assert.True(Child(Row(session, Light), "Target_A").IsIncluded);
        Assert.Equal("", Child(Row(session, Light), "Target_A").RowTag);
        Assert.Equal([Path.Combine(Light, "Target_M 45")], SkippedPaths(session));
    }

    [Fact]
    public void AFolderHoldingOnlyThisTargetsOtherNight_StartsUnticked_AsOtherNight()
    {
        var date2 = Path.Combine(M31, N2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var library = new Library().Frame(N1, Ha(N1, "a.fits")).Frame(N2, Ha(N2, "b.fits"));
        using var h = ExportHarness.Create(library, [N1], listSubfolders: folder =>
            Is(folder, M31) ? [Date, date2] : []);
        var session = h.Page.Sessions[0];
        session.ChosenIndex = Row(session, M31).Index;
        Settle(session);

        var other = Child(Row(session, M31), N2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Assert.False(other.IsIncluded);
        Assert.Equal("other night", other.ReasonTag);
        Assert.Equal("other night", other.RowTag);
        Assert.True(Row(session, Date).IsIncluded);
        Assert.Equal([date2], SkippedPaths(session));
    }

    [Fact]
    public void TickingAnAutoUntickedRow_ClearsItsReason_AndIsKeptOnRescan()
    {
        using var h = ForeignTargetHarness();
        var session = h.Page.Sessions[0];
        var date = Row(session, Date);
        session.ChosenIndex = date.Index;
        Settle(session);
        var foreign = Child(Row(session, Light), "Target_M 45");

        foreign.IsIncluded = true;

        Assert.Null(foreign.ReasonTag);
        Assert.False(foreign.HasReasonTag);
        Assert.Equal("", foreign.RowTag);
        Assert.Null(session.Chosen!.SkippedFolders);

        // The pick moves away and back, which re-zones the tree: the user's tick stands.
        session.ChosenIndex = Row(session, Light).Index;
        session.ChosenIndex = date.Index;
        Settle(session);

        Assert.True(foreign.IsIncluded);
        Assert.Null(foreign.ReasonTag);
        Assert.Null(session.Chosen!.SkippedFolders);

        // Unticked by hand, the row reads the plain skipped tag: the reason is gone for good.
        foreign.IsIncluded = false;
        Assert.Equal(FolderNodeViewModel.SkippedText, foreign.RowTag);
    }

    [Fact]
    public void TheChosenRow_IsNeverAutoUnticked()
    {
        // Two foreign-only levels, which the catalogue never produces for a chain, so the rule
        // is pinned on the view-model directly.
        var top = Path.Combine(Library.Root, "M31");
        var levels = new SessionLevels(
            N1,
            Library.Root,
            [
                new FolderLevel(top, 1, 0, ["NGC 7000"], [], 10),
                new FolderLevel(Path.Combine(top, "x"), 2, 0, [], ["2025-03-21"], 10),
            ],
            0,
            0,
            0,
            null);
        var session = new SessionLevelViewModel(levels, "M 31", _ => { });

        Assert.True(session.Rows[0].IsIncluded);
        Assert.Null(session.Rows[0].ReasonTag);
        Assert.False(session.Rows[1].IsIncluded);
        Assert.Equal("other night", session.Rows[1].RowTag);

        session.ChosenIndex = 1;

        Assert.True(session.Rows[1].IsIncluded);
        Assert.Null(session.Rows[1].ReasonTag);
    }

    [Fact]
    public async Task AMixedFolder_StaysTicked_ExcludesForeignFilesPerFile_AndReadsKOfN()
    {
        var x1 = Ha(N1, "x1.fits");
        var x2 = Ha(N1, "x2.fits");
        var library = new Library()
            .Frame(N1, Ha(N1, "a.fits"))
            .Frame(N1, Ha(N1, "b.fits"))
            .Frame(N1, Ha(N1, "c.fits"))
            .Foreign(x1, N1, size: 500)
            .Foreign(x2, N1, size: 500);
        var reads = new List<(string Folder, string Key, DateOnly Night)>();
        using var h = ExportHarness.Create(library, [N1], listSubfolders: _ => [], readForeignLights: (folder, key, night, _) =>
        {
            lock (reads)
            {
                reads.Add((folder, key, night));
            }

            return [new WbppFramePath(Guid.NewGuid(), x1, 500), new WbppFramePath(Guid.NewGuid(), x2, 500)];
        });
        var session = h.Page.Sessions[0];
        var ha = Row(session, HaFolder);
        session.ChosenIndex = ha.Index;
        Settle(session);
        Until(() => ha.FrameCountText == "3 of 5 files");

        Assert.True(ha.IsIncluded);
        lock (reads)
        {
            Assert.Equal(library.GroupKey, reads.Single().Key);
            Assert.Equal(N1, reads.Single().Night);
        }

        Assert.Equal(2, session.ForeignExcluded.Count);
        Until(() => h.Page.SizeText == MetricText.Bytes(3_000));
        Assert.Equal("3 light frames", h.Page.FrameCountText);

        h.Page.StagingPath = h.TempFolder("staging");
        h.Page.CommitStagingCommand.Execute(null);
        h.Page.SavedConfirmationDuration = TimeSpan.Zero;
        h.Destination = h.ScriptPath("wbpp.ps1");
        Generate(h.Page);

        Assert.Equal(["@('x1.fits', 'x2.fits')"], ExcludeFilesOf(h.Page.ScriptText!));

        // The in-app copy leaves them out too: the fake disk lists every file in the folder.
        var source = h.TempFolder("source");
        foreach (var name in new[] { "a.fits", "b.fits", "c.fits", "x1.fits", "x2.fits" })
        {
            File.WriteAllBytes(Path.Combine(source, name), [1]);
        }

        var created = new List<string>();
        h.Page.StagingIoFor = _ => new StagingIo(
            _ => new MemoryStream([1]),
            _ => new DirectoryInfo(source).GetFiles(),
            _ => null,
            _ => { },
            path =>
            {
                lock (created)
                {
                    created.Add(Path.GetFileName(path));
                }

                return new MemoryStream();
            });
        var result = await h.Page.RunCopyAsync(new Progress<StagingProgress>(), io => io, CancellationToken.None);

        Assert.Equal(3, result.Copied);
        Assert.Equal(["a.fits", "b.fits", "c.fits"], created.Order(StringComparer.Ordinal));
    }

    // Each PowerShell job's ExcludeFiles array, in job order.
    private static IReadOnlyList<string> ExcludeFilesOf(string script)
        => [.. script.Split('\n')
            .Where(line => line.TrimStart().StartsWith("@{ Src", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf("ExcludeFiles = ", StringComparison.Ordinal) + "ExcludeFiles = ".Length)..])
            .Select(rest => rest[..(rest.IndexOf(')', StringComparison.Ordinal) + 1)])];

    [Fact]
    public void TwoNightsSharingOneChosenFolder_AreOneJobThatCopiesBothNightsFrames()
    {
        // A flat layout: both nights' frames in one folder, so each night's foreign read returns
        // the other night's frame, and both nights choose that same folder.
        var flat = Path.Combine(M31, "Ha");
        var n1 = Path.Combine(flat, "n1.fits");
        var n2 = Path.Combine(flat, "n2.fits");
        var library = new Library().Frame(N1, n1).Frame(N2, n2);
        using var h = ExportHarness.Create(library, [N1, N2], listSubfolders: _ => [], readForeignLights: (_, _, night, _) =>
            night == N1 ? [new WbppFramePath(library.Ids[n2], n2, 1_000)] : [new WbppFramePath(library.Ids[n1], n1, 1_000)]);
        foreach (var session in h.Page.Sessions)
        {
            session.ChosenIndex = Row(session, flat).Index;
            Settle(session);
        }

        Until(() => h.Page.Sessions.All(session => session.ForeignExcluded.Count == 1));
        h.Page.StagingPath = h.TempFolder("staging");
        h.Page.CommitStagingCommand.Execute(null);
        h.Page.SavedConfirmationDuration = TimeSpan.Zero;
        h.Destination = h.ScriptPath("wbpp.ps1");
        Generate(h.Page);

        // Each night lists the other's frame as foreign, so the merged job, which stands for both
        // nights, excludes neither.
        var job = Assert.Single(h.Page.ScriptText!.Split('\n'), line => line.TrimStart().StartsWith("@{ Src", StringComparison.Ordinal));
        Assert.DoesNotContain("ExcludeFiles", job, StringComparison.Ordinal);
    }

    [Fact]
    public void AForeignReadLandingAfterGenerate_WithdrawsTheScript()
    {
        using var gate = new ManualResetEventSlim(false);
        using var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], readForeignLights: (_, _, _, _) =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return [new WbppFramePath(Guid.NewGuid(), Ha(N1, "x.fits"), 500)];
        });
        h.Page.StagingPath = h.TempFolder("staging");
        h.Page.CommitStagingCommand.Execute(null);
        h.Page.SavedConfirmationDuration = TimeSpan.Zero;
        h.Destination = h.ScriptPath("wbpp.ps1");
        Generate(h.Page);
        Assert.True(h.Page.HasScript);

        gate.Set();

        Until(() => !h.Page.HasScript);
    }

    [Fact]
    public void PickingAShallowerLevel_ReReadsTheForeignLights_ForTheNewFolder()
    {
        // Another night's frame under Date (outside Ha) keeps the calibration start at Ha, so the
        // first read is bounded to Ha and knows nothing of Date's other files.
        var library = new Library()
            .Frame(N1, Ha(N1, "a.fits"))
            .Frame(N2, Path.Combine(Date, "Other", "z.fits"));
        var reads = new List<string>();
        using var h = ExportHarness.Create(library, [N1], listSubfolders: _ => [], readForeignLights: (folder, _, _, _) =>
        {
            lock (reads)
            {
                reads.Add(folder);
            }

            return Is(folder, Date) ? [new WbppFramePath(Guid.NewGuid(), Path.Combine(Date, "OIII", "f.fits"), 500)] : [];
        });
        var session = h.Page.Sessions[0];
        var date = Row(session, Date);
        Until(() => { lock (reads) { return reads.Count == 1; } });
        lock (reads)
        {
            Assert.Equal([HaFolder], reads);
        }

        Assert.Equal("1 light frame", date.FrameCountText);

        session.ChosenIndex = date.Index;
        Until(() => date.FrameCountText == "1 of 2 files");

        // A deeper pick, and the same shallow one again, read nothing more.
        session.ChosenIndex = Row(session, HaFolder).Index;
        session.ChosenIndex = date.Index;
        lock (reads)
        {
            Assert.Equal([HaFolder, Date], reads);
        }
    }

    [Fact]
    public void QuotingRefusal_NamesASkippedFolder()
    {
        Assert.Contains("one of the skipped folders", WbppExportViewModel.QuotingRefusal("excluded folder", "x"), StringComparison.Ordinal);
    }
}
