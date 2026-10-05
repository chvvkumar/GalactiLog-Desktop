using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Stats;

/// <summary>One filter of spec 12.5's Filter usage row: its name, its configured tint and its
/// integration, in the order the chart plots them.</summary>
public sealed record FilterUsageRow(string FilterName, IImmutableSolidColorBrush Tint, string Integration);

/// <summary>One entry of spec 12.5's Top targets row.</summary>
public sealed record TopTargetRow(int Rank, string Name, string Integration);

/// <summary>
/// Spec 12.5's Statistics page: nine sections over one cached stats response plus a separately
/// ranged calendar query.
/// </summary>
/// <remarks>
/// <para>
/// Delegates, not query objects, exactly as every other page view-model in this application takes
/// them (<c>DashboardViewModel</c>, <c>TargetDetailViewModel</c>, <c>UnresolvedNamesViewModel</c>),
/// so the page constructs in a unit test with lambdas and no database (spec 18.3).
/// </para>
/// <para>
/// The stats read is a full-library pass (Task 2's recorded ceiling), so it never runs on the UI
/// thread: <see cref="Refresh"/> reads it inside a <see cref="Task.Run(Action)"/> along with the
/// timeline's dark-hours prewarm, and only the projection runs in the post callback.
/// <c>ScanStatusService.ScanFinished</c> is the only refresh trigger; there is no direct
/// <c>ScanCoordinator</c> subscription and no timer.
/// </para>
/// <para>
/// <see cref="IsLoading"/>, <see cref="LoadFailed"/> and <see cref="IsEmpty"/> are three separate
/// states. A failed read taught nothing about whether the library is empty, so it renders a
/// failure line in place of the sections rather than spec 12.10's empty state.
/// </para>
/// </remarks>
public sealed partial class StatisticsViewModel : ObservableObject, IDisposable
{
    /// <summary>Spec 12.10's Statistics empty state, verbatim.</summary>
    public const string EmptyStateText = "No data yet. Run a scan.";

    /// <summary>Shown in place of the sections when the stats read threw.</summary>
    public const string FailureText = "Statistics could not be loaded.";

    /// <summary>The web's <c>TopTargets.tsx</c> slice.</summary>
    internal const int TopTargetLimit = 10;

    // FIXER LIST F8: the theme subscription and its unsubscribe as one token. The unsubscribe is
    // the half that matters: ChartTheme.Changed is static, so a handler left behind pins this
    // view-model for the life of the process.
    private readonly IDisposable _themeSubscription;

    private readonly Func<StatsResponse> _loadStats;
    private readonly Func<GeneralSettings> _general;
    private readonly Func<AliasMap> _aliases;
    private readonly Action? _invalidate;
    private readonly ScanStatusService? _scanStatus;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    // One host-lifetime source every background window is linked to, the shape
    // DashboardViewModel's F6 fix established: this page is a DI singleton, so without it a load
    // started just before shutdown keeps a query running after the SQLite files are gone.
    private readonly CancellationTokenSource _lifetime = new();

    // Resolved on the constructing thread, which is the UI thread, and re-resolved only from
    // ChartTheme.Changed, which is raised on it too. Never from the load's post callback: a test
    // runs that callback inline on the loading thread, and reading a token goes through
    // SolidColorBrush.Color, whose getter calls Dispatcher.VerifyAccess and throws off the UI
    // thread. The same hazard ChartTheme's own remarks record.
    private BandBrushes _bands;

    // The altitude arc's four wedge shades, resolved under exactly the rule above: the guiding
    // cards are built inside the load's post callback, which a test runs inline on the loading
    // thread, so a ramp built there would read a token off the UI thread and throw.
    private ArcBrushes _arcs;

    // The response the sections currently show, kept so a theme change can re-project the graded
    // cells without a second read of a full-library aggregate.
    private StatsResponse? _stats;

    private int _generation;
    private bool _disposed;

    // Phase 15B fixer F2. The unsubscribe half of the derived-data follow, null when the page was
    // built without one. The half that matters: the notifier is a process singleton and this page
    // is a DI singleton, so a handler left behind would outlive a host teardown.
    private readonly Action? _unfollowDerivedData;

    // The storm rule (F2's hazard). GeneralChanged fires on EVERY general save, which includes
    // each committed keystroke in the PHD2 profiles panel, so a notification arriving while a
    // load is in flight is remembered rather than started: a bool and not a count, so a burst of
    // twenty saves costs exactly one further read. Written and read on the UI thread only.
    private bool _reloadPending;

