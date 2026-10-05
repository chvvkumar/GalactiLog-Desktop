using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Theme;
using Xunit;

namespace GalactiLog.App.Tests;

public class ThemeResourceTest
{
    private static readonly string[] BrushKeys =
    {
        "ColorBgBase", "ColorBgSurface", "ColorBgElevated", "ColorBgHover", "ColorBgInput",
        "ColorBorderDefault", "ColorBorderEmphasis",
        "ColorTextPrimary", "ColorTextSecondary", "ColorTextTertiary",
        "ColorAccent", "ColorAccentHover", "ColorAccentPressed",
        "ColorSuccess", "ColorWarning", "ColorError", "ColorInfo",
        "ColorMetricIntegration", "ColorMetricFrames", "ColorMetricHfr", "ColorMetricEccentricity",
        "ColorMetricFwhm", "ColorMetricStars", "ColorMetricGuiding", "ColorMetricTemp",
        "ColorMetricGain", "ColorMetricTime",
        "ColorMetricBest", "ColorMetricWorst",
        "ColorBadgeBg", "ColorBadgeText",
        "ColorFilterHa", "ColorFilterOiii", "ColorFilterSii", "ColorFilterL",
        "ColorFilterR", "ColorFilterG", "ColorFilterB",
        "ColorScrollbarThumb", "ColorScrollbarThumbHover", "ColorScrollbarTrack",
    };

    // FIXER LIST F12, spec 14.5: a callout is the semantic colour at 20 percent fill and 50 percent
    // border, with full-strength text from the semantic token itself. Additional resources, not
    // among the 38 tokens above.
    private static readonly (string Fill, string Border, double FillOpacity, double BorderOpacity)[] CalloutKeys =
    {
        ("ColorSuccessCalloutFill", "ColorSuccessCalloutBorder", 0.2, 0.5),
        ("ColorWarningCalloutFill", "ColorWarningCalloutBorder", 0.2, 0.5),
        ("ColorErrorCalloutFill", "ColorErrorCalloutBorder", 0.2, 0.5),
        ("ColorInfoCalloutFill", "ColorInfoCalloutBorder", 0.2, 0.5),
    };

    private static readonly string[] ScaleKeys =
    {
        "RadiusSm", "RadiusMd", "RadiusLg",
        "ShadowSm", "ShadowMd", "ShadowLg",
        "FontSizeMicro", "FontSizeTiny", "FontSizeCaption", "FontSizeLabel",
    };

    [AvaloniaFact]
    public void AllFortyOneBrushKeys_Resolve()
    {
        Assert.Equal(41, BrushKeys.Length);
        foreach (var key in BrushKeys)
        {
            Assert.True(Application.Current!.TryFindResource(key, out var value), $"Missing resource '{key}'");
            Assert.NotNull(value);
        }
    }

