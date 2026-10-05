using System.Globalization;
using System.Text.Json;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Wbpp;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels.Wbpp;

/// <summary>
/// A synthetic catalogue for the export page's cases: a set of scan roots, one LIGHT frame list
/// per night, and the whole catalogue the contamination index is built from.
/// </summary>
/// <remarks>Every path here is a fictional <c>C:\p16lib</c> path and nothing in these cases opens
/// one: the page reads database rows and settings values and never a user file.</remarks>
internal sealed class Library
{
    /// <summary>The fictional library root the cases build under.</summary>
    public const string Root = @"C:\p16lib";

    /// <summary>A second fictional root, for the several-scan-roots shape.</summary>
    public const string SecondRoot = @"C:\p16lib2";

    private readonly List<WbppCataloguePath> _catalogue = [];
    private readonly Dictionary<DateOnly, List<WbppFramePath>> _nights = [];

    /// <summary>The page's own group key, passed through unchanged. Never synthesised from a
    /// <see cref="Guid"/>: a resolved key is stored upper cased and would then read as another
    /// target's.</summary>
    public string GroupKey { get; set; } = "obj:M 31";

    /// <summary>The target's display name.</summary>
    public string TargetName { get; set; } = "M 31";

    /// <summary>The configured scan roots.</summary>
    public List<string> ScanRoots { get; } = [Root];

    /// <summary>Every frame's image id, keyed by its path, so a case can exclude one by name.
    /// </summary>
    public Dictionary<string, Guid> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds one of this target's own LIGHT frames on one night.</summary>
    public Library Frame(DateOnly night, string path, long? size = 1_000L)
    {
        var id = Guid.NewGuid();
        Ids[path] = id;
        Night(night).Add(new WbppFramePath(id, path, size));
        _catalogue.Add(new WbppCataloguePath(path, GroupKey, TargetName, night, true, size));
        return this;
    }

    /// <summary>Adds a catalogued file belonging to someone else, which is what contaminates a
    /// level and what the byte figure counts.</summary>
    public Library Foreign(
        string path,
        DateOnly? night = null,
        string targetKey = "obj:NGC 7000",
        string targetName = "NGC 7000",
        long? size = 1_000L,
        bool light = true)
    {
        _catalogue.Add(new WbppCataloguePath(path, targetKey, targetName, night, light, size));
        return this;
    }

    /// <summary>Declares a night with no frame at all.</summary>
    public Library EmptyNight(DateOnly night)
    {
        Night(night);
        return this;
    }

    /// <summary>The query's answer over the requested nights, in the requested order.</summary>
    public WbppPaths Build(IReadOnlyList<DateOnly> order)
    {
        foreach (var night in order)
        {
            Night(night);
        }

        var byNight = new Dictionary<DateOnly, IReadOnlyList<WbppFramePath>>();
        foreach (var night in order)
        {
            byNight[night] = _nights[night];
        }

        return new WbppPaths(
            byNight,
            ContaminationIndex.Build([.. ScanRoots], _catalogue));
    }

    private List<WbppFramePath> Night(DateOnly night)
    {
        if (!_nights.TryGetValue(night, out var list))
        {
            list = [];
            _nights[night] = list;
        }

        return list;
    }
}

/// <summary>
/// Builds a <see cref="WbppExportViewModel"/> over a <see cref="Library"/> with every collaborator
/// recorded, so a case asserts what the page asked for as well as what it shows.
/// </summary>
internal sealed class ExportHarness : IDisposable
{
    private readonly string _appDataRoot =
        Directory.CreateTempSubdirectory("galactilog-t5a-appdata-").FullName;

    private ExportHarness(Library library, IReadOnlyList<DateOnly> nights)
    {
        Library = library;
        Nights = nights;
        Writer = new AppWriter(_appDataRoot);
    }

    /// <summary>The catalogue the page reads.</summary>
    public Library Library { get; }

    /// <summary>The checked nights, in the ledger's own order.</summary>
    public IReadOnlyList<DateOnly> Nights { get; }

    /// <summary>The application's one writer, rooted at a temporary folder.</summary>
    public AppWriter Writer { get; }

    /// <summary>The general document as the page reads it back.</summary>
    public GeneralSettings General { get; private set; } = new();

    /// <summary>How many times the page called <c>MutateGeneral</c>.</summary>
    public int MutateCalls { get; private set; }

    /// <summary>How many times the page read the paths. Exactly one per page open.</summary>
    public int ReadPathsCalls { get; private set; }

    /// <summary>What Copy script wrote.</summary>
    public List<string> Copied { get; } = [];

    /// <summary>The nights the page asked for detail on, in order.</summary>
    public List<DateOnly> DetailCalls { get; } = [];

    /// <summary>What the save dialog returns. Null is a cancelled dialog.</summary>
    public string? Destination { get; set; }

    /// <summary>The suggested file names the save dialog was offered.</summary>
    public List<string> SuggestedNames { get; } = [];

    /// <summary>What the folder picker returns. Null is a cancelled picker.</summary>
    public string? PickedFolder { get; set; }

    /// <summary>The page itself.</summary>
    public WbppExportViewModel Page { get; private set; } = null!;

    /// <summary>The gate a parked read waits on, or null when the read runs straight through.
    /// </summary>
    private ManualResetEventSlim? _gate;

    /// <summary>Lets a parked read finish and settles the page. The wait lives here rather than in
    /// a test body, because xUnit1031 is an error in this solution.</summary>
    public void Release()
    {
        _gate?.Set();
        Page.PendingLoad?.Wait(TimeSpan.FromSeconds(30));
    }