    /// <param name="loadStats">Normally <c>StatsCache.Current</c> (questions.md Q10 and Q12), so
    /// the full-library pass is memoized and a navigation back to this page costs nothing.</param>
    /// <param name="loadCalendar">Normally <c>StatsQuery.Calendar</c>. Separate from the response
    /// because the calendar range is user-controlled (spec 12.5's Query paragraph).</param>
    /// <param name="general">Observer coordinates (spec 5.8.1), read per load rather than by
    /// value: the Location settings tab can set them while this page is alive, and the next scan
    /// or refresh must pick them up.</param>
    /// <param name="aliases">Normally <c>() =&gt; aliasMapCache.Current</c>. The filter tints come
    /// from <c>AliasMap.FilterColor</c>, which is the application's one filter-colour resolver
    /// (spec 5.8.4's <c>#808080</c> fallback lives inside it).</param>
    /// <param name="invalidate">Normally <c>StatsCache.Invalidate</c>. What makes the Refresh
    /// button mean something: the cache has no TTL (questions.md Q7), so without it the command
    /// re-projects the memoized response forever. A delegate, not the cache, for the reason every
    /// other seam here is one.</param>
    /// <param name="scanStatus">The only refresh trigger. Already on the UI thread when a
    /// subscriber sees it, so its handler does not post again.</param>
    /// <param name="post">The dispatcher seam every view-model takes.</param>
    /// <param name="today">The clock, as a seam, for the timeline's efficiency horizon and the
    /// calendar's rolling range.</param>
    /// <param name="subscribeDerivedDataChanged">Phase 15B fixer F2, with items 30 and 38.
    /// Normally <c>handler =&gt; notifier.Changed += handler</c> over the one process-level
    /// notification <c>AppHost</c> raises after it has dropped the derived memos, which is what a
    /// settings save and a completed correlation re-run both route through. Without it this page,
    /// a DI singleton, kept the response a reader left it holding: the reader mapped a PHD2
    /// profile, came back, and read the same empty guiding notice that sent them to Settings. The
    /// two sides arrive as delegates rather than the notifier itself, like every other seam here
    /// (spec 18.3), and trailing and optional so no existing construction site changes.</param>
    /// <param name="unsubscribeDerivedDataChanged">Its pair, called from <see cref="Dispose"/>.
    /// </param>
    public StatisticsViewModel(
        Func<StatsResponse> loadStats,
        Func<DateOnly, DateOnly, IReadOnlyList<CalendarEntry>> loadCalendar,
        Func<GeneralSettings> general,
        Func<AliasMap> aliases,
        Action? invalidate = null,
        ScanStatusService? scanStatus = null,
        Action<Action>? post = null,
        Func<DateOnly>? today = null,
        ILogger? logger = null,
        Action<EventHandler>? subscribeDerivedDataChanged = null,
        Action<EventHandler>? unsubscribeDerivedDataChanged = null)
    {
        _loadStats = loadStats;
        _invalidate = invalidate;
        _general = general;
        _aliases = aliases;
        _scanStatus = scanStatus;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;
        _bands = new BandBrushes();
        _arcs = new ArcBrushes();

        Timeline = new ImagingTimelineViewModel(today);
        Timeline.PeriodSelected += OnPeriodSelected;
        Calendar = new ImagingCalendarViewModel(loadCalendar, general, today, _post, _logger);
        Storage = new StorageViewModel();

        // Spec 13's six bar and column rows for this screen, all six built from the one spine.
        FilterUsageChart = new StatsBarChartViewModel(
            "Filter usage",
            StatsChartOrientation.Bar,
            "No filters recorded.",
            ImagingTimelineViewModel.FormatHours);
        TopTargetsChart = new StatsBarChartViewModel(
            "Top targets",
            StatsChartOrientation.Bar,
            "No targets recorded.",
            ImagingTimelineViewModel.FormatHours);
        HfrPixelChart = new StatsBarChartViewModel(
            "HFR distribution (pixels)",
            StatsChartOrientation.Column,
            "No frame carries an HFR.");
        HfrArcsecChart = new StatsBarChartViewModel(
            "HFR distribution (arcseconds)",
            StatsChartOrientation.Column,
            "No frame has a derivable plate scale.");
        EquipmentComparisonChart = new StatsBarChartViewModel(
            "Equipment performance comparison",
            StatsChartOrientation.Column,
            "No equipment data available");
        IngestHistoryChart = new StatsBarChartViewModel(
            "Ingest history",
            StatsChartOrientation.Column,
            "No scans recorded.");

        if (scanStatus is not null)
        {
            // Already marshalled onto the UI thread by ScanStatusService; do not post again.
            scanStatus.ScanFinished += OnScanFinished;
        }

        // Spec 13's "re-read on theme change" for the one thing on this page that is a theme
        // colour but not a chart paint: the four grading band colours.
        _themeSubscription = ChartTheme.Subscribe(OnThemeChanged);

        // F2. Subscribed before the first load rather than after it: a save landing in that
        // window is exactly the one a reader would otherwise have to refresh by hand.
        if (subscribeDerivedDataChanged is not null && unsubscribeDerivedDataChanged is not null)
        {
            subscribeDerivedDataChanged(OnDerivedDataChanged);
            _unfollowDerivedData = () => unsubscribeDerivedDataChanged(OnDerivedDataChanged);
        }

        RequestLoad();
    }

