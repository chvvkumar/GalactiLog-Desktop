using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using GalactiLog.Core.Io;
using GalactiLog.Core.Text;

namespace GalactiLog.Core.Metadata;

public sealed record ImageCsvRow(
    double? MedianHfr, double? HfrStdev, double? Fwhm, double? Eccentricity,
    int? DetectedStars, double? GuidingRmsArcsec, double? GuidingRmsRaArcsec,
    double? GuidingRmsDecArcsec, double? AduStdev, double? AduMean, double? AduMedian,
    int? AduMin, int? AduMax, int? FocuserPosition, double? FocuserTemp,
    double? RotatorPosition, string? PierSide, double? Airmass, string? ExposureStartUtc);

public sealed record WeatherCsvRow(
    double? AmbientTemp, double? DewPoint, double? Humidity, double? Pressure,
    double? WindSpeed, double? WindDirection, double? WindGust, double? CloudCover,
    double? SkyQuality);

// Port of csv_metadata.py, spec 7.4. Overlays N.I.N.A. Session Metadata CSV values onto
// header-derived metadata: a real CSV value wins, a null (blank/NaN) CSV cell never erases
// a header-derived value. ApplyCsvBackfill is the only member that touches UserFiles; the
// parse/merge methods are pure and stream-based so tests use MemoryStream directly.
public sealed class NinaCsvReader
{
    private readonly ConcurrentDictionary<string, (DateTime MtimeUtc, IReadOnlyDictionary<string, ImageCsvRow> Rows)> _imageCache = new();
    private readonly ConcurrentDictionary<string, (DateTime MtimeUtc, IReadOnlyDictionary<string, WeatherCsvRow> Rows)> _weatherCache = new();

    public static IReadOnlyDictionary<string, ImageCsvRow> ParseImageCsv(Stream stream)
    {
        var result = new Dictionary<string, ImageCsvRow>();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var headerLine = reader.ReadLine();
        if (headerLine is null) return result;
        var columnIndex = BuildColumnIndex(headerLine);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var cells = CsvLine.Split(line);

            var filePath = GetCell(cells, columnIndex, "FilePath");
            if (string.IsNullOrEmpty(filePath)) continue;

            var fileName = filePath.Split('/', '\\')[^1];

            double? hfr = FloatOrNone(GetCell(cells, columnIndex, "HFR"));
            if (hfr == 0.0) hfr = null;

            var exposureStartUtc = GetCell(cells, columnIndex, "ExposureStartUTC")?.Trim();
            if (string.IsNullOrEmpty(exposureStartUtc)) exposureStartUtc = null;

            var row = new ImageCsvRow(
                MedianHfr: hfr,
                HfrStdev: FloatOrNone(GetCell(cells, columnIndex, "HFRStDev")),
                Fwhm: FloatOrNone(GetCell(cells, columnIndex, "FWHM")),
                Eccentricity: FloatOrNone(GetCell(cells, columnIndex, "Eccentricity")),
                DetectedStars: IntOrNone(GetCell(cells, columnIndex, "DetectedStars")),
                GuidingRmsArcsec: FloatOrNone(GetCell(cells, columnIndex, "GuidingRMSArcSec")),
                GuidingRmsRaArcsec: FloatOrNone(GetCell(cells, columnIndex, "GuidingRMSRAArcSec")),
                GuidingRmsDecArcsec: FloatOrNone(GetCell(cells, columnIndex, "GuidingRMSDECArcSec")),
                AduStdev: FloatOrNone(GetCell(cells, columnIndex, "ADUStDev")),
                AduMean: FloatOrNone(GetCell(cells, columnIndex, "ADUMean")),
                AduMedian: FloatOrNone(GetCell(cells, columnIndex, "ADUMedian")),
                AduMin: IntOrNone(GetCell(cells, columnIndex, "ADUMin")),
                AduMax: IntOrNone(GetCell(cells, columnIndex, "ADUMax")),
                FocuserPosition: IntOrNone(GetCell(cells, columnIndex, "FocuserPosition")),
                FocuserTemp: FloatOrNone(GetCell(cells, columnIndex, "FocuserTemp")),
                RotatorPosition: FloatOrNone(GetCell(cells, columnIndex, "RotatorPosition")),
                PierSide: StringOrNone(GetCell(cells, columnIndex, "PierSide")),
                Airmass: FloatOrNone(GetCell(cells, columnIndex, "Airmass")),
                ExposureStartUtc: exposureStartUtc);

            result[fileName] = row;
        }

