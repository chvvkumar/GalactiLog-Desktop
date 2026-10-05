using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GalactiLog.Core.Fits;
using GalactiLog.Core.Xisf;

namespace GalactiLog.Core.Metadata;

public sealed record MetadataExtractionResult(
    ExtractedMetadata Metadata, IReadOnlyList<string> Warnings);

// Port of scanner.extract_metadata (FITS) and xisf_parser.extract_xisf_metadata (XISF),
// spec 7. Both formats funnel through one shared spine, ExtractCore, once their raw
// keyword lookup is normalized: a FITS card list is looked up directly; an XISF header's
// FITSKeyword/Property values are merged into one dictionary first (spec 7.1.2's gap-fill).
// No ILogger: every place the web app logs a warning, this returns the message as data in
// MetadataExtractionResult.Warnings instead (task6.md's logging convention).
public static class MetadataExtractor
{
    private static readonly Regex HfrFilenamePattern =
        new(@"(\d+\.\d+)HFR", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (string PropertyId, string FitsKeyword)[] PropertyMap =
    [
        ("Observation:Object:Name", "OBJECT"),
        ("Instrument:ExposureTime", "EXPTIME"),
        ("Instrument:Filter:Name", "FILTER"),
        ("Instrument:Sensor:Temperature", "CCD-TEMP"),
        ("Instrument:Camera:Gain", "GAIN"),
        ("Instrument:Telescope:Name", "TELESCOP"),
        ("Instrument:Camera:Name", "INSTRUME"),
        ("Observation:Time:Start", "DATE-OBS"),
    ];

    public static MetadataExtractionResult FromFits(IReadOnlyList<FitsCard> cards, string fileName)
    {
        object? Lookup(string kw) => FitsHeaderReader.GetValue(cards, kw);
        var rawHeaders = FitsHeaderReader.BuildRawHeaders(cards);
        return ExtractCore(Lookup, rawHeaders, fileName);
    }

    public static MetadataExtractionResult FromXisf(XisfHeaderResult header, string fileName)
    {
        var merged = new Dictionary<string, string>(header.FitsKeywords);
        var imageTypeFilledFromAttribute = false;

        foreach (var (propertyId, fitsKeyword) in PropertyMap)
        {
            if (!merged.ContainsKey(fitsKeyword) && header.Properties.TryGetValue(propertyId, out var value))
            {
                merged[fitsKeyword] = value;
            }
        }
        if (!merged.ContainsKey("IMAGETYP") && header.ImageTypeAttribute is { Length: > 0 } imageType)
        {
            merged["IMAGETYP"] = imageType;
            imageTypeFilledFromAttribute = true;
        }

        var rawHeaders = XisfHeaderReader.BuildRawHeaders(header);

        object? Lookup(string kw) => merged.TryGetValue(kw, out var s) ? s : null;
        var result = ExtractCore(Lookup, rawHeaders, fileName);

        if (imageTypeFilledFromAttribute && result.Metadata.ImageType is not null)
        {
            var provenance = new Dictionary<string, string>(result.Metadata.Provenance)
            {
                ["image_type"] = "xisf:imageType",
            };
            var metadata = result.Metadata with { Provenance = provenance };
            return result with { Metadata = metadata };
        }

        return result;
    }

    // Rejects non-finite (NaN/Infinity) in both the double-card and string-parse cases, so
    // a header value like "nan" or a genuinely non-finite double card can never silently
    // reach a downstream computation (e.g. Units.ArcsecPerPixel).
    private static bool TryToDouble(object? value, out double result)
    {
        switch (value)
        {
            case long l: result = l; return true;
            case double d: result = d; return double.IsFinite(d);
            case string s: return double.TryParse(s.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
            default: result = 0; return false;
        }
    }

    private static MetadataExtractionResult ExtractCore(
        Func<string, object?> lookup, JsonObject rawHeaders, string fileName)
    {
        var provenance = new Provenance();
        var warnings = new List<string>();

        double? TryFirstDouble(string field, params string[] keywords)
        {
            foreach (var kw in keywords)
            {
                var raw = lookup(kw);
                if (raw is not null && TryToDouble(raw, out var d))
                {
                    provenance.Set(field, kw);
                    return d;
                }
            }
            return null;
        }

        // Matches Python: header.get(key) can return a non-string CLR type for an
        // unquoted card (e.g. a bare numeric OBJECT), and the web app stores whatever it
        // got. A string-typed field here stores that value's invariant-culture string
        // form instead, rather than silently dropping it.
        string? TryFirstString(string field, params string[] keywords)
        {
            foreach (var kw in keywords)
            {
                var s = lookup(kw) switch
                {
                    string str => str,
                    long l => l.ToString(CultureInfo.InvariantCulture),
                    double d => d.ToString(CultureInfo.InvariantCulture),
                    bool b => b.ToString(CultureInfo.InvariantCulture),
                    _ => null,
                };
                if (s is not null)
                {
                    provenance.Set(field, kw);
                    return s;
                }
            }
            return null;
        }

        double? TryToDoubleFromLookup(string kw) =>
            lookup(kw) is { } v && TryToDouble(v, out var d) ? d : null;

        // 1-4.
        var objectName = TryFirstString("object_name", "OBJECT");
        var exposureTime = TryFirstDouble("exposure_time", "EXPTIME", "EXPOSURE");
        var filterUsed = TryFirstString("filter_used", "FILTER");
        var sensorTemp = TryFirstDouble("sensor_temp", "CCD-TEMP");

        // 5. camera_gain: strict base-10 integer, never routed through TryToDouble.
        var gainRaw = lookup("GAIN");
        int? cameraGain = null;
        if (gainRaw is long gl)
        {
            if (gl >= int.MinValue && gl <= int.MaxValue)
            {
                cameraGain = (int)gl;
                provenance.Set("camera_gain", "GAIN");
            }
            else
            {
                warnings.Add($"camera_gain: GAIN value '{gl}' out of range in {fileName}");
            }
        }
        else if (gainRaw is string gs)
        {
            if (long.TryParse(gs.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var gl2)
                && gl2 >= int.MinValue && gl2 <= int.MaxValue)
            {
                cameraGain = (int)gl2;
                provenance.Set("camera_gain", "GAIN");
            }
            else
            {
                warnings.Add($"camera_gain: non-integer GAIN value '{gs}' in {fileName}");
            }
        }
        else if (gainRaw is double gd)
        {
            warnings.Add($"camera_gain: non-integer GAIN value '{gd.ToString(CultureInfo.InvariantCulture)}' in {fileName}");
        }

        // 6-9.
        // F4 (spec 7.5). Capture software spells IMAGETYP in whatever case it likes, and a frame
        // with no IMAGETYP at all is a light frame in practice, so the normalisation happens once
        // here, at the write side, rather than in every query. The raw value stays in raw_headers.
        // CalibrationFrames.IsCalibrationFrame keeps its own normalisation: it may be handed a raw
        // value straight from the CSV backfill, which never passes through here.
        var imageType = TryFirstString("image_type", "IMAGETYP")?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(imageType))
        {
            imageType = "LIGHT";
            provenance.Set("image_type", "default");
            warnings.Add($"image_type: no IMAGETYP in {fileName}; defaulted to LIGHT");
        }

        var telescope = TryFirstString("telescope", "TELESCOP");
        var camera = TryFirstString("camera", "INSTRUME");
        var rotatorPosition = TryFirstDouble("rotator_position", "OBJCTROT");

        // 10. median_hfr: zero-fallthrough plus filename fallback. Read first, decide the
        // winning value, then stamp provenance only on that branch - stamping "HFR" before
        // knowing whether the zero fall-through will run left a stale provenance entry
        // behind when the fallback found nothing either (fixed per review).
        var hfrHeaderValue = TryToDoubleFromLookup("HFR");
        double? medianHfr;
        if (hfrHeaderValue is { } hfr && hfr != 0.0)
        {
            medianHfr = hfr;
            provenance.Set("median_hfr", "HFR");
        }
        else
        {
            var match = HfrFilenamePattern.Match(fileName);
            // TryParse plus a finiteness test: a filename with a long enough digit run
            // ("1e999..." is not matched, but "999...9.9HFR" is) parses to PositiveInfinity
            // with double.Parse and would poison every downstream statistic.
            if (match.Success &&
                double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var filenameHfr) &&
                double.IsFinite(filenameHfr))
            {
                medianHfr = filenameHfr;
                provenance.Set("median_hfr", "filename");
            }
            else
            {
                medianHfr = null; // an HFR of exactly 0 with no filename match is absent, not zero
            }
        }

        // 11. median_fwhm: provenance-only, per 7.1.1. Never read when computing Fwhm
        // (which this task does not compute at all - it stays null until Task 7).
        var medianFwhm = TryFirstDouble("median_fwhm", "MEANFWHM", "FWHM");

        // 12. Eccentricity, per spec 7.2.
        double? eccentricity = null;
        string? eccentricitySource = null;
        var ecc = TryToDoubleFromLookup("ECCENTRICITY");
        if (ecc is { } e)
        {
            eccentricity = e;
            eccentricitySource = "header";
        }
        else
        {
            var ellip = TryToDoubleFromLookup("ELLIPTICITY");
            if (ellip is { } el)
            {
                var axisRatio = 1.0 - el;
                var v = 1.0 - axisRatio * axisRatio;
                if (v >= 0)
                {
                    eccentricity = Math.Sqrt(v);
                    eccentricitySource = "ellipticity";
                }
            }
        }
        if (eccentricity is not null) provenance.Set("eccentricity", eccentricitySource!);

        // 13.
        var altitudeDeg = TryFirstDouble("altitude_deg", "OBJCTALT", "CENTALT");

        // 14. arcsec_per_pixel: compound provenance label.
        var xpixsz = TryToDoubleFromLookup("XPIXSZ");
        var focallen = TryToDoubleFromLookup("FOCALLEN");
        var arcsecPerPixel = Units.ArcsecPerPixel(xpixsz, focallen);
        if (arcsecPerPixel is not null) provenance.Set("arcsec_per_pixel", "XPIXSZ+FOCALLEN");

        // 15. capture_date, spec 7.1.2. An empty/whitespace-only value is treated as
        // absent, matching Python's `if date_obs:` truthiness check - no warning.
        string? captureDate = null;
        if (lookup("DATE-OBS") is string dateObsRaw && !string.IsNullOrWhiteSpace(dateObsRaw))
        {
            const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
            if (DateTime.TryParse(dateObsRaw, CultureInfo.InvariantCulture, styles, out var parsed))
            {
                captureDate = parsed.Millisecond != 0
                    ? parsed.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
                    : parsed.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                provenance.Set("capture_date", "DATE-OBS");
            }
            else
            {
                warnings.Add($"Unparseable DATE-OBS '{dateObsRaw}' in {fileName}; frame will be excluded from timeline/calendar/session analytics");
            }
        }

        // 16. CSV-only fields: left null here, filled in by Task 7.
        var metadata = new ExtractedMetadata
        {
            ObjectName = objectName,
            ExposureTime = exposureTime,
            FilterUsed = filterUsed,
            SensorTemp = sensorTemp,
            CameraGain = cameraGain,
            ImageType = imageType,
            Telescope = telescope,
            Camera = camera,
            RotatorPosition = rotatorPosition,
            MedianHfr = medianHfr,
            MedianFwhm = medianFwhm,
            Eccentricity = eccentricity,
            EccentricitySource = eccentricitySource,
            AltitudeDeg = altitudeDeg,
            ArcsecPerPixel = arcsecPerPixel,
            CaptureDate = captureDate,
            RawHeaders = rawHeaders,
            Provenance = provenance.Snapshot(),
        };
        return new MetadataExtractionResult(metadata, warnings);
    }
}
