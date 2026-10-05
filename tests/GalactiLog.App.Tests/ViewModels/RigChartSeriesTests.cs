using System.Collections.ObjectModel;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using Xunit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Page = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

/// <summary>
/// Spec 13's "Rig series" (PAR-004): the five-row dash table, the per-rig split on both Target
/// detail charts, and the rule that the colour still comes from the metric.
/// </summary>
/// <remarks>
/// Every assertion is against the <c>Series</c> the view-model built, never against rendered
/// output (spec 18.3). The single-rig twin of each case is what the roadmap's Verify clause asks
/// for: a single-rig night and a single-rig target draw exactly what they drew before this phase.
/// </remarks>
public class RigChartSeriesTests
{
    private const string RigA = "Alpha / Cam";
    private const string RigB = "Bravo / Cam";

    private sealed class Fixture : IDisposable
    {
        private GraphSettings _document;

        public Fixture(string[]? metrics = null)
        {
            _document = new GraphSettings
            {
                EnabledMetrics = metrics ?? ["hfr"],
                EnabledFilters = ["overall"],
            };

            var writer = new GraphSettingsWriter(
                () => _document,
                value =>
                {
                    _document = value;
                    Saves++;
                });

            Selection = new ChartSelectionViewModel(
                _document,
                writer,
                () => new AliasMap(new Dictionary<string, FilterSetting>(), new EquipmentSettings()));
        }

        public ChartSelectionViewModel Selection { get; }

        /// <summary>How many times the graph document was written. The rig selection writes none.
        /// </summary>
        public int Saves { get; private set; }

        public GraphSettings Document => _document;

        public SessionChartViewModel? Chart { get; private set; }

        public SessionChartViewModel Build(SessionDetail detail)
            => Chart = new SessionChartViewModel(detail, Selection);

        public void Dispose() => Chart?.Dispose();
    }

    // ---- fixtures ------------------------------------------------------------------------

    private static FrameRow Frame(int index, string rig, double hfr, double? fwhm = null)
        => FrameTableViewModelTests.Frame(
            fileName: $"frame_{index:0000}.fits",
            captureDate: new DateTime(2025, 12, 7, 21, 0, 0, DateTimeKind.Utc).AddMinutes(index * 5),
            filterUsed: "Ha",
            exposureTime: 300d,
            medianHfr: hfr,
            fwhm: fwhm,
            rig: rig);

    private static RigGroup Group(int index, string label, int frameCount = 2)
        => new(index, label, label.Split(" / ")[0], "Cam", frameCount, frameCount * 300d, null, null);

    private static SessionDetail Detail(IReadOnlyList<RigGroup> rigs, IReadOnlyList<FrameRow> frames)
        => Cards.PopulatedDetail() with { Rigs = rigs, Frames = frames };

    private static SessionDetail SingleRig()
        => Detail(
            [Group(0, RigA)],
            [Frame(0, RigA, 2.0d), Frame(1, RigA, 2.1d)]);

    private static SessionDetail TwoRigs()
        => Detail(
            [Group(0, RigA), Group(1, RigB)],
            [Frame(0, RigA, 2.0d), Frame(1, RigB, 3.0d), Frame(2, RigA, 2.1d), Frame(3, RigB, 3.1d)]);

    /// <summary>
    /// Whether a series is drawn dashed. The pattern itself cannot be read back: rc5.4's
    /// <c>DashEffect</c> captures its array in a primary-constructor field and exposes no
    /// accessor, and reading a compiler-generated field by reflection is a test that breaks on a
    /// package bump for no gain. The table's exact values are pinned by
    /// <see cref="RigDash_IsTheSpecifiedTable"/> against the one method that produces them, and
    /// what these cases need from the series is whether the rig's mark is there and stable.
    /// </summary>
    private static bool IsDashed(ISeries series)
        => (Assert.IsType<LineSeries<double?>>(series).Stroke as SolidColorPaint)?.PathEffect
            is DashEffect;

    // ---- the dash table (spec 13, questions.md Q9) -----------------------------------------

    [Theory]
    [InlineData(0, new double[0])]
    [InlineData(1, new double[] { 6, 3 })]
    [InlineData(2, new double[] { 2, 2 })]
    [InlineData(3, new double[] { 8, 3, 2, 3 })]
    [InlineData(4, new double[] { 1, 3 })]
    public void RigDash_IsTheSpecifiedTable(int index, double[] expected)
        => Assert.Equal(expected, MetricChartViewModel.RigDash(index));

