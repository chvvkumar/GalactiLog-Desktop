using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Theme;
using GalactiLog.Core.Sessions;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace GalactiLog.App.ViewModels.Stats;

/// <summary>Spec 12.5's three timeline granularities.</summary>
public enum TimelineGranularity
{
    Monthly,
    Weekly,
    Daily,
}

/// <summary>Spec 12.5's five timeline range presets.</summary>
public enum TimelineRangePreset
{
    All,
    OneYear,
    Quarter,
    Month,
    Week,
}

/// <summary>One entry of the granularity selector. A concrete record rather than a generic one
/// because a XAML <c>x:DataType</c> cannot name an open generic.</summary>
public sealed record GranularityOption(TimelineGranularity Value, string Label);

/// <summary>One bar of the timeline, after windowing and gap filling.</summary>
/// <param name="Period">The raw period key, <c>yyyy-MM</c>, <c>yyyy-Www</c> or
/// <c>yyyy-MM-dd</c>.</param>
/// <param name="EfficiencyPercent">Null when the period's dark hours came to zero, when every one
/// of its dates is past the efficiency horizon, or whenever observer coordinates are unset (in
/// which case there is no efficiency series at all).</param>
public sealed record TimelineBar(
    string Period,
    string Label,
    DateOnly Start,
    DateOnly End,
    double IntegrationSeconds,
    double? EfficiencyPercent)
{
    /// <summary>The bar's height in hours, which is what the chart plots.</summary>
    public double IntegrationHours => IntegrationSeconds / 3600d;
}

/// <summary>
/// Spec 12.5's Imaging timeline: integration per period as a column chart, with an efficiency
/// percentage line on a secondary 0 to 100 axis when observer coordinates are configured.
/// </summary>
/// <remarks>
/// <para>
/// Spec 12.5, "Wheel zoom and drag pan" (Phase 14C, UI layout ruling 13), reverses Phase 9's Q15:
/// the wheel now steps the granularity selector one notch per gesture, monthly to weekly to daily
/// and back, keeping the period under the pointer in place rather than recentering the axis, and a
/// left mouse button drag pans the visible date range without changing granularity. Both act on
/// the same three explicit granularities the selector already offers; the web's continuous zoom
/// value and its pinch gesture are not ported. A preset still sets a granularity too, using the
/// web's own preset-to-zoom map read back through <c>granularityForZoom</c>: All and 1Y give
/// Monthly, Q gives Weekly, M and W give Daily. The selector stays live afterwards, so the user can
/// override, and so can the wheel and the drag.
/// </para>
/// <para>
/// questions.md Q6: the efficiency division lives here rather than in <c>StatsQuery</c>. The
/// arithmetic is <c>backend/app/api/stats.py::_eff_for_dates</c>, transcribed in
/// <see cref="Efficiency"/>, over <see cref="StatsResponse.RigsPerNight"/> and
/// <c>AstroNight.DarkHoursForNight</c>. Spec 8.4's last paragraph is why an unset coordinate
/// removes the series and its axis rather than plotting zero.
/// </para>
/// <para>
/// ponytail: a monthly bar walks up to 31 dates and each date costs 144
/// <c>AstroNight.SunAltitudeDegrees</c> evaluations, so a monthly series across two years is
/// roughly 100,000 evaluations on a cold memo. <see cref="Prewarm"/> pays that once, on the page's
/// background load thread, across the whole daily span; every later rebuild (a granularity change,
/// a preset click, the imaged-only toggle) then hits <c>AstroNight</c>'s per-night memo and is
/// dictionary lookups plus arithmetic, which is why those rebuilds are allowed to run inline on
/// the UI thread.
/// </para>
/// </remarks>
public sealed partial class ImagingTimelineViewModel : ObservableObject, IDisposable
{
    /// <summary>The web's footer legend, ported verbatim.</summary>
    public const string LegendText =
        "% = exposure time / astronomical dark hours, per rig on multi-rig nights";

    /// <summary>Spec 12.5's "Imaged only" label, the left half of the toggle.</summary>
    public const string ImagedOnlyLabel = "Imaged only";

    private static readonly string[] MonthNames =
    [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];

    // FIXER LIST F8: the theme subscription and its unsubscribe as one token. The unsubscribe is
    // the half that matters: ChartTheme.Changed is static, so a handler left behind pins this
    // view-model for the life of the process.
    private readonly IDisposable _themeSubscription;

