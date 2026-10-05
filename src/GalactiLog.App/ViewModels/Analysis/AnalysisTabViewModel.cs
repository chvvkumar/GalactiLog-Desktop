using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// The rows of spec 12.14's States table that every Analysis tab shares. The view binds visibility
/// to this, and each tab's own extra rows (the Correlation point-count sentences, the box plot's
/// dropped groups, the Compare short-group states) are that tab's own properties, not members
/// here.
/// </summary>
public enum AnalysisTabState
{
    /// <summary>A tab not yet visited. Nothing is created and nothing is queried.</summary>
    NotLoaded,

    /// <summary>Loading the first result. The tab's body is replaced by a loading line.</summary>
    Loading,

    /// <summary>Loading a refresh. The last result stays drawn and the controls stay live.</summary>
    Refreshing,

    /// <summary>A result is drawn.</summary>
    Ready,

    /// <summary>No row matched the filters.</summary>
    Empty,

    /// <summary>Rows matched but too few of them to answer, which is a different sentence per tab
    /// (the histogram's two values, the box plot's four per group, Compare's four per group).
    /// </summary>
    TooFew,

    /// <summary>The query threw. Per tab, never per page: the other four are untouched.</summary>
    Failed,
}

/// <summary>
/// The one Analysis tab spine. Five tabs is one pattern five times, and this is the
/// only place the load loop exists: lazy body creation, kept state, a query only while visible,
/// stale marking while hidden, the last result kept through a refresh, the generation token, the
/// per tab failure and the mixed plate scale callout. **A tab that re-implements any of it is a
/// finding.**
/// </summary>
/// <remarks>
/// <para>
/// A subclass supplies a body, a query and a result mapper and nothing else. The base names no
/// query type, taking the query as a callback on the subclass, so a tab can be handed the cache or
/// the query itself and the base depends on neither (<c>core-shapes.md</c> section 7: the two
/// publish the same eight members precisely so the page can be handed either; the page is handed
/// the cache).
/// </para>
/// <para>
/// Threading is one shape for every tab: the query runs inside a <see cref="Task.Run(Action)"/> off the UI
/// thread and every publish goes back through the post seam, the shape
/// <c>StatisticsViewModel.RequestLoad</c> already uses. There is no <c>ConfigureAwait</c> anywhere
/// in this file, and no cancellation token: the queries are synchronous and short, a cancelled
/// <see cref="Task.Run(Action)"/> would still run to completion, and the monotonic generation is
/// the whole of the stale-answer mechanism.
/// </para>
/// </remarks>
public abstract class AnalysisTabViewModel : ObservableObject, IDisposable
{
    /// <summary>Spec 12.14's mixed plate scale warning, verbatim and in one place because four
    /// tabs show the same sentence in the same place (design lesson 1). The markup
    /// draws it as <c>Border.callout warn</c> above the chart and inside the tab body.</summary>
    public const string PlateScaleWarningText =
        "This selection mixes optical trains with different plate scales. HFR is measured in "
        + "pixels, so these figures are not comparable across them. Choose one equipment "
        + "combination in the filter bar.";

    /// <summary>Spec 12.14's States table, "A query fails". The Retry button sits beside it.</summary>
    public const string FailureText = "The figures could not be read.";

    /// <summary>Spec 12.14's States table, "No row matches the filters", verbatim and in one place
    /// for the same reason the three sentences around it are: all five tabs show it, and a wording
    /// edit that lands in four of five makes the page say two different things about one state
    /// (design lesson 1). <see cref="EmptyMessage"/> answers it for every tab and stays virtual, so
    /// a tab that later needs its own sentence can still say so.</summary>
    public const string EmptyText =
        "No frames match the current filters. Widen them in the filter bar above.";

    /// <summary>Spec 12.14's States table, "Loading, first result", for every tab but the Matrix,
    /// which overrides <see cref="LoadingMessage"/>.</summary>
    public const string DefaultLoadingText = "Loading...";

