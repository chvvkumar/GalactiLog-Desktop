using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Core.Wbpp;
using Xunit;
using static GalactiLog.App.Tests.ViewModels.Wbpp.CalibrationFixtures;

namespace GalactiLog.App.Tests.ViewModels.Wbpp;

/// <summary>
/// The Calibration found block: the page scans each night's hierarchy through a fake walker on
/// load, climbs while the parent holds only this target, publishes matched rows, owns the
/// calibration folders against the lights operation, and folds the selection into the footer.
/// Nothing here reads a directory.
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

    private static CalibrationRowViewModel CalRow(SessionLevelViewModel session, string folder)
        => session.CalibrationRows.Single(r => Is(r.Row.Folder, folder));

    private static void Generate(WbppExportViewModel page)
        => page.GenerateCommand.ExecuteAsync(WbppScriptType.PowerShell).Wait(TimeSpan.FromSeconds(30));

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
    public void OnPublish_TheNightScansItsStartLevel_AndPublishesMatchedRows()
    {
        var disk = DateFirstDisk();
        using var h = ExportHarness.Create(DateFirst(), [N], walk: disk.Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);

        Assert.True(session.HasCalibrationBlock);
        Assert.Equal([DateDir], disk.Calls);
        Assert.Equal(2, session.CalibrationRows.Count);

        var ticked = CalRow(session, Angle128);
        Assert.True(ticked.IsTicked);
        Assert.Equal("2 of 2 files", ticked.CountText);
        Assert.Equal(Path.Combine("FLAT", "ZWO ASI2600MM Pro", "Angle_128.85"), ticked.RelativePath);
        Assert.Equal("same night", ticked.Note);

        var other = CalRow(session, Angle139);
        Assert.False(other.IsTicked);
        Assert.Equal("1 file", other.CountText);
        Assert.Equal("rotation 139.07", other.Note);

        Assert.Contains("Scanned Date_2025-10-15, 3 headers", session.ScanSentence, StringComparison.Ordinal);
        Assert.Empty(session.Warnings);
        Assert.Equal(["Light sets: Ha on ZWO ASI2600MM Pro, gain 110, offset 50, bin 1, rotation 128.9"], session.LightSetLines);
        Assert.Null(session.LookHigherText);
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
        Assert.Equal(Path.Combine(target, "FLAT"), session.CalibrationRows.Single().Row.Folder);
        Assert.True(session.CalibrationRows.Single().IsTicked);
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
        Assert.Equal(Path.Combine(plan, "Flat"), session.CalibrationRows[0].Row.Folder);
        Assert.Empty(session.Warnings);
        Assert.Null(session.LookHigherText);
    }

    [Fact]
    public void CalibrationOperations_CarryTheTypeSubfolder_AndJoinTheLightsOperation()
    {
        using var h = ExportHarness.Create(DateFirst(), [N], walk: DateFirstDisk().Walker);
        var session = h.Page.Sessions[0];
        Scanned(session);
        Stage(h);
        Generate(h.Page);

        var script = h.Page.ScriptText!;
        Assert.Contains(@"LIGHT\FLAT\ZWO ASI2600MM Pro\Angle_128.85", script, StringComparison.Ordinal);
        Assert.Contains(Angle128, script, StringComparison.Ordinal);
        Assert.DoesNotContain(Angle139, script, StringComparison.Ordinal);
    }

    [Fact]
    public void EdgeRule_WhenTheDateFolderIsChosen_OwnedFoldersAreExcludedFromTheLightsOperation_AndHiddenInTheTree()
    {
        var disk = new FakeDisk()
            .Add(F(Path.Combine(LightDir, "a.fits"), CalibrationType.Light, K(), night: N))
            .Add(F(Path.Combine(FlatDir, "f1.fits"), CalibrationType.Flat, K(), night: N));
        using var h = ExportHarness.Create(
            new Library().Frame(N, Path.Combine(LightDir, "a.fits")),
            [N],
            listSubfolders: folder => Is(folder, DateDir) ? [FlatDir, M31Dir] : [],
            walk: disk.Walker);
        var session = h.Page.Sessions[0];
        session.ChosenIndex = Row(session, DateDir).Index;
        Scanned(session);
        var date = Row(session, DateDir);
        Until(() => date.Children.Any(c => c.FolderName == "FLAT"));
        var flat = date.Children.Single(c => c.FolderName == "FLAT");

        Until(() => flat.IsOwnedByCalibration);
        Assert.False(flat.IsVisible);
        Assert.Equal([FlatDir], (session.Chosen!.SkippedFolders ?? []).Select(level => level.Path));
        Assert.Equal("", session.SkippedText);

        Stage(h);
        Generate(h.Page);
        var script = h.Page.ScriptText!;
        Assert.Contains(@"Date_2025-10-15\FLAT'", script, StringComparison.Ordinal);
        Assert.Contains(@"ExcludeDirs = @('FLAT\')", script, StringComparison.Ordinal);
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

        session.ChosenIndex = Row(session, ha).Index;

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
        CalRow(session, Angle128).IsTicked = false;
        CalRow(session, Angle139).IsTicked = true;

        session.RescanCommand.Execute(null);
        Until(() => disk.Calls.Count == 2);
        Scanned(session);

        Assert.False(CalRow(session, Angle128).IsTicked);
        Assert.True(CalRow(session, Angle139).IsTicked);
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
    public void WithNoWalker_TheBlockIsHidden_AndBehaviourIsAsBefore()
    {
        using var h = ExportHarness.Create(DateFirst(), [N]);
        var session = h.Page.Sessions[0];

        Assert.False(session.HasCalibrationBlock);
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
            () => session.CalibrationRows.Any(row => Is(row.Row.Folder, stale)), TimeSpan.FromMilliseconds(300)));
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

        CalRow(session, Angle139).IsTicked = true;

        Assert.False(h.Page.HasScript);
    }
}
