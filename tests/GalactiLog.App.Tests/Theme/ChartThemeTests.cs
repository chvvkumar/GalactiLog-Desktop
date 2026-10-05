using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using GalactiLog.App.Theme;
using LiveChartsCore;
using SkiaSharp;
using Xunit;

namespace GalactiLog.App.Tests.Theme;

// Spec 13's global LiveCharts2 configuration. AvaloniaFact throughout, because every assertion
// depends on Application.Current and the merged token dictionary existing (ruling Q2 is that the
// configuration is only meaningful once Avalonia has started).
public class ChartThemeTests
{
    // The token value as the shipped dictionary declares it, resolved the same way the production
    // Read does, so a test cannot pass by agreeing with itself about a hard-coded hex string.
    private static SKColor Token(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var value), $"Missing resource '{key}'");
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(value);
        return new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A);
    }

    [AvaloniaFact]
    public void Apply_ResolvesEveryPaletteEntryFromTheShippedDictionary()
    {
        ChartTheme.Apply();
        var palette = ChartTheme.Palette;

        // Not the fallback: every one of the five chrome colours came out of the dictionary.
        Assert.NotEqual(ChartTheme.Fallback, palette.AxisLabels);
        Assert.NotEqual(ChartTheme.Fallback, palette.Separators);
        Assert.NotEqual(ChartTheme.Fallback, palette.LegendText);
        Assert.NotEqual(ChartTheme.Fallback, palette.TooltipBackground);
        Assert.NotEqual(ChartTheme.Fallback, palette.TooltipText);

        // All ten spec 14.1 metric tokens, each resolved rather than defaulted.
        Assert.Equal(10, palette.Metrics.Count);
        foreach (var key in ChartTheme.MetricTokenOrder)
        {
            Assert.Equal(Token(key), palette.Metrics[key]);
        }
    }

    // Review finding 5: this asserts the token reader, not Apply, so it is named for what it
    // covers. Apply's own resilience is covered by Apply_ResolvesEveryPaletteEntry, which would
    // fail with the fallback value if any key were missing.
    [AvaloniaFact]
    public void Read_MissingResourceKey_FallsBackWithoutThrowing()
        => Assert.Equal(ChartTheme.Fallback, ChartTheme.Read("ColorThisKeyDoesNotExist", ChartTheme.Fallback));

    // A theme swap calls Apply a second time. rc5.4's Theme.AxisBuilder is a List the
    // HasRuleForAxes extension appends to, and the append survives a later UseDefaults, so a
    // second Apply that re-registered would grow the rule list without bound and leak a paint per
    // swap. Asserted on the library's own state, not on a private flag.
    [AvaloniaFact]
    public void Apply_IsIdempotent()
    {
        ChartTheme.Apply();
        var axisRules = ((ICollection?)LiveCharts.DefaultSettings.GetTheme().AxisBuilder)?.Count;
        var themeInstance = LiveCharts.DefaultSettings.GetTheme();

        ChartTheme.Apply();

        Assert.Equal(axisRules, ((ICollection?)LiveCharts.DefaultSettings.GetTheme().AxisBuilder)?.Count);
        Assert.Same(themeInstance, LiveCharts.DefaultSettings.GetTheme());
        Assert.True(LiveCharts.HasBackend);
        Assert.True(LiveCharts.HasTheme);
    }

    [AvaloniaFact]
    public void Apply_RaisesChanged()
    {
        var raised = 0;
        void Handler(object? sender, EventArgs e) => raised++;

        ChartTheme.Changed += Handler;
        try
        {
            ChartTheme.Apply();
            ChartTheme.Apply();
        }
        finally
        {
            ChartTheme.Changed -= Handler;
        }

        Assert.Equal(2, raised);
    }

    // ---- FIXER LIST F8: the one subscribe-and-unsubscribe token ------------------------------

    [AvaloniaFact]
    public void Subscribe_InvokesTheRebuild_OnEveryApply()
    {
        var rebuilds = 0;
        using var subscription = ChartTheme.Subscribe(() => rebuilds++);

        ChartTheme.Apply();
        ChartTheme.Apply();

        Assert.Equal(2, rebuilds);
    }

    [AvaloniaFact]
    public void Subscribe_Dispose_Unsubscribes()
    {
        // The half that matters: Changed is a static event, so a handler left behind pins its
        // whole view-model for the life of the process and keeps rebuilding charts nobody sees.
        var rebuilds = 0;
        var subscription = ChartTheme.Subscribe(() => rebuilds++);

        ChartTheme.Apply();
        subscription.Dispose();
        ChartTheme.Apply();

        Assert.Equal(1, rebuilds);
    }

    [AvaloniaFact]
    public void Subscribe_Dispose_IsIdempotent()
    {
        var rebuilds = 0;
        var subscription = ChartTheme.Subscribe(() => rebuilds++);

        subscription.Dispose();
        subscription.Dispose();
        ChartTheme.Apply();

        Assert.Equal(0, rebuilds);
    }

    [AvaloniaFact]
    public void Subscribe_TwoTokens_AreIndependent()
    {
        var first = 0;
        var second = 0;
        var one = ChartTheme.Subscribe(() => first++);
        using var two = ChartTheme.Subscribe(() => second++);

        one.Dispose();
        ChartTheme.Apply();

        Assert.Equal(0, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public void Subscribe_RefusesANullRebuild()
        => Assert.Throws<ArgumentNullException>(() => ChartTheme.Subscribe(null!));

    // Spec 13: "animations 200 ms, matching the interface's transition duration". The library's
    // own default is 800 ms, so this is a real override and the assertion covers both the constant
    // and the fact that Apply installs it.
    [AvaloniaFact]
    public void AnimationsSpeed_IsTwoHundredMilliseconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(200), ChartTheme.AnimationsSpeed);

        ChartTheme.Apply();

        Assert.Equal(TimeSpan.FromMilliseconds(200), LiveCharts.DefaultSettings.AnimationsSpeed);
    }

    [AvaloniaFact]
    public void Palette_AxisLabels_MatchesColorTextSecondary()
    {
        ChartTheme.Apply();
        Assert.Equal(Token("ColorTextSecondary"), ChartTheme.Palette.AxisLabels);
    }

    [AvaloniaFact]
    public void Palette_Separators_MatchesColorBorderDefault()
    {
        ChartTheme.Apply();
        Assert.Equal(Token("ColorBorderDefault"), ChartTheme.Palette.Separators);
    }

    [AvaloniaFact]
    public void Palette_LegendText_MatchesColorTextSecondary()
    {
        ChartTheme.Apply();
        Assert.Equal(Token("ColorTextSecondary"), ChartTheme.Palette.LegendText);
    }

    [AvaloniaFact]
    public void Palette_TooltipBackground_MatchesColorBgElevated()
    {
        ChartTheme.Apply();
        Assert.Equal(Token("ColorBgElevated"), ChartTheme.Palette.TooltipBackground);
    }

    [AvaloniaFact]
    public void Palette_TooltipText_MatchesColorTextPrimary()
    {
        ChartTheme.Apply();
        Assert.Equal(Token("ColorTextPrimary"), ChartTheme.Palette.TooltipText);
    }

    // A surface token may carry alpha (deep-sky's ColorBgElevated is #E6181B26); dropping it
    // renders opaque panels where that theme asks for glass. The conversion is asserted against a
    // literal, and the palette entry against whatever alpha the merged dictionary declares, so
    // this case pins the conversion rather than one theme's opacity. luminance, the
    // default from Phase 12 on, declares opaque surfaces (P12 R7).
    [AvaloniaFact]
    public void ToSkColor_PreservesAlpha()
    {
        Assert.Equal(0xE6, ChartTheme.ToSkColor(Color.Parse("#E6181B26")).Alpha);

        ChartTheme.Apply();

        Assert.Equal(Token("ColorBgElevated").Alpha, ChartTheme.Palette.TooltipBackground.Alpha);
    }

    // Spec 14.5: the series palette is the metric tokens, in the token-table order, and never
    // reassigned per chart. Seeding the library's own colour cycle from it is what stops an
    // unpainted series showing a colour that is not in the theme.
    [AvaloniaFact]
    public void Apply_SeedsTheLibraryColourCycleFromTheMetricTokens()
    {
        ChartTheme.Apply();

        var colors = LiveCharts.DefaultSettings.GetTheme().Colors;
        Assert.Equal(ChartTheme.MetricTokenOrder.Length, colors.Length);
        for (var index = 0; index < ChartTheme.MetricTokenOrder.Length; index++)
        {
            var expected = Token(ChartTheme.MetricTokenOrder[index]);
            Assert.Equal(expected.Red, colors[index].R);
            Assert.Equal(expected.Green, colors[index].G);
            Assert.Equal(expected.Blue, colors[index].B);
        }
    }

    // Phase 9 Task 3 review finding I1, and the coordinator's structural escalation on it. The
    // shipped dictionary declares the four semantic colours as Color and every other token as a
    // SolidColorBrush (DeepSky.axaml's own comment says why), and Read used to return the
    // documented grey for the first form. That silence is indistinguishable from a missing key at
    // the call site, and it cost the whole grading palette on the Statistics page.
    [AvaloniaTheory]
    [InlineData("ColorSuccessValue", "ColorSuccess")]
    [InlineData("ColorWarningValue", "ColorWarning")]
    [InlineData("ColorErrorValue", "ColorError")]
    [InlineData("ColorInfoValue", "ColorInfo")]
    public void Read_AcceptsAColorResource_AndAgreesWithItsBrushTwin(string colorKey, string brushKey)
    {
        var fromColor = ChartTheme.Read(colorKey, ChartTheme.Fallback);

        Assert.NotEqual(ChartTheme.Fallback, fromColor);
        Assert.Equal(Token(brushKey), fromColor);
    }

    [AvaloniaFact]
    public void Read_ANonColourResource_StillFallsBack()
    {
        // The fallback still means "this key is not a colour", which is what keeps a typo loud
        // rather than silently grey in one more shape.
        Assert.True(Application.Current!.TryFindResource("RadiusMd", out var radius));
        Assert.NotNull(radius);
        Assert.Null(ChartTheme.AsBrush(radius));
        Assert.Equal(ChartTheme.Fallback, ChartTheme.Read("RadiusMd", ChartTheme.Fallback));
    }
}
