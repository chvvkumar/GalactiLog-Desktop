using GalactiLog.Core.Metadata;
using GalactiLog.Core.Tests.Fixtures;
using Xunit;

namespace GalactiLog.Core.Tests.Metadata;

public class NinaCsvReaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static MetadataExtractionResult Extracted(ExtractedMetadata metadata)
        => new(metadata, Array.Empty<string>());

    // --- ParseImageCsv --------------------------------------------------

    [Fact]
    public void ParseImageCsv_BackslashFilePath_KeyedByLastSegment()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            new Dictionary<string, string?> { ["FilePath"] = @"D:\Astro\Lights\frame_001.fits", ["HFR"] = "2.5" },
        };
        using var stream = CsvBuilder.BuildImageMetaDataCsv(rows);

        var result = NinaCsvReader.ParseImageCsv(stream);

        Assert.True(result.ContainsKey("frame_001.fits"));
        Assert.Equal(2.5, result["frame_001.fits"].MedianHfr);
    }

    [Fact]
    public void ParseImageCsv_ForwardSlashFilePath_KeyedByLastSegment()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            new Dictionary<string, string?> { ["FilePath"] = "/mnt/astro/lights/frame_002.fits", ["HFR"] = "1.1" },
        };
        using var stream = CsvBuilder.BuildImageMetaDataCsv(rows);

        var result = NinaCsvReader.ParseImageCsv(stream);

        Assert.True(result.ContainsKey("frame_002.fits"));
    }

    [Fact]
    public void ParseImageCsv_HfrZero_ConvertsToNull()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            new Dictionary<string, string?> { ["FilePath"] = "frame.fits", ["HFR"] = "0" },
        };
        using var stream = CsvBuilder.BuildImageMetaDataCsv(rows);

        var result = NinaCsvReader.ParseImageCsv(stream);

        Assert.Null(result["frame.fits"].MedianHfr);
    }

    [Fact]
    public void ParseImageCsv_HfrNonZero_ConvertsNormally()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            new Dictionary<string, string?> { ["FilePath"] = "frame.fits", ["HFR"] = "3.25" },
        };
        using var stream = CsvBuilder.BuildImageMetaDataCsv(rows);

        var result = NinaCsvReader.ParseImageCsv(stream);

        Assert.Equal(3.25, result["frame.fits"].MedianHfr);
    }

    [Fact]
    public void ParseImageCsv_BlankAndNaNFloatCells_ConvertToNull()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            new Dictionary<string, string?> { ["FilePath"] = "a.fits", ["FWHM"] = "" },
            new Dictionary<string, string?> { ["FilePath"] = "b.fits", ["FWHM"] = "NaN" },
        };
        using var stream = CsvBuilder.BuildImageMetaDataCsv(rows);

        var result = NinaCsvReader.ParseImageCsv(stream);

        Assert.Null(result["a.fits"].Fwhm);
        Assert.Null(result["b.fits"].Fwhm);
    }

    [Fact]
    public void ParseImageCsv_BlankPierSide_ConvertsToNull()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            new Dictionary<string, string?> { ["FilePath"] = "a.fits", ["PierSide"] = "" },
        };
        using var stream = CsvBuilder.BuildImageMetaDataCsv(rows);

        var result = NinaCsvReader.ParseImageCsv(stream);

        Assert.Null(result["a.fits"].PierSide);
    }

    [Fact]
    public void ParseImageCsv_RowMissingFilePath_IsSkippedAndDoesNotThrow()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            new Dictionary<string, string?> { ["FilePath"] = "", ["HFR"] = "2.0" },
            new Dictionary<string, string?> { ["FilePath"] = "keep.fits", ["HFR"] = "2.0" },
        };
        using var stream = CsvBuilder.BuildImageMetaDataCsv(rows);

        var result = NinaCsvReader.ParseImageCsv(stream);

        Assert.Single(result);
        Assert.True(result.ContainsKey("keep.fits"));
    }

    // --- ParseWeatherCsv -------------------------------------------------

    [Fact]
    public void ParseWeatherCsv_RowKeyedByExposureStartUtc_RoundTripsColumns()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            new Dictionary<string, string?>
            {
                ["ExposureStartUTC"] = "2025-01-01T00:00:00Z",
                ["Temperature"] = "-5.5",
                ["DewPoint"] = "-10.0",
                ["Humidity"] = "80",
                ["Pressure"] = "1013.2",
                ["WindSpeed"] = "3.1",
                ["WindDirection"] = "270",
                ["WindGust"] = "5.5",
                ["CloudCover"] = "10",
                ["SkyQuality"] = "21.3",
            },
        };
        using var stream = CsvBuilder.BuildWeatherDataCsv(rows);

        var result = NinaCsvReader.ParseWeatherCsv(stream);

        var row = result["2025-01-01T00:00:00Z"];
        Assert.Equal(-5.5, row.AmbientTemp);
        Assert.Equal(-10.0, row.DewPoint);
        Assert.Equal(80, row.Humidity);
        Assert.Equal(1013.2, row.Pressure);
        Assert.Equal(3.1, row.WindSpeed);
        Assert.Equal(270, row.WindDirection);
        Assert.Equal(5.5, row.WindGust);
        Assert.Equal(10, row.CloudCover);
        Assert.Equal(21.3, row.SkyQuality);
    }

    [Fact]
    public void ParseWeatherCsv_BlankExposureStartUtc_IsSkipped()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            new Dictionary<string, string?> { ["ExposureStartUTC"] = "", ["Temperature"] = "5" },
        };
        using var stream = CsvBuilder.BuildWeatherDataCsv(rows);

        var result = NinaCsvReader.ParseWeatherCsv(stream);

        Assert.Empty(result);
    }

    // --- Merge -------------------------------------------------------------

    private static readonly ImageCsvRow EmptyImageRow = new(
        MedianHfr: null, HfrStdev: null, Fwhm: null, Eccentricity: null, DetectedStars: null,
        GuidingRmsArcsec: null, GuidingRmsRaArcsec: null, GuidingRmsDecArcsec: null,
        AduStdev: null, AduMean: null, AduMedian: null, AduMin: null, AduMax: null,
        FocuserPosition: null, FocuserTemp: null, RotatorPosition: null, PierSide: null,
        Airmass: null, ExposureStartUtc: null);

    [Fact]
    public void Merge_BlankCsvHfr_DoesNotEraseHeaderValue_Spec18_1Case()
    {
        var metadata = new ExtractedMetadata
        {
            MedianHfr = 2.0,
            Provenance = new Dictionary<string, string> { ["median_hfr"] = "HFR" },
        };
        var extracted = Extracted(metadata);
        var imageRow = EmptyImageRow with { MedianHfr = null };

        var result = NinaCsvReader.Merge(extracted, imageRow, null);

        Assert.Equal(2.0, result.Metadata.MedianHfr);
        Assert.Equal("HFR", result.Metadata.Provenance["median_hfr"]);
    }

    [Fact]
    public void Merge_RealCsvHfr_OverwritesHeaderValue_WithCsvProvenance()
    {
        var metadata = new ExtractedMetadata { MedianHfr = 2.0 };
        var extracted = Extracted(metadata);
        var imageRow = EmptyImageRow with { MedianHfr = 3.5 };

        var result = NinaCsvReader.Merge(extracted, imageRow, null);

        Assert.Equal(3.5, result.Metadata.MedianHfr);
        Assert.Equal("csv:HFR", result.Metadata.Provenance["median_hfr"]);
    }

    [Fact]
    public void Merge_Eccentricity_FromCsv_OverwritesHeaderAndUsesBareCsvLabel()
    {
        var metadata = new ExtractedMetadata
        {
            Eccentricity = 0.2,
            EccentricitySource = "header",
            Provenance = new Dictionary<string, string> { ["eccentricity"] = "header" },
        };
        var extracted = Extracted(metadata);
        var imageRow = EmptyImageRow with { Eccentricity = 0.9 };

        var result = NinaCsvReader.Merge(extracted, imageRow, null);

        Assert.Equal(0.9, result.Metadata.Eccentricity);
        Assert.Equal("csv", result.Metadata.EccentricitySource);
        Assert.Equal("csv", result.Metadata.Provenance["eccentricity"]);
    }

    [Fact]
    public void Merge_OnlyGuidingRmsArcsecSet_StampsGuidingRmsSourceForWholeTriple()
    {
        var extracted = Extracted(new ExtractedMetadata());
        var imageRow = EmptyImageRow with { GuidingRmsArcsec = 1.2 };

        var result = NinaCsvReader.Merge(extracted, imageRow, null);

        Assert.Equal("csv", result.Metadata.GuidingRmsSource);
        Assert.Equal(1.2, result.Metadata.GuidingRmsArcsec);
        Assert.Null(result.Metadata.GuidingRmsRaArcsec);
        Assert.Null(result.Metadata.GuidingRmsDecArcsec);
    }

    [Fact]
    public void Merge_FullyPopulatedRows_RoundTripsEveryCsvOnlyFieldWithCorrectProvenance()
    {
        var imageRow = new ImageCsvRow(
            MedianHfr: 2.0, HfrStdev: 0.1, Fwhm: 3.3, Eccentricity: 0.5, DetectedStars: 42,
            GuidingRmsArcsec: 0.5, GuidingRmsRaArcsec: 0.3, GuidingRmsDecArcsec: 0.4,
            AduStdev: 10.0, AduMean: 500.0, AduMedian: 501.0, AduMin: 100, AduMax: 60000,
            FocuserPosition: 12345, FocuserTemp: 15.5, RotatorPosition: 90.0,
            PierSide: "EAST", Airmass: 1.2, ExposureStartUtc: "2025-01-01T00:00:00Z");
        var weatherRow = new WeatherCsvRow(
            AmbientTemp: -1.0, DewPoint: -5.0, Humidity: 70.0, Pressure: 1012.0,
            WindSpeed: 2.0, WindDirection: 180.0, WindGust: 4.0, CloudCover: 20.0, SkyQuality: 21.0);

        var extracted = Extracted(new ExtractedMetadata());
        var result = NinaCsvReader.Merge(extracted, imageRow, weatherRow);
        var m = result.Metadata;
        var p = m.Provenance;

        Assert.Equal(0.1, m.HfrStdev); Assert.Equal("csv:HFRStDev", p["hfr_stdev"]);
        Assert.Equal(3.3, m.Fwhm); Assert.Equal("csv:FWHM", p["fwhm"]);
        Assert.Equal(42, m.DetectedStars); Assert.Equal("csv:DetectedStars", p["detected_stars"]);
        Assert.Equal(0.5, m.GuidingRmsArcsec); Assert.Equal("csv:GuidingRMSArcSec", p["guiding_rms_arcsec"]);
        Assert.Equal(0.3, m.GuidingRmsRaArcsec); Assert.Equal("csv:GuidingRMSRAArcSec", p["guiding_rms_ra_arcsec"]);
        Assert.Equal(0.4, m.GuidingRmsDecArcsec); Assert.Equal("csv:GuidingRMSDECArcSec", p["guiding_rms_dec_arcsec"]);
        Assert.Equal(10.0, m.AduStdev); Assert.Equal("csv:ADUStDev", p["adu_stdev"]);
        Assert.Equal(500.0, m.AduMean); Assert.Equal("csv:ADUMean", p["adu_mean"]);
        Assert.Equal(501.0, m.AduMedian); Assert.Equal("csv:ADUMedian", p["adu_median"]);
        Assert.Equal(100, m.AduMin); Assert.Equal("csv:ADUMin", p["adu_min"]);
        Assert.Equal(60000, m.AduMax); Assert.Equal("csv:ADUMax", p["adu_max"]);
        Assert.Equal(12345, m.FocuserPosition); Assert.Equal("csv:FocuserPosition", p["focuser_position"]);
        Assert.Equal(15.5, m.FocuserTemp); Assert.Equal("csv:FocuserTemp", p["focuser_temp"]);
        Assert.Equal("EAST", m.PierSide); Assert.Equal("csv:PierSide", p["pier_side"]);
        Assert.Equal(1.2, m.Airmass); Assert.Equal("csv:Airmass", p["airmass"]);

        Assert.Equal(-1.0, m.AmbientTemp); Assert.Equal("csv:Temperature", p["ambient_temp"]);
        Assert.Equal(-5.0, m.DewPoint); Assert.Equal("csv:DewPoint", p["dew_point"]);
        Assert.Equal(70.0, m.Humidity); Assert.Equal("csv:Humidity", p["humidity"]);
        Assert.Equal(1012.0, m.Pressure); Assert.Equal("csv:Pressure", p["pressure"]);
        Assert.Equal(2.0, m.WindSpeed); Assert.Equal("csv:WindSpeed", p["wind_speed"]);
        Assert.Equal(180.0, m.WindDirection); Assert.Equal("csv:WindDirection", p["wind_direction"]);
        Assert.Equal(4.0, m.WindGust); Assert.Equal("csv:WindGust", p["wind_gust"]);
        Assert.Equal(20.0, m.CloudCover); Assert.Equal("csv:CloudCover", p["cloud_cover"]);
        Assert.Equal(21.0, m.SkyQuality); Assert.Equal("csv:SkyQuality", p["sky_quality"]);
    }

    [Fact]
    public void Merge_NullImageRow_ReturnsInputUnchanged()
    {
        var extracted = Extracted(new ExtractedMetadata { MedianHfr = 5.0 });

        var result = NinaCsvReader.Merge(extracted, null, null);

        Assert.Same(extracted, result);
    }

    // --- ApplyCsvBackfill ----------------------------------------------

    [Fact]
    public void ApplyCsvBackfill_CachesParseAndPicksUpChangeAfterMtimeUpdate()
    {
        Directory.CreateDirectory(_tempDir);
        var framePath = Path.Combine(_tempDir, "frame_001.fits");
        var csvPath = Path.Combine(_tempDir, "ImageMetaData.csv");

        File.WriteAllText(csvPath, "FilePath,HFR\r\nframe_001.fits,2.0\r\n");
        var reader = new NinaCsvReader();
        var extracted = Extracted(new ExtractedMetadata());

        var first = reader.ApplyCsvBackfill(extracted, framePath);
        Assert.Equal(2.0, first.Metadata.MedianHfr);

        // Same content, same mtime semantics: second call must not blow up and should
        // still reflect cached data.
        var second = reader.ApplyCsvBackfill(extracted, framePath);
        Assert.Equal(2.0, second.Metadata.MedianHfr);

        // Rewrite with new content and force a distinct, later mtime so the cache's
        // mtime check must observe a change to pick it up.
        File.WriteAllText(csvPath, "FilePath,HFR\r\nframe_001.fits,9.0\r\n");
        File.SetLastWriteTimeUtc(csvPath, DateTime.UtcNow.AddSeconds(5));

        var third = reader.ApplyCsvBackfill(extracted, framePath);
        Assert.Equal(9.0, third.Metadata.MedianHfr);
    }

    [Fact]
    public void ApplyCsvBackfill_NoImageMetaDataCsv_ReturnsInputUnchangedAndDoesNotThrow()
    {
        Directory.CreateDirectory(_tempDir);
        var framePath = Path.Combine(_tempDir, "frame_001.fits");
        var reader = new NinaCsvReader();
        var extracted = Extracted(new ExtractedMetadata { MedianHfr = 1.0 });

        var result = reader.ApplyCsvBackfill(extracted, framePath);

        Assert.Same(extracted, result);
    }

    [Fact]
    public void ApplyCsvBackfill_JoinsWeatherByExposureStartUtc()
    {
        Directory.CreateDirectory(_tempDir);
        var framePath = Path.Combine(_tempDir, "frame_001.fits");
        var csvPath = Path.Combine(_tempDir, "ImageMetaData.csv");
        var weatherPath = Path.Combine(_tempDir, "WeatherData.csv");

        File.WriteAllText(csvPath, "FilePath,ExposureStartUTC,HFR\r\nframe_001.fits,2025-01-01T00:00:00Z,2.0\r\n");
        File.WriteAllText(weatherPath, "ExposureStartUTC,Temperature\r\n2025-01-01T00:00:00Z,-3.5\r\n");

        var reader = new NinaCsvReader();
        var extracted = Extracted(new ExtractedMetadata());

        var result = reader.ApplyCsvBackfill(extracted, framePath);

        Assert.Equal(-3.5, result.Metadata.AmbientTemp);
    }

    // Review item 11: FloatOrNone rejected NaN only, so Infinity and overflowing literals
    // passed straight through into stored metadata.
    [Theory]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e400")]
    [InlineData("NaN")]
    public void ParseImageCsv_NonFiniteFloat_ConvertsToNull(string cell)
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            new Dictionary<string, string?> { ["FilePath"] = "frame.fits", ["FWHM"] = cell },
        };
        using var stream = CsvBuilder.BuildImageMetaDataCsv(rows);

        var result = NinaCsvReader.ParseImageCsv(stream);

        Assert.Null(result["frame.fits"].Fwhm);
    }

    // Review item 11: an unchecked (int) cast of an out-of-range double yields
    // int.MinValue rather than failing, so 1e30 stars became -2147483648.
    [Theory]
    [InlineData("1e30")]
    [InlineData("-1e30")]
    [InlineData("Infinity")]
    public void ParseImageCsv_OutOfRangeInteger_ConvertsToNull(string cell)
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            new Dictionary<string, string?> { ["FilePath"] = "frame.fits", ["DetectedStars"] = cell },
        };
        using var stream = CsvBuilder.BuildImageMetaDataCsv(rows);

        var result = NinaCsvReader.ParseImageCsv(stream);

        Assert.Null(result["frame.fits"].DetectedStars);
    }

    [Fact]
    public void ParseImageCsv_InRangeInteger_StillParses()
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>
        {
            new Dictionary<string, string?> { ["FilePath"] = "frame.fits", ["DetectedStars"] = "1234" },
        };
        using var stream = CsvBuilder.BuildImageMetaDataCsv(rows);

        var result = NinaCsvReader.ParseImageCsv(stream);

        Assert.Equal(1234, result["frame.fits"].DetectedStars);
    }

    // Review item 10: Exists/GetFileInfo/OpenRead is a TOCTOU window; a CSV locked or
    // deleted between the check and the open threw out of ApplyCsvBackfill, which is
    // documented as best-effort.
    [Fact]
    public void ApplyCsvBackfill_CsvLockedExclusively_ReturnsInputUnchanged()
    {
        Directory.CreateDirectory(_tempDir);
        var framePath = Path.Combine(_tempDir, "frame_001.fits");
        var csvPath = Path.Combine(_tempDir, "ImageMetaData.csv");
        File.WriteAllText(csvPath, "FilePath,HFR\r\nframe_001.fits,2.0\r\n");

        using var exclusive = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var reader = new NinaCsvReader();
        var extracted = Extracted(new ExtractedMetadata { MedianHfr = 1.0 });

        var result = reader.ApplyCsvBackfill(extracted, framePath);

        Assert.Same(extracted, result);
    }
}
