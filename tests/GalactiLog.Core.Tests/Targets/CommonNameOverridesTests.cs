using GalactiLog.Core.Targets;
using Xunit;

namespace GalactiLog.Core.Tests.Targets;

public class CommonNameOverridesTests
{
    [Fact]
    public void Map_HasExactlySeventyTwoEntries()
    {
        Assert.Equal(72, CommonNameOverrides.Map.Count);
    }

    [Fact]
    public void Map_ContainsKnownAbbreviation()
    {
        Assert.Equal("rho Oph", CommonNameOverrides.Map["rho oph"]);
    }

    [Fact]
    public void Map_ContainsSimbadNameFormatEntry()
    {
        Assert.Equal("NAME Markarian Chain", CommonNameOverrides.Map["markarian's chain"]);
    }

    [Fact]
    public void Map_ContainsFullCaldwellOneThroughFiftyRange()
    {
        Assert.Equal("NGC 188", CommonNameOverrides.Map["caldwell 1"]);
        Assert.Equal("NGC 2244", CommonNameOverrides.Map["caldwell 50"]);
        Assert.False(CommonNameOverrides.Map.ContainsKey("caldwell 51"));
    }

    [Fact]
    public void Map_KeysAreAlreadyLowercase()
    {
        foreach (var key in CommonNameOverrides.Map.Keys)
        {
            Assert.Equal(key.ToLowerInvariant(), key);
        }
    }
}
