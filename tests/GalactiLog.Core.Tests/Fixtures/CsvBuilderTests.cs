using System.Text;
using Xunit;

namespace GalactiLog.Core.Tests.Fixtures;

public class CsvBuilderTests
{
    [Fact]
    public void BuildImageMetaDataCsv_HeaderListsAllColumns_AndMissingKeysAreBlank()
    {
        var fullRow = new Dictionary<string, string?>
        {
            ["FilePath"] = @"C:\data\frame1.fits",
            ["ExposureStartUTC"] = "2024-01-01T00:00:00Z",
            ["HFR"] = "2.5",
            ["HFRStDev"] = "0.1",
            ["FWHM"] = "3.0",
            ["Eccentricity"] = "0.2",
            ["DetectedStars"] = "120",
            ["GuidingRMSArcSec"] = "0.5",
            ["GuidingRMSRAArcSec"] = "0.3",
            ["GuidingRMSDECArcSec"] = "0.4",
            ["ADUStDev"] = "10",
            ["ADUMean"] = "500",
            ["ADUMedian"] = "490",
            ["ADUMin"] = "100",
            ["ADUMax"] = "60000",
            ["FocuserPosition"] = "12345",
            ["FocuserTemp"] = "5.5",
            ["RotatorPosition"] = "90",
            ["PierSide"] = "East",
            ["Airmass"] = "1.2",
        };
        var sparseRow = new Dictionary<string, string?>
        {
            ["FilePath"] = @"C:\data\frame2.fits",
            ["ExposureStartUTC"] = "2024-01-01T00:05:00Z",
        };

        var stream = CsvBuilder.BuildImageMetaDataCsv(new IReadOnlyDictionary<string, string?>[] { fullRow, sparseRow }, withBom: false);
        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            "FilePath,ExposureStartUTC,HFR,HFRStDev,FWHM,Eccentricity,DetectedStars,GuidingRMSArcSec,GuidingRMSRAArcSec,GuidingRMSDECArcSec,ADUStDev,ADUMean,ADUMedian,ADUMin,ADUMax,FocuserPosition,FocuserTemp,RotatorPosition,PierSide,Airmass",
            lines[0]);

        var sparseCells = lines[2].Split(',');
        Assert.Equal(@"C:\data\frame2.fits", sparseCells[0]);
        Assert.Equal("2024-01-01T00:05:00Z", sparseCells[1]);
        for (var i = 2; i < sparseCells.Length; i++)
        {
            Assert.Equal(string.Empty, sparseCells[i]);
        }
    }

    [Fact]
    public void BuildImageMetaDataCsv_ZeroHfr_WritesLiteralZeroCell()
    {
        var row = new Dictionary<string, string?> { ["HFR"] = "0" };
        var stream = CsvBuilder.BuildImageMetaDataCsv(new IReadOnlyDictionary<string, string?>[] { row }, withBom: false);
        var dataLine = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)[1];

        Assert.Equal("0", dataLine.Split(',')[2]); // HFR is column index 2
    }

    [Fact]
    public void WithBom_TogglesLeadingBomBytes()
    {
        var withBom = CsvBuilder.BuildImageMetaDataCsv(Array.Empty<IReadOnlyDictionary<string, string?>>(), withBom: true).ToArray();
        var withoutBom = CsvBuilder.BuildImageMetaDataCsv(Array.Empty<IReadOnlyDictionary<string, string?>>(), withBom: false).ToArray();

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, withBom[..3]);
        Assert.False(withoutBom.Length >= 3 && withoutBom[0] == 0xEF && withoutBom[1] == 0xBB && withoutBom[2] == 0xBF);
    }

    [Fact]
    public void BuildWeatherDataCsv_HeaderMatchesCanonicalColumnOrder()
    {
        var stream = CsvBuilder.BuildWeatherDataCsv(Array.Empty<IReadOnlyDictionary<string, string?>>(), withBom: false);
        var header = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];

        Assert.Equal(
            "ExposureStartUTC,Temperature,DewPoint,Humidity,Pressure,WindSpeed,WindDirection,WindGust,CloudCover,SkyQuality",
            header);
    }
}
