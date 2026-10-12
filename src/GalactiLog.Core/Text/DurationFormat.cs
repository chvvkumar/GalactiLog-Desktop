using System.Globalization;

namespace GalactiLog.Core.Text;

/// <summary>
/// The one way a duration reads (ux-flow F12): <see cref="Table"/> for a column of figures,
/// <see cref="Prose"/> for a figure in a sentence, a tile or a caption. Before it the same
/// integration read "131.1 h" on the dashboard, "06h 45m 18s" on Statistics and "01h 40m" on
/// Mosaics.
/// </summary>
public static class DurationFormat
{
    /// <summary>Hours with one decimal and thousands separators, for example <c>131.1 h</c>. A
    /// negative or non-finite total reads as zero, as in <see cref="Prose"/>.</summary>
    public static string Table(double seconds)
        => string.Create(CultureInfo.InvariantCulture, $"{Clamp(seconds) / 3600d:N1} h");

    private static double Clamp(double seconds) => double.IsFinite(seconds) ? Math.Max(0d, seconds) : 0d;

    /// <summary>
    /// Hours and minutes with no leading zeros, for example <c>6 h 45 m</c>. Seconds show only
    /// below an hour (<c>45 m 18 s</c>), and a zero trailing field is dropped (<c>2 h</c>). A
    /// negative or non-finite total reads as zero.
    /// </summary>
    /// <remarks>The total is rounded to the second once, half away from zero, and every field is
    /// derived from that rounded total, so 3599.6 s reads <c>1 h</c> rather than <c>60 m</c>.</remarks>
    public static string Prose(double seconds)
    {
        var total = (long)Math.Round(Clamp(seconds), MidpointRounding.AwayFromZero);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var rest = total % 60;

        if (hours > 0)
        {
            return minutes == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{hours} h")
                : string.Create(CultureInfo.InvariantCulture, $"{hours} h {minutes} m");
        }

        if (minutes > 0)
        {
            return rest == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{minutes} m")
                : string.Create(CultureInfo.InvariantCulture, $"{minutes} m {rest} s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{rest} s");
    }
}
