using System.Globalization;
using System.Text;
using GalactiLog.Core.Aliases;

namespace GalactiLog.Core.Text;

/// <summary>One data line of the AstroBin CSV (spec 12.16 "The AstroBin CSV"): one (night, rig,
/// filter, exposure) group, already aggregated. Every nullable member renders as a blank cell,
/// which is what "not measured" means in this file; none of them renders as zero.</summary>
/// <param name="Date">The session date, rendered <c>yyyy-MM-dd</c>.</param>
/// <param name="FilterName">The stored filter name of the group, used only to look the AstroBin
/// id up. It never reaches the file: the filter cell is an id, which is what keeps every cell
/// free of a comma.</param>
/// <param name="FrameCount">The group's frame count, the <c>number</c> cell.</param>
/// <param name="ExposureSeconds">The group's exposure, null when the frames carry none.</param>
/// <param name="Gain">The group's modal <c>camera_gain</c>, null when no frame carries one.</param>
/// <param name="SensorTemp">The group's median <c>sensor_temp</c>, rounded on render.</param>
/// <param name="SkyQuality">The group's median <c>sky_quality</c>, the <c>meanSqm</c> cell.</param>
/// <param name="Fwhm">The group's median <c>fwhm</c>, already arcseconds (spec 7.1.1).</param>
/// <param name="AmbientTemp">The group's median <c>ambient_temp</c>, the <c>temperature</c>
/// cell.</param>
public sealed record AstroBinRow(
    DateOnly Date,
    string FilterName,
    int FrameCount,
    double? ExposureSeconds,
    int? Gain,
    double? SensorTemp,
    double? SkyQuality,
    double? Fwhm,
    double? AmbientTemp);

/// <summary>Renders spec 12.16's AstroBin acquisition CSV: <see cref="Header"/>, then one line per
/// row, joined with <c>\n</c> and ending with one <c>\n</c>. Pure: no IO, no clipboard, no clock.
/// The caller writes the text through <c>ShellIntegration</c> and nothing is written to
/// disk.</summary>
/// <remarks>
/// <para>No cell is quoted and no cell is escaped, because no cell can hold a comma, a quote or a
/// newline: each of the twelve is a number, a blank or an ISO date.</para>
/// <para>The line separator is a bare line feed and not <see cref="Environment.NewLine"/>, which
/// is a deliberate difference from <see cref="FrameListFormats"/>' path form: spec 12.16 names
/// <c>\n</c> so the text matches the web application's own clipboard write byte for byte.</para>
/// </remarks>
public static class AstroBinCsv
{
    /// <summary>The header line, byte for byte the web application's (spec 12.16).</summary>
    public const string Header =
        "date,filter,number,duration,binning,gain,sensorCooling,fNumber,bortle,meanSqm,meanFwhm,temperature";

    private const string Whole = "0";
    private const string TwoPlaces = "0.00";
    private const string UpToFourPlaces = "0.####";

    /// <summary>Renders the file. <paramref name="filterIds"/> is spec 5.8.1's
    /// <c>astrobin_filter_ids</c>, looked up by the canonical filter name first and by the stored
    /// name second; a filter neither lookup finds still gets its row, with an empty filter cell.
    /// <paramref name="bortle"/> is <c>astrobin_bortle</c> and renders blank when null. An empty
    /// <paramref name="rows"/> renders the header and its newline.</summary>
    public static string Build(
        IReadOnlyList<AstroBinRow> rows,
        IReadOnlyDictionary<string, int> filterIds,
        int? bortle,
        AliasMap aliases)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(filterIds);
        ArgumentNullException.ThrowIfNull(aliases);

        // The bortle cell is the same on every line, so it is formatted once.
        var bortleCell = Number(bortle, 0, Whole);
        var builder = new StringBuilder(Header);

        foreach (var row in rows)
        {
            builder
                .Append('\n')
                .Append(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(FilterId(row.FilterName, filterIds, aliases)).Append(',')
                .Append(Number(row.FrameCount, 0, Whole)).Append(',')
                .Append(Number(row.ExposureSeconds, 4, UpToFourPlaces))
                .Append(",,") // binning: always blank (no binning column in this port's schema)
                .Append(Number(row.Gain, 0, Whole)).Append(',')
                // sensorCooling: the web's Math.round sends a negative half toward positive infinity.
                .Append(Number(row.SensorTemp, 0, Whole, MidpointRounding.ToPositiveInfinity))
                .Append(",,") // fNumber: always blank (the focal ratio is not stored)
                .Append(bortleCell).Append(',')
                .Append(Number(row.SkyQuality, 2, TwoPlaces)).Append(',')
                .Append(Number(row.Fwhm, 2, TwoPlaces)).Append(',')
                .Append(Number(row.AmbientTemp, 2, TwoPlaces));
        }

        return builder.Append('\n').ToString();
    }

    /// <summary>The one numeric formatter of this file (spec 12.16): rounds a half away from zero
    /// by default, or toward positive infinity for the one cell that asks for it, then renders
    /// with the invariant culture so a decimal comma can never put a second field separator inside
    /// a value. Null renders the empty cell.</summary>
    /// <remarks>A rounded value of zero is normalised, because a small negative sensor temperature
    /// rounds to negative zero and <c>-0</c> is not a cell any reader expects.</remarks>
    private static string Number(
        double? value, int decimals, string format,
        MidpointRounding rounding = MidpointRounding.AwayFromZero)
    {
        if (value is not { } number)
        {
            return string.Empty;
        }

        var rounded = Math.Round(number, decimals, rounding);
        return (rounded == 0d ? 0d : rounded).ToString(format, CultureInfo.InvariantCulture);
    }

    // Spec 12.16: looked up by the canonical name first and the stored name second, so an alias never loses the group.
    private static string FilterId(
        string filterName, IReadOnlyDictionary<string, int> filterIds, AliasMap aliases)
    {
        var canonical = aliases.CanonicalFilter(filterName);
        if (canonical is not null && filterIds.TryGetValue(canonical, out var byCanonical))
        {
            return Number(byCanonical, 0, Whole);
        }

        return filterIds.TryGetValue(filterName, out var byStored)
            ? Number(byStored, 0, Whole)
            : string.Empty;
    }
}
