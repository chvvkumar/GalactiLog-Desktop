using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// One entry of a Compare group picker: what the reader sees and what goes on the wire.
/// </summary>
/// <remarks>
/// The two halves are kept apart on purpose. <c>AnalysisQuery.Compare</c> splits an
/// Equipment-mode string on three vertical bars, so the wire value is the two raw canonical names
/// joined by that separator with nothing trimmed and no marker, while the label is
/// "&lt;telescope&gt; + &lt;camera&gt;" plus the grouped marker. Sending the label instead makes
/// every equipment comparison match no rows, silently, through the no-rows state.
/// </remarks>
/// <param name="Label">What the picker draws.</param>
/// <param name="Value">What <c>AnalysisCache.Compare</c> is handed.</param>
/// <param name="Grouped">Whether the combination folds more than one raw name, which draws the
/// page's one grouped marker beside the label and never inside the value.</param>
public sealed record CompareGroupChoice(string Label, string Value, bool Grouped)
{
    /// <summary>
    /// The one text every user-visible place outside the picker prints for this group: what the
    /// picker shows, which is <see cref="Label"/> with the page's one grouped marker where the
    /// picker draws it.
    /// </summary>
    /// <remarks>
    /// The picker draws that marker as a run of its own, in the tertiary ink and carrying its
    /// tooltip, which a chart category label, a sentence and a card heading cannot do, so they
    /// take it as part of the one string. The marker is never in <see cref="Value"/>: it is a
    /// property of the alias grouping and not of the two canonical names the query matches on.
    /// </remarks>
    public string Display => Grouped ? Label + " " + SharedFilterViewModel.GroupedMarker : Label;
}

/// <summary>
/// Spec 12.14's Compare tab: a two-button segment over Equipment and Filter, a metric picker over
/// the ten Y metrics, two group pickers, and on a result one box plot with two categories, the
/// verdict sentence and two stats cards.
/// </summary>
/// <remarks>
/// <para>
/// It is the one tab that overrides <see cref="CanQuery"/> and <see cref="PublishCannotQuery"/>:
/// spec 12.14's States table says "Compare with fewer than two groups chosen, or the same group
/// twice ... Nothing is queried", so nothing is queried until the two pickers hold two different
/// groups. Because the base builds the body above both of its early returns, the pickers exist
/// while the tab is not yet queryable, which is what makes the tab reachable at all.
/// </para>
/// <para>
/// It is also the one tab that never shows the mixed plate scale warning: spec 12.14's "When it is
/// shown" paragraph excludes it, because the verdict is computed on arcsecond medians rather than
/// on pixels and the tab carries its own not-comparable sentence instead.
/// </para>
/// <para>
/// <b>The two group pickers take the filter bar's own two collections as constructor arguments</b>,
/// the instances the bar fills in place rather than snapshots of them, so this tab runs no list
/// query of its own and a bar read that lands after the page is built still reaches the pickers.
/// An earlier shape had the tab's view walk its visual ancestors for the page's view-model and
/// assign the lists on attach; the constructor seam deletes that coupling, and the tab body's code
/// behind is back to the two lines every other tab body has.
/// </para>
/// <para>
/// <b>The wire value reaches no reader.</b> It is the identity and the query argument, and the
/// query can only echo back the two names it is given, so the map from a key to the name the
/// reader chose is this tab's: every user-visible place, the box plot's two category labels, the
/// verdict, both card headings and the three too-few sentences, prints
/// <see cref="CompareGroupChoice.Display"/> of the chosen row, taken in <see cref="Query"/>. No
/// place splits, trims or substitutes on the key.
/// </para>
/// <para>
/// <see cref="RebuildChoices"/> updates <see cref="GroupChoices"/> IN PLACE and never clears it,
/// and rederives both pickers' rows from the groups the reader chose. Clearing a bound option list
/// while a two-way <c>SelectedItem</c> is live is the Phase 15B defect the Library tab's Scan
/// interval select and the rig picker both paid for, which <c>SharedFilterViewModel</c>'s own
/// constructor records; writing in place is not on its own enough, because a REPLACE at the
/// selected index makes the control write null back just as a clear does. That method says what
/// holds the two groups instead.
/// </para>
/// </remarks>
public sealed partial class CompareTabViewModel : AnalysisTabViewModel<CompareResult>
{
    /// <summary>Spec 12.14's States table, "Compare with fewer than two groups chosen, or the same
    /// group twice", verbatim and word for word with <c>CompareTab.tsx</c> line 34. It is the
    /// tab's status line while <see cref="CanQuery"/> is false, and it is NOT the base's generic
    /// no-frames sentence, which stays reserved for the state it names.</summary>
    public const string ChooseGroupsText = "Select two different groups to compare.";

