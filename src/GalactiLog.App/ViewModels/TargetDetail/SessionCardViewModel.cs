using System.ComponentModel;
using System.Globalization;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.Core.Sessions;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;


namespace GalactiLog.App.ViewModels.TargetDetail;
/// <summary>
/// One night of spec 12.4, in two surfaces since P12 R10 retired the session accordion card: the
/// ledger row the page's night list renders from the overview it already loaded, and the session
/// pane beside it, which issues <c>SessionDetailQuery</c> once on selection and renders the merged
/// filter table, the session ranges, the night strip, the outlier pills, the frame table, the per-session
/// chart and the session notes box.
/// </summary>
/// <remarks>
/// <para>
/// The card holding the overview by value is what makes spec 12.4's "the session detail query is
/// issued when a card expands, not up front" true rather than aspirational: there is no code path
/// from construction to a query. Every collaborator arrives as a delegate, the rule
/// <c>DashboardViewModel</c> and <c>TargetDetailViewModel</c> follow, so the card constructs in a
/// unit test with lambdas and no database (spec 18.3).
/// </para>
/// <para>
/// The retired card's collapsed field set went with it. The equipment strings, the rig count and
/// its multi-rig flag, the notes indicator, the eccentricity source line, the three weather
/// <c>Has*</c> companions, the joined comparison sentence, the detail flag and the three projected
/// lists (<c>FilterMedians</c>, <c>FilterDetails</c> and <c>Insights</c>, with their three view
/// model types) were bound by no view once the pane shipped and are gone with their cases (phase
/// review P2-4, coordinator ruling (3)). The pane reads the query lists straight off
/// <see cref="Detail"/>, through <c>FilterRows</c>, <c>Ranges</c> and <c>OutlierPills</c>. Nothing is
/// kept here as a read surface.
/// </para>
/// </remarks>
public sealed partial class SessionCardViewModel : ObservableObject, IStripItem, IDisposable
{
    // Long enough for a SQLite write behind one other write, short enough that a locked database
    // is a stutter rather than a hang. The same budget TargetDetailViewModel.Dispose uses.
    private static readonly TimeSpan FlushBudget = TimeSpan.FromSeconds(2);

    private readonly string _groupKey;
    private readonly Func<string, DateOnly, SessionDetail?> _getDetail;
    private readonly Func<SessionDetail, object?> _createFrameTable;
    private readonly Func<SessionDetail, object?> _createChart;
    private readonly TimeZoneInfo _zone;
    private readonly bool _use24Hour;
    private readonly Func<string, ImmutableSolidColorBrush> _filterTint;

    // The debounce seam, kept so spec 12.15's cells take the same one the notes box does. Null in
    // the application, where AutosaveField's own IdleWindow is the window.
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay;

    // PAR-008. Null on a card built without one, which leaves every box showing its placeholder.
    private readonly Func<string, ThumbnailSlotViewModel>? _createFrameThumbnail;

    // P13 R5's live copy of display.target_page, one per process. The two section flags below are
    // pass-throughs to it, so a toggle on this night is on every other night's card and on a page
    // opened afterwards without a relaunch (phase review P2-1).
    private readonly TargetPageState _targetPage;

    // P12: the observer's site, for the night strip's astronomical-night band. Both are null until
    // Settings carries a location, which renders the strip with no band rather than no strip.
    private readonly double? _latitudeDeg;
    private readonly double? _longitudeDeg;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    // One card-lifetime source the expansion query is linked to, the shape DashboardViewModel
    // established: disposing the card cancels a read that is still in flight.
    private readonly CancellationTokenSource _lifetime = new();

    // Only the newest load may write to the bindings. The token alone is not enough: a load that
    // already finished can still be sitting in the dispatcher queue when Invalidate runs, and
    // posting it then would show a stale night.
    private int _generation;

    private bool _loading;
    private bool _disposed;

    /// <param name="overview">The collapsed field set, already computed by
    /// <c>TargetDetailQuery</c>. The card renders it without a query of its own.</param>
    /// <param name="groupKey">The page's group key, carried so the expansion query names the same
    /// group the page named. The storage form from <c>TargetHeaderBlock.GroupKey</c> (Task 1
    /// handoff), not a string rebuilt in a view model.</param>
    /// <param name="getDetail">Normally <c>SessionDetailQuery.Get</c>. Synchronous and takes a
    /// SQLite read, so it runs on the thread pool and publishes through
    /// <paramref name="post"/>.</param>
    /// <param name="saveNotes">Normally <c>TargetWriteRepository.SaveSessionNotes</c> partially
    /// applied to the target id. Null for an <c>obj:</c> group, which has no target id to key a
    /// note on; <see cref="Notes"/> is then null and the box is absent.</param>
    /// <param name="createFrameTable">Task 5's frame table, built from the loaded detail. Task 5
    /// left the seam typed <c>object?</c> so this card keeps disposing whatever a factory returns;
    /// a null return renders as a correct empty region.</param>
    /// <param name="createChart">Task 8's per-session chart, on the same seam.</param>
    /// <param name="display">The metric-group toggles (spec 5.8.2) by value, read once by AppHost
    /// on Program.Main's thread (ruling Q13). Spec 12.4 states the gate inside its Frame table
    /// paragraph, so it governs the frame table's columns and hides nothing on this card's own
    /// body (review finding 2). Kept on <see cref="Display"/> for Task 5.</param>
    /// <param name="general">Supplies <c>general.timezone</c> and <c>general.use_24h_time</c> for
    /// the first and last frame times (spec 5.8.1).</param>
    /// <param name="delay">The autosave debounce seam, passed straight through to
    /// <see cref="Notes"/>.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed expansion is logged and surfaced on the card,
    /// never rethrown on the UI thread: one failed session must not take the page down.</param>
    /// <param name="filterTint">Resolves a canonical filter name to its configured colour for
    /// the night pane's filter swatches, normally
    /// <c>ChartSelectionViewModel.FilterTint</c>, which is the same resolution the chart pills
    /// use. Optional and trailing, so no existing construction site changes; null falls back to
    /// spec 5.8.4's grey, which since P13 R2a is what <c>FilterColor.Resolve</c> reaches only after
    /// the stored colour and the seeded palette have both missed.</param>
    /// <param name="targetPage">P13 R5's live <c>display.target_page</c> holder, the one instance
    /// in the process, which is what makes a section toggle on one night visible on every other
    /// night's card and on a page opened afterwards (phase review P2-1). Optional and trailing, so
    /// no existing positional construction site changes; null gives this card a private holder
    /// seeded from <paramref name="display"/> and writing nowhere, which is what a test that is
    /// not about the sections wants.</param>
    /// <param name="getGuiding">Spec 12.4's Guiding section (Phase 15B), normally
    /// <c>Phd2NightQuery.Get</c>. Optional and trailing; null leaves <see cref="Guiding"/> null and
    /// the band absent, which is what a test that is not about guiding wants, and so does an
    /// <paramref name="anyGuideLogs"/> that answers false. Nothing calls it until the band is open
    /// on a shown card, and then with this card's own rig.</param>
    /// <param name="anyGuideLogs">Spec 12.4's "the band is drawn whenever the library holds at
    /// least one <c>phd2_logs</c> row", normally <c>Phd2NightQuery.AnyGuideLogs</c>. Called once
    /// here, in this constructor, and never per night: it is a library-wide answer that cannot
    /// change while the page is open. Optional and trailing; null hides the band.</param>
    /// <param name="getFrames">The guide graph's frames read (Phase 15B), normally
    /// <c>Phd2FramesQuery.Get</c>, handed to <see cref="Guiding"/> with this card's own zone and
    /// clock. Optional and trailing; null gives a graph that reads nothing.</param>
    /// <param name="createFrameThumbnail">Spec 12.4's per-night reference thumbnails (PAR-008):
    /// builds a slot for one frame path, normally <c>AppHost</c>'s frame-thumbnail factory with
    /// <c>ThumbnailKind.Frame</c> bound in. Optional and trailing; null leaves every box's slot
    /// null and the strip renders placeholders, which is what a test that is not about thumbnails
    /// wants.</param>
    public SessionCardViewModel(
        SessionOverview overview,
        string groupKey,
        Func<string, DateOnly, SessionDetail?> getDetail,
        Action<DateOnly, string?>? saveNotes,
        Func<SessionDetail, object?> createFrameTable,
        Func<SessionDetail, object?> createChart,
        DisplaySettings display,
        GeneralSettings general,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<Action>? post = null,
        ILogger? logger = null,
        Func<string, ImmutableSolidColorBrush>? filterTint = null,
        TargetPageState? targetPage = null,
        Func<string, ThumbnailSlotViewModel>? createFrameThumbnail = null,
        Func<DateOnly, string?, Phd2NightGuiding>? getGuiding = null,
        Func<bool>? anyGuideLogs = null,
        Func<Guid, Phd2SessionFrames?>? getFrames = null,
        IReadOnlyList<DateOnly>? nights = null,
        IReadOnlyList<SessionCardViewModel>? noteNights = null)
    {
        Overview = overview;
        Nights = nights ?? [overview.SessionDate];
        NoteNights = noteNights ?? [];
        _createFrameThumbnail = createFrameThumbnail;
        _groupKey = groupKey;
        _getDetail = getDetail;
        _createFrameTable = createFrameTable;
        _createChart = createChart;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;
        _zone = SessionTimeFormat.Resolve(general.DisplayTimezoneId);
        _use24Hour = general.Use24HTime;
        _filterTint = filterTint ?? (_ => ChartSelectionViewModel.FallbackFilterTint);
        _delay = delay;
        _targetPage = targetPage ?? new TargetPageState(display.TargetPage);
        _targetPage.PropertyChanged += OnTargetPageChanged;

        // Spec 5.8.1's observer location, off the same document the two frame times already come
        // from. The brief asked for two more constructor parameters for these; the document the
        // card is handed is where they live, so a second route to the same two numbers would be a
        // second thing to keep in step.
        _latitudeDeg = general.ObserverLatitude;
        _longitudeDeg = general.ObserverLongitude;

        // The card's identity, fixed for its lifetime: a refresh only ever brings a new overview
        // for the same night (see Refresh).
        SessionDate = overview.SessionDate;
        Overview = overview;
        Display = display;

        // The collapsed ledger strip's label (P13 R7), built beside the card's identity so the
        // strip and the ledger row cannot disagree about which night a line names. Invariant on
        // purpose: it is a fixed-width two-by-two label in a 48 px strip, not a localized date.
        ShortSessionDateText = SessionDate.ToString("MM-dd", CultureInfo.InvariantCulture);

        // Constructed eagerly so the box exists before the first expansion, holding nothing until
        // the detail arrives. One debounce for the whole application: AutosaveField (Task 3) with
        // its own one second window, never a second implementation.
        Notes = saveNotes is null
            ? null
            : new AutosaveField(text => saveNotes(SessionDate, text), delay, _post, _logger);

        // Spec 12.4's Guiding band (Phase 15B). The library-wide EXISTS runs once, here, and never
        // per night: a per-night call would put it on every ledger click for an answer that cannot
        // change while the page is open. No section is built at all on a library with no guide log,
        // so nothing on a hidden band can ever reach the night query (fixer item 34).
        HasGuidingBand = anyGuideLogs?.Invoke() ?? false;
        Guiding = getGuiding is null || !HasGuidingBand
            ? null
            : new GuidingSectionViewModel(
                SessionDate,
                getGuiding,
                _post,
                _logger,
                getFrames,
                _zone,
                _use24Hour,
                // The band is this card's rig's, and the whole night's only when this card's own
                // night carries several rigs, which is where the web stops narrowing
                // (SessionAccordionCard.tsx:1240, target_detail.py:563-571). Both members are the
                // port's own detail().equipment.telescope and isMultiRig().
                Overview.RigCount > 1 ? null : Overview.Telescope);
    }

