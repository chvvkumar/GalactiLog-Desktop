using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using GalactiLog.App.Theme;
using SkiaSharp;
using Xunit;

namespace GalactiLog.App.Tests.Theme;

// Every family the Display tab's font picker offers ships its four static weights and its licence
// as embedded resources, and each FontFamily's avares:// fragment names the family the files'
// own name table carries. A missing weight would be synthesised; a misnamed family would fall
// through to Segoe UI and look almost right. The family name is read through SkiaSharp because
// the headless platform's text stack is a stub that resolves any name.
public class FontAssetsTest
{
    public static TheoryData<string> OptionIds => [.. FontManager.Options.Select(option => option.Id)];

    [AvaloniaTheory]
    [MemberData(nameof(OptionIds))]
    public void EachFamily_HasFourStaticWeightsAndALicence(string id)
    {
        var option = FontManager.Resolve(id);
        var folder = option.Folder.Length == 0 ? "Assets/Fonts" : $"Assets/Fonts/{option.Folder}";

        foreach (var weight in new[] { "Regular", "Medium", "SemiBold", "Bold" })
        {
            var uri = new Uri($"avares://GalactiLog/{folder}/{option.FilePrefix}-{weight}.ttf");
            Assert.True(AssetLoader.Exists(uri), $"{uri} is not an embedded resource.");

            using var stream = AssetLoader.Open(uri);
            var magic = new byte[4];
            stream.ReadExactly(magic);
            Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x00 }, magic);
        }

        Assert.True(AssetLoader.Exists(new Uri($"avares://GalactiLog/{folder}/OFL.txt")), $"{folder} has no OFL.txt.");
    }

    [AvaloniaTheory]
    [MemberData(nameof(OptionIds))]
    public void EachFamilyName_MatchesTheEmbeddedRegularWeight(string id)
    {
        var option = FontManager.Resolve(id);
        using var stream = AssetLoader.Open(option.RegularAsset);
        using var typeface = SKTypeface.FromStream(stream);

        Assert.NotNull(typeface);
        Assert.Equal(option.FontFamily.Name, typeface.FamilyName);
    }
}
