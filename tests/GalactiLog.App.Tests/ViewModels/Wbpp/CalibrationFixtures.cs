using GalactiLog.Core.Io;
using GalactiLog.Core.Wbpp;

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
