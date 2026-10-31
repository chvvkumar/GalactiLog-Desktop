using GalactiLog.Core.Io;
using GalactiLog.Core.Wbpp;
using static GalactiLog.App.Tests.ViewModels.Wbpp.CalibrationFixtures;

namespace GalactiLog.App.Tests.ViewModels.Wbpp;

/// <summary>Frame builders for the calibration cases, copied from the Core matcher tests because
/// the App test project does not reference the Core test project.</summary>
internal static class CalibrationFixtures
{
    public static FrameKeys K(string? cam = "ZWO ASI2600MM Pro", string? filter = "Ha", double? bin = 1, double? gain = 110,
        double? offset = 50, string? tel = "SQA55", double? rot = 128.85, string? readout = null) =>
        new(cam, filter, bin, gain, offset, tel, rot, readout);

    public static ScannedFrame F(string path, CalibrationType type, FrameKeys keys, double? exp = 0.01, DateOnly? night = null, long size = 100) =>
        new(path, size, type, keys, exp, night);
}

/// <summary>
/// A fictional disk for the calibration walk: every file it holds, and a <see cref="Walker"/> that
/// answers a root with the files under it, as <c>CalibrationWalk.Scan</c> would. A file the
/// skip predicate names comes back typed Other with no keys and no header read. Records every root
/// it was called with; an optional gate parks every call until released.
/// </summary>
internal sealed class FakeDisk
{
    private static readonly FrameKeys NoKeys = new(null, null, null, null, null, null, null, null);

    public List<ScannedFrame> Files { get; } = [];

    public List<ScanIssue> Issues { get; } = [];

    public ManualResetEventSlim? Gate { get; set; }

    private readonly List<string> _calls = [];

    private int _returned;

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public int Returned => Volatile.Read(ref _returned);

    public FakeDisk Add(ScannedFrame frame)
    {
        Files.Add(frame);
        return this;
    }

    public CalibrationWalk.Walker Walker => (root, skipHeaderRead, _, progress, _) =>
    {
        lock (_calls)
        {
            _calls.Add(root);
        }

        Gate?.Wait(TimeSpan.FromSeconds(30));
        var frames = new List<ScannedFrame>();
        var headers = 0;
        foreach (var file in Files.Where(f => PathConfinement.IsUnderOrEqual(root, f.FilePath)))
        {
            if (skipHeaderRead(file.FilePath))
            {
                frames.Add(new ScannedFrame(file.FilePath, file.Size, CalibrationType.Other, NoKeys, null, null));
                continue;
            }

            headers++;
            progress?.Report(headers);
            frames.Add(file);
        }

        Interlocked.Increment(ref _returned);
        return new WalkResult(frames, [.. Issues.Where(i => PathConfinement.IsUnderOrEqual(root, i.Path))], headers);
    };
}

/// <summary>The user's own layout under a fictional W: root: one night of M 45 with its flats in the date folder's FLAT branch, a rejected angle beside them, OIII lights whose flats sit under the previous date folder, and another target's lights under that date folder.</summary>
internal static class Pleiades
{
    public static readonly DateOnly N18 = new(2026, 10, 18);
    public static readonly DateOnly N15 = new(2026, 10, 15);
    public const string Cam = "ZWO ASI533MM Pro";
    public const string Incoming = @"W:\Astro\Incoming";
    public static readonly string Year = Path.Combine(Incoming, "2026");
    public static readonly string Date18 = Path.Combine(Year, "Date_2026-10-18");
    public static readonly string Date15 = Path.Combine(Year, "Date_2026-10-15");
    public static readonly string Light = Path.Combine(Date18, "LIGHT");
    public static readonly string LightCam = Path.Combine(Light, Cam);
    public static readonly string Target = Path.Combine(LightCam, "Target_Pleiades");
    public static readonly string Lights = Path.Combine(Target, "Angle_45.18");
    public static readonly string Flat = Path.Combine(Date18, "FLAT");
    public static readonly string FlatCam = Path.Combine(Flat, Cam);
    public static readonly string Flats = Path.Combine(FlatCam, "Angle_45.18");
    public static readonly string Flats96 = Path.Combine(FlatCam, "Angle_96.26");
    public static readonly string Borrowed = Path.Combine(Date15, "FLAT", Cam, "Angle_45.18");
    public static readonly string ForeignLight = Path.Combine(Date15, "LIGHT", Cam, "Target_NGC7000", "Angle_12.00", "n1.fits");
    public static readonly string L1 = Path.Combine(Lights, "L1.fits");
    public static readonly string L2 = Path.Combine(Lights, "L2.fits");
    public static readonly string L3 = Path.Combine(Lights, "L3.fits");   // the OIII light

    public static FrameKeys P(string filter = "Ha", double rot = 45.18) => K(cam: Cam, filter: filter, rot: rot);

    /// <summary>Scan root W:\Astro\Incoming; three lights of this night at 1,000 bytes each; another target's light on the previous night, so the year row holds other nights and other targets and the scan starts at the date folder.</summary>
    public static Library Catalogue()
    {
        var library = new Library { GroupKey = "obj:M 45", TargetName = "M 45" };
        library.ScanRoots.Clear();
        library.ScanRoots.Add(Incoming);
        return library
            .Frame(N18, L1, keys: P()).Frame(N18, L2, keys: P()).Frame(N18, L3, keys: P(filter: "OIII"))
            .Foreign(ForeignLight, N15);
    }

    /// <summary>Flats f1, f2 match the Ha lights; g1 is the same filter at rotation 96.26; b1, b2 are the OIII flats three nights earlier.</summary>
    public static FakeDisk Disk() => new FakeDisk()
        .Add(F(L1, CalibrationType.Light, P(), night: N18, size: 1_000))
        .Add(F(L2, CalibrationType.Light, P(), night: N18, size: 1_000))
        .Add(F(L3, CalibrationType.Light, P(filter: "OIII"), night: N18, size: 1_000))
        .Add(F(Path.Combine(Flats, "f1.fits"), CalibrationType.Flat, P(), night: N18))
        .Add(F(Path.Combine(Flats, "f2.fits"), CalibrationType.Flat, P(), night: N18))
        .Add(F(Path.Combine(Flats96, "g1.fits"), CalibrationType.Flat, P(rot: 96.26), night: N18))
        .Add(F(Path.Combine(Borrowed, "b1.fits"), CalibrationType.Flat, P(filter: "OIII"), night: N15))
        .Add(F(Path.Combine(Borrowed, "b2.fits"), CalibrationType.Flat, P(filter: "OIII"), night: N15))
        .Add(F(ForeignLight, CalibrationType.Light, P(filter: "OIII", rot: 12.0), night: N15, size: 1_000));
}
