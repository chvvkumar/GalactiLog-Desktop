using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Stats;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Analysis;

/// <summary>
/// One entry of the equipment combination picker: the "All equipment" row, or one
/// <c>(telescope, camera)</c> pair carrying the grouped marker (spec 12.14's filter bar table).
/// </summary>
/// <remarks>
/// The two names are null on the "All equipment" row and non-null on every other, which is exactly
/// what <see cref="AnalysisFilter"/> wants: a null telescope or camera applies no predicate.
/// </remarks>
/// <param name="Label">"All equipment", or <c>"&lt;telescope&gt; + &lt;camera&gt;"</c>.</param>
/// <param name="Grouped">Whether either name folds more than one raw name, which is what
/// <see cref="EquipmentCombination.Grouped"/> already carries.</param>
/// <param name="Telescope">The canonical telescope, or null on the "All equipment" row.</param>
/// <param name="Camera">The canonical camera, or null on the "All equipment" row.</param>
public sealed record EquipmentChoice(string Label, bool Grouped, string? Telescope, string? Camera);

/// <summary>
/// Spec 12.14's shared filter bar: the equipment combination, the filter, the granularity segment
/// and the two dates, plus the reversed range rule that stops every tab querying.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Current"/> is the whole of the "no query on any tab" row of spec 12.14's States
/// table: it answers null exactly while the range is reversed, the tab base asks for it on every
/// refresh, and a null there returns before any query is issued. The rule lives here and not only
/// in the markup, because the error sentence is a view concern and the refusal to query is not.
/// </para>
/// <para>
/// <b>This is a DEPARTURE from the web, not parity.</b> <c>AnalysisPage.tsx</c> lines 83 to 90
/// build their shared filter with <c>dateFrom: dateRangeError() ? undefined : dateFrom()</c> and
/// the tabs' queries still fire, with both dates cleared, so the web silently widens the range to
/// the whole library while the error sentence is on screen. Spec 12.14 says "runs nothing" and is
/// approved, so the port refuses the query instead.
/// </para>
/// <para>
/// Both lists are read on <see cref="Load"/> and re-read on <see cref="Reload"/>, which the page
/// calls on every event that rewrites the data behind it: a completed scan, an alias source save, a
/// general save and a completed correlation pass. Those are the four that drop
/// <c>AnalysisCache</c>, so there is no event left that can move a row in either list without this
/// bar hearing of it.
/// </para>
/// <para>
/// The read is NOT started in this constructor. <see cref="AnalysisViewModel"/> builds its five
/// tabs out of the two collections below, and <c>CompareTabViewModel</c>'s own constructor
/// enumerates them; a read already in flight can publish into them from its thread-pool thread
/// while that enumeration is running, which is a "Collection was modified" throw out of a page
/// constructor. Under <c>UiPost.Default</c> the publish is queued behind the UI thread and the
/// throw is unreachable, but that is thread luck rather than a rule, and under an inline seam it
/// is reachable and was seen. <see cref="Load"/> is the rule: the owner starts the read once
/// everything that reads the two collections exists.
/// </para>
/// </remarks>
public sealed partial class SharedFilterViewModel : ObservableObject
{
    /// <summary>Spec 12.14's reversed range sentence, verbatim.</summary>
    public const string RangeErrorText = "From date must be on or before To date.";

    /// <summary>The equipment picker's first row, which applies no equipment predicate.</summary>
    public const string AllEquipmentLabel = "All equipment";

    /// <summary>The filter picker's first row, which applies no filter predicate.</summary>
    public const string AllFiltersLabel = "All filters";

    /// <summary>The grouped marker's glyph, the literal the Statistics equipment rows already draw
    /// in <c>ColorTextTertiary</c> (spec 12.5, <c>StatisticsView.axaml</c>).</summary>
    public const string GroupedMarker = "(grouped)";

