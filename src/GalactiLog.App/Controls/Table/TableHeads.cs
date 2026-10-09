namespace GalactiLog.App.Controls.Table;

/// <summary>
/// The shared column headings and their tooltips (spec.md items 4 and 5), reached from markup
/// through <c>x:Static</c> so two tables cannot spell one metric two ways. Sentence case, plain
/// words, the unit once in the heading, and an abbreviation always has a tooltip. A team that needs
/// a new shared heading adds a constant here, never a literal in a view.
/// </summary>
public static class TableHeads
{
    public const string Hfr = "HFR px";
    public const string HfrTip = "Half-flux radius, pixels";
    public const string Ecc = "Ecc";
    public const string EccTip = "Eccentricity";
    public const string Fwhm = "FWHM \"";
    public const string FwhmTip = "Full width at half maximum, arcseconds";
    public const string Rms = "RMS \"";
    public const string RmsTip = "Guiding RMS error, arcseconds";
    public const string Stars = "Stars";
    public const string Hours = "Hours";
    public const string Frames = "Frames";
    public const string Exposure = "Exp s";
    public const string ExposureTip = "Exposure, seconds";
    public const string Total = "Total";
    public const string Filter = "Filter";
    public const string Night = "Night";
}
