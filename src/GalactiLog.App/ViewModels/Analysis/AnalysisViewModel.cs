using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// Spec 12.14's Analysis page: the shared filter bar, the five-tab strip and whichever tab is
/// current. The page reads and nothing else; the only thing it writes is the four
/// <c>display.analysis</c> keys of spec 5.8.2.
/// </summary>
/// <remarks>
/// <para>
/// Values and delegates, never a service provider and never <c>AnalysisCache</c>, which is sealed
/// with no interface and no virtual member (ruling P1-1): the page constructs in a unit test with
/// no window and no database (spec 18.3), exactly as <c>StatisticsViewModel</c> does with its own
/// <c>loadStats</c>. <c>AppHost</c> binds each delegate to the matching <c>AnalysisCache</c>
/// member at the one place they are bound.
/// </para>
/// <para>
/// The page follows one "the data behind this page has been rewritten" subscription, through the
/// optional delegate pair <c>StatisticsViewModel</c> takes in the same shape. <c>AppHost</c>
/// composes into it every event that drops <c>AnalysisCache</c>: its own process-level
/// notification, which carries a general save and a completed correlation pass (ruling A6), a
/// completed scan, and an alias source save. Each arrives after the memo has been dropped, so what
/// this page re-reads is a rebuilt answer. Without it the page, a DI singleton, kept whatever the
/// reader left it holding: no tab was ever marked stale, so a return to the page redrew the figures
/// from before the scan, and the filter bar's two option lists could not be re-read at all for the
/// life of the process.
/// </para>
/// </remarks>
public sealed partial class AnalysisViewModel : ObservableObject, IDisposable
{
    private readonly Action<Func<DisplaySettings, DisplaySettings>>? _writeDisplay;
    private readonly Action<Action> _post;
    private readonly Action? _unfollowDerivedData;
    private bool _disposed;

