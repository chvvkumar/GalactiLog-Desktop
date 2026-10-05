namespace GalactiLog.Core.Phd2;

// Port of the Phd2Calibration and Phd2CalibrationStep dataclasses of
// backend/app/services/phd2_parser.py, spec 7.6.

/// <summary>One row of a calibration block's CSV.</summary>
// Direction is a string and not an enum: PHD2 may add a leg, and Backlash is already a fifth
// value beside the four compass legs. Nothing here is nullable because a row with an
// unparseable cell is dropped whole.
public sealed record Phd2CalibrationStep(
    string Direction,
    int Step,
    double Dx,
    double Dy,
    double X,
    double Y,
    double Dist);

/// <summary>One calibration block, from Calibration Begins to Calibration complete.</summary>
// Only the West and North legs ever carry an angle, a rate and a parity: East and South
// re-centre the star and emit no completion line, so there is deliberately no East or South
// triple here.
public sealed record Phd2Calibration
{
    public DateTime? StartedAtLocal { get; set; }
    public Phd2Header Header { get; set; } = new();
    public List<Phd2CalibrationStep> Steps { get; set; } = new();
    public double? WestAngleDeg { get; set; }
    public double? WestRatePxS { get; set; }
    public string? WestParity { get; set; }
    public double? NorthAngleDeg { get; set; }
    public double? NorthRatePxS { get; set; }
    public string? NorthParity { get; set; }
    public bool Completed { get; set; }

    // From the "Calibration complete, mount = ..." line, not from the header.
    public string? MountName { get; set; }
}