    /// <summary>
    /// The collapsed field set's read model. Every collapsed property below is a projection of
    /// this one value, so <see cref="Refresh"/> republishes the whole set in one assignment
    /// instead of leaving the card showing numbers a scan has already superseded.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(SessionDateText),
        nameof(IntegrationText),
        nameof(FrameCount),
        nameof(FrameCountText),
        nameof(MedianHfrText),
        nameof(MedianHfrArcsecText),
        nameof(HasHfrArcsec),
        nameof(MedianEccentricityText),
        nameof(MedianFwhmText),
        nameof(MedianGuidingRmsText),
        nameof(MedianDetectedStarsText))]
    public partial SessionOverview Overview { get; private set; }

    /// <summary>The night this card is for. Fixed for the card's lifetime: it keys the expansion
    /// query and the session note.</summary>
    public DateOnly SessionDate { get; }

    /// <summary>
    /// The metric-group toggles this card was constructed with (spec 5.8.2), by value. Spec 12.4
    /// states the group gate inside its Frame table paragraph, so it governs the frame table's
    /// columns and hides nothing on this card's own body (review finding 2). Kept here so Task 5's
    /// frame table reads the same document the card was built from.
    /// </summary>
    public DisplaySettings Display { get; }

    // ---- collapsed field set, formatted, from the overview alone ---------------------------

    public string SessionDateText => MetricText.Date(Overview.SessionDate);

    /// <summary>The collapsed ledger strip's label (P13 R7): "09-08". Built beside
    /// <see cref="SessionDateText"/>, so the strip and the row cannot disagree about which night a
    /// line names. Task 5 binds it; nothing in this task renders it.</summary>
    public string ShortSessionDateText { get; }

    // Phase 14C ruling E2. The collapsed ledger strip now renders from Theme/Controls.axaml's
    // shared StripItemTemplate, which binds these three names. Implemented explicitly, so this
    // type's own public surface does not grow three aliases for members it already has. Polish
    // wave 9 ruling 3: the strip label is the whole date, never the clipped month-day form.
    string IStripItem.ShortLabel => SessionDateText;

    string IStripItem.FullLabel => $"{SessionDateText} · {IntegrationText} · {FrameCountText} frames";

    string? IStripItem.IconKey => null;

    bool IStripItem.IsMonospace => true;

    public string IntegrationText => MetricText.Hours(Overview.IntegrationSeconds);

    public int FrameCount => Overview.FrameCount;

    /// <summary>The frame count as text. The view binds this rather than the integer, so the
    /// markup carries no implicit numeric-to-string conversion.</summary>
    public string FrameCountText => MetricText.Count(Overview.FrameCount);

    public string MedianHfrText => MetricText.Format(Overview.MedianHfr, "0.00", " px");

    /// <summary>The arcsecond HFR, empty when no frame of the night had a usable plate scale.
    /// </summary>
    public string MedianHfrArcsecText
        => MetricText.Format(Overview.MedianHfrArcsec, "0.00", " arcsec");

    public bool HasHfrArcsec => MedianHfrArcsecText.Length > 0;

    public string MedianEccentricityText => MetricText.Format(Overview.MedianEccentricity, "0.00");

    /// <summary>Arcseconds, from the <c>fwhm</c> column (spec 7.1.1).</summary>
    public string MedianFwhmText => MetricText.Format(Overview.MedianFwhm, "0.00", " arcsec");

    public string MedianGuidingRmsText
        => MetricText.Format(Overview.MedianGuidingRmsArcsec, "0.00", " arcsec");

    public string MedianDetectedStarsText => MetricText.Format(Overview.MedianDetectedStars, "N0");

    partial void OnOverviewChanged(SessionOverview value) => OnPropertyChanged(nameof(IStripItem.FullLabel));

    // ---- Spec 12.15's custom column cells -----------------------------------------------------

    // Published by the page once per load through PublishCustomColumns below, so a card never
    // issues a read of its own and a ledger of tens of nights costs the page one read. The ledger
    // cells below and the rig rows further down read the same four fields.
    private Guid? _customTargetId;
    private IReadOnlyList<CustomColumnDefinition> _customColumns = [];
    private IReadOnlyDictionary<(Guid ColumnId, CustomValueKey Key), string> _customValues =
        new Dictionary<(Guid, CustomValueKey), string>();
    private Func<Guid, CustomValueKey, string?, CustomWriteResult>? _writeCustomValue;

    // Cell lifetime has one owner. The card holds one group, replaces it by disposing
    // the old one, and disposes it when the card goes.
    private CustomCellGroup _ledgerCells = CustomCellGroup.Empty;

    /// <summary>
    /// Spec 12.15's session-scope cells on this night's ledger row. Empty when no session-scope
    /// column is shown, which is every library that has none or has switched them all off in the
    /// Display tab's Nights ledger columns picker, and empty on an <c>obj:</c>
    /// group, which has no target id to key a value on.
    /// </summary>
    public IReadOnlyList<CustomValueViewModel> LedgerCells => _ledgerCells.Cells;

    /// <summary>
    /// Takes the page's one read of spec 12.15's definitions and values. Called from
    /// <c>TargetDetailViewModel.PublishCustomColumns</c> on the UI thread, once per load and again
    /// whenever the Display tab's picker toggles a ledger column.
    /// </summary>
    /// <param name="targetId">The loaded header's target id, or null for an <c>obj:</c> group.
    /// </param>
    /// <param name="all">Every definition, for the rig rows as well as this night's ledger
    /// cells. Never filtered by the caller: <c>CustomColumnSet</c> takes the full list.</param>
    /// <param name="ledgerColumns"><c>CustomColumnSet.LedgerRow</c>'s answer, resolved once by the
    /// page rather than once per night.</param>
    /// <param name="values">Every session-scope and rig-scope value of this target, indexed on the
    /// column and the four key parts.</param>
    /// <param name="write">Normally <c>CustomColumnRepository.SetValue</c>. Null leaves this night
    /// drawing no cell at all, rather than drawing one that silently writes nothing.</param>
    internal void PublishCustomColumns(
        Guid? targetId,
        IReadOnlyList<CustomColumnDefinition> all,
        IReadOnlyList<CustomColumnDefinition> ledgerColumns,
        IReadOnlyDictionary<(Guid ColumnId, CustomValueKey Key), string> values,
        Func<Guid, CustomValueKey, string?, CustomWriteResult>? write)
    {
        if (_disposed)
        {
            return;
        }

        _customTargetId = targetId;
        _customColumns = all;
        _customValues = values;
        _writeCustomValue = write;

        // The one reconciler, shared with the three other surfaces that draw a cell: a column set
        // that is unchanged reseeds the cells already on screen, so a value half typed into one of
        // them, and its caret, survive a scan-driven reload; a changed one is rebuilt and the old
        // group is flushed and disposed.
        var replaced = _ledgerCells;
        _ledgerCells = CustomCellFactory.Reconcile(
            _ledgerCells,
            ledgerColumns,
            targetId is Guid id ? CustomValueKey.ForSession(id, SessionDate) : null,
            SessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            StoredValue,
            write,
            _delay,
            _post,
            _logger);

        if (!ReferenceEquals(_ledgerCells, replaced))
        {
            OnPropertyChanged(nameof(LedgerCells));
        }

        // The rig rows, on both paths and unconditionally. A card whose detail is already loaded
        // when this publish arrives, which spec 12.4's PAR-018 deep link produces by expanding one
        // card inside ReplaceSessions, built its rig rows before the definitions existed; this is
        // that card's one chance to draw them. A no-op while no detail is loaded.
        RebuildRigCells();
    }

    // This night's stored value for one column, or null when the slot holds none.
    private string? StoredValue(CustomColumnDefinition column)
        => _customTargetId is { } targetId
           && _customValues.TryGetValue(
               (column.Id, CustomValueKey.ForSession(targetId, SessionDate)), out var value)
            ? value
            : null;

    // ---- Phase 20 Task 6b: the rig rows' own cells --------------------------------------------

    // Ruling C20: one CustomCellGroup per rig line, keyed by the rig's own canonical label so the
    // same instances reach both the filter table's and the ranges table's label rows (task brief
    // section 3.1). Built and rebuilt by BuildRigCells below, from the four fields
    // PublishCustomColumns publishes; never touched by that method, which owns LedgerCells alone.
    private readonly Dictionary<string, CustomCellGroup> _rigCells = [];

    /// <summary>The rig-scope cells for one rig, in display order, or empty when no rig-scope
    /// column exists or this night carries no cell for that label yet. Read by BuildFilterRows and
    /// BuildRanges so both tables' label rows share one instance per cell (section 3.1).</summary>
    private IReadOnlyList<CustomValueViewModel> RigCells(string rigLabel)
        => _rigCells.TryGetValue(rigLabel, out var group) ? group.Cells : [];

    /// <summary>
    /// Called from <c>PublishCustomColumns</c>, after it assigns the four fields above, so a card
    /// that already loaded before the page's first publish picks up its rig cells instead of
    /// showing none until the next refresh. That ordering is reachable: spec 12.4's PAR-018 deep
    /// link auto-expands a card inside <c>TargetDetailViewModel.ReplaceSessions</c>, which runs
    /// before the page's own <c>PublishCustomColumns</c> call, so that one card's first
    /// <c>Refresh</c> can run with every field here still at its construction default. A no-op
    /// before the first load, when <see cref="Detail"/> is null: that card's own
    /// <c>EnsureLoaded</c> path calls <see cref="BuildRigCells"/> directly, with the fields already
    /// set by the time it runs.
    /// </summary>
    internal void RebuildRigCells()
    {
        if (Detail is not { } detail)
        {
            return;
        }

        BuildRigCells(detail.Rigs ?? []);
        Ranges = BuildRanges(detail);
        FilterRows = BuildFilterRows(detail);
    }

    // Rebuilds _rigCells for this load's rig set. Called once, before BuildFilterRows and
    // BuildRanges, so the two tables never build two CustomValueViewModel instances over the same
    // (column, rig) slot (section 3.1's interleaving defect). One reconcile per rig, through the same
    // member the ledger row and the two dashboard surfaces use, so a value half typed into a rig cell
    // survives a scan-driven reload here exactly as it does there.
    private void BuildRigCells(IReadOnlyList<RigGroup> rigs)
    {
        var rigColumns = CustomColumnSet.RigRow(_customColumns);

        var labels = new HashSet<string>(rigs.Select(rig => rig.Label), StringComparer.Ordinal);
        foreach (var stale in _rigCells.Keys.Where(label => !labels.Contains(label)).ToList())
        {
            _rigCells[stale].Dispose();
            _rigCells.Remove(stale);
        }

        foreach (var rig in rigs)
        {
            _rigCells[rig.Label] = CustomCellFactory.Reconcile(
                _rigCells.GetValueOrDefault(rig.Label, CustomCellGroup.Empty),
                rigColumns,
                _customTargetId is Guid id ? CustomValueKey.ForRig(id, SessionDate, rig.Label) : null,
                $"{SessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}, {rig.Label}",
                column => StoredRigValue(column, rig.Label),
                _writeCustomValue,
                _delay,
                _post,
                _logger);
        }
    }

    // This night's stored value for one rig-scope column on one rig, or null when the slot holds
    // none.
    private string? StoredRigValue(CustomColumnDefinition column, string rigLabel)
        => _customTargetId is { } targetId
           && _customValues.TryGetValue(
               (column.Id, CustomValueKey.ForRig(targetId, SessionDate, rigLabel)), out var value)
            ? value
            : null;

    // Flushes and disposes every rig cell. Called from Dispose, on the same FlushBudget as the
    // note and the ledger cells: a value toggled on one rig and immediately navigated away from
    // must not be lost. A card that lost its only rig-scope column mid-session can leave the shared
    // Empty group sitting in this dictionary; it holds no cell, so its flush completes at once and
    // its dispose is a no-op the type itself enforces, and no guard is written out here.
    private void DisposeAllRigCells()
    {
        foreach (var group in _rigCells.Values)
        {
            if (group.Cells.Count == 0)
            {
                group.Dispose();
                continue;
            }

            try
            {
                group.FlushAsync().Wait(FlushBudget);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Flushing this night's rig cells on close failed");
            }

            group.Dispose();
        }

        _rigCells.Clear();
    }

    // ---- P12: worse than the target (R4, ruling Q9) -------------------------------------------

    /// <summary>Set when this night's median HFR is worse than the target's mean by more than one
    /// unit of the last displayed decimal. See <see cref="CompareWith"/>.</summary>
    [ObservableProperty]
    public partial bool IsWorseHfr { get; private set; }

    /// <summary>Set when this night's median eccentricity is worse than the target's mean. See
    /// <see cref="CompareWith"/>.</summary>
    [ObservableProperty]
    public partial bool IsWorseEccentricity { get; private set; }

    /// <summary>Set when this night's median FWHM is worse than the target's mean. See
    /// <see cref="CompareWith"/>.</summary>
    [ObservableProperty]
    public partial bool IsWorseFwhm { get; private set; }

    /// <summary>Set when this night's median guiding RMS is worse than the target's mean. See
    /// <see cref="CompareWith"/>.</summary>
    [ObservableProperty]
    public partial bool IsWorseGuidingRms { get; private set; }

    /// <summary>Set when this night detected fewer stars than the target's mean by more than one.
    /// The one inverted metric: more stars is better. See <see cref="CompareWith"/>.</summary>
    [ObservableProperty]
    public partial bool IsWorseStars { get; private set; }

    /// <summary>
    /// Flags a night's median as worse only when it is worse than the target's mean by more than
    /// one unit of the last displayed decimal (P12 R4, ruling Q9); the night pane's comparison
    /// sentence names the flagged metrics. Better stays silent, because the job on this page is
    /// finding what to reject, and a difference inside the last decimal is noise rather than a
    /// signal. A null on either side is silent too.
    /// </summary>
    /// <remarks>
    /// A night median against a target mean is not a like-for-like comparison, and the comparison
    /// sentence says so (P12 R4).
    /// <para>
    /// Called by the page after its totals are assigned, for every card it adds or carries, which
    /// is on the UI thread. A null <paramref name="totals"/> clears all five, which is the
    /// missing-target case.
    /// </para>
    /// </remarks>
    public void CompareWith(TargetTotalsViewModel? totals)
    {
        var target = totals?.Totals;

        IsWorseHfr = IsWorse(Overview.MedianHfr, target?.AvgHfr, MetricEpsilon, higherIsBetter: false);
        IsWorseEccentricity = IsWorse(Overview.MedianEccentricity, target?.AvgEccentricity, MetricEpsilon, higherIsBetter: false);
        IsWorseFwhm = IsWorse(Overview.MedianFwhm, target?.AvgFwhm, MetricEpsilon, higherIsBetter: false);
        IsWorseGuidingRms = IsWorse(Overview.MedianGuidingRmsArcsec, target?.AvgGuidingRmsArcsec, MetricEpsilon, higherIsBetter: false);
        IsWorseStars = IsWorse(Overview.MedianDetectedStars, target?.AvgDetectedStars, StarsEpsilon, higherIsBetter: true);

        BuildComparison(target);
    }

    // One unit of the last displayed decimal. The four two-decimal metrics render with "0.00";
    // the star count renders with "N0".
    private const double MetricEpsilon = 0.01d;

    private const double StarsEpsilon = 1d;

    /// <summary>
    /// Strictly more than one unit of the last displayed decimal (ruling Q9): a night exactly one
    /// unit worse is silent, and either side null is silent.
    /// </summary>
    /// <remarks>
    /// Both figures are quantised to the displayed decimal before they are compared, rather than
    /// the raw difference being tested against the epsilon. The rule is about the figures the page
    /// shows, and the direct form is wrong on its own boundary case: 1.72 minus 1.71 is
    /// 0.010000000000000009 in binary floating point, which is greater than 0.01, so the pair the
    /// rule calls silent would have been flagged.
    /// </remarks>
    private static bool IsWorse(double? night, double? target, double epsilon, bool higherIsBetter)
    {
        if (night is not { } n || target is not { } t || !double.IsFinite(n) || !double.IsFinite(t))
        {
            return false;
        }

        var nightUnits = Math.Round(n / epsilon, MidpointRounding.AwayFromZero);
        var targetUnits = Math.Round(t / epsilon, MidpointRounding.AwayFromZero);
        return (higherIsBetter ? targetUnits - nightUnits : nightUnits - targetUnits) > 1d;
    }

    // ---- P12: the session pane --------------------------------------------------------------

    /// <summary>"Night of 2025-12-07". The comp's heading for the open night: the ledger row is the
    /// same night's key and this is its title. The date renders through
    /// <see cref="MetricText.Date"/> like every other date on this page, so the page never shows
    /// one night in two formats.</summary>
    public string NightHeaderText => IsMerged
        ? $"{Nights.Count} nights, {MetricText.Date(Nights[0])} to {MetricText.Date(Nights[^1])}"
        : $"Night of {MetricText.Date(SessionDate)}";

    /// <summary>The nights this card reviews, oldest first (P25 R2): the one ledger night, or the
    /// review set's members on a merged card.</summary>
    public IReadOnlyList<DateOnly> Nights { get; }

    /// <summary>The member cards of a merged card in <see cref="Nights"/> order, whose own notes
    /// fields the notes section shows; empty on a ledger card.</summary>
    public IReadOnlyList<SessionCardViewModel> NoteNights { get; }

    public bool IsMerged => Nights.Count > 1;

    /// <summary>The header's loading line: "Loading 3 nights..." on a merged card.</summary>
    public string LoadingText => IsMerged ? $"Loading {Nights.Count} nights..." : "Loading this session...";

    /// <summary>Starts the detail load if it has not run, without touching <see cref="IsExpanded"/>.</summary>
    public void EnsureDetail() => EnsureLoaded();

    /// <summary>
    /// Spec 12.4's gain, exposure times present, first and last frame time, median airmass, median
    /// ambient temperature and median humidity, as the comp's one line. Every clause drops out when
    /// its figure is absent, so an unmeasured night reads as a shorter sentence and never as a row
    /// of empty labels. Built in <c>Publish</c>, from the loaded detail.
    /// </summary>
    [ObservableProperty]
    public partial string FactsLineText { get; private set; } = "";

    /// <summary>
    /// The comp's signature graphic: one tick per exposure in its filter's ink over the
    /// astronomical-night band. Built in <c>Publish</c> from the loaded detail; null before the
    /// first load and after <see cref="Invalidate"/>, which renders nothing rather than an empty
    /// strip.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNightStrip))]
    [NotifyPropertyChangedFor(nameof(LaneAxis))]
    public partial NightStripViewModel? NightStrip { get; private set; }

    /// <summary>The night's shared time axis, which the timeline, the per-frame chart and
    /// the guide graph all read. Null before the first load.</summary>
    public NightLaneAxis? LaneAxis => NightStrip?.LaneAxis;

    public bool HasNightStrip => NightStrip is not null;

    /// <summary>
    /// The comp's comparison sentence as runs, so the deltas can take the worse ink while the words
    /// stay secondary. Only the metrics <see cref="CompareWith"/> marked worse are named with a
    /// delta; the rest are gathered into the closing clause, because the job on this page is
    /// finding what to reject and a list of five signed numbers is not a sentence.
    /// </summary>
    /// <remarks>Every run whose <c>IsFigure</c> is set is a worse delta by construction, which is
    /// what lets the view set both the <c>figure</c> and the <c>worse</c> class from one flag. The
    /// run type is <c>TargetTotalsViewModel</c>'s, reused rather than copied.</remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComparison))]
    public partial IReadOnlyList<LogLineRunViewModel> ComparisonRuns { get; private set; } = [];

    /// <summary>"(night medians against target means)". Constant, rendered in tertiary ink beside
    /// the sentence and never omitted while the sentence is present (P12 R4). The five ledger
    /// column headers carry the same words as a tooltip.</summary>
    public const string ComparisonNote = "(night medians against target means)";

    public bool HasComparison => ComparisonRuns.Count > 0;

    /// <summary>The frames strip's outlier pills (P24 R22), one per metric the night has a value
    /// for, in the table's column order; the session insights themselves are not shown. Built in
    /// <c>Publish</c>, and each pill's <c>IsActive</c> follows the frame table's filter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilterPills))]
    public partial IReadOnlyList<OutlierPillViewModel> OutlierPills { get; private set; } = [];

    /// <summary>Whether the Filter segment has any pill to show, which is what draws it and its
    /// Clear button.</summary>
    public bool HasFilterPills => OutlierPills.Count > 0 || FrameTable?.IsMultiRig == true;

    /// <summary>Whether Clear filters has anything to clear: the outlier filter on, or a rig pill
    /// off.</summary>
    public bool HasActiveFilters => FrameTable is { } table && (table.IsFiltered || table.RigPills.Any(pill => !pill.IsSelected));

    /// <summary>The comp's merged filter table: spec 12.4's per-filter medians and per-filter
    /// detail rows joined by filter name, which is what the pane renders. The two query lists it
    /// joins are read straight off <c>Detail</c>.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<FilterTableRowViewModel> FilterRows { get; private set; } = [];

    /// <summary>
    /// Spec 12.4's rigs of this night (PAR-004), in first-capture order, one entry even on the
    /// ordinary single-rig night, so the thumbnail strip has one box and the markup has no branch.
    /// Published in <c>Publish</c> from the loaded detail and cleared by <see cref="Invalidate"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMultiRig))]
    [NotifyPropertyChangedFor(nameof(HasRigs))]
    public partial IReadOnlyList<RigViewModel> Rigs { get; private set; } = [];

    /// <summary>Spec 12.4's "A multi-rig night". It governs the rig label rows, the frame table's
    /// Rig column and the rig pills, and not the thumbnail strip: a single-rig night still shows
    /// one box.</summary>
    public bool IsMultiRig => Rigs.Count > 1;

    /// <summary>Whether the thumbnail strip has anything to draw at all, which is false before the
    /// first load and after an invalidation.</summary>
    public bool HasRigs => Rigs.Count > 0;

    /// <summary>The per-session chart, typed, so the pane's band can bind its expansion with a
    /// compiled binding. <see cref="Chart"/> stays <c>object?</c> because that is what lets one
    /// disposal rule cover both children.</summary>
    public MetricChartViewModel? SessionChart => Chart as MetricChartViewModel;

    /// <summary>Whether this night has a notes box at all. False for an <c>obj:</c> group, which
    /// has no target id to key a note on, and what hides the drawer's Session notes box.</summary>
    public bool HasNotesBox => Notes is not null || NoteNights.Any(night => night.Notes is not null);

    /// <summary>The programmatic way to an outlier pill's press (P24 R6). Sets the frame table's
    /// outlier filter to the kind, or clears it when that kind is already the active filter
    /// (ruling Q14). The frame table is typed <c>object?</c> on this card, so this is the one
    /// place it is cast.</summary>
    /// <remarks>It does not touch the pills itself. The table's own <c>OutlierFilter</c>
    /// notification does, through <see cref="OnFrameTableChanged"/>, because this command is not
    /// the only thing that changes that filter: Escape inside the table runs
    /// <c>ClearFilterOrSelection</c> (ruling Q14 puts it in this flow).</remarks>
    [RelayCommand]
    private void ShowOutliers(FrameOutlierFilter filter)
    {
        if (Frames is FrameTableViewModel table)
        {
            table.SetOutlierFilter(table.OutlierFilter == filter ? FrameOutlierFilter.None : filter);
        }
    }

    /// <summary>The Filter segment's "Clear filters" (P24 R6): the outlier filter off and every
    /// rig pill on. The button's enablement binds <see cref="HasActiveFilters"/> rather than a
    /// <c>CanExecute</c>: <c>Publish</c> runs on the post seam's thread and a
    /// <c>NotifyCanExecuteChanged</c> there reaches the button unmarshalled, while a property
    /// notification does not.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        if (Frames is not FrameTableViewModel table || !HasActiveFilters)
        {
            return;
        }

        table.SetOutlierFilter(FrameOutlierFilter.None);
        foreach (var pill in table.RigPills)
        {
            pill.IsSelected = true;
        }
    }

    private void OnRigPillChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Dashboard.ToggleOptionViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(HasActiveFilters));
        }
    }

    /// <summary>Spec 12.4's "Compare to" segment. The write and the persistence are the holder's;
    /// the card only forwards, which is why there is no correctness rule in the body to guard and
    /// nothing for <c>RelayCommand.Execute</c> ignoring <c>CanExecute</c> (TRACKING item 13) to
    /// skip: either value is a legal value at any time.</summary>
    /// <remarks>
    /// The programmatic way in. The segment's own buttons do not use it: they bind
    /// <see cref="IsSessionBaseline"/> and its negation two way (review P2-1), which is what keeps
    /// one of the two lit at all times. This goes through that same property rather than assigning
    /// the holder itself, so the segment has exactly one write expression however it is driven, and
    /// it stays a published member of this phase for a caller that has a
    /// <see cref="GradingBaseline"/> in hand rather than a boolean.
    /// </remarks>
    [RelayCommand]
    private void SetGradingBaseline(GradingBaseline baseline)
        => IsSessionBaseline = baseline == GradingBaseline.Session;

    // The pill table (P24 R22), in the frame table's column order: the filter, the label's metric,
    // whether a frame carries the metric at all, and the flag the night's rule set on it.
    private static readonly (FrameOutlierFilter Filter, string Metric, Func<FrameRow, bool> HasValue, Func<FrameRow, bool> Flagged)[] PillMetrics =
    [
        (FrameOutlierFilter.Hfr, "HFR", frame => frame.MedianHfr is not null, frame => frame.IsHfrOutlier),
        (FrameOutlierFilter.Eccentricity, "Ecc", frame => frame.Eccentricity is not null, frame => frame.IsEccentricityOutlier),
        (FrameOutlierFilter.Fwhm, "FWHM", frame => frame.Fwhm is not null, frame => frame.IsFwhmOutlier),
        (FrameOutlierFilter.Stars, "Stars", frame => frame.DetectedStars is not null, frame => frame.IsStarsOutlier),
        (FrameOutlierFilter.GuidingRms, "RMS", frame => frame.GuidingRmsArcsec is not null, frame => frame.IsGuidingRmsOutlier),
    ];

    private bool _syncingPills;

    // One pill per metric the night has values for; the count is the detail's own flagged frames.
    private IReadOnlyList<OutlierPillViewModel> BuildOutlierPills(SessionDetail detail)
    {
        var active = FrameTable?.OutlierFilter ?? FrameOutlierFilter.None;
        List<OutlierPillViewModel> pills = [];
        foreach (var (filter, metric, hasValue, flagged) in PillMetrics)
        {
            if (!detail.Frames.Any(hasValue))
            {
                continue;
            }

            var count = detail.Frames.Count(flagged);
            var pill = new OutlierPillViewModel(filter, string.Format(CultureInfo.InvariantCulture, "{0} outliers ({1})", metric, count), count)
            {
                IsActive = filter == active,
            };
            pill.PropertyChanged += OnOutlierPillChanged;
            pills.Add(pill);
        }

        return pills;
    }

    // A pill's check is the press (the view binds it two way); a check pushed back from the table
    // by SyncOutlierPills is not, which is what the flag separates.
    private void OnOutlierPillChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingPills || e.PropertyName != nameof(OutlierPillViewModel.IsActive) || sender is not OutlierPillViewModel pill || Frames is not FrameTableViewModel table)
        {
            return;
        }

        if (pill.IsActive)
        {
            table.SetOutlierFilter(pill.Filter);
        }
        else if (table.OutlierFilter == pill.Filter)
        {
            table.SetOutlierFilter(FrameOutlierFilter.None);
        }
    }

    /// <summary>Pushes the frame table's current filter into every pill's <c>IsActive</c>.</summary>
    private void SyncOutlierPills()
    {
        var active = FrameTable?.OutlierFilter ?? FrameOutlierFilter.None;
        _syncingPills = true;
        try
        {
            foreach (var pill in OutlierPills)
            {
                pill.IsActive = pill.Filter == active;
            }
        }
        finally
        {
            _syncingPills = false;
        }
    }

    // Spec 12.4's two per-filter lists, joined by filter name in the medians list's order. A detail
    // row whose filter has no medians row still renders, as its own top-level row, which is
    // possible when every frame of that filter has a null metric.
    private IReadOnlyList<FilterTableRowViewModel> BuildFilterRows(SessionDetail detail)
    {
        var rigs = detail.Rigs ?? [];
        if (rigs.Count <= 1 && !CustomColumnSet.AnyRigScope(_customColumns))
        {
            // Unchanged: a single-rig night on a library with no rig-scope column renders exactly
            // as it did before Phase 20, with no label row, no block and one table. That is a
            // property of the data (every RigLabel is null) rather than a branch in the view.
            return BuildFilterBlock(detail.FilterMedians, detail.FilterDetails);
        }

        // Ruling C4: a rig label row per rig, then that rig's own filter rows under it, in the
        // night's rig order. The query already concatenated the two lists in that order and
        // stamped each row with its rig's label, so this only has to split them back out.
        //
        // User choice 7 (spec 12.15): while any rig-scope column exists, a single-rig night takes
        // this same path too, so the cells reach the screen. SessionDetailQuery.BuildRigGroups
        // still answers exactly one RigGroup for it, so the loop draws one label row followed by
        // that rig's own rows, with no second branch (task brief section 3.2). SplitPerRig never
        // stamps a RigLabel on a single-rig night's own medians and details rows (they carry null,
        // task brief's own point about RigGroup.Ranges applies here too), which IsRigLabel accounts
        // for below rather than by asking SessionDetailQuery to stamp one it does not stamp today.
        List<FilterTableRowViewModel> blocks = [];
        foreach (var rig in rigs)
        {
            blocks.Add(FilterTableRowViewModel.RigLabelRow(rig.Label, rig.FrameCount, RigCells(rig.Label)));
            blocks.AddRange(BuildFilterBlock(
                [.. detail.FilterMedians.Where(medians => IsRigLabel(medians.RigLabel, rig.Label, rigs.Count))],
                [.. detail.FilterDetails.Where(row => IsRigLabel(row.RigLabel, rig.Label, rigs.Count))]));
        }

        return blocks;
    }

    // A row's carried RigLabel matches when it names this rig by name, or, on a single-rig night,
    // when it carries none at all: SplitPerRig (SessionDetailQuery.cs) only stamps a label when
    // more than one rig exists, so a single-rig night's own rows are unstamped even when user
    // choice 7 routes them through this per-rig split for their cells.
    private static bool IsRigLabel(string? carried, string label, int rigCount)
        => carried is null ? rigCount <= 1 : string.Equals(carried, label, StringComparison.Ordinal);

    private IReadOnlyList<FilterTableRowViewModel> BuildFilterBlock(
        IReadOnlyList<FilterMedians> filterMedians,
        IReadOnlyList<FilterDetailRow> filterDetails)
    {
        List<FilterTableRowViewModel> rows = [];
        HashSet<string> named = [.. filterMedians.Select(medians => medians.FilterName)];

        foreach (var medians in filterMedians)
        {
            var details = filterDetails
                .Where(row => string.Equals(row.FilterName, medians.FilterName, StringComparison.Ordinal))
                .ToList();

            rows.Add(new FilterTableRowViewModel(
                medians,
                new FilterSwatchViewModel(medians.FilterName, _filterTint(medians.FilterName)),
                details));

            if (details.Count > 1)
            {
                rows.AddRange(details.Select(row => new FilterTableRowViewModel(row, null, isSubRow: true)));
            }
        }

        rows.AddRange(filterDetails
            .Where(row => !named.Contains(row.FilterName))
            .Select(row => new FilterTableRowViewModel(
                row,
                new FilterSwatchViewModel(row.FilterName, _filterTint(row.FilterName)),
                isSubRow: false)));

        return rows;
    }

    // The comp's one line, built from the seven spec 12.4 figures already formatted above, in spec
    // order, with the two frame times joined first.
    private string BuildFactsLine()
    {
        // P25 R8: a merged card dates its two frame times, the frame table's own "MM-dd " rule.
        var first = IsMerged ? DatePrefixed(Detail?.FirstFrameTime, FirstFrameTimeText) : FirstFrameTimeText;
        var last = IsMerged ? DatePrefixed(Detail?.LastFrameTime, LastFrameTimeText) : LastFrameTimeText;
        var times = first.Length > 0 && last.Length > 0
            ? $"{first} to {last}"
            : first.Length > 0
                ? first
                : last;

        List<string> clauses = [];

        // Spec 12.4 item 1: on a multi-rig night the line names the rigs, in rig order, before the
        // figures it already carries. A single-rig night's line is exactly what it was, which is
        // why this is guarded by IsMultiRig and not by HasRigs.
        if (IsMultiRig)
        {
            Append(string.Join(", ", Rigs.Select(rig => rig.Label)));
        }

        Append(times);
        Append(GainText.Length > 0 ? $"gain {GainText}" : "");
        Append(ExposureTimesText);
        Append(MedianAirmassText.Length > 0 ? $"airmass {MedianAirmassText}" : "");
        Append(MedianAmbientTempText);
        Append(MedianHumidityText);

        // Spec 12.4's guiding provenance sentence (Phase 15A), last: a csv-only or a guiding-free
        // night appends nothing, so its line is byte-identical to what it was before this task.
        Append(Overview.GuidingProvenance switch
        {
            GuidingRmsProvenance.Phd2 => "from a PHD2 guide log",
            GuidingRmsProvenance.Mixed => "from a PHD2 guide log for some frames",
            _ => "",
        });

        return string.Join(", ", clauses);

        void Append(string clause)
        {
            if (clause.Length > 0)
            {
                clauses.Add(clause);
            }
        }
    }

    private string DatePrefixed(DateTime? utcInstant, string clock)
        => utcInstant is { } utc && clock.Length > 0
            ? NightStripViewModel.ToLocal(utc, _zone).ToString("MM-dd ", CultureInfo.InvariantCulture) + clock
            : clock;

    // R4's sentence. Built from the same two sides CompareWith already has, so the flags and the
    // prose cannot disagree about which metric is worse.
    private void BuildComparison(TargetTotals? target)
    {
        if (target is null)
        {
            ComparisonRuns = [];
            return;
        }

        List<(string Name, bool Worse, string Delta)> compared = [];
        Compare("HFR", Overview.MedianHfr, target.AvgHfr, IsWorseHfr, "0.00", " px");
        Compare("eccentricity", Overview.MedianEccentricity, target.AvgEccentricity, IsWorseEccentricity, "0.00", "");
        Compare("FWHM", Overview.MedianFwhm, target.AvgFwhm, IsWorseFwhm, "0.00", " arcsec");
        Compare("guiding", Overview.MedianGuidingRmsArcsec, target.AvgGuidingRmsArcsec, IsWorseGuidingRms, "0.00", " arcsec");
        Compare("stars", Overview.MedianDetectedStars, target.AvgDetectedStars, IsWorseStars, "N0", "");

        if (compared.Count == 0)
        {
            ComparisonRuns = [];
            return;
        }

        List<LogLineRunViewModel> runs = [new("Against this target's average:", false)];

        var worse = compared.Where(entry => entry.Worse).ToList();
        for (var i = 0; i < worse.Count; i++)
        {
            runs.Add(new LogLineRunViewModel(worse[i].Name, false));
            runs.Add(new LogLineRunViewModel(
                worse[i].Delta + (i == worse.Count - 1 ? "." : ","),
                true));
        }

        var steady = compared.Where(entry => !entry.Worse).Select(entry => entry.Name).ToList();
        if (steady.Count > 0)
        {
            runs.Add(new LogLineRunViewModel(SpreadClause(steady), false));
        }

        ComparisonRuns = runs;

        void Compare(string name, double? night, double? mean, bool worse, string format, string suffix)
        {
            if (night is not { } n || mean is not { } t || !double.IsFinite(n) || !double.IsFinite(t))
            {
                return;
            }

            compared.Add((name, worse, Signed(n - t, format, suffix)));
        }
    }

    // A delta always carries its sign, because the sentence is prose and a bare "0.11" reads as a
    // value rather than a difference. A plain hyphen-minus: this repository forbids en and em
    // dashes outright.
    private static string Signed(double delta, string format, string suffix)
        => (delta < 0 ? "-" : "+")
           + Math.Abs(delta).ToString(format, CultureInfo.InvariantCulture)
           + suffix;

    private static string SpreadClause(IReadOnlyList<string> names)
    {
        var joined = names.Count switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
        };

        var sentence = char.ToUpperInvariant(joined[0]) + joined[1..];
        return names.Count == 1
            ? $"{sentence} within its usual spread."
            : $"{sentence} within their usual spread.";
    }

    // The night strip's filter inks, resolved once per load through the one filter-colour lookup
    // the chart pills and the ledger dots already use. Keyed by exactly the value the query puts
    // in FrameRow.FilterUsed, which is what NightStripViewModel looks up.
    private IReadOnlyDictionary<string, ImmutableSolidColorBrush> BuildFilterBrushes(
        IReadOnlyList<FrameRow> frames)
    {
        Dictionary<string, ImmutableSolidColorBrush> brushes = new(StringComparer.Ordinal);
        foreach (var name in frames.Select(frame => frame.FilterUsed).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            brushes[name] = _filterTint(name);
        }

        return brushes;
    }

    // A tick press names a frame by its index into capture order. R8's row click and R9's tick
    // click are the same action, so both go through the table's one select-and-preview entry
    // point. Frames is typed object? deliberately, so this is the one place it is cast, and it is
    // an as-cast, so a null factory return is a no-op.
    private void OnNightStripFrameSelected(object? sender, int frameIndex)
        => (Frames as FrameTableViewModel)?.SelectAndPreviewFrameAt(frameIndex);

    // Spec 12.4's rigs and their thumbnail boxes. One RigViewModel per rig even on a single-rig
    // night, so the strip always has a box and the markup carries no branch.
    private void PublishRigs(SessionDetail detail)
    {
        DisposeRigs();

        var groups = detail.Rigs ?? [];
        Rigs =
        [
            .. groups.Select(group => new RigViewModel(
                group,
                _createFrameThumbnail,
                () => OpenPreviewAtReference(detail, group.ReferenceImageId))),
        ];

        StartRigThumbnails();
    }

    // Spec 12.4's decode walk. RigViewModel.StartThumbnail is idempotent, so a second call
    // decodes nothing a second time.
    private void StartRigThumbnails()
    {
        foreach (var rig in Rigs)
        {
            rig.StartThumbnail();
        }
    }

    // Spec 12.4: a click on a box opens the preview modal at that frame, inside the night's frame
    // list. It goes through the frame table, which is where the preview opener already lives,
    // rather than a second preview entry point. The index is the frame's position in the loaded
    // night, which is exactly the order the table built its capture list in.
    //
    // PreviewFrameAt rather than SelectAndPreviewFrameAt (review P2-3): the latter is R8's
    // row-and-tick route and no-ops for a row the current filter hides, and the reference frame is
    // the sharpest frame of its set, so with an outlier filter on or that rig's pill unchecked the
    // click would be dead. The spec states it with no condition.
    private void OpenPreviewAtReference(SessionDetail detail, Guid? imageId)
    {
        if (imageId is not { } id || Frames is not FrameTableViewModel table)
        {
            return;
        }

        for (var index = 0; index < detail.Frames.Count; index++)
        {
            if (detail.Frames[index].ImageId == id)
            {
                table.PreviewFrameAt(index);
                return;
            }
        }
    }

    // Every box owns a thumbnail slot, and a slot owns an Avalonia Bitmap, which owns unmanaged
    // memory. A night replaced without this leaks one decoded bitmap per rig per expansion.
    private void DisposeRigs()
    {
        foreach (var rig in Rigs)
        {
            rig.Dispose();
        }

        Rigs = [];
    }

    // R9: a hover highlights and scrolls, and changes no selection. A frame the current outlier
    // filter hides highlights nothing, which the table enforces rather than this handler.
    private void OnNightStripFrameHovered(object? sender, int? frameIndex)
        => (Frames as FrameTableViewModel)?.HighlightFrameAt(frameIndex);

    private void DetachNightStrip()
    {
        if (NightStrip is { } strip)
        {
            strip.FrameSelected -= OnNightStripFrameSelected;
            strip.FrameHovered -= OnNightStripFrameHovered;
        }
    }

    // Phase 25 R5: one single-night strip per span of the merged frame list, each over its own
    // night's bounds, stitched onto one axis; the offset keeps a tick naming its merged row.
    private NightStripViewModel BuildStitchedStrip(SessionDetail detail)
    {
        var brushes = BuildFilterBrushes(detail.Frames);
        return NightStripViewModel.Stitched(
        [
            .. detail.Nights.Select(span => (
                span.Night,
                new NightStripViewModel(
                    [.. detail.Frames.Skip(span.FirstFrameIndex).Take(span.FrameCount)],
                    brushes,
                    ChartSelectionViewModel.FallbackFilterTint,
                    AstroNight.NightBounds(span.Night, _latitudeDeg, _longitudeDeg, _zone),
                    _zone,
                    _use24Hour),
                span.FirstFrameIndex)),
        ]);
    }

    // Ruling Q14's contract is that a pill's check names the table's current state, so the
    // state has to be watched rather than remembered. Every route that changes the filter goes
    // through this one property, including the Escape binding's ClearFilterOrSelection.
    private void OnFrameTableChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FrameTableViewModel.OutlierFilter))
        {
            SyncOutlierPills();
            OnPropertyChanged(nameof(HasActiveFilters));
        }

        // The table's selection is the strip's active tick at rest, the
        // direction P13 R9 did not carry. NightStrip can be null between AttachFrameTable and the
        // Publish that builds one, so this is a guarded push rather than an assumed one.
        if (e.PropertyName == nameof(FrameTableViewModel.SelectedCaptureIndex))
        {
            NightStrip?.SetActiveFrame((Frames as FrameTableViewModel)?.SelectedCaptureIndex);
        }
    }

    private void AttachFrameTable()
    {
        if (Frames is FrameTableViewModel table)
        {
            table.PropertyChanged += OnFrameTableChanged;
            foreach (var pill in table.RigPills)
            {
                pill.PropertyChanged += OnRigPillChanged;
            }
        }
    }

    private void DetachFrameTable()
    {
        if (Frames is FrameTableViewModel table)
        {
            table.PropertyChanged -= OnFrameTableChanged;
            foreach (var pill in table.RigPills)
            {
                pill.PropertyChanged -= OnRigPillChanged;
            }
        }
    }

    // ---- night selection --------------------------------------------------------------------

    /// <summary>
    /// Spec 12.4's night selection column (ruling C5): whether the ledger row's check box is
    /// ticked. Checking a night does not select it, so this is a second mark on the row and not
    /// a second name for <see cref="IsExpanded"/>: the lit row names the night the pane is
    /// showing, the check names a night an action will act on.
    /// </summary>
    /// <remarks>
    /// Not persisted, not part of the overview, and not cleared by <see cref="Refresh"/>: a
    /// reload that keeps the night keeps its check, and a reload that drops the night drops the
    /// card with it. <c>TargetDetailViewModel.SelectedNights</c> is the collected set and is
    /// rebuilt from this flag.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    // ---- expansion ------------------------------------------------------------------------

    /// <summary>Two-way bound to the control's Expander. The first transition to true starts the
    /// one query; every later toggle is free.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>The last expansion failure, logged and kept rather than swallowed. The card
    /// renders a warning callout and keeps whatever it had.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    public partial Exception? LastFailure { get; private set; }

    /// <summary>Drives the warning callout, so the view binds <c>IsVisible</c> with no converter.
    /// </summary>
    public bool HasFailure => LastFailure is not null;

    /// <summary>Null until the card has been expanded at least once, and null again after
    /// <see cref="Invalidate"/>.</summary>
    [ObservableProperty]
    public partial SessionDetail? Detail { get; private set; }

    /// <summary>Spec 12.4's session ranges, in spec order and already filtered by the metric-group
    /// gate, so the view is one <c>ItemsControl</c> rather than twenty hand-placed rows.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<RangeCellViewModel> Ranges { get; private set; } = [];

    /// <summary>Gain of the session's first frame by capture time. A session shot at two gains
    /// reports the first; the frame table's Gain column holds the per-frame truth (Task 2
    /// handoff).</summary>
    [ObservableProperty]
    public partial string GainText { get; private set; } = "";

    /// <summary>Spec 12.4's "exposure times present", distinct and ascending.</summary>
    [ObservableProperty]
    public partial string ExposureTimesText { get; private set; } = "";

    [ObservableProperty]
    public partial string FirstFrameTimeText { get; private set; } = "";

    [ObservableProperty]
    public partial string LastFrameTimeText { get; private set; } = "";

    [ObservableProperty]
    public partial string MedianAirmassText { get; private set; } = "";


    [ObservableProperty]
    public partial string MedianAmbientTempText { get; private set; } = "";


    [ObservableProperty]
    public partial string MedianHumidityText { get; private set; } = "";


    /// <summary>Task 5's frame table over <c>Detail.Frames</c>, built by the factory on each
    /// successful load and rendered by the card's <c>FrameTableRegion</c> template. Typed
    /// <c>object</c> so <see cref="DisposeChildren"/> stays one rule for both children whatever
    /// they turn out to be; null before the first expansion and after <see cref="Invalidate"/>,
    /// which renders nothing rather than a stub.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrameTable))]
    [NotifyPropertyChangedFor(nameof(HasFilterPills))]
    [NotifyPropertyChangedFor(nameof(HasActiveFilters))]
    public partial object? Frames { get; private set; }

    /// <summary>The same table, typed, for the Frames section's chrome to bind through. Spec
    /// 12.4's tally is a row of the section rather than of the table, and a compiled binding
    /// cannot reach a member through <see cref="Frames"/>, which is <c>object</c>. The projection
    /// is <see cref="SessionChart"/>'s over <see cref="Chart"/>, member for member, so the two
    /// regions of this pane reach their child the same way.</summary>
    public FrameTableViewModel? FrameTable => Frames as FrameTableViewModel;

    /// <summary>Task 8's per-session chart, on the same seam as <see cref="Frames"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionChart))]
    public partial object? Chart { get; private set; }

    /// <summary>The session notes box, or null for an <c>obj:</c> group. Reuses Task 3's
    /// <c>AutosaveField</c> unchanged: same one second window, same serialised background write,
    /// same dirty rule.</summary>
    public AutosaveField? Notes { get; }

    /// <summary>Spec 12.4's Guiding section, or null on a card built without a night query and on
    /// a library with no guide log, where the band is not drawn and nothing may query. Typed so
    /// the pane's markup binds its figures directly; the graph inside it is the section's own
    /// <c>object?</c> slot.</summary>
    public GuidingSectionViewModel? Guiding { get; }

    /// <summary>Spec 12.4: the Guiding band is drawn whenever the library holds at least one
    /// <c>phd2_logs</c> row and is absent otherwise, so a library that has never seen a guide log
    /// carries no dead band on every night. Answered once per card construction.</summary>
    public bool HasGuidingBand { get; }

    /// <summary>Spec 12.4's "Compare to" segment, left button. A projection of the same
    /// process-wide holder the two disclosures above read, so the segment shows the same choice on
    /// every night's pane and switching nights keeps it.</summary>
    /// <remarks>
    /// Settable, and that is what makes the segment a segment (review P2-1). The two buttons bind
    /// this property and its negation two way, which is the shape
    /// <c>Views/TargetDetail/FrameListDialogWindow.axaml</c>'s mode segment already uses: one
    /// source of truth, so there is no state in which both read unchecked. A one-way binding with
    /// a command cannot hold that invariant here, because a <c>ToggleButton</c> flips its own
    /// <c>IsChecked</c> through <c>SetCurrentValue</c> before running the command, and a one-way
    /// binding whose source value did not change does not push it back.
    /// <para>
    /// Setting false means the other side of the segment, not "no baseline": there are two values
    /// and the segment always shows one of them.
    /// </para>
    /// </remarks>
    public bool IsSessionBaseline
    {
        get => _targetPage.GradingBaseline == GradingBaseline.Session;
        set => _targetPage.GradingBaseline = value ? GradingBaseline.Session : GradingBaseline.Rig;
    }

    /// <summary>Spec 12.4's "Compare to" segment, right button. Read only on purpose: the markup
    /// binds <c>!IsSessionBaseline</c> rather than this, so one property is the whole segment's
    /// state. This is what a test and any later reader ask for the same fact by name.</summary>
    public bool IsRigBaseline => _targetPage.GradingBaseline == GradingBaseline.Rig;

    /// <summary>The in-flight expansion query, so a test can await it instead of sleeping.
    /// Mirrors <c>TargetDetailViewModel.PendingLoad</c>.</summary>
    internal Task? PendingLoad { get; private set; }

    internal bool IsDisposed => _disposed;

    /// <summary>
    /// Adopts a fresh overview for the same night and drops the loaded detail. This is what a
    /// scan-driven page reload calls for a session that is still there: the card, its expansion
    /// state and its notes box survive, while every collapsed figure and the loaded detail are
    /// re-read (review finding 3).
    /// </summary>
    public void Refresh(SessionOverview overview)
    {
        if (_disposed)
        {
            return;
        }

        // One assignment republishes the whole collapsed field set.
        Overview = overview;
        Invalidate();
    }

    /// <summary>
    /// Drops the loaded detail so the next expansion re-queries. An expanded card re-loads
    /// immediately; a collapsed one waits for its next expansion and issues nothing until then.
    /// </summary>
    public void Invalidate()
    {
        if (_disposed)
        {
            return;
        }

        // Retires any publish still queued for the load being dropped, and the indicator with
        // it: IsLoading belongs to the load that is being abandoned (review finding 7).
        _generation++;
        _loading = false;
        IsLoading = false;

        DisposeChildren();
        DetachNightStrip();
        DetachFrameTable();

        // A rescan may have brought new guide logs or rewritten the night's sessions, so the
        // section re-reads on its next open exactly as the detail does.
        Guiding?.Invalidate();
        Frames = null;
        Chart = null;
        Detail = null;
        NightStrip = null;
        FilterRows = [];
        OutlierPills = [];
        Ranges = [];
        GainText = "";
        ExposureTimesText = "";
        FirstFrameTimeText = "";
        LastFrameTimeText = "";
        MedianAirmassText = "";
        MedianAmbientTempText = "";
        MedianHumidityText = "";
        FactsLineText = "";

        if (IsExpanded)
        {
            EnsureLoaded();
        }
    }

    // Generated by [ObservableProperty]. The first transition to true is the only one that costs
    // anything; collapsing keeps Detail, so re-expanding is free.
    partial void OnIsExpandedChanged(bool value)
    {
        // Spec 12.4: the Guiding section loads for "the selected night", which is this flag. A
        // card built for a night the pane is not showing must not query, whatever the band's
        // shared open state says.
        if (Guiding is not null)
        {
            Guiding.IsShown = value;
        }

        if (value)
        {
            EnsureLoaded();
        }
    }

    // The card forwards the holder's baseline under its own property names, so a card that did
    // not make the choice repaints with the one that did.
    private void OnTargetPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TargetPageState.GradingBaseline))
        {
            OnPropertyChanged(nameof(IsSessionBaseline));
            OnPropertyChanged(nameof(IsRigBaseline));
        }
    }

    // Not a query on the calling thread: the toggle arrives on the UI thread and
    // SessionDetailQuery.Get is a synchronous SQLite read whose first call after a scan also pays
    // the rig baseline rebuild (Task 2 handoff, roughly 170 ms on a 900 frame library).
    private void EnsureLoaded()
    {
        if (_disposed || Detail is not null || _loading)
        {
            return;
        }

        _loading = true;
        IsLoading = true;
        var generation = ++_generation;
        var token = _lifetime.Token;
        PendingLoad = Task.Run(
            () =>
            {
                try
                {
                    var detail = _getDetail(_groupKey, SessionDate);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _post(() => Publish(generation, detail, null));
                }
                catch (OperationCanceledException)
                {
                    // The card was disposed while the read was in flight.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Loading session {SessionDate} of {GroupKey} failed",
                        SessionDate,
                        _groupKey);

                    _post(() => Publish(generation, null, ex));
                }
            },
            token);
    }

    // Runs on the UI thread through the post seam. A response older than the newest request is
    // dropped here rather than overwriting it.
    private void Publish(int generation, SessionDetail? detail, Exception? failure)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        _loading = false;
        IsLoading = false;

        if (failure is not null)
        {
            // Whatever the card had stays on screen, and the collapsed fields are untouched:
            // they came from the overview and never needed this query.
            LastFailure = failure;
            return;
        }

        LastFailure = null;

        if (detail is null)
        {
            // The night no longer has a LIGHT frame, which after a successful page load means a
            // scan removed it between the two reads (Task 2 handoff). An empty body, not an
            // error page.
            return;
        }

        Detail = detail;

        // Before the facts line and the two tables, all three of which read the rig list: spec
        // 12.4 item 1 puts the rig names first on the line, and ruling C4 opens each table block
        // with the rig's own label row.
        PublishRigs(detail);

        // Spec 12.15's rig-scope cells, one CustomCellGroup per rig line (ruling C20), built once
        // and shared by both tables' label rows below (section 3.1 of this task's brief: the
        // filter table and the ranges table each draw a label row per rig, and must not build two
        // cells over the same slot).
        BuildRigCells(detail.Rigs ?? []);

        Ranges = BuildRanges(detail);

        GainText = detail.Gain is { } gain ? MetricText.Count(gain) : "";
        ExposureTimesText = string.Join(
            ", ",
            detail.ExposureTimes.Select(seconds => MetricText.Format(seconds, "0.##", " s")));
        FirstFrameTimeText = SessionTimeFormat.Format(detail.FirstFrameTime, _zone, _use24Hour);
        LastFrameTimeText = SessionTimeFormat.Format(detail.LastFrameTime, _zone, _use24Hour);

        MedianAirmassText = MetricText.Format(detail.MedianAirmass, "0.00");
        MedianAmbientTempText = MetricText.Format(detail.MedianAmbientTemp, "0.0", " C");
        MedianHumidityText = MetricText.Format(detail.MedianHumidity, "0", " %");

        // P12: the pane's own surface, built from the same loaded detail and from figures already
        // formatted above, so nothing here re-reads the database or re-derives a number.
        FactsLineText = BuildFactsLine();
        FilterRows = BuildFilterRows(detail);

        DetachNightStrip();
        NightStrip = detail.Nights.Count > 1
            ? BuildStitchedStrip(detail)
            : new NightStripViewModel(
                detail.Frames,
                BuildFilterBrushes(detail.Frames),
                ChartSelectionViewModel.FallbackFilterTint,
                AstroNight.NightBounds(SessionDate, _latitudeDeg, _longitudeDeg, _zone),
                _zone,
                _use24Hour);
        NightStrip.FrameSelected += OnNightStripFrameSelected;
        NightStrip.FrameHovered += OnNightStripFrameHovered;

        // Adopts an externally changed note but never overwrites in-progress typing.
        Notes?.Reseed(detail.Notes);

        DetachFrameTable();
        Frames = _createFrameTable(detail);
        AttachFrameTable();
        NightStrip.SetActiveFrame((Frames as FrameTableViewModel)?.SelectedCaptureIndex);
        Chart = _createChart(detail);
        (Chart as SessionChartViewModel)?.UseLaneAxis(LaneAxis, _zone, NightStrip, Guiding);

        // After Frames, because a pill's check reads the table's current filter.
        OutlierPills = BuildOutlierPills(detail);
    }

    // Spec 12.4's order: HFR, eccentricity, FWHM, guiding RMS, sensor temperature. Gain, the
    // exposure times and the two frame times follow as their own labelled cells because they are
    // not ranges and forcing them into the range shape would invent a minimum and a maximum.
    private IReadOnlyList<RangeCellViewModel> BuildRanges(SessionDetail detail)
    {
        var rigs = detail.Rigs ?? [];
        if (rigs.Count <= 1 && !CustomColumnSet.AnyRigScope(_customColumns))
        {
            return RangeCells([
                detail.Hfr,
                detail.Eccentricity,
                detail.Fwhm,
                detail.GuidingRmsArcsec,
                detail.SensorTemp,
            ]);
        }

        if (rigs.Count == 1)
        {
            // User choice 7, task brief section 3.2: the single rig still gets its label row, but
            // RigGroup.Ranges is null on a single-rig night (the ranges table is the session's own
            // and unchanged), so the five ranges under the label are the session's own rather than
            // a per-rig figure this query does not produce today. Never skipped: unlike the
            // multi-rig loop below, the session always has its own ranges to show.
            //
            // Ruling C23 (fix pass 1): no cells here. The filter table is the first table of the
            // pane and carries the one CustomCellGroup per rig (BuildFilterRows); this, the second
            // table, draws the label and the frame count only, because two editors over one stored
            // value would fall out of step with each other.
            var only = rigs[0];
            List<RangeCellViewModel> single =
            [
                RangeCellViewModel.RigLabelRow(only.Label, only.FrameCount),
            ];
            single.AddRange(RangeCells([
                detail.Hfr,
                detail.Eccentricity,
                detail.Fwhm,
                detail.GuidingRmsArcsec,
                detail.SensorTemp,
            ]));

            return single;
        }

        // Spec 12.4 item 2 names both tables, so the ranges table splits per rig on the same rule
        // the filter table does: a label row, then that rig's own five ranges, in rig order. The
        // figures come from the query, which pooled each rig's eccentricity on the session's own
        // modal source (spec 7.2), so the two blocks describe one source.
        //
        // Ruling C23 (fix pass 1): the label row here carries no cells. The filter table is the
        // first table of the pane and is the one CustomCellGroup per rig's home (BuildFilterRows);
        // repeating the same editor here would be a second one over the same stored value, and the
        // two would fall out of step with each other.
        List<RangeCellViewModel> rows = [];
        foreach (var rig in rigs)
        {
            // RangeCells drops a metric no frame of the rig measured, so a rig whose five ranges
            // are all unmeasured contributes nothing at all. Its label row is skipped with them
            // (Task 5 review P3), because a heading over an empty block states a rig's ranges and
            // then shows none.
            var cells = RangeCells(rig.Ranges ?? []);
            if (cells.Count == 0)
            {
                continue;
            }

            rows.Add(RangeCellViewModel.RigLabelRow(rig.Label, rig.FrameCount));
            rows.AddRange(cells);
        }

        return rows;
    }

    // Spec 12.4's order: HFR, eccentricity, FWHM, guiding RMS, sensor temperature. One table of
    // labels and formats for both the session's ranges and a rig's, because RigGroup.Ranges is
    // carried in exactly this order and a second copy of the order is what would drift.
    private static readonly (string Label, string Format)[] RangeColumns =
    [
        ("HFR", "0.00"),
        ("Eccentricity", "0.00"),
        ("FWHM", "0.00"),
        ("Guiding RMS", "0.00"),
        ("Sensor temp", "0.0"),
    ];

    private static IReadOnlyList<RangeCellViewModel> RangeCells(IReadOnlyList<MetricRangeSummary> ranges)
        // Only an unmeasured metric drops out. Spec 12.4 states the metric-group gate inside its
        // Frame table paragraph, so no display toggle hides a session range (review finding 2).
        => [.. ranges
            .Take(RangeColumns.Length)
            .Select((range, index) => new RangeCellViewModel(
                RangeColumns[index].Label, range, RangeColumns[index].Format))
            .Where(range => range.HasValues)];

    /// <summary>
    /// Cancels the card's background work, flushes an unsaved session note, and disposes the frame
    /// table and the chart. Called by <c>TargetDetailViewModel.DisposeCards</c> on every page load
    /// and on page close.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (Notes is not null)
        {
            // Before the lifetime is cancelled: a note typed and immediately collapsed away from
            // must not be lost. Bounded, because Dispose runs on the UI thread and a locked
            // database must not hang the window; the write itself is on the thread pool.
            try
            {
                Notes.FlushAsync().Wait(FlushBudget);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Flushing the session note on close failed");
            }

            Notes.Dispose();
        }

        // Spec 12.15, on the same rule as the note above: a value typed into a
        // ledger cell and immediately navigated away from must not be lost. Bounded for the same
        // reason, and awaited before the dispose so the write is known to have landed rather than
        // merely started (CustomCellGroup.Dispose's own remark). Skipped on the every-day case of a
        // card with no cells: the count and not the identity of the shared Empty group, so a card
        // holding an empty group of its own pays nothing either and no call site owes a rule about
        // which group instance it is holding.
        if (_ledgerCells.Cells.Count > 0)
        {
            try
            {
                _ledgerCells.FlushAsync().Wait(FlushBudget);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Flushing this night's custom column cells on close failed");
            }

            _ledgerCells.Dispose();
        }

        // Task 6b's own group per rig line, same rule, same budget, its own dictionary.
        DisposeAllRigCells();

        _lifetime.Cancel();
        DisposeChildren();
        DisposeGuiding();

        // The holder outlives every card, so a card that did not drop this handler would be kept
        // alive by it for the life of the process.
        _targetPage.PropertyChanged -= OnTargetPageChanged;

        // The strip and the frame table each hold a handler on this card; dropping them here is
        // what stops a disposed card being kept alive by its own children.
        DetachNightStrip();
        DetachFrameTable();
        _lifetime.Dispose();
    }

    private void DisposeChildren()
    {
        (Frames as IDisposable)?.Dispose();
        (Chart as IDisposable)?.Dispose();
        DisposeRigs();
    }

    // The section holds a handler on the process-wide holder, so it is disposed with the card and
    // not with the loaded detail: DisposeChildren runs on every Invalidate.
    private void DisposeGuiding() => Guiding?.Dispose();
}