    [Fact]
    public void RigDash_RepeatsFromTheTopAtSix()
    {
        // Spec 13's table has five rows and repeats from the top, which is where it parts company
        // with the web's six-entry RIG_DASH_PATTERNS (questions.md Q9). Index 5 is solid here and
        // is [10, 4, 2, 4] there.
        Assert.Equal(MetricChartViewModel.RigDash(0), MetricChartViewModel.RigDash(5));
        Assert.Equal(MetricChartViewModel.RigDash(1), MetricChartViewModel.RigDash(6));
        Assert.Equal(MetricChartViewModel.RigDash(4), MetricChartViewModel.RigDash(9));
    }

    [Fact]
    public void RigZero_IsSolid()
    {
        // Solid means no dash effect at all, not a dash array that happens to look solid.
        Assert.Empty(MetricChartViewModel.RigDash(0));

        using var fixture = new Fixture();
        var chart = fixture.Build(SingleRig());

        Assert.False(IsDashed(Assert.Single(chart.Series)));
    }

    [Fact]
    public void RigDash_HandsBackACopy()
    {
        var first = MetricChartViewModel.RigDash(1);
        first[0] = 99d;

        Assert.Equal([6d, 3d], MetricChartViewModel.RigDash(1));
    }

    // ---- the per-rig split -----------------------------------------------------------------

    [Fact]
    public void ASingleRigNight_DrawsOneSeriesPerMetric()
    {
        using var fixture = new Fixture(metrics: ["hfr", "fwhm"]);
        var chart = fixture.Build(Detail(
            [Group(0, RigA)],
            [Frame(0, RigA, 2.0d, fwhm: 1.9d), Frame(1, RigA, 2.1d, fwhm: 2.0d)]));

        Assert.Equal(2, chart.Series.Count);

        // And no series name carries a rig suffix, which is the roadmap's Verify clause on this
        // surface.
        Assert.All(chart.Series, series => Assert.DoesNotContain("[", series.Name!));
    }

    [Fact]
    public void ATwoRigNight_DrawsOneSeriesPerMetricPerRig()
    {
        using var fixture = new Fixture(metrics: ["hfr"]);
        var chart = fixture.Build(TwoRigs());

        Assert.Equal(2, chart.Series.Count);
        Assert.Equal($"HFR (px) [{RigA}]", chart.Series[0].Name);
        Assert.Equal($"HFR (px) [{RigB}]", chart.Series[1].Name);

        Assert.False(IsDashed(chart.Series[0]));
        Assert.True(IsDashed(chart.Series[1]));
    }

    [Fact]
    public void ATwoRigNight_DrawsEachRigAsOneConnectedLine()
    {
        // Verification blocker B1. TwoRigs() interleaves the two rigs in capture order (A, B, A,
        // B), so every value of a rig's series sits between two nulls. With EnableNullSplitting on,
        // which is spec 13's rule for a night that measured nothing, every segment was split to a
        // single point and no stroke survived to carry the dash: both rigs rendered identically and
        // the pills went on drawing dashed samples the chart did not honour.
        //
        // The nulls in a per-rig series are the other rig's frames, not missing measurements, so
        // the split is off for these series alone and each rig's own frames join into one line at
        // their real frame indices.
        using var fixture = new Fixture(metrics: ["hfr"]);
        var chart = fixture.Build(TwoRigs());

        var rigA = Assert.IsType<LineSeries<double?>>(chart.Series[0]);
        var rigB = Assert.IsType<LineSeries<double?>>(chart.Series[1]);

        Assert.False(rigA.EnableNullSplitting);
        Assert.False(rigB.EnableNullSplitting);

        // Each series spans the night's four frames and draws its own two, at the frame indices
        // those frames actually occupy, which is what keeps the X axis honest.
        foreach (var (series, drawnAt) in new[] { (rigA, new[] { 0, 2 }), (rigB, new[] { 1, 3 }) })
        {
            var values = series.Values!.ToList();
            Assert.Equal(4, values.Count);
            Assert.Equal(2, values.Count(value => value is not null));
            Assert.Equal(drawnAt, Enumerable.Range(0, values.Count).Where(i => values[i] is not null));
        }

        // One connected segment, not two: with the split off the two drawn points are joined
        // across the other rig's frame between them rather than left as bare markers.
        Assert.False(rigA.EnableNullSplitting);
        Assert.True(IsDashed(rigB));
        Assert.False(IsDashed(rigA));
    }

