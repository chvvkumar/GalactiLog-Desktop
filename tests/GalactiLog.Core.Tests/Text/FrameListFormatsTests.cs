using GalactiLog.Core.Text;
using Xunit;

namespace GalactiLog.Core.Tests.Text;

/// <summary>
/// Spec 12.4's three-row format table, byte for byte, plus spec 5.8.2's stored literals.
/// The paths form is ruling C6 made executable: it must equal what the page copied before the
/// dialog existed.
/// </summary>
public class FrameListFormatsTests
{
    private static readonly string[] ThreePaths =
    [
        @"C:\Astro\2025-09-08\LIGHT_0001.fits",
        @"C:\Astro\2025-09-08\LIGHT_0002.fits",
        @"D:\Other\2025-09-09\LIGHT_0003.fits",
    ];

    [Fact]
    public void Paths_IsOnePerLineWithATrailingNewline()
    {
        var rendered = FrameListFormats.Render(FrameListFormat.Paths, ThreePaths);

        Assert.Equal(
            string.Join(Environment.NewLine, ThreePaths) + Environment.NewLine,
            rendered);
        Assert.EndsWith(Environment.NewLine, rendered, StringComparison.Ordinal);
        Assert.Equal(3, rendered.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Paths_IsByteForByteWhatTheFrameTableCopies()
    {
        // The literal is read from ShellIntegration.CopyFrameListAsync in
        // src/GalactiLog.App/Services/ShellIntegration.cs, which writes
        // string.Join(Environment.NewLine, framePaths) + Environment.NewLine. GalactiLog.App is
        // not referenced from a Core test, so the expression is restated here rather than called.
        var shellExpression = string.Join(Environment.NewLine, ThreePaths) + Environment.NewLine;

        Assert.Equal(shellExpression, FrameListFormats.Render(FrameListFormat.Paths, ThreePaths));
    }

    [Fact]
    public void Names_IsTheBareFileNamesInTheSameOrder()
    {
        var rendered = FrameListFormats.Render(FrameListFormat.Names, ThreePaths);

        Assert.Equal(
            new[] { "LIGHT_0001.fits", "LIGHT_0002.fits", "LIGHT_0003.fits" },
            rendered.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Names_CarriesTheSameTrailingNewline()
    {
        var names = FrameListFormats.Render(FrameListFormat.Names, ThreePaths);
        var paths = FrameListFormats.Render(FrameListFormat.Paths, ThreePaths);

        Assert.EndsWith(Environment.NewLine, names, StringComparison.Ordinal);
        Assert.Equal(
            paths.Split(Environment.NewLine).Length,
            names.Split(Environment.NewLine).Length);
    }

    [Fact]
    public void Explorer_QuotesEachNameAndJoinsWithSpaceOrSpace()
    {
        Assert.Equal(
            "\"LIGHT_0001.fits\" OR \"LIGHT_0002.fits\" OR \"LIGHT_0003.fits\"",
            FrameListFormats.Render(FrameListFormat.Explorer, ThreePaths));
        Assert.Equal(" OR ", FrameListFormats.ExplorerSeparator);
    }

    [Fact]
    public void Explorer_HasNoTrailingNewline()
    {
        var rendered = FrameListFormats.Render(FrameListFormat.Explorer, ThreePaths);

        Assert.DoesNotContain("\n", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Explorer_IsOneLine()
    {
        var rendered = FrameListFormats.Render(FrameListFormat.Explorer, ThreePaths);

        Assert.Single(rendered.Split(Environment.NewLine));
    }

    [Fact]
    public void AnEmptyList_RendersTheEmptyString_InEveryFormat()
    {
        foreach (var format in Enum.GetValues<FrameListFormat>())
        {
            Assert.Equal(string.Empty, FrameListFormats.Render(format, []));
        }
    }

    [Fact]
    public void ANameWithADoubleQuote_IsEmittedAsItIs()
    {
        // Explorer's search box has no escape for a double quote, and inventing one would produce
        // a string that finds nothing (spec 12.4).
        string[] paths = [@"C:\Astro\M31 " + "\"wide\"" + ".fits"];

        Assert.Equal(
            "\"M31 \"wide\".fits\"",
            FrameListFormats.Render(FrameListFormat.Explorer, paths));
        Assert.Equal(
            "M31 \"wide\".fits" + Environment.NewLine,
            FrameListFormats.Render(FrameListFormat.Names, paths));
    }

    [Fact]
    public void TheOrder_IsTheOrderGiven()
    {
        string[] reversed = [ThreePaths[2], ThreePaths[1], ThreePaths[0]];

        Assert.Equal(
            string.Join(Environment.NewLine, reversed) + Environment.NewLine,
            FrameListFormats.Render(FrameListFormat.Paths, reversed));
        Assert.StartsWith(
            "\"LIGHT_0003.fits\"",
            FrameListFormats.Render(FrameListFormat.Explorer, reversed),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("paths", FrameListFormat.Paths)]
    [InlineData("names", FrameListFormat.Names)]
    [InlineData("explorer", FrameListFormat.Explorer)]
    [InlineData("EXPLORER", FrameListFormat.Explorer)]
    [InlineData("Names", FrameListFormat.Names)]
    public void Parse_ReadsTheStoredLiterals(string stored, FrameListFormat expected)
    {
        Assert.Equal(expected, FrameListFormats.Parse(stored));
        Assert.Equal(FrameListFormats.ToStored(expected), FrameListFormats.ToStored(FrameListFormats.Parse(stored)));
    }

    [Fact]
    public void Parse_AnUnknownValue_IsPaths()
    {
        Assert.Equal(FrameListFormat.Paths, FrameListFormats.Parse("stacking"));
        Assert.Equal(FrameListFormat.Paths, FrameListFormats.Parse(string.Empty));
        Assert.Equal(FrameListFormat.Paths, FrameListFormats.Parse(null));
    }

    [Theory]
    [InlineData("good", FrameListMode.Good)]
    [InlineData("bad", FrameListMode.Bad)]
    [InlineData("BAD", FrameListMode.Bad)]
    [InlineData("Good", FrameListMode.Good)]
    public void ParseMode_ReadsTheStoredLiterals(string stored, FrameListMode expected)
    {
        Assert.Equal(expected, FrameListFormats.ParseMode(stored));
        Assert.Equal(FrameListFormats.ToStored(expected), FrameListFormats.ToStored(FrameListFormats.ParseMode(stored)));
    }

    [Fact]
    public void ParseMode_AnUnknownValue_IsGood()
    {
        Assert.Equal(FrameListMode.Good, FrameListFormats.ParseMode("ugly"));
        Assert.Equal(FrameListMode.Good, FrameListFormats.ParseMode(null));
    }
}
