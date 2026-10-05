using System.Text;
using GalactiLog.Core.Catalogs;
using GalactiLog.Core.Io;
using Xunit;

namespace GalactiLog.Core.Tests.Catalogs;

public class StellariumNamesTests
{
    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void ParseNamesDat_ParsesRealSampleLine_NgcPrefix()
    {
        const string line = "NGC  40              _(\"Bow-Tie Nebula\") # ESKY, B500, DSC-CO, CO-HOT";

        var names = StellariumNames.ParseNamesDat(Stream(line));

        Assert.Equal("NGC 40", names["bow-tie nebula"]);
    }

    [Fact]
    public void ParseNamesDat_ParsesRealSampleLine_Sh2PrefixHyphenJoin()
    {
        const string data =
            "SH2  3               _(\"Green Ring Nebula\")\n" +
            "SH2  8               _(\"Bear's Claw Nebula\") # WP\n";

        var names = StellariumNames.ParseNamesDat(Stream(data));

        Assert.Equal("Sh2-3", names["green ring nebula"]);
        Assert.Equal("Sh2-8", names["bear's claw nebula"]);
    }

    [Fact]
    public void ParseNamesDat_ParsesRealSampleLine_StPrefixMapsToStock()
    {
        const string line = "ST   23              _(\"Pazmino's Cluster\") # SBSX, U2K";

        var names = StellariumNames.ParseNamesDat(Stream(line));

        Assert.Equal("Stock 23", names["pazmino's cluster"]);
    }

    [Fact]
    public void ParseNamesDat_UnmappedPrefixPassesThrough()
    {
        const string line = "ZZZ  99              _(\"Test Object\")";

        var names = StellariumNames.ParseNamesDat(Stream(line));

        Assert.Equal("ZZZ 99", names["test object"]);
    }

    [Fact]
    public void ParseNamesDat_SkipsShortLine()
    {
        const string line = "NGC 1 _(\"X\")"; // well under 21 chars

        var names = StellariumNames.ParseNamesDat(Stream(line));

        Assert.Empty(names);
    }

    [Fact]
    public void ParseNamesDat_SkipsBlankLine()
    {
        var names = StellariumNames.ParseNamesDat(Stream("\n   \n"));

        Assert.Empty(names);
    }

    [Fact]
    public void ParseNamesDat_SkipsCommentLine()
    {
        const string line = "# Stellarium DSO Catalog Names";

        var names = StellariumNames.ParseNamesDat(Stream(line));

        Assert.Empty(names);
    }

    [Fact]
    public void ParseNamesDat_SkipsLineWithNoNameMatch()
    {
        const string line = "NGC  40              no name pattern here at all";

        var names = StellariumNames.ParseNamesDat(Stream(line));

        Assert.Empty(names);
    }

    [Fact]
    public void ParseNamesDat_SkipsEmptyPrefixOrId()
    {
        // Columns 0-4 and 5-19 are all spaces; remainder still has a matching pattern.
        const string line = "                     _(\"Test Object\")";

        var names = StellariumNames.ParseNamesDat(Stream(line));

        Assert.Empty(names);
    }

    [Fact]
    public void ParseNamesDat_FirstOccurrenceWins()
    {
        const string data =
            "NGC  40              _(\"Bow-Tie Nebula\")\n" +
            "NGC  41              _(\"BOW-TIE NEBULA\")\n";

        var names = StellariumNames.ParseNamesDat(Stream(data));

        Assert.Equal("NGC 40", names["bow-tie nebula"]);
    }

    [Fact]
    public void ParseNamesDat_ParsesRealBundledFile()
    {
        // Uses ParseNamesDat directly (not GetNames) so this doesn't collide with
        // GetNames_CachesAcrossCalls's static _cache in the same test run.
        var path = Path.Combine(StaticCatalogLoader.ResolveCatalogsDirectory(), "names.dat");
        using var stream = UserFiles.OpenRead(path);

        var names = StellariumNames.ParseNamesDat(stream);

        Assert.True(names.Count > 1000, $"expected > 1000 names, got {names.Count}");
        Assert.Equal("NGC 40", names["bow-tie nebula"]);
    }

    [Fact]
    public void GetNames_CachesAcrossCalls()
    {
        var dir = Directory.CreateTempSubdirectory("stellarium-names-test");
        try
        {
            var path = Path.Combine(dir.FullName, "names.dat");
            File.WriteAllText(path, "NGC  40              _(\"Bow-Tie Nebula\")\n");

            var first = StellariumNames.GetNames(dir.FullName);

            // Truncate the file; a second, non-cached call would see nothing.
            File.WriteAllText(path, string.Empty);

            var second = StellariumNames.GetNames(dir.FullName);

            Assert.Same(first, second);
            Assert.Equal("NGC 40", second["bow-tie nebula"]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