/// <summary>
/// One cell of spec 12.4's session ranges: a metric's minimum, maximum and median. Every
/// <c>MetricRangeSummary</c> is all-null or all-populated (Task 2 handoff), so
/// <see cref="HasValues"/> is one flag rather than three.
/// </summary>
public sealed class RangeCellViewModel
{
    public RangeCellViewModel(string label, MetricRangeSummary range, string format)
    {
        Label = label;
        Range = range;
        MinText = MetricText.Format(range.Min, format);
        MaxText = MetricText.Format(range.Max, format);
        MedianText = MetricText.Format(range.Median, format);

        if (range.Min is { } min && range.Max is { } max && range.Median is { } median
            && double.IsFinite(min) && double.IsFinite(max) && double.IsFinite(median))
        {
            HasPosition = true;
            var span = max - min;
            MedianPosition = span > 0d
                ? Math.Clamp((median - min) / span, 0d, 1d)
                : 0.5d;
        }
    }

    // Spec 12.4 item 2's rig label row, in the ranges table this time. Private for the reason
    // FilterTableRowViewModel's is: a label row has no metric and no range, and an overload taking
    // a string and an int would be one mistyped argument away from a range row.
    private RangeCellViewModel(RigLabelRowViewModel row)
    {
        Label = "";
        Range = new MetricRangeSummary(null, null, null);
        MinText = "";
        MaxText = "";
        MedianText = "";
        LabelRow = row;
    }

