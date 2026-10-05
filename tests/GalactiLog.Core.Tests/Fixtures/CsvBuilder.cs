using System.Text;

namespace GalactiLog.Core.Tests.Fixtures;

// Synthetic NINA CSV fixture generator (spec 7.4, fixture policy 18.2). Writes
// ImageMetaData.csv- and WeatherData.csv-shaped bytes into a MemoryStream, never to disk.
//
// Row dictionaries supply raw cell text exactly as it should appear - this builder does not
// interpret or convert values (that is the reader's job, Task 6/7). A missing key writes an
// empty cell, which is how a test creates a "gap".
public static class CsvBuilder
{
    private static readonly string[] ImageColumns =
    {
        "FilePath", "ExposureStartUTC", "HFR", "HFRStDev", "FWHM", "Eccentricity",
        "DetectedStars", "GuidingRMSArcSec", "GuidingRMSRAArcSec", "GuidingRMSDECArcSec",
        "ADUStDev", "ADUMean", "ADUMedian", "ADUMin", "ADUMax", "FocuserPosition",
        "FocuserTemp", "RotatorPosition", "PierSide", "Airmass",
    };

    private static readonly string[] WeatherColumns =
    {
        "ExposureStartUTC", "Temperature", "DewPoint", "Humidity", "Pressure", "WindSpeed",
        "WindDirection", "WindGust", "CloudCover", "SkyQuality",
    };

    public static MemoryStream BuildImageMetaDataCsv(IReadOnlyList<IReadOnlyDictionary<string, string?>> rows, bool withBom = true)
        => Build(ImageColumns, rows, withBom);

    public static MemoryStream BuildWeatherDataCsv(IReadOnlyList<IReadOnlyDictionary<string, string?>> rows, bool withBom = true)
        => Build(WeatherColumns, rows, withBom);

    private static MemoryStream Build(string[] columns, IReadOnlyList<IReadOnlyDictionary<string, string?>> rows, bool withBom)
    {
        var text = new StringBuilder();
        text.Append(string.Join(",", columns)).Append('\n');

        foreach (var row in rows)
        {
            var cells = new string[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                row.TryGetValue(columns[i], out var value);
                cells[i] = EscapeCell(value ?? string.Empty);
            }
            text.Append(string.Join(",", cells)).Append('\n');
        }

        var stream = new MemoryStream();
        if (withBom)
        {
            stream.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
        }
        var textBytes = Encoding.UTF8.GetBytes(text.ToString());
        stream.Write(textBytes, 0, textBytes.Length);
        stream.Position = 0;
        return stream;
    }

    // RFC 4180 minimal escaping: quote a cell only when it contains a comma, quote, or
    // newline, doubling any embedded quote.
    private static string EscapeCell(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n' }) < 0)
        {
            return value;
        }
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