    /// <summary>Spec 12.5's Overview tiles row.</summary>
    [ObservableProperty]
    public partial OverviewTilesViewModel Overview { get; private set; } = new();

    /// <summary>Spec 12.5's Equipment performance row.</summary>
    [ObservableProperty]
    public partial EquipmentPerformanceViewModel Performance { get; private set; } = new();

    /// <summary>Spec 12.5's Guiding row: the per-rig scorecard and the RMS-by-altitude card.
    /// </summary>
    [ObservableProperty]
    public partial GuidingViewModel Guiding { get; private set; } = new();

    /// <summary>Spec 12.5's Equipment inventory row.</summary>
    [ObservableProperty]
    public partial EquipmentInventoryViewModel Inventory { get; private set; } = new();

    /// <summary>Spec 12.5's Data quality row, without its two histograms, which are charts.
    /// </summary>
    [ObservableProperty]
    public partial DataQualityViewModel DataQuality { get; private set; } = new();

    /// <summary>Spec 12.5's Filter usage row, ordered by integration descending.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<FilterUsageRow> FilterUsageRows { get; private set; } = [];

    /// <summary>Spec 12.5's Top targets row, the first ten in the query's own ranking.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TopTargetRow> TopTargetRows { get; private set; } = [];

    /// <summary>Spec 12.5's Timeline or calendar row, the timeline half.</summary>
    public ImagingTimelineViewModel Timeline { get; }

    /// <summary>Spec 12.5's Timeline or calendar row, the calendar half.</summary>
    public ImagingCalendarViewModel Calendar { get; }

    /// <summary>Spec 12.5's Storage row and spec 13's pie.</summary>
    public StorageViewModel Storage { get; }

    public StatsBarChartViewModel FilterUsageChart { get; }

    public StatsBarChartViewModel TopTargetsChart { get; }

    public StatsBarChartViewModel HfrPixelChart { get; }

    public StatsBarChartViewModel HfrArcsecChart { get; }

    public StatsBarChartViewModel EquipmentComparisonChart { get; }

    public StatsBarChartViewModel IngestHistoryChart { get; }

    /// <summary>Which of the two halves of spec 12.5's "Timeline or calendar" row is showing.
    /// Session state; the web persists nothing here either.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCalendar))]
    public partial bool ShowTimeline { get; set; } = true;

    public bool ShowCalendar => !ShowTimeline;

    /// <summary>Before the first response has come back, and during every later refresh.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSections))]
    public partial bool IsLoading { get; private set; }

    /// <summary>The stats read threw. The sections are replaced by <see cref="FailureText"/>,
    /// never by the empty state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSections))]
    public partial bool LoadFailed { get; private set; }

    /// <summary>The library has no LIGHT frames at all: spec 12.10's
    /// <see cref="EmptyStateText"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSections))]
    public partial bool IsEmpty { get; private set; }

    /// <summary>The nine sections render only when there is something to render. Three separate
    /// flags, one derived visibility, so no combination can show two states at once.</summary>
    public bool ShowSections => !IsLoading && !LoadFailed && !IsEmpty;

    /// <summary>The in-flight load, so a test awaits it instead of blocking on it
    /// (TRACKING section 2 item 8).</summary>
    internal Task? PendingLoad { get; private set; }

