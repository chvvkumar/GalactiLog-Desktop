using System.Diagnostics;
using System.Text.Json.Nodes;
using GalactiLog.Core.Io;
using GalactiLog.Core.Metadata;
using GalactiLog.Core.Tests.Fixtures;
using GalactiLog.Core.Wbpp;
using Xunit;

namespace GalactiLog.Core.Tests.Wbpp;

public sealed class CalibrationWalkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "calwalk-" + Guid.NewGuid().ToString("N"));
    private readonly NightRule _rule = new(true, -97.5, null);
    private string? _junction;

    public CalibrationWalkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (_junction is not null) Directory.Delete(_junction, recursive: false);
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string Fits(string relative, string type, string? filter = "Ha", double gain = 110, double offset = 50,
        double rot = 128.85, string dateObs = "2025-10-16T01:15:26", string camera = "ZWO ASI2600MM Pro")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var b = new FitsBuilder().Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 0L).Card("IMAGETYP", type).Card("INSTRUME", camera);
        if (filter is not null) b.Card("FILTER", filter);
        File.WriteAllBytes(path, b.Card("XBINNING", 1L).Card("GAIN", gain).Card("OFFSET", offset)
            .Card("TELESCOP", "SQA55").Card("ROTATANG", rot).Card("EXPOSURE", 30.0)
            .Card("DATE-OBS", dateObs).Build().ToArray());
        return path;
    }

    private WalkResult Walk(Func<string, bool>? skipHeaderRead = null, IProgress<int>? progress = null, CancellationToken ct = default)
        => CalibrationWalk.Scan(_root, skipHeaderRead ?? (_ => false), _rule, UserFiles.EnumerateFileSystemEntries,
            p => CalibrationWalk.ReadFrame(p, 0, _rule), progress, ct);

    private sealed class SyncProgress(Action<int> a) : IProgress<int>
    {
        public void Report(int v) => a(v);
    }

    [Fact]
    public void Scan_ReadsHeadersBelowTheRoot_AndSizesEveryFile()
    {
        Fits(Path.Combine("FLAT", "a.fits"), "FLAT");
        Fits(Path.Combine("FLAT", "sub", "b.fits"), "FLAT", filter: "OIII");
        File.WriteAllText(Path.Combine(_root, "FLAT", "note.json"), "{}");

        var r = Walk();

        Assert.Equal(3, r.Frames.Count);
        Assert.Equal(2, r.HeadersRead);
        Assert.Empty(r.Issues);
        var flats = r.Frames.Where(f => f.Type == CalibrationType.Flat).ToList();
        Assert.Equal(2, flats.Count);
        Assert.All(flats, f => Assert.True(f.Size > 0));
        Assert.Contains(flats, f => f.Keys == new FrameKeys("ZWO ASI2600MM Pro", "Ha", 1, 110, 50, "SQA55", 128.85, null)
            && f.ExposureSeconds == 30);
        var json = r.Frames.Single(f => f.FilePath.EndsWith("note.json"));
        Assert.Equal(CalibrationType.Other, json.Type);
        Assert.Equal(2, json.Size);
        Assert.Null(json.Night);
    }

    [Fact]
    public void Scan_SkippedFiles_AreSizedButNotRead_AndTheirNeighboursAreRead()
    {
        // A light folder holding a catalogued light and a flat beside it: the predicate is per
        // file, so the light is sized only and the flat in the same folder is read.
        var light = Fits(Path.Combine("T", "2025-10-15", "l.fits"), "LIGHT");
        Fits(Path.Combine("T", "2025-10-15", "f.fits"), "FLAT");

        var r = Walk(path => path == light);

        Assert.Equal(1, r.HeadersRead);
        var sized = r.Frames.Single(f => f.FilePath.EndsWith("l.fits"));
        Assert.Equal(CalibrationType.Other, sized.Type);
        Assert.True(sized.Size > 0);
        Assert.Equal(new FrameKeys(null, null, null, null, null, null, null, null), sized.Keys);
        Assert.Equal(CalibrationType.Flat, r.Frames.Single(f => f.FilePath.EndsWith("f.fits")).Type);
    }

    [Fact]
    public void Scan_DerivesTheNightThroughTheNoonRule()
    {
        Fits("f.fits", "FLAT", dateObs: "2025-10-16T13:27:19Z");
        Assert.Equal(new DateOnly(2025, 10, 15), Walk().Frames.Single().Night);
    }

    [Fact]
    public void Scan_UnreadableDirectory_IsAnIssue_AndTheWalkContinues()
    {
        Fits(Path.Combine("bad", "x.fits"), "FLAT");
        Fits(Path.Combine("ok", "y.fits"), "FLAT");
        var bad = Path.Combine(_root, "bad");

        var r = CalibrationWalk.Scan(_root, _ => false, _rule,
            d => d == bad ? throw new UnauthorizedAccessException() : UserFiles.EnumerateFileSystemEntries(d),
            p => CalibrationWalk.ReadFrame(p, 0, _rule), null, default);

        Assert.Equal(new ScanIssue(bad, "folder could not be read"), Assert.Single(r.Issues));
        Assert.EndsWith("y.fits", Assert.Single(r.Frames).FilePath);
    }

    [Fact]
    public void Scan_UnparsableFile_IsAnIssue_AndCountsAsOther()
    {
        var p = Path.Combine(_root, "junk.fits");
        File.WriteAllBytes(p, [1, 2, 3, 4]);

        var r = Walk();

        Assert.Equal(new ScanIssue(p, "header could not be read"), Assert.Single(r.Issues));
        var f = Assert.Single(r.Frames);
        Assert.Equal(CalibrationType.Other, f.Type);
        Assert.Equal(4, f.Size);
        Assert.Equal(1, r.HeadersRead);
    }

    [Fact]
    public void Scan_SkipsReparsePoints()
    {
        var outside = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "e.txt"), "x");
        var scanRoot = Path.Combine(_root, "scan");
        Directory.CreateDirectory(scanRoot);
        var link = Path.Combine(scanRoot, "linked");
        using var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{outside}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        mklink.WaitForExit();
        Assert.True(mklink.ExitCode == 0, "junction could not be created");
        _junction = link;

        var r = CalibrationWalk.Scan(scanRoot, _ => false, _rule, UserFiles.EnumerateFileSystemEntries,
            p => CalibrationWalk.ReadFrame(p, 0, _rule), null, default);

        Assert.Empty(r.Frames);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Scan_HonoursCancellation()
    {
        Fits("a.fits", "FLAT");
        Fits("b.fits", "FLAT");
        using var cts = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() =>
            Walk(progress: new SyncProgress(_ => cts.Cancel()), ct: cts.Token));
    }

    [Fact]
    public void Scan_ReportsRunningHeaderCount()
    {
        Fits("a.fits", "FLAT");
        Fits("b.fits", "FLAT");
        var seen = new List<int>();
        Walk(progress: new SyncProgress(seen.Add));
        Assert.Equal([1, 2], seen);
    }

    [Fact]
    public void TypeOf_MapsNinaSpellings()
    {
        Assert.Equal(CalibrationType.DarkFlat, CalibrationWalk.TypeOf("FLATDARK"));
        Assert.Equal(CalibrationType.DarkFlat, CalibrationWalk.TypeOf("BIASFLAT"));
        Assert.Equal(CalibrationType.Other, CalibrationWalk.TypeOf(null));
        Assert.Equal(CalibrationType.Flat, CalibrationWalk.TypeOf(" flat "));
        Assert.Equal(CalibrationType.Light, CalibrationWalk.TypeOf("Light"));
    }

    [Fact]
    public void KeysOf_PrefersRotatangOverRotator_AndNeverObjctrot()
    {
        var raw = new JsonObject { ["ROTATANG"] = 128.85, ["ROTATOR"] = 5.0, ["OBJCTROT"] = 307.0, ["READOUTM"] = " Low " };
        var both = CalibrationWalk.KeysOf(new ExtractedMetadata { RawHeaders = raw, RotatorPosition = 307 });
        Assert.Equal(128.85, both.RotatorAngle);
        Assert.Equal("Low", both.ReadoutMode);

        raw.Remove("ROTATANG");
        Assert.Equal(5.0, CalibrationWalk.KeysOf(new ExtractedMetadata { RawHeaders = raw, RotatorPosition = 307 }).RotatorAngle);

        raw.Remove("ROTATOR");
        Assert.Null(CalibrationWalk.KeysOf(new ExtractedMetadata { RawHeaders = raw, RotatorPosition = 307 }).RotatorAngle);
    }

    [Fact]
    public void Excluding_DropsFilesUnderAPatternMatchedComponent_AndNeverReadsThem()
    {
        var real = Fits(@"Date_X\FLAT\f1.fits", "FLAT");
        var wbpp = Fits(@"Date_X\WBPP\calibrated\flat\f1_c.fits", "FLAT");
        var named = Fits(@"Date_X\FLAT_CALIBRATED\f2.fits", "FLAT");
        var read = new List<string>();
        CalibrationWalk.Walker inner = (root, skip, rule, progress, ct) => CalibrationWalk.Scan(
            root, skip, rule, UserFiles.EnumerateFileSystemEntries,
            p => { read.Add(p); return CalibrationWalk.ReadFrame(p, 0, rule); }, progress, ct);

        var r = CalibrationWalk.Excluding(inner, ScriptGenerator.DefaultExclusions)(Path.Combine(_root, "Date_X"), _ => false, _rule, null, default);

        Assert.Equal([real], r.Frames.Select(f => f.FilePath));
        Assert.Equal([real], read);
        Assert.Equal(1, r.HeadersRead);
        Assert.DoesNotContain(wbpp, r.Frames.Select(f => f.FilePath));
        Assert.DoesNotContain(named, r.Frames.Select(f => f.FilePath));
    }

    [Fact]
    public void Excluding_TestsOnlyComponentsBelowTheRoot()
    {
        // A pattern-named folder above the walked root does not hide what is under it, as the
        // copier tests names only below an operation's source.
        var path = Fits(@"WBPP\Date_X\FLAT\f1.fits", "FLAT");
        CalibrationWalk.Walker inner = (root, skip, rule, progress, ct) => CalibrationWalk.Scan(
            root, skip, rule, UserFiles.EnumerateFileSystemEntries, p => CalibrationWalk.ReadFrame(p, 0, rule), progress, ct);

        var r = CalibrationWalk.Excluding(inner, ["WBPP"])(Path.Combine(_root, "WBPP", "Date_X"), _ => false, _rule, null, default);

        Assert.Equal([path], r.Frames.Select(f => f.FilePath));
    }
}