    /// <summary>Spec 12.4 item 2: the row that opens one rig's block of the ranges table on a
    /// multi-rig night, in the same shape the filter table's label row takes. A single-rig night
    /// has none and its ranges table is exactly what it was.</summary>
    /// <remarks>It takes no cells, and cannot be handed any (review P3-1). Ruling C23 puts the rig
    /// cells on the first table of the pane only, because two live editors over one stored value fall
    /// out of step; the rule was kept at both call sites by not passing an argument, and an optional
    /// parameter left a third call site added later one argument away from the defect C23 exists to
    /// close.</remarks>
    public static RangeCellViewModel RigLabelRow(string rigLabel, int frameCount)
        => new(RigLabelRowViewModel.For(rigLabel, frameCount));

    /// <summary>The shared label row this cell is, or null on every cell that is not one. The
    /// filter table's rows hold the same type and the two tables share one <c>DataTemplate</c>
    /// over it (phase review P3-3).</summary>
    public RigLabelRowViewModel? LabelRow { get; }

    /// <summary>True on spec 12.4's rig label row, which the view renders as one full-width cell.
    /// </summary>
    public bool IsRigLabel => LabelRow is not null;

    /// <summary>The rig's canonical label. Empty on every row that is not a label row.</summary>
    public string RigLabel => LabelRow?.Label ?? "";

    /// <summary>"22 frames", beside the label. Empty on every row that is not a label row.
    /// </summary>
    public string RigFrameCountText => LabelRow?.FrameCountText ?? "";

    public string Label { get; }

    /// <summary>The read model behind the cell.</summary>
    public MetricRangeSummary Range { get; }

    public string MinText { get; }

    public string MaxText { get; }

    public string MedianText { get; }

    /// <summary>False when no frame of the night carried the metric, which takes the cell out of
    /// the list rather than showing three empty figures.</summary>
    public bool HasValues => MedianText.Length > 0 || MinText.Length > 0 || MaxText.Length > 0;

    /// <summary>
    /// Where the median sits between the minimum and the maximum, 0 to 1. It shows skew: a median
    /// near the minimum with a long tail to the maximum is the signature of a night with a few bad
    /// frames. 0.5 when the minimum equals the maximum, and 0 when any of the three is absent,
    /// with <see cref="HasPosition"/> false so the bar is absent rather than pinned left.
    /// </summary>
    public double MedianPosition { get; }

    /// <summary>Whether <see cref="MedianPosition"/> means anything, which is what the bar binds
    /// its visibility to.</summary>
    public bool HasPosition { get; }
}
