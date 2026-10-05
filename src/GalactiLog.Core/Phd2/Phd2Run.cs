namespace GalactiLog.Core.Phd2;

// Port of the Phd2Run and Phd2LogSummary dataclasses of
// backend/app/services/phd2_parser.py, spec 7.6.

/// <summary>The Log Summary line at the end of a run: a free parser cross-check.</summary>
// PHD2's own count of what it believes it wrote. The parser compares it against what it read
// and warns on a mismatch; it never trusts it over what it read.
public sealed record Phd2LogSummary(
    int CalibrationCount,
    int GuidingCount,
    int GuidingDurationS,
    int GuidingFrameCount);

/// <summary>One PHD2 process lifetime within a physical log file.</summary>
// Phd2Version and Platform are null for the PHD2 build embedded in an ASIAIR, which writes its
// banner with both left out. Absent is recorded as absent: nothing else in those files says
// which build produced them.
public sealed record Phd2Run
{
    public string? Phd2Version { get; set; }
    public string? Platform { get; set; }
    public string LogVersion { get; set; } = "";
    public DateTime? LogEnabledAt { get; set; }
    public DateTime? LogClosedAt { get; set; }
    public Phd2LogSummary? Summary { get; set; }
    public List<Phd2Section> Sections { get; set; } = new();
    public List<Phd2Calibration> Calibrations { get; set; } = new();

    // A Guiding Ends with no open section. Counted, never fatal.
    public int OrphanGuidingEnds { get; set; }

    public List<string> Warnings { get; set; } = new();
}