    /// <summary>The unit <c>AnalysisQuery.Compare</c> hands <c>Analysis.CompareVerdict</c> on its
    /// arcsecond branch, with the leading space that is part of the string. Spelled here because
    /// the sentence is rebuilt with the reader's own two names; see <see cref="Sentence"/>.
    /// </summary>
    private const string ArcsecUnit = " (arcsec)";

    /// <summary>Spec 12.14's one not-comparable sentence, verbatim, drawn in the secondary ink.
    /// The web's second not-comparable sentence, the one that reads the two arcsecond medians, is
    /// unreachable and is deliberately not built.</summary>
    public const string NotComparableText =
        "Different optical trains: pixel HFR is not comparable between these groups, and arcsecond "
        + "data is unavailable (plate scale unknown). No improvement figure is shown.";

    /// <summary>The metric picker's metrics, the ten <c>AnalysisMetrics.Y</c>.</summary>
    public static readonly IReadOnlyList<AnalysisMetric> Metrics = AnalysisMetrics.Y;

    private readonly Func<AnalysisMetric, CompareMode, string, string, DateOnly?, DateOnly?, CompareResult> _query;
    private readonly ObservableCollection<EquipmentChoice> _equipment;
    private readonly ObservableCollection<string> _filters;

    private CompareMode _mode = CompareMode.Equipment;
    private AnalysisMetric _metric = AnalysisMetric.Hfr;
    private CompareGroupChoice? _selectedGroupA;
    private CompareGroupChoice? _selectedGroupB;

    // The two groups the READER chose, held as their wire values and written only by the two
    // selection setters and by the segment. A rebuild never writes them: see RebuildChoices.
    private string _wantedA = string.Empty;
    private string _wantedB = string.Empty;
    private bool _rebuilding;

    // The two names the result is DRAWN with, taken in Query beside the wire values of that same
    // instant. Every user-visible place reads these; the wire value stays the identity and the
    // query argument and reaches no reader.
    private string _displayA = string.Empty;
    private string _displayB = string.Empty;

    private string _verdict = string.Empty;
    private bool _isNotComparable;
    private StatsCardViewModel _cardA = new(null);
    private StatsCardViewModel _cardB = new(null);
    private string _tooFew = string.Empty;

    /// <param name="filter">The shared bar's current filter. Compare reads only its two dates
    /// (<c>core-shapes.md</c> section 5.3), because its two groups ARE the equipment or filter
    /// selection and applying the bar's selection on top of them would let a reader compare two
    /// groups the bar had already emptied.</param>
    /// <param name="query">Normally <c>AnalysisCache.Compare</c>.</param>
    /// <param name="equipment">The filter bar's own equipment list, normally
    /// <c>SharedFilterViewModel.EquipmentChoices</c>. The INSTANCE the bar fills in place, not a
    /// snapshot: the bar's one list read is asynchronous and lands after the page is built, so a
    /// copy taken here would leave both pickers empty for the life of the page.</param>
    /// <param name="filters">The filter bar's own filter list, normally
    /// <c>SharedFilterViewModel.FilterChoices</c>, under the same rule.</param>
    /// <param name="post">The post seam.</param>
    /// <param name="logger">Where a failed query is recorded.</param>
    public CompareTabViewModel(
        Func<AnalysisFilter?> filter,
        Func<AnalysisMetric, CompareMode, string, string, DateOnly?, DateOnly?, CompareResult> query,
        ObservableCollection<EquipmentChoice> equipment,
        ObservableCollection<string> filters,
        Action<Action>? post = null,
        ILogger? logger = null)
        : base(AnalysisDisplay.CompareTab, "Compare", "analysis.compare", filter, post, logger)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(filters);

