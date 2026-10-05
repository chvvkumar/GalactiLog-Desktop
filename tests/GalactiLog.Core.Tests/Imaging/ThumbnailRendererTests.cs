using GalactiLog.Core.Imaging;
using GalactiLog.Core.Tests.Fixtures;
using SkiaSharp;
using Xunit;

namespace GalactiLog.Core.Tests.Imaging;

/// <summary>
/// Spec 11.2's pipeline order and spec 6.2.8's orientation rule, plus the skip contract Task 4 and
/// Task 6 rely on (a bad frame is data, never an exception).
/// </summary>
/// <remarks>
/// <para>
/// Fixtures are built with <see cref="FitsBuilder"/> / <see cref="XisfBuilder"/> (spec 18.2) and
/// written to this class's own temp directory, because <c>Render</c> takes a path. That is plain
/// test-project file IO: <c>FileSafetyTest</c> scans <c>src/**</c> only, and nothing under
/// <c>src/**</c> writes, deletes or modifies any file outside <c>AppWriter</c>.
/// </para>
/// <para>
/// Every pixel assertion here is on a decoded JPEG, so every one of them is on lossy data. Means
/// over bands with wide margins, never an individual pixel.
/// </para>
/// </remarks>
public sealed class ThumbnailRendererTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("galactilog-thumb-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A handle a failed test left open is not worth failing the run over.
        }
    }

    // Orientation, the roadmap's first Verify clause.

    [Fact]
    public void Render_FitsFrame_IsVerticallyFlipped()
    {
        var path = MonoFits("flip.fits", TopBright(32, 32));

        var result = ThumbnailRenderer.Render(path, 32, 85, RenderMode.Thumbnail);

        using var image = Decode(result);
        Assert.True(
            BandMean(image, 16, 16, c => c.Red) > BandMean(image, 0, 16, c => c.Red) + 60,
            "FITS is bottom-up: the bright top half of the source must render in the bottom half.");
    }

    [Fact]
    public void Render_XisfFrame_IsNotFlipped()
    {
        var path = MonoXisf("noflip.xisf", TopBright(32, 32));

        var result = ThumbnailRenderer.Render(path, 32, 85, RenderMode.Thumbnail);

        using var image = Decode(result);
        Assert.True(
            BandMean(image, 0, 16, c => c.Red) > BandMean(image, 16, 16, c => c.Red) + 60,
            "XISF uses a top-left origin: the bright top half of the source must stay on top.");
    }

    [Fact]
    public void Render_FitsColourFrame_FlipsEveryChannel()
    {
        var path = ColourFits("colour-flip.fits", BandedChannels(32, 32));

        var result = ThumbnailRenderer.Render(path, 32, 85, RenderMode.Thumbnail);

        using var image = Decode(result);
        // Source bands are red rows 0-7, green rows 8-15, blue rows 16-23. Flipped, they land at
        // rows 24-31, 16-23 and 8-15. A channel that was not flipped keeps its source band.
        AssertBandIsBrightest(image, c => c.Red, 24);
        AssertBandIsBrightest(image, c => c.Green, 16);
        AssertBandIsBrightest(image, c => c.Blue, 8);
    }

    [Fact]
    public void Render_XisfColourFrame_FlipsNoChannel()
    {
        var path = ColourXisf("colour-noflip.xisf", BandedChannels(32, 32));

        var result = ThumbnailRenderer.Render(path, 32, 85, RenderMode.Thumbnail);

        using var image = Decode(result);
        AssertBandIsBrightest(image, c => c.Red, 0);
        AssertBandIsBrightest(image, c => c.Green, 8);
        AssertBandIsBrightest(image, c => c.Blue, 16);
    }

    [Fact]
    public void Render_FlipHappensBeforeTheResize()
    {
        // 9 rows, so the resize prefilter (factor 2 at a target of 4) crops exactly one row off the
        // bottom of whatever it is handed. The stated order hands it the FLIPPED array, so the row
        // it crops is source row 0; the bright source row 8 survives and lands at the output's top.
        // Resizing before flipping would crop source row 8 instead and the marker would vanish.
        var pixels = new float[9, 9];
        for (var row = 0; row < 9; row++)
        {
            for (var col = 0; col < 9; col++)
            {
                pixels[row, col] = row == 8 ? 10000f : 100f + col;
            }
        }

        var result = ThumbnailRenderer.Render(MonoFits("order.fits", pixels), 4, 85, RenderMode.Thumbnail);

        using var image = Decode(result);
        Assert.Equal(4, result.Width);
        Assert.Equal(4, result.Height);
        Assert.True(
            BandMean(image, 0, 1, c => c.Red) > 200,
            "the source's last row is the one the stated order keeps, and it belongs at the top");
        Assert.True(
            BandMean(image, 1, 3, c => c.Red) < 120,
            "the rest of the frame is far below the marker row");
    }

    // Pipeline order.

    [Fact]
    public void Render_Thumbnail_BlockBinsAMonoFrame()
    {
        // 6000 by 4000 at a target of 800 gives a bin step of 3, so the resampler sees roughly
        // twice the target width rather than the raw sensor. BITPIX 8 keeps the fixture at 24 MB.
        var path = MonoFits("big.fits", Mod256(4000, 6000), bitpix: 8);

        var result = ThumbnailRenderer.Render(path, 800, 85, RenderMode.Thumbnail);

        Assert.True(result.Rendered, result.SkipReason);
        Assert.Equal(800, result.Width);
    }

    [Fact]
    public void Render_Preview_DoesNotBlockBin()
    {
        // 40 wide by 200 tall at a target of 40: the resize is a no-op (width is already at the
        // target) so block-binning is the only thing that could change the dimensions. Thumbnail
        // mode bins by 2; Preview mode must not.
        var path = MonoFits("tall.fits", Ramp(200, 40));

        var preview = ThumbnailRenderer.Render(path, 40, 90, RenderMode.Preview);
        var thumbnail = ThumbnailRenderer.Render(path, 40, 85, RenderMode.Thumbnail);

        Assert.Equal((40, 200), (preview.Width, preview.Height));
        Assert.Equal((20, 100), (thumbnail.Width, thumbnail.Height));
    }

    [Fact]
    public void Render_Preview_MaxWidthZero_RendersAtNativeResolution()
    {
        var path = MonoFits("native.fits", Ramp(23, 37));

        var result = ThumbnailRenderer.Render(path, 0, 90, RenderMode.Preview);

        Assert.True(result.Rendered, result.SkipReason);
        Assert.Equal((37, 23), (result.Width, result.Height));
    }

    [Fact]
    public void Render_Preview_MaxWidthZero_DoesNotCallTheResampler()
    {
        var path = MonoFits("native-bayer.fits", Ramp(20, 40), bayerPattern: "RGGB");

        var result = ThumbnailRenderer.Render(path, 0, 90, RenderMode.Preview);

        // Half the source in each axis, which is the debayer alone. Any resample would move it.
        Assert.Equal((20, 10), (result.Width, result.Height));
        Assert.True(result.IsColor);
    }

    [Fact]
    public void Render_BayerFrame_IsNotAlsoBlockBinned()
    {
        // 100 wide by 200 tall at a target of 50. Block-binning this mono frame would use a step of
        // 2; the debayer path must not apply it, so the output is exactly half the source.
        var path = MonoFits("bayer-nobin.fits", Ramp(200, 100), bayerPattern: "RGGB");

        var result = ThumbnailRenderer.Render(path, 50, 85, RenderMode.Thumbnail);

        Assert.Equal((50, 100), (result.Width, result.Height));
    }

    [Fact]
    public void Render_BayerFrame_ProducesAColourResult()
    {
        var path = MonoFits("bayer-colour.fits", BayerMosaic(64, 64), bayerPattern: "RGGB");

        var result = ThumbnailRenderer.Render(path, 32, 85, RenderMode.Thumbnail);

        Assert.True(result.IsColor);
        using var image = Decode(result);
        // Red rises left to right in the mosaic and blue falls; the flip does not move columns.
        Assert.True(ColumnMean(image, 24, 8, c => c.Red) > ColumnMean(image, 0, 8, c => c.Red) + 40);
        Assert.True(ColumnMean(image, 0, 8, c => c.Blue) > ColumnMean(image, 24, 8, c => c.Blue) + 40);
    }

    [Fact]
    public void Render_MonoFrame_ProducesAGrayResult()
    {
        var result = ThumbnailRenderer.Render(MonoFits("mono.fits", Ramp(16, 16)), 16, 85, RenderMode.Thumbnail);

        Assert.False(result.IsColor);
        using var image = Decode(result);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = image.GetPixel(x, y);
                Assert.Equal(pixel.Red, pixel.Green);
                Assert.Equal(pixel.Red, pixel.Blue);
            }
        }
    }

    [Fact]
    public void Render_ThreePlaneFitsColourFrame_ProducesAColourResult()
    {
        var result = ThumbnailRenderer.Render(
            ColourFits("planar.fits", BandedChannels(32, 32)), 32, 85, RenderMode.Thumbnail);

        Assert.True(result.Rendered, result.SkipReason);
        Assert.True(result.IsColor);
    }

    [Fact]
    public void Render_ColourChannelsAreStretchedIndependently()
    {
        // Red is flat, so its own normalization collapses it and Task 2's mad == 0 case renders it
        // a uniform 128. Green is a gradient and spans the byte range. A linked stretch could not
        // produce both from one set of statistics.
        var planes = new float[3, 32, 32];
        for (var row = 0; row < 32; row++)
        {
            for (var col = 0; col < 32; col++)
            {
                planes[0, row, col] = 500f;
                planes[1, row, col] = row * 100f;
                planes[2, row, col] = 500f;
            }
        }

        var result = ThumbnailRenderer.Render(ColourFits("unlinked.fits", planes), 32, 85, RenderMode.Thumbnail);

        using var image = Decode(result);
        var reds = Samples(image, c => c.Red);
        var greens = Samples(image, c => c.Green);
        Assert.InRange(reds.Average(b => (double)b), 128 - 30, 128 + 30);
        Assert.True(reds.Max() - reds.Min() < 60, "the flat channel must not pick up the gradient");
        Assert.True(greens.Max() - greens.Min() > 150, "the gradient channel must span the range");
    }

    [Fact]
    public void Render_IgnoresXbayroffAndYbayroff()
    {
        var pixels = BayerMosaic(32, 32);
        var without = ThumbnailRenderer.Render(
            MonoFits("offsets-absent.fits", pixels, bayerPattern: "RGGB"), 16, 85, RenderMode.Thumbnail);
        var with = ThumbnailRenderer.Render(
            MonoFits("offsets-present.fits", pixels, bayerPattern: "RGGB", xBayrOff: 1, yBayrOff: 1),
            16, 85, RenderMode.Thumbnail);

        Assert.True(without.Rendered, without.SkipReason);
        Assert.Equal(without.Jpeg, with.Jpeg);
    }

    [Fact]
    public void Render_XisfWithColorFilterArray_IsDebayered()
    {
        // Spec 11.1's ColorFilterArray fallback (questions.md Q6). The web application has no Bayer
        // branch for XISF at all; this port debayers, so an OSC XISF is not a green mosaic.
        var path = ColorFilterArrayXisf("cfa.xisf", BayerMosaic(64, 64), "RGGB");

        var result = ThumbnailRenderer.Render(path, 32, 85, RenderMode.Thumbnail);

        Assert.True(result.IsColor);
        Assert.Equal((32, 32), (result.Width, result.Height));
    }

    // Skips, never exceptions.

    [Fact]
    public void Render_UnsupportedExtension_IsASkip()
    {
        var path = Path.Combine(_dir, "frame.png");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        AssertSkip(ThumbnailRenderer.Render(path, 800, 85, RenderMode.Thumbnail));
    }

    [Fact]
    public void Render_MissingFile_IsASkip()
        => AssertSkip(ThumbnailRenderer.Render(
            Path.Combine(_dir, "absent.fits"), 800, 85, RenderMode.Thumbnail));

    [Fact]
    public void Render_RejectedFitsHeader_IsASkipCarryingTheRejectionReason()
    {
        var stream = new FitsBuilder()
            .RawCard("NOTSIMPLE".PadRight(80))
            .Card("BITPIX", 16L)
            .Card("NAXIS", 0L)
            .EndCard()
            .Build();

        var result = ThumbnailRenderer.Render(Write("rejected.fits", stream), 800, 85, RenderMode.Thumbnail);

        AssertSkip(result);
        Assert.Equal("not a simple FITS file", result.SkipReason);
    }

    [Fact]
    public void Render_CompressedFits_IsASkip()
    {
        var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 0L).Card("ZIMAGE", true)
            .EndCard().Build();

        var result = ThumbnailRenderer.Render(Write("zimage.fits", stream), 800, 85, RenderMode.Thumbnail);

        AssertSkip(result);
        Assert.Contains("compressed FITS", result.SkipReason);
    }

    [Fact]
    public void Render_FitsWithUnsupportedNaxisShape_IsASkip()
    {
        // Spec 6.1.5's header-only degradation: the header is accepted, the pixel data is not read.
        var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 1L).Card("NAXIS1", 10L)
            .EndCard()
            .Pixels(16, naxis1: 10, naxis2: 1, new float[1, 10])
            .Build();

        var result = ThumbnailRenderer.Render(Write("naxis1.fits", stream), 800, 85, RenderMode.Thumbnail);

        AssertSkip(result);
        Assert.Contains("unsupported NAXIS shape", result.SkipReason);
    }

    [Fact]
    public void Render_FitsBitpix64_IsASkip()
    {
        // BITPIX 64 is accepted by the header reader and declined by the pixel reader. The pixel
        // block is written at -64's sample size, which is the same eight bytes per sample.
        var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 64L).Card("NAXIS", 2L)
            .Card("NAXIS1", 4L).Card("NAXIS2", 4L)
            .EndCard()
            .Pixels(-64, naxis1: 4, naxis2: 4, new float[4, 4])
            .Build();

        var result = ThumbnailRenderer.Render(Write("bitpix64.fits", stream), 800, 85, RenderMode.Thumbnail);

        AssertSkip(result);
        Assert.Contains("BITPIX 64", result.SkipReason);
    }

    [Fact]
    public void Render_XisfWithUnsupportedSampleFormat_IsASkip()
    {
        var stream = new XisfBuilder().Geometry(4, 4, 1).SampleFormat("UInt64").Build();

        var result = ThumbnailRenderer.Render(Write("uint64.xisf", stream), 800, 85, RenderMode.Thumbnail);

        AssertSkip(result);
        Assert.Contains("UInt64", result.SkipReason);
    }

    [Theory]
    [InlineData("url:http://example.com/data.bin")]
    [InlineData("path:/some/external/file.raw")]
    public void Render_XisfWithExternalLocation_IsASkip(string location)
    {
        var stream = new XisfBuilder().Geometry(4, 4, 1).SampleFormat("UInt16")
            .LocationOverride(location).Build();

        var result = ThumbnailRenderer.Render(Write("external.xisf", stream), 800, 85, RenderMode.Thumbnail);

        AssertSkip(result);
        Assert.Equal("external data reference not supported", result.SkipReason);
    }

    [Fact]
    public void Render_XisfCielab_IsASkip()
    {
        var stream = new XisfBuilder().Geometry(4, 4, 1).SampleFormat("UInt16").ColorSpace("CIELab").Build();

        var result = ThumbnailRenderer.Render(Write("cielab.xisf", stream), 800, 85, RenderMode.Thumbnail);

        AssertSkip(result);
        Assert.Contains("CIELab", result.SkipReason);
    }

    /// <summary>
    /// The nearest reachable relative of the out-of-memory case: a frame whose declared geometry is
    /// past what the pixel reader will decode comes back as a skip, not a throw.
    /// </summary>
    /// <remarks>
    /// A genuine <see cref="OutOfMemoryException"/> cannot be forced through <c>Render</c> by any
    /// fixture of testable size, and this is deliberate in the readers rather than an oversight
    /// here. <c>FitsHeaderReader</c> rejects a header whose declared pixel block is longer than the
    /// file, <c>FitsImageReader</c> caps the axis lengths and the total pixel bytes, and
    /// <c>XisfDataBlock</c> rejects a declared uncompressed size that is negative, above
    /// <c>int.MaxValue</c>, or unequal to the geometry, before any allocation. So every path to a
    /// multi-gigabyte allocation runs through a multi-gigabyte file. <c>Render</c>'s
    /// <c>catch (OutOfMemoryException)</c> covers what is left: a real frame on a machine that
    /// cannot hold it, which is a runtime condition and not a fixture. The catch is documented at
    /// the call site and asserted here only as far as the skip contract can be reached.
    /// </remarks>
    [Fact]
    public void Render_FrameTooLargeToDecode_IsASkipNotAThrow()
    {
        var stream = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", 16L).Card("NAXIS", 2L)
            .Card("NAXIS1", 70000L).Card("NAXIS2", 1L)
            .EndCard()
            .Pixels(16, naxis1: 70000, naxis2: 1, new float[1, 70000])
            .Build();

        var result = ThumbnailRenderer.Render(Write("oversized.fits", stream), 800, 85, RenderMode.Thumbnail);

        AssertSkip(result);
        Assert.Contains("axis length out of bounds", result.SkipReason);
    }

    [Fact]
    public void Render_Skip_ReturnsNullJpegAndNonNullReason()
    {
        var path = Path.Combine(_dir, "skip.png");
        File.WriteAllBytes(path, new byte[] { 1 });

        var result = ThumbnailRenderer.Render(path, 800, 85, RenderMode.Thumbnail);

        Assert.False(result.Rendered);
        Assert.NotNull(result.SkipReason);
        Assert.Null(result.Jpeg);
        Assert.Equal(0, result.Width);
        Assert.Equal(0, result.Height);
        Assert.False(result.IsColor);
    }

    [Fact]
    public void Render_LockedFile_IsASkipNotAThrow()
    {
        var path = MonoFits("locked.fits", Ramp(8, 8));

        // FileShare.None, not FileShare.Read: UserFiles.OpenRead asks for FileShare.ReadWrite
        // precisely so a reader still opens against a FileShare.Read holder such as N.I.N.A.
        // Only a share-nothing holder actually locks it out, which is the case worth covering.
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        AssertSkip(ThumbnailRenderer.Render(path, 800, 85, RenderMode.Thumbnail));
    }

    // Encode and shape.

    [Fact]
    public void Render_ReturnsDecodableJpegBytes()
    {
        var result = ThumbnailRenderer.Render(MonoFits("decodable.fits", Ramp(24, 24)), 24, 85, RenderMode.Thumbnail);

        Assert.True(result.Rendered, result.SkipReason);
        using var image = SKBitmap.Decode(result.Jpeg);
        Assert.NotNull(image);
    }

    [Fact]
    public void Render_JpegQualityIsHonoured()
    {
        var path = MonoFits("quality.fits", Noise(64, 64));

        var high = ThumbnailRenderer.Render(path, 64, 90, RenderMode.Thumbnail);
        var low = ThumbnailRenderer.Render(path, 64, 30, RenderMode.Thumbnail);

        Assert.True(high.Jpeg!.Length > low.Jpeg!.Length);
    }

    [Fact]
    public void Render_ReportsTheOutputDimensions()
    {
        var result = ThumbnailRenderer.Render(MonoFits("dims.fits", Ramp(30, 50)), 20, 85, RenderMode.Thumbnail);

        using var image = Decode(result);
        Assert.Equal(image.Width, result.Width);
        Assert.Equal(image.Height, result.Height);
    }

    [Fact]
    public void Render_OutputWidthIsAtMostTheTarget()
    {
        var result = ThumbnailRenderer.Render(MonoFits("wide.fits", Ramp(120, 300)), 50, 85, RenderMode.Thumbnail);

        // First, as every sibling does: a skip reports Width 0, which would satisfy the bound
        // below for the one reason this test must not accept.
        Assert.True(result.Rendered, result.SkipReason);
        Assert.True(result.Width <= 50, $"width {result.Width} exceeds the target");
    }

    // Cancellation and file safety.

    [Fact]
    public void Render_Cancelled_Throws()
    {
        var path = MonoFits("cancel.fits", Ramp(16, 16));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ThumbnailRenderer.Render(path, 16, 85, RenderMode.Thumbnail, cts.Token));
    }

    [Fact]
    public void Render_DoesNotWriteAnyFile()
    {
        var path = MonoFits("readonly.fits", Ramp(16, 16));
        var before = Snapshot();

        Assert.True(ThumbnailRenderer.Render(path, 16, 85, RenderMode.Thumbnail).Rendered);

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Render_OpensTheFrameWithShareReadWrite()
    {
        var path = MonoFits("shared.fits", Ramp(16, 16));

        // A writer already holds the file. A reader that asked for FileShare.Read would be refused;
        // UserFiles.OpenRead asks for FileShare.ReadWrite, so this render succeeds.
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        var result = ThumbnailRenderer.Render(path, 16, 85, RenderMode.Thumbnail);

        Assert.True(result.Rendered, result.SkipReason);
    }

    // Fixtures.

    private string Write(string name, MemoryStream fixture)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, fixture.ToArray());
        return path;
    }

    private string MonoFits(
        string name, float[,] pixels, short bitpix = 16, string? bayerPattern = null,
        long? xBayrOff = null, long? yBayrOff = null)
    {
        var height = pixels.GetLength(0);
        var width = pixels.GetLength(1);
        var builder = new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", (long)bitpix).Card("NAXIS", 2L)
            .Card("NAXIS1", (long)width).Card("NAXIS2", (long)height);
        if (bayerPattern is not null)
        {
            builder.Card("BAYERPAT", bayerPattern);
        }
        if (xBayrOff is { } x)
        {
            builder.Card("XBAYROFF", x);
        }
        if (yBayrOff is { } y)
        {
            builder.Card("YBAYROFF", y);
        }
        return Write(name, builder.EndCard().Pixels(bitpix, width, height, pixels).Build());
    }

    private string ColourFits(string name, float[,,] planes, short bitpix = 16)
    {
        var height = planes.GetLength(1);
        var width = planes.GetLength(2);
        return Write(name, new FitsBuilder()
            .Card("SIMPLE", true).Card("BITPIX", (long)bitpix).Card("NAXIS", 3L)
            .Card("NAXIS1", (long)width).Card("NAXIS2", (long)height).Card("NAXIS3", 3L)
            .EndCard()
            .PixelsPlanarRgb(bitpix, width, height, planes)
            .Build());
    }

    private string MonoXisf(string name, float[,] pixels)
        => Write(name, new XisfBuilder()
            .Geometry(pixels.GetLength(1), pixels.GetLength(0), 1)
            .SampleFormat("UInt16")
            .Pixels(pixels)
            .Build());

    private string ColorFilterArrayXisf(string name, float[,] pixels, string pattern)
        => Write(name, new XisfBuilder()
            .Geometry(pixels.GetLength(1), pixels.GetLength(0), 1)
            .SampleFormat("UInt16")
            .ColorFilterArray(pattern, 2, 2)
            .Pixels(pixels)
            .Build());

    private string ColourXisf(string name, float[,,] planes)
        => Write(name, new XisfBuilder()
            .Geometry(planes.GetLength(2), planes.GetLength(1), 3)
            .SampleFormat("UInt16")
            .ColorSpace("RGB")
            .PixelsPlanarRgb(planes)
            .Build());

    // A bright top half over a dark bottom half, each with a mild column gradient so the stretch's
    // median absolute deviation is non-zero and the mad == 0 uniform-128 case does not fire.
    private static float[,] TopBright(int height, int width)
    {
        var pixels = new float[height, width];
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                pixels[row, col] = (row < height / 2 ? 10000f : 0f) + col;
            }
        }
        return pixels;
    }

    // Red bright over rows 0-7, green over 8-15, blue over 16-23, on a 32-row frame.
    private static float[,,] BandedChannels(int height, int width)
    {
        var planes = new float[3, height, width];
        for (var channel = 0; channel < 3; channel++)
        {
            for (var row = 0; row < height; row++)
            {
                var inBand = row >= channel * 8 && row < (channel + 1) * 8;
                for (var col = 0; col < width; col++)
                {
                    planes[channel, row, col] = (inBand ? 10000f : 0f) + col;
                }
            }
        }
        return planes;
    }

    // An RGGB mosaic whose red rises left to right and whose blue falls, with a flat green.
    private static float[,] BayerMosaic(int height, int width)
    {
        var pixels = new float[height, width];
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var cellColumn = col / 2;
                var isRed = row % 2 == 0 && col % 2 == 0;
                var isBlue = row % 2 == 1 && col % 2 == 1;
                pixels[row, col] = isRed ? 100f + (cellColumn * 100f)
                    : isBlue ? 100f + ((width / 2 - 1 - cellColumn) * 100f)
                    : 1000f;
            }
        }
        return pixels;
    }

    private static float[,] Ramp(int height, int width)
    {
        var pixels = new float[height, width];
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                pixels[row, col] = (row * 10f) + col;
            }
        }
        return pixels;
    }

    // A ramp that stays inside one byte, for the BITPIX 8 fixture.
    private static float[,] Mod256(int height, int width)
    {
        var pixels = new float[height, width];
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                pixels[row, col] = (row + col) % 251;
            }
        }
        return pixels;
    }

    private static float[,] Noise(int height, int width)
    {
        var random = new Random(7);
        var pixels = new float[height, width];
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                pixels[row, col] = random.Next(0, 20000);
            }
        }
        return pixels;
    }

    // Assertions.

    private static SKBitmap Decode(RenderResult result)
    {
        Assert.True(result.Rendered, result.SkipReason);
        Assert.NotNull(result.Jpeg);
        var image = SKBitmap.Decode(result.Jpeg);
        Assert.NotNull(image);
        return image;
    }

    private static void AssertSkip(RenderResult result)
    {
        Assert.False(result.Rendered);
        Assert.NotNull(result.SkipReason);
        Assert.Null(result.Jpeg);
    }

    private static double BandMean(SKBitmap image, int rowStart, int rowCount, Func<SKColor, byte> component)
    {
        var total = 0.0;
        for (var y = rowStart; y < rowStart + rowCount; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                total += component(image.GetPixel(x, y));
            }
        }
        return total / (rowCount * image.Width);
    }

    private static double ColumnMean(SKBitmap image, int columnStart, int columnCount, Func<SKColor, byte> component)
    {
        var total = 0.0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = columnStart; x < columnStart + columnCount; x++)
            {
                total += component(image.GetPixel(x, y));
            }
        }
        return total / (columnCount * image.Height);
    }

    private static void AssertBandIsBrightest(SKBitmap image, Func<SKColor, byte> component, int expectedBandStart)
    {
        var means = new double[4];
        for (var band = 0; band < 4; band++)
        {
            means[band] = BandMean(image, band * 8, 8, component);
        }

        var brightest = Array.IndexOf(means, means.Max());
        Assert.Equal(expectedBandStart / 8, brightest);
    }

    private static List<byte> Samples(SKBitmap image, Func<SKColor, byte> component)
    {
        var samples = new List<byte>();
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                samples.Add(component(image.GetPixel(x, y)));
            }
        }
        return samples;
    }

    private List<string> Snapshot()
        => Directory.EnumerateFileSystemEntries(_dir, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => $"{path}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path):O}")
            .ToList();
}
