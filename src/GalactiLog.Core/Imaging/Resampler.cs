using System.Runtime.InteropServices;
using SkiaSharp;

namespace GalactiLog.Core.Imaging;

/// <summary>
/// Spec 11.2's resampling, applied to linear float data before the stretch and never after
/// (a port of <c>stretch.resize_array</c>), plus <c>thumbnail._read_binned</c>'s pre-binning step.
/// </summary>
/// <remarks>
/// The resampler is Mitchell cubic, not Lanczos, and that is a deliberate divergence from the web
/// application recorded in spec 11.2. There is therefore no exactness assertion anywhere against a
/// reference resampler; spec 18.1's Resampling row tests shape, aspect ratio, non-emptiness and
/// monotonic brightness. Do not add a Lanczos kernel.
/// </remarks>
public static class Resampler
{
    /// <summary>Four 32-bit components per pixel on <see cref="SKColorType.RgbaF32"/>.</summary>
    private const int ComponentsPerPixel = 4;

    private const int BytesPerPixel = ComponentsPerPixel * sizeof(float);

    /// <summary>
    /// Step 2's integer prefilter factor: <c>width / targetWidth</c>, integer division. A value
    /// below 2 means no prefilter.
    /// </summary>
    public static int PrefilterFactor(int width, int targetWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        return width / targetWidth;
    }

    /// <summary>
    /// <c>_read_binned</c>'s factor: <c>max(1, max(height, width) / (targetWidth * 2))</c>, integer
    /// division. 1 means no pre-binning.
    /// </summary>
    public static int BinStep(int height, int width, int targetWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        return Math.Max(1, Math.Max(height, width) / (targetWidth * 2));
    }

    /// <summary>
    /// Crops height and width down to multiples of <paramref name="factor"/> and mean-pools by that
    /// factor. <c>factor &lt;= 1</c> returns the input unchanged.
    /// </summary>
    public static float[,] MeanPool(float[,] data, int factor)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (factor <= 1)
        {
            return data;
        }

        var height = data.GetLength(0);
        var width = data.GetLength(1);
        var pooledHeight = height / factor;
        var pooledWidth = width / factor;

        // ponytail: NumPy's crop-and-reshape yields a zero-row or zero-column array when a
        // dimension is shorter than the factor, which the rest of the pipeline cannot resample.
        // Ceiling: a frame with fewer rows than the factor gets no prefilter on either axis, so a
        // 1-row frame reaches the Mitchell step un-band-limited. Upgrade path: pool the axis that
        // is long enough and leave the short one alone, if a sub-factor dimension ever matters.
        // Returning the input unchanged keeps a degenerate frame renderable meanwhile.
        if (pooledHeight == 0 || pooledWidth == 0)
        {
            return data;
        }

        var divisor = (double)factor * factor;
        var pooled = new float[pooledHeight, pooledWidth];
        for (var y = 0; y < pooledHeight; y++)
        {
            for (var x = 0; x < pooledWidth; x++)
            {
                var sum = 0.0;
                for (var dy = 0; dy < factor; dy++)
                {
                    for (var dx = 0; dx < factor; dx++)
                    {
                        sum += data[(y * factor) + dy, (x * factor) + dx];
                    }
                }

                pooled[y, x] = (float)(sum / divisor);
            }
        }

