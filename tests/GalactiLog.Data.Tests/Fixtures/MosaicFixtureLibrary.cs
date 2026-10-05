using System.Globalization;
using GalactiLog.Core.Tests.Fixtures;

namespace GalactiLog.Data.Tests.Fixtures;

/// <summary>
/// The Phase 18 mosaic fixture library (plan "Fixtures", spec 7.7, fixture policy 18.2): a folder
/// of generated FITS lights, one subfolder per OBJECT, written at test time into a temp root.
/// Nothing is random and nothing is committed: every header and pixel is computed from the
/// constants below.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>"NGC 7000 Panel 1" to "Panel 4" on a 2 by 2 grid one field apart, two nights each in two
/// filters; Panel 4 holds half the integration of the others, and Panels 1 and 2 share
/// <see cref="SharedNight"/>.</item>
/// <item>"IC 1396 P1" alone, one night.</item>
/// <item>"Sh2-155 Panel 1" and "Sh2-155 Panel 2" at one identical position.</item>
/// <item>"North America Nebula", one night, no panel token.</item>
/// </list>
/// Every frame is 64 by 64 pixels carrying a gradient that rises along both axes at different
/// rates, so a rotation or a flip of the rendered tile is visible (Phase 19A).
/// </remarks>
public static class MosaicFixtureLibrary
{
    public const int WidthPx = 64;
    public const double PixelSizeUm = 4.0;
    public const double FocalLengthMm = 55.0;
    public const double ExposureSeconds = 300;

    /// <summary>206.265 * 4.0 / 55: about 15.0 arcseconds per pixel.</summary>
    public static readonly double ArcsecPerPixel = 206.265 * PixelSizeUm / FocalLengthMm;

    /// <summary>About 16.0 arcminutes. The automatic tolerance is a quarter of it, about 4.0
    /// arcminutes, so the NGC 7000 grid (one field apart) is distinct and the Sh2-155 pair (zero
    /// apart) is not.</summary>
    public static readonly double FieldArcmin = WidthPx * ArcsecPerPixel / 60;

    public const double NgcRa = 314.75;
    public const double NgcDec = 44.3;

    /// <summary>Frames per filter per night of NGC 7000 Panels 1 to 3; Panel 4 takes one.</summary>
    public const int FullFramesPerFilterNight = 2;

    public static readonly IReadOnlyList<string> Filters = ["Ha", "OIII"];

    /// <summary>The night Panels 1 and 2 both shot.</summary>
    public static readonly DateOnly SharedNight = new(2025, 9, 2);

    /// <summary>Integration of a full NGC 7000 panel: 2 nights, 2 filters, 2 frames of 300 s.</summary>
    public static readonly double FullPanelSeconds = 2 * 2 * FullFramesPerFilterNight * ExposureSeconds;

    /// <summary>One frame per filter per night: half of <see cref="FullPanelSeconds"/>.</summary>
    public static readonly double ShortPanelSeconds = FullPanelSeconds / 2;

    /// <summary>One OBJECT's header set and the <c>panel_label</c> the default keywords give it
    /// (spec 7.7's token table), written here by hand so the test does not ask the code under test.</summary>
    public sealed record Set(string ObjectName, string? ExpectedLabel, double Ra, double Dec, IReadOnlyList<DateOnly> Nights, int FramesPerFilterNight);

    /// <summary>Every header set the library writes, in write order.</summary>
    public static IReadOnlyList<Set> Sets { get; } = BuildSets();

    public static int FrameCount => Sets.Sum(set => set.Nights.Count * Filters.Count * set.FramesPerFilterNight);

    private static List<Set> BuildSets()
    {
        var step = FieldArcmin / 60;
        var stepRa = step / Math.Cos(NgcDec * Math.PI / 180);
        static DateOnly Day(int day) => new(2025, 9, day);
        return
        [
            new("NGC 7000 Panel 1", "Panel 1", NgcRa, NgcDec, [Day(1), SharedNight], FullFramesPerFilterNight),
            new("NGC 7000 Panel 2", "Panel 2", NgcRa + stepRa, NgcDec, [SharedNight, Day(3)], FullFramesPerFilterNight),
            new("NGC 7000 Panel 3", "Panel 3", NgcRa, NgcDec + step, [Day(4), Day(5)], FullFramesPerFilterNight),
            new("NGC 7000 Panel 4", "Panel 4", NgcRa + stepRa, NgcDec + step, [Day(6), Day(7)], 1),
            new("IC 1396 P1", "Panel 1", 324.74, 57.49, [Day(8)], FullFramesPerFilterNight),
            new("Sh2-155 Panel 1", "Panel 1", 343.99, 62.62, [Day(9)], FullFramesPerFilterNight),
            new("Sh2-155 Panel 2", "Panel 2", 343.99, 62.62, [Day(10)], FullFramesPerFilterNight),
            new("North America Nebula", null, NgcRa, NgcDec, [Day(11)], FullFramesPerFilterNight),
        ];
    }

    /// <summary>Writes the library under <paramref name="root"/> and returns every file path.
    /// Frames start at 22:00 UTC ten minutes apart, so at longitude 0 each one's imaging night is
    /// its calendar date (spec 8.2).</summary>
    public static IReadOnlyList<string> Write(string root)
    {
        var pixels = Gradient();
        var paths = new List<string>();
        foreach (var set in Sets)
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, set.ObjectName)).FullName;
            foreach (var night in set.Nights)
            {
                var start = night.ToDateTime(new TimeOnly(22, 0));
                var index = 0;
                foreach (var filter in Filters)
                {
                    for (var i = 0; i < set.FramesPerFilterNight; i++, index++)
                    {
                        var path = Path.Combine(folder, $"{night:yyyyMMdd}_{filter}_{i + 1:D3}.fits");
                        File.WriteAllBytes(path, Frame(set, filter, start.AddMinutes(10 * index), pixels));
                        paths.Add(path);
                    }
                }
            }
        }
        return paths;
    }

    private static byte[] Frame(Set set, string filter, DateTime captured, float[,] pixels)
        => new FitsBuilder()
            .Card("SIMPLE", true)
            .Card("BITPIX", (long)16)
            .Card("NAXIS", (long)2)
            .Card("NAXIS1", (long)WidthPx)
            .Card("NAXIS2", (long)WidthPx)
            .Card("IMAGETYP", "LIGHT")
            .Card("OBJECT", set.ObjectName)
            .Card("RA", Math.Round(set.Ra, 6))
            .Card("DEC", Math.Round(set.Dec, 6))
            .Card("FOCALLEN", FocalLengthMm)
            .Card("XPIXSZ", PixelSizeUm)
            .Card("FILTER", filter)
            .Card("EXPOSURE", ExposureSeconds)
            .Card("DATE-OBS", captured.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture))
            .Pixels(16, WidthPx, WidthPx, pixels)
            .EndCard()
            .Build().ToArray();

    // Rises 40 per column and 10 per row: the brightest corner is bottom right, and a 90 degree
    // turn or a flip moves it.
    private static float[,] Gradient()
    {
        var values = new float[WidthPx, WidthPx];
        for (var row = 0; row < WidthPx; row++)
        {
            for (var col = 0; col < WidthPx; col++)
            {
                values[row, col] = 1000 + 40 * col + 10 * row;
            }
        }
        return values;
    }
}
