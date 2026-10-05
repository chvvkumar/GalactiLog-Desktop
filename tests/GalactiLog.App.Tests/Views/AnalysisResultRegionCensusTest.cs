using System.Text.RegularExpressions;
using GalactiLog.App.Tests.TestSupport;
using Xunit;

namespace GalactiLog.App.Tests.Views;

// Phase 17 Task 4, wiring half. A plain text scan of the five Analysis tab
// body views, in the shape ControlStyleScanTest and FontSizeTokenTest already take: no shell, no
// rendering, a regex over the file.
//
// The rule. Spec 12.14's States table draws no chart in "Loading, first result", in "No row matches
// the filters", in the too-few rows or after a failure, and keeps the last result drawn through
// "Loading, refresh". AnalysisTabViewModel.ShowsResult answers exactly that set, and each tab view
// gates its chart, its table and its stats cards with one element named ResultRegion bound to it.
// The pickers and the segments stay OUTSIDE that element, because a reader who filtered a chart
// into emptiness has to be able to filter back out of it and Compare starts unqueryable.
//
// Why a census and not five assertions. Tasks 5 and 6 fill these five files and a later phase may
// add a sixth tab. A rule each filler has to remember is a convention that will be forgotten at
// site N plus 1 (design lesson 2), so the scan discovers the files by name: a tab view with no
// ResultRegion, or with one that gates on something else, fails naming the file.
public class AnalysisResultRegionCensusTest
{
    private const string RegionName = "ResultRegion";

    // The one element carrying x:Name="ResultRegion", from its opening angle bracket to the end of
    // its start tag. [^>]* cannot cross the tag's own close, and it spans newlines, so an element
    // whose attributes are written one per line is matched whole whatever order they are in.
    private static readonly Regex RegionElement = new(
        @"<[A-Za-z][\w.:]*\b[^>]*x:Name\s*=\s*""" + RegionName + @"""[^>]*>",
        RegexOptions.Compiled);

    // IsVisible bound to the base's ShowsResult, in either binding spelling the markup may use.
    private static readonly Regex GatedOnShowsResult = new(
        @"IsVisible\s*=\s*""\{\s*Binding\s+(?:Path\s*=\s*)?ShowsResult\s*\}""",
        RegexOptions.Compiled);

    [Fact]
    public void EveryAnalysisTabView_HoldsExactlyOneResultRegion_GatedOnShowsResult()
    {
        var views = TabViews();

        // A rename or a move must not make this vacuous: the five shells ship today.
        Assert.True(
            views.Length >= 5,
            $"Only {views.Length} *TabView.axaml files were found under src/GalactiLog.App/Views/Analysis.");

        var offenders = new List<string>();
        foreach (var file in views)
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            var matches = RegionElement.Matches(text);

            if (matches.Count != 1)
            {
                offenders.Add($"{name}: {matches.Count} elements named {RegionName}, expected exactly 1");
                continue;
            }

            if (!GatedOnShowsResult.IsMatch(matches[0].Value))
            {
                offenders.Add($"{name}: its {RegionName} does not bind IsVisible to ShowsResult");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Each Analysis tab view holds one ResultRegion bound to ShowsResult, which is what keeps "
            + "spec 12.14's States table true of the chart without hiding the tab's own controls. "
            + "Offenders:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // The falsifiability check for the case above. Both rules are run over markup that offends them
    // and over markup that does not, so neither can ship inert the way ControlStyleScanTest's
    // Opacity rule did.
    [Fact]
    public void BothRules_FireOnOffendingMarkupAndAreSilentOnTheShippedShape()
    {
        const string shipped = """
            <StackPanel x:Name="ResultRegion" Spacing="8" IsVisible="{Binding ShowsResult}">
              <TextBlock Text="A chart lands here." />
            </StackPanel>
            """;

        const string attributesReordered = """
            <Border IsVisible="{Binding ShowsResult}"
                    x:Name="ResultRegion">
              <TextBlock Text="A chart lands here." />
            </Border>
            """;

        const string ungated = """
            <StackPanel x:Name="ResultRegion" Spacing="8">
              <TextBlock Text="A chart lands here." />
            </StackPanel>
            """;

        const string gatedOnSomethingElse = """
            <StackPanel x:Name="ResultRegion" IsVisible="{Binding IsStale}">
              <TextBlock Text="A chart lands here." />
            </StackPanel>
            """;

        const string noRegion = """
            <StackPanel Spacing="8">
              <TextBlock Text="A chart lands here." />
            </StackPanel>
            """;

        foreach (var (markup, label) in new[] { (shipped, "the shipped shape"), (attributesReordered, "reordered attributes") })
        {
            var matches = RegionElement.Matches(markup);
            Assert.True(matches.Count == 1, $"The region rule found {matches.Count} regions in {label}.");
            Assert.True(GatedOnShowsResult.IsMatch(matches[0].Value), $"The gate rule did not fire on {label}.");
        }

        Assert.Single(RegionElement.Matches(ungated));
        Assert.DoesNotMatch(GatedOnShowsResult, RegionElement.Match(ungated).Value);

        Assert.Single(RegionElement.Matches(gatedOnSomethingElse));
        Assert.DoesNotMatch(GatedOnShowsResult, RegionElement.Match(gatedOnSomethingElse).Value);

        Assert.Empty(RegionElement.Matches(noRegion));

        // Two regions in one file is the other half of "exactly one": a second gate is a second
        // place a later reader has to look.
        Assert.Equal(2, RegionElement.Matches(shipped + Environment.NewLine + attributesReordered).Count);
    }

    private static string[] TabViews()
    {
        var root = Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Views", "Analysis");
        Assert.True(Directory.Exists(root), $"Missing {root}");

        var files = Directory.GetFiles(root, "*TabView.axaml", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }
}
