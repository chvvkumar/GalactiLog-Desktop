using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Theme;
using LiveChartsCore;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Painting;
using LiveChartsCore.SkiaSharpView.Avalonia;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Xunit;

namespace GalactiLog.App.Tests.Theme;

// Spec 13's paragraph "The four tooltip and legend paints are assigned on every chart control".
//
// This file is deliberately separate from ChartThemeTests.cs. That file asserts ChartTheme.Palette
// and LiveCharts.DefaultSettings, which is exactly the surface that already tracked the theme
// correctly and is the reason the shipped defect went unseen: every assertion there reads the
// settings, never the control. Mixing the two would blur what each file proves, so nothing here
// reads LiveCharts.DefaultSettings and nothing in ChartThemeTests.cs was edited.
//
// The defect, measured by the Phase 17 chart spike (spike-charts-report.md section 5, TRAP 1): the
// Avalonia chart controls resolve their TooltipBackgroundPaint, TooltipTextPaint,
// LegendBackgroundPaint and LegendTextPaint styled-property DEFAULTS once per process, from
// whatever LiveCharts.DefaultSettings held when the first chart control in the process was built.
// A theme swap therefore re-coloured axes, series and pills and left every tooltip and legend
// painted in the theme the application started in, on a live chart and on a chart built after the
// swap alike.
//
// The correction that shipped is shape A of task4a.md: one style block in Theme/Controls.axaml
// with three selectors, each setting the four properties through a DynamicResource to a key
// ChartTheme.Apply writes. A Setter beats a styled-property default, and a DynamicResource
// re-evaluates when the resource is written, so there is no per-control token and no attach or
// detach lifetime to get wrong.
public class ChartPaintCensusTests
{
    // The four resource keys the style block binds to, in the order the four properties appear on
    // the control. Named from ChartTheme so a rename cannot leave the test agreeing with itself.
    private static readonly string[] PaintKeys =
    [
        ChartTheme.TooltipBackgroundPaintKey,
        ChartTheme.TooltipTextPaintKey,
        ChartTheme.LegendBackgroundPaintKey,
        ChartTheme.LegendTextPaintKey,
    ];

    // The four property names, which is what a chart element must NOT set locally: a local value
    // beats a style Setter, and that is the one way a chart can silently opt out of the spine.
    private static readonly string[] PaintProperties =
    [
        "TooltipBackgroundPaint", "TooltipTextPaint", "LegendBackgroundPaint", "LegendTextPaint",
    ];

    // The three Avalonia chart types the package declares the four properties on, which is the
    // FLOOR the style block must select. It is not the whole rule: the element half below asks
    // Theme/Controls.axaml about each type it actually finds in the markup, so a chart of a fourth
    // type fails the census rather than shipping unpainted (task 4a review finding 1).
    private static readonly string[] ChartSelectors = ["CartesianChart", "PieChart", "PolarChart"];

