using System.Text.Json.Serialization;

namespace GalactiLog.Core.Phd2;

// Port of the Phd2Header, Phd2Event and Phd2Frame dataclasses of
// backend/app/services/phd2_parser.py, spec 7.6. Shapes are fixed by
// docs/superpowers/work/phase15a/record-shapes.md, which Task 3 codes against.

/// <summary>Every header line PHD2 writes above a guiding or calibration block.</summary>
// One instance per section, never per file: one corpus file carries a misconfigured one-off
// pixel scale in a single section's header, so a file-level pixel scale would corrupt every
// arcsecond conversion in that file. Mutable because the parser folds one line at a time into
// a header that already exists; treated as read-only once Parse returns.
public sealed record Phd2Header
{
    public string? EquipmentProfile { get; set; }
    public double? PixelScaleArcsec { get; set; }
    public int? Binning { get; set; }
    public double? FocalLengthMm { get; set; }
    public string? GuideCamera { get; set; }
    public double? ExposureMs { get; set; }
    public string? MountName { get; set; }
    public double? XAngleDeg { get; set; }
    public double? XRatePxS { get; set; }
    public double? YAngleDeg { get; set; }
    public double? YRatePxS { get; set; }
    public string? Parity { get; set; }
    public double? NormRateRa { get; set; }
    public double? NormRateDec { get; set; }
    public double? OrthoErrorDeg { get; set; }
    public string? AlgoRa { get; set; }
    public string? AlgoDec { get; set; }
    public double? MinMoveRa { get; set; }
    public double? MinMoveDec { get; set; }

    // PHD2 writes the two axes in different units on adjacent lines: AggressionRa is the raw
    // X-axis number (a fraction, 0.700) and AggressionDec is the Y-axis percentage with its
    // '%' never captured (100.0). Recorded as written; only AggressionRa reaches a stored column.
    public double? AggressionRa { get; set; }
    public double? AggressionDec { get; set; }

    public double? HysteresisRa { get; set; }
    public bool? BacklashCompEnabled { get; set; }
    public double? BacklashPulseMs { get; set; }
    public double? MaxRaDurationMs { get; set; }
    public double? MaxDecDurationMs { get; set; }
    public string? DecGuideMode { get; set; }
    public double? RaGuideSpeed { get; set; }
    public double? DecGuideSpeed { get; set; }
    public double? CalDecDeg { get; set; }
    public string? LastCalIssue { get; set; }
    public DateTime? CalTimestamp { get; set; }
    public double? RaHr { get; set; }
    public double? DecDeg { get; set; }
    public double? HourAngleHr { get; set; }
    public string? PierSide { get; set; }
    public string? RotatorPos { get; set; }
    public double? AltDeg { get; set; }
    public double? AzDeg { get; set; }
    public double? LockX { get; set; }
    public double? LockY { get; set; }
    public double? StarX { get; set; }
    public double? StarY { get; set; }
    public double? HfdPx { get; set; }
}

/// <summary>The seven INFO event kinds the classifier produces.</summary>
// StarLost is classified but never stored on a section: its only producer is a calibration
// block, where no guiding section is open. The type exists so the classifier is complete.
public static class Phd2EventTypes
{
    public const string SettleStart = "settle_start";
    public const string SettleDone = "settle_done";
    public const string SettleFailed = "settle_failed";
    public const string Dither = "dither";
    public const string LockShift = "lock_shift";
    public const string StarLost = "star_lost";
    public const string ParamChange = "param_change";
}

/// <summary>A non-CSV INFO line interleaved with the frame rows.</summary>
// TimeOffset is the Time value of the most recent CSV row in the same section, 0.0 before the
// first row: PHD2 does not stamp INFO lines and the preceding frame is the tightest bound
// available. The three JSON names are the stored phd2_sessions.events document's own names,
// declared here once rather than in a projection the caller would have to keep in step.
public sealed record Phd2Event(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("t")] double TimeOffset,
    [property: JsonPropertyName("detail")] string Detail);

/// <summary>One CSV row of a guiding section. All distances are pixels.</summary>
// Arcsecond conversion is the consumer's job and multiplies by the section header's pixel
// scale, so a corrected pixel scale never needs a data migration. CSV columns XStep and YStep
// are read and discarded, exactly as the Python does.
public sealed record Phd2Frame(
    int FrameIndex,
    double TimeOffset,
    bool Dropped,
    double? Dx,
    double? Dy,
    double? RaRaw,
    double? DecRaw,
    double? RaGuide,
    double? DecGuide,
    int RaDurationMs,
    string RaDirection,
    int DecDurationMs,
    string DecDirection,
    double? StarMass,
    double? Snr,
    int? ErrorCode,
    string DropReason);

/// <summary>One guiding block, from Guiding Begins to Guiding Ends.</summary>
// Truncated says something went wrong; DiscardedRows says how much it cost. A corrupt row in
// the last line of a section and a corrupt row two lines in are indistinguishable without the
// count, and one of them is a lost night.
public sealed record Phd2Section
{
    public DateTime? StartedAtLocal { get; set; }
    public Phd2Header Header { get; set; } = new();
    public DateTime? EndedAtLocal { get; set; }
    public List<Phd2Frame> Frames { get; set; } = new();
    public List<Phd2Event> Events { get; set; } = new();
    public bool Truncated { get; set; }
    public int DiscardedRows { get; set; }
}
