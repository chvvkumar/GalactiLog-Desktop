using System.Globalization;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Text;
using Xunit;

namespace GalactiLog.Core.Tests.Text;

/// <summary>
/// Spec 12.16's AstroBin CSV as <see cref="AstroBinCsv"/> renders it: the twelve cells, the two
/// always-blank ones, the two-step filter id lookup, the invariant formatting and the line
/// endings. The figures themselves are the query's and are pinned in
/// <c>SessionDetailAcquisitionTests</c>; everything here is hand built, so a cell rule can be
/// stated in one line.
/// </summary>
public class AstroBinCsvTests
{
    private static readonly DateOnly Night = new(2025, 3, 20);

    // No alias configured: CanonicalFilter answers the stored name, which is the shape every
    // library that has never opened the Filters tab is in.
    private static AliasMap NoAliases()
        => new(new Dictionary<string, FilterSetting>(), new EquipmentSettings());

    // "H-Alpha" is stored on the frames and "Ha" is the canonical name, which is the only shape
    // in which the two lookup steps can disagree.
    private static AliasMap HaAlias()
        => new(
            new Dictionary<string, FilterSetting> { ["Ha"] = new() { Aliases = ["H-Alpha"] } },
            new EquipmentSettings());

    private static AstroBinRow Row(string filter = "Ha", double? exposure = 300d)
        => new(Night, filter, 20, exposure, 120, -10.2d, 20.7d, 2.2d, 7.75d);

    private static string[] Lines(string csv) => csv.Split('\n');

    /// <summary>Case 8. Twelve fields on every line, header included. A filter name reaching the
    /// filter cell, or a dropped separator, changes this count and nothing else would notice.
    /// </summary>
    [Fact]
    public void Build_EmitsTwelveFieldsOnEveryLine()
    {
        var csv = AstroBinCsv.Build(
            [Row(), Row("OIII", 180d), new AstroBinRow(Night, "SII", 3, null, null, null, null, null, null)],
            new Dictionary<string, int> { ["Ha"] = 7 },
            4,
            NoAliases());

        var lines = Lines(csv);
        Assert.Equal(AstroBinCsv.Header, lines[0]);
        foreach (var line in lines[..^1])
        {
            Assert.Equal(12, line.Split(',').Length);
        }
    }

    /// <summary>Case 9. Rows joined with a bare line feed, exactly one trailing line feed, no
    /// carriage return anywhere. The Windows default would disagree with Copy Frame List's own
    /// clipboard write on the same page.</summary>
    [Fact]
    public void Build_JoinsWithLineFeedAndEndsWithOne()
    {
        var csv = AstroBinCsv.Build([Row(), Row("OIII", 180d)], new Dictionary<string, int>(), null, NoAliases());

        Assert.DoesNotContain('\r', csv);
        Assert.EndsWith("\n", csv, StringComparison.Ordinal);
        Assert.False(csv.EndsWith("\n\n", StringComparison.Ordinal));
        Assert.Equal(4, Lines(csv).Length); // header, two rows, the empty tail of the final newline
    }

    /// <summary>An empty selection renders the header and its newline, never a bare empty string
    /// and never a lone blank line.</summary>
    [Fact]
    public void Build_WithNoRows_IsTheHeaderAndOneNewline()
        => Assert.Equal(AstroBinCsv.Header + "\n", AstroBinCsv.Build([], new Dictionary<string, int>(), 4, NoAliases()));

    /// <summary>Case 6. <c>binning</c> and <c>fNumber</c> are always blank, <c>bortle</c> is blank
    /// while the key is unset, and a filter neither lookup finds still emits its row with the
    /// other eleven cells. A dropped row silently shrinks an upload's total.</summary>
    [Fact]
    public void Build_LeavesBinningFNumberBortleAndAnUnmappedFilterBlank()
    {
        var csv = AstroBinCsv.Build([Row("Lum")], new Dictionary<string, int> { ["Ha"] = 7 }, null, NoAliases());

        var cells = Lines(csv)[1].Split(',');
        Assert.Equal(string.Empty, cells[1]);  // filter: neither "Lum" nor its canonical name is mapped
        Assert.Equal(string.Empty, cells[4]);  // binning
        Assert.Equal(string.Empty, cells[7]);  // fNumber
        Assert.Equal(string.Empty, cells[8]);  // bortle, the key unset
        Assert.Equal("20", cells[2]);          // the row is still emitted, with its frame count
        Assert.Equal("300", cells[3]);
    }