    /// <summary>Spec 12.14's date range error, verbatim, which the filter bar shows under its two
    /// date fields. A tab first selected while the range is reversed has no last result to keep, so
    /// it carries the same sentence rather than standing blank and silent.
    /// </summary>
    public const string ReversedRangeText = "From date must be on or before To date.";

    // The two values above two distinct plate scales raises the warning; one, or none, does not.
    // Nulls never reach here: AnalysisQuery counts distinct non-null arcsec_per_pixel values only.
    private const int MixedPlateScales = 2;

    private readonly Action<Action> _post;
    private readonly Func<AnalysisFilter?> _filter;
    private readonly ILogger _logger;

    // Monotonic, incremented on the UI thread inside Refresh and compared on the UI thread inside
    // the post callback, so it is never read concurrently. A second Refresh while the first is in
    // flight publishes the second only.
    private int _generation;

    // Whether a result has ever been published, which is what makes the next load a Refreshing
    // rather than a Loading and is therefore what keeps the last result drawn (spec 12.14's
    // "Loading, refresh" row, the web's keepPreviousData).
    private bool _hasResult;

    private object? _body;
    private AnalysisTabState _state = AnalysisTabState.NotLoaded;
    private bool _isStale;
    private bool _isVisible;
    private int _distinctPlateScales;
    private bool _rangeReversed;
    private bool _disposed;

    /// <param name="key">The tab's persisted key, one of <see cref="AnalysisDisplay.TabKeys"/>.</param>
    /// <param name="title">The strip button's label.</param>
    /// <param name="helpTopicId">The topic the tab heading's glyph opens.</param>
    /// <param name="filter">The shared filter bar's current filter, or null while the date range
    /// is reversed. Normally <c>() =&gt; page.SharedFilter.Current</c>. A delegate rather than the
    /// bar itself, so the base names no view-model it does not own.</param>
    /// <param name="post">The post seam, normally <c>UiPost.Default</c>.</param>
    /// <param name="logger">Where a failed query is recorded. Null logs nothing.</param>
    protected AnalysisTabViewModel(
        string key,
        string title,
        string helpTopicId,
        Func<AnalysisFilter?> filter,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        Key = key;
        Title = title;
        HelpTopicId = helpTopicId;
        _filter = filter;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        // RelayCommand.Execute ignores CanExecute, so the Failed guard is repeated in Retry's own
        // body: a binding, a keyboard route or a test that calls Execute directly must not requery
        // a tab that is drawing a result.
        RetryCommand = new RelayCommand(Retry, () => State == AnalysisTabState.Failed);
    }

    /// <summary>The tab's persisted key.</summary>
    public string Key { get; }

    /// <summary>The strip button's label.</summary>
    public string Title { get; }

    /// <summary>The topic id the tab heading's help glyph opens.</summary>
    public string HelpTopicId { get; }

    /// <summary>Spec 12.14's States table, "A query fails": offered only in
    /// <see cref="AnalysisTabState.Failed"/>.</summary>
    public IRelayCommand RetryCommand { get; }

    /// <summary>The tab's own controls-and-chart view-model, built lazily on the first
    /// <see cref="Refresh"/> and kept afterwards with its controls and its last result (spec
    /// 12.14's Tab behaviour). Null until then, which is what makes a tab never visited cost
    /// nothing.</summary>
    public object? Body
    {
        get => _body;
        private set => SetProperty(ref _body, value);
    }

    /// <summary>The tail of the load, so a case can await it instead of sleeping. Nothing in the
    /// application awaits it.</summary>
    public Task? PendingLoad { get; private set; }