    // The token value as the shipped dictionary declares it, resolved the way the production Read
    // does. Copied from ChartThemeTests rather than referenced across files, on task4a.md's
    // instruction, so neither file's helper is load bearing for the other.
    private static SKColor Token(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var value), $"Missing resource '{key}'");
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(value);
        return new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A);
    }

    // ---- Case A, the census ------------------------------------------------------------------

    // Every LiveCharts chart element in the shipped markup is covered by the one style block, and
    // the block exists to cover it. Two halves, which is ControlStyleScanTest's own
    // needle-plus-vocabulary pairing: a rule that matches nothing anywhere passes as happily as
    // one that works.
    //
    // Two ways a chart element escapes the spine, and both are offences here:
    //
    //   1. it sets one of the four paints locally, because a local value beats a style Setter;
    //   2. its TYPE has no selector in Theme/Controls.axaml, which the task 4a review measured is
    //      worse than a stale colour. With no Setter matching and no resolvable value, the four
    //      properties read NULL: an invisible tooltip and an unpainted legend, not the documented
    //      neutral the brief predicted. A chart of a type this application has not used before is
    //      exactly the site that would ship that way, so it fails here rather than at a user's
    //      pointer. Review finding 1.
    //
    // The resolver is the markup text and not a runtime property read. A runtime read would need
    // every view built, and a view no test ever builds is exactly the site that forgets.
    [Fact]
    public void EveryChartElementInMarkup_IsLeftToTheSharedStyle()
    {
        var blocks = ControlsStyleBlocks();
        var offenders = new List<string>();
        var charts = 0;
        var scanned = 0;

        foreach (var file in SourceScan.EnumerateMarkupFiles("GalactiLog.App"))
        {
            scanned++;
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(SourceScan.SrcRoot(), file);

            foreach (var element in ChartElements(text))
            {
                charts++;
                var where = $"{relative}: <{element.Prefix}:{element.Type}{element.Name}>";

                foreach (var property in PaintProperties)
                {
                    // Both spellings of a local value: the attribute form and the property element
                    // form, which is what anyone assigning a constructed Paint would have to write.
                    if (Regex.IsMatch(element.Tag, $@"\b{property}\s*=")
                        || text.Contains($"<{element.Prefix}:{element.Type}.{property}", StringComparison.Ordinal))
                    {
                        offenders.Add($"{where} sets {property}");
                    }
                }

                // Offence 2: this element's own type, asked of the shipped style block. The type
                // comes from the markup, so the rule follows what the application actually uses
                // rather than the fixed list of three below.
                foreach (var uncovered in UncoveredPaints(blocks, element.Type))
                {
                    offenders.Add($"{where} is uncovered: {uncovered}");
                }
            }
        }

        Assert.True(scanned > 0, "No .axaml files were scanned under src/GalactiLog.App.");
        Assert.True(charts > 0, "No LiveCharts chart elements were found, so this census is vacuous.");
        Assert.True(
            offenders.Count == 0,
            "The four tooltip and legend paints come from Theme/Controls.axaml's chart style. A "
            + "local value beats the Setter, and a type with no selector reads null on all four. "
            + "Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // The vocabulary half. Theme/Controls.axaml declares a selector for each of the three chart
    // types the package declares the four properties on, and each selector sets all four paints, or
    // the census above is guarding a rule the application does not have. This is the floor; the
    // element half is what follows the markup.
    [Fact]
    public void ControlsAxaml_DeclaresTheChartStyleForAllThreeTypesWithAllFourPaints()
    {
        var blocks = ControlsStyleBlocks();
        var missing = new List<string>();

        foreach (var type in ChartSelectors)
        {
            missing.AddRange(UncoveredPaints(blocks, type));
        }

        Assert.True(
            missing.Count == 0,
            "Theme/Controls.axaml is the one place the four chart paints are assigned. Missing:"
            + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    // Every style block in Theme/Controls.axaml, with its selector and its body. Shared by both
    // halves above rather than copied into each (design lesson 1), which is also what makes the two
    // agree about what "covered" means.
    private static MatchCollection ControlsStyleBlocks()
    {
        var path = Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Theme", "Controls.axaml");
        Assert.True(File.Exists(path), $"Missing {path}");

        // The rule does not dictate whether the types are one comma-separated selector or a block
        // each: what it asserts is that each type is selected by a block carrying all four setters.
        return Regex.Matches(
            File.ReadAllText(path),
            @"<Style\s+Selector\s*=\s*""(?<selector>[^""]*)""\s*>(?<body>.*?)</Style>",
            RegexOptions.Singleline);
    }

    // What Theme/Controls.axaml fails to give one chart type: the selector itself, or any of the
    // four setters under it. An empty result means that type is fully covered.
    private static List<string> UncoveredPaints(MatchCollection blocks, string type)
    {
        var block = blocks
            .Cast<Match>()
            .FirstOrDefault(candidate =>
                Regex.IsMatch(candidate.Groups["selector"].Value, $@"lvc\|{type}(?![\w.:])"));

        if (block is null)
        {
            return [$"Theme/Controls.axaml declares no <Style Selector=\"lvc|{type}\">"];
        }

        var missing = new List<string>();
        for (var index = 0; index < PaintProperties.Length; index++)
        {
            if (!Regex.IsMatch(
                    block.Groups["body"].Value,
                    $@"Property\s*=\s*""{PaintProperties[index]}""[^>]*DynamicResource\s+{PaintKeys[index]}\s*\}}"))
            {
                missing.Add($"lvc|{type}: {PaintProperties[index]} bound to {PaintKeys[index]}");
            }
        }

        return missing;
    }

    // ---- Case B, the paints on the CONTROL after a live theme swap ---------------------------

    // The case the spike's TRAP 1 measurement is reproduced by, and the one that fails against the
    // shipped defect in both of its halves: the chart that was alive across the swap and the chart
    // built after it. Asserted on SolidColorPaint.Color against the token the swapped-in dictionary
    // declares, never against a hex literal and never against LiveCharts.DefaultSettings, which
    // tracks the theme correctly and is not what the control reads.
    // The three probes are collected and asserted once, rather than one Assert.Equal each, so a
    // failure names every paint on every probe instead of stopping at the first. That is what made
    // the red run record both halves of the spike's finding in one pass, and it is the same
    // enumerate-the-offenders shape the census above takes.
    [AvaloniaFact]
    public void TheFourPaints_OnALiveControlAndOnAFreshOne_FollowAThemeSwap()
    {
        var wrong = new List<string>();

        try
        {
            ThemeManager.Apply("luminance");

            var live = new CartesianChart();
            var window = new Window { Width = 800, Height = 400, Content = live };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Not vacuous: the control starts on theme one's ink, so theme two's values below are
            // a change rather than a coincidence.
            Collect(wrong, live, "the live chart before the swap");

            ThemeManager.Apply("deep-sky");
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            // Half one: the same live control, which the spike measured keeping theme one's ink.
            Collect(wrong, live, "the live chart after the swap");

            // Half two, the counter-intuitive one: a control constructed after the swap, which the
            // spike measured keeping theme one's ink as well.
            var fresh = new CartesianChart();
            window.Content = fresh;
            Dispatcher.UIThread.RunJobs();

            Collect(wrong, fresh, "a chart built after the swap");

            // The Storage pie is a PieChart and the lvc|PieChart selector was pinned by a text
            // match alone, so the second shipped chart type is exercised at run time too (review
            // finding 6). Collected through IChartView, which is where LiveChartsCore declares all
            // four properties, so the helper takes every chart type rather than gaining an overload
            // per type.
            var pie = new PieChart();
            window.Content = pie;
            Dispatcher.UIThread.RunJobs();

            Collect(wrong, pie, "a pie chart built after the swap");
        }
        finally
        {
            // Application.Current is process-wide under the harness, so the default theme goes back
            // rather than being left behind for whatever runs next.
            ThemeManager.Apply(ThemeManager.Available[0]);
        }

        Assert.True(
            wrong.Count == 0,
            "The four paints are read off the control, not off LiveCharts.DefaultSettings. Wrong:"
            + Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    private static void Collect(List<string> wrong, IChartView chart, string what)
    {
        foreach (var (property, paint, token) in new (string, Paint?, string)[]
                 {
                     ("TooltipBackgroundPaint", chart.TooltipBackgroundPaint, "ColorBgElevated"),
                     ("TooltipTextPaint", chart.TooltipTextPaint, "ColorTextPrimary"),
                     ("LegendBackgroundPaint", chart.LegendBackgroundPaint, "ColorBgElevated"),
                     ("LegendTextPaint", chart.LegendTextPaint, "ColorTextSecondary"),
                 })
        {
            // A null paint under the headless platform would be a different finding from the
            // defect this file exists for, so it is named rather than compared away.
            if (paint is not SolidColorPaint solid)
            {
                wrong.Add($"{what}: {property} is {paint?.GetType().Name ?? "null"}");
                continue;
            }

            var expected = Token(token);
            if (solid.Color != expected)
            {
                wrong.Add($"{what}: {property} is {solid.Color}, {token} is {expected}");
            }
        }
    }

    // ---- Case D, the ink an axis TITLE is painted in -----------------------------------------

    // ChartTheme's one axis rule set LabelsPaint and SeparatorsPaint and left NamePaint to the
    // LiveCharts default theme's own axis rule, which is the rule appended to rather than
    // replaced. That rule paints an axis TITLE in #FF232323 whatever theme is applied: a
    // near-black title on all three shipped dark pages, unreadable on red-light where every other
    // mark is red, and one that no theme swap moves while the tick labels beside it follow every
    // swap. Eight axes ship a title, seven of them added by Phase 17 and one, the Target detail
    // session chart's "Frame", shipped in Phase 14A.
    //
    // The ink is read off an axis the theme's own axis rules have been run over, in the shipped
    // order, which is exactly what a chart does to every axis it measures. It is NOT read off a
    // hosted control: under the headless platform only the first chart control in the process ever
    // measures its axes, so a control-based figure would depend on which case in the assembly ran
    // first. That measurement is recorded in fix-p17-f3-report.md.
    [AvaloniaFact]
    public void ATitledAxis_PaintsItsTitleInTheThemesAxisInk_LikeItsTickLabels_AcrossASwap()
    {
        var wrong = new List<string>();

        try
        {
            ThemeManager.Apply("luminance");

            // The axis a live chart holds: built and themed before the swap.
            var live = Themed(new LiveChartsCore.SkiaSharpView.Axis { Name = "Date" });
            CollectAxisInk(wrong, live, "an axis themed under luminance");

            // Not vacuous: the two themes' axis inks differ, so the arms below are a change rather
            // than a coincidence.
            var before = Token("ColorTextSecondary");
            ThemeManager.Apply("deep-sky");
            Assert.NotEqual(before, Token("ColorTextSecondary"));

            // The live chart after the swap. Every chart view-model answers ChartTheme.Changed by
            // publishing its axes again, and the swapped-in theme is run over what it publishes,
            // which is the one route a tick label already follows.
            CollectAxisInk(wrong, Themed(live), "the live chart's axis after the swap");

            CollectAxisInk(
                wrong,
                Themed(new LiveChartsCore.SkiaSharpView.Axis { Name = "HFR (px)" }),
                "an axis built after the swap");
        }
        finally
        {
            // Application.Current is process-wide under the harness, so the default theme goes back
            // rather than being left behind for whatever runs next.
            ThemeManager.Apply(ThemeManager.Available[0]);
        }

        Assert.True(
            wrong.Count == 0,
            "An axis title takes the theme's axis ink from ChartTheme's one axis rule, the same "
            + "ink and the same rule as its tick labels. Wrong:"
            + Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    // The shipped axis rules over one axis, in the order LiveCharts holds them: the library's own
    // first and ChartTheme's appended rule second, which is the order a measuring chart runs them
    // in and the order that decides which paint wins.
    private static LiveChartsCore.SkiaSharpView.Axis Themed(LiveChartsCore.SkiaSharpView.Axis axis)
    {
        foreach (var build in LiveCharts.DefaultSettings.GetTheme().AxisBuilder)
        {
            build(axis);
        }

        return axis;
    }

    private static void CollectAxisInk(List<string> wrong, IPlane axis, string what)
    {
        // A case over an untitled axis would pass whatever NamePaint held, so the title is
        // asserted rather than assumed.
        Assert.False(string.IsNullOrEmpty(axis.Name), $"{what}: the axis carries no title");

        var expected = Token("ColorTextSecondary");
        foreach (var (property, paint) in new (string, Paint?)[]
                 {
                     ("NamePaint", axis.NamePaint),
                     ("LabelsPaint", axis.LabelsPaint),
                 })
        {
            if (paint is not SolidColorPaint solid)
            {
                wrong.Add($"{what}: {property} is {paint?.GetType().Name ?? "null"}");
                continue;
            }

            if (solid.Color != expected)
            {
                wrong.Add($"{what}: {property} is {solid.Color}, ColorTextSecondary is {expected}");
            }
        }
    }

    // The other half, and the reason the rule above is a spine rather than a convention: a chart
    // that assigned its own NamePaint would keep the theme it was built under, exactly as the four
    // tooltip and legend paints did before task 4a's style block. No chart needs one, so none may
    // hold one.
    [Fact]
    public void NoChartSetsAnAxisNamePaintOfItsOwn()
    {
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in SourceScan.EnumerateSourceFiles())
        {
            scanned++;
            var text = SourceScan.StripComments(File.ReadAllText(file));
            if (!text.Contains("NamePaint", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Path.GetRelativePath(SourceScan.SrcRoot(), file);
            if (relative != Path.Combine("GalactiLog.App", "Theme", "ChartTheme.cs"))
            {
                offenders.Add($"{relative} assigns NamePaint");
            }
        }

        Assert.True(scanned > 0, "No source files were scanned under src.");
        Assert.True(
            offenders.Count == 0,
            "An axis title's ink comes from ChartTheme's one axis rule and from nowhere else. "
            + "Offenders:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // ---- Case C ------------------------------------------------------------------------------
    //
    // task4a.md section 3 case C is a shape B case and there is no shape B. The style block holds
    // no token, subscribes to nothing and owns no lifetime, so there is no detach to forget and
    // nothing pins a control for the life of the process. That is the strongest argument for the
    // shape that shipped and it is recorded here in place of the case.

    // Every chart element in the markup, by prefix, type and x:Name, resolving the prefix from the
    // file's own xmlns declarations rather than assuming "lvc": a file that binds the package to a
    // different prefix must still be censused. The suffix is matched, never a fixed list of three,
    // so a chart type a future package version adds is caught rather than counted.
    private static IEnumerable<(string Prefix, string Type, string Name, string Tag)> ChartElements(string text)
    {
        foreach (Match declaration in Regex.Matches(
                     text,
                     @"xmlns:(\w+)\s*=\s*""using:LiveChartsCore\.SkiaSharpView\.Avalonia"""))
        {
            var prefix = declaration.Groups[1].Value;
            foreach (Match element in Regex.Matches(text, $@"<{prefix}:(\w*Chart)\b[^>]*>"))
            {
                var name = Regex.Match(element.Value, @"x:Name\s*=\s*""(\w+)""");
                yield return (
                    prefix,
                    element.Groups[1].Value,
                    name.Success ? $" x:Name={name.Groups[1].Value}" : "",
                    element.Value);
            }
        }
    }
}
