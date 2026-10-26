using System.Globalization;
using System.Text.Json.Nodes;
using GalactiLog.Core.Io;
using GalactiLog.Core.Metadata;
using GalactiLog.Core.Sessions;

namespace GalactiLog.Core.Wbpp;

/// <summary>The calibration pre-selection's only disk read: every file below a night's folder,
/// sized, with the header of each one worth reading. The view-models never call it; they receive
/// <see cref="OnDisk"/> as a <see cref="Walker"/>.</summary>
public static class CalibrationWalk
{
    /// <summary>Walks <c>root</c>. <c>skipHeaderRead</c> takes a file's full path and is true for a
    /// file to size without reading its header: a catalogued light, or a file an earlier climb step
    /// already read.</summary>
    public delegate WalkResult Walker(string root, Func<string, bool> skipHeaderRead, NightRule nightRule,
        IProgress<int>? progress, CancellationToken ct);

    private static readonly FrameKeys NoKeys = new(null, null, null, null, null, null, null, null);

    /// <summary>Walks <paramref name="root"/> without recursion. A file for which
    /// <paramref name="skipHeaderRead"/> is true, or with an extension the header reader does not
    /// support, is sized and typed <see cref="CalibrationType.Other"/>; any other file goes to
    /// <paramref name="readFrame"/>, whose null return is a <see cref="ScanIssue"/>. A directory
    /// that throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> on
    /// enumeration is an issue and is skipped; reparse-point directories are skipped silently.</summary>
    public static WalkResult Scan(string root, Func<string, bool> skipHeaderRead, NightRule nightRule,
        Func<string, IEnumerable<FileSystemInfo>> enumerate, Func<string, ScannedFrame?> readFrame,
        IProgress<int>? progress, CancellationToken ct)
    {
        var frames = new List<ScannedFrame>();
        var issues = new List<ScanIssue>();
        var headers = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            List<FileSystemInfo> entries;
            try
            {
                entries = enumerate(dir).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new ScanIssue(dir, "folder could not be read"));
                continue;
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry is DirectoryInfo sub)
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(sub.FullName);
                    continue;
                }
                if (entry is not FileInfo file) continue;

                var size = file.Length;
                ScannedFrame? read = null;
                if (FrameReader.IsSupported(file.FullName) && !skipHeaderRead(file.FullName))
                {
                    read = readFrame(file.FullName);
                    headers++;
                    progress?.Report(headers);
                    if (read is null) issues.Add(new ScanIssue(file.FullName, "header could not be read"));
                }

                frames.Add(read is null
                    ? new ScannedFrame(file.FullName, size, CalibrationType.Other, NoKeys, null, null)
                    : read with { Size = size });
            }
        }

        return new WalkResult(frames, issues, headers);
    }

    /// <summary>Reads one file's header into a <see cref="ScannedFrame"/> with the given size, or
    /// null when the header cannot be read. No pixel data is read.</summary>
    public static ScannedFrame? ReadFrame(string path, long size, NightRule nightRule)
    {
        if (!FrameReader.TryRead(path, new NinaCsvReader(), out var result, out _)) return null;
        var m = result.Metadata;

        DateTime? captureUtc = null;
        if (m.CaptureDate is { } text
            && DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            captureUtc = parsed;
        }

        DateOnly? night = null;
        if (captureUtc is { } capture)
        {
            var longitude = SessionDate.ResolveLongitude(
                m.RawHeaders, nightRule.ObserverLongitude, nightRule.ObserverTimezone, capture).Longitude;
            night = SessionDate.Compute(capture, nightRule.UseImagingNight, longitude);
        }

        return new ScannedFrame(path, size, TypeOf(m.ImageType), KeysOf(m), m.ExposureTime, night);
    }

    /// <summary>The walk over the real disk, reading headers through <see cref="FrameReader"/>.</summary>
    public static readonly Walker OnDisk = (root, skipHeaderRead, nightRule, progress, ct) =>
        Scan(root, skipHeaderRead, nightRule, UserFiles.EnumerateFileSystemEntries,
            path => ReadFrame(path, 0, nightRule), progress, ct);

    internal static CalibrationType TypeOf(string? imageType) => imageType?.Trim().ToUpperInvariant() switch
    {
        "LIGHT" => CalibrationType.Light,
        "FLAT" => CalibrationType.Flat,
        "DARK" => CalibrationType.Dark,
        "BIAS" => CalibrationType.Bias,
        "DARKFLAT" or "FLATDARK" or "BIASFLAT" => CalibrationType.DarkFlat,
        _ => CalibrationType.Other,
    };

    /// <summary>The match keys of one header. The rotator angle is the mechanical one (ROTATANG,
    /// else ROTATOR), never <c>RotatorPosition</c>, which is OBJCTROT, the sky angle.</summary>
    internal static FrameKeys KeysOf(ExtractedMetadata m)
    {
        var raw = m.RawHeaders;
        return new FrameKeys(
            Text(m.Camera), Text(m.FilterUsed), Number(raw, "XBINNING"), m.CameraGain, Number(raw, "OFFSET"),
            Text(m.Telescope), Number(raw, "ROTATANG") ?? Number(raw, "ROTATOR"), Text(raw?["READOUTM"]?.ToString()));
    }

    private static string? Text(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static double? Number(JsonObject? raw, string key)
        => raw is not null && raw.TryGetPropertyValue(key, out var node) && node is not null
            && double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            && double.IsFinite(v)
                ? v
                : null;
}
