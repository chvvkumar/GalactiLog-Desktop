using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 12 Task 3. The night strip's whole projection: one tick per dated exposure at its
// fraction of the dusk-to-dawn axis, the astronomical-night band, the axis hours, the first and
// last labels, the hit test and the frame-selected event. No window is needed for any of it
// except the brush-type case, which needs an Application for the headless brush machinery.
public class NightStripViewModelTests
{
    // A fixed offset rather than a named zone: a named zone carries the running machine's DST
    // rules into the expected wall-clock figures, and the rule under test is "the supplied zone
    // is the one used", not "one particular Windows zone is correct".
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "GalactiLog NightStrip Test (UTC+02)",
        TimeSpan.FromHours(2),
        "GalactiLog NightStrip Test",
        "GalactiLog NightStrip Test");

    private static readonly ImmutableSolidColorBrush Luminance = new(Colors.Gainsboro);
    private static readonly ImmutableSolidColorBrush Red = new(Colors.IndianRed);
    private static readonly ImmutableSolidColorBrush Fallback = new(Colors.DimGray);

    private static readonly Dictionary<string, ImmutableSolidColorBrush> Inks = new()
    {
        ["L"] = Luminance,
        ["R"] = Red,
    };

    /// <summary>A local wall-clock instant on the night of 2025-03-10.</summary>
    private static DateTime Local(int day, int hour, int minute)
        => new(2025, 3, day, hour, minute, 0, DateTimeKind.Unspecified);

    /// <summary>The stored UTC instant for a local wall-clock time in <see cref="Zone"/>.</summary>
    private static DateTime Utc(DateTime local)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);

    // Built on the frame-table suite's factory rather than on FrameRow's positional constructor:
    // the read model carries 30 optional metrics plus the two outlier flags, and a `with` keeps
    // this file compiling whatever position those flags were appended in.
    private static FrameRow Frame(
        DateTime? localCapture,
        string? filter = "L",
        double? hfr = null,
        bool hfrOutlier = false,
        bool eccentricityOutlier = false)
        => FrameTableViewModelTests.Frame(
            captureDate: localCapture is { } local ? Utc(local) : null,
            filterUsed: filter,
            medianHfr: hfr) with
        {
            IsHfrOutlier = hfrOutlier,
            IsEccentricityOutlier = eccentricityOutlier,
        };

    private static NightStripViewModel Build(
        IReadOnlyList<FrameRow> frames,
        (DateTime Dusk, DateTime Dawn)? bounds = null,
        bool use24Hour = true)
        => new(frames, Inks, Fallback, bounds, Zone, use24Hour);

    /// <summary>Five dated frames from 19:05 to 03:31 plus one undated one at index 3.</summary>
    private static IReadOnlyList<FrameRow> ANight() =>
    [
        Frame(Local(10, 19, 5), "L", 2.41),
        Frame(Local(10, 21, 0), "L", 2.55),
        Frame(Local(10, 23, 30), "R", 2.60, hfrOutlier: true),
        Frame(null, "R", 2.62),
        Frame(Local(11, 1, 15), "R", 2.58, eccentricityOutlier: true),
        Frame(Local(11, 3, 31), "Ha", 2.91),
    ];

    [Fact]
    public void Ticks_CountEqualsTheDatedFrameCount()
    {
        var frames = ANight();

        var model = Build(frames);

        // Not frames.Count: a frame with no capture_date has no place on a time axis and is
        // dropped. The roadmap's "tick count equals frame count" reads against the dated count.
        Assert.Equal(frames.Count(frame => frame.CaptureDate is not null), model.Ticks.Count);
        Assert.Equal(5, model.Ticks.Count);
    }

    [Fact]
    public void Ticks_AFrameWithNoCaptureDate_ProducesNoTick()
    {
        var model = Build(ANight());

        Assert.DoesNotContain(3, model.Ticks.Select(tick => tick.FrameIndex));
        Assert.Equal([0, 1, 2, 4, 5], model.Ticks.Select(tick => tick.FrameIndex).Order().ToArray());
    }

    [Fact]
    public void OutlierCount_EqualsTheFlaggedFrameCount()
    {
        var frames = ANight();

        var model = Build(frames);

        Assert.Equal(
            frames.Count(frame =>
                frame.CaptureDate is not null && (frame.IsHfrOutlier || frame.IsEccentricityOutlier)),
            model.OutlierCount);
        Assert.Equal(2, model.OutlierCount);
    }

    [Fact]
    public void OutlierTicks_AreTheFramesEitherFlagMarked()
    {
        var model = Build(ANight());

        Assert.Equal(
            [2, 4],
            model.Ticks.Where(tick => tick.IsOutlier).Select(tick => tick.FrameIndex).Order().ToArray());
        Assert.All(
            model.Ticks.Where(tick => tick.FrameIndex is 0 or 1 or 5),
            tick => Assert.False(tick.IsOutlier));
    }

    [Fact]
    public void HasBand_IsFalse_WhenNightBoundsAreNull()
    {
        var model = Build(ANight(), bounds: null);

        Assert.False(model.HasBand);
        Assert.Equal("", model.BandLabel);
        Assert.Equal(0d, model.BandStartFraction);
        Assert.Equal(0d, model.BandEndFraction);
    }

    [Fact]
    public void BandFractions_BracketTheTicksOfADuskToDawnNight()
    {
        // Ticks 21:00 to 03:00, band 20:40 to 04:10, so the whole run sits inside the band and
        // the domain is the band widened to the enclosing hours, 20:00 and 05:00.
        IReadOnlyList<FrameRow> frames =
        [
            Frame(Local(10, 21, 0)),
            Frame(Local(11, 0, 0)),
            Frame(Local(11, 3, 0)),
        ];

        var model = Build(frames, (Local(10, 20, 40), Local(11, 4, 10)));

        Assert.True(model.HasBand);
        Assert.Equal("astronomical night", model.BandLabel);
        Assert.Equal(Local(10, 20, 0), model.StartLocal);
        Assert.Equal(Local(11, 5, 0), model.EndLocal);

        // 540 minutes of domain; the band opens at minute 40 and closes at minute 490.
        Assert.Equal(40d / 540d, model.BandStartFraction, 6);
        Assert.Equal(490d / 540d, model.BandEndFraction, 6);

        Assert.All(
            model.Ticks,
            tick => Assert.InRange(tick.Fraction, model.BandStartFraction, model.BandEndFraction));
    }

    [Fact]
    public void Fraction_IsZeroAtStart_AndOneAtEnd()
    {
        // On the hour at both ends, so the floor and the ceiling are the tick times themselves.
        IReadOnlyList<FrameRow> frames =
        [
            Frame(Local(10, 19, 0)),
            Frame(Local(10, 22, 0)),
            Frame(Local(11, 3, 0)),
        ];

        var model = Build(frames);

        Assert.Equal(Local(10, 19, 0), model.StartLocal);
        Assert.Equal(Local(11, 3, 0), model.EndLocal);
        Assert.Equal(0d, model.Ticks[0].Fraction, 9);
        Assert.Equal(180d / 480d, model.Ticks[1].Fraction, 9);
        Assert.Equal(1d, model.Ticks[2].Fraction, 9);
        Assert.All(model.Ticks, tick => Assert.InRange(tick.Fraction, 0d, 1d));
    }

    [Fact]
    public void HitTest_ReturnsTheNearestTickWithinTheHitWidth()
    {
        IReadOnlyList<FrameRow> frames =
        [
            Frame(Local(10, 19, 0)),
            Frame(Local(10, 23, 0)),
            Frame(Local(11, 3, 0)),
        ];

        var model = Build(frames);
        var middle = model.Ticks[1];

        var hit = model.HitTest(middle.Fraction + (NightStripViewModel.HitFraction * 0.5d));

        Assert.NotNull(hit);
        Assert.Equal(middle.FrameIndex, hit!.FrameIndex);

        // And exactly on a tick, which is what a click on the drawn line is.
        Assert.Equal(0, model.HitTest(model.Ticks[0].Fraction)!.FrameIndex);
        Assert.Equal(2, model.HitTest(model.Ticks[2].Fraction)!.FrameIndex);
    }

    [Fact]
    public void HitTest_ReturnsNull_BetweenTwoDistantTicks()
    {
        IReadOnlyList<FrameRow> frames =
        [
            Frame(Local(10, 19, 0)),
            Frame(Local(11, 3, 0)),
        ];

        var model = Build(frames);

        Assert.Null(model.HitTest(0.5d));
        Assert.Null(model.HitTest(NightStripViewModel.HitFraction * 2d));
    }

    [Fact]
    public void SelectFrame_RaisesFrameSelected_WithTheFrameIndex()
    {
        var model = Build(ANight());
        var raised = new List<int>();
        model.FrameSelected += (_, index) => raised.Add(index);

        model.SelectFrame(4);
        model.SelectFrame(0);

        Assert.Equal([4, 0], raised);
    }

    [Fact]
    public void Labels_UseTheSuppliedZoneAndTheTwentyFourHourFlag()
    {
        var frames = ANight();

        var twentyFour = Build(frames, (Local(10, 20, 40), Local(11, 4, 10)));

        Assert.Equal("first 19:05", twentyFour.FirstLabel);
        Assert.Equal("last 03:31", twentyFour.LastLabel);
        Assert.Contains("22:00", twentyFour.AxisTicks.Select(tick => tick.Label));

        var twelve = Build(frames, (Local(10, 20, 40), Local(11, 4, 10)), use24Hour: false);

        Assert.Equal("first 7:05 PM", twelve.FirstLabel);
        Assert.Equal("last 3:31 AM", twelve.LastLabel);
        Assert.Contains("10:00 PM", twelve.AxisTicks.Select(tick => tick.Label));
    }

    [Fact]
    public void AxisTicks_AreEveryTwoHoursStrictlyInsideTheDomain()
    {
        IReadOnlyList<FrameRow> frames =
        [
            Frame(Local(10, 19, 0)),
            Frame(Local(11, 3, 0)),
        ];

        var model = Build(frames);

        Assert.Equal(
            ["20:00", "22:00", "00:00", "02:00"],
            model.AxisTicks.Select(tick => tick.Label).ToArray());
        Assert.All(model.AxisTicks, tick => Assert.InRange(tick.Fraction, 0d, 1d));
    }

    [Fact]
    public void AnEmptySession_ConstructsWithNoTicksAndNoBand()
    {
        var model = Build([]);

        Assert.Empty(model.Ticks);
        Assert.Empty(model.AxisTicks);
        Assert.False(model.HasBand);
        Assert.Equal(model.StartLocal, model.EndLocal);
        Assert.Equal("", model.FirstLabel);
        Assert.Equal("", model.LastLabel);
        Assert.Equal("", model.BandLabel);
        Assert.Equal(0, model.OutlierCount);
        Assert.Null(model.HitTest(0.5d));
    }

    [AvaloniaFact]
    public async Task EveryTickBrush_IsAnImmutableSolidColorBrush()
    {
        // Spec 14.5: a SolidColorBrush constructed once a dispatcher exists calls VerifyAccess, so
        // a strip built where the session detail arrives would throw. Built on the pool and
        // awaited, never blocked on (roadmap Global Constraints, the Wait() rule).
        var frames = ANight();

        var model = await Task.Run(() => Build(frames, (Local(10, 20, 40), Local(11, 4, 10))));

        Assert.NotEmpty(model.Ticks);
        Assert.All(model.Ticks, tick => Assert.IsType<ImmutableSolidColorBrush>(tick.Brush));
    }

    [Fact]
    public void AnUnmappedFilter_TakesTheFallbackBrush()
    {
        IReadOnlyList<FrameRow> frames =
        [
            Frame(Local(10, 20, 0), "L"),
            Frame(Local(10, 21, 0), "Ha"),
            Frame(Local(10, 22, 0), null),
            Frame(Local(10, 23, 0), "   "),
        ];

        var model = Build(frames);

        Assert.Same(Luminance, model.Ticks[0].Brush);
        Assert.Same(Fallback, model.Ticks[1].Brush);
        Assert.Same(Fallback, model.Ticks[2].Brush);
        Assert.Same(Fallback, model.Ticks[3].Brush);
    }

    [Fact]
    public void ToolTipText_NamesTheFilterTheTimeAndTheHfr()
    {
        var model = Build(ANight());

        Assert.Equal("L 19:05, HFR 2.41", model.Ticks[0].ToolTipText);
        Assert.Equal("Ha 03:31, HFR 2.91", model.Ticks[4].ToolTipText);
    }

    [Fact]
    public void Labels_RenderThroughTheOneClockFormatter()
    {
        // Coordinator item 1. The two clock format strings were a second copy on this type; they
        // are SessionTimeFormat's now, so an axis mark and a frame table Time cell cannot disagree
        // on shape. Asserting the labels through the formatter is what keeps the two one.
        var frames = ANight();

        var twentyFour = Build(frames, (Local(10, 20, 40), Local(11, 4, 10)));
        Assert.Contains(
            SessionTimeFormat.FormatLocal(Local(10, 22, 0), true),
            twentyFour.AxisTicks.Select(tick => tick.Label));

        var twelve = Build(frames, (Local(10, 20, 40), Local(11, 4, 10)), use24Hour: false);
        Assert.Contains(
            SessionTimeFormat.FormatLocal(Local(10, 22, 0), false),
            twelve.AxisTicks.Select(tick => tick.Label));

        // And the formatter itself renders the two shapes, so the assertions above are not two
        // empty strings agreeing.
        Assert.Equal("22:00", SessionTimeFormat.FormatLocal(Local(10, 22, 0), true));
        Assert.Equal("10:00 PM", SessionTimeFormat.FormatLocal(Local(10, 22, 0), false));
    }

    // ---- Phase 13 Task 6: R9's hover, raised beside the press ---------------------------------

    [Fact]
    public void HoverFrame_RaisesFrameHovered_WithTheFrameIndex()
    {
        var model = Build(ANight());
        var raised = new List<int?>();
        model.FrameHovered += (_, index) => raised.Add(index);

        model.HoverFrame(2);
        model.HoverFrame(0);

        Assert.Equal([2, 0], raised);
    }

    [Fact]
    public void HoverFrame_Null_RaisesFrameHoveredWithNull()
    {
        // R9: the pointer between ticks and the pointer off the strip are the same message, and
        // the host reads it as "clear the tint".
        var model = Build(ANight());
        var raised = new List<int?>();
        model.FrameHovered += (_, index) => raised.Add(index);

        model.HoverFrame(null);

        Assert.Equal([(int?)null], raised);
    }

    // ---- Task 7: the active-frame index (user ruling U1) ---------------------------------------

    // ---- Phase 25 R5: the stitched strip. ---------------------------------------------------
    //
    // Night one, 2025-03-10: three frames 21:00 to 01:00 and a band 20:40 to 04:10, so its domain
    // is 20:00 to 05:00, nine hours. Night two, 2025-03-13: two frames 22:00 and 00:00 and a band
    // 21:50 to 03:05, domain 21:00 to 04:00, seven hours. Shares 9/16 and 7/16.

    private static NightStripViewModel NightOne() => Build(
        [
            Frame(Local(10, 21, 0), "L", 2.41),
            Frame(Local(10, 23, 0), "R", 2.60, hfrOutlier: true),
            Frame(Local(11, 1, 0), "L", 2.58),
        ],
        (Local(10, 20, 40), Local(11, 4, 10)));

    private static NightStripViewModel NightTwo() => Build(
        [
            Frame(Local(13, 22, 0), "L", 2.44),
            Frame(Local(14, 0, 0), "L", 2.70, eccentricityOutlier: true),
        ],
        (Local(13, 21, 50), Local(14, 3, 5)));

    private static NightStripViewModel TwoNights() => NightStripViewModel.Stitched(
    [
        (new DateOnly(2025, 3, 10), NightOne(), 0),
        (new DateOnly(2025, 3, 13), NightTwo(), 3),
    ]);

    private const double ShareOne = 9d / 16d;

    // A failure is a second-night tick naming a first-night row, or sitting in the first share.
    [Fact]
    public void Stitched_Ticks_CarryTheSecondNightsIndexOffset_AndSitInsideTheirSegment()
    {
        var model = TwoNights();

        Assert.Equal([0, 1, 2, 3, 4], model.Ticks.Select(tick => tick.FrameIndex).ToArray());
        Assert.All(model.Ticks.Take(3), tick => Assert.True(tick.Fraction <= ShareOne));
        Assert.All(model.Ticks.Skip(3), tick => Assert.True(tick.Fraction >= ShareOne));

        // 21:00 is one hour into a nine-hour night; 22:00 is one hour into the seven-hour one.
        Assert.Equal(1d / 16d, model.Ticks[0].Fraction, 9);
        Assert.Equal(ShareOne + (1d / 16d), model.Ticks[3].Fraction, 9);
        Assert.Equal(Local(10, 20, 0), model.StartLocal);
        Assert.Equal(Local(14, 4, 0), model.EndLocal);
    }

    [Fact]
    public void Stitched_HasTwoBands_AndTwoBoundaries()
    {
        var model = TwoNights();

        Assert.Equal(2, model.Bands.Count);
        Assert.Equal(40d / 960d, model.Bands[0].Start, 9);
        Assert.Equal(490d / 960d, model.Bands[0].End, 9);
        Assert.Equal(590d / 960d, model.Bands[1].Start, 9);
        Assert.Equal(905d / 960d, model.Bands[1].End, 9);
        Assert.True(model.HasBand);
        Assert.Equal(model.Bands[0].Start, model.BandStartFraction);
        Assert.Equal(model.Bands[0].End, model.BandEndFraction);

        Assert.Equal(
            [(0d, "03-10"), (ShareOne, "03-13")],
            model.Boundaries.Select(boundary => (Math.Round(boundary.Fraction, 9), boundary.Label)).ToArray());
        Assert.Same(model.LaneAxis!.Boundaries, model.Boundaries);
    }

    [Fact]
    public void Stitched_SumsTheOutliers_ConcatenatesTheMarks_AndDatesTheEdgeLabels()
    {
        var model = TwoNights();

        Assert.Equal(2, model.OutlierCount);
        Assert.Equal("first 03-10 21:00", model.FirstLabel);
        Assert.Equal("last 03-13 00:00", model.LastLabel);
        Assert.Equal(model.Ticks[0].Fraction, model.FirstFraction, 9);
        Assert.Equal(model.Ticks[^1].Fraction, model.LastFraction, 9);

        Assert.Equal(
            ["22:00", "00:00", "02:00", "04:00", "22:00", "00:00", "02:00"],
            model.AxisTicks.Select(tick => tick.Label).ToArray());
        Assert.Equal(model.AxisTicks.Select(tick => tick.Fraction).Order(), model.AxisTicks.Select(tick => tick.Fraction));
        Assert.Equal(model.AxisTicks, model.AxisLabelTicks);
        Assert.Equal(model.AxisTicks.Select(tick => tick.Fraction), model.LaneAxis!.TickFractions);
    }

    // A failure is an empty night taking a share of the axis, or a tick for a frame no axis holds.
    [Fact]
    public void Stitched_AMemberWithNoAxis_IsLeftOut_AndTheOthersShareTheWholeAxis()
    {
        var empty = Build([Frame(null), Frame(null)]);
        Assert.Null(empty.LaneAxis);

        var model = NightStripViewModel.Stitched(
        [
            (new DateOnly(2025, 3, 10), NightOne(), 0),
            (new DateOnly(2025, 3, 11), empty, 3),
            (new DateOnly(2025, 3, 13), NightTwo(), 5),
        ]);

        Assert.Equal(2, model.LaneAxis!.Segments.Count);
        Assert.Equal(1d, model.LaneAxis.Segments[^1].ToFraction);
        Assert.Equal(["03-10", "03-13"], model.Boundaries.Select(boundary => boundary.Label).ToArray());
        Assert.Equal([0, 1, 2, 5, 6], model.Ticks.Select(tick => tick.FrameIndex).ToArray());
        Assert.Equal(2, model.Bands.Count);
    }

    [Fact]
    public void Stitched_OverNothingButEmptyNights_HasNoAxis()
    {
        var model = NightStripViewModel.Stitched(
        [
            (new DateOnly(2025, 3, 10), Build([Frame(null)]), 0),
            (new DateOnly(2025, 3, 11), Build([]), 1),
        ]);

        Assert.Null(model.LaneAxis);
        Assert.Empty(model.Ticks);
        Assert.Empty(model.Bands);
        Assert.Empty(model.Boundaries);
        Assert.False(model.HasBand);
        Assert.Equal("", model.FirstLabel);
    }

    [Fact]
    public void ActiveFrame_StartsNull()
    {
        var model = Build(ANight());

        Assert.Null(model.ActiveFrame);
    }

    [Fact]
    public void SetActiveFrame_RaisesActiveFrameChanged()
    {
        var model = Build(ANight());
        var raises = 0;
        model.ActiveFrameChanged += (_, _) => raises++;

        model.SetActiveFrame(2);

        Assert.Equal(2, model.ActiveFrame);
        Assert.Equal(1, raises);
    }

    [Fact]
    public void SetActiveFrame_WithTheSameValue_RaisesNothing()
    {
        // OnFrameTableChanged calls this on every PropertyChanged of the table's selection, so a
        // raise on an unchanged value would repaint on every unrelated write of the table too.
        var model = Build(ANight());
        model.SetActiveFrame(2);
        var raises = 0;
        model.ActiveFrameChanged += (_, _) => raises++;

        model.SetActiveFrame(2);

        Assert.Equal(0, raises);
    }

    [Fact]
    public void SetActiveFrame_Null_ClearsIt()
    {
        var model = Build(ANight());
        model.SetActiveFrame(2);
        var raised = new List<int?>();
        model.ActiveFrameChanged += (_, _) => raised.Add(model.ActiveFrame);

        model.SetActiveFrame(null);

        Assert.Null(model.ActiveFrame);
        Assert.Equal([(int?)null], raised);
    }
}