        return result;
    }

    public static IReadOnlyDictionary<string, WeatherCsvRow> ParseWeatherCsv(Stream stream)
    {
        var result = new Dictionary<string, WeatherCsvRow>();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var headerLine = reader.ReadLine();
        if (headerLine is null) return result;
        var columnIndex = BuildColumnIndex(headerLine);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var cells = CsvLine.Split(line);

            var exposureStart = GetCell(cells, columnIndex, "ExposureStartUTC");
            if (string.IsNullOrEmpty(exposureStart)) continue;
            var key = exposureStart.Trim();

            var row = new WeatherCsvRow(
                AmbientTemp: FloatOrNone(GetCell(cells, columnIndex, "Temperature")),
                DewPoint: FloatOrNone(GetCell(cells, columnIndex, "DewPoint")),
                Humidity: FloatOrNone(GetCell(cells, columnIndex, "Humidity")),
                Pressure: FloatOrNone(GetCell(cells, columnIndex, "Pressure")),
                WindSpeed: FloatOrNone(GetCell(cells, columnIndex, "WindSpeed")),
                WindDirection: FloatOrNone(GetCell(cells, columnIndex, "WindDirection")),
                WindGust: FloatOrNone(GetCell(cells, columnIndex, "WindGust")),
                CloudCover: FloatOrNone(GetCell(cells, columnIndex, "CloudCover")),
                SkyQuality: FloatOrNone(GetCell(cells, columnIndex, "SkyQuality")));

            result[key] = row;
        }

        return result;
    }

    public static MetadataExtractionResult Merge(
        MetadataExtractionResult extracted, ImageCsvRow? imageRow, WeatherCsvRow? weatherRow)
    {
        if (imageRow is null) return extracted;

        var m = extracted.Metadata;
        var provenance = new Dictionary<string, string>(m.Provenance);

        double? medianHfr = MergeField(m.MedianHfr, imageRow.MedianHfr, provenance, "median_hfr", "HFR");
        double? hfrStdev = MergeField(m.HfrStdev, imageRow.HfrStdev, provenance, "hfr_stdev", "HFRStDev");
        double? fwhm = MergeField(m.Fwhm, imageRow.Fwhm, provenance, "fwhm", "FWHM");
        int? detectedStars = MergeField(m.DetectedStars, imageRow.DetectedStars, provenance, "detected_stars", "DetectedStars");
        double? guidingRmsArcsec = MergeField(m.GuidingRmsArcsec, imageRow.GuidingRmsArcsec, provenance, "guiding_rms_arcsec", "GuidingRMSArcSec");
        double? guidingRmsRaArcsec = MergeField(m.GuidingRmsRaArcsec, imageRow.GuidingRmsRaArcsec, provenance, "guiding_rms_ra_arcsec", "GuidingRMSRAArcSec");
        double? guidingRmsDecArcsec = MergeField(m.GuidingRmsDecArcsec, imageRow.GuidingRmsDecArcsec, provenance, "guiding_rms_dec_arcsec", "GuidingRMSDECArcSec");
        double? aduStdev = MergeField(m.AduStdev, imageRow.AduStdev, provenance, "adu_stdev", "ADUStDev");
        double? aduMean = MergeField(m.AduMean, imageRow.AduMean, provenance, "adu_mean", "ADUMean");
        double? aduMedian = MergeField(m.AduMedian, imageRow.AduMedian, provenance, "adu_median", "ADUMedian");
        int? aduMin = MergeField(m.AduMin, imageRow.AduMin, provenance, "adu_min", "ADUMin");
        int? aduMax = MergeField(m.AduMax, imageRow.AduMax, provenance, "adu_max", "ADUMax");
        int? focuserPosition = MergeField(m.FocuserPosition, imageRow.FocuserPosition, provenance, "focuser_position", "FocuserPosition");
        double? focuserTemp = MergeField(m.FocuserTemp, imageRow.FocuserTemp, provenance, "focuser_temp", "FocuserTemp");
        double? rotatorPosition = MergeField(m.RotatorPosition, imageRow.RotatorPosition, provenance, "rotator_position", "RotatorPosition");
        string? pierSide = MergeField(m.PierSide, imageRow.PierSide, provenance, "pier_side", "PierSide");
        double? airmass = MergeField(m.Airmass, imageRow.Airmass, provenance, "airmass", "Airmass");

        double? ambientTemp = MergeField(m.AmbientTemp, weatherRow?.AmbientTemp, provenance, "ambient_temp", "Temperature");
        double? dewPoint = MergeField(m.DewPoint, weatherRow?.DewPoint, provenance, "dew_point", "DewPoint");
        double? humidity = MergeField(m.Humidity, weatherRow?.Humidity, provenance, "humidity", "Humidity");
        double? pressure = MergeField(m.Pressure, weatherRow?.Pressure, provenance, "pressure", "Pressure");
        double? windSpeed = MergeField(m.WindSpeed, weatherRow?.WindSpeed, provenance, "wind_speed", "WindSpeed");
        double? windDirection = MergeField(m.WindDirection, weatherRow?.WindDirection, provenance, "wind_direction", "WindDirection");
        double? windGust = MergeField(m.WindGust, weatherRow?.WindGust, provenance, "wind_gust", "WindGust");
        double? cloudCover = MergeField(m.CloudCover, weatherRow?.CloudCover, provenance, "cloud_cover", "CloudCover");
        double? skyQuality = MergeField(m.SkyQuality, weatherRow?.SkyQuality, provenance, "sky_quality", "SkyQuality");

        // Eccentricity: bare "csv" source label, not "csv:Eccentricity".
        double? eccentricity = m.Eccentricity;
        string? eccentricitySource = m.EccentricitySource;
        if (imageRow.Eccentricity is { } eccValue)
        {
            eccentricity = eccValue;
            eccentricitySource = "csv";
            provenance["eccentricity"] = "csv";
        }

        // Guiding RMS source stamp: applied once for the whole triple, after the three
        // individual fields above have already been merged.
        string? guidingRmsSource = m.GuidingRmsSource;
        if (imageRow.GuidingRmsArcsec is not null
            || imageRow.GuidingRmsRaArcsec is not null
            || imageRow.GuidingRmsDecArcsec is not null)
        {
            guidingRmsSource = "csv";
        }

        var metadata = m with
        {
            MedianHfr = medianHfr,
            HfrStdev = hfrStdev,
            Fwhm = fwhm,
            Eccentricity = eccentricity,
            EccentricitySource = eccentricitySource,
            DetectedStars = detectedStars,
            GuidingRmsArcsec = guidingRmsArcsec,
            GuidingRmsRaArcsec = guidingRmsRaArcsec,
            GuidingRmsDecArcsec = guidingRmsDecArcsec,
            GuidingRmsSource = guidingRmsSource,
            AduStdev = aduStdev,
            AduMean = aduMean,
            AduMedian = aduMedian,
            AduMin = aduMin,
            AduMax = aduMax,
            FocuserPosition = focuserPosition,
            FocuserTemp = focuserTemp,
            RotatorPosition = rotatorPosition,
            PierSide = pierSide,
            Airmass = airmass,
            AmbientTemp = ambientTemp,
            DewPoint = dewPoint,
            Humidity = humidity,
            Pressure = pressure,
            WindSpeed = windSpeed,
            WindDirection = windDirection,
            WindGust = windGust,
            CloudCover = cloudCover,
            SkyQuality = skyQuality,
            Provenance = provenance,
        };

        return extracted with { Metadata = metadata };
    }

    public MetadataExtractionResult ApplyCsvBackfill(MetadataExtractionResult extracted, string framePath)
    {
        var directory = Path.GetDirectoryName(framePath) ?? "";
        var fileName = Path.GetFileName(framePath);

        var imageRows = GetCachedRows(_imageCache, directory, "ImageMetaData.csv", ParseImageCsv);
        if (imageRows is null || !imageRows.TryGetValue(fileName, out var imageRow))
        {
            return extracted;
        }

        WeatherCsvRow? weatherRow = null;
        if (!string.IsNullOrEmpty(imageRow.ExposureStartUtc))
        {
            var weatherRows = GetCachedRows(_weatherCache, directory, "WeatherData.csv", ParseWeatherCsv);
            weatherRows?.TryGetValue(imageRow.ExposureStartUtc, out weatherRow);
        }

        return Merge(extracted, imageRow, weatherRow);
    }

    private static IReadOnlyDictionary<string, T>? GetCachedRows<T>(
        ConcurrentDictionary<string, (DateTime MtimeUtc, IReadOnlyDictionary<string, T> Rows)> cache,
        string directory, string csvFileName, Func<Stream, IReadOnlyDictionary<string, T>> parse)
    {
        var path = Path.Combine(directory, csvFileName);
        if (!UserFiles.Exists(path))
        {
            cache.TryRemove(path, out _);
            return null;
        }

        // Exists said yes, but the CSV can be deleted, renamed or exclusively locked between
        // that check and the open. Backfill is best-effort (spec 7.4), so a lost race yields
        // no rows rather than an exception escaping ApplyCsvBackfill.
        try
        {
            var mtimeUtc = UserFiles.GetFileInfo(path).LastWriteTimeUtc;
            if (cache.TryGetValue(path, out var entry) && entry.MtimeUtc == mtimeUtc)
            {
                return entry.Rows;
            }

            using var stream = UserFiles.OpenRead(path);
            var parsed = parse(stream);
            cache[path] = (mtimeUtc, parsed);
            return parsed;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // General merge rule (spec 7.4): a null CSV value never erases an existing value,
    // regardless of what that existing value is; a non-null CSV value always wins and
    // stamps provenance as "csv:<CsvColumnName>".
    private static T? MergeField<T>(
        T? existing, T? csvValue, Dictionary<string, string> provenance, string field, string csvColumnName)
    {
        if (csvValue is null) return existing;
        provenance[field] = $"csv:{csvColumnName}";
        return csvValue;
    }

    private static Dictionary<string, int> BuildColumnIndex(string headerLine)
    {
        var columns = CsvLine.Split(headerLine);
        var index = new Dictionary<string, int>();
        for (var i = 0; i < columns.Count; i++)
        {
            index[columns[i]] = i;
        }
        return index;
    }

    private static string? GetCell(IReadOnlyList<string> cells, Dictionary<string, int> columnIndex, string columnName)
    {
        if (!columnIndex.TryGetValue(columnName, out var i) || i >= cells.Count) return null;
        return cells[i];
    }

    private static double? FloatOrNone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return null;
        // Infinity and overflowing literals like 1e400 parse successfully and are not NaN,
        // so the test is finiteness, not NaN alone.
        return double.IsFinite(f) ? f : null;
    }

    // Range-checked before the cast: an unchecked (int) of an out-of-range double yields
    // int.MinValue on x64 rather than failing.
    private static int? IntOrNone(string? value)
        => FloatOrNone(value) is { } f && f >= int.MinValue && f <= int.MaxValue ? (int)f : null;

    private static string? StringOrNone(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed == "NaN" ? null : trimmed;
    }
}
