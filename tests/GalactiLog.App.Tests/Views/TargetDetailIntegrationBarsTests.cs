using GalactiLog.App.Tests.TestSupport;
using static GalactiLog.App.Tests.TestSupport.TargetPartHost;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Views.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Data.Queries;
using Xunit;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views;

// The per-filter integration line under the log line renders one bar per filter, the longest at the full 160 px, and a target with no filters draws no row at all.
public class TargetDetailIntegrationBarsTests
{
    private static IntegrationBarsPart ShowTotals(Factory.Harness harness)
    {
        var view = new IntegrationBarsPart { DataContext = harness.ViewModel };
        Show(view);
        return view;
    }

    [AvaloniaFact]
    public void ThreeFilters_DrawThreeBars_ProportionalToTheLongest()
    {
        // A failure is a bar count other than three, widths that are all 160, or the longest
        // bar falling short of the column.
        using var harness = With(Factory.PopulatedTotals() with
        {
            FiltersUsed = ["Ha", "OIII", "SII"],
            IntegrationSecondsByFilter = new Dictionary<string, double>
            {
                ["Ha"] = 7_200d,
                ["OIII"] = 3_600d,
                ["SII"] = 1_800d,
            },
        });
        var view = ShowTotals(harness);

        Assert.True(view.Named<ItemsControl>("IntegrationBars").IsEffectivelyVisible);
        // The bars follow FilterOrder (SII, Ha, OIII), so the widths are read in that order.
        var widths = Bars(view).Select(bar => bar.Bounds.Width).ToList();
        Assert.Equal([40d, 160d, 80d], widths);
        Assert.Equal(160d, widths.Max());
        Assert.Equal(harness.ViewModel.Totals!.IntegrationBars.Select(bar => bar.BarWidth), widths);

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text);
        Assert.Contains("2.0 h", texts);
        Assert.Contains("0.5 h", texts);
    }

    [AvaloniaFact]
    public void LongFilterNames_TrimWithATip_AndShortOnesRenderWhole()
    {
        // A failure is a 40-character name spilling past the name column, or a name with no tip
        // to read it from.
        const string longName = "Hydrogen alpha three nanometre band pass";
        Assert.Equal(40, longName.Length);
        using var harness = With(Factory.PopulatedTotals() with
        {
            FiltersUsed = ["Lultimate", longName],
            IntegrationSecondsByFilter = new Dictionary<string, double> { ["Lultimate"] = 3_600d, [longName] = 1_800d },
        });
        var view = ShowTotals(harness);

        var names = view.Named<ItemsControl>("IntegrationBars")
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.Text is "Lultimate" or longName)
            .ToDictionary(block => block.Text!);

        Assert.Equal(TextTrimming.CharacterEllipsis, names[longName].TextTrimming);
        Assert.Equal(90d, names[longName].DesiredSize.Width);
        Assert.True(names[longName].TextLayout.TextLines[0].HasCollapsed, "the 40-character name is not trimmed");
        Assert.Equal(longName, ToolTip.GetTip(names[longName]));

        // The shared column is as wide as the long name, so the short one is judged by its own
        // text line, not by its arranged width.
        var shortLine = names["Lultimate"].TextLayout.TextLines[0];
        // The headless stub face measures the nine glyphs at exactly the 90 px cap: at the cap
        // is inside it, and the collapse flag is what says whether anything was cut.
        Assert.True(shortLine.Width <= 90d, $"the short name's text measures {shortLine.Width}, past the column's cap");
        Assert.False(shortLine.HasCollapsed, "the short name is trimmed");
        Assert.Equal("Lultimate", ToolTip.GetTip(names["Lultimate"]));

        // The name column is shared across items, so both bars start at the same x.
        Assert.Single(Bars(view).Select(bar => bar.Bounds.X).Distinct());
    }
}
