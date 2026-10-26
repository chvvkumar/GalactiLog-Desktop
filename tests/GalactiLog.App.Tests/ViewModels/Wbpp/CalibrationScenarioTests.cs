using System.Diagnostics;
using System.Text.RegularExpressions;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.ViewModels.TargetDetail.Wbpp;
using GalactiLog.Core.Io;
using GalactiLog.Core.Tests.Fixtures;
using GalactiLog.Core.Wbpp;
using Xunit;
using static GalactiLog.App.Tests.ViewModels.Wbpp.CalibrationFixtures;

namespace GalactiLog.App.Tests.ViewModels.Wbpp;

/// <summary>
/// The calibration pre-selection end to end: a real folder tree in a temp directory with synthetic
/// FITS headers, the real walker and lister over it, and the catalogue faked by <see cref="Library"/>.
/// Each case asserts what the design says a person sees: ticks, rows, notes, the generated script's
/// jobs, the footer and what the copier stages.
/// </summary>
public class CalibrationScenarioTests
{
    private static readonly DateOnly N = new(2025, 10, 15);
    private static readonly DateOnly N14 = new(2025, 10, 14);

    // Longitude -97.5 puts local solar time 6.5 h behind UTC. Both stamps land on the night of
    // 2025-10-15 under the noon rule, and on 2025-10-16 under a UTC-midnight rule.
    private const string Evening = "2025-10-16T03:00:00";
    private const string Dawn = "2025-10-16T11:30:00";

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void Until(Func<bool> condition)
        => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(15)));

    private static void Scanned(SessionLevelViewModel session)
        => Until(() => session.ScanState == CalibrationScanState.Done);

    private static FolderNodeViewModel Row(SessionLevelViewModel session, string path)
        => session.Rows.Single(r => Is(r.Level.Path, path));

    private static CalibrationRowViewModel CalRow(SessionLevelViewModel session, string folder)
        => session.CalibrationRows.Single(r => Is(r.Row.Folder, folder));

    private static void Generate(WbppExportViewModel page)
        => page.GenerateCommand.ExecuteAsync(WbppScriptType.PowerShell).Wait(TimeSpan.FromSeconds(30));

    private static void Stage(ExportHarness h)
    {
        h.Page.StagingPath = h.TempFolder("staging");
        h.Page.CommitStagingCommand.Execute(null);
        h.Page.SavedConfirmationDuration = TimeSpan.Zero;
        h.Destination = h.ScriptPath("wbpp.ps1");
    }

    private sealed record Job(string Src, string Dst, IReadOnlyList<string> ExcludeFiles, IReadOnlyList<string> ExcludeDirs);

    private static readonly Regex JobLine = new(
        @"^\s*@\{ Src = '(?<src>[^']*)'; Dst = \(Join-Path \$StagingRoot '(?<dst>[^']*)'\)(; ExcludeFiles = @\((?<files>[^)]*)\))?(; ExcludeDirs = @\((?<dirs>[^)]*)\))? \}",
        RegexOptions.Multiline);

    // Every PowerShell job, in script order.
    private static IReadOnlyList<Job> Jobs(string script)
    {
        static IReadOnlyList<string> List(Group group) => group.Success && group.Value.Length > 0
            ? [.. group.Value.Split(", ").Select(item => item.Trim('\''))]
            : [];

        return [.. JobLine.Matches(script).Select(m => new Job(
            m.Groups["src"].Value, m.Groups["dst"].Value, List(m.Groups["files"]), List(m.Groups["dirs"])))];
    }

    // Every staged file, relative to the copy destination, sorted.
    private static IReadOnlyList<string> Staged(ExportHarness h)
    {
        var destination = h.Page.CopyDestination!;
        return [.. Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(destination, path))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static Task<StagingCopyResult> Copy(ExportHarness h)
        => h.Page.RunCopyAsync(new Progress<StagingProgress>(), io => io, CancellationToken.None);

    /// <summary>A library root on disk. Every frame written here is a header-only FITS file; lights
    /// are also catalogued in <see cref="Library"/>, and foreign lights answer the page's foreign
    /// read from the same records.</summary>
    private sealed class Tree : IDisposable
    {
        private readonly List<(string Path, string Key, DateOnly Night, long Size)> _lights = [];
        private readonly List<string> _locked = [];

        public Tree()
        {
            Root = Path.Combine(Path.GetTempPath(), "galactilog-scenario-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Library.ScanRoots.Clear();
            Library.ScanRoots.Add(Root);
        }

        public string Root { get; }

        public Library Library { get; } = new();

        public string P(params string[] parts) => Path.Combine([Root, .. parts]);

        public static long Write(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return bytes.Length;
        }

        private static FitsBuilder Header(string type, string camera, string? filter, double? rotation, double exposure, string dateObs)
        {
            var b = new FitsBuilder().Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 0L)
                .Card("IMAGETYP", type).Card("INSTRUME", camera);
            if (filter is not null) b.Card("FILTER", filter);
            b.Card("XBINNING", 1L).Card("GAIN", 110L).Card("OFFSET", 50L).Card("TELESCOP", "SQA55");
            if (rotation is { } r) b.Card("ROTATANG", r);
            return b.Card("EXPOSURE", exposure).Card("DATE-OBS", dateObs);
        }

        /// <summary>One of this target's catalogued lights. OBJCTROT carries a sync offset the
        /// matcher must never read.</summary>
        public Tree Light(DateOnly night, string path, string camera = "ZWO ASI2600MM Pro", string? filter = "Ha",
            double? rotation = 128.85, string dateObs = Evening)
        {
            var header = Header("LIGHT", camera, filter, rotation, 300, dateObs).Card("OBJECT", Library.TargetName);
            if (rotation is { } r) header.Card("OBJCTROT", r + 7.4);
            var size = Write(path, header.Build().ToArray());
            Library.Frame(night, path, size, K(cam: camera, filter: filter, rot: rotation));
            _lights.Add((path, Library.GroupKey, night, size));
            return this;
        }

        /// <summary>Another target's catalogued light.</summary>
        public Tree Foreign(DateOnly night, string path, string key, string name, string camera = "ZWO ASI2600MM Pro",
            string? filter = "Ha", double? rotation = 128.85)
        {
            var header = Header("LIGHT", camera, filter, rotation, 300, Evening).Card("OBJECT", name);
            var size = Write(path, header.Build().ToArray());
            Library.Foreign(path, night, key, name, size);
            _lights.Add((path, key, night, size));
            return this;
        }

        /// <summary>An uncatalogued calibration frame.</summary>
        public Tree Cal(string path, string type, string camera = "ZWO ASI2600MM Pro", string? filter = "Ha",
            double? rotation = 128.85, double exposure = 1.5, string dateObs = Dawn)
        {
            Write(path, Header(type, camera, filter, rotation, exposure, dateObs).Build().ToArray());
            return this;
        }

        /// <summary>The page's foreign-lights read: catalogued lights under the folder that are not
        /// this group's on this night.</summary>
        public IReadOnlyList<WbppFramePath> ReadForeign(string folder, string key, DateOnly night, CancellationToken ct)
            => [.. _lights
                .Where(l => PathConfinement.IsUnderOrEqual(folder, l.Path) && !(l.Key == key && l.Night == night))
                .Select(l => new WbppFramePath(Library.Ids.TryGetValue(l.Path, out var id) ? id : Guid.NewGuid(), l.Path, l.Size))];

        /// <summary>Denies the current user the right to list <paramref name="dir"/>.</summary>
        public void Lock(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "inside.txt"), [1]);
            Icacls(dir, "/deny", Environment.UserName + ":(RD)");
            _locked.Add(dir);
        }

        private static void Icacls(params string[] args)
        {
            var start = new ProcessStartInfo("icacls") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            Assert.True(process.WaitForExit(15_000), "icacls did not exit");
            Assert.Equal(0, process.ExitCode);
        }

        public ExportHarness Open(params DateOnly[] nights) => ExportHarness.Create(
            Library,
            nights,
            listSubfolders: SourceSubfolders.OnDisk,
            readForeignLights: ReadForeign,
            walk: CalibrationWalk.OnDisk,
            general: g => g with { UseImagingNight = true, ObserverLongitude = -97.5 });

        public void Dispose()
        {
            try
            {
                foreach (var dir in _locked)
                {
                    Icacls(dir, "/remove:d", Environment.UserName);
                }
            }
            finally
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
        }
    }

    [Fact]
    public void Nina_DateFirst_TwoCameras_TwoAngles_SelectsOnlyTheMatchingFlats()
    {
        using var t = new Tree();
        var date = t.P("Date_X");
        var camA = t.P("Date_X", "LIGHT", "CamA");
        var leaf = Path.Combine(camA, "Target_T", "Angle_128.85");
        var flatA128 = t.P("Date_X", "FLAT", "CamA", "Angle_128.85");
        var flatA139 = t.P("Date_X", "FLAT", "CamA", "Angle_139.07");
        var flatB = t.P("Date_X", "FLAT", "CamB", "Angle_8.90");
        t.Light(N, Path.Combine(leaf, "L1.fits"), camera: "CamA")
            .Light(N, Path.Combine(leaf, "L2.fits"), camera: "CamA")
            .Light(N14, t.P("Date_Y", "LIGHT", "CamA", "Target_T", "Angle_128.85", "Y1.fits"), camera: "CamA",
                dateObs: "2025-10-15T03:00:00")
            .Foreign(N, Path.Combine(camA, "Target_Other", "Angle_139.07", "O1.fits"), "obj:Other", "Other", camera: "CamA", rotation: 139.07)
            .Foreign(N, t.P("Date_X", "LIGHT", "CamB", "Target_T2", "Angle_8.90", "T1.fits"), "obj:T2", "T2", camera: "CamB", rotation: 8.90)
            .Cal(Path.Combine(flatA128, "F1.fits"), "FLAT", camera: "CamA")
            .Cal(Path.Combine(flatA128, "F2.fits"), "FLAT", camera: "CamA")
            .Cal(Path.Combine(flatA128, "F3.fits"), "FLAT", camera: "CamA")
            .Cal(Path.Combine(flatA139, "G1.fits"), "FLAT", camera: "CamA", rotation: 139.07)
            .Cal(Path.Combine(flatA139, "G2.fits"), "FLAT", camera: "CamA", rotation: 139.07)
            .Cal(Path.Combine(flatB, "H1.fits"), "FLAT", camera: "CamB", rotation: 8.90);
        using var h = t.Open(N);
        var session = h.Page.Sessions[0];
        Assert.True(Is(leaf, session.Chosen!.Level.Path));
        Scanned(session);

        Assert.StartsWith("Scanned Date_X, ", session.ScanSentence, StringComparison.Ordinal);
        Assert.Equal(3, session.CalibrationRows.Count);
        var ticked = CalRow(session, flatA128);
        Assert.True(ticked.IsTicked);
        Assert.Equal("3 of 3 files", ticked.CountText);
        Assert.Equal("same night", ticked.Note);
        Assert.False(CalRow(session, flatA139).IsTicked);
        Assert.Equal("rotation 139.07", CalRow(session, flatA139).Note);
        Assert.False(CalRow(session, flatB).IsTicked);
        Assert.Equal("camera CamB", CalRow(session, flatB).Note);
        Assert.Empty(session.Warnings);

        Until(() => !h.Page.SizeText.EndsWith(", sizing", StringComparison.Ordinal));
        Assert.Contains("flats " + MetricText.Bytes(3 * 2880) + " (3)", h.Page.BreakdownText, StringComparison.Ordinal);

        Stage(h);
        Generate(h.Page);
        var jobs = Jobs(h.Page.ScriptText!);
        var entry = jobs.Single(j => Is(j.Src, leaf)).Dst;
        Assert.Equal(entry + @"\FLAT\CamA\Angle_128.85", jobs.Single(j => Is(j.Src, flatA128)).Dst);
        Assert.Equal(2, jobs.Count);
        Assert.DoesNotContain(jobs, j => j.Dst.Contains("Angle_139.07", StringComparison.Ordinal) || j.Dst.Contains("CamB", StringComparison.Ordinal));

        // Opening the date row lists its folders: the other target's light folder starts unticked.
        session.ChosenIndex = Row(session, date).Index;
        var camARow = Row(session, camA);
        Until(() => camARow.Children.Any(c => c.FolderName == "Target_Other"));
        var other = camARow.Children.Single(c => c.FolderName == "Target_Other");
        Assert.False(other.IsIncluded);
        Assert.Equal("Other's lights", other.ReasonTag);
    }

    [Fact]
    public void TargetFirst_FlatsUnderTheTarget_AreReachedByOneAutomaticClimb()
    {
        using var t = new Tree();
        var target = t.P("T");
        var night14 = t.P("T", "LIGHT", "2025-10-14");
        var night15 = t.P("T", "LIGHT", "2025-10-15");
        var flat15 = t.P("T", "FLAT", "2025-10-15");
        var flat09 = t.P("T", "FLAT", "2025-10-09");
        t.Light(N14, Path.Combine(night14, "a.fits"), dateObs: "2025-10-15T03:00:00")
            .Light(N, Path.Combine(night15, "b.fits"))
            .Light(N, Path.Combine(night15, "c.fits"))
            // A mis-filed flat in a light folder: header-read, it would become a row.
            .Cal(Path.Combine(night14, "stray_flat.fits"), "FLAT", dateObs: "2025-10-15T11:30:00")
            .Cal(Path.Combine(flat15, "f1.fits"), "FLAT")
            .Cal(Path.Combine(flat15, "f2.fits"), "FLAT")
            .Cal(Path.Combine(flat09, "e1.fits"), "FLAT", dateObs: "2025-10-10T11:30:00")
            .Cal(Path.Combine(flat09, "e2.fits"), "FLAT", dateObs: "2025-10-10T11:30:00");
        using var h = t.Open(N);
        var session = h.Page.Sessions[0];
        Assert.True(Is(night15, session.Chosen!.Level.Path));
        Scanned(session);

        Assert.StartsWith("Scanned T, 4 headers", session.ScanSentence, StringComparison.Ordinal);
        Assert.True(Is(target, session.Rows[session.ScannedLevelIndex].Level.Path));
        Assert.Equal(2, session.CalibrationRows.Count);
        var same = CalRow(session, flat15);
        Assert.True(same.IsTicked);
        Assert.Equal("2 of 2 files", same.CountText);
        Assert.Equal("same night", same.Note);
        var earlier = CalRow(session, flat09);
        Assert.False(earlier.IsTicked);
        Assert.Equal("not the nearest night", earlier.Note);
        Assert.DoesNotContain(session.CalibrationRows, r => Is(r.Row.Folder, night14));
        Assert.Empty(session.Warnings);
    }

    [Fact]
    public async Task Asiair_SinglePile_WithAMisfiledDarkFlat_IsReachedByLookHigher_AndRoutedByHeader()
    {
        using var t = new Tree();
        var plan = t.P("Plan");
        var ic = t.P("Plan", "Light", "IC1848");
        var pile = t.P("Plan", "Flat");
        t.Light(N, Path.Combine(ic, "Light_IC1848_1.fit"), rotation: null)
            .Light(N, Path.Combine(ic, "Light_IC1848_2.fit"), rotation: null)
            .Light(N14, Path.Combine(ic, "Light_IC1848_0.fit"), rotation: null, dateObs: "2025-10-15T03:00:00")
            .Foreign(N, t.P("Plan", "Light", "NGC6888", "Light_NGC6888_1.fit"), "obj:NGC 6888", "NGC 6888", filter: "Oiii", rotation: null)
            .Cal(Path.Combine(pile, "Flat_Ha_1.fit"), "FLAT", rotation: null)
            .Cal(Path.Combine(pile, "Flat_Ha_2.fit"), "FLAT", rotation: null)
            .Cal(Path.Combine(pile, "Flat_Ha_3.fit"), "FLAT", rotation: null)
            .Cal(Path.Combine(pile, "Flat_Oiii_1.fit"), "FLAT", filter: "Oiii", rotation: null, exposure: 2.0)
            .Cal(Path.Combine(pile, "Flat_Oiii_2.fit"), "FLAT", filter: "Oiii", rotation: null, exposure: 2.0)
            .Cal(Path.Combine(pile, "Flat_Ha_dark.fit"), "DARKFLAT", rotation: null);
        using var h = t.Open(N);
        var session = h.Page.Sessions[0];
        Assert.True(Is(ic, session.Chosen!.Level.Path));
        Scanned(session);
        Until(() => session.LookHigherText is not null);

        Assert.StartsWith("Look in Plan", session.LookHigherText, StringComparison.Ordinal);
        Assert.Empty(session.CalibrationRows);

        session.LookHigherCommand.Execute(null);
        Until(() => session.CalibrationRows.Count > 0 && session.ScanState == CalibrationScanState.Done);

        Assert.StartsWith("Scanned Plan, ", session.ScanSentence, StringComparison.Ordinal);
        var row = CalRow(session, pile);
        Assert.True(row.IsTicked);
        Assert.Equal("4 of 6 files", row.CountText);
        Assert.Empty(session.Warnings);

        Stage(h);
        Generate(h.Page);
        var jobs = Jobs(h.Page.ScriptText!);
        var entry = jobs.Single(j => Is(j.Src, ic)).Dst;
        var fromPile = jobs.Where(j => Is(j.Src, pile)).ToList();
        Assert.Equal(2, fromPile.Count);
        var flat = fromPile.Single(j => j.Dst == entry + @"\FLAT");
        Assert.Equal(["Flat_Ha_dark.fit", "Flat_Oiii_1.fit", "Flat_Oiii_2.fit"], flat.ExcludeFiles.Order(StringComparer.Ordinal));
        var darkFlat = fromPile.Single(j => j.Dst.StartsWith(entry + @"\DARKFLAT", StringComparison.Ordinal));
        Assert.Equal(
            ["Flat_Ha_1.fit", "Flat_Ha_2.fit", "Flat_Ha_3.fit", "Flat_Oiii_1.fit", "Flat_Oiii_2.fit"],
            darkFlat.ExcludeFiles.Order(StringComparer.Ordinal));

        var result = await Copy(h);
        Assert.Equal(StagingOutcome.Completed, result.Outcome);
        var staged = Staged(h);
        Assert.Equal(3, staged.Count(p => p.Contains(@"\FLAT\Flat_Ha_", StringComparison.Ordinal)));
        Assert.Single(staged, p => p.EndsWith("Flat_Ha_dark.fit", StringComparison.Ordinal) && p.Contains(@"\DARKFLAT\", StringComparison.Ordinal));
        Assert.DoesNotContain(staged, p => p.Contains("Oiii", StringComparison.Ordinal));
    }

    [Fact]
    public void Osc_NoFilterOnEitherSide_StillMatches()
    {
        using var t = new Tree();
        var lights = t.P("2025-10-15", "LIGHT", "M 31");
        var flats = t.P("2025-10-15", "FLAT");
        t.Light(N, Path.Combine(lights, "a.fits"), camera: "ZWO ASI533MC Pro", filter: null)
            .Light(N, Path.Combine(lights, "b.fits"), camera: "ZWO ASI533MC Pro", filter: null)
            .Cal(Path.Combine(flats, "f1.fits"), "FLAT", camera: "ZWO ASI533MC Pro", filter: null)
            .Cal(Path.Combine(flats, "f2.fits"), "FLAT", camera: "ZWO ASI533MC Pro", filter: null);
        using var h = t.Open(N);
        var session = h.Page.Sessions[0];
        Scanned(session);

        var row = CalRow(session, flats);
        Assert.True(row.IsTicked);
        Assert.Equal("2 of 2 files", row.CountText);
        Assert.Equal("same night", row.Note);
        Assert.StartsWith("Light sets: ZWO ASI533MC Pro, no filter, ", Assert.Single(session.LightSetLines), StringComparison.Ordinal);
        Assert.Empty(session.Warnings);
    }

    [Fact]
    public async Task MixedLightFolder_ExcludesForeignFilesAndKeepsTheRowTicked()
    {
        using var t = new Tree();
        var ha = t.P("2025-10-15", "LIGHT", "Ha");
        var flats = t.P("2025-10-15", "FLAT");
        t.Light(N, Path.Combine(ha, "a.fits"))
            .Light(N, Path.Combine(ha, "b.fits"))
            .Light(N, Path.Combine(ha, "c.fits"))
            .Foreign(N, Path.Combine(ha, "x1.fits"), "obj:NGC 7000", "NGC 7000")
            .Foreign(N, Path.Combine(ha, "x2.fits"), "obj:NGC 7000", "NGC 7000")
            .Cal(Path.Combine(flats, "f1.fits"), "FLAT");
        using var h = t.Open(N);
        var session = h.Page.Sessions[0];
        var row = Row(session, ha);
        Assert.True(Is(ha, session.Chosen!.Level.Path));
        Scanned(session);
        Until(() => row.FrameCountText == "3 of 5 files");

        Assert.True(row.IsIncluded);
        Assert.Equal(2, session.ForeignExcluded.Count);
        Assert.True(CalRow(session, flats).IsTicked);

        Stage(h);
        Generate(h.Page);
        var lightsJob = Jobs(h.Page.ScriptText!).Single(j => Is(j.Src, ha));
        Assert.Equal(["x1.fits", "x2.fits"], lightsJob.ExcludeFiles);

        var result = await Copy(h);
        Assert.Equal(StagingOutcome.Completed, result.Outcome);
        Assert.Equal(4, result.Copied);
        var staged = Staged(h);
        Assert.DoesNotContain(staged, p => p.EndsWith("x1.fits", StringComparison.Ordinal) || p.EndsWith("x2.fits", StringComparison.Ordinal));
        Assert.Equal(3, staged.Count(p => Path.GetDirectoryName(p)!.Split(Path.DirectorySeparatorChar).Length == 1));
    }

    [Fact]
    public async Task DateFolderChosen_EdgeRule_CopiesCalibrationOnceUnderTypeFolders()
    {
        using var t = new Tree();
        var date = t.P("2025-10-15");
        var lights = t.P("2025-10-15", "LIGHT", "M 31");
        var flats = t.P("2025-10-15", "FLAT");
        var old = t.P("2025-10-15", "FLAT", "old");
        t.Light(N, Path.Combine(lights, "a.fits"))
            .Light(N, Path.Combine(lights, "b.fits"))
            .Cal(Path.Combine(flats, "f1.fits"), "FLAT")
            .Cal(Path.Combine(flats, "f2.fits"), "FLAT")
            .Cal(Path.Combine(old, "o1.fits"), "FLAT", rotation: 139.07);
        using var h = t.Open(N);
        var session = h.Page.Sessions[0];
        var dateRow = Row(session, date);
        session.ChosenIndex = dateRow.Index;
        Scanned(session);
        Until(() => dateRow.Children.Any(c => c.FolderName == "FLAT"));
        var flatRow = dateRow.Children.Single(c => c.FolderName == "FLAT");
        Until(() => flatRow.IsOwnedByCalibration);

        Assert.False(flatRow.IsVisible);
        Assert.True(CalRow(session, flats).IsTicked);
        Assert.False(CalRow(session, old).IsTicked);

        Stage(h);
        Generate(h.Page);
        var jobs = Jobs(h.Page.ScriptText!);
        var lightsJob = jobs.Single(j => Is(j.Src, date));
        Assert.Contains(@"FLAT\", lightsJob.ExcludeDirs);
        var flatJob = jobs.Single(j => Is(j.Src, flats));
        Assert.Equal(lightsJob.Dst + @"\FLAT", flatJob.Dst);
        Assert.Equal(2, jobs.Count);

        var result = await Copy(h);
        Assert.Equal(StagingOutcome.Completed, result.Outcome);
        Assert.Empty(result.Skipped);
        Assert.Empty(result.Failed);
        var entry = lightsJob.Dst;
        Assert.Equal(
            new[]
            {
                Path.Combine(entry, "FLAT", "f1.fits"),
                Path.Combine(entry, "FLAT", "f2.fits"),
                Path.Combine(entry, "LIGHT", "M 31", "a.fits"),
                Path.Combine(entry, "LIGHT", "M 31", "b.fits"),
            },
            Staged(h));
    }

    [Fact]
    public async Task UnreadableDirectoryAndUnparsableFile_AreReportedAndDoNotBlockCopy()
    {
        using var t = new Tree();
        var lights = t.P("2025-10-15", "LIGHT", "M 31");
        var flats = t.P("2025-10-15", "FLAT");
        var garbage = Path.Combine(flats, "broken.fits");
        var locked = t.P("2025-10-15", "Logs");
        t.Light(N, Path.Combine(lights, "a.fits"))
            .Cal(Path.Combine(flats, "f1.fits"), "FLAT")
            .Cal(Path.Combine(flats, "f2.fits"), "FLAT");
        Tree.Write(garbage, [.. Enumerable.Range(0, 4000).Select(i => (byte)(i * 7))]);
        t.Lock(locked);
        using var h = t.Open(N);
        var session = h.Page.Sessions[0];
        Scanned(session);

        Assert.EndsWith(", skipped 2 files that could not be read", session.ScanSentence, StringComparison.Ordinal);
        Assert.Contains(garbage, session.IssuesTooltip, StringComparison.Ordinal);
        Assert.Contains(locked, session.IssuesTooltip, StringComparison.Ordinal);
        var row = CalRow(session, flats);
        Assert.True(row.IsTicked);
        Assert.Equal("2 of 2 files", row.CountText);

        Stage(h);
        var result = await Copy(h);
        Assert.Equal(StagingOutcome.Completed, result.Outcome);
        Assert.Empty(result.Failed);
        Assert.Empty(result.Skipped);
        Assert.Equal(3, result.Copied);
        Assert.DoesNotContain(Staged(h), p => p.EndsWith("broken.fits", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FullSize_MatchesWhatTheCopierCopies()
    {
        using var t = new Tree();
        var lights = t.P("2025-10-15", "LIGHT", "M 31");
        t.Light(N, Path.Combine(lights, "a.fits"))
            .Light(N, Path.Combine(lights, "b.fits"))
            .Cal(t.P("2025-10-15", "FLAT", "f1.fits"), "FLAT")
            .Cal(t.P("2025-10-15", "FLAT", "f2.fits"), "FLAT")
            .Cal(t.P("2025-10-15", "DARKFLAT", "d1.fits"), "DARKFLAT");
        // An uncatalogued sidecar beside the lights, which the copier copies with them.
        Tree.Write(Path.Combine(lights, "a.json"), [.. Enumerable.Repeat((byte)'x', 1234)]);
        using var h = t.Open(N);
        var session = h.Page.Sessions[0];
        Scanned(session);
        Until(() => !h.Page.SizeText.EndsWith(", sizing", StringComparison.Ordinal));
        var footerBytes = h.Page.TotalSizeBytes;
        var footerFiles = h.Page.Totals!.FrameCount + h.Page.Sessions.Sum(s => s.CalibrationTotals().Count);

        Stage(h);
        Assert.Empty(Directory.EnumerateFileSystemEntries(h.Page.StagingPath!));
        var result = await Copy(h);

        Assert.Equal(StagingOutcome.Completed, result.Outcome);
        Assert.Equal(footerBytes, result.BytesCopied);
        Assert.Equal(MetricText.Bytes(result.BytesCopied), h.Page.SizeText);
        Assert.Equal(footerFiles, result.Copied);
        Assert.StartsWith(result.Copied.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ", h.Page.FrameCountText, StringComparison.Ordinal);
    }
}
