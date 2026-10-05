using System.Diagnostics.CodeAnalysis;
using GalactiLog.Core.Fits;
using GalactiLog.Core.Io;
using GalactiLog.Core.Xisf;

namespace GalactiLog.Core.Metadata;

// The one path from a file path to extracted metadata (spec 10.2's extension set, 10.3's
// ingest order). The CLI's inspect verb and Phase 4's scan both call TryRead, so the
// supported-extension gate, the reader choice, the CSV backfill and the "this file is not
// usable" decision exist once instead of once per caller (design-lessons rule 1: build the
// shared spine the second time the pattern appears).
//
// Rejections come back as data, never as exceptions: no ILogger in Core.
public static class FrameReader
{
    // Spec 10.2. Matching is case-insensitive; the list is the single source of truth for
    // which files the scanner and the CLI will even open.
    public static readonly string[] SupportedExtensions = { ".fits", ".fit", ".fts", ".xisf" };

    public enum FrameFormat { Unsupported, Fits, Xisf }

    public static FrameFormat FormatOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.Equals(ext, ".xisf", StringComparison.OrdinalIgnoreCase))
        {
            return FrameFormat.Xisf;
        }
        return string.Equals(ext, ".fits", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".fit", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".fts", StringComparison.OrdinalIgnoreCase)
            ? FrameFormat.Fits
            : FrameFormat.Unsupported;
    }

    public static bool IsSupported(string path) => FormatOf(path) != FrameFormat.Unsupported;

    /// <summary>
    /// Opens one frame, reads its header, extracts metadata and applies best-effort CSV
    /// backfill (spec 10.3). Returns false with <paramref name="rejectionReason"/> set for a
    /// missing file, an unsupported extension, a rejected header, or a file that exists but
    /// cannot be read; never throws for any of those.
    /// </summary>
    /// <param name="csv">
    /// Reader whose per-directory CSV cache is reused across a scan run. Pass a throwaway
    /// instance for a one-off read.
    /// </param>
    public static bool TryRead(
        string path,
        NinaCsvReader csv,
        [NotNullWhen(true)] out MetadataExtractionResult? result,
        out string? rejectionReason)
    {
        result = null;

        if (!UserFiles.Exists(path))
        {
            rejectionReason = $"file not found: {path}";
            return false;
        }

        var format = FormatOf(path);
        if (format == FrameFormat.Unsupported)
        {
            rejectionReason = $"unsupported file extension: {Path.GetExtension(path)}";
            return false;
        }

        var fileName = Path.GetFileName(path);
        try
        {
            using var stream = UserFiles.OpenRead(path);
            if (format == FrameFormat.Fits)
            {
                var header = FitsHeaderReader.Read(stream);
                if (!header.Accepted)
                {
                    rejectionReason = header.RejectionReason;
                    return false;
                }
                result = MetadataExtractor.FromFits(header.Cards, fileName);
            }
            else
            {
                var header = XisfHeaderReader.Read(stream);
                if (!header.Accepted)
                {
                    rejectionReason = header.RejectionReason;
                    return false;
                }
                result = MetadataExtractor.FromXisf(header, fileName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file exists but could not actually be read (locked, permission denied,
            // truncated mid-read): a rejection, not a crash.
            rejectionReason = ex.Message;
            return false;
        }

        // Best-effort (spec 7.4): a missing or malformed CSV never fails the read.
        result = csv.ApplyCsvBackfill(result, path);
        rejectionReason = null;
        return true;
    }
}
