using GalactiLog.Core.Metadata;
using GalactiLog.Core.Tests.Fixtures;
using Xunit;

namespace GalactiLog.Core.Tests.Metadata;

// Review item 8: the read-header, extract, CSV-backfill pipeline and the supported-extension
// gate used to live only in the CLI. They live in FrameReader now, so this suite exercises
// the spine directly rather than through a verb. This is tests/**, outside FileSafetyTest's
// src/** scan, so writing fixture files here is fine.
public class FrameReaderTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("galactilog-framereader-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string Write(string fileName, byte[] content)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    private string WriteFits(string fileName) => Write(fileName, new FitsBuilder()
        .Card("SIMPLE", true)
        .Card("BITPIX", 16L)
        .Card("NAXIS", 0L)
        .Card("OBJECT", "M31")
        .Card("EXPTIME", 120.0)
        .Build()
        .ToArray());

    private string WriteXisf(string fileName) => Write(fileName, new XisfBuilder()
        .Geometry(2, 2, 1)
        .SampleFormat("UInt16")
        .FitsKeyword("OBJECT", "'M42'")
        .FitsKeyword("EXPTIME", "60")
        .Pixels(new float[,] { { 1, 2 }, { 3, 4 } })
        .Build()
        .ToArray());

    [Fact]
    public void SupportedExtensions_AreTheFourFromSpec()
    {
        Assert.Equal(new[] { ".fits", ".fit", ".fts", ".xisf" }, FrameReader.SupportedExtensions);
    }

    [Theory]
    [InlineData("a.FITS", FrameReader.FrameFormat.Fits)]
    [InlineData("a.Fit", FrameReader.FrameFormat.Fits)]
    [InlineData("a.fts", FrameReader.FrameFormat.Fits)]
    [InlineData("a.XISF", FrameReader.FrameFormat.Xisf)]
    [InlineData("a.png", FrameReader.FrameFormat.Unsupported)]
    [InlineData("a", FrameReader.FrameFormat.Unsupported)]
    public void FormatOf_MatchesExtensionCaseInsensitively(string fileName, FrameReader.FrameFormat expected)
    {
        Assert.Equal(expected, FrameReader.FormatOf(fileName));
    }

    [Fact]
    public void TryRead_FitsFixture_ExtractsMetadata()
    {
        var path = WriteFits("m31.fits");

        var ok = FrameReader.TryRead(path, new NinaCsvReader(), out var result, out var reason);

        Assert.True(ok, reason);
        Assert.Null(reason);
        Assert.Equal("M31", result!.Metadata.ObjectName);
        Assert.Equal(120.0, result!.Metadata.ExposureTime);
    }

    [Fact]
    public void TryRead_XisfFixture_ExtractsMetadata()
    {
        var path = WriteXisf("m42.xisf");

        var ok = FrameReader.TryRead(path, new NinaCsvReader(), out var result, out var reason);

        Assert.True(ok, reason);
        Assert.Equal("M42", result!.Metadata.ObjectName);
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsFalseWithReason()
    {
        var ok = FrameReader.TryRead(
            Path.Combine(_tempDir, "absent.fits"), new NinaCsvReader(), out var result, out var reason);

        Assert.False(ok);
        Assert.Null(result);
        Assert.Contains("file not found", reason);
    }

    [Fact]
    public void TryRead_UnsupportedExtension_ReturnsFalseWithReason()
    {
        var path = Write("frame.png", new byte[] { 1, 2, 3 });

        var ok = FrameReader.TryRead(path, new NinaCsvReader(), out var result, out var reason);

        Assert.False(ok);
        Assert.Null(result);
        Assert.Contains("unsupported file extension", reason);
    }

    [Fact]
    public void TryRead_RejectedFitsHeader_ReturnsFalseWithTheReaderReason()
    {
        var path = Write("garbage.fits", new byte[3000]);

        var ok = FrameReader.TryRead(path, new NinaCsvReader(), out var result, out var reason);

        Assert.False(ok);
        Assert.Null(result);
        Assert.Equal("header not terminated", reason);
    }

    [Fact]
    public void TryRead_TruncatedXisf_ReturnsFalseWithoutThrowing()
    {
        var path = Write("tiny.xisf", new byte[] { 1, 2, 3, 4, 5 });

        var ok = FrameReader.TryRead(path, new NinaCsvReader(), out var result, out var reason);

        Assert.False(ok);
        Assert.Null(result);
        Assert.Equal("not a valid XISF file", reason);
    }

    [Fact]
    public void TryRead_UnreadableFile_ReturnsFalseWithoutThrowing()
    {
        var path = WriteFits("locked.fits");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var ok = FrameReader.TryRead(path, new NinaCsvReader(), out var result, out var reason);

        Assert.False(ok);
        Assert.Null(result);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void TryRead_CsvPresent_BackfillsAndStampsCsvProvenance()
    {
        var path = WriteFits("frame_001.fits");
        File.WriteAllText(
            Path.Combine(_tempDir, "ImageMetaData.csv"),
            "FilePath,HFR\r\nframe_001.fits,2.75\r\n");

        var ok = FrameReader.TryRead(path, new NinaCsvReader(), out var result, out var reason);

        Assert.True(ok, reason);
        Assert.Equal(2.75, result!.Metadata.MedianHfr);
        Assert.Equal("csv:HFR", result.Metadata.Provenance["median_hfr"]);
    }

    [Fact]
    public void TryRead_CsvAbsent_StillSucceeds()
    {
        var path = WriteFits("frame_002.fits");

        var ok = FrameReader.TryRead(path, new NinaCsvReader(), out var result, out var reason);

        Assert.True(ok, reason);
        Assert.Null(result!.Metadata.MedianHfr);
    }
}
