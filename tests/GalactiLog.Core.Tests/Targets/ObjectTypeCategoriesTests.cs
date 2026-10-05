using GalactiLog.Core.Targets;
using Xunit;

namespace GalactiLog.Core.Tests.Targets;

public class ObjectTypeCategoriesTests
{
    // One assertion per row of the spec 9.8 table: every SIMBAD code across the nine
    // categories.
    [Theory]
    [InlineData("HII", "Emission Nebula")]
    [InlineData("sh", "Emission Nebula")]
    [InlineData("GNe", "Reflection Nebula")]
    [InlineData("RNe", "Reflection Nebula")]
    [InlineData("DNe", "Dark Nebula")]
    [InlineData("Cld", "Dark Nebula")]
    [InlineData("MoC", "Dark Nebula")]
    [InlineData("PN", "Planetary Nebula")]
    [InlineData("SNR", "Supernova Remnant")]
    [InlineData("G", "Galaxy")]
    [InlineData("H2G", "Galaxy")]
    [InlineData("GiG", "Galaxy")]
    [InlineData("GiC", "Galaxy")]
    [InlineData("GiP", "Galaxy")]
    [InlineData("rG", "Galaxy")]
    [InlineData("AGN", "Galaxy")]
    [InlineData("Sy2", "Galaxy")]
    [InlineData("LIN", "Galaxy")]
    [InlineData("QSO", "Galaxy")]
    [InlineData("PoG", "Galaxy")]
    [InlineData("IG", "Galaxy")]
    [InlineData("OpC", "Open Cluster")]
    [InlineData("Cl*", "Open Cluster")]
    [InlineData("GlC", "Globular Cluster")]
    [InlineData("*", "Star")]
    [InlineData("**", "Star")]
    [InlineData("Ae*", "Star")]
    public void Categorize_SimbadCode_MapsToExpectedCategory(string code, string expectedCategory)
    {
        Assert.Equal(expectedCategory, ObjectTypeCategories.Categorize(code));
    }

    // Coordinator ruling Q6: OpenNGC Type codes categorize directly, no translation layer.
    [Theory]
    [InlineData("OCl", "Open Cluster")]
    [InlineData("GCl", "Globular Cluster")]
    [InlineData("EmN", "Emission Nebula")]
    [InlineData("RfN", "Reflection Nebula")]
    [InlineData("DrkN", "Dark Nebula")]
    [InlineData("Neb", "Emission Nebula")]
    [InlineData("Ast", "Other")]
    [InlineData("*Ass", "Other")]
    [InlineData("Dup", "Other")]
    public void Categorize_OpenNgcCode_MapsToExpectedCategory(string code, string expectedCategory)
    {
        Assert.Equal(expectedCategory, ObjectTypeCategories.Categorize(code));
    }

    [Fact]
    public void Categorize_MultiCodeString_UsesFirstCodeOnly()
    {
        Assert.Equal("Emission Nebula", ObjectTypeCategories.Categorize("HII,sh"));
    }

    [Fact]
    public void Categorize_UnmappedCode_ReturnsOther()
    {
        Assert.Equal("Other", ObjectTypeCategories.Categorize("XYZ"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Categorize_NullOrEmpty_ReturnsOther(string? rawObjectType)
    {
        Assert.Equal("Other", ObjectTypeCategories.Categorize(rawObjectType));
    }

    // Phase 6's review fix item 9 removed the category-name pass-through and pinned it with a
    // theory over "galaxy", "Galaxy", "PLANET" and "pn". Coordinator ruling Q14 reverses that
    // removal, because FIXER LIST item 4 gives it the caller it lacked: SolarSystemNames.Classify
    // stores the literal category in object_type. The three category-name rows moved into the
    // pass-through tests below; the lowercase-code row stays here, where it still holds (the
    // SIMBAD codes are case-SENSITIVE, so "pn" is not "PN").
    [Theory]
    [InlineData("pn")]
    [InlineData("snr")]
    [InlineData("XYZ")]
    public void Categorize_UnknownString_IsStillOther(string input)
    {
        Assert.Equal("Other", ObjectTypeCategories.Categorize(input));
    }

    // Ruling Q14, the reason the pass-through exists: a solar-system target created by
    // TargetResolver's SolarSystem stage carries its category as object_type, and without this
    // the dashboard's five solar-system pills can never match (FIXER LIST item 4).
    [Theory]
    [InlineData("Planet")]
    [InlineData("Moon")]
    [InlineData("Sun")]
    [InlineData("Comet")]
    [InlineData("Asteroid")]
    public void Categorize_SolarSystemCategoryName_PassesThrough(string category)
    {
        Assert.Equal(category, ObjectTypeCategories.Categorize(category));
    }

    [Theory]
    [InlineData("Galaxy")]
    [InlineData("Emission Nebula")]
    [InlineData("Globular Cluster")]
    [InlineData("Star")]
    public void Categorize_DisplayCategoryName_PassesThrough(string category)
    {
        Assert.Equal(category, ObjectTypeCategories.Categorize(category));
    }

    [Fact]
    public void Categorize_OtherLiteral_IsOther()
    {
        Assert.Equal("Other", ObjectTypeCategories.Categorize("Other"));
    }

    [Fact]
    public void Categorize_PassThroughIsCaseInsensitive()
    {
        // A category name is a label, not a catalogue code, so it matches case-insensitively and
        // comes back in the canonical casing the pills are rendered from.
        Assert.Equal("Galaxy", ObjectTypeCategories.Categorize("galaxy"));
        Assert.Equal("Planet", ObjectTypeCategories.Categorize("PLANET"));
        Assert.Equal("Emission Nebula", ObjectTypeCategories.Categorize("emission nebula"));
    }

    [Fact]
    public void Categorize_EveryPillNameCategorizesAsItself()
    {
        foreach (var category in ObjectTypeCategories.DisplayCategories
            .Concat(ObjectTypeCategories.SolarSystemCategories))
        {
            Assert.Equal(category, ObjectTypeCategories.Categorize(category));
        }
    }
}