    [Fact]
    public void ASingleRigNight_KeepsItsGapsAtMissingValues()
    {
        // The other half of B1: nothing about the single-rig chart moved. Spec 13's "gaps
        // preserved" still holds there, because on that chart a null is a frame that measured
        // nothing rather than another rig's frame.
        using var fixture = new Fixture(metrics: ["hfr", "fwhm"]);
        var chart = fixture.Build(Detail(
            [Group(0, RigA)],
            [Frame(0, RigA, 2.0d, fwhm: 1.9d), Frame(1, RigA, 2.1d), Frame(2, RigA, 2.2d, fwhm: 2.1d)]));

        foreach (var series in chart.Series)
        {
            Assert.True(Assert.IsType<LineSeries<double?>>(series).EnableNullSplitting);
        }

        // The FWHM series has the gap the middle frame's missing value makes.
        var fwhm = Assert.IsType<LineSeries<double?>>(
            chart.Series.Single(series => series.Name!.StartsWith("FWHM", StringComparison.Ordinal)));
        Assert.Equal([1.9d, null, 2.1d], fwhm.Values!);
    }

    [Fact]
    public void TheSeriesColour_StillComesFromTheMetric()
    {
        using var fixture = new Fixture(metrics: ["hfr"]);
        var chart = fixture.Build(TwoRigs());

        // Spec 13: the dash is the rig's only mark, so two rigs of one metric are the same hue.
        var first = (SolidColorPaint)Assert.IsType<LineSeries<double?>>(chart.Series[0]).Stroke!;
        var second = (SolidColorPaint)Assert.IsType<LineSeries<double?>>(chart.Series[1]).Stroke!;

        Assert.Equal(first.Color, second.Color);
    }

    [Fact]
    public void TheDashIndex_ComesFromTheFullRigList_NotTheEnabledSubset()
    {
        using var fixture = new Fixture(metrics: ["hfr"]);
        fixture.Selection.OfferRigs([RigA, RigB]);
        var chart = fixture.Build(TwoRigs());

        fixture.Selection.Rigs[0].IsSelected = false;

        // Rig B stays dashed rather than dropping to solid: if the index came from the enabled
        // subset it would now be index 0, and index 0 is the one pattern that is no pattern.
        var series = Assert.Single(chart.Series);
        Assert.Equal($"HFR (px) [{RigB}]", series.Name);
        Assert.True(IsDashed(series));
    }

    [Fact]
    public void AnUncheckedRig_RemovesItsSeries()
    {
        using var fixture = new Fixture(metrics: ["hfr"]);
        fixture.Selection.OfferRigs([RigA, RigB]);
        var chart = fixture.Build(TwoRigs());

        Assert.Equal(2, chart.Series.Count);

        fixture.Selection.Rigs[1].IsSelected = false;

        Assert.Equal($"HFR (px) [{RigA}]", Assert.Single(chart.Series).Name);
    }

    [Fact]
    public void TheRigSelection_IsNotPersisted()
    {
        using var fixture = new Fixture(metrics: ["hfr"]);
        fixture.Selection.OfferRigs([RigA, RigB]);
        var before = fixture.Saves;

        fixture.Selection.Rigs[0].IsSelected = false;
        fixture.Selection.Rigs[0].IsSelected = true;

        // Spec 13: the rig state is per visit and there is no graph key for it, so nothing is
        // written and no key appears in the document.
        Assert.Equal(before, fixture.Saves);
        Assert.Equal(["overall"], fixture.Document.EnabledFilters);
        Assert.Equal(["hfr"], fixture.Document.EnabledMetrics);
    }

    [Fact]
    public void OfferRigs_KeepsAnUncheckedRigUnchecked()
    {
        using var fixture = new Fixture();
        fixture.Selection.OfferRigs([RigA, RigB]);
        fixture.Selection.Rigs[1].IsSelected = false;

        fixture.Selection.OfferRigs([RigA, RigB]);

        Assert.True(fixture.Selection.Rigs[0].IsSelected);
        Assert.False(fixture.Selection.Rigs[1].IsSelected);
        Assert.Equal([RigA], fixture.Selection.EnabledRigs);
    }

