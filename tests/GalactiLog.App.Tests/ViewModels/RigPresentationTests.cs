using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Xunit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Page = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.ViewModels;

/// <summary>
/// Spec 12.4's multi-rig presentation (PAR-004) and its per-night reference thumbnails (PAR-008) on
/// the view-model half: the card's rig list and facts line, the two tables' rig label rows, the
/// frame table's Rig column and pills, and the decode walk.
/// </summary>
/// <remarks>
/// The roadmap's Verify clause for this task is that a single-rig night renders exactly as it does
/// today, so every case below has a single-rig twin somewhere in this file or in the night part
/// tests.
/// </remarks>
public class RigPresentationTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private const string RigA = "Alpha / Cam";
    private const string RigB = "Bravo / Cam";

    // ---- fixtures ------------------------------------------------------------------------

    private static RigGroup Group(
        int index,
        string label,
        int frameCount = 4,
        Guid? referenceImageId = null,
        params string[] candidates)
        => new(
            index,
            label,
            label.Split(" / ")[0],
            "Cam",
            frameCount,
            frameCount * 300d,
            referenceImageId,
            candidates.Length > 0 ? candidates[0] : null,
            candidates);

    private static SessionDetail Detail(
        IReadOnlyList<RigGroup> rigs,
        IReadOnlyList<FrameRow>? frames = null,
        IReadOnlyList<FilterMedians>? medians = null,
        IReadOnlyList<FilterDetailRow>? details = null)
    {
        var baseline = Cards.PopulatedDetail();
        return baseline with
        {
            Rigs = rigs,
            Frames = frames ?? [],
            FilterMedians = medians ?? baseline.FilterMedians,
            FilterDetails = details ?? baseline.FilterDetails,
        };
    }

    private static IReadOnlyList<FilterMedians> SplitMedians()
        =>
        [
            new FilterMedians("Ha", 2.28d, 0.39d, 1.88d, 0.44d, 1490d, RigA),
            new FilterMedians("OIII", 2.35d, 0.42d, 1.94d, 0.47d, 1310d, RigA),
            new FilterMedians("Ha", 3.10d, 0.55d, 2.40d, 0.62d, 990d, RigB),
        ];

    private static IReadOnlyList<FilterDetailRow> SplitDetails()
        =>
        [
            new FilterDetailRow("Ha", 40, 12_000d, 2.28d, 0.39d, 300d, RigA),
            new FilterDetailRow("OIII", 34, 10_320d, 2.35d, 0.42d, 180d, RigA),
            new FilterDetailRow("Ha", 12, 3_600d, 3.10d, 0.55d, 300d, RigB),
        ];

    private static FrameTableViewModel Table(IReadOnlyList<FrameRow> frames)
    {
        var display = new DisplaySettings();
        return new FrameTableViewModel(
            frames,
            display,
            new DisplayColumnWriter(() => display, _ => { }),
            new ShellIntegration(copyText: null, start: _ => null),
            openPreview: null,
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            getHeaders: _ => null);
    }

    private static FrameRow Frame(string rig, bool outlier = false, string name = "f.fits")
        => FrameTableViewModelTests.Frame(
            fileName: name,
            medianHfr: 2.0d,
            isHfrOutlier: outlier,
            rig: rig);

    // ---- the card's rig list ---------------------------------------------------------------

    [Fact]
    public void ASingleRigNight_IsNotMultiRig()
    {
        using var harness = Cards.Create(detail: Detail([Group(0, RigA)]));
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.False(harness.Card.IsMultiRig);
        Assert.True(harness.Card.HasRigs);
    }

    [Fact]
    public void ASingleRigNight_HasOneThumbnailBox()
    {
        using var harness = Cards.Create(detail: Detail([Group(0, RigA)]));
        harness.Card.IsExpanded = true;
        harness.Settle();

        // The strip has one box on an ordinary night, which is what lets the markup carry no
        // branch: IsMultiRig governs the label rows, the Rig column and the pills, not the strip.
        var rig = Assert.Single(harness.Card.Rigs);
        Assert.Equal(RigA, rig.Label);
        Assert.Equal(0, rig.Index);
    }

    [Fact]
    public void ATwoRigNight_HasTwoBoxesInRigOrder()
    {
        using var harness = Cards.Create(detail: Detail([Group(0, RigB), Group(1, RigA)]));
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.True(harness.Card.IsMultiRig);
        Assert.Equal([RigB, RigA], harness.Card.Rigs.Select(rig => rig.Label));
        Assert.Equal([0, 1], harness.Card.Rigs.Select(rig => rig.Index));
    }

    [Fact]
    public void TheFactsLine_NamesTheRigsFirst()
    {
        using var harness = Cards.Create(detail: Detail([Group(0, RigB), Group(1, RigA)]));
        harness.Card.IsExpanded = true;
        harness.Settle();

        // Spec 12.4 item 1: the rigs, in rig order, before the figures the line already carries.
        Assert.StartsWith($"{RigB}, {RigA},", harness.Card.FactsLineText);
        Assert.Contains("gain 100", harness.Card.FactsLineText);
    }

    [Fact]
    public void ASingleRigNight_LeavesTheFactsLineAsItWas()
    {
        using var single = Cards.Create(detail: Detail([Group(0, RigA)]));
        single.Card.IsExpanded = true;
        single.Settle();

        // The roadmap's Verify clause, on this surface: no rig name is prepended at all.
        Assert.DoesNotContain(RigA, single.Card.FactsLineText);
        Assert.StartsWith("21:05 to 03:40", single.Card.FactsLineText);
    }

    // ---- the two tables' label rows (ruling C4) --------------------------------------------

    [Fact]
    public void TheFilterRows_CarryARigLabelRowPerRig()
    {
        using var harness = Cards.Create(detail: Detail(
            [Group(0, RigA, frameCount: 74), Group(1, RigB, frameCount: 12)],
            medians: SplitMedians(),
            details: SplitDetails()));
        harness.Card.IsExpanded = true;
        harness.Settle();

        var rows = harness.Card.FilterRows;
        Assert.True(rows[0].IsRigLabel);
        Assert.Equal(RigA, rows[0].RigLabel);
        Assert.Equal("74 frames", rows[0].RigFrameCountText);

        // Each rig's block opens with its label row and the filter rows follow under it.
        var labelIndices = rows
            .Select((row, index) => (row, index))
            .Where(entry => entry.row.IsRigLabel)
            .Select(entry => entry.index)
            .ToList();
        Assert.Equal(2, labelIndices.Count);
        Assert.Equal(0, labelIndices[0]);
        Assert.Equal(RigB, rows[labelIndices[1]].RigLabel);

        // Rig A's block holds its own two filters and rig B's holds its one.
        Assert.Equal(2, labelIndices[1] - labelIndices[0] - 1);
        Assert.Equal(1, rows.Count - labelIndices[1] - 1);
    }

    [Fact]
    public void TheRangeRows_CarryARigLabelRowPerRig()
    {
        var ranges = new[]
        {
            new MetricRangeSummary(1.9d, 3.1d, 2.3d),
            new MetricRangeSummary(0.3d, 0.5d, 0.4d),
            new MetricRangeSummary(1.6d, 2.4d, 1.9d),
            new MetricRangeSummary(0.3d, 0.7d, 0.45d),
            new MetricRangeSummary(-10.5d, -9.5d, -10d),
        };

        using var harness = Cards.Create(detail: Detail(
        [
            Group(0, RigA) with { Ranges = ranges },
            Group(1, RigB) with { Ranges = ranges },
        ]));
        harness.Card.IsExpanded = true;
        harness.Settle();

        // Spec 12.4 item 2 names both tables, so the ranges table splits on the same rule.
        Assert.Equal(2, harness.Card.Ranges.Count(range => range.IsRigLabel));
        Assert.True(harness.Card.Ranges[0].IsRigLabel);
        Assert.Equal(RigA, harness.Card.Ranges[0].RigLabel);
        Assert.Equal(5, harness.Card.Ranges.Count(range => !range.IsRigLabel) / 2);
    }

    [Fact]
    public void ARigWithNoMeasuredRange_ContributesNoLabelRowEither()
    {
        // Task 5 review P3. RangeCells drops a metric no frame of the rig measured, so a rig whose
        // five ranges are all unmeasured contributed nothing at all and left a bare label row with
        // an empty block under it. A heading over nothing states a rig's ranges and then shows
        // none, so the label goes with them.
        var measured = new[]
        {
            new MetricRangeSummary(1.9d, 3.1d, 2.3d),
            new MetricRangeSummary(0.3d, 0.5d, 0.4d),
            new MetricRangeSummary(1.6d, 2.4d, 1.9d),
            new MetricRangeSummary(0.3d, 0.7d, 0.45d),
            new MetricRangeSummary(-10.5d, -9.5d, -10d),
        };
        var unmeasured = new[]
        {
            new MetricRangeSummary(null, null, null),
            new MetricRangeSummary(null, null, null),
            new MetricRangeSummary(null, null, null),
            new MetricRangeSummary(null, null, null),
            new MetricRangeSummary(null, null, null),
        };

        using var harness = Cards.Create(detail: Detail(
        [
            Group(0, RigA) with { Ranges = measured },
            Group(1, RigB) with { Ranges = unmeasured },
        ]));
        harness.Card.IsExpanded = true;
        harness.Settle();

        var label = Assert.Single(harness.Card.Ranges, range => range.IsRigLabel);
        Assert.Equal(RigA, label.RigLabel);
        Assert.DoesNotContain(harness.Card.Ranges, range => range.RigLabel == RigB);
        Assert.Equal(5, harness.Card.Ranges.Count(range => !range.IsRigLabel));
    }

    [Fact]
    public void ARigLabelRow_IsOneSharedRowTypeInBothTables()
    {
        // Phase review P3-3: the filter table's row and the ranges table's row each carried their
        // own private constructor, factory and three properties, with the two runs copied in the
        // markup as well. One row model now, one shared DataTemplate over it, and one spelling of
        // the frame count.
        var ranges = new[]
        {
            new MetricRangeSummary(1.9d, 3.1d, 2.3d),
            new MetricRangeSummary(0.3d, 0.5d, 0.4d),
            new MetricRangeSummary(1.6d, 2.4d, 1.9d),
            new MetricRangeSummary(0.3d, 0.7d, 0.45d),
            new MetricRangeSummary(-10.5d, -9.5d, -10d),
        };

        using var harness = Cards.Create(detail: Detail(
        [
            Group(0, RigA, frameCount: 1) with { Ranges = ranges },
            Group(1, RigB, frameCount: 4) with { Ranges = ranges },
        ]));
        harness.Card.IsExpanded = true;
        harness.Settle();

        var filterLabel = harness.Card.FilterRows.First(row => row.IsRigLabel);
        var rangeLabel = harness.Card.Ranges.First(range => range.IsRigLabel);

        Assert.NotNull(filterLabel.LabelRow);
        Assert.Equal(filterLabel.LabelRow, rangeLabel.LabelRow);
        Assert.Equal(RigA, filterLabel.LabelRow!.Label);

        // The singular, from the one place it is spelled, and the same string the box's own
        // tooltip carries.
        Assert.Equal("1 frame", filterLabel.RigFrameCountText);
        Assert.Equal("1 frame", harness.Card.Rigs[0].FrameCountText);
        Assert.Equal("4 frames", harness.Card.Rigs[1].FrameCountText);
    }

    [Fact]
    public void ASingleRigNight_HasNoRigLabelRow()
    {
        using var harness = Cards.Create(detail: Detail([Group(0, RigA)]));
        harness.Card.IsExpanded = true;
        harness.Settle();

        // One table, exactly as today: no label row in either.
        Assert.DoesNotContain(harness.Card.FilterRows, row => row.IsRigLabel);
        Assert.DoesNotContain(harness.Card.Ranges, range => range.IsRigLabel);
        Assert.Equal(2, harness.Card.FilterRows.Count);
        Assert.Equal(5, harness.Card.Ranges.Count);
    }

    // ---- the frame table (spec 12.4 item 3) ------------------------------------------------

    [Fact]
    public void TheRigColumn_IsDrawnOnlyOnAMultiRigNight()
    {
        using var single = Table([Frame(RigA), Frame(RigA)]);
        using var multi = Table([Frame(RigB), Frame(RigA)]);

        Assert.False(single.IsMultiRig);
        Assert.Equal([RigA], single.Rigs);

        Assert.True(multi.IsMultiRig);

        // First capture order, which is the frames' own order, not alphabetical.
        Assert.Equal([RigB, RigA], multi.Rigs);
        Assert.Equal([0, 1], multi.Rows.Select(row => row.RigIndex));
        Assert.Equal([RigB, RigA], multi.Rows.Select(row => row.RigText));

        // Departure 1: the Rig column is not a display key, so the 32 columns are unchanged.
        Assert.Equal(32, multi.Columns.Count);
        Assert.DoesNotContain(multi.Columns, column => column.Key == "rig");
    }

    [Fact]
    public void TheRigPills_StartAllChecked()
    {
        using var multi = Table([Frame(RigB), Frame(RigA)]);
        using var single = Table([Frame(RigA)]);

        Assert.Equal(2, multi.RigPills.Count);
        Assert.All(multi.RigPills, pill => Assert.True(pill.IsSelected));
        Assert.Equal([RigB, RigA], multi.RigPills.Select(pill => pill.Key));

        // A single-rig night draws no pills at all.
        Assert.Empty(single.RigPills);
    }

    [Fact]
    public void AnUncheckedRig_HidesItsRows()
    {
        using var table = Table([Frame(RigB, name: "b.fits"), Frame(RigA, name: "a.fits")]);

        table.RigPills[0].IsSelected = false;

        var row = Assert.Single(table.Rows);
        Assert.Equal(RigA, row.RigText);
    }

    [Fact]
    public void TheRigFilterAndTheOutlierFilter_Compose()
    {
        using var table = Table(
        [
            Frame(RigA, outlier: true, name: "a-out.fits"),
            Frame(RigA, name: "a-ok.fits"),
            Frame(RigB, outlier: true, name: "b-out.fits"),
            Frame(RigB, name: "b-ok.fits"),
        ]);

        table.SetOutlierFilter(FrameOutlierFilter.Hfr);
        Assert.Equal(2, table.Rows.Count);

        // A row is shown when both admit it: the one flagged frame of the one checked rig.
        table.RigPills.Single(pill => pill.Key == RigB).IsSelected = false;
        var row = Assert.Single(table.Rows);
        Assert.Equal("a-out.fits", row.FileName);
    }

    [Fact]
    public void TheShownCount_ReportsTheComposedResult()
    {
        using var table = Table(
        [
            Frame(RigA, name: "a1.fits"),
            Frame(RigA, name: "a2.fits"),
            Frame(RigB, name: "b1.fits"),
        ]);

        Assert.Equal("3 frames", table.ShownCountText);

        table.RigPills.Single(pill => pill.Key == RigB).IsSelected = false;
        Assert.Equal("2 of 3 shown", table.ShownCountText);

        table.SetOutlierFilter(FrameOutlierFilter.Hfr);
        Assert.Equal("0 of 3 shown, HFR outliers", table.ShownCountText);
    }

    [Fact]
    public void UncheckingEveryRig_LeavesAnEmptyTable()
    {
        using var table = Table([Frame(RigA), Frame(RigB)]);

        foreach (var pill in table.RigPills)
        {
            pill.IsSelected = false;
        }

        // Questions.md Q11: the web's toggleRig refuses to remove the final rig and this does not.
        // An empty table with a readable count is a reversible state; a click that does nothing is
        // not.
        Assert.Empty(table.Rows);
        Assert.Equal("0 of 2 shown", table.ShownCountText);
    }

    [Fact]
    public void ASingleRigNight_FiltersExactlyAsBefore()
    {
        using var table = Table([Frame(RigA, outlier: true), Frame(RigA)]);

        Assert.Equal("2 frames", table.ShownCountText);
        table.SetOutlierFilter(FrameOutlierFilter.Hfr);
        Assert.Equal("1 of 2 shown, HFR outliers", table.ShownCountText);
    }

    // ---- the decode walk (PAR-008) ---------------------------------------------------------

    [AvaloniaFact]
    public void TheDecodeWalk_TriesTheSecondCandidateWhenTheFirstFails()
    {
        using var walk = new Walk(failing: ["one.fits"]);
        var rig = walk.Run(Group(0, RigA, referenceImageId: Guid.NewGuid(),
            candidates: ["one.fits", "two.fits", "three.fits"]));

        Assert.Equal(["one.fits", "two.fits"], walk.Requested);
        Assert.True(rig.HasThumbnail);
        Assert.False(rig.ShowsPlaceholder);
    }

    [AvaloniaFact]
    public void TheDecodeWalk_StopsAtThree_AndShowsThePlaceholder()
    {
        using var walk = new Walk(failing: ["one.fits", "two.fits", "three.fits", "four.fits"]);
        var rig = walk.Run(Group(0, RigA, referenceImageId: Guid.NewGuid(),
            candidates: ["one.fits", "two.fits", "three.fits"]));

        // At most three are tried, whatever the list's length, and after three failures the box
        // renders the placeholder with no retry.
        Assert.Equal(["one.fits", "two.fits", "three.fits"], walk.Requested);
        Assert.Equal(RigViewModel.MaxDecodeAttempts, rig.Attempts);
        Assert.False(rig.HasThumbnail);
        Assert.True(rig.ShowsPlaceholder);
    }

    [AvaloniaFact]
    public void TheDecodeWalk_StopsAtTheEndOfAShorterList()
    {
        using var walk = new Walk(failing: ["one.fits"]);
        var rig = walk.Run(Group(0, RigA, referenceImageId: Guid.NewGuid(), candidates: ["one.fits"]));

        Assert.Equal(["one.fits"], walk.Requested);
        Assert.True(rig.ShowsPlaceholder);
    }

    [AvaloniaFact]
    public void TheDecodeWalk_MarksNoFrame_AndWritesNothing()
    {
        var frames = new[] { Frame(RigA, name: "one.fits"), Frame(RigA, name: "two.fits") };
        var before = frames.Select(frame => frame with { }).ToList();

        using var walk = new Walk(failing: ["one.fits", "two.fits"]);
        var rig = walk.Run(Group(0, RigA, referenceImageId: frames[0].ImageId,
            candidates: ["one.fits", "two.fits"]));

        // A frame skipped by the walk is not marked, flagged or written to: the walk is a read
        // that did not work, not a verdict about the file. The read models are untouched, the
        // render probe saw only reads, and no write delegate exists on this path to call.
        Assert.True(rig.ShowsPlaceholder);
        Assert.Equal(before, frames);
        Assert.Equal(["one.fits", "two.fits"], walk.Requested);
        Assert.Empty(walk.Writes);
    }

    [Fact]
    public void ACardWithNoThumbnailFactory_LeavesEveryBoxEmpty()
    {
        using var harness = Cards.Create(detail: Detail(
            [Group(0, RigA, referenceImageId: Guid.NewGuid(), candidates: ["one.fits"])]));
        harness.Card.IsExpanded = true;
        harness.Settle();

        // Which is what a test that is not about thumbnails wants, and what keeps every existing
        // card case free of a render queue.
        var rig = Assert.Single(harness.Card.Rigs);
        Assert.Null(rig.Thumbnail);
        Assert.True(rig.ShowsPlaceholder);
        Assert.Empty(harness.ThumbnailRequests);
    }

    // ---- the walk is on demand (the user's B1 ruling, and the coordinator's decode ruling) ----

    [AvaloniaFact]
    public void TheWalkRunsWhenTheRigsPublish()
    {
        // The walk starts on the rigs' publication; nothing else starts it.
        using var section = new LazySection();

        var rig = Assert.Single(section.Harness.Card.Rigs);
        Assert.True(rig.HasStarted);
        Assert.Equal(["one.fits"], section.Harness.ThumbnailRequests);
    }

    // A card over one rig with one candidate and a real slot factory. The slot's decode returns
    // nothing, so the walk settles on the placeholder and no bitmap is held; what is measured is
    // whether the factory was called at all.
    private sealed class LazySection : IDisposable
    {
        private readonly ThumbnailWorker _worker;

        public LazySection()
        {
            _worker = new ThumbnailWorker(
                (path, _) => "frames/" + path + ".jpg",
                (path, _) => "previews/" + path + ".jpg",
                post: action => action());

            var holder = new TargetPageState();

            Harness = Cards.Create(
                detail: Detail([Group(0, RigA, referenceImageId: Guid.NewGuid(), candidates: ["one.fits"])]),
                targetPage: holder,
                createFrameThumbnail: path => new ThumbnailSlotViewModel(
                    path,
                    _worker,
                    _ => [0x01],
                    ThumbnailKind.Frame,
                    decode: _ => null,
                    post: action => action()));

            Harness.Card.IsExpanded = true;
            Harness.Settle();
        }

        public Cards.Harness Harness { get; }

        public void Dispose()
        {
            Harness.Dispose();
            _worker.Dispose();
        }
    }

    [Fact]
    public void AClickOnAThumbnail_OpensThePreviewAtThatFrame()
    {
        List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)> opened = [];
        var frames = new[]
        {
            Frame(RigA, name: "first.fits"),
            Frame(RigA, name: "second.fits"),
            Frame(RigA, name: "third.fits"),
        };

        var display = new DisplaySettings();
        // Not a using: the card owns the table it was handed and disposes it with its children.
        var table = new FrameTableViewModel(
            frames,
            display,
            new DisplayColumnWriter(() => display, _ => { }),
            new ShellIntegration(copyText: null, start: _ => null),
            openPreview: (rows, index) => opened.Add((rows, index)),
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            getHeaders: _ => null);

        using var harness = Cards.Create(detail: Detail(
            [Group(0, RigA, referenceImageId: frames[1].ImageId, candidates: ["second.fits"])],
            frames: frames));
        harness.FrameTableResult = table;
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Single(harness.Card.Rigs).OpenPreviewCommand.Execute(null);

        // Spec 12.4: the preview opens at that frame, inside the night's frame list, so the arrow
        // keys step through the night from there. It goes through the frame table's own seam, not
        // a second preview entry point, which is why the list it opened on is the table's rows.
        var (rows, index) = Assert.Single(opened);
        Assert.Equal("second.fits", rows[index].FileName);
        Assert.Equal(3, rows.Count);
        Assert.Same(table.Rows[1], Assert.Single(table.SelectedRows));
    }

    [Fact]
    public void AClickOnAThumbnail_OpensEvenWhenTheFilterHidesThatFrame()
    {
        // Review P2-3. The reference frame is by construction the sharpest frame of its set, so
        // with either outlier filter on it is never among the shown rows. Spec 12.4 states the
        // click with no condition, so it opens anyway, over the night's own capture order.
        List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)> opened = [];
        var frames = new[]
        {
            Frame(RigA, outlier: true, name: "flagged.fits"),
            Frame(RigA, name: "sharpest.fits"),
        };

        var display = new DisplaySettings();
        // Not a using: the card owns the table it was handed and disposes it with its children.
        var table = new FrameTableViewModel(
            frames,
            display,
            new DisplayColumnWriter(() => display, _ => { }),
            new ShellIntegration(copyText: null, start: _ => null),
            openPreview: (rows, index) => opened.Add((rows, index)),
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            getHeaders: _ => null);

        using var harness = Cards.Create(detail: Detail(
            [Group(0, RigA, referenceImageId: frames[1].ImageId, candidates: ["sharpest.fits"])],
            frames: frames));
        harness.FrameTableResult = table;
        harness.Card.IsExpanded = true;
        harness.Settle();

        table.SetOutlierFilter(FrameOutlierFilter.Hfr);
        Assert.Equal("flagged.fits", Assert.Single(table.Rows).FileName);

        Assert.Single(harness.Card.Rigs).OpenPreviewCommand.Execute(null);

        var (rows, index) = Assert.Single(opened);
        Assert.Equal("sharpest.fits", rows[index].FileName);

        // The list is the night's capture order, not the filtered rows, so the arrow keys step
        // through the whole night from there.
        Assert.Equal(2, rows.Count);

        // And the user's filter is exactly where they left it, with no row selected behind it.
        Assert.Equal(FrameOutlierFilter.Hfr, table.OutlierFilter);
        Assert.Equal("flagged.fits", Assert.Single(table.Rows).FileName);
        Assert.Empty(table.SelectedRows);
    }

    [Fact]
    public void AClickOnAThumbnail_OpensWhenThatRigsPillIsUnchecked()
    {
        // The same rule on the other filter: a rig the reader has unchecked still opens its own
        // box, because the box is the rig's and the click is about that frame.
        List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)> opened = [];
        var frames = new[]
        {
            Frame(RigA, name: "alpha.fits"),
            Frame(RigB, name: "bravo.fits"),
        };

        var display = new DisplaySettings();
        var table = new FrameTableViewModel(
            frames,
            display,
            new DisplayColumnWriter(() => display, _ => { }),
            new ShellIntegration(copyText: null, start: _ => null),
            openPreview: (rows, index) => opened.Add((rows, index)),
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            getHeaders: _ => null);

        using var harness = Cards.Create(detail: Detail(
            [
                Group(0, RigA, referenceImageId: frames[0].ImageId, candidates: ["alpha.fits"]),
                Group(1, RigB, referenceImageId: frames[1].ImageId, candidates: ["bravo.fits"]),
            ],
            frames: frames));
        harness.FrameTableResult = table;
        harness.Card.IsExpanded = true;
        harness.Settle();

        table.RigPills.Single(pill => pill.Key == RigB).IsSelected = false;
        Assert.Equal("alpha.fits", Assert.Single(table.Rows).FileName);

        harness.Card.Rigs[1].OpenPreviewCommand.Execute(null);

        var (rows, index) = Assert.Single(opened);
        Assert.Equal("bravo.fits", rows[index].FileName);
        Assert.Equal("alpha.fits", Assert.Single(table.Rows).FileName);
    }

    [Fact]
    public void AThumbnailWithNoReference_OpensNothing()
    {
        List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)> opened = [];
        var frames = new[] { Frame(RigA, name: "only.fits") };

        var display = new DisplaySettings();
        // Not a using: the card owns the table it was handed and disposes it with its children.
        var table = new FrameTableViewModel(
            frames,
            display,
            new DisplayColumnWriter(() => display, _ => { }),
            new ShellIntegration(copyText: null, start: _ => null),
            openPreview: (rows, index) => opened.Add((rows, index)),
            new GeneralSettings { Timezone = "UTC", Use24HTime = true },
            getHeaders: _ => null);

        using var harness = Cards.Create(detail: Detail([Group(0, RigA)], frames: frames));
        harness.FrameTableResult = table;
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Single(harness.Card.Rigs).OpenPreviewCommand.Execute(null);

        Assert.Empty(opened);
    }

    // ---- the walk's harness ----------------------------------------------------------------

    /// <summary>
    /// Drives <see cref="RigViewModel"/>'s decode walk with a real
    /// <see cref="ThumbnailSlotViewModel"/> over a synchronous worker, so the walk's own chaining is
    /// what is under test rather than a stub of it. A frame in <c>failing</c> decodes to null,
    /// which is exactly what an unreadable frame does in production.
    /// </summary>
    private sealed class Walk : IDisposable
    {
        private readonly HashSet<string> _failing;
        private readonly ThumbnailWorker _worker;
        private readonly ConcurrentQueue<string> _requested = new();
        private RigViewModel? _rig;

        public Walk(IReadOnlyList<string> failing)
        {
            _failing = [.. failing];
            _worker = new ThumbnailWorker(
                (path, _) =>
                {
                    _requested.Enqueue(path);
                    return "frames/" + path + ".jpg";
                },
                (path, _) => "previews/" + path + ".jpg",
                post: action => action());
        }

        /// <summary>The frame paths the walk asked the worker to render, in order.</summary>
        public IReadOnlyList<string> Requested => [.. _requested];

        /// <summary>Any write the walk attempted. It never attempts one, which is the point.
        /// </summary>
        public List<string> Writes { get; } = [];

        public RigViewModel Run(RigGroup group)
        {
            var cap = Math.Min(
                RigViewModel.MaxDecodeAttempts,
                group.ReferenceCandidates?.Count ?? 0);

            _rig = new RigViewModel(group, CreateSlot);

            // The walk is on demand since the user's B1 ruling put the strip inside the closed
            // "Night detail" section: constructing the rig requests nothing, and this is the call
            // the card makes when that section opens.
            _rig.StartThumbnail();

            // Settled means one of two things: a candidate decoded, or the last candidate this
            // walk will try has been rendered and has come back with nothing. The render count is
            // what makes the second unambiguous: a slot raises IsLoading before its render is
            // requested, so a false positive between two candidates is not reachable.
            SpinWait.SpinUntil(
                () => _rig.HasThumbnail
                    || (_requested.Count >= cap && _rig.Thumbnail?.IsLoading == false),
                Budget);

            return _rig;
        }

        public void Dispose()
        {
            _rig?.Dispose();
            _worker.Dispose();
        }

        private ThumbnailSlotViewModel CreateSlot(string framePath)
            => new(
                framePath,
                _worker,
                _ => [0x01],
                ThumbnailKind.Frame,
                decode: _ => _failing.Contains(framePath) ? null : new WritableBitmap(),
                post: action => action());
    }

    // A 3:2 bitmap with a known aspect, so a box measurement is not a decode of a real JPEG.
    // Avalonia's Bitmap is not sealed, which is how ThumbnailSlotViewModelTests does the same.
    internal sealed class WritableBitmap : Bitmap
    {
        public WritableBitmap()
            : base(new MemoryStream([0x01, 0x02, 0x03, 0x04]))
        {
        }
    }
}
