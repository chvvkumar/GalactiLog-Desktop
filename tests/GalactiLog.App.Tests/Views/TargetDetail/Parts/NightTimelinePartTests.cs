using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Tests.ViewModels;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.App.Views.TargetDetail.Parts;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Settings;
using Xunit;
using static GalactiLog.App.Tests.Views.TargetDetail.Parts.NightPartsTestKit;
using Cards = GalactiLog.App.Tests.TestSupport.SessionCardViewModelTestFactory;
using Page = GalactiLog.App.Tests.TestSupport.TargetDetailViewModelTestFactory;

namespace GalactiLog.App.Tests.Views.TargetDetail.Parts;

// Content cases of NightTimelinePart: the night strip on the card and its two-way link to the frame table.
public class NightTimelinePartTests
{
    private static GeneralSettings AtASite() => new()
    {
        Timezone = "UTC",
        Use24HTime = true,
        ObserverLatitude = 34.05d,
        ObserverLongitude = -118.25d,
    };

    [Fact]
    public void NightStrip_IsBuiltOnLoad_AndClearedOnInvalidate()
    {
        var date = Page.LastSession;
        var frames = Frames(12, date);
        using var harness = Cards.Create(
            detail: Cards.PopulatedDetail(sessionDate: date) with { Frames = frames },
            general: AtASite());

        Assert.Null(harness.Card.NightStrip);
        Assert.False(harness.Card.HasNightStrip);

        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.NotNull(harness.Card.NightStrip);
        Assert.True(harness.Card.HasNightStrip);
        Assert.Equal(12, harness.Card.NightStrip!.Ticks.Count);

        // The observer's coordinates come from the general document the card already holds, so a
        // configured site gets the astronomical-night band with no extra wiring.
        Assert.True(harness.Card.NightStrip.HasBand);

        harness.Card.IsExpanded = false;
        harness.Card.Invalidate();

        Assert.Null(harness.Card.NightStrip);
        Assert.False(harness.Card.HasNightStrip);
    }

    [Fact]
    public void NightStrip_WithNoConfiguredSite_HasNoBand()
    {
        var date = Page.LastSession;
        using var harness = Cards.Create(
            detail: Cards.PopulatedDetail(sessionDate: date) with { Frames = Frames(4, date) });

        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.NotNull(harness.Card.NightStrip);
        Assert.False(harness.Card.NightStrip!.HasBand);
    }

    [Fact]
    public void NightStripFrameSelected_SelectsTheFrameRow_AndOpensThePreview()
    {
        // R8 and R9 are one action: a tick press does what a plain row click does, so the host
        // sends it through the table's one select-and-preview entry point.
        var date = Page.LastSession;
        var frames = Frames(10, date);
        using var harness = Cards.Create(
            detail: Cards.PopulatedDetail(sessionDate: date) with { Frames = frames });
        var opens = new List<(IReadOnlyList<FrameRowViewModel> Rows, int Index)>();
        var table = TableWithPreview(frames, opens);
        harness.FrameTableResult = table;

        harness.Card.IsExpanded = true;
        harness.Settle();

        harness.Card.NightStrip!.SelectFrame(3);

        var selected = Assert.Single(table.SelectedRows);
        Assert.Equal("frame_003.fits", selected.FileName);

        var opened = Assert.Single(opens);
        Assert.Same(selected, opened.Rows[opened.Index]);
    }

    [Fact]
    public void NightStripFrameHovered_HighlightsTheFrameRow_AndChangesNoSelection()
    {
        // R9: a hover tints a row and scrolls to it. It never changes what Copy paths would copy.
        var date = Page.LastSession;
        var frames = Frames(10, date);
        using var harness = Cards.Create(
            detail: Cards.PopulatedDetail(sessionDate: date) with { Frames = frames });
        var table = Table(frames);
        harness.FrameTableResult = table;

        harness.Card.IsExpanded = true;
        harness.Settle();

        harness.Card.NightStrip!.HoverFrame(3);

        Assert.Equal("frame_003.fits", table.HighlightedRow!.FileName);
        Assert.True(table.HighlightedRow.IsHighlighted);
        Assert.Empty(table.SelectedRows);
    }