    /// <summary>
    /// Spec 12.5's bar click (questions.md Q16), forwarded from the timeline. The shell answers it
    /// by putting the range into the dashboard's date filter and selecting the dashboard, which is
    /// the same shape as its existing row-click route.
    /// </summary>
    public event EventHandler<(DateOnly From, DateOnly To)>? DateRangeRequested;

    /// <summary>
    /// Spec 12.5's empty guiding notice, forwarded from <see cref="Guiding"/>. The shell answers it
    /// by selecting the named Settings tab and bringing that tab's own section into view.
    /// </summary>
    /// <remarks>
    /// One event carrying a destination rather than one event per destination: the application
    /// already routes the Dashboard's scan filter notice to a Settings tab at a section, and this
    /// is the second occurrence of that shape, which is where the spine is built rather than at the
    /// sixth (design lesson 1). A third destination should fold
    /// <c>DashboardViewModel.ReviewScanFiltersRequested</c> onto this event too.
    /// </remarks>
    public event EventHandler<SettingsDestination>? OpenSettingsRequested;

    /// <summary>
    /// Invalidates the memoized response and re-reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RelayCommand.Execute</c> ignores <c>CanExecute</c> (TRACKING section 6 item 13), so the
    /// "already loading" guard is in the body and not only in
    /// <see cref="CanRefresh"/>.
    /// </para>
    /// <para>
    /// Review finding I2: without the invalidation this command re-projected the response already
    /// on screen for the whole life of the process, because <c>StatsCache</c> is memoized with no
    /// TTL (questions.md Q7) and nothing but a scan or a settings write drops it. The invalidation
    /// runs after the guard, so a click arriving while a load is in flight neither invalidates nor
    /// reloads.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh()
    {
        if (IsLoading || _disposed)
        {
            return;
        }

        _invalidate?.Invoke();
        RequestLoad();
    }

    private bool CanRefresh() => !IsLoading && !_disposed;

    private void OnScanFinished(object? sender, EventArgs e) => RequestLoad();

    /// <summary>
    /// Phase 15B fixer F2. The derived data behind this page was rewritten, so the page re-reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Posted, unlike <see cref="OnScanFinished"/>.</b> <c>ScanStatusService</c> has already
    /// marshalled onto the UI thread; this notification is raised on whichever thread saved the
    /// settings document or ran the correlation pass, and everything below writes observable
    /// properties (ruling G9).
    /// </para>
    /// <para>
    /// <b>Coalesced.</b> A notification arriving while a load is in flight sets
    /// <see cref="_reloadPending"/> instead of starting a second read, and the load that is in
    /// flight honours it once when it lands. Dropping it instead would be wrong, not merely
    /// cheaper: the in-flight read may already have taken its response from the memo this
    /// notification's own handler had not yet dropped.
    /// </para>
    /// <para>
    /// It does not ask whether the page is showing, because
    /// <see cref="OnScanFinished"/> does not either: this page is a DI singleton whose contract is
    /// that the reader finds it current when they come back to it.
    /// </para>
    /// </remarks>
    private void OnDerivedDataChanged(object? sender, EventArgs e) => _post(() =>
    {
        if (_disposed)
        {
            return;
        }

        if (IsLoading)
        {
            _reloadPending = true;
            return;
        }

        RequestLoad();
    });

    // Honours at most one notification that arrived while the load now landing was in flight.
    // Guarded on the generation so a response older than the newest request, which writes nothing,
    // also starts nothing.
    private void DrainDeferredReload(int generation)
    {
        if (!_reloadPending || _disposed || generation != _generation)
        {
            return;
        }

        _reloadPending = false;
        RequestLoad();
    }

    private void OnThemeChanged()
    {
        _bands = new BandBrushes();
        _arcs = new ArcBrushes();
        if (_stats is not { } stats)
        {
            return;
        }

        // Review finding M6: the rebuild starts every row collapsed, so a theme swap used to close
        // every per-filter breakdown the user had opened. The expanded set is carried across by
        // row name, which is the (telescope, camera) pair and is stable across the rebuild.
        var expanded = Performance.Rows
            .Where(row => row.IsExpanded)
            .Select(row => row.Name)
            .ToHashSet(StringComparer.Ordinal);

        Performance = new EquipmentPerformanceViewModel(stats.EquipmentPerformance, _bands);
        foreach (var row in Performance.Rows)
        {
            row.IsExpanded = expanded.Contains(row.Name);
        }

        // The guiding cards carry graded cells and a wedge ramp, both resolved from theme tokens
        // at construction, so they are rebuilt for the same reason Performance is. Review finding
        // P3-1, which is finding M6 again in a second place: the rebuild starts the table view
        // closed, so a theme swap used to shut a disclosure the user had opened.
        var tableExpanded = Guiding.IsTableExpanded;
        Guiding = BuildGuiding(stats);
        Guiding.IsTableExpanded = tableExpanded;
    }

