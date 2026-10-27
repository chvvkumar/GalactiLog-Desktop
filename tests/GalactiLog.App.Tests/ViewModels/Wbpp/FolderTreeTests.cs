using System.Globalization;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Core.Io;
using GalactiLog.Core.Wbpp;
using Xunit;
using static GalactiLog.App.Tests.ViewModels.Wbpp.CalibrationFixtures;
using static GalactiLog.App.Tests.ViewModels.Wbpp.Pleiades;

namespace GalactiLog.App.Tests.ViewModels.Wbpp;

/// <summary>
/// Step 1 of the export wizard as one folder tree per night with a tick on every row: the chain
/// rows, the disk rows, the copy sources the ticks make and the excluded directories they put on
/// <see cref="ChosenLevel.SkippedFolders"/>. The lister is a fake; nothing here reads a directory.
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

    // A row the page opened shows "-": the user's first press closes it with no read, and the
    // second opens it, which lists it.
    private static void Reopen(ExportHarness h, FolderNodeViewModel row) => h.OnUi(() =>
    {
        row.ToggleExpandCommand.Execute(null);
        row.ToggleExpandCommand.Execute(null);
    });

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

    private static FolderNodeViewModel? Find(SessionLevelViewModel session, string path)
    {
        var node = session.Roots.FirstOrDefault(r => PathConfinement.IsUnderOrEqual(r.Level.Path, path));
        while (node is not null && !Is(node.Level.Path, path))
        {
            node = node.Children.FirstOrDefault(c => PathConfinement.IsUnderOrEqual(c.Level.Path, path));
        }

        return node;
    }

    private static IReadOnlyList<string> Sources(SessionLevelViewModel session) => [.. session.CopySources.Select(s => s.Level.Path)];

    private static IReadOnlyList<string> Skipped(SessionLevelViewModel session, string source)
        => [.. (session.CopySources.Single(s => Is(s.Level.Path, source)).SkippedFolders ?? []).Select(l => l.Path)];

    [Fact]
    public void OnLoad_TheDefaultLevelIsTicked_AndTheChainAboveItIsUntickedPath()
    {
        using var h = ExportHarness.Create(Catalogue(), [N18]);
        var session = h.Page.Sessions[0];

        Assert.Equal([Year, Date18, Pleiades.Light, LightCam, Target, Lights], session.Rows.Select(r => r.Level.Path));
        Assert.Equal(5, session.ChosenIndex);
        Assert.True(Row(session, Lights).IsIncluded);
        Assert.All(session.Rows.Take(5), r => Assert.False(r.IsIncluded));
        Assert.All(session.Rows, r => Assert.True(r.IsExpanded));
        Assert.All(session.Rows.Take(5), r => Assert.Equal(1.0, r.RowOpacity));   // path, not a decision
        Assert.All(session.Rows.Take(5), r => Assert.Equal("", r.RowTag));
        Assert.Equal([Lights], Sources(session));
        Assert.Null(session.CopySources[0].SkippedFolders);
        Assert.Equal("Angle_45.18", session.ChosenOwnName);
        Assert.Equal("", session.SkippedText);
        Assert.Equal("3 light frames", h.Page.FrameCountText);
        Assert.Equal("1 folder", h.Page.FolderCountText);
    }

    [Fact]
    public void TickingAShallowerRow_TicksEverythingBeneathIt_ExceptForeignLights_AndIsTheOneSource()
    {
        var foreignTarget = Path.Combine(LightCam, "Target_NGC7000");
        var library = Catalogue().Foreign(Path.Combine(foreignTarget, "Angle_12.00", "n2.fits"), N18);
        using var h = ExportHarness.Create(library, [N18], listSubfolders: folder =>
            Is(folder, Date18) ? [Pleiades.Flat, Pleiades.Light]
            : Is(folder, Pleiades.Light) ? [LightCam]
            : Is(folder, LightCam) ? [foreignTarget, Target]
            : []);
        var session = h.Page.Sessions[0];
        Settle(session);
        Reopen(h, Row(session, Date18));
        Settle(session);
        Assert.False(Child(Row(session, Date18), "FLAT").IsIncluded);   // listed under an unticked parent

        Row(session, Date18).IsIncluded = true;
        Settle(session);

        var other = Child(Row(session, LightCam), "Target_NGC7000");
        Assert.False(other.IsIncluded);
        Assert.Equal("NGC 7000's lights", other.RowTag);
        Assert.True(other.IsSkipped);
        Assert.Equal(0.55, other.RowOpacity);
        Assert.True(Child(Row(session, Date18), "FLAT").IsIncluded);
        Assert.True(Row(session, Lights).IsIncluded);
        Assert.Equal([Date18], Sources(session));
        Assert.Equal([foreignTarget], Skipped(session, Date18));
        Assert.Equal("1 folder skipped", session.SkippedText);
        Assert.Equal(1, session.ChosenIndex);
        Assert.Equal("Date_2026-10-18", session.ChosenOwnName);
    }

    [Fact]
    public void UntickingARow_KeepsDrawingItsChildren_AndATickedRowBeneathIt_IsASourceOfItsOwn()
    {
        using var h = ExportHarness.Create(Catalogue(), [N18], listSubfolders: _ => []);
        var session = h.Page.Sessions[0];
        h.OnUi(() => Row(session, Date18).IsIncluded = true);

        h.OnUi(() => Row(session, Pleiades.Light).IsIncluded = false);

        Assert.True(Row(session, Pleiades.Light).ShowsChildren);
        Assert.True(Row(session, LightCam).IsIncluded);
        Assert.Equal(1.0, Row(session, Pleiades.Light).RowOpacity);   // path to a ticked row
        Assert.Equal([Date18, LightCam], Sources(session));
        Assert.Equal([Pleiades.Light], Skipped(session, Date18));
        Assert.Null(session.CopySources[1].SkippedFolders);
        Assert.True(session.Covers(L1));
        Assert.Equal("3 light frames", h.Page.FrameCountText);
        Assert.Equal("2 folders", h.Page.FolderCountText);
        Assert.Equal("1 folder skipped", session.SkippedText);
    }

    [Fact]
    public void TickingTheYearRow_LeavesAnotherNightsDateFolderUnticked_WithItsReason()
    {
        using var h = ExportHarness.Create(Catalogue(), [N18], listSubfolders: folder => Is(folder, Year) ? [Date15, Date18] : []);
        var session = h.Page.Sessions[0];
        Settle(session);

        Row(session, Year).IsIncluded = true;
        Settle(session);

        var other = Child(Row(session, Year), "Date_2026-10-15");
        Assert.False(other.IsIncluded);
        Assert.Equal("NGC 7000's lights", other.RowTag);
        Assert.Equal([Year], Sources(session));
        Assert.Equal([Date15], Skipped(session, Year));
        Assert.Equal(0, session.ChosenIndex);
    }

    [Fact]
    public void TickingAListedSiblingDateFolder_BoundsTheReadsByTheYearRow_AndExcludesItsForeignLights()
    {
        using var h = ExportHarness.Create(Catalogue(), [N18],
            listSubfolders: folder => Is(folder, Year) ? [Date15, Date18] : [],
            readForeignLights: (folder, _, _, _) => PathConfinement.IsUnderOrEqual(folder, ForeignLight)
                ? [new WbppFramePath(Guid.NewGuid(), ForeignLight, 1_000)]
                : []);
        var session = h.Page.Sessions[0];
        Settle(session);
        Reopen(h, Row(session, Year));
        Settle(session);

        Child(Row(session, Year), "Date_2026-10-15").IsIncluded = true;

        Assert.Equal([Date15, Lights], Sources(session));
        Assert.Equal(0, session.ChosenIndex);   // the year row holds both sources
        Until(() => session.ForeignExcluded.Any(frame => Is(frame.FilePath, ForeignLight)));
    }

    [Fact]
    public void UntickingEveryRow_BlamesTheTicks_NotTheFilter()
    {
        using var h = ExportHarness.Create(Catalogue(), [N18]);
        var session = h.Page.Sessions[0];
        // Under the post gate, so the load's foreign read cannot restate the footer mid-write.
        h.OnUi(() => Row(session, Lights).IsIncluded = false);

        Assert.Empty(session.CopySources);
        Assert.Equal(5, session.ChosenIndex);   // the scan stays bounded by the default level
        Assert.Equal(WbppExportViewModel.SkipsEmptyWarning, h.Page.EmptyExportText);
        Assert.Equal("", session.ChosenOwnName);
    }

    [Fact]
    public void ATickedRowUnderATickedParent_AddsNothing_AndListingLandsTicked()
    {
        using var h = ExportHarness.Create(Catalogue(), [N18], listSubfolders: folder => Is(folder, Lights) ? [Path.Combine(Lights, "calibrated")] : []);
        var session = h.Page.Sessions[0];
        Settle(session);

        var calibrated = Child(Row(session, Lights), "calibrated");
        Assert.True(calibrated.IsIncluded);
        Assert.Equal([Lights], Sources(session));
        Assert.Null(session.CopySources[0].SkippedFolders);

        calibrated.IsIncluded = false;
        Assert.Equal([Path.Combine(Lights, "calibrated")], Skipped(session, Lights));
        Assert.Equal(FolderNodeViewModel.SkippedText, calibrated.RowTag);
    }

    [Fact]
    public void OnLoad_OnlyTheCopiedRowLists_AndEveryRowListsOnce()
    {
        var calls = new List<string>();
        using var h = ExportHarness.Create(Catalogue(), [N18], listSubfolders: folder =>
        {
            lock (calls)
            {
                calls.Add(folder);
            }

            return Is(folder, Date18) ? [Pleiades.Flat, Pleiades.Light] : [];
        });
        var session = h.Page.Sessions[0];
        Settle(session);
        lock (calls)
        {
            Assert.Equal([Lights], calls);   // the path above the ticked lights folder lists nothing
        }

        // The user's reopening lists the date row. FLAT is listed under an unticked parent: it is
        // drawn, unticked, with no tag.
        var date = Row(session, Date18);
        Reopen(h, date);
        Settle(session);
        var flat = Child(date, "FLAT");
        Assert.False(flat.IsIncluded);
        Assert.Equal("", flat.RowTag);

        // Collapsing and reopening reads nothing; ticking lists the open rows it brings into the
        // copied area, once.
        h.OnUi(() => date.ToggleExpandCommand.Execute(null));
        h.OnUi(() => date.ToggleExpandCommand.Execute(null));
        h.OnUi(() => date.IsIncluded = true);
        Settle(session);
        h.OnUi(() => date.IsIncluded = false);
        h.OnUi(() => date.IsIncluded = true);
        Settle(session);
        lock (calls)
        {
            Assert.Equal([Date18, Pleiades.Light, LightCam, Target, Lights], calls.Order(StringComparer.OrdinalIgnoreCase));
        }

        // And FLAT, which a tick reached closed, lists on its own first expand only.
        flat.IsExpanded = true;
        Settle(flat);
        lock (calls)
        {
            Assert.Equal(6, calls.Count);
            Assert.Equal(Pleiades.Flat, calls[^1]);
        }
    }

    private static (ExportHarness Harness, List<string> Calls) YearHarness()
    {
        var calls = new List<string>();
        var h = ExportHarness.Create(Catalogue(), [N18], listSubfolders: folder =>
        {
            lock (calls)
            {
                calls.Add(folder);
            }

            return Is(folder, Year) ? [Date15, Date18] : [];
        });
        Settle(h.Page.Sessions[0]);
        return (h, calls);
    }

    private static int CallsFor(List<string> calls, string folder)
    {
        lock (calls)
        {
            return calls.Count(c => Is(c, folder));
        }
    }

    [Fact]
    public void TheYearRowOnThePath_StartsOpen_AndShowsOnlyItsChainChild()
    {
        var (h, calls) = YearHarness();
        using var _ = h;
        var year = Row(h.Page.Sessions[0], Year);

        Assert.True(year.IsExpanded);
        Assert.Equal([Date18], year.Children.Select(c => c.Level.Path));
        Assert.Equal(0, CallsFor(calls, Year));
        Assert.Equal("-", year.ExpandLabel);   // open, so a press closes it
    }

    [Fact]
    public void PressingTheOpenYearRow_ClosesItWithNoRead_AndTheNextPressListsIt()
    {
        var (h, calls) = YearHarness();
        using var _ = h;
        var session = h.Page.Sessions[0];
        var year = Row(session, Year);

        h.OnUi(() => year.ToggleExpandCommand.Execute(null));

        Assert.False(year.IsExpanded);
        Assert.Equal("+", year.ExpandLabel);
        Assert.Equal(0, CallsFor(calls, Year));

        h.OnUi(() => year.ToggleExpandCommand.Execute(null));
        Settle(session);

        Assert.True(year.IsExpanded);
        Assert.Equal(["Date_2026-10-15", "Date_2026-10-18"], year.Children.Select(c => c.FolderName));
        Assert.Same(Row(session, Date18), Child(year, "Date_2026-10-18"));
        Assert.Equal(1, CallsFor(calls, Year));
        Assert.Equal("-", year.ExpandLabel);
    }

    [Fact]
    public void TickingANeverListedRow_ListsIt()
    {
        var (h, calls) = YearHarness();
        using var _ = h;
        var session = h.Page.Sessions[0];
        var year = Row(session, Year);

        h.OnUi(() => year.IsIncluded = true);
        Settle(session);

        Assert.Equal(1, CallsFor(calls, Year));
        Assert.False(Child(year, "Date_2026-10-15").IsIncluded);   // the cascade read its facts
        Assert.Equal([Date15], Skipped(session, Year));
    }

    [Fact]
    public void TheDefaultTickedLightsFolder_StillListsOnLoad()
    {
        var (h, calls) = YearHarness();
        using var _ = h;
        var lights = Row(h.Page.Sessions[0], Lights);

        Assert.Equal(1, CallsFor(calls, Lights));
        Assert.True(lights.IsLeaf);   // listed, nothing inside
        Assert.False(lights.ShowsExpander);
    }

    private static void Generate(WbppExportViewModel page)
        => Assert.True(page.GenerateCommand.ExecuteAsync(WbppScriptType.PowerShell).Wait(TimeSpan.FromSeconds(30)), "The command did not finish within 30 s.");

    [Fact]
    public void AChainChildIsMergedIntoTheListingInPlace_AndKeptWhenTheDiskDoesNotListIt()
    {
        using (var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: folder =>
            Is(folder, Date) ? [Flat, HaFolder, Oiii] : []))
        {
            var session = h.Page.Sessions[0];
            var date = Row(session, Date);
            Reopen(h, date);
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
            Reopen(h, date);
            Settle(session);

            Assert.Equal(["FLAT", "Ha"], date.Children.Select(c => c.FolderName));
            Assert.True(Child(date, "Ha").IsChain);
        }
    }

    [Fact]
    public void ForkedLevels_ShowBothLeavesUnderOneParent_BothTicked()
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
        Assert.True(ha.IsIncluded);
        Assert.True(oiii.IsIncluded);
        Assert.True(date.IsIncluded);
        Assert.Equal([Date], Sources(session));
        Assert.Equal(2, date.ChainChildren.Count);
    }

    [Fact]
    public void UntickingARowUnderATickedParent_IsASkip_AndRetickingTheParentReseedsIt()
    {
        using var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: folder =>
            Is(folder, Date) ? [Flat, HaFolder] : []);
        var session = h.Page.Sessions[0];
        Settle(session);
        var date = Row(session, Date);
        date.IsIncluded = true;
        Settle(session);
        var flat = Child(date, "FLAT");

        flat.IsIncluded = false;
        Assert.Equal("1 folder skipped", session.SkippedText);
        Assert.True(session.HasSkippedText);
        Assert.True(flat.IsSkipped);
        Assert.Equal([Flat], Skipped(session, Date));

        date.IsIncluded = false;
        Assert.Equal("", session.SkippedText);
        Assert.False(session.HasSkippedText);
        Assert.False(flat.IsIncluded);
        Assert.Equal([HaFolder], Sources(session));

        date.IsIncluded = true;
        Assert.True(flat.IsIncluded);   // the cascade seeds it
        Assert.Equal("", session.SkippedText);
    }

    [Fact]
    public void UntickingAParent_KeepsDrawingItsChildren_AndOnlyTheParentIsExcluded()
    {
        using var h = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1], listSubfolders: folder =>
            Is(folder, Date) ? [Flat]
            : Is(folder, Flat) ? [Deeper]
            : []);
        var session = h.Page.Sessions[0];
        Settle(session);
        var date = Row(session, Date);
        date.IsIncluded = true;
        Settle(session);
        var flat = Child(date, "FLAT");

        flat.IsExpanded = true;
        Settle(flat);
        var deeper = flat.Children.Single();
        Assert.True(flat.ShowsChildren);
        Assert.True(deeper.IsIncluded);

        deeper.IsIncluded = false;
        flat.IsIncluded = false;

        Assert.True(flat.ShowsChildren);
        Assert.Equal([Flat], Skipped(session, Date));
        Assert.False(deeper.IsIncluded);

        flat.IsIncluded = true;

        Assert.True(deeper.IsIncluded);
        Assert.Null(session.CopySources.Single().SkippedFolders);
    }

    [Fact]
    public void ATickChange_RefreshesTheFooterAndTheNightSummary_AndWithdrawsTheScript()
    {
        var library = new Library().Frame(N1, Ha(N1, "a.fits"), 100).Frame(N1, Path.Combine(Oiii, "b.fits"), 200);
        using var h = ExportHarness.Create(library, [N1], listSubfolders: folder =>
            Is(folder, Date) ? [HaFolder, Oiii] : []);
        var session = h.Page.Sessions[0];
        Settle(session);
        h.Page.StagingPath = h.TempFolder("staging");
        h.Page.CommitStagingCommand.Execute(null);
        h.Page.SavedConfirmationDuration = TimeSpan.Zero;
        h.Destination = h.ScriptPath("wbpp.ps1");
        Generate(h.Page);
        Assert.True(h.Page.HasScript);
        Assert.Equal("2 light frames", h.Page.FrameCountText);

        var oiii = Row(session, Oiii);
        Assert.True(oiii.IsIncluded);
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
        Reopen(h, date);
        Settle(session);

        Assert.Equal(FolderNodeViewModel.UnreadableText, date.ChildrenText);
        Assert.True(date.HasChildrenText);
        Assert.Equal(["Ha"], date.Children.Select(c => c.FolderName));
        Assert.True(ha.IsLeaf);
        Assert.False(ha.ShowsExpander);
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
        Assert.Null(session.CopySources.Single().SkippedFolders);
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
    public void UntickingTheOnlyLightsFolder_ThenTickingItAlone_SkipsNothing()
    {
        var library = new Library().Frame(N1, Ha(N1, "a.fits")).Frame(N1, Ha(N1, "b.fits"));
        using var h = ExportHarness.Create(library, [N1], listSubfolders: folder =>
            Is(folder, Date) ? [HaFolder] : []);
        var session = h.Page.Sessions[0];
        var ha = Row(session, HaFolder);
        Settle(session);
        Row(session, Date).IsIncluded = true;
        ha.IsIncluded = false;
        Assert.Equal([HaFolder], Skipped(session, Date));

        Row(session, Date).IsIncluded = false;
        ha.IsIncluded = true;

        Assert.Equal([HaFolder], Sources(session));
        Assert.Null(session.CopySources.Single().SkippedFolders);
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
        Row(session, Date).IsIncluded = true;
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
        Settle(session);
        Row(session, Date).IsIncluded = true;
        Settle(session);

        var foreign = Child(Row(session, Light), "Target_M 45");
        Assert.False(foreign.IsIncluded);
        Assert.Equal("M 45's lights", foreign.ReasonTag);
        Assert.True(foreign.HasReasonTag);
        Assert.Equal("M 45's lights", foreign.RowTag);
        Assert.True(Child(Row(session, Light), "Target_A").IsIncluded);
        Assert.Equal("", Child(Row(session, Light), "Target_A").RowTag);
        Assert.Equal([Path.Combine(Light, "Target_M 45")], Skipped(session, Date));
    }

    [Fact]
    public void AFolderHoldingOnlyThisTargetsOtherNight_StartsUnticked_AsOtherNight()
    {
        var date2 = Path.Combine(M31, N2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var library = new Library().Frame(N1, Ha(N1, "a.fits")).Frame(N2, Ha(N2, "b.fits"));
        using var h = ExportHarness.Create(library, [N1], listSubfolders: folder =>
            Is(folder, M31) ? [Date, date2] : []);
        var session = h.Page.Sessions[0];
        Settle(session);
        Row(session, M31).IsIncluded = true;
        Settle(session);

        var other = Child(Row(session, M31), N2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Assert.False(other.IsIncluded);
        Assert.Equal("other night", other.ReasonTag);
        Assert.Equal("other night", other.RowTag);
        Assert.True(Row(session, Date).IsIncluded);
        Assert.Equal([date2], Skipped(session, M31));
    }

    [Fact]
    public void TickingAnAutoUntickedRow_ClearsItsReason_AndATickElsewhereLeavesIt()
    {
        using var h = ForeignTargetHarness();
        var session = h.Page.Sessions[0];
        var date = Row(session, Date);
        Settle(session);
        date.IsIncluded = true;
        Settle(session);
        var foreign = Child(Row(session, Light), "Target_M 45");

        foreign.IsIncluded = true;

        Assert.Null(foreign.ReasonTag);
        Assert.False(foreign.HasReasonTag);
        Assert.Equal("", foreign.RowTag);
        Assert.Null(session.CopySources.Single().SkippedFolders);

        // A collapse and re-expand, and a filter change, leave the user's tick standing.
        date.IsExpanded = false;
        date.IsExpanded = true;
        h.Page.SetExcludedFrames([]);
        Settle(session);

        Assert.True(foreign.IsIncluded);
        Assert.Null(foreign.ReasonTag);
        Assert.Null(session.CopySources.Single().SkippedFolders);

        // Unticked by hand, the row reads the plain skipped tag: the reason is gone for good.
        foreign.IsIncluded = false;
        Assert.Equal(FolderNodeViewModel.SkippedText, foreign.RowTag);
    }

    [Fact]
    public void TheDefaultLevel_StartsTicked_EvenWhenContaminated_AndItsChainChildSeedsByItsOwnFacts()
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

        session.Rows[0].IsIncluded = false;
        session.Rows[1].IsIncluded = true;

        Assert.Null(session.Rows[1].ReasonTag);
        Assert.Equal(1, session.ChosenIndex);
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

        var destination = h.Page.CopyDestination!;
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

    // Copies the page's plan through the in-app copier over real files that mirror the fake
    // library, and returns every written file relative to the copy destination, sorted.
    private static async Task<IReadOnlyList<string>> StageAsync(ExportHarness h, params string[] files)
    {
        var staging = h.TempFolder("staging");
        h.Page.StagingPath = staging;
        h.Page.CommitStagingCommand.Execute(null);
        h.Page.SavedConfirmationDuration = TimeSpan.Zero;

        var mirror = h.TempFolder("mirror");
        foreach (var file in files)
        {
            var real = Path.Combine(mirror, Path.GetRelativePath(Library.Root, file));
            Directory.CreateDirectory(Path.GetDirectoryName(real)!);
            File.WriteAllBytes(real, [1]);
        }

        var destination = h.Page.CopyDestination!;
        var created = new List<string>();
        h.Page.StagingIoFor = _ => new StagingIo(
            _ => new MemoryStream([1]),
            folder => new DirectoryInfo(PathConfinement.IsUnderOrEqual(Library.Root, folder)
                ? Path.Combine(mirror, Path.GetRelativePath(Library.Root, folder))
                : folder).GetFileSystemInfos(),
            _ => null,
            _ => { },
            path =>
            {
                lock (created)
                {
                    created.Add(Path.GetRelativePath(destination, path));
                }

                return new MemoryStream();
            });
        var result = await h.Page.RunCopyAsync(new Progress<StagingProgress>(), io => io, CancellationToken.None);

        Assert.Empty(result.Failed);
        return [.. created.Order(StringComparer.OrdinalIgnoreCase)];
    }

    // A foreign read over a multi-night library: every other night's frame under the folder.
    private static Func<string, string, DateOnly, CancellationToken, IReadOnlyList<WbppFramePath>> OtherNights(
        Library library, params (DateOnly Night, string Path)[] frames)
        => (folder, _, night, _) => [.. frames
            .Where(frame => frame.Night != night && PathConfinement.IsUnderOrEqual(folder, frame.Path))
            .Select(frame => new WbppFramePath(library.Ids[frame.Path], frame.Path, 1_000))];

    [Fact]
    public async Task TwoNightsSharingOneChosenFolder_AreOneJobThatCopiesBothNightsFrames()
    {
        // A flat layout: both nights' frames in one folder, so each night's foreign read returns
        // the other night's frame, and both nights choose that same folder.
        var flat = Path.Combine(M31, "Ha");
        var n1 = Path.Combine(flat, "n1.fits");
        var n2 = Path.Combine(flat, "n2.fits");
        var library = new Library().Frame(N1, n1).Frame(N2, n2);
        using var h = ExportHarness.Create(library, [N1, N2], listSubfolders: _ => [], readForeignLights: OtherNights(library, (N1, n1), (N2, n2)));
        foreach (var session in h.Page.Sessions)
        {
            Settle(session);
        }

        Until(() => h.Page.Sessions.All(session => session.ForeignExcluded.Count == 1));
        var staged = await StageAsync(h, n1, n2);
        h.Destination = h.ScriptPath("wbpp.ps1");
        Generate(h.Page);

        // Each night lists the other's frame as foreign, so the merged job, which stands for both
        // nights, excludes neither, and both nights' frames land.
        var job = Assert.Single(h.Page.ScriptText!.Split('\n'), line => line.TrimStart().StartsWith("@{ Src", StringComparison.Ordinal));
        Assert.DoesNotContain("ExcludeFiles", job, StringComparison.Ordinal);
        Assert.Equal([@"M31\Ha\n1.fits", @"M31\Ha\n2.fits"], staged);
    }

    [Fact]
    public async Task ANightNestedInsideAnotherNightsFolder_StagesBothNightsFrames_LessItsOwnRejects()
    {
        // Filter folders holding both nights: night 1 has Ha and OIII, so it picks M31; night 2
        // has Ha only, so it picks M31\Ha, which night 1's job copies. Night 1 lists night 2's Ha
        // frames as foreign; the merged job still copies the one night 2 keeps.
        var ha = Path.Combine(M31, "Ha");
        var oiii = Path.Combine(M31, "OIII");
        var a1 = Path.Combine(ha, "a1.fits");
        var o1 = Path.Combine(oiii, "o1.fits");
        var a2 = Path.Combine(ha, "a2.fits");
        var r2 = Path.Combine(ha, "r2.fits");
        var library = new Library().Frame(N1, a1).Frame(N1, o1).Frame(N2, a2).Frame(N2, r2);
        using var h = ExportHarness.Create(library, [N1, N2],
            listSubfolders: folder => Is(folder, M31) ? [ha, oiii] : [],
            readForeignLights: OtherNights(library, (N1, a1), (N1, o1), (N2, a2), (N2, r2)));
        var (night1, night2) = (h.Page.Sessions[0], h.Page.Sessions[1]);
        Settle(night1);
        Settle(night2);
        h.Page.SetExcludedFrames([library.Ids[r2]]);

        Until(() => night1.ForeignExcluded.Count == 2 && night2.ForeignExcluded.Count == 1);
        var staged = await StageAsync(h, a1, o1, a2, r2);

        Assert.Equal([@"M31\Ha\a1.fits", @"M31\Ha\a2.fits", @"M31\OIII\o1.fits"], staged);
    }

    [Fact]
    public async Task ASharedFolderWithASubfolderOneNightSkips_CopiesWhatTheOtherNightKeeps()
    {
        // Both nights pick M31. Night 2 unticks OIII, so its list names nothing there; night 1
        // lists night 2's OIII frame as foreign and rejects its own o1r. Only o1 of OIII copies.
        var ha = Path.Combine(M31, "Ha");
        var oiii = Path.Combine(M31, "OIII");
        var a1 = Path.Combine(ha, "a1.fits");
        var o1 = Path.Combine(oiii, "o1.fits");
        var o1r = Path.Combine(oiii, "o1r.fits");
        var a2 = Path.Combine(ha, "a2.fits");
        var o2 = Path.Combine(oiii, "o2.fits");
        var library = new Library().Frame(N1, a1).Frame(N1, o1).Frame(N1, o1r).Frame(N2, a2).Frame(N2, o2);
        using var h = ExportHarness.Create(library, [N1, N2],
            listSubfolders: folder => Is(folder, M31) ? [ha, oiii] : [],
            readForeignLights: OtherNights(library, (N1, a1), (N1, o1), (N1, o1r), (N2, a2), (N2, o2)));
        foreach (var session in h.Page.Sessions)
        {
            Row(session, M31).IsIncluded = true;
            Settle(session);
        }

        var night2 = h.Page.Sessions[1];
        Row(night2, oiii).IsIncluded = false;
        h.Page.SetExcludedFrames([library.Ids[o1r]]);

        Until(() => h.Page.Sessions[0].ForeignExcluded.Count == 2 && night2.ForeignExcluded.Count == 1);
        var staged = await StageAsync(h, a1, o1, o1r, a2, o2);

        Assert.Equal([@"M31\Ha\a1.fits", @"M31\Ha\a2.fits", @"M31\OIII\o1.fits"], staged);
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

        date.IsIncluded = true;
        Until(() => date.FrameCountText == "1 of 2 files");

        // An untick, and the same shallow tick again, read nothing more.
        date.IsIncluded = false;
        date.IsIncluded = true;
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

    // ------------------------------------------------------------------ calibration on a row

    private static FolderNodeViewModel Node(string path, FolderNodeViewModel? parent = null, bool catalogued = false, int frames = 0, int depth = 1)
        => new(new SubfolderLevel(new FolderLevel(path, depth, frames, [], [], frames * 1_000L), catalogued), false, -1, true, "M 45", parent, _ => { }, (_, _) => { });

    private static CalibrationRow Matched(string folder, int selected, int unselected, string note)
        => new(folder,
            [.. Enumerable.Range(0, selected).Select(i => new FrameMatch(F(Path.Combine(folder, $"f{i}.fits"), CalibrationType.Flat, P(), night: N18), true, note))
                .Concat(Enumerable.Range(0, unselected).Select(i => new FrameMatch(F(Path.Combine(folder, $"h{i}.fits"), CalibrationType.Flat, P(rot: 96.26), night: N18), false, "rotation 96.26")))],
            selected > 0, note);

    [Fact]
    public void ACalibrationRow_ReadsItsFiguresAndNote_InPlaceOfTheCatalogueText()
    {
        var node = Node(Flats);
        Assert.Equal(FolderNodeViewModel.NotCataloguedText, node.FrameCountText);
        Assert.Equal("", node.CalibrationText);
        Assert.False(node.HasNoteTag);

        node.SetCalibrationRow(Matched(Flats, 2, 1, "same night"));

        Assert.Equal("2 of 3 files, " + MetricText.Bytes(200), node.CalibrationText);
        Assert.True(node.HasCalibration);
        Assert.Equal("same night", node.NoteTag);
        Assert.True(node.HasNoteTag);
        Assert.Equal("", node.FrameCountText);
        Assert.Equal("", node.BytesText);

        node.SetCalibrationRow(Matched(Flats96, 0, 1, "rotation 96.26"));
        Assert.Equal("1 file, " + MetricText.Bytes(100), node.CalibrationText);

        node.SetCalibrationRow(null);
        Assert.Equal(FolderNodeViewModel.NotCataloguedText, node.FrameCountText);
        Assert.False(node.HasNoteTag);
    }

    [Fact]
    public void ARowHoldingLightsAndFlatsTogether_KeepsItsLightCount_AndAddsTheCalibrationText()
    {
        var node = Node(Lights, catalogued: true, frames: 3);
        node.SetCalibrationRow(Matched(Lights, 3, 1, "same night"));

        Assert.Equal("3 light frames", node.FrameCountText);
        Assert.Equal(MetricText.Bytes(3_000), node.BytesText);
        Assert.Equal("3 of 4 files, " + MetricText.Bytes(300), node.CalibrationText);
    }

    [Fact]
    public void AnInteriorRow_SumsTheCalibrationBeneathIt()
    {
        var flat = Node(Pleiades.Flat);
        var cam = Node(FlatCam, flat);
        flat.AddChild(cam);
        var flats = Node(Flats, cam);
        var flats96 = Node(Flats96, cam);
        cam.AddChild(flats);
        cam.AddChild(flats96);
        flats.SetCalibrationRow(Matched(Flats, 2, 1, "same night"));
        flats96.SetCalibrationRow(Matched(Flats96, 0, 1, "rotation 96.26"));
        flat.RefreshCalibrationText();
        cam.RefreshCalibrationText();

        // 2 selected of the matched row plus every file of the row with no selection.
        Assert.Equal("3 files, " + MetricText.Bytes(300), cam.CalibrationText);
        Assert.Equal("3 files, " + MetricText.Bytes(300), flat.CalibrationText);
        Assert.Equal("", flat.FrameCountText);
        Assert.Equal("", flat.NoteTag);
    }

    [Fact]
    public void AListingMergesACreatedChildInPlace_WhateverItsCase_AndKeepsOneTheListingDidNotName()
    {
        var date = Node(Date18);
        var flat = Node(Pleiades.Flat, date);
        date.AddChild(flat);

        date.SetChildren([
            new SubfolderLevel(new FolderLevel(Path.Combine(Date18, "flat"), 3, 0, [], [], 0), false),
            new SubfolderLevel(new FolderLevel(Pleiades.Light, 3, 3, [], [], 3_000), true)]);

        Assert.Equal(["FLAT", "LIGHT"], date.Children.Select(c => c.FolderName));
        Assert.Same(flat, date.Children[0]);
        Assert.Equal(1, date.Children.Count(c => string.Equals(c.Level.Path, Pleiades.Flat, StringComparison.OrdinalIgnoreCase)));

        var other = Node(Date15);
        var borrowed = Node(Path.Combine(Date15, "FLAT"), other);
        other.AddChild(borrowed);
        other.SetChildren([new SubfolderLevel(new FolderLevel(Path.Combine(Date15, "LIGHT"), 3, 0, [], [], 0), false)]);
        Assert.Equal(["LIGHT", "FLAT"], other.Children.Select(c => c.FolderName));
    }

    [Fact]
    public void AnEmptyListing_MakesALeaf_AndAChildAddedLater_ReopensIt()
    {
        var flats = Node(Flats);
        flats.IsExpanded = true;
        flats.SetChildren([]);

        Assert.True(flats.IsLeaf);
        Assert.False(flats.ShowsExpander);
        Assert.False(flats.ShowsChildren);
        Assert.Equal("", flats.ChildrenText);

        var child = Node(Path.Combine(Flats, "old"), flats);
        flats.AddChild(child);

        Assert.False(flats.IsLeaf);
        Assert.True(flats.ShowsExpander);
        Assert.True(flats.ShowsChildren);

        flats.RemoveChild(child);
        Assert.Empty(flats.Children);
        Assert.True(flats.IsLeaf);
    }

    [Fact]
    public void HasTickedDescendant_FollowsTheTicksBeneath_AndRaisesTheAncestorsOpacity()
    {
        var cam = Node(FlatCam);
        var flats = Node(Flats, cam);
        cam.AddChild(flats);
        cam.IsIncluded = false;
        flats.IsIncluded = false;
        Assert.False(cam.HasTickedDescendant);

        var raised = new List<string?>();
        cam.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        flats.IsIncluded = true;

        Assert.True(cam.HasTickedDescendant);
        Assert.Contains(nameof(FolderNodeViewModel.RowOpacity), raised);
        Assert.Equal([flats], cam.Descendants());
    }
}