    [Fact]
    public void OfferRigs_WithTheListItAlreadyHas_RaisesNothingAndKeepsThePills()
    {
        // Phase review P3-2. Since the Task 5 fix pass this runs on every card Detail publication,
        // which on the ordinary single-rig target is every night the reader opens. Rebuilding the
        // list raises Rigs, and every live chart answers that by rebuilding its pill row and its
        // whole series set, so an unconditional offer cost one rebuild per chart per expansion for
        // a list that had not changed.
        using var fixture = new Fixture();
        fixture.Selection.OfferRigs([RigA, RigB]);

        List<string?> raised = [];
        fixture.Selection.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var pills = fixture.Selection.Rigs.ToList();

        fixture.Selection.OfferRigs([RigA, RigB]);

        Assert.Empty(raised);
        Assert.Equal(pills, fixture.Selection.Rigs);

        // A genuinely different list, and a reordered one, are both changes and both rebuild.
        fixture.Selection.OfferRigs([RigB, RigA]);
        Assert.Contains(nameof(ChartSelectionViewModel.Rigs), raised);
        Assert.Equal([RigB, RigA], fixture.Selection.Rigs.Select(pill => pill.Key));
    }

    [Fact]
    public void OfferRigs_RefusesAnEmptyOffer()
    {
        using var fixture = new Fixture();
        fixture.Selection.OfferRigs([RigA, RigB]);

        fixture.Selection.OfferRigs([]);

        // An empty list is what a page that has not finished loading looks like, and the same
        // refusal OfferFilters makes for the same reason.
        Assert.Equal(2, fixture.Selection.Rigs.Count);
        Assert.True(fixture.Selection.HasRigPills);
    }

    [Fact]
    public void ASingleRigTarget_DrawsNoRigPillRow()
    {
        using var fixture = new Fixture();
        fixture.Selection.OfferRigs([RigA]);

        Assert.False(fixture.Selection.HasRigPills);
        Assert.Single(fixture.Chart?.RigPills ?? fixture.Build(SingleRig()).RigPills);
    }

    [Fact]
    public void ExpandingATwoRigCard_OffersTheRigsToBothCharts()
    {
        // Review P2-1, and the ordinary flow: nothing here calls OfferRigs by hand. A page builds
        // its cross-session chart over cards that have not loaded yet, and a card's Detail arrives
        // only when the reader expands it. Before this fix the rigs were offered once, at
        // construction, when no card had a detail to offer rigs from, so Rigs stayed empty for the
        // whole visit: no pill row on either chart and no per-rig split on the cross-session one.
        using var fixture = new Fixture(metrics: ["hfr"]);
        using var harness = Cards.Create(
            overview: Page.Session(Page.LastSession),
            detail: TwoRigs());

        ObservableCollection<SessionCardViewModel> sessions = [harness.Card];
        using var target = new TargetChartViewModel(sessions, fixture.Selection);

        // The precondition: an unexpanded card carries no rig groups, so there is nothing to offer
        // and the empty offer is refused.
        Assert.False(fixture.Selection.HasRigPills);
        Assert.Empty(target.RigPills);

        harness.Card.IsExpanded = true;
        harness.Settle();

        // The card published its detail, which is the same notification the per-filter medians
        // already rode in on.
        Assert.True(fixture.Selection.HasRigPills);
        Assert.Equal([RigA, RigB], fixture.Selection.Rigs.Select(pill => pill.Key));
        Assert.All(fixture.Selection.Rigs, pill => Assert.True(pill.IsSelected));

        // The cross-session chart now splits per rig and draws its pill row, keeping the unsplit
        // whole-night series beside the two (phase review P2-1, ruled option 1): that series is
        // the one every night contributes to whether or not the reader has opened it. It is the
        // solid line here, so the rig series take dash rows 1 and 2 of spec 13's table rather than
        // rows 0 and 1, and both of them are dashed.
        Assert.Equal(2, target.RigPills.Count);
        Assert.Equal(3, target.Series.Count);
        Assert.Equal("HFR (px)", target.Series[0].Name);
        Assert.Equal($"HFR (px) [{RigA}]", target.Series[1].Name);
        Assert.Equal($"HFR (px) [{RigB}]", target.Series[2].Name);
        Assert.False(IsDashed(target.Series[0]));
        Assert.True(IsDashed(target.Series[1]));
        Assert.True(IsDashed(target.Series[2]));

        // And a per-night chart built from the same shared selection has the pill row too, which
        // is what spec 13's "on both charts" asks for.
        var session = fixture.Build(TwoRigs());
        Assert.Equal(2, session.RigPills.Count);
        Assert.Equal(2, session.Series.Count);
    }