    /// <summary>Case 7. The id resolves through the canonical name when the frames carry a raw
    /// alias, and through the raw name when only that is mapped. A single-step lookup loses one
    /// of the two.</summary>
    [Theory]
    [InlineData("Ha", 7)]
    [InlineData("H-Alpha", 9)]
    public void Build_LooksTheIdUpByCanonicalNameThenByStoredName(string mappedKey, int id)
    {
        var csv = AstroBinCsv.Build(
            [Row("H-Alpha")],
            new Dictionary<string, int> { [mappedKey] = id },
            null,
            HaAlias());

        Assert.Equal(id.ToString(CultureInfo.InvariantCulture), Lines(csv)[1].Split(',')[1]);
    }

    /// <summary>Spec 12.16's rounding: halves away from zero on the three two-decimal cells,
    /// toward positive infinity on <c>sensorCooling</c> so a negative half matches the web's
    /// <c>Math.round</c> rather than going further negative. A value that is not a half is rounded
    /// by its own arithmetic, which is why the query's medians are reported unrounded.</summary>
    [Fact]
    public void Build_RoundsHalvesAwayFromZeroExceptSensorCoolingTowardPositiveInfinity()
    {
        var csv = AstroBinCsv.Build(
            [new AstroBinRow(Night, "Ha", 1, 0.5d, 100, -10.5d, 20.125d, 2.125d, -7.125d)],
            new Dictionary<string, int>(),
            null,
            NoAliases());

        var cells = Lines(csv)[1].Split(',');
        Assert.Equal("0.5", cells[3]);      // duration, at most four decimals, trailing zeros gone
        Assert.Equal("-10", cells[6]);      // sensorCooling, a negative half rounds toward positive infinity
        Assert.Equal("20.13", cells[9]);
        Assert.Equal("2.13", cells[10]);
        Assert.Equal("-7.13", cells[11]);
    }

    /// <summary>A sensor temperature that rounds to zero from below renders <c>0</c> and never
    /// <c>-0</c>, which is not a cell any reader expects.</summary>
    [Fact]
    public void Build_NeverRendersNegativeZero()
    {
        var csv = AstroBinCsv.Build(
            [new AstroBinRow(Night, "Ha", 1, 300d, 100, -0.4d, null, null, null)],
            new Dictionary<string, int>(),
            null,
            NoAliases());

        Assert.Equal("0", Lines(csv)[1].Split(',')[6]);
    }

    /// <summary>Case 5. Every number is invariant. On a German machine the current culture would
    /// put a decimal comma inside a value and give the line thirteen fields, a corrupted upload no
    /// test on an English machine finds.</summary>
    [Fact]
    public void Build_IsInvariantOfTheCurrentCulture()
    {
        var rows = new[] { Row(), Row("OIII", 180.25d) };
        var ids = new Dictionary<string, int> { ["Ha"] = 7 };
        var expected = AstroBinCsv.Build(rows, ids, 4, NoAliases());
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var csv = AstroBinCsv.Build(rows, ids, 4, NoAliases());
            Assert.Equal(expected, csv);
            foreach (var line in Lines(csv)[..^1])
            {
                Assert.Equal(12, line.Split(',').Length);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>Every nullable member renders as a blank cell and never as zero: a blank means
    /// "not measured" and a zero is a measurement.</summary>
    [Fact]
    public void Build_RendersEveryMissingFigureAsABlankCell()
    {
        var csv = AstroBinCsv.Build(
            [new AstroBinRow(Night, "Ha", 4, null, null, null, null, null, null)],
            new Dictionary<string, int> { ["Ha"] = 7 },
            null,
            NoAliases());

        Assert.Equal("2025-03-20,7,4,,,,,,,,,", Lines(csv)[1]);
    }
}