    /// <summary>The grouped marker's hover text. The one declaration is
    /// <see cref="EquipmentComboRowViewModel.GroupedTooltip"/>; this is an alias so the markup can
    /// bind it without naming the Statistics page's row type, and never a second copy.</summary>
    public const string GroupedTooltip = EquipmentComboRowViewModel.GroupedTooltip;

    private readonly Func<IReadOnlyList<EquipmentCombination>> _loadEquipment;
    private readonly Func<IReadOnlyList<string>> _loadFilters;
    private readonly Action<Action> _post;
    private readonly Action<AnalysisGranularity>? _writeGranularity;
    private readonly ILogger _logger;
    private bool _loadStarted;
    private bool _readInFlight;
    private bool _reloadPending;
    private bool _publishing;

    /// <remarks>Constructing the bar reads nothing and starts no task. <see cref="Load"/> does
    /// both, and the owner calls it once everything that reads the two collections exists.</remarks>
    /// <param name="loadEquipment">Normally <c>AnalysisCache.EquipmentCombinations</c>. A delegate
    /// and never the cache, which is sealed with no interface (ruling P1-1).</param>
    /// <param name="loadFilters">Normally <c>AnalysisCache.Filters</c>.</param>
    /// <param name="granularity">The stored <c>display.analysis.granularity</c>, already read
    /// through <see cref="AnalysisDisplay.ParseGranularity"/>.</param>
    /// <param name="post">Ruling A8's post seam, normally <c>UiPost.Default</c>.</param>
    /// <param name="writeGranularity">Writes <c>display.analysis.granularity</c> when the segment
    /// moves, normally <c>g =&gt; AnalysisDisplay.WriteGranularity(writeDisplay, g)</c>. It sits
    /// here rather than on the page because the segment this bar owns is the only thing that moves
    /// the value, and routing it back through a property notification would be a subscription and a
    /// disposal arm for one assignment.</param>
    /// <param name="logger">Where a failed list read is recorded. Null logs nothing.</param>
    public SharedFilterViewModel(
        Func<IReadOnlyList<EquipmentCombination>> loadEquipment,
        Func<IReadOnlyList<string>> loadFilters,
        AnalysisGranularity granularity = AnalysisGranularity.Frame,
        Action<Action>? post = null,
        Action<AnalysisGranularity>? writeGranularity = null,
        ILogger? logger = null)
    {
        _loadEquipment = loadEquipment;
        _loadFilters = loadFilters;
        _post = post ?? UiPost.Default;
        _writeGranularity = writeGranularity;
        _logger = logger ?? NullLogger.Instance;
        _granularity = granularity;

        // Both collections are created here and never replaced. Rebuilding a bound option list
        // while a two-way SelectedItem binding is live renders the control empty, which is the
        // Phase 15B lesson the Library tab's Scan interval select and the rig picker both paid for;
        // Load fills these instances in place and reassigns the selection afterwards.
        EquipmentChoices = [AllEquipment];
        FilterChoices = [AllFiltersLabel];
        _selectedEquipment = AllEquipment;
        _selectedFilter = AllFiltersLabel;
    }

    /// <summary>
    /// Starts the one list read: off the UI thread, published back through the post seam
    /// (ruling A8). Idempotent, so a second call reads nothing and leaves
    /// <see cref="PendingLoad"/> naming the first read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from the constructor on purpose, and the class remarks say why: the owner must be
    /// able to finish building everything that reads <see cref="EquipmentChoices"/> and
    /// <see cref="FilterChoices"/> before a publish can touch them.
    /// <see cref="AnalysisViewModel"/> calls this as the last statement of its own constructor; a
    /// bar built on its own calls it itself.
    /// </para>
    /// <para>
    /// Both delegates reach SQLite through <c>AnalysisCache</c>, so the failure arm is the tab
    /// base's own: log it and leave the bar holding its two "All" rows,
    /// which are already there. Spec 12.14's States table gives no page state for a failed bar
    /// load and none is invented here; without this arm a database error published nothing, logged
    /// nothing and left <see cref="PendingLoad"/> faulted with nobody observing it.
    /// </para>
    /// </remarks>
    public void Load()
    {
        if (_loadStarted)
        {
            return;
        }

        _loadStarted = true;
        Read();
    }

