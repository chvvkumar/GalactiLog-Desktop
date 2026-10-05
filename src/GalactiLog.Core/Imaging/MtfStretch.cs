using System.Buffers;

namespace GalactiLog.Core.Imaging;

/// <summary>
/// Spec 11.2's N.I.N.A. and PixInsight auto-STF equivalent, a port of
/// <c>backend/app/services/stretch.py</c>. Ported arithmetically exactly: a rewrite that looks
/// equivalent produces visibly different thumbnails, which is spec 11.2's own warning.
/// </summary>
public static class MtfStretch
{
    /// <summary>The stretch target the midtone solves for, from <c>stretch_channel</c>.</summary>
    /// <remarks>Internal rather than private so <c>MtfStretchTests</c> can bind its sweep to the
    /// production constant: the |denom| branch below is unreachable only while this is 0.25.</remarks>
    internal const double Target = 0.25;

    /// <summary>Midtones transfer function: <c>(m - 1) * x / ((2m - 1) * x - m)</c>.</summary>
    public static double Mtf(double x, double m) => (m - 1.0) * x / (((2.0 * m - 1.0) * x) - m);

    /// <summary>
    /// Per-channel normalization to [0, 1]. A channel whose max equals its min returns all zeros,
    /// matching <c>np.zeros_like</c>. Allocates a new buffer; the caller's input is never mutated,
    /// because the pipeline reuses the raw frame for other channels.
    /// </summary>
    public static float[,] NormalizeToUnit(float[,] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var height = data.GetLength(0);
        var width = data.GetLength(1);
        var result = new float[height, width];
        if (height == 0 || width == 0)
        {
            return result;
        }

        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double value = data[y, x];
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }
            }
        }

        if (!(max > min))
        {
            return result;
        }

        var range = max - min;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                result[y, x] = (float)((data[y, x] - min) / range);
            }
        }

        return result;
    }

    /// <summary>
    /// Spec 11.2's <c>stretchChannel</c>. Input already normalized to [0, 1]; output is a byte
    /// buffer of the same shape.
    /// </summary>
    public static byte[,] StretchChannel(float[,] normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);

        var height = normalized.GetLength(0);
        var width = normalized.GetLength(1);
        var result = new byte[height, width];
        var count = height * width;
        if (count == 0)
        {
            return result;
        }

        // One rented scratch buffer, used twice: once sorted for the median, once refilled with
        // the absolute deviations and sorted again for the MAD. A frame is megapixels, so a second
        // buffer would be a second megapixel allocation for no gain.
        var scratch = ArrayPool<float>.Shared.Rent(count);
        double median;
        double mad;
        try
        {
            var span = scratch.AsSpan(0, count);
            Copy(normalized, span);
            median = Median(span);

            var medianAsFloat = (float)median;
            var index = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    span[index++] = Math.Abs(normalized[y, x] - medianAsFloat);
                }
            }

            mad = Median(span);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scratch);
        }

        var midtone = 0.5;
        var shadows = 0.0;

        if (mad > 0)
        {
            shadows = median - (2.8 * mad);
            if (shadows < 0.0)
            {
                shadows = 0.0;
            }

            if (shadows >= 1.0)
            {
                // Spec 11.2 trap 1: this resets shadows to 0, it does not clamp to 1. A clamp
                // would make scale 0, take the scale <= 0 branch, and produce a different image.
                shadows = 0.0;
            }

            var shadowScale = 1.0 - shadows;
            if (shadowScale <= 0)
            {
                shadowScale = 1.0;
            }

            var medianNorm = Math.Clamp((median - shadows) / shadowScale, 1e-6, 1.0 - 1e-6);

            var denom = (2.0 * Target * medianNorm) - Target - medianNorm;
            if (Math.Abs(denom) > 1e-10)
            {
                midtone = Math.Clamp(medianNorm * (Target - 1.0) / denom, 0.001, 0.999);
            }
            else
            {
                // Unreachable while Target is 0.25: denom reduces to -(0.5 * medianNorm + 0.25),
                // whose magnitude is at least 0.25 across the clamped medianNorm range. Ported for
                // fidelity with stretch.py rather than pruned.
                midtone = 0.5;
            }
        }

        var scale = 1.0 - shadows;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        // Spec 11.2 trap 2: the mad == 0 return to a flat 128 comes AFTER the shadows and midtone
        // block, not before, and it returns a uniform mid grey rather than a black frame. A
        // perfectly flat frame is a calibration artefact, not a black sky. The spec materializes
        // the clamped `normed` array just above this check; that per-pixel work is fused into the
        // conversion loop below because it is never read when mad is 0, and the branch order is
        // otherwise exactly the spec's.
        if (mad == 0.0)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    result[y, x] = 128;
                }
            }

            return result;
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var normed = Math.Clamp((normalized[y, x] - shadows) / scale, 0.0, 1.0);
                var stretched = Mtf(normed, midtone);

                // The NaN case is unreachable: the midtone clamp to [0.001, 0.999] and the normed
                // clamp to [0, 1] together keep the denominator away from zero. The guard is here
                // so a future change to either clamp cannot emit a garbage byte silently.
                if (!double.IsFinite(stretched) || stretched < 0.0)
                {
                    stretched = 0.0;
                }
                else if (stretched > 1.0)
                {
                    // mtf(1, m) can land a few ulps above 1, and a double above 255 has an
                    // unspecified byte conversion in C#.
                    stretched = 1.0;
                }

                // NumPy's astype(np.uint8): truncation toward zero, so 254.9 renders 254.
                // Convert.ToByte rounds and is wrong here.
                result[y, x] = (byte)(stretched * 255.0);
            }
        }

        return result;
    }

    private static void Copy(float[,] source, Span<float> destination)
    {
        var height = source.GetLength(0);
        var width = source.GetLength(1);
        var index = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                destination[index++] = source[y, x];
            }
        }
    }

    /// <summary>
    /// <c>np.median</c>: the mean of the two middle elements for an even-length input, not the
    /// lower one. Sorts <paramref name="values"/> in place.
    /// </summary>
    private static double Median(Span<float> values)
    {
        values.Sort();
        var middle = values.Length / 2;
        return (values.Length & 1) == 1
            ? values[middle]
            : ((double)values[middle - 1] + values[middle]) / 2.0;
    }
}
