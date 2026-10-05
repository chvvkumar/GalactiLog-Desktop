using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using GalactiLog.App.Tests.TestSupport;
using Xunit;

namespace GalactiLog.App.Tests.Theme;

// P12 R8. Atkinson Hyperlegible Next and Atkinson Hyperlegible Mono are embedded as application
// resources under Assets/Fonts with their licence, and Avalonia.Fonts.Inter is gone. These cases
// are what fails if the avares:// fragment stops naming the family in the font's own name table,
// if a variable font replaces the static instances, if the AvaloniaResource glob stops covering
// the directory, or if the licence is dropped.
public class FontResourceTest
{
    [AvaloniaFact]
    public void FontFamilyResources_Resolve_AndNameTheEmbeddedFamilies()
    {
        Assert.True(Application.Current!.TryFindResource("FontSans", out var sansValue));
        Assert.True(Application.Current!.TryFindResource("FontMono", out var monoValue));

        var sans = Assert.IsType<FontFamily>(sansValue);
        var mono = Assert.IsType<FontFamily>(monoValue);

        Assert.Equal("Atkinson Hyperlegible Next", sans.Name);
        Assert.Equal("Atkinson Hyperlegible Mono", mono.Name);

        // An embedded family carries a FontFamilyKey; a pure system family does not. This is what
        // catches a missing or misnamed avares:// fragment, which would otherwise fall through to
        // the system fallback and look almost right.
        Assert.NotNull(sans.Key);
        Assert.NotNull(mono.Key);
    }

    // The regression pin for ruling Q1: six static instances, not the two variable files
    // google/fonts publishes, because Avalonia does not support variable fonts.
    //
    // This is a file census rather than a rendering assertion. The headless platform this suite
    // runs on uses Avalonia's drawing and text stubs, so every typeface measures with the same
    // synthetic advance and four weights rendered side by side are identical whatever fonts ship.
    // A rendering assertion would therefore pin nothing. The tables in the files do pin it: a
    // variable font carries an fvar axis table and a static instance does not.
    [Theory]
    [InlineData("AtkinsonHyperlegibleNext-Regular.ttf")]
    [InlineData("AtkinsonHyperlegibleNext-Medium.ttf")]
    [InlineData("AtkinsonHyperlegibleNext-SemiBold.ttf")]
    [InlineData("AtkinsonHyperlegibleNext-Bold.ttf")]
    [InlineData("AtkinsonHyperlegibleMono-Regular.ttf")]
    [InlineData("AtkinsonHyperlegibleMono-Medium.ttf")]
    public void EachEmbeddedFont_IsAStaticTrueTypeInstance(string fileName)
    {
        var path = Path.Combine(FontsDirectory(), fileName);
        Assert.True(File.Exists(path), $"Missing {path}");

        var bytes = File.ReadAllBytes(path);

        // The TrueType sfnt version, 0x00010000. Rules out an OpenType/CFF or WOFF file, which
        // would need a different loader.
        Assert.True(
            bytes.Length > 12 && bytes[0] == 0x00 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00,
            $"{fileName} does not start with the TrueType sfnt version 00 01 00 00.");

        Assert.DoesNotContain("fvar", TableTags(bytes));
    }

    [Fact]
    public void TheFontsDirectory_HoldsTheSixStaticInstancesAndNothingElse()
    {
        var files = Directory.GetFiles(FontsDirectory(), "*.ttf")
            .Select(path => Path.GetFileName(path) ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "AtkinsonHyperlegibleMono-Medium.ttf",
                "AtkinsonHyperlegibleMono-Regular.ttf",
                "AtkinsonHyperlegibleNext-Bold.ttf",
                "AtkinsonHyperlegibleNext-Medium.ttf",
                "AtkinsonHyperlegibleNext-Regular.ttf",
                "AtkinsonHyperlegibleNext-SemiBold.ttf",
            ],
            files);
    }

    // The csproj globs Assets\** as AvaloniaResource, so the six files are compiled into the
    // GalactiLog assembly. This is what fails if that glob stops covering the new directory: the
    // FontFamily resource above would still resolve and every window would quietly fall through
    // to the system fallback.
    [AvaloniaFact]
    public void TheSixStaticInstances_AreEmbeddedInTheApplicationAssembly()
    {
        foreach (var fileName in new[]
                 {
                     "AtkinsonHyperlegibleNext-Regular.ttf",
                     "AtkinsonHyperlegibleNext-Medium.ttf",
                     "AtkinsonHyperlegibleNext-SemiBold.ttf",
                     "AtkinsonHyperlegibleNext-Bold.ttf",
                     "AtkinsonHyperlegibleMono-Regular.ttf",
                     "AtkinsonHyperlegibleMono-Medium.ttf",
                 })
        {
            var uri = new Uri($"avares://GalactiLog/Assets/Fonts/{fileName}");
            Assert.True(AssetLoader.Exists(uri), $"{uri} is not an embedded resource.");
        }
    }

    /// <summary>The four character tags in a TrueType file's table directory.</summary>
    private static IReadOnlyList<string> TableTags(byte[] bytes)
    {
        var count = (bytes[4] << 8) | bytes[5];
        var tags = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var offset = 12 + (index * 16);
            if (offset + 4 > bytes.Length)
            {
                break;
            }

            tags.Add(System.Text.Encoding.ASCII.GetString(bytes, offset, 4));
        }

        return tags;
    }

    [Fact]
    public void TheEmbeddedFontsShipTheirLicence()
        => Assert.True(File.Exists(Path.Combine(FontsDirectory(), "OFL.txt")));

    // The repository root comes from TestSupport/SourceScan rather than from a fifteenth private
    // copy of FindRepoRoot: this project already extracted that helper in Phase 10 and it is
    // reachable from here (HANDOFF 5.4 item 7 tracks the copy count; this task adds none).
    private static string FontsDirectory()
        => Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Assets", "Fonts");
}