    /// <summary>
    /// Re-reads both lists, off the UI thread and published back through the post seam, and applies
    /// the answer to the two collection INSTANCES in place. Called by the owner when the host says
    /// the derived data behind the page has been rewritten. A no-op until <see cref="Load"/> has
    /// started the first read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without it the bar's two lists were not merely stale but unrecoverable: an alias group
    /// renamed, a PHD2 profile mapped or a rescan with a second rig left the equipment picker, the
    /// filter picker and the Compare tab's two group pickers showing the rows this process first
    /// read, for the life of the process, with no control on the page able to move them.
    /// </para>
    /// <para>
    /// Coalesced, the shape and the reason <c>StatisticsViewModel.OnDerivedDataChanged</c> gives:
    /// a call arriving while a read is in flight is owed one further read rather than dropped,
    /// because the read in flight may have taken its answer from the memo that call's own handler
    /// had not yet dropped. A burst therefore costs one further read and not one per notification.
    /// </para>
    /// </remarks>
    public void Reload()
    {
        // A reload before the first read has nothing to re-read: both lists still hold exactly what
        // that read will publish. Returning here rather than starting the read leaves Load, which
        // the owner calls as the last statement of its own constructor, owning the ordering even
        // when a notification lands between the owner's subscription and that line.
        if (!_loadStarted)
        {
            return;
        }

        if (_readInFlight)
        {
            _reloadPending = true;
            return;
        }

        Read();
    }

    private void Read()
    {
        _readInFlight = true;
        PendingLoad = Task.Run(() =>
        {
            IReadOnlyList<EquipmentCombination> equipment;
            IReadOnlyList<string> filters;
            try
            {
                equipment = _loadEquipment();
                filters = _loadFilters();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The Analysis filter bar's option lists could not be read");

                // The failure arm still finishes the read: a database error that left the flag
                // standing would refuse every later reload for the life of the page.
                _post(Drain);
                return;
            }

            _post(() =>
            {
                Publish(equipment, filters);
                Drain();
            });
        });
    }

    // Runs on the UI thread through the post seam, like every other write here, so the two flags
    // are set and read on one thread.
    private void Drain()
    {
        _readInFlight = false;
        if (!_reloadPending)
        {
            return;
        }

        _reloadPending = false;
        Read();
    }

    /// <summary>The "All equipment" row, one instance so a selection reset compares by reference.
    /// </summary>
    public static readonly EquipmentChoice AllEquipment = new(AllEquipmentLabel, false, null, null);

    /// <summary>Raised whenever any of the five controls moves, including a reversed range becoming
    /// legal again. The page marks every tab stale and refreshes the selected one.</summary>
    public event EventHandler? Changed;

    /// <summary>The tail of the one list read, so a case can await it instead of sleeping. A
    /// completed task until <see cref="Load"/> has been called, never null, so every existing await
    /// holds whether or not the read has been started.</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <summary>"All equipment" plus one row per <c>(telescope, camera)</c> pair.</summary>
    public ObservableCollection<EquipmentChoice> EquipmentChoices { get; }

    /// <summary>"All filters" plus every distinct stored <c>filter_used</c>, ordinally ascending.
    /// </summary>
    public ObservableCollection<string> FilterChoices { get; }

    private EquipmentChoice _selectedEquipment;

    /// <summary>The chosen equipment combination. Never null: a picker that writes null through a
    /// two-way binding falls back to "All equipment", the same shape
    /// <c>MainWindowViewModel.Selected</c> uses for the rail.</summary>
    public EquipmentChoice SelectedEquipment
    {
        get => _selectedEquipment;
        set
        {
            if (SetProperty(ref _selectedEquipment, value ?? AllEquipment) && !_publishing)
            {
                Raise();
            }
        }
    }

