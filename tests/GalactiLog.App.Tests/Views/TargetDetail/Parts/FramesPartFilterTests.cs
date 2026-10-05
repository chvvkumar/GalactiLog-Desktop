using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Tests.ViewModels;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Data.Queries;
using Xunit;
using static GalactiLog.App.Tests.Views.TargetDetail.Parts.NightPartsTestKit;
using static GalactiLog.App.Tests.Views.TargetDetail.Parts.ThumbnailKit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Page = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

// P24 R6 and R22: the frames strip's Filter segment, the per-metric outlier pills, the rig pills
// and Clear filters.
public class FramesPartFilterTests
{
    /// <summary>Six frames carrying an HFR, an eccentricity and a FWHM and nothing else: two
    /// flagged HFR outliers, one an eccentricity outlier, no FWHM outlier. So the night has three
    /// pills with three different counts and lacks the Stars and RMS pills. With rig names, the
    /// frames alternate between them so the table is multi-rig.</summary>
    private static IReadOnlyList<FrameRow> FlaggedFrames(DateOnly date, string[]? rigs = null)
        => [.. Enumerable.Range(0, 6).Select(i => FrameTableViewModelTests.Frame(
            fileName: $"frame_{i:000}.fits",
            captureDate: date.ToDateTime(new TimeOnly(21, 0)).AddMinutes(i),
            filterUsed: "Ha",
            exposureTime: 300d,
            medianHfr: 2d + (i * 0.01d),
            eccentricity: 0.4d,
            fwhm: 2.1d,
            isHfrOutlier: i < 2,
            isEccentricityOutlier: i == 5,
            rig: rigs?[i % rigs.Length]))];

    private static (FramesPart Part, Cards.Harness Harness, FrameTableViewModel Table) Host(
        IReadOnlyList<FrameRow>? frames = null, bool compact = false, string[]? rigs = null, double width = 1600)
    {
        var date = Page.LastSession;
        frames ??= FlaggedFrames(date, rigs);
        var harness = Cards.Create(detail: Cards.PopulatedDetail(sessionDate: date, insights: []) with { Frames = frames });
        var table = Table(frames, harness.TargetPage);
        harness.FrameTableResult = table;
        harness.Card.IsExpanded = true;
        harness.Settle();

        var part = new FramesPart { DataContext = harness.Card, IsCompact = compact };
        Show(part, width);
        Dispatcher.UIThread.RunJobs();
        return (part, harness, table);
    }

    /// <summary>A night on the named rigs with three outlier pills and their flagged frames, the
    /// widest the Filter segment gets: the shape the layout pins measure.</summary>
    internal static (FramesPart Part, Cards.Harness Harness, FrameTableViewModel Table) TwoRigHost(
        string[] rigs, bool compact, double width)
        => Host(null, compact, rigs, width);

    private static bool Shown(FramesPart part, string name)
        => part.NamedOrNull<Control>(name) is { } control && control.IsEffectivelyVisible && part.GetVisualDescendants().Contains(control);

    /// <summary>The outlier pills in their drawn order, by label.</summary>
    internal static IReadOnlyList<ToggleButton> Pills(Control part)
        => part.NamedOrNull<ItemsControl>("OutlierPillRow") is { } row ? [.. row.GetVisualDescendants().OfType<ToggleButton>()] : [];

    private static ToggleButton Pill(Control part, string metric)
        => Assert.Single(Pills(part), pill => pill.Content is string label && label.StartsWith(metric + " outliers", StringComparison.Ordinal));

    private static void Click(FramesPart part, Control target)
        => TargetPartHost.Click(Assert.IsAssignableFrom<Window>(part.GetVisualRoot()), target);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePills_AreOnePerMetricTheNightHas_InColumnOrder_WithTheFlaggedCount(bool compact)
    {
        // Red if a pill is drawn for a metric no frame carries (Stars, RMS here), missing for one
        // the night has, out of column order, or its count is not the flagged frames.
        var (part, harness, _) = Host(compact: compact);
        using var scope = harness;

        Assert.Equal(
            ["HFR outliers (2)", "Ecc outliers (1)", "FWHM outliers (0)"],
            Pills(part).Select(pill => pill.Content as string));
        Assert.All(Pills(part), pill => Assert.True(pill.IsEffectivelyVisible));
    }

    [AvaloniaFact]
    public void ANightWithNoMetricValues_DrawsNoPill_AndNoClear()
    {
        // Red if a pill or Clear filters is drawn with nothing to filter on a single-rig night.
        var date = Page.LastSession;
        var bare = Enumerable.Range(0, 3).Select(i => FrameTableViewModelTests.Frame(
            fileName: $"bare_{i:000}.fits", captureDate: date.ToDateTime(new TimeOnly(21, 0)).AddMinutes(i))).ToList();
        var (part, harness, _) = Host(bare);
        using var scope = harness;

        Assert.Empty(Pills(part));
        Assert.False(Shown(part, "ClearFiltersButton"), "Clear filters is drawn with nothing to clear on a single-rig night");
    }

    [AvaloniaFact]
    public void APillWhoseCountIsZero_IsDisabled()
    {
        // Red if the FWHM pill, with values but no flagged frame, can be pressed, or a pill with
        // flagged frames cannot.
        var (part, harness, _) = Host();
        using var scope = harness;

        Assert.False(Pill(part, "FWHM").IsEffectivelyEnabled, "the FWHM pill is enabled at a count of 0");
        Assert.True(Pill(part, "HFR").IsEffectivelyEnabled, "the HFR pill is disabled at a count of 2");
    }