    // TRACKING hard rule 4: dark is the default. Without this the Fluent chrome follows the OS
    // and renders light controls against the dark token dictionary on a light-mode Windows.
    [AvaloniaFact]
    public void Application_PinsTheDarkThemeVariant()
        => Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);

    [AvaloniaFact]
    public void CalloutComposites_DeriveFromTheSemanticColourAtTheSpecifiedOpacities()
    {
        foreach (var (fillKey, borderKey, fillOpacity, borderOpacity) in CalloutKeys)
        {
            var semanticKey = fillKey.Replace("CalloutFill", "", StringComparison.Ordinal);
            Assert.True(Application.Current!.TryFindResource(semanticKey, out var semantic));
            var expected = Assert.IsAssignableFrom<ISolidColorBrush>(semantic).Color;

            foreach (var (key, opacity) in new[] { (fillKey, fillOpacity), (borderKey, borderOpacity) })
            {
                Assert.True(Application.Current!.TryFindResource(key, out var value), $"Missing resource '{key}'");
                var brush = Assert.IsAssignableFrom<ISolidColorBrush>(value);

                // Same hue as the semantic token, differing only in opacity: a callout must never
                // introduce a colour of its own.
                Assert.Equal(expected, brush.Color);
                Assert.Equal(opacity, brush.Opacity, 3);
            }
        }
    }

    [AvaloniaFact]
    public void AllScaleKeys_Resolve()
    {
        foreach (var key in ScaleKeys)
        {
            Assert.True(Application.Current!.TryFindResource(key, out var value), $"Missing resource '{key}'");
            Assert.NotNull(value);
        }
    }

    // P12 R7, six dictionaries: every one of them carries exactly the same key
    // set, so a theme swap can never leave a surface unpainted. The census above asserts against
    // whichever dictionary App.axaml merges; this one asserts against each file directly.
    [AvaloniaTheory]
    [InlineData("avares://GalactiLog/Theme/Themes/CivilDusk.axaml")]
    [InlineData("avares://GalactiLog/Theme/Themes/Luminance.axaml")]
    [InlineData("avares://GalactiLog/Theme/Themes/DeepSky.axaml")]
    [InlineData("avares://GalactiLog/Theme/Themes/RedLight.axaml")]
    [InlineData("avares://GalactiLog/Theme/Themes/Atlas.axaml")]
    [InlineData("avares://GalactiLog/Theme/Themes/Logbook.axaml")]
    public void EveryShippedTheme_CarriesAllFortyOneKeysAndTheEightCallouts(string source)
    {
        var dictionary = new ResourceInclude((Uri?)null) { Source = new Uri(source) };

        foreach (var key in BrushKeys)
        {
            Assert.True(
                dictionary.TryGetResource(key, ThemeVariant.Dark, out var value) && value is not null,
                $"{source} is missing '{key}'");
        }

        foreach (var (fillKey, borderKey, fillOpacity, borderOpacity) in CalloutKeys)
        {
            foreach (var (key, opacity) in new[] { (fillKey, fillOpacity), (borderKey, borderOpacity) })
            {
                Assert.True(
                    dictionary.TryGetResource(key, ThemeVariant.Dark, out var value),
                    $"{source} is missing '{key}'");
                var brush = Assert.IsAssignableFrom<ISolidColorBrush>(value);
                Assert.Equal(opacity, brush.Opacity, 3);
            }
        }

        foreach (var key in new[] { "ColorGradientFromValue", "ColorGradientToValue" })
        {
            Assert.True(
                dictionary.TryGetResource(key, ThemeVariant.Dark, out var value) && value is Color,
                $"{source} is missing the Color '{key}'");
        }

        Assert.True(
            dictionary.TryGetResource("BrushPageBackground", ThemeVariant.Dark, out var background),
            $"{source} is missing 'BrushPageBackground'");
        Assert.IsAssignableFrom<IGradientBrush>(background);
    }

    // civil-dusk is the default and leads the picker, the six ship in this order, and the two
    // renamed themes carry their new ids. There is no migration, so an unknown stored id falls
    // back to Table[0], which this case pins as civil-dusk. A failure looks like a picker in the wrong order or an old label surviving.
    [AvaloniaFact]
    public void DefaultTheme_IsCivilDusk_AndSixShipInPickerOrder()
    {
        Assert.Equal("civil-dusk", ThemeManager.Available[0]);
        Assert.Equal(["civil-dusk", "luminance", "deep-sky", "red-light", "atlas", "logbook"], ThemeManager.Available);
        Assert.Equal("Civil Dusk", ThemeManager.LabelFor("civil-dusk"));
        Assert.Equal("Luminance", ThemeManager.LabelFor("luminance"));
        Assert.Equal("Deep Sky", ThemeManager.LabelFor("deep-sky"));
        Assert.Equal("Red Light", ThemeManager.LabelFor("red-light"));
        Assert.Equal("Atlas", ThemeManager.LabelFor("atlas"));
        Assert.Equal("Logbook", ThemeManager.LabelFor("logbook"));
    }

    // The two light themes have to move the platform controls to the light variant, or a Fluent
    // combo box drop-down draws dark chrome over a paper surface. The four dark ones keep Dark. A
    // failure looks like every row reporting Dark.
    [AvaloniaTheory]
    [InlineData("civil-dusk", "Dark")]
    [InlineData("luminance", "Dark")]
    [InlineData("deep-sky", "Dark")]
    [InlineData("red-light", "Dark")]
    [InlineData("atlas", "Light")]
    [InlineData("logbook", "Light")]
    public void EachTheme_SetsItsOwnThemeVariant(string themeId, string variant)
    {
        try
        {
            ThemeManager.Apply(themeId);
            Assert.Equal(variant == "Light" ? ThemeVariant.Light : ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    // An older profile may still store observing-ledger or glass-void. There is no migration:
    // spec 14's unknown-id rule reads either as the default, and nothing
    // rewrites the stored value. A failure looks like the old id resolving to a dictionary of its
    // own or the application being left on the theme applied before.
    [AvaloniaTheory]
    [InlineData("observing-ledger")]
    [InlineData("glass-void")]
    public void ARetiredId_ReadsAsTheDefault(string retired)
    {
        try
        {
            ThemeManager.Apply("red-light");
            ThemeManager.ApplyStored(new StartupTheme(retired));

            Assert.EndsWith("CivilDusk.axaml", AppliedThemeSource()!.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal(retired, ThemeManager.LabelFor(retired));
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    // The census above proves presence, not identity: it asks each dictionary for the keys this
    // file names, so a dictionary carrying an extra key, or three dictionaries drifting apart on
    // anything the list forgot, passes it (Task 1 review P3-3, phase review P3-4). This asserts the
    // six key sets are equal to one another, which is the claim R7 actually makes.
    [AvaloniaFact]
    public void TheSixShippedThemes_DeclareOneIdenticalKeySet()
    {
        string[] sources =
        [
            "avares://GalactiLog/Theme/Themes/CivilDusk.axaml",
            "avares://GalactiLog/Theme/Themes/Luminance.axaml",
            "avares://GalactiLog/Theme/Themes/DeepSky.axaml",
            "avares://GalactiLog/Theme/Themes/RedLight.axaml",
            "avares://GalactiLog/Theme/Themes/Atlas.axaml",
            "avares://GalactiLog/Theme/Themes/Logbook.axaml",
        ];

        var sets = sources
            .Select(source => (Source: source, Keys: KeysOf(source)))
            .ToList();

        // Not vacuous: the set has to be the shipped one rather than an empty read.
        Assert.All(sets, entry => Assert.Contains("ColorAccent", entry.Keys));
        Assert.All(sets, entry => Assert.True(entry.Keys.Count >= BrushKeys.Length));

        var first = sets[0];
        foreach (var other in sets.Skip(1))
        {
            Assert.True(
                first.Keys.SetEquals(other.Keys),
                $"{first.Source} and {other.Source} declare different keys. "
                + $"Only in the first: {string.Join(", ", first.Keys.Except(other.Keys).Order())}. "
                + $"Only in the second: {string.Join(", ", other.Keys.Except(first.Keys).Order())}.");
        }
    }

    // Phase 12 verification, blocker 2. A stored general.theme was not applied at startup: the
    // Settings combo read Deep Sky and the window rendered the default, because App.axaml
    // merges the default dictionary by hand and nothing called Apply with the stored id when the
    // shell was built. ApplyStored is the seam the harness can execute: the headless harness never
    // reaches a line of App.OnFrameworkInitializationCompleted (TRACKING section 6 item 29), so
    // the call site is censused below and the behaviour is asserted here.
    [AvaloniaFact]
    public void ApplyStored_AppliesTheStoredThemesDictionary()
    {
        const string DeepSky = "avares://GalactiLog/Theme/Themes/DeepSky.axaml";
        const string CivilDusk = "avares://GalactiLog/Theme/Themes/CivilDusk.axaml";

        try
        {
            ThemeManager.ApplyStored(new StartupTheme("deep-sky"));

            Assert.EndsWith("DeepSky.axaml", AppliedThemeSource()!.AbsolutePath, StringComparison.Ordinal);

            // Not only the merged entry: the token the window reads is the stored theme's, and it
            // is not the default's, so neither half of this can pass vacuously.
            Assert.Equal(TokenOf(DeepSky, "ColorAccent"), AppliedToken("ColorAccent"));
            Assert.NotEqual(TokenOf(CivilDusk, "ColorAccent"), TokenOf(DeepSky, "ColorAccent"));

            // Ruling Q13 has no migration: an id this build does not ship falls back to the
            // table's first entry, and nothing rewrites what is stored.
            ThemeManager.ApplyStored(new StartupTheme("no-such-theme"));
            Assert.EndsWith("CivilDusk.axaml", AppliedThemeSource()!.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal(TokenOf(CivilDusk, "ColorAccent"), AppliedToken("ColorAccent"));

            // No container, which is the headless harness and nothing else: the default theme,
            // exactly what the line this replaced left merged.
            ThemeManager.ApplyStored(new StartupTheme("red-light"));
            Assert.EndsWith("RedLight.axaml", AppliedThemeSource()!.AbsolutePath, StringComparison.Ordinal);
            ThemeManager.ApplyStored(null);
            Assert.EndsWith("CivilDusk.axaml", AppliedThemeSource()!.AbsolutePath, StringComparison.Ordinal);
        }
        finally
        {
            // Application.Current is process-wide under the harness, so this case puts the default
            // back rather than leaving a theme behind for whatever runs next.
            ThemeManager.Apply(ThemeManager.Available[0]);
        }
    }

    // The census half of blocker 2 (TRACKING section 6 item 29): App.OnFrameworkInitializationCompleted
    // is unreachable from every test in this project, so the claim that the startup path applies
    // the stored theme is a source-text read, the same shape every other assertion about that
    // method takes. Two things are pinned: the call is there, and it is the only theme step the
    // startup path takes, so a future edit cannot put a bare ChartTheme.Apply() back beside it and
    // leave the stored id unapplied again.
    [Fact]
    public void Startup_AppliesTheStoredTheme_Census()
    {
        var app = Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "App.axaml.cs");
        var source = SourceScan.StripComments(File.ReadAllText(app));

        Assert.Contains("ThemeManager.ApplyStored(Services?.GetService<Theme.StartupTheme>())", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ChartTheme.Apply()", source, StringComparison.Ordinal);

        // And the value it is handed is the stored one, registered by AppHost from the document it
        // already read.
        var host = SourceScan.StripComments(
            File.ReadAllText(Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "AppHost.cs")));
        Assert.Contains("new Theme.StartupTheme(initialGeneral.Theme)", host, StringComparison.Ordinal);

        // Non-vacuity: the two files were read, not two empty strings.
        Assert.Contains("OnFrameworkInitializationCompleted", source, StringComparison.Ordinal);
        Assert.Contains("builder.Services", host, StringComparison.Ordinal);
    }

    /// <summary>The theme dictionary the application currently has merged: the one entry under
    /// <c>Theme/Themes</c>, located the same way <c>ThemeManager.Apply</c> locates it.</summary>
    private static Uri? AppliedThemeSource()
        => Application.Current!.Resources.MergedDictionaries
            .OfType<ResourceInclude>()
            .Select(include => include.Source)
            .FirstOrDefault(source =>
                source is not null
                && source.AbsolutePath.Contains("/Theme/Themes/", StringComparison.OrdinalIgnoreCase));

    private static Color AppliedToken(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var value), $"Missing resource '{key}'");
        return ((ISolidColorBrush)value!).Color;
    }

    private static Color TokenOf(string source, string key)
    {
        var dictionary = new ResourceInclude((Uri?)null) { Source = new Uri(source) };
        Assert.True(dictionary.TryGetResource(key, ThemeVariant.Dark, out var value), $"{source} is missing '{key}'");
        return ((ISolidColorBrush)value!).Color;
    }

    private static HashSet<string> KeysOf(string source)
    {
        var dictionary = new ResourceInclude((Uri?)null) { Source = new Uri(source) };
        return [.. dictionary.Loaded.Keys.OfType<string>()];
    }
}
