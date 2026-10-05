using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Controls;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Survey;
using GalactiLog.Data.Queries;
using Xunit;
using static GalactiLog.App.Tests.TestSupport.TargetPartHost;
using Factory = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

// Content case for the per-filter integration bars, hosted on the part alone.
public class IntegrationBarsPartTests
{
    [AvaloniaFact]
    public void NoFilters_DrawsNoBarRow()
    {
        using var harness = With(Factory.PopulatedTotals() with
        {
            FiltersUsed = [],
            IntegrationSecondsByFilter = new Dictionary<string, double>(),
        });
        var view = new IntegrationBarsPart { DataContext = harness.ViewModel };
        Show(view);

        Assert.False(view.Named<ItemsControl>("IntegrationBars").IsEffectivelyVisible);
        Assert.Empty(Bars(view));
    }

    private static readonly DateOnly Newer = Factory.LastSession;
    private static readonly DateOnly Older = Factory.FirstSession;

    // SII 9.997 h, Ha 10 h (the longest), OIII 9 h: OIII is short by 1.0 h, SII by 0.003 h.
    private static Factory.Harness FullPage()
    {
        var totals = Factory.PopulatedTotals() with
        {
            FiltersUsed = ["SII", "Ha", "OIII"],
            IntegrationSecondsByFilter = new Dictionary<string, double> { ["SII"] = 35_990d, ["Ha"] = 36_000d, ["OIII"] = 32_400d },
        };
        NightFilterOverview Row(DateOnly night, string filter, double seconds)
            => new(night, filter, seconds, 1, [], null, null, null, null, null);
        return Factory.Create(get: _ => Factory.PopulatedDetail(totals: totals) with
        {
            NightFilters =
            [
                Row(Newer, "SII", 20_000d), Row(Newer, "Ha", 18_000d), Row(Newer, "OIII", 14_400d),
                Row(Older, "SII", 15_990d), Row(Older, "Ha", 18_000d), Row(Older, "OIII", 18_000d),
            ],
        }).Settle();
    }

    private static IntegrationBarsPart ShowFull(Factory.Harness harness)
    {
        var view = new IntegrationBarsPart { DataContext = harness.ViewModel, IsFull = true };
        Show(view);
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static List<ItemsControl> Tracks(Control view)
        => [.. view.Named<ItemsControl>("FullIntegrationBars").GetVisualDescendants().OfType<ItemsControl>().Where(items => items.Name == "Track")];

    private static List<Border> Segments(ItemsControl track)
        => [.. track.GetVisualDescendants().OfType<Border>().Where(border => border.Height == 14d && border.Width > 0d)];

    [AvaloniaFact]
    public void Full_DrawsOneThirtyPixelRowPerFilter_AndHidesTheSmallBars()
    {
        // A failure is a row height other than 30, a missing row, or the small bars still showing.
        using var harness = FullPage();
        var view = ShowFull(harness);

        Assert.True(view.Named<ItemsControl>("FullIntegrationBars").IsEffectivelyVisible);
        Assert.False(view.Named<ItemsControl>("IntegrationBars").IsEffectivelyVisible);
        var rows = view.Named<ItemsControl>("FullIntegrationBars").GetVisualDescendants().OfType<Grid>().Where(grid => grid.Height == 30d).ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(30d, row.Bounds.Height));
    }

    [AvaloniaFact]
    public void Full_SegmentsAreOnePerNight_OnePixelApart_OnOneScale()
    {
        // A failure is a segment count other than the nights, a gap other than 1 px, or a scale
        // that is not the longest filter (Ha's two segments plus gaps fill the track).
        using var harness = FullPage();
        var view = ShowFull(harness);

        var tracks = Tracks(view);
        Assert.Equal(3, tracks.Count);
        var ha = Segments(tracks[1]);
        Assert.Equal(2, ha.Count);
        Assert.Equal(tracks[1].Bounds.Width, ha.Sum(segment => segment.Bounds.Width + 1d), 0.5);
        Assert.Equal(1d, ((Visual)ha[1]).TranslatePoint(new Point(0, 0), tracks[1])!.Value.X - ((Visual)ha[0]).TranslatePoint(new Point(ha[0].Bounds.Width, 0), tracks[1])!.Value.X, 0.5);
        Assert.Equal(tracks[0].Bounds.Width, tracks[1].Bounds.Width);
        Assert.Equal("2025-12-07: 5.0 h", ToolTip.GetTip(ha[0]));
        Assert.Equal(14d, ha[0].Bounds.Height);
    }

    [AvaloniaFact]
    public void Full_GoalTickAndShortByText_AppearOnlyWhereTheFilterIsShort()
    {
        // A failure is a tick on the longest filter, short-by text under 0.05 h, or a missing
        // "short by 1.0 h" on OIII.
        using var harness = FullPage();
        var view = ShowFull(harness);

        var ticks = view.Named<ItemsControl>("FullIntegrationBars").GetVisualDescendants().OfType<Border>()
            .Where(border => border.Name == "GoalTick" && border.IsEffectivelyVisible).ToList();
        Assert.Equal(2, ticks.Count);
        Assert.All(ticks, tick => Assert.Equal(1d, tick.Bounds.Width));
        Assert.Equal(["short by 1.0 h"], VisibleTexts(view).Where(text => text.StartsWith("short by")));
    }

    [AvaloniaFact]
    public void Full_GoalTickSitsAtTheGoalFractionOfItsTrack_LessOnePixel()
    {
        // A failure is a tick at the track's left edge or anywhere but the goal.
        using var harness = FullPage();
        var view = ShowFull(harness);

        var bars = harness.ViewModel.Totals!.IntegrationBars;
        var tracks = Tracks(view);
        var ticks = view.Named<ItemsControl>("FullIntegrationBars").GetVisualDescendants().OfType<Border>()
            .Where(border => border.Name == "GoalTick").ToList();
        foreach (var name in new[] { "SII", "OIII" })
        {
            var index = bars.ToList().FindIndex(bar => bar.FilterName == name);
            var expected = tracks[index].Bounds.Width * bars[index].GoalFraction!.Value - 1d;
            Assert.True(expected > 1d);
            Assert.Equal(expected, ticks[index].Bounds.X, 0.5);
        }
    }

    [AvaloniaFact]
    public void Small_IsUnchangedWhenNotFull()
    {
        // A failure is the full rows showing without the layout asking for them.
        using var harness = FullPage();
        var view = new IntegrationBarsPart { DataContext = harness.ViewModel };
        Show(view);

        Assert.False(view.Named<ItemsControl>("FullIntegrationBars").IsEffectivelyVisible);
        Assert.Equal(3, Bars(view).Count);
    }
}