    private readonly Func<DateOnly> _today;

    private IReadOnlyList<TimelineEntry> _monthly = [];
    private IReadOnlyList<TimelineEntry> _weekly = [];
    private IReadOnlyList<TimelineEntry> _daily = [];
    private IReadOnlyDictionary<DateOnly, int> _rigsPerNight = new Dictionary<DateOnly, int>();
    private double? _latitude;
    private double? _longitude;

    private bool _suppressRebuild;
    private bool _disposed;

    /// <param name="today">The clock, as a seam, because the efficiency horizon is
    /// <c>max(today, the newest daily period)</c> and a test must be able to pin it.</param>
    public ImagingTimelineViewModel(Func<DateOnly>? today = null)
    {
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Today));

        Series = [];
        XAxes = [new Axis()];
        YAxes = [new Axis()];
        Bars = [];
        IsEmpty = true;
        GranularityOptions = OptionsFor(RangePreset);

        _themeSubscription = ChartTheme.Subscribe(OnThemeChanged);
    }

    /// <summary>Spec 13's row name for this chart.</summary>
    public string Title => "Imaging timeline";

    /// <summary>The three granularity options, in spec 12.5's order.</summary>
    internal static IReadOnlyList<GranularityOption> AllGranularities { get; } =
    [
        new(TimelineGranularity.Monthly, "Monthly"),
        new(TimelineGranularity.Weekly, "Weekly"),
        new(TimelineGranularity.Daily, "Daily"),
    ];

    /// <summary>
    /// What the granularity selector offers for the current preset.
    /// </summary>
    /// <remarks>
    /// Review finding M14, ruled as minor 18: the All preset spans the library's whole history and
    /// the port ships no continuous zoom; the wheel steps the three granularities the selector
    /// offers, so All plus Daily would emit one bar and one axis label per calendar date across
    /// that span, with no way out. Under All the selector
    /// offers Monthly and Weekly only, and choosing All while Daily is selected snaps to Weekly.
    /// Every other preset offers all three.
    /// </remarks>
    [ObservableProperty]
    public partial IReadOnlyList<GranularityOption> GranularityOptions { get; private set; }

    /// <summary>The five range presets, in spec 12.5's order, with the web's own button labels.
    /// The view renders one button per entry, each bound to
    /// <see cref="SelectPresetCommand"/>.</summary>
    public static IReadOnlyList<(TimelineRangePreset Preset, string Label)> PresetLabels { get; } =
    [
        (TimelineRangePreset.All, "All"),
        (TimelineRangePreset.OneYear, "1Y"),
        (TimelineRangePreset.Quarter, "Q"),
        (TimelineRangePreset.Month, "M"),
        (TimelineRangePreset.Week, "W"),
    ];

    /// <summary>Which of the three period lists is plotted. Defaults to Monthly, which is what
    /// the web's default preset (1Y, zoom 2) resolves to.</summary>
    [ObservableProperty]
    public partial TimelineGranularity Granularity { get; set; } = TimelineGranularity.Monthly;

    /// <summary>The window, measured back from the newest period present in the data rather than
    /// from today, so a library that stopped six months ago still shows something.</summary>
    [ObservableProperty]
    public partial TimelineRangePreset RangePreset { get; set; } = TimelineRangePreset.OneYear;

    /// <summary>False by default, matching the web's <c>gapMode = "all"</c>: periods with no
    /// frames are filled in at zero so a gap in the sky reads as a gap.</summary>
    [ObservableProperty]
    public partial bool ImagedOnly { get; set; }

    /// <summary>The right half of the imaged-only toggle. Its label follows the granularity, which
    /// the web does too.</summary>
    public string AllPeriodsLabel => Granularity switch
    {
        TimelineGranularity.Daily => "All nights",
        TimelineGranularity.Weekly => "All weeks",
        _ => "All months",
    };

    /// <summary>A signed offset, in whole periods of the current granularity, applied to the
    /// window <see cref="BuildBars"/> computes. Zero is today's behaviour exactly: the window ends
    /// at the newest period the data carries. This is the member drag pan moves
    /// (questions.md Q7); <see cref="Pan"/> is the only public way to change it, clamped so the
    /// window never runs past the first period's start or past the newest period's end.</summary>
    [ObservableProperty]
    public partial int PanPeriods { get; private set; }

    /// <summary>The bars actually plotted, after windowing and gap filling. Public because it is
    /// what the click route indexes into and what the tests assert.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TimelineBar> Bars { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ISeries> Series { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> XAxes { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ICartesianAxis> YAxes { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>The empty state for this section alone. The page's own empty state (spec 12.10)
    /// covers an empty library; this covers a window with nothing in it.</summary>
    public string EmptyMessage => "No imaging in this range.";

    /// <summary>True when the efficiency series is plotted, which is exactly "both observer
    /// coordinates are configured" (spec 8.4's last paragraph). The view hides the legend line
    /// when it is false.</summary>
    public bool HasEfficiency => _latitude is not null && _longitude is not null;

    /// <summary>Spec 12.5's bar click (questions.md Q16): the period's date range, for the
    /// Dashboard's own date filter. The page forwards it; the shell applies it and navigates.
    /// </summary>
    public event EventHandler<(DateOnly From, DateOnly To)>? PeriodSelected;

    /// <summary>The bar index <see cref="PointClicked"/> resolved but has not yet navigated to.
    /// LiveCharts raises <c>DataPointerDownCommand</c> (and so <see cref="PointClickedCommand"/>)
    /// on pointer down, verified against <c>LiveChartsCore.SkiaSharpView.Avalonia.xml</c>'s own
    /// "Gets or sets a command to execute when the pointer goes down on a data or data points",
    /// not on release, so a flag set on release (the web's <c>didDrag</c> shape) always arrives one
    /// gesture late: a drag that starts over a bar has already navigated by the time any movement
    /// is measured, and the flag then swallows the next genuine click instead. Deferring the click
    /// itself fixes the timing: nothing navigates until the gesture ends, at
    /// <see cref="CommitPendingClick"/> or <see cref="DiscardPendingClick"/>.</summary>
    private int? _pendingPeriodIndex;

    /// <summary>
    /// Replaces the plotted data and rebuilds. Called on the UI thread, because it writes the
    /// bindings; the expensive half is <see cref="PrewarmDarkHours"/>, which the page has already
    /// run on its background load thread.
    /// </summary>
    public void SetData(StatsResponse stats, GeneralSettings general)
    {
        _monthly = stats.TimelineMonthly;
        _weekly = stats.TimelineWeekly;
        _daily = stats.TimelineDaily;
        _rigsPerNight = stats.RigsPerNight;
        _latitude = general.ObserverLatitude;
        _longitude = general.ObserverLongitude;

        Rebuild();
        OnPropertyChanged(nameof(HasEfficiency));
    }

    /// <summary>
    /// Fills <c>AstroNight</c>'s per-night memo for every date in the daily span, so every later
    /// rebuild costs dictionary lookups instead of the evaluation count in this type's remarks.
    /// A no-op when observer coordinates are unset, because nothing then reads dark hours.
    /// </summary>
    /// <remarks>
    /// Static and stateless on purpose: it runs on the page's background thread, where touching an
    /// observable property would write a binding off the UI thread. It touches nothing but
    /// <c>AstroNight</c>'s own concurrent memo.
    /// </remarks>
    public static void PrewarmDarkHours(IReadOnlyList<TimelineEntry> daily, GeneralSettings general)
    {
        if (general.ObserverLatitude is not { } latitude
            || general.ObserverLongitude is not { } longitude
            || daily.Count == 0)
        {
            return;
        }

        var dates = daily.Select(entry => Bounds(entry.Period, TimelineGranularity.Daily).Start).ToList();
        for (var date = dates.Min(); date <= dates.Max(); date = date.AddDays(1))
        {
            AstroNight.DarkHoursForNight(date, latitude, longitude);
        }
    }

    /// <summary>
    /// Spec 12.5: a preset sets the range <em>and</em> the granularity, then leaves the
    /// granularity selector free to be changed, by the combo box, the wheel or a drag alike. One
    /// rebuild, not two, and it resets any pan the wheel or the drag had applied: a preset is a
    /// request for a named window, and a stale offset inside it would be meaningless.
    /// </summary>
    [RelayCommand]
    public void SelectPreset(TimelineRangePreset preset)
    {
        _suppressRebuild = true;
        try
        {
            RangePreset = preset;
            Granularity = GranularityForPreset(preset);
            PanPeriods = 0;
        }
        finally
        {
            _suppressRebuild = false;
        }

        Rebuild();
    }

    /// <summary>The web's own <c>presetToZoom</c> map read back through
    /// <c>granularityForZoom</c>: All (zoom 1) and 1Y (zoom 2) are monthly, Q (zoom 5) is weekly,
    /// M (zoom 11) and W (zoom 16) are daily.</summary>
    internal static TimelineGranularity GranularityForPreset(TimelineRangePreset preset) => preset switch
    {
        TimelineRangePreset.All => TimelineGranularity.Monthly,
        TimelineRangePreset.OneYear => TimelineGranularity.Monthly,
        TimelineRangePreset.Quarter => TimelineGranularity.Weekly,
        _ => TimelineGranularity.Daily,
    };

    /// <summary>Spec 12.5's bar click, by bar index. The chart's own pointer route goes through
    /// <see cref="PointClickedCommand"/>, which resolves a chart point to this index.</summary>
    [RelayCommand]
    public void SelectPeriod(int index)
    {
        if (index < 0 || index >= Bars.Count)
        {
            return;
        }

        var bar = Bars[index];
        PeriodSelected?.Invoke(this, (bar.Start, bar.End));
    }

    /// <summary>
    /// What <c>CartesianChart.DataPointerDownCommand</c> is bound to, and so what fires on pointer
    /// down rather than on a click or a release. It records which bar the pointer went down over
    /// and raises nothing yet: the gesture is not over, and spec 12.5's drag pan needs to be able
    /// to turn this same press into a drag instead of a navigation. Separated from
    /// <see cref="SelectPeriodCommand"/> so the rule (which period a click means) is testable
    /// without constructing a LiveCharts point.
    /// </summary>
    [RelayCommand]
    public void PointClicked(IEnumerable<ChartPoint>? points)
        => _pendingPeriodIndex = points?.FirstOrDefault() is { } point ? point.Index : null;

    /// <summary>The code-behind's release path calls this when the pointer did not travel past
    /// the drag threshold: the press <see cref="PointClicked"/> recorded was a plain click, so it
    /// navigates now, on release, which is when the web's own <c>click</c>-guarded gesture would
    /// have fired.</summary>
    public void CommitPendingClick()
    {
        if (_pendingPeriodIndex is { } index)
        {
            _pendingPeriodIndex = null;
            SelectPeriod(index);
        }
    }

    /// <summary>The code-behind's release path calls this when the pointer did travel past the
    /// drag threshold, or when its pointer capture was lost before a release ever arrived: the
    /// press was a drag, not a click, so it never navigates.</summary>
    public void DiscardPendingClick() => _pendingPeriodIndex = null;

    /// <summary>
    /// Spec 12.5's drag pan: adds <paramref name="periods"/> whole periods of the current
    /// granularity to <see cref="PanPeriods"/>, clamped so the window stays inside the data, and
    /// rebuilds once. The code-behind converts a drag's pixel delta to a period count and calls
    /// this once per gesture, on release, never once per pixel of movement.
    /// </summary>
    public void Pan(int periods)
    {
        if (ResolveWindow() is not { } window)
        {
            return;
        }

        PanPeriods = ClampPan(PanPeriods + periods, window.TrueFrom, window.DataStart);
        Rebuild();
    }

    /// <summary>
    /// Spec 12.5's wheel zoom: steps <see cref="Granularity"/> one notch per call through the
    /// currently offered <see cref="GranularityOptions"/>, never through the raw
    /// <see cref="TimelineGranularity"/> enum, because under the All preset that list is Monthly
    /// and Weekly only (questions.md Q8). Stops at either end rather than wrapping. When
    /// <paramref name="anchor"/> is given, the pan is set afterwards so the anchor's period sits at
    /// the centre of the new window, which is spec 12.5's "keeping the bar under the pointer in
    /// place".
    /// </summary>
    public void StepGranularity(int notches, DateOnly? anchor = null)
    {
        var options = GranularityOptions;
        var currentIndex = 0;
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].Value == Granularity)
            {
                currentIndex = i;
                break;
            }
        }

        var nextIndex = Math.Clamp(currentIndex + notches, 0, options.Count - 1);
        if (nextIndex == currentIndex)
        {
            return;
        }

        _suppressRebuild = true;
        try
        {
            Granularity = options[nextIndex].Value;
        }
        finally
        {
            _suppressRebuild = false;
        }

        if (anchor is { } anchorDate)
        {
            CenterOnAnchor(anchorDate);
        }
        else
        {
            Rebuild();
        }
    }

    partial void OnGranularityChanged(TimelineGranularity value)
    {
        // Spec 12.5: a granularity change rebuilds the period set, so a pan offset measured in the
        // old period unit is meaningless. Set before ClampGranularity's own re-entrant pass, so the
        // reset holds however this handler was reached.
        PanPeriods = 0;

        // A snap re-enters this handler through the property setter, and that pass does the
        // notification and the rebuild.
        if (ClampGranularity())
        {
            return;
        }

        OnPropertyChanged(nameof(AllPeriodsLabel));
        Rebuild();
    }

    partial void OnRangePresetChanged(TimelineRangePreset value)
    {
        // Clamp before the options list is replaced, so the selector is never showing a list that
        // does not contain its own selected value.
        var snapped = ClampGranularity();
        GranularityOptions = OptionsFor(value);
        if (!snapped)
        {
            Rebuild();
        }
    }

    /// <summary>Minor 18's rule: All plus Daily snaps to Weekly. Returns true when it moved the
    /// granularity, in which case the setter's own handler has already rebuilt.</summary>
    private bool ClampGranularity()
    {
        if (RangePreset != TimelineRangePreset.All || Granularity != TimelineGranularity.Daily)
        {
            return false;
        }

        Granularity = TimelineGranularity.Weekly;
        return true;
    }

    private static IReadOnlyList<GranularityOption> OptionsFor(TimelineRangePreset preset)
        => preset == TimelineRangePreset.All
            ? [.. AllGranularities.Where(option => option.Value != TimelineGranularity.Daily)]
            : AllGranularities;

    partial void OnImagedOnlyChanged(bool value) => Rebuild();

    private void OnThemeChanged() => Rebuild();

    private void Rebuild()
    {
        if (_suppressRebuild)
        {
            return;
        }

        Bars = BuildBars();
        if (Bars.Count == 0)
        {
            PublishEmpty();
            return;
        }

        var palette = ChartTheme.Palette.Metrics;
        var integration = new ColumnSeries<double?>
        {
            Name = "Integration",
            Values = [.. Bars.Select(bar => (double?)bar.IntegrationHours)],
            Fill = new SolidColorPaint(Colour(palette, "ColorMetricIntegration")),
            Stroke = null,
            ScalesYAt = 0,
        };

        var hours = new Axis
        {
            Labeler = value => FormatHours(value * 3600d),
            MinLimit = 0,
        };

        var categorical = new Axis
        {
            Labels = [.. Bars.Select(bar => bar.Label)],
            MinStep = 1,
            ForceStepToMin = true,
        };

        if (!HasEfficiency)
        {
            // Spec 8.4: "The statistics page hides the efficiency series rather than showing
            // zero." No series, no secondary axis, no labels.
            Series = [integration];
            XAxes = [categorical];
            YAxes = [hours];
            IsEmpty = false;
            return;
        }

        var efficiency = new LineSeries<double?>
        {
            Name = "Efficiency",
            Values = [.. Bars.Select(bar => bar.EfficiencyPercent)],
            Stroke = new SolidColorPaint(Colour(palette, "ColorMetricTime")) { StrokeThickness = 2 },
            Fill = null,
            GeometrySize = 6,
            GeometryStroke = new SolidColorPaint(Colour(palette, "ColorMetricTime")) { StrokeThickness = 2 },
            GeometryFill = new SolidColorPaint(Colour(palette, "ColorMetricTime")),
            LineSmoothness = 0,
            // A period with no computable efficiency leaves a gap rather than joining its two
            // neighbours across it, the same rule spec 13 gives the Target detail charts.
            EnableNullSplitting = true,
            ScalesYAt = 1,
        };

        Series = [integration, efficiency];
        XAxes = [categorical];
        YAxes =
        [
            hours,
            new Axis
            {
                Position = AxisPosition.End,
                MinLimit = 0,
                MaxLimit = 100,
                Labeler = value => value.ToString("0", CultureInfo.InvariantCulture) + "%",
            },
        ];
        IsEmpty = false;
    }

    // FIXER LIST item 10: one bare axis per side, never none.
    private void PublishEmpty()
    {
        Series = [];
        XAxes = [new Axis()];
        YAxes = [new Axis()];
        IsEmpty = true;
    }

    private IReadOnlyList<TimelineBar> BuildBars()
    {
        if (ResolveWindow() is not { } window)
        {
            return [];
        }

        var seconds = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in window.Measured)
        {
            seconds[item.Entry.Period] = item.Entry.IntegrationSeconds;
        }

        var pan = ClampPan(PanPeriods, window.TrueFrom, window.DataStart);
        var windowStart = OffsetByPeriods(window.TrueFrom, pan);
        var windowEnd = OffsetByPeriods(window.DataEnd, pan);

        var horizon = Horizon();
        var bars = new List<TimelineBar>();
        var cursor = PeriodContaining(windowStart);
        while (cursor.Start <= windowEnd)
        {
            var period = Key(cursor.Start);
            var integration = seconds.GetValueOrDefault(period);

            // ImagedOnly is the web's gapMode = "imaged": only periods that actually carry
            // integration. In the default "all" mode every period in the window is emitted, the
            // absent ones at zero.
            if (!ImagedOnly || integration > 0)
            {
                bars.Add(new TimelineBar(
                    period,
                    FormatLabel(period, Granularity),
                    cursor.Start,
                    cursor.End,
                    integration,
                    Efficiency(cursor.Start, cursor.End, integration, horizon)));
            }

            cursor = Bounds(Key(Next(cursor.Start)), Granularity);
        }

        return bars;
    }

    /// <summary>Spec 12.5's drag pan and wheel zoom (questions.md Q7, Q8) both need the same three
    /// facts about the window <see cref="BuildBars"/> would build at <see cref="PanPeriods"/> zero:
    /// where the data starts, where it ends, and where the unpanned window's own start falls once
    /// it is clamped to the data and aligned to a period boundary. Null when the current
    /// granularity's list is empty.</summary>
    private readonly record struct PeriodWindow(
        DateOnly DataStart,
        DateOnly DataEnd,
        DateOnly TrueFrom,
        List<(TimelineEntry Entry, (DateOnly Start, DateOnly End) Range)> Measured);

    private PeriodWindow? ResolveWindow()
    {
        var measured = Measured();
        if (measured is null)
        {
            return null;
        }

        var dataStart = measured[0].Range.Start;
        var dataEnd = measured[^1].Range.End;
        var rawFrom = RawWindowFrom(dataEnd);
        var trueFrom = PeriodContaining(rawFrom < dataStart ? dataStart : rawFrom).Start;
        return new PeriodWindow(dataStart, dataEnd, trueFrom, measured);
    }

    private List<(TimelineEntry Entry, (DateOnly Start, DateOnly End) Range)>? Measured()
    {
        var source = Granularity switch
        {
            TimelineGranularity.Weekly => _weekly,
            TimelineGranularity.Daily => _daily,
            _ => _monthly,
        };

        if (source.Count == 0)
        {
            return null;
        }

        return source
            .Select(entry => (Entry: entry, Range: Bounds(entry.Period, Granularity)))
            .OrderBy(item => item.Range.Start)
            .ToList();
    }

    // The web expresses these only as zoom levels. Stated here as real windows, measured back from
    // the newest period in the data: 1Y is the last 12 months, Q the last 3, M the last month, W
    // the last 7 days. A period that straddles the boundary is kept, because dropping it would
    // leave a half-window with a missing edge. All returns a date before any real data, so the
    // caller's own clamp to dataStart is what actually places it.
    private DateOnly RawWindowFrom(DateOnly newestEnd) => RangePreset switch
    {
        TimelineRangePreset.All => DateOnly.MinValue,
        TimelineRangePreset.OneYear => newestEnd.AddYears(-1).AddDays(1),
        TimelineRangePreset.Quarter => newestEnd.AddMonths(-3).AddDays(1),
        TimelineRangePreset.Month => newestEnd.AddMonths(-1).AddDays(1),
        _ => newestEnd.AddDays(-6),
    };

    /// <summary>Pan is clamped so the window never runs past the first period's start (moving
    /// <paramref name="trueFrom"/> below <paramref name="dataStart"/>) or past the newest period's
    /// end (the window's upper edge already sits there at zero, so panning forward is never
    /// valid).</summary>
    private int ClampPan(int requested, DateOnly trueFrom, DateOnly dataStart)
        => Math.Clamp(requested, PeriodsBetween(trueFrom, dataStart), 0);

    /// <summary>The whole number of periods, at the current granularity, from <paramref
    /// name="from"/> to <paramref name="to"/>. Both dates are expected to already sit on a period
    /// boundary, which every caller here guarantees.</summary>
    private int PeriodsBetween(DateOnly from, DateOnly to) => Granularity switch
    {
        TimelineGranularity.Weekly => (to.DayNumber - from.DayNumber) / 7,
        TimelineGranularity.Daily => to.DayNumber - from.DayNumber,
        _ => ((to.Year - from.Year) * 12) + (to.Month - from.Month),
    };

    private DateOnly OffsetByPeriods(DateOnly date, int periods) => Granularity switch
    {
        TimelineGranularity.Weekly => date.AddDays(7 * periods),
        TimelineGranularity.Daily => date.AddDays(periods),
        _ => date.AddMonths(periods),
    };

    /// <summary>Spec 12.5's "keeping the bar under the pointer in place": sets
    /// <see cref="PanPeriods"/> so the anchor's own period sits at the centre of the window the new
    /// granularity would otherwise show unpanned, clamped exactly as <see cref="Pan"/> clamps a
    /// drag.</summary>
    private void CenterOnAnchor(DateOnly anchor)
    {
        if (ResolveWindow() is not { } window)
        {
            PanPeriods = 0;
            Rebuild();
            return;
        }

        var anchorStart = PeriodContaining(anchor).Start;
        var windowLength = PeriodsBetween(window.TrueFrom, window.DataEnd);
        var desiredStart = OffsetByPeriods(anchorStart, -(windowLength / 2));
        var requestedPan = PeriodsBetween(window.TrueFrom, desiredStart);

        PanPeriods = ClampPan(requestedPan, window.TrueFrom, window.DataStart);
        Rebuild();
    }

    // eff_horizon = max(date.today(), the newest daily period). Computed from the daily list
    // rather than from the plotted granularity, exactly as the Python does.
    private DateOnly Horizon()
    {
        var today = _today();
        if (_daily.Count == 0)
        {
            return today;
        }

        var newest = _daily.Max(entry => Bounds(entry.Period, TimelineGranularity.Daily).Start);
        return newest > today ? newest : today;
    }

    /// <summary>
    /// <c>backend/app/api/stats.py::_eff_for_dates</c>, transcribed. The period's dates past the
    /// horizon are dropped; the rest contribute their dark hours times their rig count, floored at
    /// one rig even for a date with no frames; the result is the integration hours over that
    /// total, as a percentage, to one decimal.
    /// </summary>
    /// <remarks>
    /// The Python guards <c>any(d not in dark_map)</c> against a precomputed <c>site_dark_hours</c>
    /// table this port does not have (spec 5.14 lists it as deliberately absent). Here the value
    /// is computed on demand, so the guard becomes "observer coordinates are configured": when
    /// they are, every date has a value; when they are not, there is no series at all.
    /// </remarks>
    internal double? Efficiency(DateOnly start, DateOnly end, double integrationSeconds, DateOnly horizon)
    {
        if (_latitude is not { } latitude || _longitude is not { } longitude)
        {
            return null;
        }

        var totalDark = 0d;
        var counted = 0;
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            if (date > horizon)
            {
                continue;
            }

            counted++;
            var rigs = Math.Max(1, _rigsPerNight.GetValueOrDefault(date));
            totalDark += AstroNight.DarkHoursForNight(date, latitude, longitude) * rigs;
        }

        if (counted == 0 || totalDark <= 0)
        {
            return null;
        }

        return Math.Round(integrationSeconds / 3600d / totalDark * 100d, 1);
    }

    /// <summary>
    /// <c>ImagingTimeline.tsx::formatLabel</c>: <c>"2025-05"</c> is <c>"May 2025"</c>,
    /// <c>"2025-W03"</c> is <c>"W3 '25"</c>, <c>"2025-12-03"</c> is <c>"Dec 3"</c>.
    /// </summary>
    internal static string FormatLabel(string period, TimelineGranularity granularity)
    {
        switch (granularity)
        {
            case TimelineGranularity.Weekly:
            {
                var (year, week) = ParseWeek(period);
                return string.Create(CultureInfo.InvariantCulture, $"W{week} '{year % 100:00}");
            }

            case TimelineGranularity.Daily:
            {
                var date = DateOnly.ParseExact(period, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                return string.Create(CultureInfo.InvariantCulture, $"{MonthNames[date.Month - 1]} {date.Day}");
            }

            default:
            {
                var year = int.Parse(period[..4], CultureInfo.InvariantCulture);
                var month = int.Parse(period.Substring(5, 2), CultureInfo.InvariantCulture);
                return string.Create(CultureInfo.InvariantCulture, $"{MonthNames[month - 1]} {year}");
            }
        }
    }

    /// <summary><c>ImagingTimeline.tsx::formatHours</c>: one decimal below ten hours, rounded to
    /// a whole number at ten and above.</summary>
    internal static string FormatHours(double seconds)
    {
        var hours = seconds / 3600d;
        return hours < 10d
            ? string.Create(CultureInfo.InvariantCulture, $"{hours:0.0}h")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(hours, MidpointRounding.AwayFromZero):0}h");
    }

    /// <summary>
    /// <c>ImagingTimeline.tsx::periodToDateRange</c>: a month is its first to its last day, an ISO
    /// week is its Monday to its Sunday, a day is itself at both ends.
    /// </summary>
    /// <remarks>
    /// The ISO week arithmetic goes through <see cref="ISOWeek"/> rather than through Task 1's
    /// <c>AstroNight.IsoWeekMonday</c>, which is <c>internal</c> to <c>GalactiLog.Core</c> and so
    /// not visible from this assembly. It is the same rule from the same direction:
    /// <c>StatsQuery.Weekly</c> already builds the period key with <see cref="ISOWeek"/>, and
    /// Task 1's own test asserts <c>IsoWeekMonday</c> and <c>ISOWeek.ToDateTime</c> agree across
    /// several years.
    /// </remarks>
    internal static (DateOnly Start, DateOnly End) Bounds(string period, TimelineGranularity granularity)
    {
        switch (granularity)
        {
            case TimelineGranularity.Weekly:
            {
                var (year, week) = ParseWeek(period);
                var monday = DateOnly.FromDateTime(ISOWeek.ToDateTime(year, week, DayOfWeek.Monday));
                return (monday, monday.AddDays(6));
            }

            case TimelineGranularity.Daily:
            {
                var date = DateOnly.ParseExact(period, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                return (date, date);
            }

            default:
            {
                var year = int.Parse(period[..4], CultureInfo.InvariantCulture);
                var month = int.Parse(period.Substring(5, 2), CultureInfo.InvariantCulture);
                var first = new DateOnly(year, month, 1);
                return (first, first.AddMonths(1).AddDays(-1));
            }
        }
    }

    private static (int Year, int Week) ParseWeek(string period)
    {
        var separator = period.IndexOf('W', StringComparison.Ordinal);
        var year = int.Parse(period[..(separator - 1)], CultureInfo.InvariantCulture);
        var week = int.Parse(period[(separator + 1)..], CultureInfo.InvariantCulture);
        return (year, week);
    }

    // The period key a date falls in, at the current granularity. Together with Next() below this
    // is the gap filler: every month, every ISO week, or every date between the bounds.
    private (DateOnly Start, DateOnly End) PeriodContaining(DateOnly date) => Granularity switch
    {
        TimelineGranularity.Weekly => (
            date.AddDays(-((int)date.DayOfWeek + 6) % 7),
            date.AddDays(-((int)date.DayOfWeek + 6) % 7).AddDays(6)),
        TimelineGranularity.Daily => (date, date),
        _ => (new DateOnly(date.Year, date.Month, 1), new DateOnly(date.Year, date.Month, 1).AddMonths(1).AddDays(-1)),
    };

    private DateOnly Next(DateOnly start) => Granularity switch
    {
        TimelineGranularity.Weekly => start.AddDays(7),
        TimelineGranularity.Daily => start.AddDays(1),
        _ => start.AddMonths(1),
    };

    private string Key(DateOnly start) => Granularity switch
    {
        TimelineGranularity.Weekly => string.Create(
            CultureInfo.InvariantCulture,
            $"{ISOWeek.GetYear(start.ToDateTime(TimeOnly.MinValue)):D4}-W{ISOWeek.GetWeekOfYear(start.ToDateTime(TimeOnly.MinValue)):D2}"),
        TimelineGranularity.Daily => start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => start.ToString("yyyy-MM", CultureInfo.InvariantCulture),
    };

    private static SKColor Colour(IReadOnlyDictionary<string, SKColor> palette, string key)
        => palette.TryGetValue(key, out var colour) ? colour : ChartTheme.Fallback;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _themeSubscription.Dispose();
    }
}