    /// <summary>Which of spec 12.14's shared state rows the tab is in.</summary>
    public AnalysisTabState State
    {
        get => _state;
        protected set
        {
            if (!SetProperty(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(ShowsResult));
            RetryCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Whether a filter change has happened since the last published result. A filter
    /// change while hidden marks; the refresh happens on the next selection, which is the whole of
    /// spec 12.14's "switching the equipment combination does not fire five queries".</summary>
    public bool IsStale
    {
        get => _isStale;
        private set => SetProperty(ref _isStale, value);
    }

    /// <summary>
    /// Whether this is the selected tab. A tab issues a query only while it is visible.
    /// </summary>
    /// <remarks>
    /// Becoming visible refreshes the tab when it is stale or has never loaded, and a tab that is
    /// current and clean re-renders nothing (spec 12.14). The rule lives on the setter rather than
    /// in the page's selection handler so that the page assigns visibility and nothing else, and a
    /// second caller cannot forget it (design lesson 2).
    /// </remarks>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (!SetProperty(ref _isVisible, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsCurrent));

            if (value && (IsStale || State == AnalysisTabState.NotLoaded))
            {
                Refresh();
            }
        }
    }

    /// <summary>Whether this is the selected tab, which is what the page's tab strip marks as
    /// current. The same answer <see cref="IsVisible"/> gives, under the strip's own word, so the
    /// strip binds to a name that says what it draws and no second flag can drift from it.
    /// </summary>
    public bool IsCurrent => IsVisible;

    /// <summary>The count of distinct non-null plate scales among the rows the last result read.
    /// Set by the subclass inside its mapper, off the seam's
    /// <c>DistinctPlateScales</c> field. <see cref="Publish"/> clears it before every mapping and
    /// <see cref="Fail"/> clears it too, so a mapper's early arm cannot leave the previous result's
    /// count standing over rows it never read.</summary>
    public int DistinctPlateScales
    {
        get => _distinctPlateScales;
        protected set
        {
            if (SetProperty(ref _distinctPlateScales, value))
            {
                OnPropertyChanged(nameof(PlateScaleWarningVisible));
            }
        }
    }

    /// <summary>Spec 12.14's mixed plate scale callout: two or more distinct plate scales AND a
    /// pixel-domain metric. One, or none, does not raise it, and neither does two under a metric
    /// that is not measured in pixels.</summary>
    public bool PlateScaleWarningVisible => DistinctPlateScales >= MixedPlateScales && IsPixelMetric;

    /// <summary>
    /// Whether the tab's result region is drawn: its chart, its table and its stats cards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// True in exactly <see cref="AnalysisTabState.Ready"/> and
    /// <see cref="AnalysisTabState.Refreshing"/>, which is spec 12.14's States table read straight
    /// off: "Loading, first result" replaces the body with a loading line, "No row matches the
    /// filters" draws no chart, and "Loading, refresh" keeps the last result drawn, which is why
    /// <see cref="AnalysisTabState.Refreshing"/> is in the set and
    /// <see cref="AnalysisTabState.Loading"/> is not.
    /// </para>
    /// <para>
    /// Each tab body view holds one element named <c>ResultRegion</c> bound to this, with its
    /// chart, table and cards inside it and its pickers, segments and toggles OUTSIDE it. The
    /// controls stay live in every state on purpose: a reader who picked a metric with no data has
    /// to be able to pick another, and Compare starts with nothing queryable at all, so hiding a
    /// whole tab body on a non-empty <see cref="StatusLine"/> would make that tab unreachable.
    /// <c>AnalysisResultRegionCensusTest</c> is the choke point for the rule, so a sixth tab view
    /// cannot ship without one (design lesson 2).
    /// </para>
    /// </remarks>
    public bool ShowsResult => State is AnalysisTabState.Ready or AnalysisTabState.Refreshing;

    /// <summary>The sentence for the current state. Empty in the three states that draw a chart or
    /// nothing at all.</summary>
    public virtual string StatusLine => State switch
    {
        AnalysisTabState.Loading => LoadingMessage,
        AnalysisTabState.Empty => EmptyMessage,
        AnalysisTabState.TooFew => TooFewMessage,
        AnalysisTabState.Failed => FailureText,
        _ => _rangeReversed ? ReversedRangeText : string.Empty,
    };

    /// <summary>Spec 12.14's "When it is shown": the Y metric is <c>hfr</c> on Correlation, the
    /// chosen metric is <c>hfr</c> on the histogram, the box plot and Time Series, always true on
    /// the Matrix because the grid's <c>hfr</c> row is present whenever the grid is, and always
    /// false on Compare, which converts to arcseconds.</summary>
    protected abstract bool IsPixelMetric { get; }

    /// <summary>Spec 12.14's "No row matches the filters" sentence, which is
    /// <see cref="EmptyText"/> for every tab that ships today.</summary>
    protected virtual string EmptyMessage => EmptyText;

    /// <summary>Spec 12.14's too-few sentence for this tab. The Matrix has no such row and takes
    /// the empty one.</summary>
    protected virtual string TooFewMessage => EmptyMessage;

    /// <summary>The loading line. The Matrix reads "Computing correlations..." (spec 12.14).</summary>
    protected virtual string LoadingMessage => DefaultLoadingText;

    /// <summary>Whether the tab's own controls name a query at all. False only on Compare, whose
    /// two groups start unchosen (spec 12.14: "Compare with fewer than two groups chosen, or the
    /// same group twice ... Nothing is queried").</summary>
    protected virtual bool CanQuery => true;

    /// <summary>What the tab publishes instead of a query when <see cref="CanQuery"/> is false.
    /// Overridden by Compare alone.</summary>
    protected virtual void PublishCannotQuery()
    {
    }

    /// <summary>Builds the tab's body. Called once, on the first <see cref="Refresh"/> that gets
    /// as far as a query.</summary>
    protected abstract object CreateBody();

    /// <summary>One <c>AnalysisCache</c> call, run off the UI thread. Every query goes through the
    /// cache and never through <c>AnalysisQuery</c>.</summary>
    protected abstract object? RunQuery(AnalysisFilter filter);

    /// <summary>Maps the result onto the body's bound properties and answers the state it leaves
    /// the tab in. Runs on the UI thread, through the post seam.</summary>
    protected abstract AnalysisTabState MapResult(object? result);

    /// <summary>Marks the tab stale without querying. Called on every tab when the shared filter
    /// changes; the selected one is refreshed as well.</summary>
    public void MarkStale() => IsStale = true;

    /// <summary>
    /// Stops the tab publishing. A result that arrives after the tab or its page has been disposed
    /// publishes nothing, which is the guard <c>StatisticsViewModel</c> holds as a host-lifetime
    /// token for the same reason: the page is built by the shell and a load started just before a
    /// shutdown or a page rebuild must not write into a view-model nothing is bound to any more.
    /// </summary>
    public virtual void Dispose() => _disposed = true;

    /// <summary>The load loop, written once.</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        // Rule 4: nothing is queried for a tab that has never been visible, and no body is built
        // for one.
        if (!IsVisible)
        {
            MarkStale();
            return;
        }

        // The body is the tab's live controls, not a result, so it is built above both of the arms
        // below rather than below them. A tab whose CanQuery is false draws its
        // own pickers and can therefore become queryable through them; a tab first selected under a
        // reversed range draws its controls instead of nothing at all.
        EnsureBody();

        // Spec 12.14's reversed date range: no tab issues a query and the last drawn result stays
        // on screen. The bar answers null exactly while the range is illegal, which mirrors the
        // web passing undefined for both dates rather than firing an empty query.
        var filter = _filter();
        if (filter is null)
        {
            // With nothing drawn yet there is no last result to keep, so the tab carries the
            // filter bar's own sentence rather than standing blank and silent.
            // With a result drawn the States table's row applies unchanged and the result stays.
            SetRangeReversed(!_hasResult);
            return;
        }

        SetRangeReversed(false);

        if (!CanQuery)
        {
            PublishCannotQuery();
            return;
        }

        // Rule 1: Refreshing never clears Body, so the last result stays drawn.
        State = _hasResult ? AnalysisTabState.Refreshing : AnalysisTabState.Loading;

        var generation = ++_generation;
        PendingLoad = Task.Run(() =>
        {
            object? result;
            try
            {
                result = RunQuery(filter);
            }
            catch (Exception ex)
            {
                // Rule 3: a failure is per tab, not per page. Nothing escapes to the shell.
                _logger.LogWarning(ex, "The {Tab} analysis query failed", Key);
                _post(() => Fail(generation));
                return;
            }

            // The post call sits outside the try on purpose. Under
            // UiPost.Default the closure is queued and runs later on the dispatcher, so a try here
            // would cover a mapper's throw only under a post seam that runs its closure inline,
            // which is the harness and not the application. Publish carries its own.
            _post(() => Publish(generation, result));
        });
    }