        return pooled;
    }

    /// <summary>
    /// <c>_read_binned</c>'s step, for a 2D mono frame: mean-pool by <see cref="BinStep"/> so
    /// roughly twice the target width is left for the resampler. Block averaging rather than
    /// strided subsampling, which integrates noise across each bin and gives square-root-of-N noise
    /// reduction per axis.
    /// </summary>
    public static float[,] ReadBinned(float[,] data, int targetWidth)
    {
        ArgumentNullException.ThrowIfNull(data);
        return MeanPool(data, BinStep(data.GetLength(0), data.GetLength(1), targetWidth));
    }

    /// <summary>
    /// Spec 11.2's four-step resample. Returns the input unchanged when it is already at or below
    /// <paramref name="targetWidth"/>.
    /// </summary>
    public static float[,] ResizeArray(float[,] data, int targetWidth)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);

        var height = data.GetLength(0);
        var width = data.GetLength(1);

        // Step 1. The same instance, not a copy: nothing in the pipeline mutates what it hands in
        // or what it gets back.
        if (width <= targetWidth)
        {
            return data;
        }

        // Step 2. The integer box prefilter band-limits the input below the output Nyquist
        // frequency, which is what stops the stretch from amplifying aliasing on periodic patterns
        // and noise. It is not an optimization and it is not optional.
        var prefilter = PrefilterFactor(width, targetWidth);
        if (prefilter >= 2)
        {
            data = MeanPool(data, prefilter);
            height = data.GetLength(0);
            width = data.GetLength(1);

            // Step 3. Checked again against the pooled width. Skipping this re-resamples data that
            // is already at the target.
            if (width <= targetWidth)
            {
                return data;
            }
        }

        // Step 4. int(h * ratio), truncating rather than rounding. A very wide short frame
        // truncates to 0, and SkiaSharp refuses a zero-height bitmap.
        var newHeight = (int)(height * ((double)targetWidth / width));
        if (newHeight < 1)
        {
            newHeight = 1;
        }

        return Resample(data, height, width, targetWidth, newHeight);
    }

    private static float[,] Resample(float[,] data, int height, int width, int targetWidth, int targetHeight)
    {
        var sourceInfo = new SKImageInfo(width, height, SKColorType.RgbaF32, SKAlphaType.Unpremul);
        using var source = new SKBitmap(sourceInfo);
        var pixels = source.GetPixels();
        if (pixels == IntPtr.Zero)
        {
            return data;
        }

        // One float channel replicated into R, G and B with A = 1. Skia has no single-component
        // 32-bit float colour type, and leaving G, B and A at zero would make the Mitchell kernel
        // operate on a vector whose other components are a constant that means nothing. The read
        // back takes R.
        var rowBuffer = new byte[width * BytesPerPixel];
        var rowFloats = MemoryMarshal.Cast<byte, float>(rowBuffer.AsSpan());
        var sourceRowBytes = source.RowBytes;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = data[y, x];
                var offset = x * ComponentsPerPixel;
                rowFloats[offset] = value;
                rowFloats[offset + 1] = value;
                rowFloats[offset + 2] = value;
                rowFloats[offset + 3] = 1f;
            }

            // (nint) before the multiply, not after: int * int overflows at 2 GB of pixel data,
            // which is a 134 megapixel source, and a 151 megapixel mono preview at native
            // resolution (general.preview_resolution 0) is inside the range this port supports.
            Marshal.Copy(rowBuffer, 0, pixels + ((nint)y * sourceRowBytes), rowBuffer.Length);
        }

        var targetInfo = new SKImageInfo(targetWidth, targetHeight, SKColorType.RgbaF32, SKAlphaType.Unpremul);
        using var resized = source.Resize(targetInfo, new SKSamplingOptions(SKCubicResampler.Mitchell));

        // Resize returns null when Skia refuses the allocation. Carrying on with the larger image
        // beats throwing out of a thumbnail render, and Core has no logger to record it.
        if (resized is null)
        {
            return data;
        }

        var result = new float[targetHeight, targetWidth];
        var resizedPixels = resized.GetPixels();
        if (resizedPixels == IntPtr.Zero)
        {
            return data;
        }

        var resizedRowBytes = resized.RowBytes;
        var readBuffer = new byte[targetWidth * BytesPerPixel];
        var readFloats = MemoryMarshal.Cast<byte, float>(readBuffer.AsSpan());
        for (var y = 0; y < targetHeight; y++)
        {
            // Widened before the multiply for the same reason as the write loop above.
            Marshal.Copy(resizedPixels + ((nint)y * resizedRowBytes), readBuffer, 0, readBuffer.Length);
            for (var x = 0; x < targetWidth; x++)
            {
                result[y, x] = readFloats[x * ComponentsPerPixel];
            }
        }

        return result;
    }
}