    /// <param name="loadEquipment">Normally <c>AnalysisCache.EquipmentCombinations</c>.</param>
    /// <param name="loadFilters">Normally <c>AnalysisCache.Filters</c>.</param>
    /// <param name="correlation">Normally <c>AnalysisCache.Correlation</c>.</param>
    /// <param name="distribution">Normally <c>AnalysisCache.Distribution</c>.</param>
    /// <param name="boxPlot">Normally <c>AnalysisCache.BoxPlot</c>.</param>
    /// <param name="timeSeries">Normally <c>AnalysisCache.TimeSeries</c>.</param>
    /// <param name="matrix">Normally <c>AnalysisCache.Matrix</c>.</param>
    /// <param name="compare">Normally <c>AnalysisCache.Compare</c>.</param>
    /// <param name="display">The display document, read once for the four
    /// <c>display.analysis</c> keys. <c>AppHost</c> passes <c>settingsStore.GetDisplay()</c>.</param>
    /// <param name="writeDisplay">Normally <c>DisplayColumnWriter.Write</c>, the one serialized
    /// chain over the display document. Null writes nothing, which is what a unit test wants.</param>
    /// <param name="post">Ruling A8's post seam, normally <c>UiPost.Default</c>.</param>
    /// <param name="logger">Where a failed tab query is recorded.</param>
    /// <param name="subscribeDerivedDataChanged">"The data behind this page has been rewritten",
    /// from every source that can rewrite it. <c>AppHost</c> composes three into this one
    /// delegate: the process-level notification it raises after dropping the derived memos, which
    /// carries a general save and a completed correlation pass; a completed scan; and an alias
    /// source save. All three arrive after the memo they invalidated has been dropped. The two
    /// sides arrive as delegates rather than the sources themselves, like every other seam here
    /// (spec 18.3), and trailing and optional so no existing construction site changes.</param>
    /// <param name="unsubscribeDerivedDataChanged">Its pair, called from <see cref="Dispose"/>.
    /// </param>
    public AnalysisViewModel(
        Func<IReadOnlyList<EquipmentCombination>> loadEquipment,
        Func<IReadOnlyList<string>> loadFilters,
        Func<AnalysisMetric, AnalysisMetric, AnalysisFilter, CorrelationResult> correlation,
        Func<AnalysisMetric, AnalysisFilter, DistributionResult?> distribution,
        Func<AnalysisMetric, BoxPlotGrouping, AnalysisFilter, BoxPlotResult> boxPlot,
        Func<AnalysisMetric, AnalysisFilter, TimeSeriesResult> timeSeries,
        Func<AnalysisFilter, MatrixResult> matrix,
        Func<AnalysisMetric, CompareMode, string, string, DateOnly?, DateOnly?, CompareResult> compare,
        DisplaySettings? display = null,
        Action<Func<DisplaySettings, DisplaySettings>>? writeDisplay = null,
        Action<Action>? post = null,
        ILogger<AnalysisViewModel>? logger = null,
        Action<EventHandler>? subscribeDerivedDataChanged = null,
        Action<EventHandler>? unsubscribeDerivedDataChanged = null)
    {
        _writeDisplay = writeDisplay;
        _post = post ?? UiPost.Default;
        var stored = (display ?? new DisplaySettings()).Analysis;

        SharedFilter = new SharedFilterViewModel(
            loadEquipment,
            loadFilters,
            AnalysisDisplay.ParseGranularity(stored.Granularity),
            post,
            granularity => AnalysisDisplay.WriteGranularity(writeDisplay, granularity),
            logger);

        // The base asks for the bar's filter on every refresh and answers null exactly while the
        // range is reversed, which is the whole of spec 12.14's "no query on any tab" in one
        // nullable. A delegate, so the base names no view-model it does not own.
        var filter = () => SharedFilter.Current;

        Tabs =
        [
            new CorrelationTabViewModel(
                filter,
                correlation,
                AnalysisDisplay.XMetric(stored.XMetric),
                AnalysisDisplay.YMetric(stored.YMetric),
                PersistMetrics,
                post,
                logger),
            new DistributionsTabViewModel(filter, distribution, boxPlot, post, logger),
            new TimeSeriesTabViewModel(filter, timeSeries, post, logger),

            // Ruling B9: the Matrix reaches the Correlation tab through this delegate and never
            // through a page reference or a window-level event (spec 12.14: "The navigation is a
            // single call on the page view-model").
            new MatrixTabViewModel(filter, matrix, OpenCorrelationOn, post, logger),
            // The bar's two collection INSTANCES, not snapshots: its one list read is asynchronous
            // and is not even started until the last line of this constructor, and the Compare
            // tab's two group pickers are the only reader of those lists.
            new CompareTabViewModel(
                filter,
                compare,
                SharedFilter.EquipmentChoices,
                SharedFilter.FilterChoices,
                post,
                logger),
        ];

        // Assigned to the field, not through the setter: seeding the stored tab must select it and
        // query NOTHING (spec 12.14's "A tab not yet visited"). The first query is the selected
        // tab's own first refresh, which runs when the view binds and calls Activate.
        _selectedTab = Tabs.First(tab =>
            string.Equals(tab.Key, AnalysisDisplay.ParseTab(stored.Tab), StringComparison.Ordinal));

        // Subscribed below both members OnFilterChanged dereferences: a
        // handler wired before Tabs and _selectedTab exist is a null dereference in a constructor
        // the day anything in the bar's Publish raises Changed.
        SharedFilter.Changed += OnFilterChanged;

        // Subscribed above the bar's first read rather than below it, the order
        // StatisticsViewModel uses: a save landing in that window is exactly the one a reader
        // would otherwise have to recover from by restarting the application.
        if (subscribeDerivedDataChanged is not null && unsubscribeDerivedDataChanged is not null)
        {
            subscribeDerivedDataChanged(OnDerivedDataChanged);
            _unfollowDerivedData = () => unsubscribeDerivedDataChanged(OnDerivedDataChanged);
        }

        // The LAST statement of this constructor, and the whole of why the bar's constructor does
        // not start its own read. The read runs on a thread-pool thread and publishes through the
        // post seam, so under an inline seam the publish appends to EquipmentChoices and
        // FilterChoices from that thread; CompareTabViewModel's constructor, five lines above,
        // enumerates those same two collections. Started here, nothing the publish touches is
        // still being built, so neither the tabs, the selection nor this subscription can be
        // observed half-made. The throw was seen once.
        //
        // This line owns the first read, and not merely by sitting last: the subscription above it
        // can deliver a notification before this line runs, which under an inline seam reaches
        // OnDerivedDataChanged on this thread. SharedFilterViewModel.Reload answers nothing until
        // Load has started, so such a notification cannot start the read from inside this
        // constructor. Under UiPost.Default it could not anyway, the post being queued behind the
        // UI thread, but that is thread luck rather than a rule.
        SharedFilter.Load();
    }

    /// <summary>The page heading (spec 12.14's parts table).</summary>
    public string Title => "Analysis";

    /// <summary>Spec 12.14's filter bar, shared by all five tabs.</summary>
    public SharedFilterViewModel SharedFilter { get; }

    /// <summary>The five tabs, in spec 12.14's strip order. Each is built here with its persisted
    /// key, its title and its help topic id; its own body is built lazily by the base on the first
    /// refresh (ruling A7).</summary>
    public IReadOnlyList<AnalysisTabViewModel> Tabs { get; }

    private AnalysisTabViewModel _selectedTab;

    /// <summary>
    /// The current tab. Assigning it writes <c>display.analysis.tab</c>, hides the outgoing tab and
    /// shows the incoming one; the base's <c>IsVisible</c> setter is what refreshes a tab that is
    /// stale or has never loaded, and does nothing for one that is current and clean, so the page
    /// assigns visibility and calls nothing else.
    /// </summary>
    /// <remarks>Hand-written rather than <c>[ObservableProperty]</c> for those side effects. The
    /// strip is a row of <c>Button</c>s and not a <c>ListBox</c>, so there is no ctrl-click that
    /// writes null and no null arm is needed here.</remarks>
    public AnalysisTabViewModel SelectedTab
    {
        get => _selectedTab;
        set
        {
            var previous = _selectedTab;
            if (_disposed || !SetProperty(ref _selectedTab, value))
            {
                return;
            }

            previous.IsVisible = false;
            AnalysisDisplay.WriteTab(_writeDisplay, value.Key);
            value.IsVisible = true;
        }
    }