    private void EnsureBody() => Body ??= CreateBody();

    /// <summary>Whether <see cref="StatusLine"/> carries the filter bar's own date sentence. Set by
    /// the load loop above, and by a tab that holds more than one result half when it swaps the
    /// half under a reversed range: the incoming half can hold nothing although the tab as a whole
    /// has published before, and the reader is then owed the range's sentence rather than one about
    /// filters that are not the problem.</summary>
    protected void SetRangeReversed(bool value)
    {
        if (_rangeReversed == value)
        {
            return;
        }

        _rangeReversed = value;
        OnPropertyChanged(nameof(StatusLine));
    }

    // Both publish arms run on the UI thread through the post seam, so the generation compare and
    // every observable write below it are on one thread (rule 2).
    private void Publish(int generation, object? result)
    {
        if (_disposed || generation != _generation)
        {
            return;
        }

        try
        {
            // The count is per result, structurally, so a mapper's early arm that reads no rows
            // cannot leave the previous result's count standing. A mapper that
            // has a count overwrites this.
            DistinctPlateScales = 0;
            State = MapResult(result);
        }
        catch (Exception ex)
        {
            // This runs on the UI thread through the post seam, outside the query's own try, so
            // rule 3 is enforced here as well: a mapper that throws fails this tab and nothing
            // else.
            _logger.LogWarning(ex, "The {Tab} analysis result could not be mapped", Key);
            Fail(generation);
            return;
        }

        _hasResult = true;
        IsStale = false;
    }

