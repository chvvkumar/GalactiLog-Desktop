using GalactiLog.Core.Targets;
using Xunit;

namespace GalactiLog.Core.Tests.Targets;

// FIXER LIST item 4: the five solar-system categories of spec 9.8 have pills on the dashboard and,
// until this type existed, no producer. Every case below is a name no catalogue can resolve, which
// is the only situation in which TargetResolver consults this classifier at all.
public class SolarSystemNamesTests
{
    [Theory]
    [InlineData("Sun")]
    [InlineData("Sol")]
    [InlineData("The Sun")]
    public void Classify_Sun_And_Sol(string name)
    {
        Assert.Equal(("Sun", "Sun"), SolarSystemNames.Classify(name));
    }

    [Theory]
    [InlineData("Moon")]
    [InlineData("Luna")]
    [InlineData("The Moon")]
    public void Classify_Moon_And_Luna(string name)
    {
        Assert.Equal(("Moon", "Moon"), SolarSystemNames.Classify(name));
    }

    [Theory]
    [InlineData("Mercury")]
    [InlineData("Venus")]
    [InlineData("Mars")]
    [InlineData("Jupiter")]
    [InlineData("Saturn")]
    [InlineData("Uranus")]
    [InlineData("Neptune")]
    // Spec 9.8 has five solar-system categories and no dwarf-planet one, so Pluto is a Planet.
    [InlineData("Pluto")]
    public void Classify_EveryPlanetName(string name)
    {
        Assert.Equal(("Planet", name), SolarSystemNames.Classify(name));
    }

    [Fact]
    public void Classify_IsCaseInsensitive()
    {
        Assert.Equal(("Planet", "Jupiter"), SolarSystemNames.Classify("jupiter"));
        Assert.Equal(("Planet", "Jupiter"), SolarSystemNames.Classify("JUPITER"));
        Assert.Equal(("Sun", "Sun"), SolarSystemNames.Classify("sOl"));
        Assert.Equal(("Asteroid", "vesta"), SolarSystemNames.Classify("vesta"));
    }

    [Fact]
    public void Classify_CollapsesWhitespace()
    {
        Assert.Equal(("Sun", "Sun"), SolarSystemNames.Classify("  The   Sun "));
        // The comet and asteroid rows carry the collapsed name through as the display name, so
        // the collapse is visible in the result rather than only in the match.
        Assert.Equal(("Asteroid", "4 Vesta"), SolarSystemNames.Classify("4   Vesta"));
    }

    [Fact]
    public void Classify_StripsAPanelSuffix()
    {
        Assert.Equal(("Moon", "Moon"), SolarSystemNames.Classify("Moon Panel 2"));
        Assert.Equal(("Comet", "C/2023 A3"), SolarSystemNames.Classify("C/2023 A3 Panel 3"));
    }

    [Theory]
    [InlineData("C/2023 A3")]
    [InlineData("P/2010 A2")]
    [InlineData("1P/Halley")]
    [InlineData("73P/Schwassmann")]
    public void Classify_CometDesignationWithASlash(string name)
    {
        Assert.Equal(("Comet", name), SolarSystemNames.Classify(name));
    }

    [Fact]
    public void Classify_BareNumberedPeriodicComet()
    {
        Assert.Equal(("Comet", "73P"), SolarSystemNames.Classify("73P"));
    }

    [Theory]
    [InlineData("Ceres")]
    [InlineData("Pallas")]
    [InlineData("Juno")]
    [InlineData("Vesta")]
    public void Classify_NamedAsteroids(string name)
    {
        Assert.Equal(("Asteroid", name), SolarSystemNames.Classify(name));
    }

    [Theory]
    [InlineData("4 Vesta")]
    [InlineData("(4) Vesta")]
    [InlineData("243 Ida")]
    public void Classify_NumberedMinorPlanet(string name)
    {
        Assert.Equal(("Asteroid", name), SolarSystemNames.Classify(name));
    }

    [Fact]
    public void Classify_BareNumber_IsNotAnAsteroid()
    {
        // The numbered pattern requires a name after the number, so an unquoted numeric OBJECT
        // card stays unresolved rather than becoming a minor planet.
        Assert.Null(SolarSystemNames.Classify("7331"));
    }

    [Theory]
    [InlineData("M 31")]
    [InlineData("NGC 7331")]
    [InlineData("IC 1396")]
    [InlineData("Sh2-155")]
    [InlineData("Abell 21")]
    // "47 Tuc" has the shape of a numbered minor planet. The second token is an IAU
    // constellation abbreviation, which a minor planet's name never is.
    [InlineData("47 Tuc")]
    public void Classify_CatalogueDesignations_MatchNothing(string name)
    {
        Assert.Null(SolarSystemNames.Classify(name));
    }

    [Fact]
    public void Classify_MoonInsideALongerName_MatchesNothing()
    {
        Assert.Null(SolarSystemNames.Classify("Half Moon Nebula"));
        Assert.Null(SolarSystemNames.Classify("Sunflower Galaxy"));
    }

    [Fact]
    public void Classify_CometPatternNotAtTheStart_MatchesNothing()
    {
        Assert.Null(SolarSystemNames.Classify("NGC 1P"));
        Assert.Null(SolarSystemNames.Classify("Barnard C/2023"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_EmptyOrWhitespace_ReturnsNull(string name)
    {
        Assert.Null(SolarSystemNames.Classify(name));
    }

    [Fact]
    public void Classify_EveryResultIsOneOfObjectTypeCategoriesSolarSystemCategories()
    {
        // The two lists cannot drift: a category this type produces that the dashboard has no
        // pill for would be invisible, and a pill with no producer is FIXER item 4 again.
        string[] names =
        [
            "Sun", "Sol", "The Sun", "Moon", "Luna", "The Moon",
            "Mercury", "Venus", "Mars", "Jupiter", "Saturn", "Uranus", "Neptune", "Pluto",
            "C/2023 A3", "P/2010 A2", "1P/Halley", "73P/Schwassmann", "73P",
            "Ceres", "Pallas", "Juno", "Vesta", "4 Vesta", "(4) Vesta", "243 Ida",
        ];

        foreach (var name in names)
        {
            var classified = SolarSystemNames.Classify(name);
            Assert.NotNull(classified);
            Assert.Contains(classified.Value.Category, ObjectTypeCategories.SolarSystemCategories);
        }
    }
}