    /// <summary>A temporary folder that exists on disk, for a staging value that has to.</summary>
    public string TempFolder(string name)
    {
        var path = Path.Combine(_appDataRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Where a generated script may be written.</summary>
    public string ScriptPath(string name) => Path.Combine(_appDataRoot, name);

    public static ExportHarness Create(
        Library library,
        IReadOnlyList<DateOnly> nights,
        string? storedStaging = null,
        Func<DateOnly, SessionDetail?>? detail = null,
        JsonElement? exclusions = null,
        bool installPickers = true,
        Func<IReadOnlyList<WbppFrame>, IReadOnlyDictionary<Guid, FrameGrading>, string,
            Action<IReadOnlyCollection<Guid>>, object>? createPanel = null,
        bool parked = false)
    {
        var harness = new ExportHarness(library, nights)
        {
            _gate = parked ? new ManualResetEventSlim(false) : null,
            General = new GeneralSettings
            {
                ScanRoots = [.. library.ScanRoots],
                WbppStagingPathDocument = storedStaging is null
                    ? null
                    : WbppSettingsRead.WriteStagingPath(storedStaging),
                WbppExclusionsDocument = exclusions,
            },
        };

        harness.Page = new WbppExportViewModel(
            library.GroupKey,
            library.TargetName,
            nights,
            (_, requested, _, _) =>
            {
                harness.ReadPathsCalls++;
                harness._gate?.Wait(TimeSpan.FromSeconds(30));
                return library.Build(requested);
            },
            (_, night) =>
            {
                harness.DetailCalls.Add(night);
                return detail?.Invoke(night);
            },
            () => harness.General,
            mutate =>
            {
                harness.MutateCalls++;
                harness.General = mutate(harness.General);
                return harness.General;
            },
            harness.Writer,
            text =>
            {
                harness.Copied.Add(text);
                return Task.CompletedTask;
            },
            createPanel,
            post: action => action());

        if (installPickers)
        {
            harness.Page.ScriptDestinationPicker = name =>
            {
                harness.SuggestedNames.Add(name);
                return Task.FromResult(harness.Destination);
            };
            harness.Page.StagingFolderPicker = () => Task.FromResult(harness.PickedFolder);
        }

        if (!parked)
        {
            harness.Page.PendingLoad?.Wait(TimeSpan.FromSeconds(30));
        }

        return harness;
    }

    /// <summary>Replaces the configured scan roots, which is what a root added while the page is
    /// open looks like from here.</summary>
    public void SetScanRoots(params string[] roots)
        => General = General with { ScanRoots = roots };

    public void Dispose()
    {
        _gate?.Set();
        Page.Dispose();
        if (Directory.Exists(_appDataRoot))
        {
            Directory.Delete(_appDataRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Spec 12.13's Export for stacking page: the load, the session rows, the unavailable sentences,
/// the level tree, the totals and the two sets the footer and the tally describe
/// (<c>task5a.md</c> sections 10.1 to 10.5).
/// </summary>
public class WbppExportViewModelTests
{
    private static readonly DateOnly N1 = new(2025, 3, 20);
    private static readonly DateOnly N2 = new(2025, 3, 21);
    private static readonly DateOnly N3 = new(2025, 3, 22);

    private static string Ha(DateOnly night, string file) => Path.Combine(
        Library.Root, "M31", night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Ha", file);

    // The load wait lives in a helper rather than in a test body: xUnit1031 is an error in this
    // solution and a blocking wait inside a test method trips it. The shape MergeHistoryViewTests
    // and FrameListDialogWindowTests already use.
    private static void Settle(WbppExportViewModel page)
    {
        try
        {
            page.PendingLoad?.Wait(TimeSpan.FromSeconds(30));
        }
        catch (AggregateException exception) when (exception.InnerException is OperationCanceledException)
        {
            // A page disposed while its first read was parked cancels the load, which is the
            // silent close spec 12.13 asks for rather than an error the page states.
        }
    }

    // ------------------------------------------------------------------ 10.1 the load

    [Fact]
    public void Load_ThreeNights_ProducesThreeRowsInLedgerOrder_FromOneRead()
    {
        var library = new Library()
            .Frame(N1, Ha(N1, "a.fits"))
            .Frame(N1, Ha(N1, "b.fits"))
            .Frame(N2, Ha(N2, "a.fits"))
            .Frame(N3, Ha(N3, "a.fits"));

        using var harness = ExportHarness.Create(library, [N3, N2, N1]);

        // A failure looks like one query per night, which on a real library is five full table
        // reads where the design says one (W3).
        Assert.Equal(1, harness.ReadPathsCalls);

        Assert.Equal([N3, N2, N1], harness.Page.Sessions.Select(row => row.Night));
        Assert.Equal([1, 1, 2], harness.Page.Sessions.Select(row => row.Levels.TotalFrameCount));
        Assert.False(harness.Page.IsLoading);

        // Each night picks its own default, which is Task 2's and is never re-derived here.
        foreach (var session in harness.Page.Sessions)
        {
            Assert.Equal(session.Levels.DefaultLevelIndex, session.ChosenIndex);
            Assert.Equal("Ha", session.ChosenOwnName);
        }
    }

    [Fact]
    public async Task Load_RunsOffTheUiThread()
    {
        // WbppPathsQuery.Read is a synchronous read measured at around 1.7 seconds on a 200,000
        // row library, so a failure here freezes the window for that long. Awaited, never
        // blocked: xUnit1031 is an error in this solution.
        var library = new Library().Frame(N1, Ha(N1, "a.fits"));
        var caller = Environment.CurrentManagedThreadId;
        var readOn = 0;

        var page = new WbppExportViewModel(
            library.GroupKey,
            library.TargetName,
            [N1],
            (_, nights, _, _) =>
            {
                readOn = Environment.CurrentManagedThreadId;
                return library.Build(nights);
            },
            (_, _) => null,
            () => new GeneralSettings { ScanRoots = [.. library.ScanRoots] },
            mutate => mutate(new GeneralSettings()),
            new AppWriter(Directory.CreateTempSubdirectory("galactilog-t5a-thread-").FullName),
            _ => Task.CompletedTask,
            post: action => action());

        await page.PendingLoad!;

        Assert.NotEqual(caller, readOn);
        page.Dispose();
    }

    [Fact]
    public async Task Disposing_WhileTheReadIsParked_PublishesNothing()
    {
        // The page can be dismissed while the first read is still parked on a locked database. A
        // publish after that would raise property changes on a page nothing is bound to.
        var (page, release) = ParkedPage();
        page.Dispose();
        release();

        await Task.CompletedTask;
        Assert.Empty(page.Sessions);
        Assert.True(page.IsLoading);
    }

    [Fact]
    public void Load_ReadsTheDetailOncePerCheckedNight()
    {
        var library = new Library().Frame(N1, Ha(N1, "a.fits")).Frame(N2, Ha(N2, "a.fits"));

        using var harness = ExportHarness.Create(library, [N2, N1]);

        Assert.Equal([N2, N1], harness.DetailCalls);
    }

    [Fact]
    public void Load_FiltersToLightFramesAtTheOneProjection()
    {
        // The coordinator's binding rule from the Task 3a filter review: WbppFrame carries no
        // frame type and QualityFilter judges whatever it is handed, so a non-LIGHT row must be
        // dropped at the FrameRow to WbppFrame projection and nowhere else. A failure looks like a
        // calibration frame being judged, tallied, excludable and deducted from the footer.
        var library = new Library().Frame(N1, Ha(N1, "a.fits"));
        var lightId = library.Ids[Ha(N1, "a.fits")];
        var darkId = Guid.NewGuid();

        using var harness = ExportHarness.Create(
            library,
            [N1],
            detail: night => SessionCardViewModelTestFactory.PopulatedDetail(night) with
            {
                Frames =
                [
                    PreviewModalViewModelTestFactory.Row(lightId, Ha(N1, "a.fits"), "a.fits"),
                    PreviewModalViewModelTestFactory.Row(darkId, Ha(N1, "dark.fits"), "dark.fits"),
                ],
            });

        Assert.Equal([lightId], harness.Page.Frames.Select(frame => frame.ImageId));

        // And it cannot reach the per-file excludes either: an id the page never admitted resolves
        // to no frame and deducts nothing.
        var before = harness.Page.Totals!.FrameCount;
        harness.Page.SetExcludedFrames([darkId]);
        Assert.Equal(before, harness.Page.Totals!.FrameCount);
    }

    [Fact]
    public void RigKey_IsTheFirstFramesOwnLabel_AndAnAllUnknownLabelIsKept()
    {
        var library = new Library().Frame(N1, Ha(N1, "a.fits"));
        var id = library.Ids[Ha(N1, "a.fits")];

        using var harness = ExportHarness.Create(
            library,
            [N1],
            detail: night => SessionCardViewModelTestFactory.PopulatedDetail(night) with
            {
                Frames =
                [
                    PreviewModalViewModelTestFactory.Row(id, Ha(N1, "a.fits"), "a.fits") with
                    {
                        Rig = "Unknown / Unknown",
                    },
                ],
            });

        // A failure looks like the helpful implementation that folds an all-unknown label into the
        // default slot, which mixes every rig-less library's filter into one entry.
        Assert.Equal("Unknown / Unknown", harness.Page.RigKey);
        Assert.NotEqual(WbppQualityByRig.DefaultRigKey, harness.Page.RigKey);
    }

    [Fact]
    public void RigKey_WithNoFrameAtAll_IsTheDefaultSlot()
    {
        using var harness = ExportHarness.Create(new Library().EmptyNight(N1), [N1]);

        Assert.Equal(WbppQualityByRig.DefaultRigKey, harness.Page.RigKey);
    }

    [Fact]
    public void GroupKey_DifferingOnlyByCaseFromTheCatalogue_ReadsItsOwnLevelsAsClean()
    {
        // images.resolved_target_id is stored upper cased, so a resolved target's group key string
        // is an upper cased GUID while Guid.ToString() is lower cased. A failure looks like the
        // page seeing its own target as another target, marking every level contaminated and
        // moving the default one level deeper, silently.
        var id = Guid.NewGuid();
        var library = new Library { GroupKey = id.ToString().ToUpperInvariant() }
            .Frame(N1, Ha(N1, "a.fits"));

        using var harness = ExportHarness.Create(library, [N1]);

        var chosen = harness.Page.Sessions[0].Chosen!;
        Assert.Empty(chosen.Level.OtherTargets);
        Assert.False(chosen.Level.IsContaminated);
        Assert.Equal("Ha", chosen.Level.Path[^2..]);
    }

    [Fact]
    public void Load_PublishesThroughThePostSeam()
    {
        // The load runs off the UI thread and everything it publishes goes through post. A failure
        // looks like a property write from the query's own thread, which the headless harness does
        // not enforce and a real dispatcher kills the process over.
        var (page, posted) = PostCountingPage();

        Assert.Single(posted);
        Assert.Single(page.Sessions);
        page.Dispose();
    }

    private static (WbppExportViewModel Page, List<int> Count) PostCountingPage()
    {
        var posted = new List<int>();
        var library = new Library().Frame(N1, Ha(N1, "a.fits"));
        var page = new WbppExportViewModel(
            library.GroupKey,
            library.TargetName,
            [N1],
            (_, nights, _, _) => library.Build(nights),
            (_, _) => null,
            () => new GeneralSettings { ScanRoots = [.. library.ScanRoots] },
            mutate => mutate(new GeneralSettings()),
            new AppWriter(Directory.CreateTempSubdirectory("galactilog-t5a-post-").FullName),
            _ => Task.CompletedTask,
            post: action =>
            {
                posted.Add(1);
                action();
            });

        Settle(page);
        return (page, posted);
    }

    // ------------------------------------------------------ 10.2 the unavailable sentences

    [Fact]
    public void ANightWithNoFrame_ReadsItsOwnSentenceAndContributesNothing()
    {
        using var harness = ExportHarness.Create(new Library().EmptyNight(N1), [N1]);

        var row = harness.Page.Sessions[0];
        Assert.Equal(SessionLevelViewModel.NoFramesText, row.UnavailableText);
        Assert.False(row.HasLevels);
        Assert.Empty(row.Rows);
        Assert.Null(row.Chosen);
        Assert.Equal(0, harness.Page.Totals!.FolderCount);
    }

    [Fact]
    public void FramesInTheScanRootItself_ReadTheirOwnSentenceAndOfferNoTree()
    {
        var library = new Library().Frame(N1, Path.Combine(Library.Root, "a.fits"));

        using var harness = ExportHarness.Create(library, [N1]);

        var row = harness.Page.Sessions[0];
        Assert.Equal(SessionLevelViewModel.FramesInRootText, row.UnavailableText);
        Assert.Empty(row.Rows);
        Assert.Equal(0, harness.Page.Totals!.FolderCount);
    }

    [Fact]
    public void FramesAcrossSeveralScanRoots_ReadTheirOwnSentenceAndOfferNoTree()
    {
        var library = new Library().Frame(N1, Ha(N1, "a.fits"));
        library.ScanRoots.Add(Library.SecondRoot);
        library.Frame(N1, Path.Combine(Library.SecondRoot, "M31", "Ha", "b.fits"));

        using var harness = ExportHarness.Create(library, [N1]);

        var row = harness.Page.Sessions[0];
        Assert.Equal(SessionLevelViewModel.SeveralScanRootsText, row.UnavailableText);
        Assert.Empty(row.Rows);
        Assert.Equal(0, harness.Page.Totals!.FolderCount);
    }

    [Fact]
    public void EveryFrameUnderNoRoot_ReadsLikeTheRowAboveAndContributesNothing()
    {
        var library = new Library().Frame(N1, @"C:\elsewhere\M31\Ha\a.fits");

        using var harness = ExportHarness.Create(library, [N1]);

        var row = harness.Page.Sessions[0];
        Assert.Equal(LevelsUnavailable.NoScanRoot, row.Levels.Unavailable);
        Assert.Equal(SessionLevelViewModel.SeveralScanRootsText, row.UnavailableText);
        Assert.Equal(0, harness.Page.Totals!.FolderCount);
    }

    [Fact]
    public void SomeFramesUnderNoRoot_KeepTheLevelsAndStateTheLeftoverCount()
    {
        // Seam review ruling 3's other half, which is the pair an implementation collapses into
        // one. A failure looks like any rootless frame promoting a night to NoScanRoot, which
        // silently drops a night the user can still export most of.
        var library = new Library()
            .Frame(N1, Ha(N1, "a.fits"))
            .Frame(N1, Ha(N1, "b.fits"))
            .Frame(N1, @"C:\elsewhere\stray.fits");

        using var harness = ExportHarness.Create(library, [N1]);

        var row = harness.Page.Sessions[0];
        Assert.True(row.HasLevels);
        Assert.NotEmpty(row.Rows);
        Assert.True(row.HasLeftoverText);

        // Read from SessionLevels, never subtracted from two figures.
        Assert.Equal(1, row.Levels.FramesWithoutRoot);
        Assert.Equal(
            "1 frame of this night lies under no library folder and is left out of this export",
            row.LeftoverText);
        Assert.Equal(1, harness.Page.Totals!.FolderCount);
    }

    [Fact]
    public void SomeFramesInTheScanRootItself_KeepTheLevelsAndStateTheCount()
    {
        // The mixed shape: one frame directly in the root and the rest deeper. The night keeps its
        // levels and no level can carry the one in the root, so the row states it. A failure looks
        // like the count going unstated, which leaves a user expecting a frame the copy will not
        // move.
        var library = new Library()
            .Frame(N1, Ha(N1, "a.fits"))
            .Frame(N1, Path.Combine(Library.Root, "loose.fits"));

        using var harness = ExportHarness.Create(library, [N1]);

        var row = harness.Page.Sessions[0];
        Assert.True(row.HasLevels);
        Assert.NotEmpty(row.Rows);
        Assert.Equal(1, row.Levels.FramesInRootItself);
        Assert.True(row.HasRootFramesText);
        Assert.Equal(
            "1 frame of this night sits in the library root itself and is left out of this export",
            row.RootFramesText);
    }

    [Fact]
    public void EveryFrameInTheScanRootItself_IsTheUnavailableStateInstead()
    {
        var library = new Library().Frame(N1, Path.Combine(Library.Root, "loose.fits"));

        using var harness = ExportHarness.Create(library, [N1]);

        var row = harness.Page.Sessions[0];
        Assert.Equal(LevelsUnavailable.FramesInRootItself, row.Levels.Unavailable);
        Assert.Equal(SessionLevelViewModel.FramesInRootText, row.UnavailableText);
        Assert.False(row.HasRootFramesText);
    }

    [Fact]
    public void BothLeftoverCountsAreStatedWhenBothAreNonZero()
    {
        var library = new Library()
            .Frame(N1, Ha(N1, "a.fits"))
            .Frame(N1, Path.Combine(Library.Root, "loose.fits"))
            .Frame(N1, @"C:\elsewhere\stray.fits");

        using var harness = ExportHarness.Create(library, [N1]);

        var row = harness.Page.Sessions[0];
        Assert.True(row.HasLeftoverText);
        Assert.True(row.HasRootFramesText);
        Assert.NotEqual(row.LeftoverText, row.RootFramesText);
    }

    [Fact]
    public void TheLeftoverSentenceIsPluralWhenItHasTo()
    {
        var library = new Library()
            .Frame(N1, Ha(N1, "a.fits"))
            .Frame(N1, @"C:\elsewhere\one.fits")
            .Frame(N1, @"C:\elsewhere\two.fits");

        using var harness = ExportHarness.Create(library, [N1]);

        Assert.Equal(
            "2 frames of this night lie under no library folder and are left out of this export",
            harness.Page.Sessions[0].LeftoverText);
    }

    // ------------------------------------------------------ 10.3 the tree, the order, the badge

    [Fact]
    public void SiblingLevelsAtOneDepth_AreDrawnInPathOrder_AndTheSameOrderTwice()
    {
        var library = new Library()
            .Frame(N1, Path.Combine(Library.Root, "M31", "OIII", "a.fits"))
            .Frame(N1, Path.Combine(Library.Root, "M31", "Ha", "b.fits"));

        using var first = ExportHarness.Create(library, [N1]);
        using var second = ExportHarness.Create(library, [N1]);

        // A failure looks like the tree ordering siblings by whatever the set enumerated, which is
        // the Python's own defect and which draws a different tree on two openings of one page.
        var names = first.Page.Sessions[0].Rows.Select(row => row.FolderName).ToList();
        Assert.Equal(["M31", "Ha", "OIII"], names);
        Assert.Equal(names, second.Page.Sessions[0].Rows.Select(row => row.FolderName));

        // And the indent follows the level's own depth, not its position in the list.
        Assert.Equal(1 * (int)LevelRowViewModel.IndentStep, (int)first.Page.Sessions[0].Rows[0].Indent.Left);
        Assert.Equal(2 * (int)LevelRowViewModel.IndentStep, (int)first.Page.Sessions[0].Rows[1].Indent.Left);
        Assert.Equal(2 * (int)LevelRowViewModel.IndentStep, (int)first.Page.Sessions[0].Rows[2].Indent.Left);
    }

    [Fact]
    public void AContaminatedLevel_IsSelectableAndCarriesItsCostBadges()
    {
        var library = new Library()
            .Frame(N1, Ha(N1, "a.fits"))
            .Foreign(Path.Combine(Library.Root, "M31", "2025-03-20", "OIII", "x.fits"), N2)
            .Foreign(Path.Combine(Library.Root, "M31", "NGC7000", "y.fits"), N2);

        using var harness = ExportHarness.Create(library, [N1]);

        var m31 = harness.Page.Sessions[0].Rows.Single(row => row.FolderName == "M31");
        Assert.True(m31.HasOtherNights);
        Assert.True(m31.HasOtherTargets);
        Assert.Equal("+1 other night", m31.OtherNightsBadge);
        Assert.Equal("+1 other target", m31.OtherTargetsBadge);
        Assert.Equal("NGC 7000", m31.OtherTargetsTooltip);

        // Advisory, not a veto: choosing one is how a user deliberately copies a whole night
        // folder that two targets share.
        m31.IsChosen = true;
        Assert.Equal(m31.Index, harness.Page.Sessions[0].ChosenIndex);
        Assert.Equal("M31", harness.Page.Sessions[0].ChosenOwnName);
    }

    [Fact]
    public void AnOtherTargetSharingThisPagesOwnName_CarriesTheSameNameNote()
    {
        // Two different group keys can present one display name, a resolved target beside its
        // unresolved obj: twin being the usual pair. The entry is correct; a bare repeat of the
        // page's own name would read as the page contradicting itself about whose frames these
        // are.
        var library = new Library { GroupKey = "obj:M 31", TargetName = "M 31" }
            .Frame(N1, Ha(N1, "a.fits"))
            .Foreign(
                Path.Combine(Library.Root, "M31", "twin", "x.fits"),
                N2,
                targetKey: Guid.NewGuid().ToString(),
                targetName: "M 31");

        using var harness = ExportHarness.Create(library, [N1]);

        var m31 = harness.Page.Sessions[0].Rows.Single(row => row.FolderName == "M31");
        Assert.Equal("M 31 (separate entry, same name)", m31.OtherTargetsTooltip);

        // And an unrelated target reads as its own plain name.
        var other = new Library().Frame(N1, Ha(N1, "a.fits"))
            .Foreign(Path.Combine(Library.Root, "M31", "ngc", "y.fits"), N2);
        using var plain = ExportHarness.Create(other, [N1]);
        Assert.Equal(
            "NGC 7000",
            plain.Page.Sessions[0].Rows.Single(row => row.FolderName == "M31").OtherTargetsTooltip);
    }

    [Fact]
    public void ThePluralBadgeReadsCorrectly()
    {
        var library = new Library()
            .Frame(N1, Ha(N1, "a.fits"))
            .Foreign(Path.Combine(Library.Root, "M31", "a", "x.fits"), N2)
            .Foreign(Path.Combine(Library.Root, "M31", "b", "y.fits"), N3);

        using var harness = ExportHarness.Create(library, [N1]);

        var m31 = harness.Page.Sessions[0].Rows.Single(row => row.FolderName == "M31");
        Assert.Equal("+2 other nights", m31.OtherNightsBadge);
    }

    [Fact]
    public void PickingALevelOnOneNight_LeavesAnotherNightsPickAlone()
    {
        var library = new Library().Frame(N1, Ha(N1, "a.fits")).Frame(N2, Ha(N2, "a.fits"));

        using var harness = ExportHarness.Create(library, [N2, N1]);

        var first = harness.Page.Sessions[0];
        var second = harness.Page.Sessions[1];
        var before = second.ChosenIndex;

        first.Rows[0].IsChosen = true;

        Assert.Equal(0, first.ChosenIndex);
        Assert.Equal(before, second.ChosenIndex);
        Assert.NotEqual(first.GroupName, second.GroupName);
    }

    [Fact]
    public void TheStagingRenameIsStatedOnTheRowsItHappensToAndNoOthers()
    {
        // Nights 1 and 3 both pick a leaf named Ha, which is the collision case; night 2 picks
        // OIII and states nothing.
        var library = new Library()
            .Frame(N1, Ha(N1, "a.fits"))
            .Frame(N2, Path.Combine(Library.Root, "M31", "2025-03-21", "OIII", "a.fits"))
            .Frame(N3, Ha(N3, "a.fits"));

        using var harness = ExportHarness.Create(library, [N1, N2, N3]);

        Assert.Equal("2025-03-20_Ha", harness.Page.Sessions[0].StagingName);
        Assert.Equal("", harness.Page.Sessions[1].StagingName);
        Assert.False(harness.Page.Sessions[1].HasStagingName);
        Assert.Equal("2025-03-22_Ha", harness.Page.Sessions[2].StagingName);
    }

    [Fact]
    public void ARefreshWithNoNightOfferingAPick_ClearsTheNamesRatherThanLeavingThemStanding()
    {
        // Latent rather than shipped: the levels are fixed at load and a night with levels always
        // has a pick, so nothing on the page reaches this state today. The rule is pinned anyway,
        // because a row keeping its last name would state a rename for a plan the page no longer
        // holds, and the state is one level list away from being reachable.
        using var harness = ExportHarness.Create(new Library().EmptyNight(N1), [N1]);
        var session = harness.Page.Sessions[0];
        Assert.Null(session.Chosen);

        session.SetStagingEntryName("2025-03-20_Ha");
        Assert.True(session.HasStagingName);

        // A pick moving with nothing to pick is what reaches the refresh's early return.
        session.ChosenIndex = 0;

        Assert.Equal("", session.StagingName);
        Assert.False(session.HasStagingName);
    }

    // ------------------------------------------------------ 10.4 the totals, including unknown

    [Fact]
    public void TheFootersFiguresEqualTheCoreTotalsOnTheSameInput()
    {
        var library = new Library()
            .Frame(N1, Ha(N1, "a.fits"), 1_000L)
            .Frame(N1, Ha(N1, "b.fits"), 2_000L);

        using var harness = ExportHarness.Create(library, [N1]);

        var chosen = harness.Page.Sessions.Select(row => row.Chosen!).ToList();
        var expected = FolderLevels.Totals(chosen, []);

        Assert.Equal(expected, harness.Page.Totals);
        Assert.Equal("2 light frames", harness.Page.FrameCountText);
        Assert.Equal("1 folder", harness.Page.FolderCountText);
        Assert.Equal("3 KB", harness.Page.SizeText);
    }

    [Fact]
    public void OneNullSize_MakesTheWholeTotalReadUnknown_WithTheFilterOffAndOn()
    {
        var library = new Library()
            .Frame(N1, Ha(N1, "a.fits"), 1_000L)
            .Frame(N1, Ha(N1, "b.fits"), null);

        using var harness = ExportHarness.Create(library, [N1]);

        // A failure looks like a partial sum rendered where one size is missing, which presents an
        // undercount as fact to a user who is about to commit to a copy.
        Assert.Equal("unknown", harness.Page.SizeText);
        Assert.Null(harness.Page.Totals!.SizeBytes);

        harness.Page.SetExcludedFrames([library.Ids[Ha(N1, "a.fits")]]);
        Assert.Equal("unknown", harness.Page.SizeText);
    }

    [Fact]
    public void AFolderHoldingNoCataloguedFileTotalsZero()
    {
        var library = new Library().EmptyNight(N1).Frame(N2, Ha(N2, "a.fits"), 0L);

        using var harness = ExportHarness.Create(library, [N2]);

        Assert.Equal(0L, harness.Page.Totals!.SizeBytes);
        Assert.Equal("0 B", harness.Page.SizeText);
    }

    [Fact]
    public void TheLevelRowStatesUnknownRatherThanAPartialSum()
    {
        var library = new Library()
            .Frame(N1, Ha(N1, "a.fits"), 1_000L)
            .Foreign(Path.Combine(Library.Root, "M31", "2025-03-20", "Ha", "sidecar.fits"), N1, size: null);

        using var harness = ExportHarness.Create(library, [N1]);

        Assert.Equal("unknown", harness.Page.Sessions[0].Rows[^1].BytesText);
    }

    // ------------------------------------------------------ 10.5 two sets, on purpose

    [Fact]
    public void TheFooterSubtractsOnlyTheExclusionsUnderTheChosenLevel()
    {
        // A night split across two filter folders with one chosen and failures in the other. A
        // failure looks like the footer subtracting every excluded frame of the selection, which
        // understates the copy by frames that were never going to be copied.
        var ha = Path.Combine(Library.Root, "M31", "2025-03-20", "Ha", "a.fits");
        var oiii = Path.Combine(Library.Root, "M31", "2025-03-20", "OIII", "b.fits");
        var library = new Library().Frame(N1, ha, 1_000L).Frame(N1, oiii, 1_000L);

        using var harness = ExportHarness.Create(library, [N1]);

        var session = harness.Page.Sessions[0];
        session.Rows.Single(row => row.FolderName == "Ha").IsChosen = true;

        Assert.Equal(1, harness.Page.Totals!.FrameCount);

        // The OIII failure is outside the chosen level and subtracts nothing.
        harness.Page.SetExcludedFrames([library.Ids[oiii]]);
        Assert.Equal(1, harness.Page.Totals!.FrameCount);

        // The Ha failure is inside it and does.
        harness.Page.SetExcludedFrames([library.Ids[oiii], library.Ids[ha]]);
        Assert.Equal(0, harness.Page.Totals!.FrameCount);
    }

    [Fact]
    public void TheFooterCarriesSpecTwelveThirteensOwnSentence()
    {
        using var harness = ExportHarness.Create(new Library().Frame(N1, Ha(N1, "a.fits")), [N1]);

        Assert.Equal(
            "Frames counts this target's light frames. The size counts every catalogued file in "
            + "the chosen folders; sidecars and files GalactiLog never read are copied too and are "
            + "not counted.",
            harness.Page.FooterNoteText);
    }

    [Fact]
    public void TheEmptyExportWarningBlamesTheFilterOnlyWhenTheFilterIsWhatEmptiedIt()
    {
        var library = new Library().Frame(N1, Ha(N1, "a.fits"));

        using var harness = ExportHarness.Create(library, [N1]);
        Assert.False(harness.Page.HasEmptyExportWarning);

        harness.Page.SetExcludedFrames([library.Ids[Ha(N1, "a.fits")]]);
        Assert.Equal(WbppExportViewModel.EmptyExportWarning, harness.Page.EmptyExportText);
    }

    [Fact]
    public void ASelectionWhoseLevelsHoldNoFrameAtAll_IsNotBlamedOnTheFilter()
    {
        // The night's own frames are all rootless, so the one level that exists holds none of
        // them. A failure looks like the filter being blamed for an empty export it had no part
        // in.
        var library = new Library()
            .Frame(N1, Ha(N1, "a.fits"), 0L)
            .Foreign(Path.Combine(Library.Root, "M31", "2025-03-20", "Ha", "x.fits"), N2);

        using var harness = ExportHarness.Create(library, [N1]);
        harness.Page.SetExcludedFrames([library.Ids[Ha(N1, "a.fits")]]);

        Assert.Equal(WbppExportViewModel.EmptyExportWarning, harness.Page.EmptyExportText);

        // A selection whose levels hold no frames at all says so instead: telling a user to loosen
        // a filter that excluded nothing sends them to the wrong control.
        using var empty = ExportHarness.Create(new Library().EmptyNight(N1), [N1]);
        Assert.Equal(
            "The selected folders hold no frames, so this export would copy nothing.",
            empty.Page.EmptyExportText);
    }

    [Fact]
    public void WithNoPanel_EveryFrameIsIncluded()
    {
        var library = new Library().Frame(N1, Ha(N1, "a.fits")).Frame(N1, Ha(N1, "b.fits"));

        using var harness = ExportHarness.Create(library, [N1]);

        Assert.Null(harness.Page.QualityPanel);
        Assert.Equal(2, harness.Page.Totals!.FrameCount);
    }

    [Fact]
    public void ThePanelIsBuiltFromTheSlotFactory_OverTheResolvedRigKey()
    {
        var library = new Library().Frame(N1, Ha(N1, "a.fits"));
        var id = library.Ids[Ha(N1, "a.fits")];
        string? seenRigKey = null;

        using var harness = ExportHarness.Create(
            library,
            [N1],
            detail: night => SessionCardViewModelTestFactory.PopulatedDetail(night) with
            {
                Frames = [PreviewModalViewModelTestFactory.Row(id, Ha(N1, "a.fits"), "a.fits")],
            },
            createPanel: (frames, _, rigKey, _) =>
            {
                seenRigKey = rigKey;
                return frames.Count;
            });

        Assert.Equal("TS 130 / ASI2600MM", seenRigKey);
        Assert.Equal(1, Assert.IsType<int>(harness.Page.QualityPanel));
    }

    [Fact]
    public void TheOpeningStateStatesNoTotals()
    {
        var (page, release) = ParkedPage();

        // The footer's figures read nothing, not zero: a zero here is a claim the page cannot yet
        // make about a copy the user is deciding on.
        Assert.True(page.IsLoading);
        Assert.Equal("", page.FrameCountText);
        Assert.Equal("", page.SizeText);
        Assert.Null(page.Totals);

        release();
        Assert.False(page.IsLoading);
        page.Dispose();
    }

    // The page with its first read parked, and the action that lets it finish. Both the park and
    // the wait live out of the test body, because xUnit1031 is an error here.
    private static (WbppExportViewModel Page, Action Release) ParkedPage(string? storedStaging = null)
    {
        var library = new Library().Frame(N1, Ha(N1, "a.fits"));
        var gate = new ManualResetEventSlim(false);

        var page = new WbppExportViewModel(
            library.GroupKey,
            library.TargetName,
            [N1],
            (_, nights, _, _) =>
            {
                gate.Wait(TimeSpan.FromSeconds(30));
                return library.Build(nights);
            },
            (_, _) => null,
            () => new GeneralSettings
            {
                ScanRoots = [.. library.ScanRoots],
                WbppStagingPathDocument = storedStaging is null
                    ? null
                    : WbppSettingsRead.WriteStagingPath(storedStaging),
            },
            mutate => mutate(new GeneralSettings()),
            new AppWriter(Directory.CreateTempSubdirectory("galactilog-t5a-parked-").FullName),
            _ => Task.CompletedTask,
            post: action => action());

        return (page, () =>
        {
            gate.Set();
            Settle(page);
        });
    }

    [Fact]
    public void AStoredStagingFolder_IsNotCommittedUntilTheFirstReadPublishes()
    {
        // The stored value is checked against the scan roots and the chosen sources by
        // RecheckStaging, which Publish runs. A failure looks like the constructor committing it
        // unchecked, which on a library the path read is slow on leaves a keyboard path to
        // Generate over a staging folder inside the library and over zero levels.
        var (page, release) = ParkedPage(storedStaging: @"C:\p16lib\staged");

        Assert.False(page.GenerateCommand.CanExecute(WbppScriptType.PowerShell));
        Assert.Equal(WbppExportViewModel.NoStagingReason, page.GenerateDisabledReason);

        release();

        // And once the levels exist the stored value is reported rather than silently accepted.
        Assert.Equal(WbppExportViewModel.ScanRootRefusal, page.StagingError);
        Assert.False(page.GenerateCommand.CanExecute(WbppScriptType.PowerShell));
        page.Dispose();
    }

    [Fact]
    public void EveryLevelContaminated_StatesTheChosenLevelsCostOnTheSessionRowItself()
    {
        // Spec 12.13's Every level contaminated row. The tree is closed when the page opens, so a
        // badge drawn only inside it is a cost no user reads before committing to the copy. A
        // failure looks like the session row carrying nothing at all.
        var library = new Library()
            .Frame(N1, Ha(N1, "a.fits"))
            .Foreign(Path.Combine(Library.Root, "M31", "2025-03-20", "Ha", "x.fits"), N2)
            .Foreign(Path.Combine(Library.Root, "M31", "2025-03-20", "Ha", "y.fits"), N3);

        using var harness = ExportHarness.Create(library, [N1]);
        var row = harness.Page.Sessions[0];

        Assert.All(row.Rows, level => Assert.True(level.Level.IsContaminated));
        Assert.True(row.EveryLevelIsContaminated);

        // The strings are the chosen level row's own and are never recomputed here.
        var chosen = row.Rows[row.ChosenIndex];
        Assert.Equal(chosen.OtherNightsBadge, row.ChosenOtherNightsBadge);
        Assert.Equal(chosen.OtherTargetsBadge, row.ChosenOtherTargetsBadge);
        Assert.Equal(chosen.OtherNightsTooltip, row.ChosenOtherNightsTooltip);
        Assert.True(row.HasChosenOtherNightsBadge);
        Assert.True(row.HasChosenOtherTargetsBadge);

        // And a night with a clean level to move to states nothing on the row, because the tree is
        // where a cost the user can still avoid belongs.
        var clean = new Library()
            .Frame(N1, Ha(N1, "a.fits"))
            .Foreign(Path.Combine(Library.Root, "M31", "NGC7000", "y.fits"), N2);
        using var other = ExportHarness.Create(clean, [N1]);

        Assert.False(other.Page.Sessions[0].EveryLevelIsContaminated);
        Assert.False(other.Page.Sessions[0].HasChosenOtherTargetsBadge);
    }

    [Fact]
    public void Disposing_DisposesWhateverWasBuiltIntoTheQualityPanelSlot()
    {
        // The slot is the only reference to the panel once the page is gone, so the panel's own
        // debouncer, cancellation source and pending settings flush are unreachable unless the
        // page disposes it. A failure looks like a flush that never runs in the shipped
        // application and a timer that outlives the window.
        var library = new Library().Frame(N1, Ha(N1, "a.fits"));
        var panel = new DisposableProbe();

        var harness = ExportHarness.Create(library, [N1], createPanel: (_, _, _, _) => panel);
        Assert.Same(panel, harness.Page.QualityPanel);
        Assert.False(panel.IsDisposed);

        harness.Dispose();

        Assert.True(panel.IsDisposed);

        // And a second disposal is a no-op rather than an ObjectDisposedException out of a
        // cleanup path the modal host and a test harness both reach.
        harness.Page.Dispose();
        Assert.Equal(1, panel.DisposeCalls);
    }

    private sealed class DisposableProbe : IDisposable
    {
        public int DisposeCalls { get; private set; }

        public bool IsDisposed => DisposeCalls > 0;

        public void Dispose() => DisposeCalls++;
    }
}
