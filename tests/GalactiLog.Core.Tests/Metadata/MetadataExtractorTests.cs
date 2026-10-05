using GalactiLog.Core.Fits;
using GalactiLog.Core.Metadata;
using GalactiLog.Core.Tests.Fixtures;
using GalactiLog.Core.Xisf;
using Xunit;

namespace GalactiLog.Core.Tests.Metadata;

public class MetadataExtractorTests
{
    // SIMPLE/BITPIX/NAXIS=0 satisfies every header-level acceptance rule with no pixel
    // data (mirrors FitsHeaderReaderTests.MinimalValid), so field-extraction tests that
    // don't care about pixel data can build on this.
    // F4: IMAGETYP is part of the minimal fixture because an absent one is now a defaulted value
    // with a warning note (spec 7.5), and most tests here assert on an otherwise-empty warning
    // list. The tests that care about the default build a header without it, below.
    private static FitsBuilder MinimalValidFits() => NoImageTypeFits().Card("IMAGETYP", "LIGHT");

    private static FitsBuilder NoImageTypeFits() =>
        new FitsBuilder().Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 0L);

    private static IReadOnlyList<FitsCard> ReadCards(FitsBuilder builder)
    {
        using var stream = builder.Build();
        var result = FitsHeaderReader.Read(stream);
        Assert.True(result.Accepted);
        return result.Cards;
    }

    private static XisfHeaderResult ReadXisfHeader(XisfBuilder builder)
    {
        var stream = builder.Build();
        var result = XisfHeaderReader.Read(stream);
        Assert.True(result.Accepted);
        return result;
    }

    // F4: same reason as MinimalValidFits above.
    private static XisfBuilder MinimalValidXisf() => NoImageTypeXisf().FitsKeyword("IMAGETYP", "LIGHT");

    private static XisfBuilder NoImageTypeXisf() =>
        new XisfBuilder().Geometry(2, 2, 1).SampleFormat("UInt16")
            .Pixels(new float[,] { { 1, 2 }, { 3, 4 } });

    // --- Field table: first matching keyword wins, provenance is the matched keyword ---

    [Fact]
    public void FromFits_EveryHeaderSourcedField_ResolvesFromFirstMatchingKeyword()
    {
        var cards = ReadCards(MinimalValidFits()
            .Card("OBJECT", "M31")
            .Card("EXPTIME", 30.0)
            .Card("FILTER", "Ha")
            .Card("CCD-TEMP", -10.5)
            .Card("IMAGETYP", "LIGHT")
            .Card("TELESCOP", "Newt200")
            .Card("INSTRUME", "ASI2600MM")
            .Card("OBJCTROT", 45.0)
            .Card("OBJCTALT", 62.0));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");
        var m = result.Metadata;

        Assert.Equal("M31", m.ObjectName);
        Assert.Equal(30.0, m.ExposureTime);
        Assert.Equal("Ha", m.FilterUsed);
        Assert.Equal(-10.5, m.SensorTemp);
        Assert.Equal("LIGHT", m.ImageType);
        Assert.Equal("Newt200", m.Telescope);
        Assert.Equal("ASI2600MM", m.Camera);
        Assert.Equal(45.0, m.RotatorPosition);
        Assert.Equal(62.0, m.AltitudeDeg);

        Assert.Equal("OBJECT", m.Provenance["object_name"]);
        Assert.Equal("EXPTIME", m.Provenance["exposure_time"]);
        Assert.Equal("FILTER", m.Provenance["filter_used"]);
        Assert.Equal("CCD-TEMP", m.Provenance["sensor_temp"]);
        Assert.Equal("IMAGETYP", m.Provenance["image_type"]);
        Assert.Equal("TELESCOP", m.Provenance["telescope"]);
        Assert.Equal("INSTRUME", m.Provenance["camera"]);
        Assert.Equal("OBJCTROT", m.Provenance["rotator_position"]);
        Assert.Equal("OBJCTALT", m.Provenance["altitude_deg"]);
    }

    [Fact]
    public void FromFits_ExptimeAbsentExposurePresent_ResolvesExposureTimeWithExposureProvenance()
    {
        var cards = ReadCards(MinimalValidFits().Card("EXPOSURE", 60.0));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Equal(60.0, result.Metadata.ExposureTime);
        Assert.Equal("EXPOSURE", result.Metadata.Provenance["exposure_time"]);
    }

    [Fact]
    public void FromFits_CentaltUsedWhenObjctaltAbsent()
    {
        var cards = ReadCards(MinimalValidFits().Card("CENTALT", 40.0));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Equal(40.0, result.Metadata.AltitudeDeg);
        Assert.Equal("CENTALT", result.Metadata.Provenance["altitude_deg"]);
    }

    [Fact]
    public void TryFirstString_UnquotedNumericObject_StoresInvariantStringForm()
    {
        // Ruling: an unquoted OBJECT card parses to a long (FitsCard.Value), not a
        // string. object_name must still store its invariant-culture string form
        // rather than silently dropping it, matching Python's untyped header.get().
        var cards = ReadCards(MinimalValidFits().Card("OBJECT", 7331L));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Equal("7331", result.Metadata.ObjectName);
        Assert.Equal("OBJECT", result.Metadata.Provenance["object_name"]);
    }

    // --- camera_gain: strict base-10 integer ---

    [Fact]
    public void CameraGain_IntegerCard_Resolves()
    {
        var cards = ReadCards(MinimalValidFits().Card("GAIN", 120L));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Equal(120, result.Metadata.CameraGain);
        Assert.Equal("GAIN", result.Metadata.Provenance["camera_gain"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void CameraGain_FloatCard_IsNullWithWarning()
    {
        var cards = ReadCards(MinimalValidFits().Card("GAIN", 120.5));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Null(result.Metadata.CameraGain);
        Assert.False(result.Metadata.Provenance.ContainsKey("camera_gain"));
        Assert.Single(result.Warnings);
        Assert.Contains("camera_gain", result.Warnings[0]);
    }

    [Fact]
    public void CameraGain_StringCard_IsNullWithWarning()
    {
        var cards = ReadCards(MinimalValidFits().Card("GAIN", "high"));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Null(result.Metadata.CameraGain);
        Assert.False(result.Metadata.Provenance.ContainsKey("camera_gain"));
        Assert.Single(result.Warnings);
        Assert.Contains("camera_gain", result.Warnings[0]);
    }

    // --- median_hfr: zero header value falls through to filename, like Python's `or` ---

    [Fact]
    public void MedianHfr_ZeroHeaderValue_FallsThroughToFilenamePattern()
    {
        var cards = ReadCards(MinimalValidFits().Card("HFR", 0.0));

        var result = MetadataExtractor.FromFits(cards, "m31_1.56HFR.fits");

        Assert.Equal(1.56, result.Metadata.MedianHfr);
        Assert.Equal("filename", result.Metadata.Provenance["median_hfr"]);
    }

    [Fact]
    public void MedianHfr_NonZeroHeaderValue_UsesHeaderNotFilename()
    {
        var cards = ReadCards(MinimalValidFits().Card("HFR", 2.3));

        var result = MetadataExtractor.FromFits(cards, "m31_1.56HFR.fits");

        Assert.Equal(2.3, result.Metadata.MedianHfr);
        Assert.Equal("HFR", result.Metadata.Provenance["median_hfr"]);
    }

    [Fact]
    public void MedianHfr_AbsentNoFilenameMatch_IsNull()
    {
        var cards = ReadCards(MinimalValidFits());

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Null(result.Metadata.MedianHfr);
        Assert.False(result.Metadata.Provenance.ContainsKey("median_hfr"));
    }

    [Fact]
    public void MedianHfr_ZeroHeaderValueAndNoFilenameMatch_IsNullWithNoStaleProvenance()
    {
        // Regression: provenance must never be stamped "HFR" for the zero-fallthrough
        // case just because HFR was present - only the branch that actually wins the
        // value gets to set provenance.
        var cards = ReadCards(MinimalValidFits().Card("HFR", 0.0));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Null(result.Metadata.MedianHfr);
        Assert.False(result.Metadata.Provenance.ContainsKey("median_hfr"));
    }

    // --- median_fwhm and fwhm never cross-populate (roadmap's explicit check) ---

    [Fact]
    public void MedianFwhm_NeverAssignedToFwhm_NoMeanfwhmFixture()
    {
        var cards = ReadCards(MinimalValidFits());

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Null(result.Metadata.MedianFwhm);
        Assert.Null(result.Metadata.Fwhm);
    }

    [Fact]
    public void MedianFwhm_MeanfwhmPresent_FillsMedianFwhmNotFwhm()
    {
        var cards = ReadCards(MinimalValidFits().Card("MEANFWHM", 2.1));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        // The roadmap's explicit check: no code path in MetadataExtractor assigns
        // MedianFwhm to Fwhm or vice versa. Verified here by checking both fields
        // independently: MedianFwhm carries the header value, Fwhm stays null (it is
        // never computed by this task at all - Task 7's CSV backfill is the only writer).
        Assert.Equal(2.1, result.Metadata.MedianFwhm);
        Assert.Equal("MEANFWHM", result.Metadata.Provenance["median_fwhm"]);
        Assert.Null(result.Metadata.Fwhm);
    }

    [Fact]
    public void MedianFwhm_FwhmKeywordUsedWhenMeanfwhmAbsent()
    {
        var cards = ReadCards(MinimalValidFits().Card("FWHM", 3.4));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Equal(3.4, result.Metadata.MedianFwhm);
        Assert.Equal("FWHM", result.Metadata.Provenance["median_fwhm"]);
        Assert.Null(result.Metadata.Fwhm);
    }

    // --- Eccentricity, spec 7.2 ---

    [Fact]
    public void Eccentricity_NativeHeaderValue_UsedDirectly()
    {
        // ECCENTRICITY is 12 characters, past the standard 8-byte keyword field, so a
        // realistic fixture (and real capture/analysis software) writes it via the
        // HIERARCH convention rather than a plain Card.
        var cards = ReadCards(MinimalValidFits().Hierarch("ECCENTRICITY", "0.5"));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Equal(0.5, result.Metadata.Eccentricity);
        Assert.Equal("header", result.Metadata.EccentricitySource);
        Assert.Equal("header", result.Metadata.Provenance["eccentricity"]);
    }

    [Fact]
    public void Eccentricity_DerivedFromEllipticityWhenEccentricityAbsent()
    {
        var cards = ReadCards(MinimalValidFits().Hierarch("ELLIPTICITY", "0.2"));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        var axisRatio = 1.0 - 0.2;
        var expected = Math.Sqrt(1.0 - axisRatio * axisRatio);
        Assert.NotNull(result.Metadata.Eccentricity);
        Assert.Equal(expected, result.Metadata.Eccentricity!.Value, 10);
        Assert.Equal("ellipticity", result.Metadata.EccentricitySource);
        Assert.Equal("ellipticity", result.Metadata.Provenance["eccentricity"]);
    }

    [Fact]
    public void Eccentricity_EllipticityProducingNegativeRadicand_BothNull()
    {
        var cards = ReadCards(MinimalValidFits().Hierarch("ELLIPTICITY", "2.5"));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Null(result.Metadata.Eccentricity);
        Assert.Null(result.Metadata.EccentricitySource);
        Assert.False(result.Metadata.Provenance.ContainsKey("eccentricity"));
    }

    [Fact]
    public void Eccentricity_NeitherKeywordPresent_BothNull()
    {
        var cards = ReadCards(MinimalValidFits());

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Null(result.Metadata.Eccentricity);
        Assert.Null(result.Metadata.EccentricitySource);
        Assert.False(result.Metadata.Provenance.ContainsKey("eccentricity"));
    }

    // --- Plate scale ---

    [Fact]
    public void ArcsecPerPixel_ComputedFromXpixszAndFocallen()
    {
        var cards = ReadCards(MinimalValidFits().Card("XPIXSZ", 3.8).Card("FOCALLEN", 600.0));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.NotNull(result.Metadata.ArcsecPerPixel);
        Assert.Equal(206.265 * 3.8 / 600, result.Metadata.ArcsecPerPixel!.Value, 10);
        Assert.Equal("XPIXSZ+FOCALLEN", result.Metadata.Provenance["arcsec_per_pixel"]);
    }

    [Theory]
    [InlineData(null, 600.0)]
    [InlineData(3.8, null)]
    [InlineData(0.0, 600.0)]
    [InlineData(-3.8, 600.0)]
    [InlineData(3.8, 0.0)]
    [InlineData(3.8, -600.0)]
    public void ArcsecPerPixel_MissingZeroOrNegative_IsNullNoProvenance(double? xpixsz, double? focallen)
    {
        var builder = MinimalValidFits();
        if (xpixsz is not null) builder = builder.Card("XPIXSZ", xpixsz.Value);
        if (focallen is not null) builder = builder.Card("FOCALLEN", focallen.Value);
        var cards = ReadCards(builder);

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Null(result.Metadata.ArcsecPerPixel);
        Assert.False(result.Metadata.Provenance.ContainsKey("arcsec_per_pixel"));
    }

    [Fact]
    public void ArcsecPerPixel_NaNXpixsz_IsNullNoProvenance()
    {
        var cards = ReadCards(MinimalValidFits().Card("XPIXSZ", "NaN").Card("FOCALLEN", 600.0));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Null(result.Metadata.ArcsecPerPixel);
        Assert.False(result.Metadata.Provenance.ContainsKey("arcsec_per_pixel"));
    }

    // --- DATE-OBS, spec 7.1.2 ---

    [Fact]
    public void CaptureDate_OffsetlessDateObs_TreatedAsUtc()
    {
        var cards = ReadCards(MinimalValidFits().Card("DATE-OBS", "2025-03-20T20:00:00"));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Equal("2025-03-20T20:00:00Z", result.Metadata.CaptureDate);
        Assert.Equal("DATE-OBS", result.Metadata.Provenance["capture_date"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void CaptureDate_ExplicitOffsetDateObs_ConvertedToUtc()
    {
        var cards = ReadCards(MinimalValidFits().Card("DATE-OBS", "2025-03-20T20:00:00+02:00"));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Equal("2025-03-20T18:00:00Z", result.Metadata.CaptureDate);
    }

    [Fact]
    public void CaptureDate_UnparseableDateObs_IsNullWithWarning()
    {
        var cards = ReadCards(MinimalValidFits().Card("DATE-OBS", "not a date"));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Null(result.Metadata.CaptureDate);
        Assert.False(result.Metadata.Provenance.ContainsKey("capture_date"));
        Assert.Single(result.Warnings);
        Assert.Contains("Unparseable DATE-OBS", result.Warnings[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CaptureDate_EmptyOrWhitespaceDateObs_IsTreatedAsAbsentWithNoWarning(string dateObsRaw)
    {
        var cards = ReadCards(MinimalValidFits().Card("DATE-OBS", dateObsRaw));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Null(result.Metadata.CaptureDate);
        Assert.False(result.Metadata.Provenance.ContainsKey("capture_date"));
        Assert.Empty(result.Warnings);
    }

    // --- RawHeaders parity with Task 2's BuildRawHeaders ---

    [Fact]
    public void FromFits_RawHeaders_MatchesFitsHeaderReaderBuildRawHeadersByteForByte()
    {
        var cards = ReadCards(MinimalValidFits().Card("OBJECT", "M31").Card("EXPTIME", 30.0));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");
        var expected = FitsHeaderReader.BuildRawHeaders(cards);

        Assert.Equal(expected.ToJsonString(), result.Metadata.RawHeaders.ToJsonString());
    }

    // --- FromXisf: gap-fill priority and image_type divergence ---

    [Fact]
    public void FromXisf_FitsKeywordTakesPriorityOverProperty()
    {
        var header = ReadXisfHeader(MinimalValidXisf()
            .FitsKeyword("OBJECT", "M31")
            .Property("Observation:Object:Name", "String", "M42"));

        var result = MetadataExtractor.FromXisf(header, "frame.xisf");

        Assert.Equal("M31", result.Metadata.ObjectName);
        Assert.Equal("OBJECT", result.Metadata.Provenance["object_name"]);
    }

    [Fact]
    public void FromXisf_PropertyOnlyValue_FillsGapWithFitsKeywordProvenance()
    {
        var header = ReadXisfHeader(MinimalValidXisf()
            .Property("Observation:Object:Name", "String", "M42"));

        var result = MetadataExtractor.FromXisf(header, "frame.xisf");

        // The gap-fill value's provenance is the FITS keyword name it was mapped to, not
        // the XISF property id: ExtractCore reads the already-merged dictionary and has
        // no way to know (or care) where the value originated.
        Assert.Equal("M42", result.Metadata.ObjectName);
        Assert.Equal("OBJECT", result.Metadata.Provenance["object_name"]);
    }

    [Fact]
    public void FromXisf_ImageTypeAsRealFitsKeyword_ProvenanceIsImagetyp()
    {
        var header = ReadXisfHeader(NoImageTypeXisf().FitsKeyword("IMAGETYP", "LIGHT"));

        var result = MetadataExtractor.FromXisf(header, "frame.xisf");

        Assert.Equal("LIGHT", result.Metadata.ImageType);
        Assert.Equal("IMAGETYP", result.Metadata.Provenance["image_type"]);
    }

    [Fact]
    public void FromXisf_ImageTypeFromAttributeOnly_ProvenanceIsXisfImageType()
    {
        var header = ReadXisfHeader(NoImageTypeXisf().ImageTypeAttribute("Light"));

        var result = MetadataExtractor.FromXisf(header, "frame.xisf");

        // F4: normalised at the write side, so the XISF attribute path folds case too.
        Assert.Equal("LIGHT", result.Metadata.ImageType);
        Assert.Equal("xisf:imageType", result.Metadata.Provenance["image_type"]);
    }

    // ---- F4 / spec 7.5: image_type is normalised once, at the write side -------------------

    [Theory]
    [InlineData("Light", "LIGHT")]
    [InlineData(" light ", "LIGHT")]
    [InlineData("LIGHT", "LIGHT")]
    [InlineData("dark", "DARK")]
    // "Light Frame" is a distinct value and stays one: only exact LIGHT is a light frame for the
    // dashboard, and CalibrationFrames classification is unaffected either way.
    [InlineData("Light Frame", "LIGHT FRAME")]
    public void FromFits_ImageType_IsStoredTrimmedAndUpperCased(string stored, string expected)
    {
        var cards = ReadCards(NoImageTypeFits().Card("IMAGETYP", stored));

        var result = MetadataExtractor.FromFits(cards, "frame.fits");

        Assert.Equal(expected, result.Metadata.ImageType);
        Assert.Equal("IMAGETYP", result.Metadata.Provenance["image_type"]);
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("image_type:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromFits_AbsentOrBlankImageType_DefaultsToLightWithDefaultProvenanceAndAWarning(string? stored)
    {
        var builder = NoImageTypeFits();
        if (stored is not null)
        {
            builder = builder.Card("IMAGETYP", stored);
        }

        var result = MetadataExtractor.FromFits(ReadCards(builder), "frame.fits");

        Assert.Equal("LIGHT", result.Metadata.ImageType);
        Assert.Equal("default", result.Metadata.Provenance["image_type"]);
        Assert.Contains(result.Warnings, warning => warning.StartsWith("image_type:", StringComparison.Ordinal));
    }

    [Fact]
    public void FromXisf_AbsentImageType_DefaultsToLightWithDefaultProvenance()
    {
        var header = ReadXisfHeader(NoImageTypeXisf());

        var result = MetadataExtractor.FromXisf(header, "frame.xisf");

        Assert.Equal("LIGHT", result.Metadata.ImageType);
        Assert.Equal("default", result.Metadata.Provenance["image_type"]);
    }

    [Fact]
    public void FromXisf_PropertiesOnly_FillExptimeCcdTempGainAndDateObs()
    {
        var header = ReadXisfHeader(MinimalValidXisf()
            .Property("Instrument:ExposureTime", "Float64", "30.0")
            .Property("Instrument:Sensor:Temperature", "Float64", "-10.5")
            .Property("Instrument:Camera:Gain", "Int32", "120")
            .Property("Observation:Time:Start", "String", "2025-03-20T20:00:00"));

        var result = MetadataExtractor.FromXisf(header, "frame.xisf");
        var m = result.Metadata;

        Assert.Equal(30.0, m.ExposureTime);
        Assert.Equal("EXPTIME", m.Provenance["exposure_time"]);
        Assert.Equal(-10.5, m.SensorTemp);
        Assert.Equal("CCD-TEMP", m.Provenance["sensor_temp"]);
        Assert.Equal(120, m.CameraGain);
        Assert.Equal("GAIN", m.Provenance["camera_gain"]);
        Assert.Equal("2025-03-20T20:00:00Z", m.CaptureDate);
        Assert.Equal("DATE-OBS", m.Provenance["capture_date"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void FromXisf_GainPropertyNonInteger_WarnsAndNulls()
    {
        var header = ReadXisfHeader(MinimalValidXisf()
            .Property("Instrument:Camera:Gain", "Float64", "120.0"));

        var result = MetadataExtractor.FromXisf(header, "frame.xisf");

        Assert.Null(result.Metadata.CameraGain);
        Assert.False(result.Metadata.Provenance.ContainsKey("camera_gain"));
        Assert.Single(result.Warnings);
        Assert.Contains("camera_gain", result.Warnings[0]);
    }

    [Fact]
    public void FromXisf_RawHeaders_ContainsBothFitsKeywordsAndProperties()
    {
        var header = ReadXisfHeader(MinimalValidXisf()
            .FitsKeyword("OBJECT", "M31")
            .Property("Instrument:Filter:Name", "String", "Ha"));

        var result = MetadataExtractor.FromXisf(header, "frame.xisf");

        Assert.Equal("M31", (string?)result.Metadata.RawHeaders["OBJECT"]);
        Assert.Equal("Ha", (string?)result.Metadata.RawHeaders["Instrument:Filter:Name"]);
    }

    // Review item 15: double.Parse on the filename HFR capture returns PositiveInfinity for
    // an overflowing digit run instead of throwing, poisoning every downstream statistic.
    [Fact]
    public void FromFits_FilenameHfrOverflowsDouble_LeavesMedianHfrNull()
    {
        var fileName = new string('9', 400) + ".5HFR.fits";
        var header = FitsHeaderReader.Read(MinimalValidFits().Build());
        Assert.True(header.Accepted, header.RejectionReason);

        var result = MetadataExtractor.FromFits(header.Cards, fileName);

        Assert.Null(result.Metadata.MedianHfr);
        Assert.False(result.Metadata.Provenance.ContainsKey("median_hfr"));
    }

    [Fact]
    public void FromFits_FilenameHfrInRange_StillParses()
    {
        var header = FitsHeaderReader.Read(MinimalValidFits().Build());
        Assert.True(header.Accepted, header.RejectionReason);

        var result = MetadataExtractor.FromFits(header.Cards, "light_3.25HFR.fits");

        Assert.Equal(3.25, result.Metadata.MedianHfr);
        Assert.Equal("filename", result.Metadata.Provenance["median_hfr"]);
    }

    // Review item 9: raw_headers for XISF is built in one place now, so the extractor and
    // the CLI's dump-headers verb cannot disagree about its contents.
    [Fact]
    public void FromXisf_RawHeadersMatchesXisfHeaderReaderBuildRawHeaders()
    {
        var stream = new XisfBuilder()
            .Geometry(2, 2, 1)
            .SampleFormat("UInt16")
            .FitsKeyword("OBJECT", "'M31'")
            .Property("Instrument:ExposureTime", "Float32", "120")
            .Pixels(new float[,] { { 1, 2 }, { 3, 4 } })
            .Build();
        var header = XisfHeaderReader.Read(stream);
        Assert.True(header.Accepted, header.RejectionReason);

        var result = MetadataExtractor.FromXisf(header, "frame.xisf");

        Assert.Equal(
            XisfHeaderReader.BuildRawHeaders(header).ToJsonString(),
            result.Metadata.RawHeaders.ToJsonString());
    }

    // --- Phase 18 geometry fields: ra_deg, dec_deg, width_px (spec 7.1) ---

    [Fact]
    public void FromFits_DecimalRaDec_AndNaxis1_FillGeometryWithProvenance()
    {
        var cards = ReadCards(MinimalValidFits()
            .Card("RA", 10.684).Card("DEC", 41.269)
            .Card("OBJCTRA", "01 00 00").Card("OBJCTDEC", "+10 00 00")
            .Card("NAXIS1", 6248L));

        var m = MetadataExtractor.FromFits(cards, "frame.fits").Metadata;

        Assert.Equal(10.684, m.RaDeg);
        Assert.Equal(41.269, m.DecDeg);
        Assert.Equal(6248, m.WidthPx);
        Assert.Equal("RA", m.Provenance["ra_deg"]);
        Assert.Equal("DEC", m.Provenance["dec_deg"]);
        Assert.Equal("NAXIS1", m.Provenance["width_px"]);
    }

    [Fact]
    public void FromFits_SexagesimalFallback_PerAxis()
    {
        var cards = ReadCards(MinimalValidFits()
            .Card("RA", "not a number").Card("OBJCTRA", "00 42 44")
            .Card("OBJCTDEC", "-00 30 00"));

        var m = MetadataExtractor.FromFits(cards, "frame.fits").Metadata;

        Assert.Equal((42 / 60.0 + 44 / 3600.0) * 15, m.RaDeg!.Value, 9);
        Assert.Equal(-0.5, m.DecDeg);
        Assert.Equal("OBJCTRA", m.Provenance["ra_deg"]);
        Assert.Equal("OBJCTDEC", m.Provenance["dec_deg"]);
    }

    [Fact]
    public void FromFits_MissingDeclinationOrBadWidth_LeavesGeometryNull()
    {
        var cards = ReadCards(MinimalValidFits().Card("RA", 10.0).Card("NAXIS1", "4000.5"));

        var m = MetadataExtractor.FromFits(cards, "frame.fits").Metadata;

        Assert.Null(m.RaDeg);
        Assert.Null(m.DecDeg);
        Assert.Null(m.WidthPx);
        Assert.False(m.Provenance.ContainsKey("ra_deg"));
        Assert.False(m.Provenance.ContainsKey("width_px"));
    }
}
