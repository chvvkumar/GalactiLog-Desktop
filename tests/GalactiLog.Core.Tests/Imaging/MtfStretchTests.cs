using System.Globalization;
using GalactiLog.Core.Imaging;
using Xunit;

namespace GalactiLog.Core.Tests.Imaging;

/// <summary>
/// Spec 18.1's Stretch row. This is the one place in Phase 8 that asserts arithmetic exactness:
/// spec 11.2 requires the midtones transfer function to be ported exactly, because a rewrite that
/// looks equivalent produces visibly different thumbnails.
/// </summary>
public class MtfStretchTests
{
    private static byte[] Flatten(byte[,] data)
    {
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        var flat = new byte[height * width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                flat[(y * width) + x] = data[y, x];
            }
        }

        return flat;
    }

    private static float[] Flatten(float[,] data)
    {
        var height = data.GetLength(0);
        var width = data.GetLength(1);
        var flat = new float[height * width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                flat[(y * width) + x] = data[y, x];
            }
        }

        return flat;
    }

    [Fact]
    public void Mtf_AtZero_IsZero()
    {
        foreach (var m in new[] { 0.001, 0.1, 0.25, 0.5, 0.75, 0.999 })
        {
            Assert.Equal(0.0, MtfStretch.Mtf(0.0, m), 12);
        }
    }

    [Fact]
    public void Mtf_AtOne_IsOne()
    {
        foreach (var m in new[] { 0.001, 0.1, 0.25, 0.5, 0.75, 0.999 })
        {
            Assert.Equal(1.0, MtfStretch.Mtf(1.0, m), 12);
        }
    }

    [Fact]
    public void Mtf_AtTheMidtone_IsAHalf()
    {
        foreach (var m in new[] { 0.001, 0.1, 0.25, 0.4, 0.75, 0.999 })
        {
            Assert.Equal(0.5, MtfStretch.Mtf(m, m), 12);
        }
    }

    [Fact]
    public void Mtf_WithMidtoneOneHalf_IsTheIdentity()
    {
        foreach (var x in new[] { 0.0, 0.125, 0.25, 0.5, 0.75, 0.875, 1.0 })
        {
            Assert.Equal(x, MtfStretch.Mtf(x, 0.5), 12);
        }
    }

    [Fact]
    public void Mtf_IsMonotonicIncreasingOnTheUnitInterval()
    {
        foreach (var m in new[] { 0.001, 0.1, 0.25, 0.5, 0.75, 0.999 })
        {
            var previous = double.NegativeInfinity;
            for (var i = 0; i <= 1000; i++)
            {
                var value = MtfStretch.Mtf(i / 1000.0, m);
                Assert.True(value > previous, $"m={m} i={i} value={value} previous={previous}");
                previous = value;
            }
        }
    }

    [Fact]
    public void Mtf_KnownPoints_MatchTheFormula()
    {
        // Each expected value is (m - 1) * x / ((2m - 1) * x - m) worked by hand.
        (double X, double M, double Expected)[] points =
        [
            (0.5, 0.25, 0.75),
            (0.75, 0.25, 0.9),
            (0.5, 0.75, 0.25),
            (0.2, 0.1, 9.0 / 13.0),
            (0.1, 0.9, 1.0 / 82.0),
        ];

        foreach (var (x, m, expected) in points)
        {
            Assert.Equal(expected, MtfStretch.Mtf(x, m), 12);
        }
    }

    [Fact]
    public void NormalizeToUnit_MapsMinToZeroAndMaxToOne()
    {
        var result = MtfStretch.NormalizeToUnit(new float[,] { { 2f, 4f }, { 6f, 10f } });
        Assert.Equal([0f, 0.25f, 0.5f, 1f], Flatten(result));
    }

    [Fact]
    public void NormalizeToUnit_FlatInput_IsAllZeros()
    {
        var result = MtfStretch.NormalizeToUnit(new float[,] { { 7f, 7f }, { 7f, 7f } });
        Assert.Equal([0f, 0f, 0f, 0f], Flatten(result));
    }

    [Fact]
    public void NormalizeToUnit_NegativeValues_AreMappedIntoTheUnitRange()
    {
        // A calibrated frame carries negative values after dark subtraction.
        var result = MtfStretch.NormalizeToUnit(new float[,] { { -4f, -2f, 0f, 4f } });
        Assert.Equal([0f, 0.25f, 0.5f, 1f], Flatten(result));
    }

    [Fact]
    public void NormalizeToUnit_DoesNotMutateTheInput()
    {
        var input = new float[,] { { 2f, 4f }, { 6f, 10f } };
        MtfStretch.NormalizeToUnit(input);
        Assert.Equal([2f, 4f, 6f, 10f], Flatten(input));
    }

    [Fact]
    public void StretchChannel_FlatInput_ReturnsUniform128()
    {
        var result = MtfStretch.StretchChannel(new float[,] { { 0.3f, 0.3f }, { 0.3f, 0.3f } });
        Assert.Equal<byte>([128, 128, 128, 128], Flatten(result));
    }

    [Fact]
    public void StretchChannel_FlatInputAtOne_ReturnsUniform128()
    {
        // The flat return is keyed on mad, not on the data value: a flat frame at 1.0 is still
        // mid grey, not white.
        var result = MtfStretch.StretchChannel(new float[,] { { 1f, 1f }, { 1f, 1f } });
        Assert.Equal<byte>([128, 128, 128, 128], Flatten(result));
    }

    [Fact]
    public void StretchChannel_MadZeroReturnIsTakenAfterTheShadowsComputation()
    {
        // Spec 11.2 places the mad == 0 return after the shadows and midtone block. A median above
        // 1.0 reaches that return only through the computed path, and the answer is still the
        // uniform mid grey rather than a saturated or black frame.
        var result = MtfStretch.StretchChannel(new float[,] { { 5f, 5f, 5f, 5f } });
        Assert.Equal<byte>([128, 128, 128, 128], Flatten(result));
    }

    [Fact]
    public void StretchChannel_ShadowsAtOrAboveOne_ResetsShadowsToZero()
    {
        // median = 1.28125, mad = 0.03125, so shadows = 1.19375 >= 1.0 and resets to 0.
        // With shadows = 0 the medianNorm clamp pins midtone at 0.999 and every value at or above
        // 1.0 saturates. A clamp to 1.0 instead of a reset would give scale = 0, take the
        // scale <= 0 branch, land on midtone 0.54 and render the 1.25 samples around 56.
        var input = new float[,] { { 0.25f, 1.25f, 1.25f, 1.28125f, 1.28125f, 1.3125f, 1.3125f, 1.5f } };
        Assert.Equal<byte>([0, 255, 255, 255, 255, 255, 255, 255], Flatten(MtfStretch.StretchChannel(input)));
    }

    [Fact]
    public void StretchChannel_ShadowsBelowZero_ClampsToZero()
    {
        // median = 0.5, mad = 0.25, shadows = -0.2 and clamps to 0, so scale stays 1 and the
        // darkest sample stays black. An unclamped -0.2 would lift it off zero.
        var input = new float[,] { { 0f, 0.5f, 0.5f, 1f } };
        Assert.Equal<byte>([0, 63, 63, 255], Flatten(MtfStretch.StretchChannel(input)));
    }

    [Fact]
    public void StretchChannel_DenominatorNearZero_UsesMidtoneOneHalf()
    {
        // denom = 2 * target * medianNorm - target - medianNorm with target = 0.25 reduces to
        // -(0.5 * medianNorm + 0.25), whose magnitude is at least 0.25 across the whole clamped
        // medianNorm range. The |denom| <= 1e-10 branch is therefore unreachable in this port and
        // the midtone is never forced back to 0.5 by it. It is ported for fidelity with spec 11.2;
        // this test pins the reason it can never fire. The sweep reads the production constant, so
        // a change to MtfStretch.Target turns the branch reachable here rather than silently.
        const double target = MtfStretch.Target;
        Assert.Equal(0.25, target);
        for (var i = 0; i <= 1000; i++)
        {
            var medianNorm = Math.Clamp(i / 1000.0, 1e-6, 1.0 - 1e-6);
            var denom = (2.0 * target * medianNorm) - target - medianNorm;
            Assert.True(Math.Abs(denom) > 1e-10, $"medianNorm={medianNorm} denom={denom}");
        }
    }

    [Fact]
    public void StretchChannel_MidtoneIsClampedToTheSpecRange()
    {
        // Lower end: medianNorm 1.220703125e-4 gives an unclamped midtone of 3.66e-4, which clamps
        // to 0.001. At 0.001 the two mid samples render 27; the unclamped value would render 63.
        var low = new float[,] { { 0f, 0.0001220703125f, 0.0001220703125f, 1f } };
        Assert.Equal<byte>([0, 27, 27, 255], Flatten(MtfStretch.StretchChannel(low)));

        // Upper end: medianNorm clamps to 0.999999, giving an unclamped midtone of 0.9999996 that
        // clamps to 0.999. At 0.999 the 0.90625 sample renders 2; at 0.9999996 it would render 0.
        var high = new float[,] { { 0.90625f, 1.25f, 1.25f, 1.28125f, 1.28125f, 1.3125f, 1.3125f, 1.5f } };
        Assert.Equal<byte>([2, 255, 255, 255, 255, 255, 255, 255], Flatten(MtfStretch.StretchChannel(high)));
    }

    [Fact]
    public void StretchChannel_ByteConversionTruncates()
    {
        // mtf(0.5, 0.75) is exactly 0.25, so the byte is 0.25 * 255 = 63.75. NumPy's
        // astype(np.uint8) truncates toward zero and renders 63; Math.Round would render 64.
        var input = new float[,] { { 0f, 0.5f, 0.5f, 1f } };
        Assert.Equal(63, MtfStretch.StretchChannel(input)[0, 1]);
    }

    [Fact]
    public void StretchChannel_OutputHasTheSameShapeAsTheInput()
    {
        var result = MtfStretch.StretchChannel(new float[7, 3]);
        Assert.Equal(7, result.GetLength(0));
        Assert.Equal(3, result.GetLength(1));
    }

    private static float[,] SyntheticStarField()
    {
        const int size = 256;
        var random = new Random(12345);
        var field = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                field[y, x] = (float)(0.08 + (random.NextDouble() * 0.02));
            }
        }

        // The profile is a compactly supported quartic, amplitude * (1 - r2 / radius2) squared,
        // not a Gaussian. Math.Exp is not required to be correctly rounded and is not guaranteed
        // bit-identical across .NET runtimes, architectures or versions, which would make the
        // captured reference below drift for reasons that have nothing to do with the stretch.
        // Every operation here is an IEEE 754 add, subtract, multiply or divide on values that are
        // exact in binary, so it is correctly rounded and therefore reproducible everywhere.
        // Amplitudes and radii are powers-of-two fractions and small integers for the same reason.
        (int X, int Y, double Amplitude, int Radius)[] stars =
        [
            (40, 60, 0.875, 6),
            (200, 50, 0.5, 9),
            (128, 128, 1.0, 4),
            (30, 210, 0.4375, 8),
            (220, 200, 0.75, 6),
        ];

        foreach (var (starX, starY, amplitude, radius) in stars)
        {
            var radius2 = (double)(radius * radius);
            for (var dy = -12; dy <= 12; dy++)
            {
                for (var dx = -12; dx <= 12; dx++)
                {
                    var y = starY + dy;
                    var x = starX + dx;
                    if (y < 0 || y >= size || x < 0 || x >= size)
                    {
                        continue;
                    }

                    var falloff = 1.0 - ((double)((dx * dx) + (dy * dy)) / radius2);
                    if (falloff <= 0.0)
                    {
                        continue;
                    }

                    var value = field[y, x] + (amplitude * falloff * falloff);
                    field[y, x] = (float)Math.Min(1.0, value);
                }
            }
        }

        return field;
    }

    [Fact]
    public void StretchChannel_SyntheticStarField_MatchesCapturedReferenceOutput()
    {
        // Spec 18.2 forbids a checked-in fixture, so the field is generated from a fixed seed and
        // the reference is a handful of captured figures rather than a 65536 byte array.
        var stretched = MtfStretch.StretchChannel(SyntheticStarField());

        (int Y, int X)[] samples = [(0, 0), (60, 40), (50, 200), (128, 128), (210, 30), (200, 220), (255, 255)];
        long sum = 0;
        var bright = 0;
        foreach (var value in Flatten(stretched))
        {
            sum += value;
            if (value > 200)
            {
                bright++;
            }
        }

        var parts = new List<string>();
        foreach (var (y, x) in samples)
        {
            parts.Add(stretched[y, x].ToString(CultureInfo.InvariantCulture));
        }

        parts.Add((sum / (double)(256 * 256)).ToString("F4", CultureInfo.InvariantCulture));
        parts.Add(bright.ToString(CultureInfo.InvariantCulture));

        // Seven sampled bytes (one background corner, the five star centres, the far corner), then
        // the whole-frame mean to four places and the count of bytes above 200.
        Assert.Equal("28,254,245,255,243,252,56,62.3684,399", string.Join(",", parts));
    }

    [Fact]
    public void StretchChannel_BrighterInput_IsNotDarkerAfterStretch()
    {
        // Brightening the highlights only, not the whole frame. A uniform lift moves the median and
        // the MAD with it, and the stretch is adaptive: it re-centres on the new median and hands
        // back almost the same mean, so a uniform test has no headroom and passes or fails on
        // rounding. Lifting only the top quartile of a 64 step ramp leaves both middle samples and
        // both middle absolute deviations untouched, so median, MAD, shadows and midtone are
        // identical between the two frames and only the sampled values move. Measured headroom is
        // about 8 bytes of mean (81.34 against 89.45); the assertion demands 5.
        var dim = new float[1, 64];
        var bright = new float[1, 64];
        for (var i = 0; i < 64; i++)
        {
            var value = i / 64f;
            dim[0, i] = value;
            bright[0, i] = i >= 48 ? (value + 1f) / 2f : value;
        }

        var dimMean = Flatten(MtfStretch.StretchChannel(dim)).Average(b => (double)b);
        var brightMean = Flatten(MtfStretch.StretchChannel(bright)).Average(b => (double)b);

        Assert.True(brightMean >= dimMean + 5.0, $"dim={dimMean} bright={brightMean}");
    }

    [Fact]
    public void StretchChannel_DoesNotMutateTheInput()
    {
        var input = new float[,] { { 0f, 0.5f, 0.5f, 1f } };
        MtfStretch.StretchChannel(input);
        Assert.Equal([0f, 0.5f, 0.5f, 1f], Flatten(input));
    }
}