    [Fact]
    public void ASingleRigCard_OffersOneRigAndDrawsNoPillRow()
    {
        // The same path on the night that is most of a library: one rig is offered, HasRigPills
        // stays false, and the chart draws exactly what it drew before this phase.
        using var fixture = new Fixture(metrics: ["hfr"]);
        using var harness = Cards.Create(
            overview: Page.Session(Page.LastSession),
            detail: SingleRig());

        ObservableCollection<SessionCardViewModel> sessions = [harness.Card];
        using var target = new TargetChartViewModel(sessions, fixture.Selection);

        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Single(fixture.Selection.Rigs);
        Assert.False(fixture.Selection.HasRigPills);
        Assert.DoesNotContain("[", Assert.Single(target.Series).Name!);
    }

    [Fact]
    public void AMultiRigTarget_KeepsAPointPerNightOnTheWholeNightSeries()
    {
        // Phase review P2-1, the case its ruling asks for. Three nights, one of them expanded.
        //
        // Before the fix, offering two rigs replaced the unsplit series rather than adding to it,
        // and the per-rig accessor reads card.Detail, which only an expanded card has. The chart
        // therefore dropped from one point per night to one point on the one night the reader had
        // opened, on precisely the targets PAR-004 exists for, and on the first click of
        // verification bar item 3. The proving case for the split used a collection of exactly one
        // card, so there was no unexpanded night for the nulls to land on.
        using var fixture = new Fixture(metrics: ["hfr"]);
        var dates = new[]
        {
            Page.LastSession.AddDays(-2),
            Page.LastSession.AddDays(-1),
            Page.LastSession,
        };

        using var oldest = Cards.Create(overview: Page.Session(dates[0]), detail: TwoRigs());
        using var middle = Cards.Create(overview: Page.Session(dates[1]), detail: TwoRigs());
        using var newest = Cards.Create(overview: Page.Session(dates[2]), detail: TwoRigs());

        // Newest first, which is the order the page's own collection is in.
        ObservableCollection<SessionCardViewModel> sessions =
            [newest.Card, middle.Card, oldest.Card];
        using var target = new TargetChartViewModel(sessions, fixture.Selection);

        // graph.default_chart_sessions is 1 in an unwritten document, and the scope control is
        // what a reader clicks to see the season. This case is about the season.
        target.ShowAllSessions = true;

        // One night opened, which is what a visit looks like.
        newest.Card.IsExpanded = true;
        newest.Settle();

        Assert.True(fixture.Selection.HasRigPills);
        Assert.Equal(3, target.Series.Count);

        // The whole-night series carries a point for every night, because its source is the
        // overview each ledger row already has.
        var whole = Assert.IsType<LineSeries<double?>>(target.Series[0]);
        Assert.Equal("HFR (px)", whole.Name);
        Assert.Equal(3, whole.Values!.Count());
        Assert.All(whole.Values!, value => Assert.NotNull(value));

        // Each rig series carries three slots and exactly one value, the opened night's, with the
        // other two null so the gap machinery renders them as gaps rather than as zeroes.
        foreach (var index in new[] { 1, 2 })
        {
            var rig = Assert.IsType<LineSeries<double?>>(target.Series[index]);
            Assert.Contains("[", rig.Name!, StringComparison.Ordinal);
            Assert.Equal(3, rig.Values!.Count());
            Assert.Single(rig.Values!, value => value is not null);
        }
    }