    [AvaloniaFact]
    public void ThePills_FollowTheActiveFilter()
    {
        // Red if a pill's checked state does not track the table's filter, including a clear the
        // table performs itself (Escape).
        var (part, harness, table) = Host();
        using var scope = harness;
        var hfr = Pill(part, "HFR");
        var ecc = Pill(part, "Ecc");
        Assert.False(hfr.IsChecked == true || ecc.IsChecked == true, "a pill is checked with no filter");

        harness.Card.ShowOutliersCommand.Execute(FrameOutlierFilter.Hfr);
        Dispatcher.UIThread.RunJobs();
        Assert.True(hfr.IsChecked, "the HFR pill is unchecked while the HFR filter is on");
        Assert.False(ecc.IsChecked == true, "the Ecc pill is checked while the HFR filter is on");

        harness.Card.ShowOutliersCommand.Execute(FrameOutlierFilter.Eccentricity);
        Dispatcher.UIThread.RunJobs();
        Assert.False(hfr.IsChecked == true, "the HFR pill is checked while the Ecc filter is on");
        Assert.True(ecc.IsChecked, "the Ecc pill is unchecked while the Ecc filter is on");

        table.ClearFilterOrSelectionCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(FrameOutlierFilter.None, table.OutlierFilter);
        Assert.False(hfr.IsChecked == true || ecc.IsChecked == true, "a pill stays checked after the table cleared its own filter");
    }

    [AvaloniaFact]
    public void PressingAPill_SetsTheFilter_AndPressingItCheckedClearsIt()
    {
        // Red if a press on the pill does not reach the table's filter, a second press on the
        // checked pill leaves the filter on, or a press on another pill does not take the filter
        // over (one filter at a time).
        var (part, harness, table) = Host();
        using var scope = harness;
        var hfr = Pill(part, "HFR");
        var ecc = Pill(part, "Ecc");

        Click(part, hfr);
        Assert.Equal(FrameOutlierFilter.Hfr, table.OutlierFilter);
        Assert.True(hfr.IsChecked);
        Assert.Equal(2, table.Rows.Count);

        Click(part, ecc);
        Assert.Equal(FrameOutlierFilter.Eccentricity, table.OutlierFilter);
        Assert.False(hfr.IsChecked == true, "the HFR pill stays checked after the Ecc pill took the filter");
        Assert.Single(table.Rows);

        Click(part, ecc);
        Assert.Equal(FrameOutlierFilter.None, table.OutlierFilter);
        Assert.False(ecc.IsChecked == true, "the pill stays checked after the press that cleared the filter");
        Assert.Equal(6, table.Rows.Count);
    }

    [AvaloniaFact]
    public void ClearFilters_IsEnabledByTheOutlierFilterOrAnUncheckedRigPill_AndRestoresBoth()
    {
        // Red if Clear is enabled with nothing filtered, stays disabled under either filter, or
        // leaves the outlier filter on or a rig pill off after a press.
        var (part, harness) = RigHost(card => new FramesPart { DataContext = card }, [RigA, RigB]);
        using var scope = harness;
        var table = harness.Card.FrameTable!;
        var clear = part.Named<Button>("ClearFiltersButton");
        Assert.True(Shown(part, "ClearFiltersButton"), "Clear filters is missing on a two-rig night");
        Assert.False(clear.IsEffectivelyEnabled, "Clear filters is enabled with nothing filtered");

        harness.Card.ShowOutliersCommand.Execute(FrameOutlierFilter.Hfr);
        Dispatcher.UIThread.RunJobs();
        Assert.True(clear.IsEffectivelyEnabled, "Clear filters is disabled under the outlier filter");

        table.ClearFilterOrSelectionCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(clear.IsEffectivelyEnabled, "Clear filters stays enabled after the table cleared its filter");

        table.RigPills[1].IsSelected = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(clear.IsEffectivelyEnabled, "Clear filters is disabled with a rig pill off");

        harness.Card.ShowOutliersCommand.Execute(FrameOutlierFilter.Eccentricity);
        Dispatcher.UIThread.RunJobs();
        Click(part, clear);

        Assert.Equal(FrameOutlierFilter.None, table.OutlierFilter);
        Assert.All(table.RigPills, pill => Assert.True(pill.IsSelected));
        Assert.All(part.Named<ItemsControl>("RigPillRow").GetVisualDescendants().OfType<ToggleButton>(), pill => Assert.True(pill.IsChecked));
        Assert.False(clear.IsEffectivelyEnabled, "Clear filters stays enabled after it cleared both");
    }

    [AvaloniaFact]
    public void TheRigPills_SitInTheFilterSegment_AndASingleRigNightShowsNone()
    {
        // Red if the rig pills left the Filter segment, or a single-rig night draws one.
        var two = RigHost(card => new FramesPart { DataContext = card }, [RigA, RigB]);
        using (two.Harness)
        {
            var row = two.Host.Named<ItemsControl>("RigPillRow");
            Assert.True(row.IsEffectivelyVisible);
            Assert.Equal(2, row.GetVisualDescendants().OfType<ToggleButton>().Count());
            Assert.Contains(two.Host.Named<Control>("FilterSegment"), row.GetVisualAncestors());
        }

        var one = RigHost(card => new FramesPart { DataContext = card }, [RigA]);
        using (one.Harness)
        {
            var row = one.Host.Named<ItemsControl>("RigPillRow");
            Assert.False(row.IsEffectivelyVisible, "a single-rig night draws the rig pill row");
            Assert.Empty(row.GetVisualDescendants().OfType<ToggleButton>());
        }
    }
}
