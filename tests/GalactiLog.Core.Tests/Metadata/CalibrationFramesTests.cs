using GalactiLog.Core.Metadata;
using Xunit;

namespace GalactiLog.Core.Tests.Metadata;

public class CalibrationFramesTests
{
    [Theory]
    [InlineData("BIAS")]
    [InlineData("DARK")]
    [InlineData("FLAT")]
    [InlineData("DARKFLAT")]
    [InlineData("BIASFLAT")]
    [InlineData("bias")]
    [InlineData("Dark")]
    [InlineData("flAT")]
    public void IsCalibrationFrame_KnownTypesAnyCase_ReturnsTrue(string imageType)
    {
        Assert.True(CalibrationFrames.IsCalibrationFrame(imageType));
    }

    [Fact]
    public void IsCalibrationFrame_Light_ReturnsFalse()
    {
        Assert.False(CalibrationFrames.IsCalibrationFrame("LIGHT"));
    }

    [Fact]
    public void IsCalibrationFrame_Null_ReturnsFalse()
    {
        Assert.False(CalibrationFrames.IsCalibrationFrame(null));
    }

    [Fact]
    public void IsCalibrationFrame_Unrecognized_ReturnsFalse()
    {
        Assert.False(CalibrationFrames.IsCalibrationFrame("SNAPSHOT"));
    }
}
