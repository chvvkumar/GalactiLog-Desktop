using GalactiLog.Core;
using GalactiLog.Core.Metadata;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Sessions;
using GalactiLog.Core.Tests.Fixtures;
using Xunit;

namespace GalactiLog.Core.Tests.Scanning;

// The reader half of spec 10.3 step 3: header read, calibration classification (7.5) and
// session-date derivation (8), with no database access of any kind. This is tests/**,
// outside FileSafetyTest's src/** scan, so writing fixture files here is fine.
public class ScanRecordParserTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("galactilog-parser-").FullName;
    private readonly NinaCsvReader _csv = new();
    private readonly OnceGate _gate = new();
    private readonly List<string> _warnings = new();

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private DiscoveredFile WriteFits(string fileName, Action<FitsBuilder> cards)
    {
        var builder = new FitsBuilder().Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 0L);
        cards(builder);
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, builder.Build().ToArray());
        var info = new FileInfo(path);
        return new DiscoveredFile(path, info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds());
    }

    private ParsedRecord Parse(
        DiscoveredFile file, bool includeCalibration = false, bool useImagingNight = false,
        double? longitude = null, string? timezone = null)
        => ScanRecordParser.Parse(file, _csv, includeCalibration, useImagingNight, longitude, timezone,
            _gate, _warnings.Add);

    [Fact]
    public void Parse_ValidFrame_ReturnsIngestKind()
    {
        var file = WriteFits("light.fits", b => b
            .Card("OBJECT", "M 31")
            .Card("IMAGETYP", "LIGHT")
            .Card("EXPTIME", 300.0)
            .Card("DATE-OBS", "2025-03-14T22:15:00"));

        var record = Parse(file);

        Assert.Equal(ParsedRecordKind.Ingest, record.Kind);
        Assert.Equal(file.Path, record.FilePath);
        Assert.Equal("light.fits", record.FileName);
        Assert.Equal(file.FileSize, record.FileSize);
        Assert.Equal(file.FileMtimeUnixSeconds, record.FileMtimeUnixSeconds);
        Assert.NotNull(record.Metadata);
        Assert.Equal("M 31", record.Metadata!.ObjectName);
        Assert.Equal(300.0, record.Metadata.ExposureTime);
        Assert.Null(record.RejectionReason);
    }

    [Fact]
    public void Parse_RejectedHeader_ReturnsRejectedKindWithReason()
    {
        // BITPIX 12 is not in the accepted set (spec 6.1.5).
        var path = Path.Combine(_tempDir, "bad.fits");
        File.WriteAllBytes(path, new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 12L).Card("NAXIS", 0L).Build().ToArray());
        var file = new DiscoveredFile(path, new FileInfo(path).Length, 0);

        var record = Parse(file);

        Assert.Equal(ParsedRecordKind.Rejected, record.Kind);
        Assert.Equal("bad.fits", record.FileName);
        Assert.Contains("BITPIX", record.RejectionReason);
        Assert.Null(record.Metadata);
        Assert.Null(record.SessionDate);
    }

    [Fact]
    public void Parse_MissingFile_ReturnsRejectedKind()
    {
        var record = Parse(new DiscoveredFile(Path.Combine(_tempDir, "gone.fits"), 0, 0));

        Assert.Equal(ParsedRecordKind.Rejected, record.Kind);
        Assert.Contains("not found", record.RejectionReason);
    }

    [Theory]
    [InlineData("BIAS")]
    [InlineData("DARK")]
    [InlineData("FLAT")]
    [InlineData("DARKFLAT")]
    [InlineData("BIASFLAT")]
    public void Parse_CalibrationFrame_IncludeCalibrationFalse_ReturnsSkippedCalibration(string imageType)
    {
        var file = WriteFits($"cal-{imageType}.fits", b => b
            .Card("OBJECT", "M 31")
            .Card("IMAGETYP", imageType)
            .Card("DATE-OBS", "2025-03-14T22:15:00"));

        var record = Parse(file, includeCalibration: false);

        Assert.Equal(ParsedRecordKind.SkippedCalibration, record.Kind);
        Assert.Null(record.Metadata);
        Assert.Null(record.SessionDate);
        Assert.Null(record.RejectionReason);
        // The file stat still rides along so the writer/coordinator can account for it.
        Assert.Equal(file.Path, record.FilePath);
    }

    [Fact]
    public void Parse_CalibrationFrame_IncludeCalibrationTrue_ReturnsIngest()
    {
        var file = WriteFits("dark.fits", b => b
            .Card("IMAGETYP", "DARK")
            .Card("DATE-OBS", "2025-03-14T22:15:00"));

        var record = Parse(file, includeCalibration: true);

        Assert.Equal(ParsedRecordKind.Ingest, record.Kind);
        Assert.Equal("DARK", record.Metadata!.ImageType);
    }

    [Fact]
    public void Parse_NoDateObs_SessionDateNull_NoWarning()
    {
        var file = WriteFits("nodate.fits", b => b.Card("OBJECT", "M 31").Card("IMAGETYP", "LIGHT"));

        var record = Parse(file, useImagingNight: true);

        Assert.Equal(ParsedRecordKind.Ingest, record.Kind);
        Assert.Null(record.CaptureDateUtc);
        Assert.Null(record.SessionDate);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Parse_CaptureDate_IsParsedAsUtc_AndSessionDateFollowsLongitude()
    {
        // 2025-03-15T02:00:00Z at longitude 0: solar noon offset is 12h, so the frame
        // belongs to the night labelled 2025-03-14 (spec 8.2).
        var file = WriteFits("night.fits", b => b
            .Card("OBJECT", "M 31")
            .Card("DATE-OBS", "2025-03-15T02:00:00"));

        var record = Parse(file, useImagingNight: true, longitude: 0.0);

        Assert.Equal(DateTimeKind.Utc, record.CaptureDateUtc!.Value.Kind);
        Assert.Equal(new DateTime(2025, 3, 15, 2, 0, 0, DateTimeKind.Utc), record.CaptureDateUtc);
        Assert.Equal(new DateOnly(2025, 3, 14), record.SessionDate);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Parse_SiteLongHeader_WinsOverConfiguredLongitude_NoWarning()
    {
        var file = WriteFits("sitelong.fits", b => b
            .Card("OBJECT", "M 31")
            .Card("SITELONG", -105.0)
            .Card("DATE-OBS", "2025-03-15T02:00:00"));

        var record = Parse(file, useImagingNight: true, longitude: 0.0);

        // -105 degrees: offset = 12 - (-105/15) = 19h, so 02:00Z lands on the previous day.
        Assert.Equal(new DateOnly(2025, 3, 14), record.SessionDate);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Parse_ImagingNightFallback_WarnsOnce()
    {
        var first = WriteFits("a.fits", b => b.Card("OBJECT", "M 31").Card("DATE-OBS", "2025-03-15T02:00:00"));
        var second = WriteFits("b.fits", b => b.Card("OBJECT", "M 31").Card("DATE-OBS", "2025-03-16T02:00:00"));

        var a = Parse(first, useImagingNight: true);
        var b = Parse(second, useImagingNight: true);

        Assert.Single(_warnings);
        Assert.Equal(SessionDate.ImagingNightFallbackWarning, _warnings[0]);
        // Both still get a UTC-midnight session date; the fallback degrades, never fails.
        Assert.Equal(new DateOnly(2025, 3, 15), a.SessionDate);
        Assert.Equal(new DateOnly(2025, 3, 16), b.SessionDate);
    }

    [Fact]
    public void Parse_ImagingNightDisabled_NoFallbackWarning()
    {
        var file = WriteFits("plain.fits", b => b.Card("OBJECT", "M 31").Card("DATE-OBS", "2025-03-15T02:00:00"));

        var record = Parse(file, useImagingNight: false);

        // The warning text says use_imaging_night is enabled; a run that never asked for
        // imaging-night grouping is not degraded and must not be told it is.
        Assert.Empty(_warnings);
        Assert.Equal(new DateOnly(2025, 3, 15), record.SessionDate);
    }

    // ---- a parse that throws is one failed file, never an aborted scan ----------------

    // Spec 10.3 step 3's frame-side counterpart of step 5 item 3, "One bad file never stops the
    // pass". Spec 8.2's imaging-night shift carries no range guard, so a DATE-OBS at either end
    // of DateTime's range with a longitude resolvable makes the subtraction underflow or
    // overflow and raise ArgumentOutOfRangeException. Before the choke point in Parse, that
    // exception left this call, faulted the reader task and aborted the whole run over one file
    // the user did not write and cannot repair; the file itself was never marked failed.
    private const string ThrowPrefix = "unexpected error while reading the file: ArgumentOutOfRangeException: ";

    [Fact]
    public void Parse_CaptureDateUnderflowsTheImagingNightShift_IsRejectedNamingTheException()
    {
        var file = WriteFits("minvalue.fits", b => b
            .Card("OBJECT", "M 31")
            .Card("IMAGETYP", "LIGHT")
            .Card("DATE-OBS", "0001-01-01T00:00:00"));

        // Longitude 0 gives a 12-hour shift, so DateTime.MinValue - 12h is un-representable.
        var record = Parse(file, useImagingNight: true, longitude: 0.0);

        Assert.Equal(ParsedRecordKind.Rejected, record.Kind);
        Assert.Equal(file.Path, record.FilePath);
        Assert.Equal("minvalue.fits", record.FileName);
        Assert.Equal(file.FileSize, record.FileSize);
        Assert.Equal(file.FileMtimeUnixSeconds, record.FileMtimeUnixSeconds);
        Assert.StartsWith(ThrowPrefix, record.RejectionReason);
        // ex.Message, never ex.ToString(): a stack trace has no place in the activity feed or
        // in the activity_events.details JSON.
        Assert.DoesNotContain("   at ", record.RejectionReason);
        Assert.Null(record.Metadata);
        Assert.Null(record.CaptureDateUtc);
        Assert.Null(record.SessionDate);
    }

    [Fact]
    public void Parse_CaptureDateOverflowsTheImagingNightShift_IsRejectedNamingTheException()
    {
        // SITELONG 250 is the 0 to 360 east convention some capture software writes, kept
        // unchanged by step 1 on purpose. It gives a negative shift, so DateTime.MaxValue plus
        // it is un-representable.
        var file = WriteFits("maxvalue.fits", b => b
            .Card("OBJECT", "M 31")
            .Card("IMAGETYP", "LIGHT")
            .Card("SITELONG", 250.0)
            .Card("DATE-OBS", "9999-12-31T23:59:59"));

        var record = Parse(file, useImagingNight: true);

        Assert.Equal(ParsedRecordKind.Rejected, record.Kind);
        Assert.Equal("maxvalue.fits", record.FileName);
        Assert.StartsWith(ThrowPrefix, record.RejectionReason);
        Assert.DoesNotContain("   at ", record.RejectionReason);
        Assert.Null(record.Metadata);
        Assert.Null(record.SessionDate);
    }

    // The same frame with imaging night off takes the early return of spec 8.2 and is a
    // perfectly ordinary Ingest: the catch is reached by a genuine failure, never by a date
    // this parser can represent.
    [Fact]
    public void Parse_CaptureDateAtTheEdge_ImagingNightDisabled_IsStillIngested()
    {
        var file = WriteFits("edge.fits", b => b
            .Card("OBJECT", "M 31")
            .Card("IMAGETYP", "LIGHT")
            .Card("DATE-OBS", "0001-01-01T00:00:00"));

        var record = Parse(file, useImagingNight: false);

        Assert.Equal(ParsedRecordKind.Ingest, record.Kind);
        Assert.Equal(new DateOnly(1, 1, 1), record.SessionDate);
        Assert.Null(record.RejectionReason);
    }
}