    [Fact]
    public void AMultiRigTarget_WithAFilterPillOn_DrawsNoUnsuffixedFilterSeries()
    {
        // Fix-wave review P2-1F. The whole-night spec reinstated by the P2-1 fix ran the per-filter
        // branch as well, and FilterMedian with a null label reads
        // detail.FilterMedians.FirstOrDefault(name matches). On a multi-rig night
        // SessionDetailQuery.SplitPerRig builds that block per rig and stamps every row with its
        // rig's label, so there is no night-level row to find: the lookup returned the first rig's
        // and the chart drew a solid, unsuffixed "HFR (px) (Ha)" line that was rig A's median under
        // the night's name, duplicating rig A's own dashed line. The per-filter lines belong to the
        // rig series on such a target, and only to them.
        using var fixture = new Fixture(metrics: ["hfr"]);
        using var newest = Cards.Create(
            overview: Page.Session(Page.LastSession), detail: TwoRigsWithPerRigFilterMedians());
        using var older = Cards.Create(
            overview: Page.Session(Page.LastSession.AddDays(-1)), detail: TwoRigsWithPerRigFilterMedians());

        ObservableCollection<SessionCardViewModel> sessions = [newest.Card, older.Card];
        using var target = new TargetChartViewModel(sessions, fixture.Selection);
        target.ShowAllSessions = true;

        newest.Card.IsExpanded = true;
        newest.Settle();

        // The pill click, on the chart the reader is looking at.
        fixture.Selection.Filters.Single(pill => pill.Key == "Ha").IsSelected = true;

        Assert.True(fixture.Selection.HasRigPills);
        var names = target.Series.Select(series => series.Name!).ToList();

        // One solid, unsuffixed, unfiltered whole-night series: the one every night contributes to.
        Assert.Equal("HFR (px)", names[0]);
        Assert.False(IsDashed(target.Series[0]));

        // No unsuffixed filter line at all, and one per rig instead.
        Assert.DoesNotContain("HFR (px) (Ha)", names);
        Assert.Contains($"HFR (px) (Ha) [{RigA}]", names);
        Assert.Contains($"HFR (px) (Ha) [{RigB}]", names);

        // Whole night, then each rig's overall line and each rig's Ha line. Nothing is duplicated.
        Assert.Equal(5, target.Series.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ASingleRigTarget_WithAFilterPillOn_StillDrawsItsUnsuffixedFilterSeries()
    {
        // The other half: on a single-rig night the query's FilterMedians rows carry no rig label
        // and are the night's own, so the same whole-night spec draws both lines and the filter
        // split is exactly what it was before this phase.
        using var fixture = new Fixture(metrics: ["hfr"]);
        using var newest = Cards.Create(overview: Page.Session(Page.LastSession), detail: SingleRig());

        ObservableCollection<SessionCardViewModel> sessions = [newest.Card];
        using var target = new TargetChartViewModel(sessions, fixture.Selection);

        newest.Card.IsExpanded = true;
        newest.Settle();
        fixture.Selection.Filters.Single(pill => pill.Key == "Ha").IsSelected = true;

        Assert.False(fixture.Selection.HasRigPills);
        var names = target.Series.Select(series => series.Name!).ToList();

        Assert.Equal(["HFR (px)", "HFR (px) (Ha)"], names);
        Assert.All(target.Series, series => Assert.False(IsDashed(series)));
    }

    // A two-rig night in the shape SessionDetailQuery.SplitPerRig actually returns: one
    // FilterMedians row per (filter, rig), every one stamped with its rig's label, and no
    // night-level row for the filter at all.
    private static SessionDetail TwoRigsWithPerRigFilterMedians()
        => TwoRigs() with
        {
            FilterMedians =
            [
                new FilterMedians("Ha", 2.0d, 0.4d, 1.9d, 0.45d, 1400d, RigA),
                new FilterMedians("Ha", 3.0d, 0.5d, 2.4d, 0.55d, 1200d, RigB),
            ],
        };

    [Fact]
    public void ASingleRigTarget_DrawsExactlyWhatItDrewBefore()
    {
        // The other half of P2-1's ruling: the whole-night series is the only series a single-rig
        // target has, with no label suffix, no dash and a point for every night whether or not the
        // reader has opened it. Nothing about the ordinary target changed.
        using var fixture = new Fixture(metrics: ["hfr"]);
        using var oldest = Cards.Create(
            overview: Page.Session(Page.LastSession.AddDays(-1)), detail: SingleRig());
        using var newest = Cards.Create(
            overview: Page.Session(Page.LastSession), detail: SingleRig());

        ObservableCollection<SessionCardViewModel> sessions = [newest.Card, oldest.Card];
        using var target = new TargetChartViewModel(sessions, fixture.Selection);
        target.ShowAllSessions = true;

        newest.Card.IsExpanded = true;
        newest.Settle();

        Assert.False(fixture.Selection.HasRigPills);
        var whole = Assert.IsType<LineSeries<double?>>(Assert.Single(target.Series));
        Assert.Equal("HFR (px)", whole.Name);
        Assert.False(IsDashed(whole));
        Assert.Equal(2, whole.Values!.Count());
        Assert.All(whole.Values!, value => Assert.NotNull(value));
    }

    [Fact]
    public void TheChartRigPills_AndTheTableRigPills_DoNotDriveEachOther()
    {
        using var fixture = new Fixture(metrics: ["hfr"]);
        fixture.Selection.OfferRigs([RigA, RigB]);
        var chart = fixture.Build(TwoRigs());

        var display = new DisplaySettings();
        using var table = new FrameTableViewModel(
            [Frame(0, RigA, 2.0d), Frame(1, RigB, 3.0d)],
            display,
            new DisplayColumnWriter(() => display, _ => { }),
            new ShellIntegration(copyText: null, start: _ => null),
            openPreview: null,
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            getHeaders: _ => null);

        // Spec 13: one hides a line, the other hides a row, and a reader who wanted both unchecks
        // both.
        fixture.Selection.Rigs[1].IsSelected = false;

        Assert.Single(chart.Series);
        Assert.Equal(2, table.Rows.Count);
        Assert.All(table.RigPills, pill => Assert.True(pill.IsSelected));

        table.RigPills[0].IsSelected = false;

        Assert.Single(chart.Series);
        Assert.Single(table.Rows);
    }

    // ---- the per-frame chart offers the night on display, the trend chart the union ----------

    private const string RigC = "Charlie / Cam";

    private static SessionDetail NightOne()
        => Detail(
            [Group(0, RigA), Group(1, RigB)],
            [Frame(0, RigA, 2.0d), Frame(1, RigB, 3.0d)]);

    private static SessionDetail NightTwo()
        => Detail(
            [Group(0, RigB), Group(1, RigC)],
            [Frame(0, RigB, 3.0d), Frame(1, RigC, 4.0d)]);

    [Fact]
    public void PerFrameChart_AfterANightSwitch_OffersOnlyThatNightsRigs()
    {
        // Failure looks like a rig of the other night among the pills, because the shared list is
        // the union of every loaded night.
        using var fixture = new Fixture();
        fixture.Selection.OfferRigs([RigA, RigB, RigC]);

        using var first = new SessionChartViewModel(NightOne(), fixture.Selection);
        using var second = new SessionChartViewModel(NightTwo(), fixture.Selection);

        Assert.Equal([RigA, RigB], first.RigPills.Select(pill => pill.Toggle.Key));
        Assert.Equal([RigB, RigC], second.RigPills.Select(pill => pill.Toggle.Key));

        // One rig of the night is no row, however many the shared list holds.
        using var single = new SessionChartViewModel(SingleRig(), fixture.Selection);
        Assert.True(first.HasRigPills);
        Assert.False(single.HasRigPills);
    }

    [Fact]
    public void TrendChart_AfterANightSwitch_StillOffersTheUnionOfNights()
    {
        using var fixture = new Fixture();
        using var target = new TargetChartViewModel([], fixture.Selection);

        fixture.Selection.OfferRigs([RigA, RigB, RigC]);

        Assert.Equal([RigA, RigB, RigC], target.RigPills.Select(pill => pill.Toggle.Key));
    }

    [Fact]
    public void PerFrameChart_ARigToggledOffOnOneNight_IsStillOffWhenYouReturn()
    {
        // Failure looks like the narrowing dropping the stored choice, so the rig comes back on.
        using var fixture = new Fixture();
        fixture.Selection.OfferRigs([RigA, RigB, RigC]);

        using (var first = new SessionChartViewModel(NightOne(), fixture.Selection))
        {
            first.RigPills.Single(pill => pill.Toggle.Key == RigB).Toggle.IsSelected = false;
        }

        using (var second = new SessionChartViewModel(NightTwo(), fixture.Selection))
        {
            Assert.False(second.RigPills.Single(pill => pill.Toggle.Key == RigB).Toggle.IsSelected);
            Assert.True(second.RigPills.Single(pill => pill.Toggle.Key == RigC).Toggle.IsSelected);
        }

        using var back = new SessionChartViewModel(NightOne(), fixture.Selection);
        Assert.False(back.RigPills.Single(pill => pill.Toggle.Key == RigB).Toggle.IsSelected);
        Assert.True(back.RigPills.Single(pill => pill.Toggle.Key == RigA).Toggle.IsSelected);
    }
}
