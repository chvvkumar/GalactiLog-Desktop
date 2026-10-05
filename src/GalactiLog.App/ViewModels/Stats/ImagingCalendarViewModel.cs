using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Services;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Sessions;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Stats;

/// <summary>One range the calendar can show: the rolling year, or one calendar year.</summary>
/// <param name="Year">Null for the rolling "Last 12 months" option.</param>
public sealed record CalendarRangeOption(int? Year, string Label);

/// <summary>One day cell of the heatmap.</summary>
/// <param name="Column">Its week, counted from the first Monday of the range.</param>
/// <param name="Row">0 for Monday through 6 for Sunday.</param>
/// <param name="Intensity">The five-step ramp index, 0 through 4.</param>
/// <param name="Tooltip">Empty for a day with no integration, which is the web's rule: a blank
/// cell explains itself.</param>
public sealed record CalendarCell(
    DateOnly Date,
    int Column,
    int Row,
    int Intensity,
    IImmutableSolidColorBrush Brush,
    string Tooltip);

/// <summary>A month name above the first week whose Monday falls in that month.</summary>
public sealed record CalendarMonthLabel(int Column, string Text);

/// <summary>One swatch of the "Less ... More" legend. A record rather than a bare brush so the
/// view's item template can declare an <c>x:DataType</c>.</summary>
public sealed record CalendarLegendSwatch(IImmutableSolidColorBrush Brush);

/// <summary>
/// Spec 12.5's Imaging calendar: a GitHub-style year heatmap of integration per night, drawn by
/// the custom <c>CalendarHeatmap</c> control rather than by LiveCharts (spec 13's row says so).
/// </summary>
/// <remarks>
/// <para>
/// The layout is <c>ImagingCalendar.tsx</c>'s: columns are weeks, rows are Monday through Sunday,
/// the range start snaps back to Monday, whole seven-day weeks are always emitted so the last
/// column may overrun the end, month labels appear at the first week whose Monday changes month,
/// and day labels render on rows 0, 2, 4 and 6.
/// </para>
/// <para>
/// The five ramp thresholds are the web's, in hours: at or below zero is step 0, below 1 is step
/// 1, below 3 is step 2, below 6 is step 3, above that is step 4. The five colours are not the
/// web's hard-coded GitHub palette: spec 13 says "a five-step ramp between <c>bg-elevated</c> and
/// <c>metric-integration</c>", so the ramp is interpolated between those two tokens and step 0 is
/// the plain elevated background.
/// </para>
/// <para>
/// Spec 12.5 and the roadmap's Verify line add dark hours to the web's three-part tooltip, so a
/// cell carries five things: date, integration, frame count, target count and dark hours. Dark
/// hours are omitted, not zeroed, when observer coordinates are unset (spec 8.4).
/// </para>
/// </remarks>
public sealed partial class ImagingCalendarViewModel : ObservableObject, IDisposable
{
    /// <summary>Monday first, matching the grid's row order.</summary>
    public static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <summary>The rows that carry a day label, from the web's <c>i % 2 === 0</c>.</summary>
    public static readonly int[] LabelledDayRows = [0, 2, 4, 6];

    /// <summary>How many calendar years are offered beside the rolling option.</summary>
    internal const int YearOptionCount = 6;

    // FIXER LIST F8: the theme subscription and its unsubscribe as one token. The unsubscribe is
    // the half that matters: ChartTheme.Changed is static, so a handler left behind pins this
    // view-model for the life of the process.
    private readonly IDisposable _themeSubscription;

    private readonly Func<DateOnly, DateOnly, IReadOnlyList<CalendarEntry>> _loadCalendar;
    private readonly Func<GeneralSettings> _general;
    private readonly Func<DateOnly> _today;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    private IReadOnlyList<IImmutableSolidColorBrush> _ramp;
    private int _generation;
    private bool _disposed;