    private GuidingViewModel BuildGuiding(StatsResponse stats)
        => new(stats.Guiding, _bands, _arcs, destination => OpenSettingsRequested?.Invoke(this, destination));

    private void OnPeriodSelected(object? sender, (DateOnly From, DateOnly To) range)
        => DateRangeRequested?.Invoke(this, range);

    private void RequestLoad()
    {
        if (_disposed)
        {
            return;
        }

        var generation = ++_generation;

        // Review finding M1: the three states really are exclusive, which means the two that
        // describe a finished load are cleared when a new one starts. Without this, a refresh after
        // a failure, or a refresh of an empty library, painted the loading line on top of one of
        // the other two.
        IsLoading = true;
        LoadFailed = false;
        IsEmpty = false;
        RefreshCommand.NotifyCanExecuteChanged();
        var token = _lifetime.Token;

        PendingLoad = Task.Run(
            () =>
            {
                try
                {
                    // The two expensive halves, both off the UI thread: Task 2's full-library
                    // aggregate, and the timeline's dark-hours prewarm (see that type's ponytail
                    // remark for the evaluation count it pays once here).
                    var stats = _loadStats();
                    var general = _general();
                    var aliases = _aliases();
                    ImagingTimelineViewModel.PrewarmDarkHours(stats.TimelineDaily, general);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _post(() =>
                    {
                        Apply(generation, stats, general, aliases);
                        DrainDeferredReload(generation);
                    });
                }
                catch (OperationCanceledException)
                {
                    // The host is going away.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "The statistics query failed");
                    _post(() =>
                    {
                        Fail(generation);
                        DrainDeferredReload(generation);
                    });
                }
            },
            token);
    }

    // Runs on the UI thread, through the post seam. A response older than the newest request is
    // dropped here rather than overwriting it.
    private void Apply(int generation, StatsResponse stats, GeneralSettings general, AliasMap aliases)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        _stats = stats;
        Overview = new OverviewTilesViewModel(stats.Overview, stats.Storage);
        Performance = new EquipmentPerformanceViewModel(stats.EquipmentPerformance, _bands);
        Guiding = BuildGuiding(stats);
        Inventory = new EquipmentInventoryViewModel(stats.Cameras, stats.Telescopes);
        DataQuality = new DataQualityViewModel(stats.DataQuality);
        Storage.Update(stats.Storage);

        FilterUsageRows = BuildFilterUsage(stats.FilterUsage, aliases);
        FilterUsageChart.Update(
            [.. FilterUsageRows.Select(row => row.FilterName)],
            [
                .. stats.FilterUsage
                    .OrderByDescending(entry => entry.IntegrationSeconds)
                    .Select((entry, index) => new StatsChartSeries(
                        entry.FilterName,
                        Sparse(stats.FilterUsage.Count, index, entry.IntegrationSeconds / 3600d),
                        // The one legitimate non-token chart colour (spec 14.5): a filter's tint is
                        // user data. Resolved through AliasMap.FilterColor and the application's
                        // one parser, never a second resolver.
                        Colour: ChartTheme.ToSkColor(
                            TargetRowViewModel.ParseTint(aliases.FilterColor(entry.FilterName)).Color))),
            ]);

        TopTargetRows =
        [
            .. stats.TopTargets.Take(TopTargetLimit).Select((entry, index) => new TopTargetRow(
                index + 1, entry.PrimaryName, MetricText.Integration(entry.IntegrationSeconds))),
        ];
        TopTargetsChart.Update(
            [.. stats.TopTargets.Take(TopTargetLimit).Select(entry => entry.PrimaryName)],
            [
                new StatsChartSeries(
                    "Integration",
                    [.. stats.TopTargets.Take(TopTargetLimit).Select(entry => (double?)(entry.IntegrationSeconds / 3600d))],
                    "ColorMetricIntegration"),
            ]);