    /// <summary>Selects a tab from the strip.</summary>
    [RelayCommand]
    private void SelectTab(AnalysisTabViewModel? tab)
    {
        if (tab is not null)
        {
            SelectedTab = tab;
        }
    }

    /// <summary>
    /// Shows the current tab, which is what makes its first query run. Called by
    /// <c>AnalysisView</c> when the page attaches to the visual tree (ruling A12: the seam the view
    /// assigns), never from the constructor, so building the page queries nothing.
    /// </summary>
    /// <remarks>Idempotent: the base refreshes only a tab that is stale or has never loaded, so a
    /// detach and a re-attach of the same page redraw from the result it is already holding.
    /// </remarks>
    public void Activate()
    {
        if (!_disposed)
        {
            SelectedTab.IsVisible = true;
        }
    }

    /// <summary>
    /// The Matrix's click route (spec 12.14): writes both stored metric keys and selects the
    /// Correlation tab, which the selection's own rule then refreshes.
    /// </summary>
    /// <remarks>A single call on the page view-model, not a window-level event as the web uses.
    /// The Matrix tab calls it through the delegate it was constructed with.
    /// </remarks>
    public void OpenCorrelationOn(AnalysisMetric x, AnalysisMetric y)
    {
        if (_disposed)
        {
            return;
        }

        if (Tabs[0] is CorrelationTabViewModel correlation)
        {
            correlation.XMetric = x;
            correlation.YMetric = y;
            correlation.MarkStale();
        }

        PersistMetrics(x, y);

        // A click that lands on the tab already showing changes no selection, so the selection's
        // own refresh rule never fires and the stale mark above would sit there unanswered.
        if (ReferenceEquals(SelectedTab, Tabs[0]))
        {
            Tabs[0].Refresh();
        }
        else
        {
            SelectedTab = Tabs[0];
        }
    }

    /// <summary>
    /// Disposes the five tabs, so a query in flight when the page goes away publishes nothing, and
    /// drops both subscriptions: the bar's and the host's notification.
    /// </summary>
    /// <remarks>Resolves nothing and builds nothing: every participant is already held. The host's
    /// notifier outlives every page it speaks to, which is why the unfollow is the half that
    /// matters.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SharedFilter.Changed -= OnFilterChanged;
        _unfollowDerivedData?.Invoke();
        foreach (var tab in Tabs)
        {
            tab.Dispose();
        }
    }

    // Spec 12.14's Tab behaviour: a filter change marks every tab stale and the selected one
    // refreshes, so switching the equipment combination does not fire five queries. A hidden tab's
    // Refresh marks stale and returns, which is the base's own rule, so this is safe before the
    // view has activated the page.
    private void OnFilterChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var tab in Tabs)
        {
            tab.MarkStale();
        }

        SelectedTab.Refresh();
    }

    /// <summary>
    /// The derived data behind this page has been rewritten, so the bar re-reads its two option
    /// lists and every tab is marked stale with the selected one refreshed, which is
    /// <see cref="OnFilterChanged"/>'s body plus that re-read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Posted, unlike the bar's own <c>Changed</c>.</b> This notification is raised on whichever
    /// thread saved the settings document or ran the correlation pass, and everything below writes
    /// observable properties.
    /// </para>
    /// <para>
    /// It does not ask whether the page is showing, for the reason <c>StatisticsViewModel</c>'s own
    /// handler does not: this page is a DI singleton whose contract is that the reader finds it
    /// current when they come back to it. Nothing extra is queried by a hidden page either, because
    /// a hidden tab's <c>Refresh</c> marks stale and returns.
    /// </para>
    /// <para>
    /// A burst of notifications costs one further list read, which the bar's own
    /// <see cref="SharedFilterViewModel.Reload"/> coalesces, and one further query per tab, which
    /// the tab base's generation token already collapses.
    /// </para>
    /// </remarks>
    private void OnDerivedDataChanged(object? sender, EventArgs e) => _post(() =>
    {
        if (_disposed)
        {
            return;
        }

        SharedFilter.Reload();
        OnFilterChanged(this, EventArgs.Empty);
    });

    // The two metric writers of spec 5.8.2, one call each, through the one seam that applies each
    // mutation to the document as loaded inside the queued write (ruling Q17).
    private void PersistMetrics(AnalysisMetric x, AnalysisMetric y)
    {
        AnalysisDisplay.WriteXMetric(_writeDisplay, x);
        AnalysisDisplay.WriteYMetric(_writeDisplay, y);
    }
}