    public ImagingCalendarViewModel(
        Func<DateOnly, DateOnly, IReadOnlyList<CalendarEntry>> loadCalendar,
        Func<GeneralSettings> general,
        Func<DateOnly>? today = null,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        _loadCalendar = loadCalendar;
        _general = general;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Today));
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;
        _ramp = BuildRamp();

        var year = _today().Year;
        RangeOptions =
        [
            new CalendarRangeOption(null, "Last 12 months"),
            .. Enumerable.Range(0, YearOptionCount).Select(offset =>
                new CalendarRangeOption(year - offset, (year - offset).ToString(CultureInfo.InvariantCulture))),
        ];
        _selectedRange = RangeOptions[0];

        Cells = [];
        MonthLabels = [];

        _themeSubscription = ChartTheme.Subscribe(OnThemeChanged);
    }

    /// <summary>Spec 13's row name for this chart.</summary>
    public string Title => "Imaging calendar";

    /// <summary>"Last 12 months" followed by the current year and the five before it.</summary>
    public IReadOnlyList<CalendarRangeOption> RangeOptions { get; }

    private CalendarRangeOption _selectedRange;

    /// <summary>The range showing. Changing it reloads off the UI thread.</summary>
    public CalendarRangeOption SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedRange))
            {
                return;
            }

            _selectedRange = value;
            OnPropertyChanged();
            Reload();
        }
    }

    [ObservableProperty]
    public partial IReadOnlyList<CalendarCell> Cells { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<CalendarMonthLabel> MonthLabels { get; private set; }

    /// <summary>How many week columns the grid holds.</summary>
    [ObservableProperty]
    public partial int WeekCount { get; private set; }

    /// <summary>The five legend swatches, in ramp order, between the "Less" and "More" captions.
    /// </summary>
    public IReadOnlyList<CalendarLegendSwatch> Legend
        => [.. _ramp.Select(brush => new CalendarLegendSwatch(brush))];

    /// <summary>The five ramp brushes, step 0 through step 4. Internal because it is the rule a
    /// test asserts, while <see cref="Legend"/> is what the view binds.</summary>
    internal IReadOnlyList<IImmutableSolidColorBrush> Ramp => _ramp;

    /// <summary>The in-flight load, so a test awaits it instead of sleeping.</summary>
    internal Task? PendingLoad { get; private set; }

    /// <summary>Starts a load for the current range. The page calls this once per stats load and
    /// the range selector calls it on every change.</summary>
    public void Reload()
    {
        if (_disposed)
        {
            return;
        }

        var generation = ++_generation;
        var (from, to) = Range();
        PendingLoad = Task.Run(() =>
        {
            try
            {
                var entries = _loadCalendar(from, to);
                var general = _general();

                // Built off the UI thread, brushes included: every one of them is an
                // ImmutableSolidColorBrush, which has no dispatcher affinity (spec 14.5). The ramp
                // itself was resolved on the UI thread, in the constructor or on a theme change,
                // because that is the part that reads the merged resource dictionary.
                var built = Build(entries, general, from, to, _ramp);
                _post(() =>
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    Cells = built.Cells;
                    MonthLabels = built.MonthLabels;
                    WeekCount = built.WeekCount;
                });
            }
            catch (Exception ex)
            {
                // The calendar keeps whatever it already showed; a failed range read must not take
                // the page down. The page's own failure line covers the stats read.
                _logger.LogWarning(ex, "The statistics calendar query failed");
            }
        });
    }

    /// <summary>
    /// The range's bounds: 1 January to 31 December for a chosen year, otherwise today back one
    /// year plus a day, which is the web's rolling twelve months.
    /// </summary>
    internal (DateOnly From, DateOnly To) Range()
    {
        if (SelectedRange.Year is { } year)
        {
            return (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
        }

        var today = _today();
        return (today.AddYears(-1).AddDays(1), today);
    }

    /// <summary>The web's <c>intensity(hours)</c>, as a ramp index.</summary>
    internal static int Intensity(double hours) => hours switch
    {
        <= 0d => 0,
        < 1d => 1,
        < 3d => 2,
        < 6d => 3,
        _ => 4,
    };

    /// <summary>
    /// The five-part tooltip: date, integration, frame count, target count and dark hours. Empty
    /// for a cell with no integration, matching the web, and silent about darkness when observer
    /// coordinates are unset rather than showing a zero.
    /// </summary>
    internal static string Tooltip(DateOnly date, double integrationSeconds, int frames, int targets, double? darkHours)
    {
        if (integrationSeconds <= 0d)
        {
            return "";
        }

        var hours = integrationSeconds / 3600d;
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{MetricText.Date(date)}: {hours:0.0}h, {targets} target{(targets == 1 ? "" : "s")}, {frames} frame{(frames == 1 ? "" : "s")}");

        return darkHours is { } dark
            ? text + string.Create(CultureInfo.InvariantCulture, $", {dark:0.0}h dark")
            : text;
    }

    private static (IReadOnlyList<CalendarCell> Cells, IReadOnlyList<CalendarMonthLabel> MonthLabels, int WeekCount)
        Build(
            IReadOnlyList<CalendarEntry> entries,
            GeneralSettings general,
            DateOnly from,
            DateOnly to,
            IReadOnlyList<IImmutableSolidColorBrush> ramp)
    {
        var byDate = entries.ToDictionary(entry => entry.Date);
        var latitude = general.ObserverLatitude;
        var longitude = general.ObserverLongitude;

        // mondayOffset: the web snaps the start back to the Monday of its own week.
        var start = from.AddDays(-((int)from.DayOfWeek + 6) % 7);

        var cells = new List<CalendarCell>();
        var labels = new List<CalendarMonthLabel>();
        var column = 0;
        var lastMonth = -1;
        for (var monday = start; monday <= to; monday = monday.AddDays(7), column++)
        {
            if (monday.Month != lastMonth)
            {
                labels.Add(new CalendarMonthLabel(
                    column,
                    CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(monday.Month)));
                lastMonth = monday.Month;
            }

            // Whole weeks always, so the last column can overrun the end. That is the web's loop
            // and it is what keeps every column seven cells tall.
            for (var row = 0; row < 7; row++)
            {
                var date = monday.AddDays(row);
                var entry = byDate.GetValueOrDefault(date);
                var seconds = entry?.IntegrationSeconds ?? 0d;
                var intensity = Intensity(seconds / 3600d);

                // Review finding M7: only an imaged night has a tooltip at all, so only an imaged
                // night needs a dark-hours figure. Computing it for every cell was up to 371
                // AstroNight calls per reload, on every stats load, range change and theme change,
                // for a value that was then discarded.
                var dark = seconds > 0d && latitude is { } lat && longitude is { } lon
                    ? AstroNight.DarkHoursForNight(date, lat, lon)
                    : (double?)null;

                cells.Add(new CalendarCell(
                    date,
                    column,
                    row,
                    intensity,
                    ramp[intensity],
                    Tooltip(date, seconds, entry?.FrameCount ?? 0, entry?.TargetCount ?? 0, dark)));
            }
        }

        return (cells, labels, column);
    }

    // Spec 13: five steps between bg-elevated and metric-integration, step 0 being the plain
    // elevated background. Read through ChartTheme so the fallback for a missing key lives in one
    // place; called from the constructor and from ChartTheme.Changed, both on the UI thread.
    private static IReadOnlyList<IImmutableSolidColorBrush> BuildRamp()
    {
        var low = ChartTheme.Read("ColorBgElevated", ChartTheme.Fallback);
        var high = ChartTheme.Read("ColorMetricIntegration", ChartTheme.Fallback);

        return
        [
            .. Enumerable.Range(0, 5).Select(step => (IImmutableSolidColorBrush)new ImmutableSolidColorBrush(
                Color.FromArgb(
                    Mix(low.Alpha, high.Alpha, step),
                    Mix(low.Red, high.Red, step),
                    Mix(low.Green, high.Green, step),
                    Mix(low.Blue, high.Blue, step)))),
        ];
    }

    // Linear interpolation across the four gaps of a five-stop ramp, so step 0 is exactly the low
    // token and step 4 exactly the high one.
    private static byte Mix(byte low, byte high, int step)
        => (byte)Math.Round(low + ((high - low) * (step / 4d)), MidpointRounding.AwayFromZero);

    private void OnThemeChanged()
    {
        _ramp = BuildRamp();
        OnPropertyChanged(nameof(Legend));

        // The cells carry their brush, so a theme swap has to rebuild them. Cheap: the range is
        // already loaded and the query is a single indexed read.
        Reload();
    }

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
