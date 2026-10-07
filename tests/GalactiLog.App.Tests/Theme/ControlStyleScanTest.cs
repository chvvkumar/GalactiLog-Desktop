using System.Text.RegularExpressions;
using GalactiLog.App.Tests.TestSupport;
using Xunit;

namespace GalactiLog.App.Tests.Theme;

// A plain text scan of the shipped control vocabulary, in the shape of FontSizeTokenTest and
// GalactiLog.Core.Tests FileSafetyTest: no shell, no rendering, a regex over the file.
//
// P12's direction refuses shaded buttons. A button is a 1 px outline over a transparent fill,
// with exactly one filled exception (Button.primary, ColorAccent at full strength). The two rules
// below are the choke point for that: the first keeps the gradient, the tinted fill and the drop
// shadow out of every Button style, the second keeps button, tag and callout styling out of the
// views entirely so it cannot be reintroduced one view at a time (design lesson 2).
public class ControlStyleScanTest
{
    // <Style Selector="Button..."> or <Style Selector="ToggleButton..."> through its matching
    // </Style>. Non-greedy so each block ends at its own close tag rather than at the last one in
    // the file. The ToggleButton arm is P12 Task 5's: the chart pill strips are ToggleButtons, a
    // ToggleButton is not a Button, so Fluent's filled toggle would otherwise have been the one
    // control in the vocabulary this rule never looked at.
    private static readonly Regex ButtonStyleBlock = new(
        @"<Style\s+Selector\s*=\s*""(?:Toggle)?Button[^""]*""\s*>.*?</Style>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Gradient = new(
        @"GradientBrush|GradientStop|LinearGradient",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A Background or BorderBrush whose value is an 8 digit hex literal with an alpha byte other
    // than FF: a tinted fill wearing a colour token's clothes.
    private static readonly Regex SemiTransparentLiteral = new(
        @"Property\s*=\s*""[\w.]*(?:Background|BorderBrush)""[^>]*Value\s*=\s*""#(?!FF)[0-9A-F]{8}""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CalloutFill = new(
        @"Property\s*=\s*""[\w.]*Background""[^>]*Color\w*CalloutFill",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Any Opacity inside a Button style block, wherever it sits. The earlier form of this rule
    // anchored on Property="...Background" and then looked for the nested brush with [^>]*, which
    // cannot cross the > that the property element form necessarily puts between the two:
    //
    //   <Setter Property="Background">
    //     <SolidColorBrush Color="..." Opacity="0.2" />
    //   </Setter>
    //
    // That is the form anyone reintroducing a tinted button would actually write, so the rule was
    // inert against the only case it existed for. A Button in this vocabulary is a 1 px outline
    // over a transparent fill or Button.primary's full strength accent, and neither needs an
    // Opacity anywhere, so the rule no longer tries to guess which property the opacity lands on.
    private static readonly Regex BrushWithOpacity = new(
        @"Opacity\s*=",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BoxShadow = new(
        @"BoxShadow",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void NoButtonStyle_CarriesAGradientOrASemiTransparentAccentFill()
    {
        var path = Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Theme", "Controls.axaml");
        Assert.True(File.Exists(path), $"Missing {path}");

        var text = File.ReadAllText(path);
        var blocks = ButtonStyleBlock.Matches(text);

        // A file rename or a selector rewrite must not make this test vacuous.
        Assert.True(blocks.Count > 0, "No Button style blocks were found in Theme/Controls.axaml.");

        // And the ToggleButton arm is not vacuous either: the pill default has to be in the set
        // the rules are run over, or adding it bought nothing.
        Assert.Contains(
            blocks.Cast<Match>(),
            block => block.Value.StartsWith("<Style Selector=\"ToggleButton", StringComparison.Ordinal));

        var offenders = new List<string>();
        foreach (Match block in blocks)
        {
            var selector = block.Value[..Math.Min(block.Value.Length, block.Value.IndexOf('>') + 1)];

            foreach (var (rule, name) in new[]
                     {
                         (Gradient, "gradient"),
                         (SemiTransparentLiteral, "semi-transparent literal fill"),
                         (CalloutFill, "callout fill"),
                         (BrushWithOpacity, "an Opacity attribute"),
                         (BoxShadow, "box shadow"),
                     })
            {
                if (rule.IsMatch(block.Value))
                {
                    offenders.Add($"{selector}: {name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A Button style must be a 1 px outline over a transparent fill, or Button.primary's "
            + "full strength ColorAccent. Offenders:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    // The falsifiability check for the case above. That case scans a clean file, so it passes
    // exactly as happily with a rule that can never match as with a rule that works, which is how
    // the Opacity rule shipped inert. Each rule is therefore run over a block that offends it, and
    // all five over a block that offends none of them. The samples are in-test text; the scan of
    // the real Theme/Controls.axaml stays where it is.
    [Fact]
    public void EveryRule_FiresOnAnOffendingBlockAndIsSilentOnACleanOne()
    {
        const string clean = """
            <Style Selector="Button.primary /template/ ContentPresenter">
              <Setter Property="Background" Value="{DynamicResource ColorAccent}" />
              <Setter Property="TextElement.Foreground" Value="{DynamicResource ColorBgSurface}" />
            </Style>
            """;

        var samples = new (Regex Rule, string Name, string Offender)[]
        {
            (Gradient, "gradient", """
                <Style Selector="Button.bad /template/ ContentPresenter">
                  <Setter Property="Background">
                    <LinearGradientBrush StartPoint="0%,0%" EndPoint="0%,100%">
                      <GradientStop Color="#FFEFE9DC" Offset="0" />
                      <GradientStop Color="#FFA8A49C" Offset="1" />
                    </LinearGradientBrush>
                  </Setter>
                </Style>
                """),
            (SemiTransparentLiteral, "semi-transparent literal fill", """
                <Style Selector="Button.bad /template/ ContentPresenter">
                  <Setter Property="Background" Value="#33EFE9DC" />
                </Style>
                """),
            (CalloutFill, "callout fill", """
                <Style Selector="Button.bad /template/ ContentPresenter">
                  <Setter Property="Background" Value="{DynamicResource ColorInfoCalloutFill}" />
                </Style>
                """),
            // The property element form the old rule could not see: the > after the Setter tag
            // sits between the attribute and the nested brush.
            (BrushWithOpacity, "an Opacity attribute", """
                <Style Selector="Button.bad /template/ ContentPresenter">
                  <Setter Property="Background">
                    <SolidColorBrush Color="{DynamicResource ColorAccentValue}" Opacity="0.2" />
                  </Setter>
                </Style>
                """),
            (BoxShadow, "box shadow", """
                <Style Selector="Button.bad /template/ ContentPresenter">
                  <Setter Property="BoxShadow" Value="0 2 8 0 #40000000" />
                </Style>
                """),
        };

        Assert.Equal(5, samples.Length);

        foreach (var (rule, name, offender) in samples)
        {
            // The block extractor has to see the sample too, or the rule would never be handed it.
            Assert.True(
                ButtonStyleBlock.IsMatch(offender),
                $"The {name} sample is not recognised as a Button style block.");
            Assert.True(rule.IsMatch(offender), $"The {name} rule did not fire on its own offender.");
            Assert.False(rule.IsMatch(clean), $"The {name} rule fires on the shipped filled button.");
        }

        Assert.True(ButtonStyleBlock.IsMatch(clean), "The clean sample is not recognised as a Button style block.");
    }

    // The choke point rule from design lesson 2 applied to styling: a view that declares its own
    // button, tag or callout style is how the direction erodes at site N+1. Theme/Controls.axaml is
    // the one place any of them may be declared.
    //
    // Six files failed this while it was skipped. P12 Task 5 made it pass by lifting rather than
    // by narrowing, so the rule has no exemption list anyone has to remember: the two Border.badge
    // declarations went with the retired session card and Task 4's page rewrite; the three
    // Button.pill copies in Views/ActivityView.axaml, Views/Settings/EquipmentTabView.axaml and
    // Views/Settings/FiltersTabView.axaml became one Button.pill in the shared vocabulary; and the
    // frame table's two header-cell alignment setters moved there too, because a rule with one
    // "except that file" clause is a convention again rather than a choke point.
    //
    // The needle set is the phase fixer's (phase review P2-1). Border.badge was a class this phase
    // renamed out of existence, so half the rule was scanning for a string that could not occur;
    // it is now Border.tag, which is the badge shape that actually ships. ToggleButton joined it
    // because P12 Task 5 added ToggleButtons to the vocabulary and a ToggleButton is not a Button,
    // so the rule would never have looked at one. Border.callout joined it when the page's and the
    // pane's copies were lifted, so the third copy cannot be written.
    //
    // questions.md Q9: Theme/Controls.axaml is skipped outright by the walk below, so its own
    // ScrollBar control theme fired no needle at all before these two joined, and neither would a
    // <Style Selector="ScrollBar"> a later view declared locally. The scrollbar being
    // single-sourced is the whole point; without a needle nothing enforced it (design-lessons rule
    // 2, a choke point added at the moment it is cheapest).
    //
    // Phase 21's fixer widens the ScrollBar control-theme needle to any <ControlTheme in a view: a
    // control theme is shared vocabulary and Theme/Controls.axaml is where it lives, and the
    // narrow form could not see the MenuItem theme the target page's send submenus declared.
    //
    // Both ScrollBar needles stay now that ruling E3 is reversed and no ScrollBar control theme
    // ships anywhere (fixer-list item 1): what they guard is a view writing one of its own, which
    // is exactly the mistake the withdrawal was taken to stop repeating. Their entry in the
    // falsifiability case below therefore names the restyle's own declaration, ScrollBarSize in
    // Theme/Controls.axaml, as the shipped vocabulary rather than the needle itself.
    // Phase 17 Task 4a adds the seventh needle and changes nothing else in this file. Spec 13's
    // four tooltip and legend paints are assigned by three lvc| style blocks in
    // Theme/Controls.axaml, and a view-level style for the same selector beats the application
    // style silently: ChartPaintCensusTests scans chart ELEMENTS for a local paint, and a style
    // block is neither an element nor visible to it. The direction had nothing guarding it, which
    // is the shape design lesson 2 refuses, so the needle lives here beside the six that already
    // say "no view declares this vocabulary locally" rather than in a rule of its own.
    private static readonly string[] ViewStyleNeedles =
    [
        "<Style Selector=\"Button",
        "<Style Selector=\"ToggleButton",
        "<Style Selector=\"Border.tag",
        "<Style Selector=\"ContentControl.callout",
        "<Style Selector=\"ScrollBar",
        "<ControlTheme",
        "<Style Selector=\"lvc|",
    ];

    [Fact]
    public void NoViewUnderSrc_DeclaresItsOwnButtonTagOrCalloutStyle()
    {
        var root = SourceScan.SrcRoot();
        var controls = Path.Combine(root, "GalactiLog.App", "Theme", "Controls.axaml");
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || string.Equals(file, controls, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            scanned++;
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(root, file);

            foreach (var needle in ViewStyleNeedles)
            {
                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{relative}: {needle}");
                }
            }
        }

        Assert.True(scanned > 0, $"No .axaml files were scanned under {root}.");
        Assert.True(
            offenders.Count == 0,
            "Button, tag and callout styling lives in Theme/Controls.axaml only. Offenders:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // The falsifiability check for the rule above, and the one the badge needle needed: a needle
    // that matches nothing anywhere passes the scan exactly as happily as one that works. Each
    // needle is therefore matched against a declaration of the shape it exists to catch, and
    // against the shipped Theme/Controls.axaml, which has to declare all four or the choke point is
    // naming a vocabulary the application does not have.
    [Fact]
    public void EveryViewStyleNeedle_MatchesItsOwnDeclarationAndTheSharedVocabulary()
    {
        // Vocabulary is what Theme/Controls.axaml has to contain for the needle to be guarding
        // something the application actually ships. For the four style needles that is the needle
        // itself. The two ScrollBar needles guard the absence of a local ScrollBar style now that
        // the control theme is withdrawn, so their vocabulary is the restyle that replaced it.
        const string ScrollBarVocabulary = "<x:Double x:Key=\"ScrollBarSize\">";

        var samples = new (string Needle, string Declaration, string Vocabulary)[]
        {
            ("<Style Selector=\"Button", "<Style Selector=\"Button.bad\">", "<Style Selector=\"Button"),
            ("<Style Selector=\"ToggleButton", "<Style Selector=\"ToggleButton.bad\">", "<Style Selector=\"ToggleButton"),
            ("<Style Selector=\"Border.tag", "<Style Selector=\"Border.tag.bad\">", "<Style Selector=\"Border.tag"),
            ("<Style Selector=\"ContentControl.callout", "<Style Selector=\"ContentControl.callout.bad\">", "<Style Selector=\"ContentControl.callout"),
            ("<Style Selector=\"ScrollBar", "<Style Selector=\"ScrollBar.bad\">", ScrollBarVocabulary),
            ("<ControlTheme", "<ControlTheme TargetType=\"MenuItem\">", "<ControlTheme"),
            ("<Style Selector=\"lvc|", "<Style Selector=\"lvc|CartesianChart\">", "<Style Selector=\"lvc|CartesianChart\">"),
        };

        Assert.Equal(ViewStyleNeedles.Length, samples.Length);

        var controls = File.ReadAllText(
            Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Theme", "Controls.axaml"));

        foreach (var (needle, declaration, vocabulary) in samples)
        {
            Assert.Contains(needle, ViewStyleNeedles);
            Assert.Contains(needle, declaration, StringComparison.Ordinal);
            Assert.True(
                controls.Contains(vocabulary, StringComparison.Ordinal),
                $"Theme/Controls.axaml declares nothing matching {vocabulary}, so the rule guards a "
                + "vocabulary that does not ship.");
        }
    }
}