    [Fact]
    public void NightStripFrameHovered_Null_ClearsTheHighlight()
    {
        var date = Page.LastSession;
        var frames = Frames(10, date);
        using var harness = Cards.Create(
            detail: Cards.PopulatedDetail(sessionDate: date) with { Frames = frames });
        var table = Table(frames);
        harness.FrameTableResult = table;

        harness.Card.IsExpanded = true;
        harness.Settle();

        harness.Card.NightStrip!.HoverFrame(3);
        var hovered = table.HighlightedRow!;

        harness.Card.NightStrip.HoverFrame(null);

        Assert.Null(table.HighlightedRow);
        Assert.False(hovered.IsHighlighted);
    }

    [Fact]
    public void ANewNight_DetachesTheOldStripsHoverHandler()
    {
        // DetachNightStrip's proof. Without the hover unsubscribe the first night's strip stays
        // wired to the card and a stale pointer would tint a row of the night after it.
        var date = Page.LastSession;
        var frames = Frames(10, date);
        using var harness = Cards.Create(
            detail: Cards.PopulatedDetail(sessionDate: date) with { Frames = frames });
        harness.FrameTableResult = Table(frames);

        harness.Card.IsExpanded = true;
        harness.Settle();

        var firstStrip = harness.Card.NightStrip!;

        harness.Card.IsExpanded = false;
        harness.Card.Invalidate();

        var second = Table(frames);
        harness.FrameTableResult = second;
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.NotSame(firstStrip, harness.Card.NightStrip);

        firstStrip.HoverFrame(3);
        firstStrip.SelectFrame(3);

        Assert.Null(second.HighlightedRow);
        Assert.Empty(second.SelectedRows);
    }

    [AvaloniaFact]
    public void ClearingTheFrameSelection_ClearsTheActiveTick()
    {
        var date = Page.LastSession;
        var frames = Frames(10, date);
        using var harness = Cards.Create(
            detail: Cards.PopulatedDetail(sessionDate: date) with { Frames = frames });
        var table = Table(frames);
        harness.FrameTableResult = table;

        harness.Card.IsExpanded = true;
        harness.Settle();

        table.SelectFrameAt(3);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, harness.Card.NightStrip!.ActiveFrame);

        table.SelectedRows.Clear();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(harness.Card.NightStrip!.ActiveFrame);
    }

    [AvaloniaFact]
    public void ANewNight_ClearsTheActiveTick()
    {
        var date = Page.LastSession;
        var frames = Frames(10, date);
        using var harness = Cards.Create(
            detail: Cards.PopulatedDetail(sessionDate: date) with { Frames = frames });
        var firstTable = Table(frames);
        harness.FrameTableResult = firstTable;

        harness.Card.IsExpanded = true;
        harness.Settle();

        firstTable.SelectFrameAt(3);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, harness.Card.NightStrip!.ActiveFrame);

        harness.Card.IsExpanded = false;
        harness.Card.Invalidate();

        var secondTable = Table(frames);
        harness.FrameTableResult = secondTable;
        harness.Card.IsExpanded = true;
        harness.Settle();

        Assert.Null(harness.Card.NightStrip!.ActiveFrame);
    }

    [AvaloniaFact]
    public void TheStripToTableDirection_StillWorks()
    {
        // The regression guard on P13's R9: this Task's table-to-strip wire must not displace the
        // existing strip-to-table one, and the two must coexist on one round trip.
        var date = Page.LastSession;
        var frames = Frames(10, date);
        using var harness = Cards.Create(
            detail: Cards.PopulatedDetail(sessionDate: date) with { Frames = frames });
        var table = Table(frames);
        harness.FrameTableResult = table;

        harness.Card.IsExpanded = true;
        harness.Settle();

        harness.Card.NightStrip!.SelectFrame(4);

        var selected = Assert.Single(table.SelectedRows);
        Assert.Equal("frame_004.fits", selected.FileName);

        // The strip named the frame, the table selected it, and the selection's own projection
        // settles the strip on the same tick rather than the round trip losing it.
        Assert.Equal(4, harness.Card.NightStrip.ActiveFrame);
    }
}