        _query = query;
        _equipment = equipment;
        _filters = filters;

        _equipment.CollectionChanged += OnSourceChanged;
        _filters.CollectionChanged += OnSourceChanged;
        RebuildChoices();
    }

    /// <summary>The one box plot, holding both groups' boxes as two categories in one series
    /// (spec 13). The same type the Distributions tab draws N groups through, fed two.</summary>
    public BoxPlotChartViewModel Chart { get; } = new();

    /// <summary>Both pickers' options, rebuilt from the filter bar's own lists whenever the
    /// segment moves or those lists fill. The bar's two sentinel rows, "All equipment" and "All
    /// filters", are excluded: they are a scope and not a group, and offering one
    /// would send an empty or sentinel string into the query.</summary>
    public ObservableCollection<CompareGroupChoice> GroupChoices { get; } = [];

    /// <summary>The metric picker's entries, in <see cref="Metrics"/> order. Every label is
    /// <see cref="AnalysisMetricLabels"/>'s and none is spelled here. One stable list,
    /// never rebuilt (Phase 15B).</summary>
    public static IReadOnlyList<AnalysisMetricChoice> MetricChoices { get; } =
        AnalysisMetricLabels.Choices(Metrics);

    /// <summary>Equipment or Filter. <b>Switching the segment clears both groups</b> (spec 12.14),
    /// so an equipment name can never be queried against the filter mode.</summary>
    public CompareMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEquipmentMode));
            OnPropertyChanged(nameof(IsFilterMode));
            OnPropertyChanged(nameof(GroupLabel));

            _selectedGroupA = null;
            _selectedGroupB = null;
            _wantedA = string.Empty;
            _wantedB = string.Empty;
            GroupA = string.Empty;
            GroupB = string.Empty;
            OnPropertyChanged(nameof(SelectedGroupA));
            OnPropertyChanged(nameof(SelectedGroupB));

            RebuildChoices();
            GroupsChanged();
        }
    }

    /// <summary>Whether the Equipment half of the segment is the current one.</summary>
    public bool IsEquipmentMode => _mode == CompareMode.Equipment;

    /// <summary>Whether the Filter half of the segment is the current one.</summary>
    public bool IsFilterMode => _mode == CompareMode.Filter;

    /// <summary>What the two pickers are captioned with, which is the segment's own word.</summary>
    public string GroupLabel => IsEquipmentMode ? "Equipment" : "Filter";

    /// <summary>The chosen metric, the ten Y candidates offered.</summary>
    public AnalysisMetric Metric
    {
        get => _metric;
        set
        {
            if (_metric == value)
            {
                return;
            }

            _metric = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedMetricChoice));
            Refresh();
        }
    }

    /// <summary>The metric picker's two-way selection, matched on the metric the entry carries and
    /// never on its label or its position. A cleared selection moves nothing: raising the property
    /// restores the entry the picker had.</summary>
    public AnalysisMetricChoice? SelectedMetricChoice
    {
        get => MetricChoices.FirstOrDefault(choice => choice.Metric == _metric);
        set => AnalysisMetricChoice.Apply(
            value,
            _metric,
            metric => Metric = metric,
            () => OnPropertyChanged(nameof(SelectedMetricChoice)));
    }

    /// <summary>The first group's picker selection, or null while nothing is chosen.</summary>
    /// <remarks>The refresh is suppressed across <see cref="RebuildChoices"/>, which raises one at
    /// the end and only where a group really moved. Without that, the control's null write-back
    /// queries a half-applied option list and putting the selection back queries a second
    /// time.</remarks>
    public CompareGroupChoice? SelectedGroupA
    {
        get => _selectedGroupA;
        set
        {
            if (ReferenceEquals(_selectedGroupA, value))
            {
                return;
            }

            _selectedGroupA = value;
            GroupA = value?.Value ?? string.Empty;
            OnPropertyChanged();

            if (_rebuilding)
            {
                return;
            }

            _wantedA = GroupA;
            GroupsChanged();
        }
    }

    /// <summary>The second group's picker selection, or null while nothing is chosen.</summary>
    /// <remarks>Under the same suppression as <see cref="SelectedGroupA"/>.</remarks>
    public CompareGroupChoice? SelectedGroupB
    {
        get => _selectedGroupB;
        set
        {
            if (ReferenceEquals(_selectedGroupB, value))
            {
                return;
            }

            _selectedGroupB = value;
            GroupB = value?.Value ?? string.Empty;
            OnPropertyChanged();

            if (_rebuilding)
            {
                return;
            }

            _wantedB = GroupB;
            GroupsChanged();
        }
    }

    /// <summary>The first group's wire value, empty until a picker chooses one.</summary>
    public string GroupA { get; private set; } = string.Empty;

    /// <summary>The second group's wire value, empty until a picker chooses one.</summary>
    public string GroupB { get; private set; } = string.Empty;

    /// <summary>The verdict sentence, built by <c>Analysis.CompareVerdict</c> over the query's own
    /// figures and the two names the reader chose. Nothing here re-derives it, re-formats its
    /// percentage or computes a median: the arithmetic is that one function's and the figures are
    /// the query's. See <see cref="Sentence"/>.</summary>
    public string Verdict
    {
        get => _verdict;
        private set => SetProperty(ref _verdict, value);
    }

    /// <summary>Whether spec 12.14's not-comparable sentence is drawn in place of a verdict.
    /// </summary>
    public bool IsNotComparable
    {
        get => _isNotComparable;
        private set => SetProperty(ref _isNotComparable, value);
    }

    /// <summary>The first group's stats card, labelled with its own group name.</summary>
    public StatsCardViewModel CardA
    {
        get => _cardA;
        private set => SetProperty(ref _cardA, value);
    }

    /// <summary>The second group's stats card, labelled with its own group name.</summary>
    public StatsCardViewModel CardB
    {
        get => _cardB;
        private set => SetProperty(ref _cardB, value);
    }

    /// <summary>Moves the segment. The parameter is the half that was pressed.</summary>
    [RelayCommand]
    private void SelectMode(CompareMode mode) => Mode = mode;

    /// <summary>Spec 12.14: Compare is not in the mixed plate scale list at all.</summary>
    protected override bool IsPixelMetric => false;

    /// <inheritdoc/>
    /// <remarks>Which group is short is the published state's answer and never a
    /// count compared here, so a threshold that moves in the query cannot leave a second copy
    /// behind.</remarks>
    protected override string TooFewMessage => _tooFew;

    /// <summary>
    /// The tab's sentence. While <see cref="CanQuery"/> is false it is spec 12.14's own
    /// "Select two different groups to compare." and NOT the base's generic no-frames sentence,
    /// which the landed shell published by putting the tab into
    /// <see cref="AnalysisTabState.Empty"/> alone.
    /// </summary>
    /// <remarks>The <see cref="AnalysisTabState.NotLoaded"/> term is the base's reversed-range
    /// sentence winning on a first selection: a tab selected for the first time while the
    /// date range is reversed never reaches a query at all, so the base's
    /// <see cref="AnalysisTabViewModel.ReversedRangeText"/> is the sentence that names why, and
    /// this tab's own sentence takes over as soon as the range is legal and the load loop has
    /// published a state.</remarks>
    public override string StatusLine =>
        CanQuery || State == AnalysisTabState.NotLoaded ? base.StatusLine : ChooseGroupsText;

    /// <inheritdoc/>
    protected override bool CanQuery =>
        GroupA.Length > 0 && GroupB.Length > 0 && !string.Equals(GroupA, GroupB, StringComparison.Ordinal);

    /// <inheritdoc/>
    /// <remarks>The state alone; <see cref="StatusLine"/> supplies the sentence, so no other state
    /// has to borrow the too-few slot to carry it.</remarks>
    protected override void PublishCannotQuery() => State = AnalysisTabState.Empty;

    /// <inheritdoc/>
    protected override object CreateBody() => new();

    /// <inheritdoc/>
    /// <remarks>
    /// The two names the result will be drawn with are taken HERE, beside the two wire values of
    /// the same instant, so a drawn result's figures and its two names always come from one
    /// choice. The query echoes back the names it is given, which are the wire values, so the map
    /// from a key to the name the reader chose is this tab's and nothing downstream can make it.
    /// <para>
    /// It matters that this is not re-read when the result lands.
    /// <c>AnalysisTabViewModel.Refresh</c> returns through <c>PublishCannotQuery</c> without
    /// bumping its generation, so a bar re-read that withdraws a chosen group while that group's
    /// query is in flight leaves the in-flight result to publish and draw. Read from the live
    /// selection at that moment it would find no choice at all and print nothing where a group's
    /// name belongs; read from here it prints the name the reader chose when the query went out.
    /// </para>
    /// <para>
    /// The fallback is unreachable and is kept only so the expression is total:
    /// <see cref="GroupA"/> is written on the same two lines as the field behind
    /// <see cref="SelectedGroupA"/>, so <see cref="CanQuery"/> already implies a chosen row.
    /// </para>
    /// </remarks>
    protected override CompareResult? Query(AnalysisFilter filter)
    {
        _displayA = _selectedGroupA?.Display ?? GroupA;
        _displayB = _selectedGroupB?.Display ?? GroupB;
        return _query(_metric, _mode, GroupA, GroupB, filter.From, filter.To);
    }

    /// <inheritdoc/>
    protected override AnalysisTabState Map(CompareResult? result)
    {
        if (result is null || result.State == CompareState.NoRows)
        {
            Draw(null);
            return AnalysisTabState.Empty;
        }

        if (result.State != CompareState.Ok)
        {
            Draw(null);
            _tooFew = ShortText(result, _displayA, _displayB);
            return AnalysisTabState.TooFew;
        }

        Draw(result);
        return AnalysisTabState.Ready;
    }

    /// <summary>Disposes the box plot, which drops its static theme subscription, and lets the two
    /// option lists go.</summary>
    public override void Dispose()
    {
        base.Dispose();
        _equipment.CollectionChanged -= OnSourceChanged;
        _filters.CollectionChanged -= OnSourceChanged;
        Chart.Dispose();
    }

    // The seam's three short sentences. The group's own NAME reaches the sentence, not
    // merely the letter, and the raw counts are the result's. No threshold is written here.
    // The two names are the reader's own, not the result's echoed wire values: the query is given
    // the key and can answer nothing else.
    private static string ShortText(CompareResult result, string nameA, string nameB)
        => result.State switch
        {
            CompareState.GroupAShort => string.Create(
                CultureInfo.InvariantCulture,
                $"Group A, {nameA}, has too few frames to compare ({result.CountA} values)."),
            CompareState.GroupBShort => string.Create(
                CultureInfo.InvariantCulture,
                $"Group B, {nameB}, has too few frames to compare ({result.CountB} values)."),
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"Both groups have too few frames to compare: {nameA} ({result.CountA} values) "
                + $"and {nameB} ({result.CountB} values)."),
        };

    private void Draw(CompareResult? result)
    {
        if (result?.GroupA is not { } a || result.GroupB is not { } b)
        {
            Chart.Clear();
            CardA = new StatsCardViewModel(null);
            CardB = new StatsCardViewModel(null);
            Verdict = string.Empty;
            IsNotComparable = false;
            return;
        }

        // The box's own GroupName is the query's grouping key and the result's published name is
        // that same key echoed back, so both category labels, both card headings and the sentence
        // below take the name the READER chose instead.
        Chart.Show(
            [a.Box with { GroupName = _displayA }, b.Box with { GroupName = _displayB }],
            _metric);

        var unit = AnalysisMetricLabels.Unit(_metric);
        CardA = new StatsCardViewModel(a.Stats, _displayA, unit);
        CardB = new StatsCardViewModel(b.Stats, _displayB, unit);

        Verdict = Sentence(result, a, b);
        IsNotComparable = !result.Comparable;
    }

    /// <summary>
    /// The verdict, rebuilt through <c>Analysis.CompareVerdict</c>, the ONE sentence
    /// builder <c>AnalysisQuery.Compare</c> itself calls, with the two names the reader chose in
    /// place of the two wire values that query was given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is re-derived and no percentage is formatted here: the wording, the three branches,
    /// the <c>(N=a vs N=b)</c> tail, user ruling U2's denominator and the half-to-even rounding all
    /// stay inside that one function. A substitution on the built sentence is not available
    /// either, and would be wrong: a canonical telescope or camera name may hold any text at all.
    /// </para>
    /// <para>
    /// The figures are the query's own, bit for bit and not a rounded copy, which is what makes
    /// the rebuilt sentence differ from the query's in the two names and in nothing else.
    /// <c>AnalysisQuery.Compare</c> passes the same <c>medianArcsecA</c> and <c>medianArcsecB</c>
    /// locals it publishes as <see cref="CompareResult.MedianArcsecA"/> and
    /// <see cref="CompareResult.MedianArcsecB"/>, already rounded to three places by
    /// <c>PythonNumerics.RoundLikePython</c>, and on its other branch the same
    /// <c>boxA</c> and <c>boxB</c> it publishes as <see cref="CompareResult.GroupA"/> and
    /// <see cref="CompareResult.GroupB"/>; both branches take their two counts from those same
    /// summaries and never from <see cref="CompareResult.CountA"/>.
    /// </para>
    /// <para>
    /// Which of the two branches ran is read off the result rather than recomputed from the
    /// metric: the query publishes both arcsecond medians exactly on the branch that also passes
    /// <see cref="ArcsecUnit"/>, and leaves both null on the other, which is the property the
    /// result's own summary records.
    /// </para>
    /// </remarks>
    private string Sentence(CompareResult result, CompareGroup a, CompareGroup b)
    {
        if (result.Verdict is null)
        {
            return string.Empty;
        }

        // Named in full: the file's own namespace ends in Analysis, so the bare name is that
        // namespace and not the Core type.
        return result.MedianArcsecA is { } arcsecA && result.MedianArcsecB is { } arcsecB
            ? GalactiLog.Core.Metrics.Analysis.CompareVerdict(
                _displayA, _displayB, arcsecA, arcsecB, a.Stats.Count, b.Stats.Count, ArcsecUnit)
            : GalactiLog.Core.Metrics.Analysis.CompareVerdict(
                _displayA, _displayB, a.Stats.Median, b.Stats.Median,
                a.Stats.Count, b.Stats.Count, string.Empty);
    }

    private void GroupsChanged()
    {
        OnPropertyChanged(nameof(GroupA));
        OnPropertyChanged(nameof(GroupB));

        // CanQuery is what StatusLine switches on, and a change that leaves State alone (two
        // unqueryable selections in a row) raises nothing of its own.
        OnPropertyChanged(nameof(StatusLine));
        Refresh();
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildChoices();

    /// <summary>
    /// Brings <see cref="GroupChoices"/> up to date with the bar's list for the current mode,
    /// IN PLACE and without ever clearing it, keeping each picker on the group the reader chose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both <c>ComboBox</c>es take this collection as their <c>ItemsSource</c> with a live two-way
    /// <c>SelectedItem</c>, and a clear makes the control write null back through that binding,
    /// which is the Phase 15B defect <c>SharedFilterViewModel</c>'s constructor records. An entry
    /// that has not changed is not written at all, so a rebuild that answers what the collection
    /// already holds raises nothing.
    /// </para>
    /// <para>
    /// Writing in place is not on its own enough, and this is the same shape
    /// <c>SharedFilterViewModel.Publish</c> carries, for the same measured reason. When the row at
    /// a bound <c>ComboBox</c>'s selected index is REPLACED, Avalonia writes null back through the
    /// two-way binding synchronously, inside the loop below, and does not write the replacement
    /// back. The page re-reads the bar's lists after a scan, an alias save, a general save and a
    /// completed correlation pass, and the equipment list is ordered by frame count first, so a
    /// scan can carry one rig past another with no row added and none removed; a new filter name
    /// sorts into the middle of the filter list. The apply never inserts, so either shape reaches
    /// the reader's own index as a replace. Measured on the shown page with both groups chosen
    /// through the controls: the group whose row moved was left unchosen, its picker reading no
    /// row at all, and the tab fell out of its result into
    /// <see cref="ChooseGroupsText"/>.
    /// </para>
    /// <para>
    /// So each picker's row is REDERIVED here from the group's IDENTITY, which is the group's wire
    /// value: the <c>(telescope, camera)</c> pair in
    /// <see cref="CompareGroups.EquipmentSeparator"/> encoding, or the filter's own name. Never the
    /// whole record: <see cref="CompareGroupChoice"/> is a record, so value equality also withdraws
    /// the reader's row when an alias save flips <see cref="CompareGroupChoice.Grouped"/> alone and
    /// leaves the wire value standing.
    /// </para>
    /// <para>
    /// The identity is held in a field of its own, written only by the two selection setters and by
    /// the segment, and NOT re-read from the current selection here. It has to be, because this
    /// method does not run once per bar re-read: the bar applies its own answer row by row to the
    /// collections below, and every one of those writes raises
    /// <see cref="OnSourceChanged"/>, so this runs once per moved row over a HALF-APPLIED bar list
    /// in which the reader's own row can be temporarily absent. Re-reading the identity from the
    /// selection would take that absence for the answer and lose the reader's group for good, which
    /// is what an insert ahead of both groups and a rotation both produced.
    /// </para>
    /// <para>
    /// A group whose identity no longer names an offered row clears to unchosen, which empties
    /// <see cref="GroupA"/> or <see cref="GroupB"/> so nothing is queried for a group the library
    /// does not hold, and leaves the OTHER group exactly where it was. The identity itself is kept,
    /// so a rig that comes back on a later scan brings its group back with it; while it is away the
    /// tab shows <see cref="ChooseGroupsText"/>.
    /// </para>
    /// <para>
    /// Refreshing is suppressed across the rebuild and done once at the end, and only where a
    /// group's wire value really moved. Without that the control's null write-back refreshes the
    /// tab from inside the loop, on a half-applied option list, and putting the selection back
    /// refreshes it a second time. <c>PropertyChanged</c> is still raised throughout, which is what
    /// moves the control back.
    /// </para>
    /// </remarks>
    private void RebuildChoices()
    {
        var desired = Desired();
        var valueA = GroupA;
        var valueB = GroupB;

        _rebuilding = true;
        try
        {
            for (var index = 0; index < desired.Count; index++)
            {
                if (index >= GroupChoices.Count)
                {
                    GroupChoices.Add(desired[index]);
                }
                else if (GroupChoices[index] != desired[index])
                {
                    GroupChoices[index] = desired[index];
                }
            }

            while (GroupChoices.Count > desired.Count)
            {
                GroupChoices.RemoveAt(GroupChoices.Count - 1);
            }

            // An empty identity matches no row, which is exactly the answer the segment wants: it
            // clears both groups above this call and both must stay cleared.
            SelectedGroupA = GroupChoices.FirstOrDefault(choice => Same(choice.Value, _wantedA));
            SelectedGroupB = GroupChoices.FirstOrDefault(choice => Same(choice.Value, _wantedB));
        }
        finally
        {
            _rebuilding = false;
        }

        // One refresh for the whole rebuild, and none at all for the ordinary re-read in which both
        // chosen groups are still offered, wherever they now sit.
        if (!Same(GroupA, valueA) || !Same(GroupB, valueB))
        {
            GroupsChanged();
        }
    }

    // Ordinal throughout, because that is what AnalysisQuery.Compare matches the stored names with.
    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

    private List<CompareGroupChoice> Desired()
    {
        var desired = new List<CompareGroupChoice>();

        if (IsEquipmentMode)
        {
            foreach (var choice in _equipment)
            {
                // The "All equipment" row carries two nulls, which is exactly the sentinel the
                // query excludes: it is a scope and not a group.
                if (choice.Telescope is not { } telescope || choice.Camera is not { } camera)
                {
                    continue;
                }

                desired.Add(new CompareGroupChoice(
                    choice.Label,
                    telescope + CompareGroups.EquipmentSeparator + camera,
                    choice.Grouped));
            }

            return desired;
        }

        foreach (var filter in _filters)
        {
            if (string.Equals(filter, SharedFilterViewModel.AllFiltersLabel, StringComparison.Ordinal))
            {
                continue;
            }

            desired.Add(new CompareGroupChoice(filter, filter, Grouped: false));
        }

        return desired;
    }
}
