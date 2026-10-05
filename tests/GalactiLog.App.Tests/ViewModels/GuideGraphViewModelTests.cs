using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Phd2;
using GalactiLog.Data.Queries;
using Xunit;

namespace GalactiLog.App.Tests.ViewModels;

// Phase 15B Task 4a, brief sections 9.1 to 9.7. Plain facts over literals: the transform, the
// settle windows, the downsample, the selector, the caption and the hover readout are pure members
// of the view-model precisely so that no case here needs a control or a routed input event.
public class GuideGraphViewModelTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "GalactiLog GuideGraph Test (UTC+02)",
        TimeSpan.FromHours(2),
        "GalactiLog GuideGraph Test",
        "GalactiLog GuideGraph Test");

    /// <summary>21:45:00 on the observer's clock.</summary>
    internal static readonly DateTime Start = new(2025, 3, 10, 19, 45, 0, DateTimeKind.Utc);

    // A stand-in for the Guiding section's compact duration member, which Task 4b binds.
    private static string Duration(double seconds) => FormattableString.Invariant($"{seconds:0}s");

    internal static Phd2FramePoint F(double t, double? ra = 0.1, double? dec = -0.1, bool dropped = false)
        => new(t, ra, dec, 0, "", 0, "", null, null, dropped);

    private static Phd2Event E(string type, double t) => new(type, t, "");

    // The one place a Phd2SessionFrames is built, so a seam change to that record is one edit.
    internal static Phd2SessionFrames Frames(IReadOnlyList<Phd2FramePoint> frames, params Phd2Event[] events)
        => new(1.5, Start, frames, events);

    internal static Phd2SessionSummary Session(
        Guid id, int frameCount = 500, string? telescope = "Scope", string profile = "Profile", int startMinute = 0)
        => new(
            id, Start.AddMinutes(startMinute), null, 3600, frameCount, profile, telescope, 1.5,
            null, null, null, null, null, 0, 0, 0, 0, 0, 0, null, null, null, null, null);

    internal static List<Phd2FramePoint> Every(int count, double step, Func<int, double?> ra)
        => [.. Enumerable.Range(0, count).Select(i => F(i * step, ra(i)))];

    private static GuideGraphViewModel Vm(
        IReadOnlyList<Phd2SessionSummary> sessions, Func<Guid, Phd2SessionFrames?>? get = null)
        => new(sessions, get ?? (_ => Frames([])), Zone, true, a => a(), Duration);

    /// <summary>One session of 201 frames across 1,000 seconds, largest excursion exactly 1.0, so
    /// the arcsecond axis at full zoom is 1.08 either side of zero.</summary>
    private static async Task<GuideGraphViewModel> Loaded(params Phd2Event[] events)
    {
        var vm = Vm(
            [Session(Guid.NewGuid())],
            _ => Frames(Every(201, 5, i => i == 100 ? 1.0 : 0.2), events));
        await vm.PendingLoad;
        return vm;
    }

    // ------------------------------------------------------------------ 9.1 the transform

    public static TheoryData<string, double[], double[], string, double[], double[]> TransformRows => new()
    {
        // name, view, full (min, max, maxSpan or NaN), gesture, arguments, expected
        { "zoom in about the left edge", [0, 1000], [0, 1000, double.NaN], "zoom", [0, 0.5, 10], [0, 500] },
        { "zoom in about the centre", [0, 1000], [0, 1000, double.NaN], "zoom", [500, 0.5, 10], [250, 750] },
        { "zoom in about the right edge", [0, 1000], [0, 1000, double.NaN], "zoom", [1000, 0.5, 10], [500, 1000] },
        { "one more notch at the minimum span", [100, 110], [0, 1000, double.NaN], "zoom", [105, 0.82, 10], [100.9, 110.9] },
        { "zoom out past the range, parked at the start", [0, 600], [0, 1000, double.NaN], "zoom", [600, 1.5, 10], [0, 900] },
        { "zoom out past the range, exactly full width", [200, 800], [0, 1000, double.NaN], "zoom", [500, 2, 10], [0, 1000] },
        { "pan inside the range", [100, 300], [0, 1000, double.NaN], "pan", [50, 10], [150, 350] },
        { "pan right past the start", [100, 300], [0, 1000, double.NaN], "pan", [-500, 10], [0, 200] },
        { "pan left past the end", [100, 300], [0, 1000, double.NaN], "pan", [5000, 10], [800, 1000] },
        { "wider than the range is centred", [0, 5], [-1, 1, 20], "clamp", [0.1], [-2.5, 2.5] },
        { "wider than the ceiling is cut to it", [-50, 50], [-1, 1, 20], "clamp", [0.1], [-10, 10] },
        { "a zero width range returns the range", [0, 10], [5, 5, double.NaN], "clamp", [10], [5, 5] },
    };

    /// <summary>A failure looks like: the width clamped after the parking rather than before it,
    /// which is right on every interior row and wrong on every edge row.</summary>
    [Theory]
    [MemberData(nameof(TransformRows))]
    public void TheTransform_RowByRow(
        string name, double[] view, double[] full, string gesture, double[] args, double[] expected)
    {
        var start = new GuideView(view[0], view[1]);
        var bounds = new GuideBounds(full[0], full[1], double.IsNaN(full[2]) ? null : full[2]);

        var actual = gesture switch
        {
            "zoom" => GuideGraphViewModel.ZoomView(start, bounds, args[0], args[1], args[2]),
            "pan" => GuideGraphViewModel.PanView(start, bounds, args[0], args[1]),
            _ => GuideGraphViewModel.ClampView(start, bounds, args[0]),
        };

        Assert.True(
            Math.Abs(actual.Min - expected[0]) < 1e-9 && Math.Abs(actual.Max - expected[1]) < 1e-9,
            $"{name}: expected ({expected[0]}, {expected[1]}), got ({actual.Min}, {actual.Max}).");
    }

    [Fact]
    public void TheSixPortedFigures_AreTheWebs()
    {
        Assert.Equal(0.82, GuideGraphViewModel.ZoomStep);
        Assert.Equal(10, GuideGraphViewModel.MinTimeSpan);
        Assert.Equal(0.1, GuideGraphViewModel.MinArcsecSpan);
        Assert.Equal(0.08, GuideGraphViewModel.ArcsecHeadroom);
        Assert.Equal(10, GuideGraphViewModel.ArcsecZoomOut);
        Assert.Equal(2000, GuideGraphViewModel.MaxPlotPoints);

        var range = GuideGraphViewModel.ArcsecRange(2);
        Assert.Equal(-2.16, range.Min, 12);
        Assert.Equal(2.16, range.Max, 12);
        Assert.Equal(43.2, range.MaxSpan!.Value, 12);
    }

    // ------------------------------------------------------------------ 9.2 the override drop

    /// <summary>A failure looks like: one "at least full width" predicate on both axes, which
    /// collapses a deliberately zoomed-out calm night back to the data's own scale.</summary>
    [Fact]
    public async Task TheOverrideDrop_IsAsymmetric()
    {
        var vm = await Loaded();

        vm.ZoomTime(500, 3);
        Assert.NotNull(vm.TimeOverride);
        vm.ZoomTime(500, -3);
        Assert.Null(vm.TimeOverride);

        vm.ZoomArcsec(0, 2);
        Assert.NotNull(vm.ArcsecOverride);
        vm.ZoomArcsec(0, -2);
        Assert.Null(vm.ArcsecOverride);

        vm.ZoomArcsec(0, -1);
        Assert.True(
            vm.ArcsecOverride is not null,
            "A window wider than the data is a zoomed-out reading of a calm night and is kept.");
        Assert.False(GuideGraphViewModel.IsFullView(vm.ArcsecOverride.Value, vm.ArcsecBounds));
        Assert.Equal(0, vm.ArcsecView.Min + vm.ArcsecView.Max, 9);
        Assert.Equal(2.16 / 0.82, vm.ArcsecView.Max - vm.ArcsecView.Min, 9);
    }

    [Fact]
    public async Task AZeroWheelDelta_StepsNothing()
    {
        var vm = await Loaded();
        vm.ZoomTime(500, 0);
        vm.ZoomArcsec(0, 0);
        Assert.Null(vm.TimeOverride);
        Assert.Null(vm.ArcsecOverride);

        // A tilt wheel arrives while a window is held: a failure looks like a sideways scroll
        // disturbing the reader's zoom, or recomputing the plot for a step that stepped nothing.
        vm.ZoomTime(500, 3);
        vm.ZoomArcsec(0.3, 2);
        var time = vm.TimeOverride;
        var arcsec = vm.ArcsecOverride;
        var plotted = vm.Plotted;
        vm.ZoomTime(321, 0);
        vm.ZoomArcsec(-0.2, 0);
        Assert.Equal(time, vm.TimeOverride);
        Assert.Equal(arcsec, vm.ArcsecOverride);
        Assert.Same(plotted, vm.Plotted);
    }

    /// <summary>A failure looks like: a full downsample and a repaint per wheel notch that could
    /// not move the window, one more notch in at the minimum span or out at full width.</summary>
    [Fact]
    public async Task AZoomThatCannotMove_KeepsThePlottedMemo_AndRaisesNothing()
    {
        var vm = await Loaded();
        var raised = 0;
        vm.PropertyChanged += (_, _) => raised++;

        var atRest = vm.Plotted;
        vm.ZoomTime(500, -1);
        Assert.Same(atRest, vm.Plotted);

        vm.ZoomTime(0, 40);
        Assert.Equal(GuideGraphViewModel.MinTimeSpan, vm.TimeView.Max - vm.TimeView.Min, 9);
        var held = vm.Plotted;
        raised = 0;
        vm.ZoomTime(0, 1);
        Assert.Same(held, vm.Plotted);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Pan_DoesNothingWhileBothAxesShowEverything_AndMovesOnlyTheZoomedAxis()
    {
        var vm = await Loaded();

        // A failure looks like: the memo dropped and a repaint raised on every pointer move of a
        // drag that had no slack to take up, a full downsample per move on a long night.
        var atRest = vm.Plotted;
        var raised = 0;
        vm.PropertyChanged += (_, _) => raised++;
        for (var move = 0; move < 25; move++)
        {
            vm.Pan(100, 0.5);
        }

        Assert.Null(vm.TimeOverride);
        Assert.Null(vm.ArcsecOverride);
        Assert.Same(atRest, vm.Plotted);
        Assert.Equal(0, raised);

        vm.ZoomTime(500, 5);
        var before = vm.TimeView;
        vm.Pan(-50, 0.5);
        Assert.Equal(before.Min - 50, vm.TimeView.Min, 9);
        Assert.Equal(before.Max - before.Min, vm.TimeView.Max - vm.TimeView.Min, 9);
        Assert.Null(vm.ArcsecOverride);

        vm.ResetView();
        Assert.Null(vm.TimeOverride);
    }

    [Fact]
    public async Task SelectingAnotherSession_ClearsBothWindows()
    {
        var vm = Vm(
            [Session(Guid.NewGuid()), Session(Guid.NewGuid(), startMinute: 90)],
            _ => Frames(Every(201, 5, _ => 0.5)));
        await vm.PendingLoad;
        vm.ZoomTime(500, 4);
        vm.ZoomArcsec(0, 2);

        vm.SelectedSession = vm.SessionOptions[1];
        await vm.PendingLoad;

        Assert.Null(vm.TimeOverride);
        Assert.Null(vm.ArcsecOverride);
    }

    /// <summary>A failure looks like: between the selection and the landed read, the previous
    /// session's zoomed slice drawn stretched across its full axis.</summary>
    [Fact]
    public async Task ASessionSwitch_RedrawsThePreviousSessionWhole_UntilTheNextLands()
    {
        var second = Guid.NewGuid();
        using var release = new ManualResetEventSlim();
        var vm = Vm(
            [Session(Guid.NewGuid()), Session(second, startMinute: 90)],
            id =>
            {
                if (id == second)
                {
                    release.Wait();
                }

                return Frames(Every(201, 5, _ => 0.5));
            });
        await vm.PendingLoad;
        vm.ZoomTime(500, 8);
        Assert.True(vm.Plotted.Count < 201);

        vm.SelectedSession = vm.SessionOptions[1];
        Assert.Equal(201, vm.Plotted.Count);

        release.Set();
        await vm.PendingLoad;
    }

    /// <summary>A failure looks like: the host's answer to the cleared filter re-entering
    /// SetSessions half way through its own body and rebuilding the list twice.</summary>
    [Fact]
    public void SetSessions_IsNotReEnteredByTheFilterItClears()
    {
        var vm = Vm([Session(Guid.NewGuid(), telescope: "A"), Session(Guid.NewGuid(), telescope: "B")]);
        vm.SelectedRigOption = "B";
        var next = new[] { Session(Guid.NewGuid(), telescope: "A"), Session(Guid.NewGuid(), telescope: "C") };

        var rebuilt = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GuideGraphViewModel.SessionOptions))
            {
                rebuilt++;
            }
            else if (e.PropertyName == nameof(GuideGraphViewModel.SelectedRig))
            {
                // What Task 4b's handler does: narrow locally by RigLabel and hand the lists back.
                vm.SetSessions(next, [.. next.Where(s => GuideGraphViewModel.RigLabel(s) == (vm.SelectedRig ?? GuideGraphViewModel.RigLabel(s)))]);
            }
        };

        vm.SetSessions(next, []);

        Assert.Equal(1, rebuilt);
        Assert.Null(vm.SelectedRig);
        Assert.Equal(2, vm.SessionOptions.Count);
    }

    // ------------------------------------------------------------------ 9.3 the settle windows

    private static SettleWindow[] Windows(double fallback, params Phd2Event[] events)
        => [.. GuideGraphViewModel.SettleWindows(events, fallback)];

    [Fact]
    public void Settle_Rule1_EventsAreTakenInTimeOrder()
        => Assert.Equal(
            [new SettleWindow(10, 30, false)],
            Windows(99, E(Phd2EventTypes.SettleDone, 30), E(Phd2EventTypes.SettleStart, 10)));

    [Fact]
    public void Settle_Rule2_AStartWithNothingOpen_OpensAWindow()
        => Assert.Equal(
            [new SettleWindow(10, 20, false)],
            Windows(99, E(Phd2EventTypes.SettleStart, 10), E(Phd2EventTypes.SettleDone, 20)));

    /// <summary>The separating case. A failure looks like: the implementer calling
    /// <c>Phd2Metrics.DitherSettleWindows</c>, which compiles, draws plausible bands and paints
    /// none of them red.</summary>
    [Fact]
    public void Settle_Rule3_ASecondStart_ClosesTheFirstAsOrdinary_AndTheMetricRuleDiffers()
    {
        Phd2Event[] events =
        [
            E(Phd2EventTypes.SettleStart, 10), E(Phd2EventTypes.SettleStart, 20), E(Phd2EventTypes.SettleFailed, 30),
        ];

        var presentation = Windows(30, events);
        Assert.Equal([new SettleWindow(10, 20, false), new SettleWindow(20, 30, true)], presentation);

        var metric = Phd2Metrics.DitherSettleWindows(events.Select(e => (e.Type, e.TimeOffset)), 30);
        Assert.Equal([(10d, 30d)], metric);
        Assert.False(
            presentation.Select(w => (w.Start, w.End)).SequenceEqual(metric),
            "The two rules answer different questions (spec 12.4): the metric rule swallows the second "
                + "start and carries no failed flag, so it decides which frames an RMS counted and cannot "
                + "paint a failed settle red. The graph must not be fed from it.");
    }

    [Fact]
    public void Settle_Rule4_ACompletionCloses_AndOnlySettleFailedIsFailed()
    {
        Assert.Equal(
            [new SettleWindow(10, 20, true), new SettleWindow(40, 50, false)],
            Windows(
                99,
                E(Phd2EventTypes.SettleStart, 10),
                E(Phd2EventTypes.SettleFailed, 20),
                E(Phd2EventTypes.SettleStart, 40),
                E(Phd2EventTypes.SettleDone, 50)));
    }

    [Fact]
    public void Settle_Rule5_ACompletionWithNothingOpen_IsDropped()
        => Assert.Empty(Windows(99, E(Phd2EventTypes.SettleDone, 5), E(Phd2EventTypes.SettleFailed, 6)));

    [Fact]
    public void Settle_Rule6_EveryOtherEvent_DitherIncluded_IsIgnored()
        => Assert.Equal(
            [new SettleWindow(10, 20, false)],
            Windows(
                99,
                E(Phd2EventTypes.Dither, 5),
                E(Phd2EventTypes.SettleStart, 10),
                E(Phd2EventTypes.LockShift, 12),
                E(Phd2EventTypes.SettleDone, 20)));

    [Fact]
    public void Settle_Rule7_AWindowStillOpen_ClosesAtTheFallbackAsOrdinary()
        => Assert.Equal([new SettleWindow(10, 99, false)], Windows(99, E(Phd2EventTypes.SettleStart, 10)));

    [Fact]
    public async Task TheLoadedSession_FeedsTheFallbackFromTheLastFrame_AndListsTheDithers()
    {
        var vm = await Loaded(E(Phd2EventTypes.Dither, 100), E(Phd2EventTypes.SettleStart, 990));

        Assert.Equal([new SettleWindow(990, 1000, false)], vm.SettleBands);
        Assert.Equal([100d], vm.DitherTimes);
    }

    // ------------------------------------------------------------------ 9.4 downsampling

    /// <summary>A failure looks like: a plain stride, which passes the count and drops the
    /// excursion. The excursion is asserted by value.</summary>
    [Fact]
    public void Downsample_KeepsTheExcursion_AndBothEndpoints()
    {
        var frames = Every(5000, 1, i => i == 2500 ? 9.0 : 0.1);

        var kept = GuideGraphViewModel.Downsample(frames);

        Assert.True(kept.Count <= GuideGraphViewModel.MaxPlotPoints);
        Assert.Contains(kept, f => f.Ra == 9.0);
        Assert.Same(frames[0], kept[0]);
        Assert.Same(frames[^1], kept[^1]);
        Assert.Equal(kept.OrderBy(f => f.T), kept);
    }

    [Fact]
    public void Downsample_AtOrBelowTheBudget_KeepsEveryInstanceInOrder()
    {
        var frames = Every(100, 1, _ => 0.1);
        var kept = GuideGraphViewModel.Downsample(frames);

        Assert.Equal(100, kept.Count);
        Assert.All(Enumerable.Range(0, 100), i => Assert.Same(frames[i], kept[i]));
    }

    [Fact]
    public void Downsample_ABucketWithNothingToKeep_ContributesItsFirstFrame()
    {
        List<Phd2FramePoint> frames = [.. Enumerable.Range(0, 5000).Select(i => F(i, null, null))];

        var kept = GuideGraphViewModel.Downsample(frames);

        // 666 buckets of 7.5 frames: bucket 1 starts at floor(7.5) and bucket 2 at floor(15.0).
        Assert.Equal(667, kept.Count);
        Assert.Same(frames[7], kept[1]);
        Assert.Same(frames[15], kept[2]);
    }

    [Fact]
    public void Downsample_KeepsTheFirstDroppedFrameOfABucket()
    {
        var frames = Every(5000, 1, _ => 0.1);
        frames[3001] = F(3001, null, null, dropped: true);

        Assert.Contains(GuideGraphViewModel.Downsample(frames), f => f.Dropped);
    }

    /// <summary>A failure looks like: downsampling once at load, so that zooming in magnifies the
    /// bucket survivors instead of resolving the frames between them.</summary>
    [Fact]
    public async Task Plotted_IsDownsampledOverTheVisibleWindow_NotOverTheSession()
    {
        var frames = Every(10000, 1, i => i % 7 * 0.01);
        var vm = Vm([Session(Guid.NewGuid())], _ => Frames(frames));
        await vm.PendingLoad;
        Assert.True(vm.Plotted.Count <= GuideGraphViewModel.MaxPlotPoints);

        vm.ZoomTime(5000, 10);
        var window = vm.TimeView;
        var inWindow = GuideGraphViewModel.SliceByTime(frames, window.Min, window.Max);
        var downsampledFirst = GuideGraphViewModel.SliceByTime(
            GuideGraphViewModel.Downsample(frames), window.Min, window.Max);

        Assert.Equal(inWindow.Count, vm.Plotted.Count);
        Assert.True(
            vm.Plotted.Count > downsampledFirst.Count,
            $"Zoomed to {inWindow.Count} frames the plot must hold every one of them, not the "
                + $"{downsampledFirst.Count} that survived a whole-session pass.");
    }

    [Fact]
    public void SliceByTime_TakesOneFrameEitherSide()
    {
        var frames = Every(11, 10, _ => 0.1);

        var slice = GuideGraphViewModel.SliceByTime(frames, 25, 55);

        Assert.Equal([20d, 30d, 40d, 50d, 60d], slice.Select(f => f.T));
        Assert.Empty(GuideGraphViewModel.SliceByTime([], 0, 10));
        Assert.Equal([0d, 10d], GuideGraphViewModel.SliceByTime(frames, -50, 5).Select(f => f.T));
    }

    // ------------------------------------------------------------------ 9.5 the symmetric bound

    /// <summary>A failure looks like: the ceiling applied without the floor, which gives a 0.1
    /// arcsecond axis on a perfectly guided night and makes read noise look like a disaster.
    /// </summary>
    [Theory]
    [InlineData(1.63, 1, 1.7)]
    [InlineData(1.0, 1, 1.0)]
    [InlineData(0.63, 1, 1.0)]
    [InlineData(0.63, 0.5, 0.7)]
    [InlineData(0.0, 1, 1.0)]
    public void SymmetricBound_CeilingToATenth_WithAFloor(double excursion, double minimum, double expected)
        => Assert.Equal(
            expected,
            GuideGraphViewModel.SymmetricBound([F(0, excursion, -0.01), F(1, 0.01, 0.0)], minimum),
            12);

    [Fact]
    public void SymmetricBound_IgnoresNullNaNAndInfinity_AndReadsDecAsWellAsRa()
    {
        Assert.Equal(1, GuideGraphViewModel.SymmetricBound([F(0, null, null), F(1, null, null)]));
        Assert.Equal(1, GuideGraphViewModel.SymmetricBound([]));
        Assert.Equal(
            2.4,
            GuideGraphViewModel.SymmetricBound(
                [F(0, double.NaN, double.PositiveInfinity), F(1, 0.2, -2.31), F(2, double.NegativeInfinity, 0)]),
            12);
    }

    // ------------------------------------------------------------------ 9.6 selector and rig filter

    [Fact]
    public void TheDefaultSelection_IsTheFirstSessionThatIsNotGated()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var vm = Vm([Session(ids[0], 40), Session(ids[1], 500, startMinute: 10), Session(ids[2], 500, startMinute: 20)]);

        Assert.Equal(ids[1], vm.SelectedSession!.Id);
        Assert.True(vm.ShowSessionSelector);
    }

    [Fact]
    public void WhenEverySessionIsGated_TheDefaultIsTheFirst()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var vm = Vm([Session(ids[0], 40), Session(ids[1], 50, startMinute: 10), Session(ids[2], 60, startMinute: 20)]);

        Assert.Equal(ids[0], vm.SelectedSession!.Id);
    }

    [Fact]
    public void OneSession_OffersNoSelector_AndNoSessions_SelectsNothing()
    {
        Assert.False(Vm([Session(Guid.NewGuid())]).ShowSessionSelector);

        var empty = Vm([]);
        Assert.Null(empty.SelectedSession);
        Assert.False(empty.HasFrames);
        Assert.Equal("", empty.Caption);
    }

    [Fact]
    public void TwoRigs_OfferTheFilter_AllRigsFirst_ThenOrdinal_WithTheProfileFallback()
    {
        var vm = Vm(
        [
            Session(Guid.NewGuid(), telescope: "alpha"),
            Session(Guid.NewGuid(), telescope: null, profile: "Zeta profile", startMinute: 10),
            Session(Guid.NewGuid(), telescope: "alpha", startMinute: 20),
        ]);

        Assert.True(vm.ShowRigFilter);
        Assert.Equal(["All rigs", "Zeta profile", "alpha"], vm.RigOptions);
        Assert.Null(vm.SelectedRig);
    }

    [Fact]
    public void OneRig_OffersNoFilter()
    {
        var vm = Vm([Session(Guid.NewGuid()), Session(Guid.NewGuid(), startMinute: 10)]);

        Assert.False(vm.ShowRigFilter);
        Assert.Empty(vm.RigOptions);
    }

    /// <summary>A failure looks like: an empty panel on a night that has data, because the filter
    /// still names a rig the night does not carry.</summary>
    [Fact]
    public void AFilterNamingARigTheNewNightLacks_ClearsItself()
    {
        var vm = Vm([Session(Guid.NewGuid(), telescope: "A"), Session(Guid.NewGuid(), telescope: "B")]);
        vm.SelectedRigOption = "B";
        Assert.Equal("B", vm.SelectedRig);

        var next = new[] { Session(Guid.NewGuid(), telescope: "A"), Session(Guid.NewGuid(), telescope: "C") };
        vm.SetSessions(next, visible: []);

        Assert.Equal(GuideGraphViewModel.AllRigs, vm.SelectedRigOption);
        Assert.Null(vm.SelectedRig);
        Assert.Equal(2, vm.SessionOptions.Count);
        Assert.Equal(next[0].Id, vm.SelectedSession!.Id);
    }

    [Fact]
    public async Task ChoosingARig_MovesTheSelectionOntoTheNarrowedList_AndKeepsTheFilter()
    {
        var a = Session(Guid.NewGuid(), telescope: "A");
        var b = Session(Guid.NewGuid(), telescope: "B", startMinute: 10);
        var vm = Vm([a, b]);
        Assert.Equal(a.Id, vm.SelectedSession!.Id);

        // The harness posts synchronously, so the constructor's frames read publishes from the
        // pool thread and raises PropertyChanged from it. Draining it before the subscription
        // exists is what stops that thread appending to `raised` while the assertion enumerates
        // it ("Collection was modified"). Production posts through UiPost.Default and has no such
        // seam (task4b-review.md P3-9).
        await vm.PendingLoad;

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.SelectedRigOption = "B";
        Assert.Contains(nameof(GuideGraphViewModel.SelectedRig), raised);

        // The host answers the change with the list the night query narrowed.
        vm.SetSessions([a, b], visible: [b]);

        Assert.Equal("B", vm.SelectedRig);
        Assert.Equal(b.Id, vm.SelectedSession!.Id);
        Assert.False(vm.ShowSessionSelector);
        Assert.True(vm.ShowRigFilter);
    }

    [Fact]
    public void TheRigList_KeepsItsInstanceWhileItsContentIsEqual()
    {
        // Fixer item 53. RigOptions is a pure function of the unfiltered night, which does not
        // change while a graph lives, so replacing it on every SetSessions made the bound combo
        // box clear its selection mid-narrowing. A failure looks like a new list instance for the
        // same three rigs, which is what forced a stable mirror onto the section.
        var a = Session(Guid.NewGuid(), telescope: "A");
        var b = Session(Guid.NewGuid(), telescope: "B", startMinute: 10);
        var vm = Vm([a, b]);

        var first = vm.RigOptions;
        Assert.Equal([GuideGraphViewModel.AllRigs, "A", "B"], first);

        vm.SetSessions([a, b], visible: [b]);
        Assert.Same(first, vm.RigOptions);

        vm.SetSessions([a, b], visible: null);
        Assert.Same(first, vm.RigOptions);

        // A night whose rig set really changed does get a new list.
        var c = Session(Guid.NewGuid(), telescope: "C", startMinute: 20);
        vm.SetSessions([a, b, c], visible: null);
        Assert.NotSame(first, vm.RigOptions);
        Assert.Equal([GuideGraphViewModel.AllRigs, "A", "B", "C"], vm.RigOptions);
    }

    [Fact]
    public void TheSecondsClock_IsSessionTimeFormats_AndTheGraphDeclaresNoClockFormat()
    {
        // Fixer item 23. The graph declared "HH:mm:ss" and "h:mm:ss tt" privately, a second copy
        // of the shapes MetricText.cs owns. A failure looks like the graph's axis drifting from
        // the frame table's Time column after one of the two copies is edited.
        var start = Start;
        Assert.Equal(
            SessionTimeFormat.FormatWithSeconds(
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(start.AddSeconds(90), DateTimeKind.Utc), Zone),
                true),
            GuideGraphViewModel.ClockAt(start, 90d, Zone, true, withSeconds: true, Duration));

        Assert.Equal(
            SessionTimeFormat.FormatWithSeconds(
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(start.AddSeconds(90), DateTimeKind.Utc), Zone),
                false),
            GuideGraphViewModel.ClockAt(start, 90d, Zone, false, withSeconds: true, Duration));

        var code = TestSupport.SourceScan.StripComments(File.ReadAllText(Path.Combine(
            TestSupport.SourceScan.SrcRoot(), "GalactiLog.App", "ViewModels", "TargetDetail", "GuideGraphViewModel.cs")));
        Assert.DoesNotContain("mm:ss", code, StringComparison.Ordinal);
        Assert.DoesNotContain("HH:mm", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLabel_CarriesTheRigOnlyWhenTheVisibleListMixesRigs()
    {
        var mixed = Vm([Session(Guid.NewGuid(), telescope: "A"), Session(Guid.NewGuid(), telescope: "B", startMinute: 10)]);
        Assert.Equal("21:45 · 3600s · A", mixed.SessionOptions[0].Label);
        Assert.Equal("21:55 · 3600s · B", mixed.SessionOptions[1].Label);

        var single = Vm([Session(Guid.NewGuid(), telescope: "A"), Session(Guid.NewGuid(), telescope: "A", startMinute: 10)]);
        Assert.Equal("21:45 · 3600s", single.SessionOptions[0].Label);
    }

    /// <summary>A failure looks like: the marker derived from a local frame count comparison,
    /// which is a second copy of the gate.</summary>
    [Fact]
    public void TheShortMarker_IsTheMinFramesGate_AndTheViewModelHoldsNoSecondCopy()
    {
        var vm = Vm(
        [
            Session(Guid.NewGuid(), Phd2Metrics.MinFrames - 1),
            Session(Guid.NewGuid(), Phd2Metrics.MinFrames, startMinute: 10),
        ]);

        Assert.EndsWith(" · short", vm.SessionOptions[0].Label, StringComparison.Ordinal);
        Assert.DoesNotContain("short", vm.SessionOptions[1].Label, StringComparison.Ordinal);

        var code = SourceScan.StripComments(File.ReadAllText(Path.Combine(
            SourceScan.SrcRoot(), "GalactiLog.App", "ViewModels", "TargetDetail", "GuideGraphViewModel.cs")));
        Assert.DoesNotContain("FrameCount", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DitherSettleWindows", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureAwait", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Round", code, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 9.7 caption and hover

    [Fact]
    public void TheCaption_OnAZonedSession_ReadsClockTimesAndTheWholeLength()
        => Assert.Equal(
            "Error in arcseconds, 21:45 to 22:15 of 3h 20m.",
            GuideGraphViewModel.RangeCaption(new GuideView(0, 1800), 12000, Start, Zone, true, _ => "3h 20m"));

    /// <summary>The seam case: an unzoned session stores no UTC start, and a wrong clock time is
    /// worse than an elapsed one. Driven through the pure member with a null, so it holds whether
    /// <c>Phd2SessionFrames.StartedAtUtc</c> is nullable yet or not.</summary>
    [Fact]
    public void TheCaption_AndTheAxis_WithNoSessionStart_ReadElapsedDurations()
    {
        var caption = GuideGraphViewModel.RangeCaption(new GuideView(60, 1800), 12000, null, Zone, true, Duration);

        Assert.Equal("Error in arcseconds, 60s to 1800s of 12000s.", caption);
        Assert.DoesNotContain(":", caption, StringComparison.Ordinal);
        Assert.Equal("90s", GuideGraphViewModel.ClockAt(null, 90, Zone, true, true, Duration));
        Assert.Equal("21:46:30", GuideGraphViewModel.ClockAt(Start, 90, Zone, true, true, Duration));
        Assert.Equal("9:46:30 PM", GuideGraphViewModel.ClockAt(Start, 90, Zone, false, true, Duration));
    }

    [Fact]
    public async Task TheLoadedCaption_FollowsTheWindow()
    {
        var vm = await Loaded();
        Assert.Equal("Error in arcseconds, 21:45 to 22:01 of 3600s.", vm.Caption);

        vm.ZoomTime(0, 20);
        Assert.Equal("Error in arcseconds, 21:45 of 3600s.", vm.Caption);
    }

    // A failure is the caption giving the last frame's time (1000s here) where the picker gives
    // the session's recorded length (3600s): two lengths for one session, a few seconds apart.
    [Fact]
    public async Task TheCaptionLength_IsThePickersLength()
    {
        var vm = await Loaded();

        Assert.Contains("3600s", vm.SessionOptions[0].Label, StringComparison.Ordinal);
        Assert.EndsWith("of 3600s.", vm.Caption, StringComparison.Ordinal);
    }

    // A failure is "21:45 to 21:45" for a window that rounds to one clock minute.
    [Fact]
    public void TheCaption_OfAWindowInOneClockMinute_ReadsOneTimeAndTheLength()
        => Assert.Equal(
            "Error in arcseconds, 21:45 of 40s.",
            GuideGraphViewModel.RangeCaption(new GuideView(0, 20), 40, Start, Zone, true, Duration));

    [Fact]
    public async Task HoverText_NamesTheNearerFrame_ToTheSecond()
    {
        var vm = Vm([Session(Guid.NewGuid())], _ => Frames([F(0, 0.123, -0.456), F(10, 1.5, 2.5)]));
        await vm.PendingLoad;

        Assert.Equal(["21:45:00", "RA: 0.12″", "Dec: -0.46″"], vm.HoverText(4)!.Split(Environment.NewLine));
        Assert.Equal(["21:45:10", "RA: 1.50″", "Dec: 2.50″"], vm.HoverText(6)!.Split(Environment.NewLine));
        Assert.StartsWith("21:45:00", vm.HoverText(-500), StringComparison.Ordinal);
        Assert.StartsWith("21:45:10", vm.HoverText(500), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HoverText_OnADroppedFrame_ReadsStarLostAlone()
    {
        var vm = Vm([Session(Guid.NewGuid())], _ => Frames([F(0), F(10, 0.4, 0.4, dropped: true)]));
        await vm.PendingLoad;

        Assert.Equal(["21:45:10", "Star lost"], vm.HoverText(10)!.Split(Environment.NewLine));
    }

    [Fact]
    public async Task HoverText_AHiddenTrace_ContributesNoLine()
    {
        var vm = Vm([Session(Guid.NewGuid())], _ => Frames([F(0, 0.5, 0.25)]));
        await vm.PendingLoad;

        vm.Legend[0].IsShown = false;

        Assert.Equal(["21:45:00", "Dec: 0.25″"], vm.HoverText(0)!.Split(Environment.NewLine));
        Assert.Null(Vm([]).HoverText(0));
    }

    // ------------------------------------------------------------------ the legend and the load

    [Fact]
    public void TheLegend_IsFiveEntriesInOrder_AllShown_AndATogglesRaisesAChange()
    {
        var vm = Vm([]);
        Assert.Equal(["RA", "Dec", "Star lost", "Dither", "Settling"], vm.Legend.Select(e => e.Label));
        Assert.All(vm.Legend, e => Assert.True(e.IsShown));

        var raised = 0;
        vm.PropertyChanged += (_, _) => raised++;
        vm.Legend[4].IsShown = false;

        Assert.False(vm.IsShown(GuideLayer.Settling));
        Assert.True(vm.IsShown(GuideLayer.Dither));
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task AFailedRead_KeepsThePreviousPlot_AndRecordsTheFailure()
    {
        var second = Guid.NewGuid();
        var vm = Vm(
            [Session(Guid.NewGuid()), Session(second, startMinute: 90)],
            id => id == second ? throw new InvalidOperationException("boom") : Frames(Every(50, 1, _ => 0.3)));
        await vm.PendingLoad;
        Assert.True(vm.HasFrames);

        vm.SelectedSession = vm.SessionOptions[1];
        await vm.PendingLoad;

        Assert.IsType<InvalidOperationException>(vm.LastFailure);
        Assert.True(vm.HasFrames);
        Assert.False(vm.IsLoading);
    }

    // A failure is "21:45 to 21:45 lies outside the timeline" for a session that rounds to one minute.
    [Fact]
    public async Task TheOutsideCaption_OfAOneMinuteSession_ReadsOneTime()
    {
        var vm = Vm([Session(Guid.NewGuid())], _ => Frames(Every(3, 10, _ => 0.3)));
        await vm.PendingLoad;
        var night = new DateTime(2025, 3, 11, 0, 0, 0, DateTimeKind.Unspecified);

        vm.UseLaneAxis(new NightLaneAxis(night, night.AddHours(2), NightLaneAxis.SharedPlotLeft, NightLaneAxis.SharedPlotRight));

        Assert.Equal("Guide session 21:45 lies outside the timeline, 00:00 to 02:00.", vm.Caption);
    }

    // A failure is the old plot's times joined to the newly selected session's length.
    [Fact]
    public async Task AFailedSwitch_KeepsTheOldPlotsCaptionLength()
    {
        var first = Session(Guid.NewGuid()) with { DurationS = 3600 };
        var second = Session(Guid.NewGuid(), startMinute: 90) with { DurationS = 7200 };
        var vm = Vm(
            [first, second],
            id => id == second.Id ? throw new InvalidOperationException("boom") : Frames(Every(50, 1, _ => 0.3)));
        await vm.PendingLoad;
        var before = vm.Caption;

        vm.SelectedSession = vm.SessionOptions[1];
        await vm.PendingLoad;

        Assert.IsType<InvalidOperationException>(vm.LastFailure);
        Assert.Equal(second.Id, vm.SelectedSession!.Id);
        Assert.Contains("of 3600s.", before, StringComparison.Ordinal);
        Assert.Equal(before, vm.Caption);
    }

    /// <summary>A failure looks like: a session of one frame, whose time window has no width,
    /// dividing by zero and handing the control a NaN coordinate.</summary>
    [Fact]
    public void PixelConversion_RoundTrips_AndAZeroWidthWindowAnswersTheMiddle()
    {
        var view = new GuideView(100, 300);
        Assert.Equal(150, GuideGraphViewModel.ValueAt(view, 0.25), 12);
        Assert.Equal(0.25, GuideGraphViewModel.FractionOf(view, 150), 12);
        Assert.Equal(0.5, GuideGraphViewModel.FractionOf(new GuideView(5, 5), 5));
        Assert.Equal(5, GuideGraphViewModel.ValueAt(new GuideView(5, 5), 0.7));
    }

    [Fact]
    public void TickValues_AreMultiplesOfAStepThatFits()
    {
        Assert.Equal([0d, 300, 600, 900], GuideGraphViewModel.TickValues(new GuideView(0, 1000), 4, clockSteps: true));
        Assert.Equal([-1d, -0.5, 0, 0.5, 1], GuideGraphViewModel.TickValues(new GuideView(-1.08, 1.08), 8, clockSteps: false));
        Assert.Empty(GuideGraphViewModel.TickValues(new GuideView(5, 5), 8, clockSteps: true));
    }

    // D211: red while the graph or its section still carries a brush span member.
    [Fact]
    public void TheGraphAndItsSection_HaveNoSpanMember()
    {
        foreach (var name in new[] { "ShowSpan", "ClearSpan", "HasSpan", "ReapplySpanCommand" })
        {
            Assert.Empty(typeof(GuideGraphViewModel).GetMember(name));
        }

        Assert.Empty(typeof(GuidingSectionViewModel).GetMember("ShowBrush"));
        Assert.Empty(typeof(GuidingSectionViewModel).GetMember("ClearBrush"));
    }
}