        HfrPixelChart.Update(
            [.. stats.DataQuality.HfrPixelHistogram.Select(bucket => bucket.Label)],
            [
                new StatsChartSeries(
                    "Frames",
                    [.. stats.DataQuality.HfrPixelHistogram.Select(bucket => (double?)bucket.FrameCount)],
                    "ColorMetricHfr"),
            ]);
        HfrArcsecChart.Update(
            [.. stats.DataQuality.HfrArcsecHistogram.Select(bucket => bucket.Label)],
            [
                new StatsChartSeries(
                    "Frames",
                    [.. stats.DataQuality.HfrArcsecHistogram.Select(bucket => (double?)bucket.FrameCount)],
                    "ColorMetricHfr"),
            ]);

        // Spec 13's three compared metrics, each in the token that metric carries on every other
        // chart in the application (spec 14.5: never reassigned per chart).
        EquipmentComparisonChart.Update(
            [.. stats.EquipmentPerformance.Select(combo => $"{combo.Telescope} + {combo.Camera}")],
            [
                new StatsChartSeries(
                    "Median HFR",
                    [.. stats.EquipmentPerformance.Select(combo => combo.MedianHfr)],
                    "ColorMetricHfr"),
                new StatsChartSeries(
                    "Median FWHM",
                    [.. stats.EquipmentPerformance.Select(combo => combo.MedianFwhm)],
                    "ColorMetricFwhm"),
                new StatsChartSeries(
                    "Median guiding RMS",
                    [.. stats.EquipmentPerformance.Select(combo => combo.MedianGuidingRmsArcsec)],
                    "ColorMetricGuiding"),
            ]);

        IngestHistoryChart.Update(
            [.. stats.IngestHistory.Select(entry => entry.Date.ToString("MMM d", CultureInfo.InvariantCulture))],
            [
                new StatsChartSeries(
                    "Files added",
                    [.. stats.IngestHistory.Select(entry => (double?)entry.FilesAdded)],
                    "ColorMetricFrames"),
            ]);

        Timeline.SetData(stats, general);
        Calendar.Reload();

        IsEmpty = stats.Overview.TotalFrames == 0;
        LoadFailed = false;
        IsLoading = false;
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private void Fail(int generation)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        LoadFailed = true;
        IsEmpty = false;
        IsLoading = false;
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private static IReadOnlyList<FilterUsageRow> BuildFilterUsage(
        IReadOnlyList<FilterUsageEntry> usage, AliasMap aliases)
        =>
        [
            .. usage
                .OrderByDescending(entry => entry.IntegrationSeconds)
                .Select(entry => new FilterUsageRow(
                    entry.FilterName,
                    TargetRowViewModel.ParseTint(aliases.FilterColor(entry.FilterName)),
                    MetricText.Integration(entry.IntegrationSeconds))),
        ];

    // One series per filter, carrying a value only at its own category index, which is how the
    // spine expresses a per-bar tint. See StatsBarChartViewModel's remarks.
    private static IReadOnlyList<double?> Sparse(int length, int index, double value)
    {
        var values = new double?[length];
        values[index] = value;
        return values;
    }

    /// <summary>
    /// Cancels every background window and drops every subscription: the page's own
    /// <c>ScanFinished</c> handler, the timeline's period event, and each child's
    /// <c>ChartTheme.Changed</c> handler. A chart that does not unsubscribe keeps rebuilding after
    /// its page has closed, because the theme is static and outlives it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();

        if (_scanStatus is not null)
        {
            _scanStatus.ScanFinished -= OnScanFinished;
        }

        // F2's other half. The notifier outlives this page, so a handler left on it would pin the
        // page and keep re-reading a full-library aggregate nobody can see.
        _unfollowDerivedData?.Invoke();

        _themeSubscription.Dispose();
        Timeline.PeriodSelected -= OnPeriodSelected;
        Timeline.Dispose();
        Calendar.Dispose();
        Storage.Dispose();
        FilterUsageChart.Dispose();
        TopTargetsChart.Dispose();
        HfrPixelChart.Dispose();
        HfrArcsecChart.Dispose();
        EquipmentComparisonChart.Dispose();
        IngestHistoryChart.Dispose();

        _lifetime.Dispose();
    }

    /// <summary>Joins whatever background work is in flight. Test-only: a fixture that deletes a
    /// temp database must not race a load still holding a connection to it.</summary>
    internal void Quiesce(TimeSpan timeout)
    {
        var pending = new[] { PendingLoad, Calendar.PendingLoad }
            .Where(task => task is not null)
            .Select(task => task!);
        Task.WhenAll(pending).ContinueWith(_ => { }, TaskScheduler.Default).Wait(timeout);
    }
}