    private string _selectedFilter;

    /// <summary>The chosen filter, or <see cref="AllFiltersLabel"/>.</summary>
    public string SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value ?? AllFiltersLabel) && !_publishing)
            {
                Raise();
            }
        }
    }

    private AnalysisGranularity _granularity;

    /// <summary>Per Frame or Per Session. The one control on this bar that persists (spec 12.14's
    /// Persistence subsection).</summary>
    public AnalysisGranularity Granularity
    {
        get => _granularity;
        set
        {
            if (!SetProperty(ref _granularity, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsPerFrame));
            OnPropertyChanged(nameof(IsPerSession));
            _writeGranularity?.Invoke(value);
            Raise();
        }
    }

    /// <summary>Whether the Per Frame half of the segment is the current one.</summary>
    public bool IsPerFrame => Granularity == AnalysisGranularity.Frame;

    /// <summary>Whether the Per Session half of the segment is the current one.</summary>
    public bool IsPerSession => Granularity == AnalysisGranularity.Session;

    /// <summary>Moves the granularity segment. The parameter is the half that was pressed.</summary>
    [RelayCommand]
    private void SelectGranularity(AnalysisGranularity granularity) => Granularity = granularity;

    private DateTimeOffset? _dateFrom;

    /// <summary>The inclusive lower bound on <c>images.session_date</c>, or null. Never stored
    /// (spec 12.14: a date range is a question, not a preference).</summary>
    public DateTimeOffset? DateFrom
    {
        get => _dateFrom;
        set
        {
            if (SetProperty(ref _dateFrom, value))
            {
                RaiseRange();
            }
        }
    }

    private DateTimeOffset? _dateTo;

    /// <summary>The inclusive upper bound on <c>images.session_date</c>, or null.</summary>
    public DateTimeOffset? DateTo
    {
        get => _dateTo;
        set
        {
            if (SetProperty(ref _dateTo, value))
            {
                RaiseRange();
            }
        }
    }

    /// <summary>Clears the lower bound. A <c>DatePicker</c> has no clear gesture of its own, which
    /// is why the dashboard's date range carries the same pair of buttons.</summary>
    [RelayCommand]
    private void ClearDateFrom() => DateFrom = null;

    /// <summary>Clears the upper bound.</summary>
    [RelayCommand]
    private void ClearDateTo() => DateTo = null;

    /// <summary>The one predicate: both dates set and From later than To.</summary>
    public bool IsRangeReversed => DateFrom is { } from && DateTo is { } to && From(from) > From(to);

    /// <summary>What the markup binds the error sentence and both fields' error ink to. The same
    /// answer as <see cref="IsRangeReversed"/>, named for the view.</summary>
    public bool HasError => IsRangeReversed;

    /// <summary>
    /// The filter every tab queries with, or <b>null</b> while the range is reversed, which is what
    /// makes spec 12.14's "no query on any tab" a rule of the view-model rather than of the markup.
    /// </summary>
    /// <remarks>A range with only one end set is legal and is passed as a one-sided bound.</remarks>
    public AnalysisFilter? Current => IsRangeReversed
        ? null
        : new AnalysisFilter(
            SelectedEquipment.Telescope,
            SelectedEquipment.Camera,
            string.Equals(SelectedFilter, AllFiltersLabel, StringComparison.Ordinal) ? null : SelectedFilter,
            Granularity,
            DateFrom is { } from ? From(from) : null,
            DateTo is { } to ? From(to) : null);

    private static DateOnly From(DateTimeOffset value) => DateOnly.FromDateTime(value.Date);

    // The two lists arrive together, on the UI thread, and the collection instances are the ones
    // built in the constructor: the rows are written in place, so neither bound control is ever
    // handed a new collection. The same index-compare-and-write CompareTabViewModel.RebuildChoices
    // uses, and for its reason: clearing a bound option list under a live two-way SelectedItem
    // makes the control write null back through that binding, which is the Phase 15B defect the
    // constructor above records. A row that has not changed is not written at all, so the first
    // read raises nothing for the sentinel at index 0 and a reload that answers what the lists
    // already hold raises nothing at all.
    //
    // Each selection's IDENTITY is captured BEFORE the rows move and reassigned afterwards, rather
    // than tested for membership once the rows have moved. Measured on the shown page: when the row
    // at a ComboBox's selected index is REPLACED, Avalonia writes null back through the two-way
    // binding synchronously, inside the loop below, so by the time a membership test could run the
    // reader's choice is already the "All" row and every test passes over the loss. The re-read is
    // the one thing that moves a row: the equipment list is sorted on frame count first, so a scan
    // that catalogues frames for one rig can carry it past another, and a new filter name sorts
    // into the middle of the filter list. Apply writes in place, so either shape reaches the
    // selected index as a replace.
    //
    // Identity is the (telescope, camera) pair and not the whole record: EquipmentChoice is a
    // record, so value equality also withdraws the reader's row when an alias save flips Grouped
    // alone and leaves both canonical names standing. It is the plain string for the filter.
    //
    // Raising is suppressed across the publish and done once at the end, and only where the
    // identity really changed. Without that, the control's null write-back raises Changed from
    // inside Apply, the page marks every tab stale and requeries for a filter the reader never
    // chose, and putting the selection back raises it a second time. SetProperty still raises
    // PropertyChanged throughout, which is what moves the control back.
    private void Publish(IReadOnlyList<EquipmentCombination> equipment, IReadOnlyList<string> filters)
    {
        var equipmentRows = new List<EquipmentChoice> { AllEquipment };
        foreach (var combination in equipment)
        {
            equipmentRows.Add(new EquipmentChoice(
                $"{combination.Telescope} + {combination.Camera}",
                combination.Grouped,
                combination.Telescope,
                combination.Camera));
        }

        var filterRows = new List<string> { AllFiltersLabel };
        filterRows.AddRange(filters);

        var telescope = SelectedEquipment.Telescope;
        var camera = SelectedEquipment.Camera;
        var filter = SelectedFilter;

        _publishing = true;
        try
        {
            Apply(EquipmentChoices, equipmentRows);
            Apply(FilterChoices, filterRows);

            // Kept wherever a row still carries the identity, and the "All" row only where none
            // does. The "All equipment" row itself carries the null pair, so a reader who chose it
            // finds it again here rather than through an arm of its own.
            SelectedEquipment = EquipmentChoices.FirstOrDefault(choice => Same(choice, telescope, camera))
                ?? AllEquipment;
            SelectedFilter = FilterChoices.FirstOrDefault(name => Same(name, filter))
                ?? AllFiltersLabel;
        }
        finally
        {
            _publishing = false;
        }

        // One raise for the whole publish, and none at all for the ordinary reload in which both
        // chosen rows are still offered.
        if (!Same(SelectedEquipment, telescope, camera) || !Same(SelectedFilter, filter))
        {
            Raise();
        }
    }

    private static bool Same(EquipmentChoice choice, string? telescope, string? camera)
        => string.Equals(choice.Telescope, telescope, StringComparison.Ordinal)
            && string.Equals(choice.Camera, camera, StringComparison.Ordinal);

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);

    // Value equality throughout: EquipmentChoice is a record and the filter rows are strings.
    private static void Apply<T>(ObservableCollection<T> live, IReadOnlyList<T> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            if (index >= live.Count)
            {
                live.Add(desired[index]);
            }
            else if (!EqualityComparer<T>.Default.Equals(live[index], desired[index]))
            {
                live[index] = desired[index];
            }
        }

        while (live.Count > desired.Count)
        {
            live.RemoveAt(live.Count - 1);
        }
    }

    // A date edit moves two derived answers as well as the filter itself.
    private void RaiseRange()
    {
        OnPropertyChanged(nameof(IsRangeReversed));
        OnPropertyChanged(nameof(HasError));
        Raise();
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(Current));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
