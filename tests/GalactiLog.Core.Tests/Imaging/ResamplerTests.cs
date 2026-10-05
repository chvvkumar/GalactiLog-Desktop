using GalactiLog.Core.Imaging;
using Xunit;

namespace GalactiLog.Core.Tests.Imaging;

/// <summary>
/// Spec 18.1's Resampling row. Every assertion here is about shape: prefilter and bin factor
/// selection, output dimensions, preserved aspect ratio, a non-empty result, and brightness
/// monotonic after the stretch.
/// </summary>
/// <remarks>
/// There is deliberately no exactness assertion against any reference resampler, and none may be
/// added. Spec 11.2 records the resampler as Mitchell cubic rather than PIL's Lanczos, a knowing
/// divergence from the web application, and states the consequence: "there is no exactness
/// assertion on resampling output". Spec 18.1's Resampling row repeats it. A later contributor who
/// wants byte agreement with PIL would have to hand-write a Lanczos kernel, which spec 11.2
/// forbids.
/// </remarks>
public class ResamplerTests
{
    private static float[,] Ramp(int height, int width)
    {
        var data = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = (float)(((x + (y * 0.5)) % (width + 1.0)) / (width + 1.0));
            }
        }

        return data;
    }

    [Fact]
    public void PrefilterFactor_WidthBelowTarget_IsZeroOrOne()
    {
        Assert.True(Resampler.PrefilterFactor(400, 800) < 2);
        Assert.True(Resampler.PrefilterFactor(800, 800) < 2);
        Assert.True(Resampler.PrefilterFactor(1599, 800) < 2);
    }

    [Fact]
    public void PrefilterFactor_ExactlyTwiceTheTarget_IsTwo()
    {
        Assert.Equal(2, Resampler.PrefilterFactor(1600, 800));
    }

    [Fact]
    public void PrefilterFactor_IsIntegerDivision()
    {
        Assert.Equal(2, Resampler.PrefilterFactor(1999, 800));
        Assert.Equal(7, Resampler.PrefilterFactor(6000, 800));
    }

    [Fact]
    public void BinStep_SmallFrame_IsOne()
    {
        Assert.Equal(1, Resampler.BinStep(600, 1000, 800));
        Assert.Equal(1, Resampler.BinStep(4, 4, 800));
    }

    [Fact]
    public void BinStep_LargeFrame_LeavesRoughlyTwiceTheTargetWidth()
    {
        // 6000 by 4000 at target 800 gives 3, leaving 2000 across, roughly twice the target.
        var step = Resampler.BinStep(4000, 6000, 800);
        Assert.Equal(3, step);
        Assert.InRange(6000 / step, 800 * 2, 800 * 3);
    }

    [Fact]
    public void BinStep_UsesTheLargerDimension()
    {
        // A tall narrow frame bins on its height, not on its width.
        Assert.Equal(3, Resampler.BinStep(6000, 1000, 800));
    }

    [Fact]
    public void MeanPool_AveragesEachBlock()
    {
        var input = new float[,]
        {
            { 1f, 3f, 5f, 7f },
            { 3f, 5f, 7f, 9f },
            { 0f, 0f, 2f, 2f },
            { 4f, 4f, 6f, 6f },
        };

        var pooled = Resampler.MeanPool(input, 2);

        Assert.Equal(2, pooled.GetLength(0));
        Assert.Equal(2, pooled.GetLength(1));
        Assert.Equal(3f, pooled[0, 0]);
        Assert.Equal(7f, pooled[0, 1]);
        Assert.Equal(2f, pooled[1, 0]);
        Assert.Equal(4f, pooled[1, 1]);
    }

    [Fact]
    public void MeanPool_CropsToAMultipleOfTheFactor()
    {
        var pooled = Resampler.MeanPool(Ramp(7, 7), 2);
        Assert.Equal(3, pooled.GetLength(0));
        Assert.Equal(3, pooled.GetLength(1));
    }

    [Fact]
    public void MeanPool_FactorOne_ReturnsTheInputUnchanged()
    {
        var input = Ramp(4, 4);
        Assert.Same(input, Resampler.MeanPool(input, 1));
        Assert.Same(input, Resampler.MeanPool(input, 0));
    }

    [Fact]
    public void ReadBinned_SmallFrame_ReturnsTheInputUnchanged()
    {
        var input = Ramp(600, 1000);
        Assert.Same(input, Resampler.ReadBinned(input, 800));
    }

    [Fact]
    public void ReadBinned_LargeFrame_ReducesTheLargerDimension()
    {
        var binned = Resampler.ReadBinned(Ramp(400, 6000), 800);

        // The larger dimension is 6000, so step = 6000 / (800 * 2) = 3.
        Assert.Equal(133, binned.GetLength(0));
        Assert.Equal(2000, binned.GetLength(1));
    }

    [Fact]
    public void ResizeArray_AlreadyAtOrBelowTarget_ReturnsTheInputUnchanged()
    {
        var atTarget = Ramp(100, 800);
        Assert.Same(atTarget, Resampler.ResizeArray(atTarget, 800));

        var belowTarget = Ramp(100, 400);
        Assert.Same(belowTarget, Resampler.ResizeArray(belowTarget, 800));
    }

    [Fact]
    public void ResizeArray_OutputWidthIsExactlyTheTarget()
    {
        var resized = Resampler.ResizeArray(Ramp(600, 1000), 800);
        Assert.Equal(800, resized.GetLength(1));
    }

    [Fact]
    public void ResizeArray_PreservesAspectRatioWithinOnePixel()
    {
        var resized = Resampler.ResizeArray(Ramp(600, 1000), 800);
        var expectedHeight = 600 * (800.0 / 1000.0);
        Assert.True(Math.Abs(resized.GetLength(0) - expectedHeight) <= 1.0, $"height={resized.GetLength(0)}");
    }

    [Fact]
    public void ResizeArray_ResultIsNonEmpty()
    {
        var resized = Resampler.ResizeArray(Ramp(600, 1000), 800);
        Assert.True(resized.GetLength(0) > 0);
        Assert.True(resized.GetLength(1) > 0);

        var nonZero = false;
        foreach (var value in resized)
        {
            if (value > 0f)
            {
                nonZero = true;
                break;
            }
        }

        Assert.True(nonZero);
    }

    [Fact]
    public void ResizeArray_AppliesThePrefilterBeforeTheResample()
    {
        // 3200 wide at target 800: the prefilter is 4, the pooled intermediate is exactly 800 wide,
        // and step 3's second early return fires, so the result is the mean-pooled buffer with no
        // Skia resample at all.
        var input = Ramp(200, 3200);
        var resized = Resampler.ResizeArray(input, 800);
        var pooled = Resampler.MeanPool(input, 4);

        Assert.Equal(pooled.GetLength(0), resized.GetLength(0));
        Assert.Equal(pooled.GetLength(1), resized.GetLength(1));
        for (var y = 0; y < pooled.GetLength(0); y++)
        {
            for (var x = 0; x < pooled.GetLength(1); x++)
            {
                Assert.Equal(pooled[y, x], resized[y, x]);
            }
        }
    }

    [Fact]
    public void ResizeArray_PrefilterLeavesAFractionalRemainder_ResamplesToTarget()
    {
        // 1999 wide at target 800: the prefilter is 2, which leaves 999, which then resamples to
        // exactly 800.
        var resized = Resampler.ResizeArray(Ramp(100, 1999), 800);
        Assert.Equal(800, resized.GetLength(1));
        Assert.Equal(40, resized.GetLength(0));
    }

    [Fact]
    public void ResizeArray_VeryWideShortFrame_KeepsAtLeastOneRow()
    {
        // 2 by 2000 at target 800: the prefilter of 2 pools to 1 by 1000, and the resample's
        // truncated height would be 0.
        var resized = Resampler.ResizeArray(Ramp(2, 2000), 800);
        Assert.Equal(1, resized.GetLength(0));
        Assert.Equal(800, resized.GetLength(1));
    }

    [Fact]
    public void ResizeArray_BrightnessIsMonotonic()
    {
        // Highlights only, for the reason given in MtfStretchTests: a uniform lift moves the median
        // the stretch re-centres on, so it leaves no headroom. Here the top quartile of a
        // column ramp is pushed halfway to white, which leaves the median and the MAD of both
        // frames effectively unchanged and moves only the bright end. Measured headroom is about
        // 8 bytes of mean after the resample and the stretch (82.05 against 89.83); the
        // assertion demands 5.
        var dim = new float[600, 1000];
        var bright = new float[600, 1000];
        for (var y = 0; y < 600; y++)
        {
            for (var x = 0; x < 1000; x++)
            {
                var value = x / 1000f;
                dim[y, x] = value;
                bright[y, x] = value > 0.75f ? (value + 1f) / 2f : value;
            }
        }

        var dimMean = Mean(MtfStretch.StretchChannel(Resampler.ResizeArray(dim, 800)));
        var brightMean = Mean(MtfStretch.StretchChannel(Resampler.ResizeArray(bright, 800)));

        Assert.True(brightMean >= dimMean + 5.0, $"dim={dimMean} bright={brightMean}");
    }

    private static double Mean(byte[,] data)
    {
        long sum = 0;
        foreach (var value in data)
        {
            sum += value;
        }

        return sum / (double)data.Length;
    }
}
