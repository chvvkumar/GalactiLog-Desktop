namespace GalactiLog.Core.Metadata;

// Calibration frame classification, spec 7.5 (scanner.CALIBRATION_FRAME_TYPES). Standalone
// predicate; nothing in this phase wires it into a scan pipeline (Phase 4 does).
public static class CalibrationFrames
{
    public static readonly IReadOnlySet<string> FrameTypes =
        new HashSet<string> { "BIAS", "DARK", "FLAT", "DARKFLAT", "BIASFLAT" };

    public static bool IsCalibrationFrame(string? imageType)
        => imageType is not null && FrameTypes.Contains(imageType.Trim().ToUpperInvariant());
}
