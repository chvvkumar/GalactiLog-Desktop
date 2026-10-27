using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Core.Io;
using GalactiLog.Core.Wbpp;
using Xunit;
using static GalactiLog.App.Tests.ViewModels.Wbpp.CalibrationFixtures;
using static GalactiLog.App.Tests.ViewModels.Wbpp.Pleiades;

namespace GalactiLog.App.Tests.ViewModels.Wbpp;

/// <summary>
/// The calibration strip and the calibration rows in the tree: the page scans each night's
/// hierarchy through a fake walker on load, climbs while the parent holds only this target, puts
/// the matched folders into the tree as ticked rows along their path, keeps the user's ticks across
/// a new result, and folds the selection into the footer. Nothing here reads a directory.
/// </summary>
public class CalibrationBlockTests
{
    private static readonly DateOnly N = new(2025, 10, 15);
    private static readonly DateOnly N2 = new(2025, 10, 16);

    // Date-first: Date_2025-10-15\M31\LIGHT holds the lights, Date_2025-10-15\FLAT the flats.
    private static readonly string DateDir = Path.Combine(Library.Root, "Date_2025-10-15");
    private static readonly string M31Dir = Path.Combine(DateDir, "M31");
    private static readonly string LightDir = Path.Combine(M31Dir, "LIGHT");
    private static readonly string FlatDir = Path.Combine(DateDir, "FLAT");
    private static readonly string Angle128 = Path.Combine(FlatDir, "ZWO ASI2600MM Pro", "Angle_128.85");
    private static readonly string Angle139 = Path.Combine(FlatDir, "ZWO ASI2600MM Pro", "Angle_139.07");

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void Until(Func<bool> condition)
        => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)));

    private static void Scanned(SessionLevelViewModel session)
        => Until(() => session.ScanState == CalibrationScanState.Done);

    private static FolderNodeViewModel Row(SessionLevelViewModel session, string path)
        => session.Rows.Single(r => Is(r.Level.Path, path));

    private static FolderNodeViewModel CalRow(SessionLevelViewModel session, string folder)
        => session.CalibrationRows.Single(r => Is(r.Row!.Folder, folder));

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

    private static void Generate(WbppExportViewModel page)
        => Assert.True(page.GenerateCommand.ExecuteAsync(WbppScriptType.PowerShell).Wait(TimeSpan.FromSeconds(30)), "The command did not finish within 30 s.");

    private static Library DateFirst() => new Library()
        .Frame(N, Path.Combine(LightDir, "a.fits"))
        .Frame(N, Path.Combine(LightDir, "b.fits"));

    private static FakeDisk DateFirstDisk() => new FakeDisk()
        .Add(F(Path.Combine(LightDir, "a.fits"), CalibrationType.Light, K(), night: N, size: 1_000))
        .Add(F(Path.Combine(LightDir, "b.fits"), CalibrationType.Light, K(), night: N, size: 1_000))
        .Add(F(Path.Combine(Angle128, "f1.fits"), CalibrationType.Flat, K(), night: N))
        .Add(F(Path.Combine(Angle128, "f2.fits"), CalibrationType.Flat, K(), night: N))
        .Add(F(Path.Combine(Angle139, "g1.fits"), CalibrationType.Flat, K(rot: 139.07), night: N));

    private static void Stage(ExportHarness h)
    {
        h.Page.StagingPath = h.TempFolder("staging");
        h.Page.CommitStagingCommand.Execute(null);
        h.Page.SavedConfirmationDuration = TimeSpan.Zero;
        h.Destination = h.ScriptPath("wbpp.ps1");
    }

    [Fact]
    public void OnPublish_MatchedFlats_BecomeTickedRowsAlongTheirPath_AndUnmatchedOnesStartUnticked()
    {
        var disk = Disk();
        using var h = ExportHarness.Create(Catalogue(), [N18], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);
        Until(() => session.LookHigherText == "Look in 2026, 3 files");

        // The scan walked the date folder; the second call is the Look in offer's count of the year
        // folder, which reads no header.
        Assert.Equal([Date18, Year], disk.Calls);
        Assert.Contains("Scanned Date_2026-10-18, 3 headers", session.ScanSentence, StringComparison.Ordinal);
        Assert.Equal(2, session.CalibrationRows.Count);

        var flats = Find(session, Flats)!;
        Assert.True(flats.IsIncluded);
        Assert.Equal("Angle_45.18", flats.FolderName);
        Assert.Equal("2 of 2 files, " + MetricText.Bytes(200), flats.CalibrationText);
        Assert.Equal("same night", flats.NoteTag);
        Assert.Equal("", flats.FrameCountText);
        Assert.Same(Row(session, Date18), flats.Parent!.Parent!.Parent);
        Assert.Equal("FLAT", flats.Parent.Parent.FolderName);
        Assert.True(flats.Parent.IsExpanded);
        Assert.True(flats.Parent.Parent.IsExpanded);
        Assert.False(flats.Parent.IsIncluded);
        Assert.Equal(1.0, flats.Parent.RowOpacity);
        Assert.Equal("3 files, " + MetricText.Bytes(300), flats.Parent.CalibrationText);

        var rejected = Find(session, Flats96)!;
        Assert.False(rejected.IsIncluded);
        Assert.Equal("rotation 96.26", rejected.NoteTag);
        Assert.Equal("1 file, " + MetricText.Bytes(100), rejected.CalibrationText);
        Assert.Equal(0.55, rejected.RowOpacity);
        Assert.Equal("", rejected.RowTag);

        Assert.Equal([Lights, Flats], Sources(session));
        Assert.Equal("1 of 2 calibration folders ticked", session.CalibrationTally);
        Assert.Equal(["No flats found for OIII on ZWO ASI533MM Pro"], session.Warnings);
        Assert.Equal("", session.SkippedText);
        Until(() => h.Page.FrameCountText == "5 files");
        Assert.Equal(MetricText.Bytes(3_200), h.Page.SizeText);
        Assert.Equal("2 folders", h.Page.FolderCountText);
        Assert.EndsWith(" plus 2 flats, " + MetricText.Bytes(200), session.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void LookIn_AddsTheOtherDateFoldersRows_UnderTheYearRow_InPlace()
    {
        var disk = Disk();
        using var h = ExportHarness.Create(Catalogue(), [N18], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);
        Until(() => session.LookHigherText is not null);
        var flats = Find(session, Flats)!;
        Row(session, Year).IsExpanded = false;

        session.LookHigherCommand.Execute(null);
        Scanned(session);

        Assert.Equal(Year, disk.Calls[^1]);
        var borrowed = Find(session, Borrowed)!;
        Assert.True(borrowed.IsIncluded);
        Assert.Equal("3 days earlier", borrowed.NoteTag);
        Assert.Equal("2 of 2 files, " + MetricText.Bytes(200), borrowed.CalibrationText);
        var date15 = borrowed.Parent!.Parent!.Parent!;
        Assert.Equal("Date_2026-10-15", date15.FolderName);
        Assert.Same(Row(session, Year), date15.Parent);
        Assert.True(date15.IsExpanded);
        Assert.False(date15.IsIncluded);
        Assert.Same(flats, Find(session, Flats));
        Assert.True(flats.IsIncluded);
        Assert.Equal([Borrowed, Flats, Lights], Sources(session).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal("2 of 3 calibration folders ticked", session.CalibrationTally);
        Assert.Empty(session.Warnings);
        Assert.Null(session.LookHigherText);

        // The user's collapse of the year row stands; the tally is what says the new rows are there.
        Assert.False(Row(session, Year).IsExpanded);
    }

    [Fact]
    public void AMatchedFolderUnderATickedAncestor_IsCopiedByThatOperation_WithUnselectedFramesExcludedFileByFile()
    {
        var disk = Disk().Add(F(Path.Combine(Flats, "h1.fits"), CalibrationType.Flat, P(rot: 96.26), night: N18));
        using var h = ExportHarness.Create(Catalogue(), [N18], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);
        Assert.Equal("2 of 3 files, " + MetricText.Bytes(200), Find(session, Flats)!.CalibrationText);

        Row(session, Date18).IsIncluded = true;

        Assert.True(Find(session, Flat)!.IsIncluded);
        Assert.True(Find(session, Flats)!.IsIncluded);
        Assert.False(Find(session, Flats96)!.IsIncluded);
        Assert.Equal([Date18], Sources(session));
        Assert.Equal([Flats96], Skipped(session, Date18));
        Assert.Equal([Path.Combine(Flats, "h1.fits")], session.CalibrationInPlaceExcludes().Select(f => f.FilePath));
        Assert.Equal("5 files", h.Page.FrameCountText);
        Assert.Equal(MetricText.Bytes(3_200), h.Page.SizeText);
        Assert.Equal("1 folder", h.Page.FolderCountText);

        Stage(h);
        Generate(h.Page);
        var jobs = h.Page.ScriptText!.Split('\n').Where(line => line.TrimStart().StartsWith("@{ Src", StringComparison.Ordinal)).ToList();
        var job = Assert.Single(jobs);
        Assert.Contains("Src = '" + Date18 + "'", job, StringComparison.Ordinal);
        Assert.Contains(@"ExcludeDirs = @('FLAT\ZWO ASI533MM Pro\Angle_96.26\')", job, StringComparison.Ordinal);
        Assert.Contains(@"'FLAT\ZWO ASI533MM Pro\Angle_45.18\h1.fits'", job, StringComparison.Ordinal);
    }

    [Fact]
    public void AMatchedFolderUnderNoTickedAncestor_IsItsOwnOperation_AndUntickingItDropsIt()
    {
        using var h = ExportHarness.Create(Catalogue(), [N18], walk: Disk().Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);
        Stage(h);
        Generate(h.Page);
        var script = h.Page.ScriptText!;
        Assert.Contains("Src = '" + Flats + "'", script, StringComparison.Ordinal);
        Assert.Contains("Src = '" + Lights + "'", script, StringComparison.Ordinal);
        Assert.DoesNotContain(Flats96, script, StringComparison.Ordinal);

        Find(session, Flats)!.IsIncluded = false;

        Assert.False(h.Page.HasScript);
        Assert.Equal([Lights], Sources(session));
        Assert.Equal("0 of 2 calibration folders ticked", session.CalibrationTally);
        Assert.Equal("3 light frames", h.Page.FrameCountText);
        Assert.Equal("", session.SkippedText);
    }

    [Fact]
    public void TicksOnCalibrationRows_SurviveRescan_LookIn_AndATickOnAnotherBranch()
    {
        var disk = Disk();
        using var h = ExportHarness.Create(Catalogue(), [N18], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);
        Until(() => session.LookHigherText == "Look in 2026, 3 files");
        Find(session, Flats)!.IsIncluded = false;
        Find(session, Flats96)!.IsIncluded = true;

        session.RescanCommand.Execute(null);
        Scanned(session);
        Assert.False(Find(session, Flats)!.IsIncluded);
        Assert.True(Find(session, Flats96)!.IsIncluded);

        Until(() => session.LookHigherText is not null);
        session.LookHigherCommand.Execute(null);
        Scanned(session);
        Assert.False(Find(session, Flats)!.IsIncluded);
        Assert.True(Find(session, Flats96)!.IsIncluded);

        // A pick-equivalent change on another branch re-matches with the same light sets, and walks
        // nothing.
        var walks = disk.Calls.Count;
        Row(session, Target).IsIncluded = true;
        Assert.False(Find(session, Flats)!.IsIncluded);
        Assert.True(Find(session, Flats96)!.IsIncluded);
        Assert.Equal([Borrowed, Flats96, Target], Sources(session).Order(StringComparer.OrdinalIgnoreCase));
        Assert.False(SpinWait.SpinUntil(() => disk.Calls.Count > walks, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void AQualityExclusionThatChangesTheLightSets_ReseedsUntouchedRows_AndKeepsUserTicks()
    {
        var library = Catalogue();
        using var h = ExportHarness.Create(library, [N18], walk: Disk().Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);
        Until(() => session.LookHigherText is not null);
        session.LookHigherCommand.Execute(null);
        Scanned(session);
        Assert.True(Find(session, Borrowed)!.IsIncluded);
        Find(session, Flats96)!.IsIncluded = true;

        h.Page.SetExcludedFrames([library.Ids[L3]]);   // the only OIII light

        Assert.False(Find(session, Borrowed)!.IsIncluded);          // its default flipped and the user never touched it
        Assert.Equal("filter OIII", Find(session, Borrowed)!.NoteTag);
        Assert.True(Find(session, Flats96)!.IsIncluded);            // the user's tick stands
        Assert.True(Find(session, Flats)!.IsIncluded);
        Assert.Equal("2 of 3 calibration folders ticked", session.CalibrationTally);
    }

    [Fact]
    public void ARowCreatedForACalibrationFolder_IsNeverListedTwice_WhenItsParentIsExpanded()
    {
        var calls = new List<string>();
        using var h = ExportHarness.Create(Catalogue(), [N18], listSubfolders: folder =>
            {
                lock (calls)
                {
                    calls.Add(folder);
                }

                return Is(folder, Date18) ? [Flat, Light]
                    : Is(folder, Flat) ? [FlatCam]
                    : Is(folder, FlatCam) ? [Flats, Flats96]
                    : [];
            },
            walk: Disk().Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);
        var flats = Find(session, Flats)!;

        // The match opened the path to the flats without listing it; the user's presses list it.
        void Press(string path)
        {
            h.OnUi(() => Find(session, path)!.ToggleExpandCommand.Execute(null));
            Until(() => session.Rows.Concat(session.Rows.SelectMany(r => r.Descendants())).All(r => !r.IsListing));
        }

        Press(Date18);
        Press(Flat);
        Press(FlatCam);

        var date = Row(session, Date18);
        Assert.Equal(1, date.Children.Count(c => Is(c.Level.Path, Flat)));
        var cam = Find(session, FlatCam)!;
        Assert.Equal(["Angle_45.18", "Angle_96.26"], cam.Children.Select(c => c.FolderName));
        Assert.Same(flats, Find(session, Flats));
        Assert.True(flats.IsIncluded);
        Assert.True(Find(session, Flat)!.IsExpanded);   // whichever landed first, the listing or the match
        Assert.True(cam.IsExpanded);
        lock (calls)
        {
            Assert.Equal(1, calls.Count(c => Is(c, Flat)));
            Assert.Equal(1, calls.Count(c => Is(c, FlatCam)));
        }
    }

    [Fact]
    public void AScanResultChange_RemovesRowsTheUserNeverTicked_AndKeepsTheOnesTheyDid()
    {
        var full = Disk();
        var without = new FakeDisk();
        foreach (var frame in full.Files.Where(f => !PathConfinement.IsUnderOrEqual(Flats96, f.FilePath)))
        {
            without.Add(frame);
        }

        var calls = 0;
        CalibrationWalk.Walker walk = (root, skip, rule, progress, ct) =>
            Interlocked.Increment(ref calls) == 1 ? full.Walker(root, skip, rule, progress, ct) : without.Walker(root, skip, rule, progress, ct);

        using (var h = ExportHarness.Create(Catalogue(), [N18], walk: walk))
        {
            var session = h.Page.Sessions[0];
            Scanned(session);
            Assert.NotNull(Find(session, Flats96));

            session.RescanCommand.Execute(null);
            Scanned(session);

            Assert.Null(Find(session, Flats96));
            Assert.NotNull(Find(session, Flats));
            Assert.Equal(["Angle_45.18"], Find(session, FlatCam)!.Children.Select(c => c.FolderName));
            Assert.Equal("1 of 1 calibration folder ticked", session.CalibrationTally);
        }

        calls = 0;
        using (var h = ExportHarness.Create(Catalogue(), [N18], walk: walk))
        {
            var session = h.Page.Sessions[0];
            Scanned(session);
            Find(session, Flats96)!.IsIncluded = true;

            session.RescanCommand.Execute(null);
            Scanned(session);

            var kept = Find(session, Flats96)!;
            Assert.True(kept.IsIncluded);
            Assert.Null(kept.Row);
            Assert.Equal(FolderNodeViewModel.NotCataloguedText, kept.FrameCountText);
            Assert.Contains(Flats96, Sources(session));
        }
    }

    [Fact]
    public void ARowAListingNamed_StaysDrawnWhenALaterScanResultDropsIt()
    {
        // The match creates the camera and angle rows first; the gated listings then name them.
        using var release = new ManualResetEventSlim(false);
        var disk = Disk();
        var dateWalks = 0;
        CalibrationWalk.Walker walk = (root, skip, rule, progress, ct) =>
            Is(root, Date18) && Interlocked.Increment(ref dateWalks) > 1
                ? throw new InvalidOperationException("disk gone")
                : disk.Walker(root, skip, rule, progress, ct);
        using var h = ExportHarness.Create(Catalogue(), [N18], listSubfolders: folder =>
            {
                if (Is(folder, Flat) || Is(folder, FlatCam))
                {
                    release.Wait(TimeSpan.FromSeconds(10));
                }

                return Is(folder, Date18) ? [Flat, Light]
                    : Is(folder, Flat) ? [FlatCam]
                    : Is(folder, FlatCam) ? [Flats, Flats96]
                    : [];
            },
            walk: walk);
        var session = h.Page.Sessions[0];
        Row(session, Date18).IsIncluded = true;
        Scanned(session);
        Assert.NotNull(Find(session, Flats96));
        release.Set();
        Until(() => session.Rows.Concat(session.Rows.SelectMany(r => r.Descendants())).All(r => !r.IsListing));

        session.RescanCommand.Execute(null);
        Scanned(session);

        Assert.StartsWith("Could not scan", session.ScanSentence, StringComparison.Ordinal);
        Assert.Equal(["Angle_45.18", "Angle_96.26"], Find(session, FlatCam)!.Children.Select(c => c.FolderName));
        Assert.True(Find(session, Flat)!.IsIncluded);
        Assert.Equal([Date18], Sources(session));
    }

    [Fact]
    public void AUserUntickBeforeTheFirstMatch_StandsAgainstTheMatchersDefault()
    {
        // Flats directly in the M31 folder, which holds the LIGHT folder the night copies.
        var disk = new FakeDisk()
            .Add(F(Path.Combine(LightDir, "a.fits"), CalibrationType.Light, K(), night: N, size: 1_000))
            .Add(F(Path.Combine(LightDir, "b.fits"), CalibrationType.Light, K(), night: N, size: 1_000))
            .Add(F(Path.Combine(M31Dir, "f1.fits"), CalibrationType.Flat, K(), night: N));
        disk.Gate = new ManualResetEventSlim(false);
        using var h = ExportHarness.Create(DateFirst(), [N], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        var m31 = Row(session, M31Dir);
        m31.IsIncluded = true;
        m31.IsIncluded = false;

        disk.Gate.Set();
        Scanned(session);

        Assert.Same(m31, CalRow(session, M31Dir));
        Assert.False(m31.IsIncluded);
        Assert.Equal([LightDir], Sources(session));
    }

    [Fact]
    public void TheStrip_StatesNoCalibration_WithAnEmptyTally()
    {
        var disk = new FakeDisk()
            .Add(F(L1, CalibrationType.Light, P(), night: N18, size: 1_000))
            .Add(F(L2, CalibrationType.Light, P(), night: N18, size: 1_000))
            .Add(F(L3, CalibrationType.Light, P(filter: "OIII"), night: N18, size: 1_000));
        using var h = ExportHarness.Create(Catalogue(), [N18], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);

        Assert.Equal("No calibration frames under Date_2026-10-18", session.NoCalibrationText);
        Assert.Equal("", session.CalibrationTally);
        Assert.Empty(session.CalibrationRows);
        Assert.Equal([Lights], Sources(session));
    }

    [Fact]
    public void ClimbsAutomatically_WhileTheParentHoldsOnlyThisTarget()
    {
        var target = Path.Combine(Library.Root, "Target_Y");
        var light = Path.Combine(target, "LIGHT");
        var night = Path.Combine(light, "2025-10-15");
        var library = new Library()
            .Frame(N, Path.Combine(night, "a.fits"))
            .Foreign(Path.Combine(light, "2025-10-16", "b.fits"), N2, "obj:M 31", "M 31");
        var disk = new FakeDisk()
            .Add(F(Path.Combine(night, "a.fits"), CalibrationType.Light, K(), night: N))
            .Add(F(Path.Combine(light, "2025-10-16", "b.fits"), CalibrationType.Light, K(), night: N2))
            .Add(F(Path.Combine(target, "FLAT", "f1.fits"), CalibrationType.Flat, K(), night: N));
        using var h = ExportHarness.Create(library, [N], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);

        Assert.Equal([night, light, target], disk.Calls);
        Assert.Equal(Path.Combine(target, "FLAT"), session.CalibrationRows.Single().Row!.Folder);
        Assert.True(session.CalibrationRows.Single().IsIncluded);
        Assert.Same(Row(session, target), Find(session, Path.Combine(target, "FLAT"))!.Parent);
        Assert.Equal(0, session.ScannedLevelIndex);
        Assert.Contains("Scanned Target_Y", session.ScanSentence, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotClimbPastAFolderWithOtherTargets_AndOffersLookHigher()
    {
        var plan = Path.Combine(Library.Root, "Plan");
        var light = Path.Combine(plan, "Light");
        var m31 = Path.Combine(light, "M31");
        var library = new Library()
            .Frame(N, Path.Combine(m31, "a.fits"))
            .Foreign(Path.Combine(m31, "b.fits"), N2, "obj:M 31", "M 31")
            .Foreign(Path.Combine(light, "NGC7000", "c.fits"), N);
        var disk = new FakeDisk()
            .Add(F(Path.Combine(m31, "a.fits"), CalibrationType.Light, K(), night: N))
            .Add(F(Path.Combine(m31, "b.fits"), CalibrationType.Light, K(), night: N2))
            .Add(F(Path.Combine(light, "NGC7000", "c.fits"), CalibrationType.Light, K(), night: N))
            .Add(F(Path.Combine(plan, "Flat", "f1.fits"), CalibrationType.Flat, K(), night: N))
            .Add(F(Path.Combine(plan, "Flat", "f2.fits"), CalibrationType.Flat, K(), night: N));
        using var h = ExportHarness.Create(library, [N], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);
        Until(() => session.LookHigherText == "Look in Plan, 2 files");

        Assert.Equal(light, disk.Calls[0]);
        Assert.Empty(session.CalibrationRows);
        Assert.Equal(["No flats found for Ha on ZWO ASI2600MM Pro"], session.Warnings);

        var before = disk.Calls.Count;
        session.LookHigherCommand.Execute(null);
        Scanned(session);

        Assert.Equal(before + 1, disk.Calls.Count);
        Assert.Equal(plan, disk.Calls[^1]);
        Assert.Equal(Path.Combine(plan, "Flat"), session.CalibrationRows[0].Row!.Folder);
        Assert.Empty(session.Warnings);
        Assert.Null(session.LookHigherText);
    }

    [Fact]
    public void CalibrationSources_LandUnderTheirOwnFolderNames_BesideTheLights()
    {
        using var h = ExportHarness.Create(DateFirst(), [N], walk: DateFirstDisk().Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);
        Stage(h);
        Generate(h.Page);

        // S = {Date_2025-10-15\M31\LIGHT, Date_2025-10-15\FLAT\ZWO ASI2600MM Pro\Angle_128.85}; the start
        // folder is the date folder and is kept, so both entries begin with it.
        var script = h.Page.ScriptText!;
        Assert.Contains(@"Join-Path $StagingRoot 'Date_2025-10-15\M31\LIGHT'", script, StringComparison.Ordinal);
        Assert.Contains(@"Join-Path $StagingRoot 'Date_2025-10-15\FLAT\ZWO ASI2600MM Pro\Angle_128.85'", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"LIGHT\FLAT", script, StringComparison.Ordinal);
        Assert.DoesNotContain(Angle139, script, StringComparison.Ordinal);
        Assert.Equal("Folder names kept from " + DateDir, h.Page.FolderNamesKeptText);
    }

    [Fact]
    public void TheLongPathFigureMeasuresTheCalibrationOperationToo()
    {
        using var h = ExportHarness.Create(DateFirst(), [N], walk: DateFirstDisk().Walker, storedStaging: @"C:\s");
        Scanned(h.Page.Sessions[0]);

        // The flats operation's entry plus its longest file beats Date_2025-10-15\M31\LIGHT plus a.fits.
        var flats = @"Date_2025-10-15\FLAT\ZWO ASI2600MM Pro\Angle_128.85";
        Assert.Equal(@"C:\s".Length + 1 + h.Page.SubfolderName.Length + 1 + flats.Length + 1 + "f1.fits".Length, h.Page.LongestDestinationLength);
    }

    [Fact]
    public void Footer_ShowsSizingWhileScanning_ThenTheFullBreakdown()
    {
        var disk = DateFirstDisk();
        disk.Gate = new ManualResetEventSlim(false);
        using var h = ExportHarness.Create(DateFirst(), [N], walk: disk.Walker);
        var session = h.Page.Sessions[0];

        Assert.Equal(CalibrationScanState.Scanning, session.ScanState);
        Assert.Equal(MetricText.Bytes(2_000) + ", sizing", h.Page.SizeText);

        disk.Gate.Set();
        Scanned(session);
        Until(() => !h.Page.SizeText.EndsWith(", sizing", StringComparison.Ordinal));

        Assert.Equal(MetricText.Bytes(2_200), h.Page.SizeText);
        Assert.Equal(
            "lights " + MetricText.Bytes(2_000) + " (2), flats " + MetricText.Bytes(200) + " (2), flat-darks 0",
            h.Page.BreakdownText);
        Assert.Equal("4 files", h.Page.FrameCountText);
        Assert.EndsWith(" plus 2 flats, " + MetricText.Bytes(200), session.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void Footer_CountsAnUncataloguedSidecarUnderTheChosenFolder_AsTheCopierWill()
    {
        var disk = DateFirstDisk().Add(F(Path.Combine(LightDir, "a.json"), CalibrationType.Other, K(), size: 50));
        using var h = ExportHarness.Create(DateFirst(), [N], walk: disk.Walker);
        Scanned(h.Page.Sessions[0]);
        Until(() => !h.Page.SizeText.EndsWith(", sizing", StringComparison.Ordinal));

        Assert.Equal(MetricText.Bytes(2_250), h.Page.SizeText);
        Assert.Equal("5 files", h.Page.FrameCountText);
    }

    [Fact]
    public void SingleFolder_CataloguedLightsAreNotRead_AndFlatsBesideThemAreCopiedInPlace()
    {
        var dir = Path.Combine(Library.Root, "T", "2025-10-15");
        var a = Path.Combine(dir, "a.fits");
        var x = Path.Combine(dir, "x.fits");
        var library = new Library().Frame(N, a).Foreign(x, N);
        var disk = new FakeDisk()
            .Add(F(a, CalibrationType.Light, K(), night: N, size: 1_000))
            .Add(F(x, CalibrationType.Light, K(), night: N, size: 1_000))
            .Add(F(Path.Combine(dir, "f1.fits"), CalibrationType.Flat, K(), night: N))
            .Add(F(Path.Combine(dir, "f2.fits"), CalibrationType.Flat, K(gain: 200), night: N));
        using var h = ExportHarness.Create(
            library, [N], readForeignLights: (_, _, _, _) => [new WbppFramePath(Guid.NewGuid(), x, 1_000)], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);

        // The own and the foreign light are sized only: two headers, the two flats.
        Assert.Contains(", 2 headers", session.ScanSentence, StringComparison.Ordinal);
        Assert.Equal("1 of 2 files, " + MetricText.Bytes(100), Row(session, dir).CalibrationText);
        Assert.Same(Row(session, dir), CalRow(session, dir));
        Until(() => session.ForeignExcluded.Count == 1);

        Stage(h);
        Generate(h.Page);
        var jobs = h.Page.ScriptText!.Split('\n').Where(line => line.TrimStart().StartsWith("@{ Src", StringComparison.Ordinal)).ToList();
        var job = Assert.Single(jobs);
        Assert.Contains("ExcludeFiles = @('x.fits', 'f2.fits')", job, StringComparison.Ordinal);
        Assert.DoesNotContain("FLAT", job, StringComparison.Ordinal);
        Assert.Equal(MetricText.Bytes(1_100), h.Page.SizeText);
        Assert.Equal(1_100, h.Page.TotalSizeBytes);
    }

    [Fact]
    public void APickChange_RecomputesFromCachedFrames_WithoutWalking()
    {
        var ha = Path.Combine(M31Dir, "Ha");
        var oiii = Path.Combine(M31Dir, "Oiii");
        var library = new Library()
            .Frame(N, Path.Combine(ha, "a.fits"))
            .Frame(N, Path.Combine(oiii, "b.fits"), keys: K(filter: "Oiii"));
        var disk = new FakeDisk()
            .Add(F(Path.Combine(ha, "a.fits"), CalibrationType.Light, K(), night: N))
            .Add(F(Path.Combine(oiii, "b.fits"), CalibrationType.Light, K(filter: "Oiii"), night: N))
            .Add(F(Path.Combine(FlatDir, "f1.fits"), CalibrationType.Flat, K(), night: N));
        using var h = ExportHarness.Create(library, [N], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);
        Assert.Equal(["No flats found for Oiii on ZWO ASI2600MM Pro"], session.Warnings);

        Row(session, oiii).IsIncluded = false;

        Assert.Empty(session.Warnings);
        Assert.Single(disk.Calls);
    }

    [Fact]
    public void Rescan_WalksAgain_AndKeepsUserTicksByFolder()
    {
        var disk = DateFirstDisk();
        using var h = ExportHarness.Create(DateFirst(), [N], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);
        CalRow(session, Angle128).IsIncluded = false;
        CalRow(session, Angle139).IsIncluded = true;

        session.RescanCommand.Execute(null);
        Until(() => disk.Calls.Count == 2);
        Scanned(session);

        Assert.False(CalRow(session, Angle128).IsIncluded);
        Assert.True(CalRow(session, Angle139).IsIncluded);
    }

    [Fact]
    public void AScanResult_WithdrawsAGeneratedScript()
    {
        var disk = DateFirstDisk();
        disk.Gate = new ManualResetEventSlim(false);
        using var h = ExportHarness.Create(DateFirst(), [N], walk: disk.Walker);
        Stage(h);
        Generate(h.Page);
        Assert.True(h.Page.HasScript);

        disk.Gate.Set();
        Scanned(h.Page.Sessions[0]);

        Until(() => !h.Page.HasScript);
    }

    [Fact]
    public void Dispose_DuringAScan_PublishesNothing()
    {
        var disk = DateFirstDisk();
        disk.Gate = new ManualResetEventSlim(false);
        var h = ExportHarness.Create(DateFirst(), [N], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Until(() => disk.Calls.Count == 1);
        var posts = h.PostCount;

        h.Page.Dispose();
        disk.Gate.Set();
        Until(() => disk.Returned == 1);

        Assert.False(SpinWait.SpinUntil(() => h.PostCount != posts, TimeSpan.FromMilliseconds(300)));
        Assert.Empty(session.CalibrationRows);
        Assert.NotEqual(CalibrationScanState.Done, session.ScanState);
        h.Dispose();
    }

    [Fact]
    public void WithNoWalker_TheStripIsHidden_AndTheTreeHasNoCalibrationRows()
    {
        using var h = ExportHarness.Create(DateFirst(), [N]);
        var session = h.Page.Sessions[0];

        Assert.False(session.HasCalibrationBlock);
        Assert.Empty(session.CalibrationRows);
        Assert.Equal("", session.CalibrationTally);
        Assert.Equal(CalibrationScanState.Idle, session.ScanState);
        Assert.Equal(MetricText.Bytes(2_000), h.Page.SizeText);
        Assert.Equal("", h.Page.BreakdownText);
        Assert.Equal("2 light frames", h.Page.FrameCountText);

        Stage(h);
        Generate(h.Page);
        Assert.DoesNotContain("FLAT", h.Page.ScriptText!, StringComparison.Ordinal);
    }

    [Fact]
    public void Issues_AreSummarisedInTheScanSentence_WithPathsInTheTooltip()
    {
        var disk = DateFirstDisk();
        var bad1 = Path.Combine(FlatDir, "broken1.fits");
        var bad2 = Path.Combine(FlatDir, "broken2.fits");
        disk.Issues.Add(new ScanIssue(bad1, "header could not be read"));
        disk.Issues.Add(new ScanIssue(bad2, "header could not be read"));
        using var h = ExportHarness.Create(DateFirst(), [N], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);

        Assert.EndsWith(", skipped 2 files that could not be read", session.ScanSentence, StringComparison.Ordinal);
        Assert.Contains(bad1, session.IssuesTooltip, StringComparison.Ordinal);
        Assert.Contains(bad2, session.IssuesTooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void AWalkerThatThrows_StatesCouldNotScan_AndTheExportStaysUsable()
    {
        using var h = ExportHarness.Create(
            DateFirst(), [N], walk: (_, _, _, _, _) => throw new InvalidOperationException("disk gone"));
        var session = h.Page.Sessions[0];
        Scanned(session);

        Assert.StartsWith("Could not scan", session.ScanSentence, StringComparison.Ordinal);
        Assert.Empty(session.CalibrationRows);
        Assert.Equal(MetricText.Bytes(2_000), h.Page.SizeText);

        Stage(h);
        Assert.True(h.Page.GenerateCommand.CanExecute(WbppScriptType.PowerShell));
        Generate(h.Page);
        Assert.True(h.Page.HasScript);
        Assert.NotNull(h.Page.CopyDestination);
    }

    [Fact]
    public void ANightWithNoLightSets_WalksOnce_NeverClimbs_AndWarnsOfNothing()
    {
        // The target-first layout that climbs twice when the lights have keys.
        var target = Path.Combine(Library.Root, "Target_Y");
        var light = Path.Combine(target, "LIGHT");
        var night = Path.Combine(light, "2025-10-15");
        var library = new Library { WithoutKeys = true }
            .Frame(N, Path.Combine(night, "a.fits"))
            .Foreign(Path.Combine(light, "2025-10-16", "b.fits"), N2, "obj:M 31", "M 31");
        var disk = new FakeDisk()
            .Add(F(Path.Combine(night, "a.fits"), CalibrationType.Light, K(), night: N))
            .Add(F(Path.Combine(target, "FLAT", "f1.fits"), CalibrationType.Flat, K(), night: N));
        using var h = ExportHarness.Create(library, [N], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);

        Assert.Equal([night], disk.Calls);
        Assert.Empty(session.Warnings);
        Assert.Null(session.LookHigherText);
        Assert.Equal("No calibration frames under 2025-10-15", session.NoCalibrationText);
    }

    [Fact]
    public void AStaleScan_IsDroppedAfterRescan()
    {
        var disk = DateFirstDisk();
        var gate = new ManualResetEventSlim(false);
        var stale = Path.Combine(DateDir, "STALE");
        var calls = 0;
        var firstReturned = false;
        CalibrationWalk.Walker walk = (root, sizeOnly, rule, progress, ct) =>
        {
            if (Interlocked.Increment(ref calls) > 1)
            {
                return disk.Walker(root, sizeOnly, rule, progress, ct);
            }

            gate.Wait(TimeSpan.FromSeconds(30));
            Volatile.Write(ref firstReturned, true);
            return new WalkResult([F(Path.Combine(stale, "s.fits"), CalibrationType.Flat, K(), night: N)], [], 1);
        };
        using var h = ExportHarness.Create(DateFirst(), [N], walk: walk);
        var session = h.Page.Sessions[0];
        Until(() => Volatile.Read(ref calls) == 1);

        session.RescanCommand.Execute(null);
        Scanned(session);
        Assert.Equal(2, session.CalibrationRows.Count);

        gate.Set();
        Until(() => Volatile.Read(ref firstReturned));
        Assert.False(SpinWait.SpinUntil(
            () => session.CalibrationRows.Any(row => Is(row.Row!.Folder, stale)), TimeSpan.FromMilliseconds(300)));
        Assert.Equal(2, session.CalibrationRows.Count);
        Assert.Equal(CalibrationScanState.Done, session.ScanState);
    }

    [Fact]
    public void ACalibrationRowTick_WithdrawsAGeneratedScript()
    {
        using var h = ExportHarness.Create(DateFirst(), [N], walk: DateFirstDisk().Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);
        Stage(h);
        Generate(h.Page);
        Assert.True(h.Page.HasScript);

        CalRow(session, Angle139).IsIncluded = true;

        Assert.False(h.Page.HasScript);
    }

    [Fact]
    public void APickAboveTheScannedRoot_WalksTheNewlyExposedFolder_AndItsFlatsBecomeRows()
    {
        // Date_2025-10-15 holds another night's and another target's lights, so the scan starts
        // at M31 and the automatic climb stops below the date folder.
        var library = DateFirst()
            .Foreign(Path.Combine(DateDir, "NGC7000", "c.fits"), N2);
        var disk = DateFirstDiskWithout()
            .Add(F(Path.Combine(DateDir, "NGC7000", "c.fits"), CalibrationType.Light, K(), night: N2))
            .Add(F(Path.Combine(FlatDir, "f1.fits"), CalibrationType.Flat, K(), night: N));
        using var h = ExportHarness.Create(library, [N], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);
        Until(() => session.LookHigherText == "Look in Date_2025-10-15, 2 files");
        Assert.Equal(M31Dir, disk.Calls[0]);
        Assert.Empty(session.CalibrationRows);
        Assert.True(session.ChosenIndex > Row(session, DateDir).Index);

        var before = disk.Calls.Count;
        Row(session, DateDir).IsIncluded = true;
        Scanned(session);

        Assert.Equal(before + 1, disk.Calls.Count);
        Assert.Equal(DateDir, disk.Calls[^1]);
        Assert.True(Find(session, FlatDir)!.IsIncluded);
        Assert.Equal([DateDir], Sources(session));
        Assert.Empty(session.CalibrationInPlaceExcludes());

        // An untick back inside the scanned roots walks nothing more.
        Row(session, DateDir).IsIncluded = false;
        Assert.False(SpinWait.SpinUntil(() => disk.Calls.Count > before + 1, TimeSpan.FromMilliseconds(300)));
        Assert.Contains(FlatDir, Sources(session));
        Assert.Contains(M31Dir, Sources(session));
    }

    [Fact]
    public void ThePagesExclusionPatterns_KeepAWbppOutputFolderOutOfTheScan()
    {
        var calibrated = Path.Combine(DateDir, "WBPP", "calibrated", "flat");
        var disk = DateFirstDisk()
            .Add(F(Path.Combine(calibrated, "f1_c.fits"), CalibrationType.Flat, K(), night: N))
            .Add(F(Path.Combine(DateDir, "masters", "m.fits"), CalibrationType.Flat, K(), night: N));
        using var h = ExportHarness.Create(DateFirst(), [N], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);

        Assert.Equal(2, session.CalibrationRows.Count);
        Assert.DoesNotContain(session.CalibrationRows, row => row.Row!.Folder.Contains("WBPP", StringComparison.Ordinal)
            || row.Row!.Folder.Contains("masters", StringComparison.Ordinal));
        Assert.DoesNotContain(session.ScannedFrames, frame => frame.FilePath.Contains("WBPP", StringComparison.Ordinal));
        Assert.Contains("3 headers", session.ScanSentence, StringComparison.Ordinal);
    }

    private static FakeDisk DateFirstDiskWithout() => new FakeDisk()
        .Add(F(Path.Combine(LightDir, "a.fits"), CalibrationType.Light, K(), night: N, size: 1_000))
        .Add(F(Path.Combine(LightDir, "b.fits"), CalibrationType.Light, K(), night: N, size: 1_000));
}