    private void Fail(int generation)
    {
        if (_disposed || generation != _generation)
        {
            return;
        }

        DistinctPlateScales = 0;
        State = AnalysisTabState.Failed;
    }

    private void Retry()
    {
        // The command's CanExecute says the same thing; RelayCommand.Execute does not consult it.
        if (State != AnalysisTabState.Failed)
        {
            return;
        }

        Refresh();
    }

    /// <summary>Raises <see cref="PlateScaleWarningVisible"/> after the subclass's own pixel-metric
    /// answer has changed, for a tab whose metric picker moves without the count moving with
    /// it.</summary>
    protected void NotifyPixelMetricChanged() => OnPropertyChanged(nameof(PlateScaleWarningVisible));
}

/// <summary>
/// The tab spine with the subclass's own result type, which is the whole of what a tab adds to
/// <see cref="AnalysisTabViewModel"/>: a typed query and a typed mapper. The two bridges below are
/// sealed, so a tab cannot reach past them to the untyped pair and re-enter the loop.
/// </summary>
/// <typeparam name="TResult">The seam record this tab's one cache call answers. Nullable because
/// <c>AnalysisCache.Distribution</c> answers null below two values (<c>core-shapes.md</c> section
/// 5.4 item 5).</typeparam>
public abstract class AnalysisTabViewModel<TResult> : AnalysisTabViewModel
    where TResult : class
{
    /// <inheritdoc cref="AnalysisTabViewModel(string, string, string, Func{AnalysisFilter}, Action{Action}, ILogger)"/>
    protected AnalysisTabViewModel(
        string key,
        string title,
        string helpTopicId,
        Func<AnalysisFilter?> filter,
        Action<Action>? post = null,
        ILogger? logger = null)
        : base(key, title, helpTopicId, filter, post, logger)
    {
    }

    /// <summary>One <c>AnalysisCache</c> call, run off the UI thread.</summary>
    protected abstract TResult? Query(AnalysisFilter filter);

    /// <summary>Maps the result onto the body's bound properties and answers the state it leaves
    /// the tab in. Runs on the UI thread.</summary>
    protected abstract AnalysisTabState Map(TResult? result);

    /// <inheritdoc/>
    protected sealed override object? RunQuery(AnalysisFilter filter) => Query(filter);

    /// <inheritdoc/>
    protected sealed override AnalysisTabState MapResult(object? result) => Map((TResult?)result);
}
