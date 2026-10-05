using System.Globalization;
using GalactiLog.Core.Survey;
using Xunit;

namespace GalactiLog.Core.Tests.Survey;

// Spec 11.3's field rule, the pan and zoom arithmetic, the query and the cache key (unit A cases 1
// to 8). Every case is a pure function of its inputs; no request is made.
public class SurveyViewTests
{
    private const double Tolerance = 1e-9;

    // Case 1. A failure is an unclamped field, or the web's zero field for a zero catalogued size.
    [Theory]
    [InlineData(60.0, 1.5)]
    [InlineData(1.0, 0.1)]
    [InlineData(600.0, 5.0)]
    [InlineData(null, 0.5)]
    [InlineData(0.0, 0.5)]
    [InlineData(-3.0, 0.5)]
    [InlineData(double.NaN, 0.5)]
    [InlineData(double.PositiveInfinity, 0.5)]
    public void DefaultField_ClampsAndReadsUnusableSizesAsUnknown(double? sizeMajor, double expected)
    {
        Assert.Equal(expected, SurveyView.DefaultField(sizeMajor), Tolerance);
    }

    // Case 2. A failure is a zoom past either clamp.
    [Fact]
    public void Zoomed_StepsByOnePointFiveInsideTheClamp()
    {
        var view = new SurveyView(Surveys.DefaultId, 10, 20, 1.5);

        Assert.Equal(1.0, view.Zoomed(1).Fov, Tolerance);
        Assert.Equal(2.25, view.Zoomed(-1).Fov, Tolerance);

        var inward = view;
        var outward = view;
        for (var i = 0; i < 20; i++)
        {
            inward = inward.Zoomed(1);
            outward = outward.Zoomed(-1);
        }

        Assert.Equal(SurveyView.MinField, inward.Fov, Tolerance);
        Assert.Equal(SurveyView.MaxField, outward.Fov, Tolerance);
    }

    // Case 3. A failure is a pan that drifts at high declination, or a centre off the sphere.
    [Fact]
    public void Panned_ScalesRaByDeclinationClampsDecAndWrapsRa()
    {
        var equator = new SurveyView(Surveys.DefaultId, 100, 0, 1);
        Assert.Equal(100.5, equator.Panned(0.5, 0).Ra, Tolerance);
        Assert.Equal(0.5, equator.Panned(0, 0.5).Dec, Tolerance);

        var high = new SurveyView(Surveys.DefaultId, 100, 60, 1);
        Assert.Equal(101.0, high.Panned(0.5, 0).Ra, 1e-6);

        var nearPole = new SurveyView(Surveys.DefaultId, 100, 89.8, 1);
        Assert.Equal(90.0, nearPole.Panned(0, 0.5).Dec, Tolerance);

        var nearWrap = new SurveyView(Surveys.DefaultId, 359.8, 0, 1);
        Assert.Equal(0.2, nearWrap.Panned(0.4, 0).Ra, 1e-6);
    }

    // A failure is a pan at the pole divided by cos 90 degrees, about 6e-17, flinging RA anywhere.
    [Fact]
    public void Panned_AtThePoleDividesByTheCosineFloor()
    {
        var pole = new SurveyView(Surveys.DefaultId, 100, 90, 1);

        Assert.Equal(150.0, pole.Panned(0.5, 0).Ra, 1e-6);
    }

    // Case 4. A failure is a missing, extra or reordered parameter.
    [Fact]
    public void QueryString_IsTheGoldenStringForM31()
    {
        var m31 = SurveyView.Initial(
            new SurveyTarget(Guid.NewGuid(), "M 31", 10.684708, 41.26917, 190), Surveys.DefaultId);

        Assert.Equal(
            "hips=P%2FDSS2%2Fcolor&ra=10.6847&dec=41.2692&fov=4.7500&width=1024&height=1024&projection=TAN&format=jpg",
            m31.QueryString());
    }

    // Case 5. A failure is a decimal comma, which would break the query under a German culture.
    [Fact]
    public void QueryString_UsesAPointUnderAGermanCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var query = new SurveyView(Surveys.DefaultId, 10.5, -20.25, 0.75).QueryString();

            Assert.Contains("ra=10.5000&dec=-20.2500&fov=0.7500", query);
            Assert.DoesNotContain(",", query);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    // Case 6. A failure is 360.0000 or a declination outside -90 to 90 on the wire.
    [Fact]
    public void QueryString_WrapsRaAfterRoundingAndClampsDec()
    {
        var query = new SurveyView(Surveys.DefaultId, 359.99996, -95, 1).QueryString();

        Assert.Contains("ra=0.0000&dec=-90.0000&", query);
    }

    // Case 7. A failure is two views sharing one cached file.
    [Fact]
    public void CacheKey_IsLowercaseHexAndDistinguishesEveryValue()
    {
        var view = new SurveyView(Surveys.DefaultId, 10, 20, 1);
        var key = view.CacheKey();

        Assert.Matches("^[0-9a-f]{32}$", key);
        Assert.Equal(key, new SurveyView(Surveys.DefaultId, 10, 20, 1).CacheKey());
        Assert.NotEqual(key, (view with { SurveyId = "P/DSS2/red" }).CacheKey());
        Assert.NotEqual(key, (view with { Ra = 10.001 }).CacheKey());
        Assert.NotEqual(key, (view with { Dec = 20.001 }).CacheKey());
        Assert.NotEqual(key, (view with { Fov = 1.001 }).CacheKey());
    }

    // A failure is a key over anything but spec 11.3's raw hips|ra|dec|fov|1024 text.
    [Fact]
    public void CacheKey_IsTheGoldenKeyForAFixedView()
    {
        Assert.Equal(
            "36bfe1678013b0184001638f7f5b15ad",
            new SurveyView(Surveys.DefaultId, 10, 20, 1).CacheKey());
    }
}
