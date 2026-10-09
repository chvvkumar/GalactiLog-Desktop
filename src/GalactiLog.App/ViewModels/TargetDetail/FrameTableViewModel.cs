using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// design-spec 12.4's frame table, hosted inside one expanded session card: the 32 columns of the
/// eight-group list, each sortable in both directions, visibility persisted in
/// <c>display.columns.frames</c> and additionally gated by the <c>display.groups</c> metric-group
/// toggles, and the five per-frame actions.
/// <para>
/// It queries nothing. The frames arrive from the card's one <c>SessionDetailQuery</c>, and both
/// sorting and column visibility are in-memory operations over that list: re-querying to reorder
/// a few hundred already-loaded rows would be absurd, and hiding a column is a rendering change.
/// The only writes it performs are the column list and the column widths, through
/// <see cref="DisplayColumnWriter"/>, and those go to the database, never to a file.
/// </para>
/// </summary>
public sealed partial class FrameTableViewModel : ObservableObject, IDisposable
{
    /// <summary>One comparison per column key, so no caller re-derives which read-model field a
    /// column sorts on. Every one of design-spec 12.4's 32 keys is here: "Every column is sortable
    /// ascending and descending", the textual ones and the four always-on ones included. The
    /// selectors read <see cref="FrameRowViewModel.Row"/>, never the formatted cell text, so
    /// numbers order numerically rather than by their rendering.</summary>
    private static readonly Dictionary<string, Func<FrameRowViewModel, IComparable?>> SortSelectors =
        new(StringComparer.Ordinal)
        {
            ["time"] = row => row.Row.CaptureDate,
            ["file_name"] = row => row.Row.FileName,
            ["filter_used"] = row => row.Row.FilterUsed,
            ["exposure_time"] = row => row.Row.ExposureTime,

            ["median_hfr"] = row => row.Row.MedianHfr,
            ["eccentricity"] = row => row.Row.Eccentricity,
            ["fwhm"] = row => row.Row.Fwhm,
            ["detected_stars"] = row => row.Row.DetectedStars,

            ["guiding_rms_arcsec"] = row => row.Row.GuidingRmsArcsec,
            ["guiding_rms_ra_arcsec"] = row => row.Row.GuidingRmsRaArcsec,
            ["guiding_rms_dec_arcsec"] = row => row.Row.GuidingRmsDecArcsec,

            ["adu_mean"] = row => row.Row.AduMean,
            ["adu_median"] = row => row.Row.AduMedian,
            ["adu_stdev"] = row => row.Row.AduStdev,
            ["adu_min"] = row => row.Row.AduMin,
            ["adu_max"] = row => row.Row.AduMax,

            ["focuser_position"] = row => row.Row.FocuserPosition,
            ["focuser_temp"] = row => row.Row.FocuserTemp,

            ["ambient_temp"] = row => row.Row.AmbientTemp,
            ["dew_point"] = row => row.Row.DewPoint,
            ["humidity"] = row => row.Row.Humidity,
            ["pressure"] = row => row.Row.Pressure,
            ["wind_speed"] = row => row.Row.WindSpeed,
            ["wind_direction"] = row => row.Row.WindDirection,
            ["wind_gust"] = row => row.Row.WindGust,
            ["cloud_cover"] = row => row.Row.CloudCover,
            ["sky_quality"] = row => row.Row.SkyQuality,

            ["airmass"] = row => row.Row.Airmass,
            ["pier_side"] = row => row.Row.PierSide,
            ["rotator_position"] = row => row.Row.RotatorPosition,

            ["sensor_temp"] = row => row.Row.SensorTemp,
            ["camera_gain"] = row => row.Row.CameraGain,
        };

    /// <summary>
    /// The display titles: units in the inch-mark style from <see cref="TableHeads"/>, sentence
    /// case; <c>FrameColumns.All</c> keeps spec 12.4's verbatim titles. The comp puts the unit in
    /// the header and never in the cell, so a column of figures shares one decimal axis.
    /// </summary>
    /// <remarks>
    /// <c>FrameColumns.All</c> is not touched: it is Core, and its <c>Title</c> is documented as
    /// verbatim from design-spec 12.4's table. The rewrite is a display concern and belongs here,
    /// beside the Time column's zone-label rewrite. The persisted key is untouched either way.
    /// </remarks>
    private static readonly Dictionary<string, string> HeaderTitles = new(StringComparer.Ordinal)
    {
        ["exposure_time"] = TableHeads.Exposure,
        ["median_hfr"] = TableHeads.Hfr,
        ["eccentricity"] = TableHeads.Ecc,
        ["fwhm"] = TableHeads.Fwhm,
        ["detected_stars"] = TableHeads.Stars,
        ["guiding_rms_arcsec"] = TableHeads.Rms,
        ["guiding_rms_ra_arcsec"] = "RMS RA \"",
        ["guiding_rms_dec_arcsec"] = "RMS Dec \"",
        ["adu_mean"] = "ADU mean",
        ["adu_median"] = "ADU median",
        ["adu_min"] = "ADU min",
        ["adu_max"] = "ADU max",
        ["focuser_temp"] = "Focus temp",
        ["ambient_temp"] = "Ambient temp",
        ["dew_point"] = "Dew point",
        ["wind_direction"] = "Wind dir",
        ["sensor_temp"] = "Temp C",
    };

    /// <summary>What an abbreviated header stands for (spec item 5). A column missing here takes
    /// its title as its tip, so a text title dragged narrow enough to trim still reads in full.
    /// </summary>
    private static readonly Dictionary<string, string> HeaderTips = new(StringComparer.Ordinal)
    {
        ["exposure_time"] = TableHeads.ExposureTip,
        ["median_hfr"] = TableHeads.HfrTip,
        ["eccentricity"] = TableHeads.EccTip,
        ["fwhm"] = TableHeads.FwhmTip,
        ["guiding_rms_arcsec"] = TableHeads.RmsTip,
        ["guiding_rms_ra_arcsec"] = "Guiding RMS error in right ascension, arcseconds",
        ["guiding_rms_dec_arcsec"] = "Guiding RMS error in declination, arcseconds",
        ["adu_mean"] = "Analog-to-digital units",
        ["adu_median"] = "Analog-to-digital units",
        ["adu_stdev"] = "Analog-to-digital units",
        ["adu_min"] = "Analog-to-digital units",
        ["adu_max"] = "Analog-to-digital units",
        ["sky_quality"] = "Sky quality meter, magnitudes per square arcsecond",
        ["sensor_temp"] = "Sensor temperature, degrees Celsius",
    };

    /// <summary>One formatted cell text per column key, what the view measures for R5's auto-fit.
    /// The same texts the row template binds, so the fit and the rendering cannot disagree; the
    /// RMS cell carries its source mark, which shares the column.</summary>
    private static readonly Dictionary<string, Func<FrameRowViewModel, string>> CellTexts =
        new(StringComparer.Ordinal)
        {
            ["time"] = row => row.TimeText,
            ["file_name"] = row => row.FileName,
            ["filter_used"] = row => row.FilterText,
            ["exposure_time"] = row => row.ExposureText,

            ["median_hfr"] = row => row.HfrCell.Text,
            ["eccentricity"] = row => row.EccentricityCell.Text,
            ["fwhm"] = row => row.FwhmCell.Text,
            ["detected_stars"] = row => row.DetectedStarsCell.Text,

            ["guiding_rms_arcsec"] = row => row.GuidingRmsCell.Text + row.GuidingRmsSourceGlyph,
            ["guiding_rms_ra_arcsec"] = row => row.GuidingRmsRaText,
            ["guiding_rms_dec_arcsec"] = row => row.GuidingRmsDecText,

            ["adu_mean"] = row => row.AduMeanText,
            ["adu_median"] = row => row.AduMedianCell.Text,
            ["adu_stdev"] = row => row.AduStdevText,
            ["adu_min"] = row => row.AduMinText,
            ["adu_max"] = row => row.AduMaxText,

            ["focuser_position"] = row => row.FocuserPositionText,
            ["focuser_temp"] = row => row.FocuserTempText,

            ["ambient_temp"] = row => row.AmbientTempText,
            ["dew_point"] = row => row.DewPointText,
            ["humidity"] = row => row.HumidityText,
            ["pressure"] = row => row.PressureText,
            ["wind_speed"] = row => row.WindSpeedText,
            ["wind_direction"] = row => row.WindDirectionText,
            ["wind_gust"] = row => row.WindGustText,
            ["cloud_cover"] = row => row.CloudCoverText,
            ["sky_quality"] = row => row.SkyQualityText,

            ["airmass"] = row => row.AirmassText,
            ["pier_side"] = row => row.PierSideText,
            ["rotator_position"] = row => row.RotatorPositionText,

            ["sensor_temp"] = row => row.SensorTempText,
            ["camera_gain"] = row => row.CameraGainText,
        };

    private readonly DisplayColumnWriter _columns;
    private readonly Action<EventHandler<DisplaySettings>>? _unsubscribeDisplayChanged;

    // R5: the widths the profile stores for this table, overlaid with what this process has
    // written since the display snapshot was read; and the auto-fit the view last measured per
    // column, which is what a stored width falls back to when cleared.
    private readonly Dictionary<string, double> _storedWidths;
    private readonly Dictionary<string, double> _autoWidths = new(StringComparer.Ordinal);
    private readonly Action<Action> _post;
    private readonly ShellIntegration _shell;
    private readonly Action<IReadOnlyList<FrameRowViewModel>, int>? _openPreview;
    private readonly Func<Guid, FrameHeaders?> _getHeaders;
    private readonly ILogger _logger;

    // Cancelled by Dispose, and handed to every raw header panel this table builds so a read still
    // in flight when the card collapses drops its result instead of posting it (phase review
    // item 4).
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;

    // The guard that makes Dispose idempotent, the shape SessionCardViewModel and RigViewModel
    // already use.
    private bool _disposed;

    // Capture order, as SessionDetailQuery returned it. Sorting projects from this into Rows, so
    // a sort is never a sort of an already-sorted list and the tie-break order is stable.
    private readonly List<FrameRowViewModel> _captureOrder;

    // Spec 12.4's page-wide "Compare to" choice. Never a snapshot of its value: the holder itself,
    // so a flip on another night's pane is visible here (P13 phase review P2-1).
    private readonly TargetPageState _targetPage;

    /// <param name="frames">The session's frame rows from <c>SessionDetailQuery</c>, in capture
    /// order.</param>
    /// <param name="display">The display document as the host read it, by value (ruling Q13). The
    /// gate is evaluated once from it and re-evaluated afterwards through
    /// <paramref name="subscribeDisplayChanged"/>.</param>
    /// <param name="columns">The process-wide <c>display.columns</c> writer, shared with the
    /// dashboard's target list. The only write this view-model performs.</param>
    /// <param name="shell">Task 3's <c>ShellIntegration</c>: reveal, open-with, clipboard. The
    /// only way a frame path leaves this process, and it writes nothing (design-spec 2.1).</param>
    /// <param name="openPreview">design-spec 11.5's preview modal, handed <see cref="Rows"/> in
    /// its current sort order and the clicked row's index within it: navigation is within the
    /// frame list the modal was opened from, and stepping must follow what the user sees rather
    /// than capture order (Phase 8 Task 7, ruling Q16). Null still leaves the action disabled,
    /// which is what an unbound seam should look like on screen.</param>
    /// <param name="general"><c>general.timezone</c> and <c>general.use_24h_time</c> for the Time
    /// column (design-spec 5.8.1). Display formatting only; <c>session_date</c> is never touched
    /// by it.</param>
    /// <param name="getHeaders">Task 6's <c>FrameHeadersQuery.Get</c>. Handed to a row's raw
    /// header panel only when that row's panel is first expanded (<see cref="ToggleRawHeaders"/>),
    /// never issued up front for every row.</param>
    /// <param name="logger">Optional. Passed through to each row's raw header panel: a failed
    /// header read is logged and surfaced on that panel, never rethrown on the UI thread. This is
    /// the first thing in this view-model with a failure path worth logging (Task 5 handoff, D6).
    /// </param>
    /// <param name="subscribeDisplayChanged">Normally
    /// <c>handler =&gt; settingsStore.DisplayChanged += handler</c> (Phase 9 FIXER item 7). Only
    /// the <c>groups</c> half of the document is taken from it: the <c>columns</c> half keeps
    /// arriving through <see cref="DisplayColumnWriter.Changed"/>, which is design-spec 5.8.2's
    /// own split and is what stops one column click from being applied twice. Null leaves the
    /// gate on the constructor's value, which is what a unit test that is not about settings
    /// changes wants.</param>
    /// <param name="unsubscribeDisplayChanged">The matching detach, called from
    /// <see cref="Dispose"/>. Frame tables are transient, one per expanded session card, so
    /// without it a collapsed card's table stays reachable from the store for the life of the
    /// process.</param>
    /// <param name="post">How to reach the UI thread from the thread that saved. Defaults to
    /// <c>UiPost.Default</c>.</param>
    /// <param name="targetPage">The process-wide <c>display.target_page</c> holder, which carries
    /// spec 12.4's "Compare to" baseline. Null gives the table its own holder on the fresh-profile
    /// defaults, which is what every test that is not about the baseline wants and is the same
    /// idiom <c>SessionCardViewModel</c> already uses for this holder.</param>
    /// <param name="datePrefixed">P25 R7: true when the detail spans more than one night, so
    /// every row's Time cell carries its local capture date.</param>
    public FrameTableViewModel(
        IReadOnlyList<FrameRow> frames,
        DisplaySettings display,
        DisplayColumnWriter columns,
        ShellIntegration shell,
        Action<IReadOnlyList<FrameRowViewModel>, int>? openPreview,
        GeneralSettings general,
        Func<Guid, FrameHeaders?> getHeaders,
        ILogger? logger = null,
        Action<EventHandler<DisplaySettings>>? subscribeDisplayChanged = null,
        Action<EventHandler<DisplaySettings>>? unsubscribeDisplayChanged = null,
        Action<Action>? post = null,
        TargetPageState? targetPage = null,
        bool datePrefixed = false)
    {
        _columns = columns;
        _targetPage = targetPage ?? new TargetPageState();
        _unsubscribeDisplayChanged = unsubscribeDisplayChanged;
        _post = post ?? UiPost.Default;
        _shell = shell;
        _openPreview = openPreview;
        _getHeaders = getHeaders;
        _logger = logger ?? NullLogger.Instance;

        // Read once: CancellationTokenSource.Token throws after the source is disposed, and the
        // token itself keeps answering IsCancellationRequested.
        _lifetimeToken = _lifetime.Token;

        // Spec 5.8.2's two rules, applied in the order the spec states them and kept separate:
        // the persisted list with its documented fallback, and IsGroupEnabled as the metric-group
        // gate layered on top. Folding them together would lose the distinction the picker needs,
        // which is that a gated column is still in the persisted list.
        //
        // The persisted list comes from the writer first (review finding 1). display is the
        // snapshot AppHost read on Program.Main's thread (ruling Q13) and every table on the page
        // is built from that same value, so once one table has toggled a column the snapshot is
        // stale: a table built afterwards would show the hidden column and its own first toggle
        // would rewrite the list from the snapshot and revert the hide. LastWritten is null until
        // this process has written the table, which is the normal case, and it reads no settings.
        var visible = _columns.LastWritten(DisplaySettings.FramesTableId)
            ?? display.ColumnsFor(DisplaySettings.FramesTableId);

        // Spec 5.8.1's zone, resolved once: FindSystemTimeZoneById is a system lookup and must
        // not run per row. The header used to read "Time (<raw stored id>)", which on Windows is
        // the zone's own id ("Time (India Standard Time)") rather than anything a reader parses
        // at a glance, and at the Time column's 78 px width it also painted over the File name
        // header beside it (pre-existing since Phase 6, phase-review carried observation,
        // fixer-list item 20). It now reads the zone's GMT offset, "Time (GMT+05:30)", built from
        // TimezoneOptions.FormatOffset, the same standard-offset formatter ruling U4 gave the two
        // Location selectors, so the header and the selectors can never disagree on how an offset
        // is written. BaseUtcOffset is the zone's standard offset, never today's actual one, so
        // the header does not change wording when daylight saving starts or ends (U4).
        var zone = SessionTimeFormat.Resolve(general.DisplayTimezoneId);
        var zoneLabel = TimezoneOptions.FormatOffset(zone.BaseUtcOffset);

        Columns = [.. FrameColumns.All.Select(column =>
        {
            var title = HeaderTitle(column, zoneLabel);
            return new ColumnViewModel(
                column.Key,
                title,
                visible.Contains(column.Key),

                // Ruling Q15: every frame column is hideable. No single one is the row's identity,
                // a group-gated column already renders nothing, and the picker is always in the
                // header.
                canHide: true,
                isGroupEnabled: FrameColumns.IsGroupEnabled(column, display),

                // Spec 14.4's alignment, carried from Core's own column table rather than restated
                // in the markup: the header cell aligns over its figures instead of at the far edge.
                isNumeric: column.IsNumeric)
            {
                Tip =HeaderTips.GetValueOrDefault(column.Key) ?? title,
            };
        })];

        // Spec 12.4's rig order (PAR-004): first capture time within the night, which is the first
        // appearance of each label in the frames the query already ordered by capture_date. The
        // same rule SessionDetailQuery applies over the same rows, so the table's rig order and
        // the query's cannot disagree and no second list has to be threaded in here.
        List<string> rigs = [];
        Dictionary<string, int> rigIndices = new(StringComparer.Ordinal);
        foreach (var frame in frames)
        {
            if (rigIndices.TryAdd(frame.Rig, rigs.Count))
            {
                rigs.Add(frame.Rig);
            }
        }

        Rigs = rigs;

        // One pill per rig, all checked on open (spec 12.4 item 3). Per visit and not persisted:
        // it names which of tonight's rigs the reader is looking at, which is not a preference.
        // Empty on a single-rig night, where the toolbar draws no pills at all.
        RigPills = IsMultiRig
            ? [.. rigs.Select(label => new ToggleOptionViewModel(label, label) { IsSelected = true })]
            : [];
        foreach (var pill in RigPills)
        {
            pill.PropertyChanged += OnRigPillChanged;
        }

        _captureOrder = [.. frames.Select(frame => new FrameRowViewModel(
            frame, Columns, zone, general.Use24HTime, _targetPage.GradingBaseline, datePrefixed)
        {
            RigIndex = rigIndices[frame.Rig],
        })];

        Rows = [.. _captureOrder];
        RecomputeTally();
        SortKey = "";

        // R5: a stored width wins from the first render; every other column starts at the floor
        // rather than at zero and takes the view's auto-fit as soon as it is measured.
        _storedWidths = display.ColumnWidthsFor(DisplaySettings.FramesTableId);
        foreach (var (key, written) in _columns.LastWrittenWidths(DisplaySettings.FramesTableId))
        {
            if (written is { } value)
            {
                _storedWidths[key] = value;
            }
            else
            {
                _storedWidths.Remove(key);
            }
        }

        foreach (var column in Columns)
        {
            column.Width = _storedWidths.TryGetValue(column.Key, out var stored)
                ? Math.Max(ColumnFloor, stored)
                : ColumnFloor;
        }

        // The selection drives three read-only surfaces and two commands, and nothing else
        // watches it, so one handler keeps all five honest. Detached in Dispose.
        SelectedRows.CollectionChanged += OnSelectedRowsChanged;

        // Two session cards can be expanded at once, so two frame tables can be live over the same
        // display.columns.frames entry. Each one applies the other's toggle here, which is what
        // stops the second table's next toggle from rewriting the document from its own stale list
        // and reverting the first table's hide (phase review item 1).
        _columns.Changed += OnColumnsChanged;

        // FIXER item 7: display.groups has a writer in the process now (the Settings Display
        // tab), so a group turned off must hide its columns here without a restart.
        subscribeDisplayChanged?.Invoke(OnDisplayChanged);

        // Spec 12.4's "Compare to" toggle. The holder is process wide, so a flip on one night's
        // table reaches every other live table and every table built afterwards (P13 phase review
        // P2-1). Detached in Dispose, beside the column writer's detach.
        _targetPage.PropertyChanged += OnTargetPageChanged;
    }

    /// <summary>The rows in their current sort order, capture order until the first sort. Replaced
    /// in place rather than rebound, so the hosting <c>ItemsControl</c> keeps its scroll position
    /// behaviour predictable.</summary>
    public ObservableCollection<FrameRowViewModel> Rows { get; }

    /// <summary>The night's rigs (PAR-004), in first-capture order. One entry on the ordinary
    /// single-rig night.</summary>
    public IReadOnlyList<string> Rigs { get; }

    /// <summary>Spec 12.4's "A multi-rig night". It is what draws the Rig column and the pill row,
    /// and a single-rig night draws neither.</summary>
    public bool IsMultiRig => Rigs.Count > 1;

    /// <summary>Spec 12.4 item 3's rig pills, one per rig, all checked on open, empty on a
    /// single-rig night. The same <c>ToggleOptionViewModel</c> the chart pill rows use; the two are
    /// separate filters and do not drive each other (spec 13): one hides a line, the other hides a
    /// row.</summary>
    public IReadOnlyList<ToggleOptionViewModel> RigPills { get; }

    /// <summary>All 32 columns in design-spec 12.4's order, whatever their visibility or gate.
    /// The column picker binds this; the table binds <see cref="VisibleColumns"/>.</summary>
    public IReadOnlyList<ColumnViewModel> Columns { get; }

    /// <summary>The rendered subset: in the persisted visible list and group-enabled. An empty
    /// result is legal (ruling Q15): the table renders its header row and empty rows, and the
    /// picker is how the user gets back out of it.</summary>
    public IEnumerable<ColumnViewModel> VisibleColumns
        => Columns.Where(column => column.IsShown);

    /// <summary>
    /// The rows the user has selected, in the order the table shows them, handed to the
    /// <c>ListBox</c> as its <c>SelectedItems</c> so the control and this collection are one
    /// object rather than two that synchronise. Empty by default: the table had no selection
    /// model at all before this phase, and P12's "which frames should I reject, and get their
    /// paths out" is what put one in.
    /// </summary>
    /// <remarks>
    /// A caller must not assume the order a <c>ListBox</c> fills this in: clicking the third row
    /// and then the first leaves them in click order. Everything that cares about order projects
    /// through <see cref="Rows"/>, which is what the user sees.
    /// </remarks>
    public ObservableCollection<FrameRowViewModel> SelectedRows { get; } = [];

    /// <summary>
    /// Task 7's projection (user ruling U1): the <see cref="_captureOrder"/> position of the one
    /// selected row, or null when the selection is empty or holds more than one row. A read of
    /// state <see cref="SelectedRows"/> already owns, recomputed in <see cref="OnSelectedRowsChanged"/>
    /// alongside the other four selection-driven surfaces, never written from outside: the strip's
    /// active tick at rest is a projection of the table's selection, not a second source of truth
    /// for it.
    /// </summary>
    [ObservableProperty]
    public partial int? SelectedCaptureIndex { get; private set; }

    /// <summary>
    /// R9's hovered row: the frame the night strip's pointer is over, tinted but not selected.
    /// Null when the pointer is between ticks, has left the strip, or is over a frame the current
    /// outlier filter hides. Distinct from <see cref="SelectedRows"/> on purpose: a hover must not
    /// change what Copy paths would copy.
    /// </summary>
    /// <remarks>This is also the seam the view watches: <c>FrameTableView</c> subscribes to this
    /// property and calls <c>ScrollIntoView</c>, because scrolling is a control call and no
    /// view-model can make it (ruling Q7).</remarks>
    [ObservableProperty]
    public partial FrameRowViewModel? HighlightedRow { get; private set; }

    /// <summary>Every loaded frame's formatted text for one column, in capture order, for the
    /// view's auto-fit measurement (R5). The whole night, not the filtered subset: a filter must
    /// not resize a column (ruling Q9). Empty for a key that is not a column.</summary>
    public IReadOnlyList<string> CellTextsToMeasure(string columnKey)
        => CellTexts.TryGetValue(columnKey, out var text) ? [.. _captureOrder.Select(text)] : [];

    /// <summary>The column key the rows are ordered by, empty before the first sort.</summary>
    [ObservableProperty]
    public partial string SortKey { get; private set; }

    [ObservableProperty]
    public partial bool Descending { get; private set; }

    /// <summary>Which finding the table is filtered to. Set through
    /// <see cref="SetOutlierFilter"/>, never assigned from outside, so the re-projection and the
    /// sort cannot be skipped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered))]
    [NotifyPropertyChangedFor(nameof(ShownCountText))]
    [NotifyCanExecuteChangedFor(nameof(ClearFilterOrSelectionCommand))]
    public partial FrameOutlierFilter OutlierFilter { get; private set; }

    /// <summary>Whether any finding filter is applied. The toolbar's clear affordance and the
    /// Escape key both read it (ruling Q14).</summary>
    public bool IsFiltered => OutlierFilter != FrameOutlierFilter.None;

    // ---- spec 12.4's grading tally (PAR-003) --------------------------------------------------
    //
    // The counts are over the loaded night's frames, which is _captureOrder and not Rows: spec
    // 12.4 says "the loaded night's frames", and a tally that moved when the outlier filter was
    // toggled would make the Copy Frame List dialog disagree with the table.
    //
    // Better and neutral are counted together as good, which is what makes three counts out of
    // four bands: the watch band exists to be looked at, not to be excluded.

    /// <summary>Frames whose row score falls in the better or the neutral band.</summary>
    [ObservableProperty]
    public partial int GoodCount { get; private set; }

    /// <summary>Frames whose row score falls in the watch band.</summary>
    [ObservableProperty]
    public partial int WatchCount { get; private set; }

    /// <summary>Frames whose row score falls in the reject band.</summary>
    [ObservableProperty]
    public partial int RejectCount { get; private set; }

    /// <summary>Frames the grading produced no score for. In no count, and named on the line only
    /// when there is at least one.</summary>
    [ObservableProperty]
    public partial int UngradedCount { get; private set; }

    /// <summary>The mean row score over the scored frames, or null when none is scored.</summary>
    [ObservableProperty]
    public partial double? MeanScore { get; private set; }

    /// <summary>The whole tally line as one string, for a test to assert and for a pane too narrow
    /// for the four runs to fall back to. The markup renders the four runs, so each count carries
    /// its own band ink; this is the same line composed in one piece.</summary>
    public string TallyText
    {
        get
        {
            var head = string.Create(
                CultureInfo.InvariantCulture,
                $"Good {GoodCount}   Watch {WatchCount}   Reject {RejectCount}");

            if (MeanScore is { } mean)
            {
                head += string.Create(
                    CultureInfo.InvariantCulture,
                    $"   Mean score {Math.Round(mean, MidpointRounding.AwayFromZero):0}");
            }

            return UngradedCount == 0
                ? head
                : head + string.Create(CultureInfo.InvariantCulture, $"   {UngradedCount} ungraded");
        }
    }

    /// <summary>Whether the tally line is drawn at all. A night with no frame has nothing to
    /// count.</summary>
    public bool HasTally => _captureOrder.Count > 0;

    /// <summary>The mean score to the nearest whole number, for the markup's own run.</summary>
    public string MeanScoreText => MeanScore is { } mean
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(mean, MidpointRounding.AwayFromZero):0}")
        : "";

    public bool HasMeanScore => MeanScore is not null;

    public bool HasUngraded => UngradedCount > 0;

    /// <summary>
    /// "6 of 158 shown, HFR outliers, 2 selected". The comp's line under the Frames heading. Each
    /// clause drops out when it does not apply, so an unfiltered table with no selection reads
    /// "158 frames".
    /// </summary>
    /// <remarks>
    /// The comp's clause names the threshold ("above 2.73 px HFR"). This one does not: the
    /// threshold lives in the insight's prose, which <c>SessionDetailQuery</c>'s own rule forbids
    /// parsing, and the findings line directly above the table already prints it. Reproducing the
    /// number here would be a second, independently computed copy of a figure that must agree.
    /// </remarks>
    public string ShownCountText
    {
        get
        {
            var total = _captureOrder.Count;

            // Spec 12.4 item 3: the rig pills and the outlier filter compose and this line reports
            // the composed result, so a table narrowed by pills alone still says how many of how
            // many are shown. It carries no clause then: the pills are on screen beside it and
            // naming them here would restate what the reader just clicked.
            var head = IsFiltered || Rows.Count != total
                ? FilterClause.Length > 0
                    ? $"{MetricText.Count(Rows.Count)} of {MetricText.Count(total)} shown, {FilterClause}"
                    : $"{MetricText.Count(Rows.Count)} of {MetricText.Count(total)} shown"
                : $"{MetricText.Count(total)} {(total == 1 ? "frame" : "frames")}";

            return SelectedRows.Count == 0
                ? head
                : $"{head}, {MetricText.Count(SelectedRows.Count)} selected";
        }
    }

    /// <summary>"Copy paths (2)". The count is in the label because the action is destructive of
    /// the clipboard and the user should see how many before clicking.</summary>
    public string CopySelectedPathsText => $"Copy paths ({SelectedRows.Count})";

    private string FilterClause => OutlierFilter switch
    {
        FrameOutlierFilter.Hfr => "HFR outliers",
        FrameOutlierFilter.Eccentricity => "eccentricity outliers",
        FrameOutlierFilter.Fwhm => "FWHM outliers",
        FrameOutlierFilter.Stars => "star count outliers",
        FrameOutlierFilter.GuidingRms => "guiding RMS outliers",
        _ => "",
    };

    /// <summary>Sorts by a column key, flipping direction on a repeat click (design-spec 12.4:
    /// "Every column is sortable ascending and descending"). In memory over the card's loaded
    /// frames; nothing here queries.</summary>
    [RelayCommand]
    private void SortBy(string? columnKey)
    {
        // A key with no selector is ignored rather than clearing the sort, the shape
        // TargetListViewModel.SortBy already uses for its two unsortable columns.
        if (columnKey is null || !SortSelectors.ContainsKey(columnKey))
        {
            return;
        }

        if (SortKey == columnKey)
        {
            Descending = !Descending;
        }
        else
        {
            SortKey = columnKey;
            Descending = false;
        }

        ApplySort();
    }

    // ---- the selection's two actions (design-spec 12.4, P12 R2) -------------------------------

    /// <summary>
    /// design-spec 12.4's "copy path" applied to n rows: one absolute path per line, newline
    /// separated, with a trailing newline, in the order the table shows them. Exactly the format
    /// the target-level "Copy frame list" already writes, because it is the same method: P12 R2
    /// amends spec 12.4's sentence "the frame table's per-frame copy path covers the single-file
    /// case", and one helper rather than two is what stops the two copies from disagreeing about
    /// a separator (design lesson 1, the second occurrence).
    /// </summary>
    /// <remarks>
    /// No dialog, no grading, no format picker and no script output. A move-or-delete script is a
    /// file-mutation affordance this application never ships (spec 2.1), and this action stays
    /// inside that rule: it writes text to the clipboard and touches no file.
    /// <c>ShellIntegration</c> is the only way a path leaves this process and it has no write
    /// member.
    /// <para>
    /// No repeat of the <c>CanExecute</c> guard, unlike <see cref="RevealSelected"/>: an empty
    /// selection reaches <c>CopyFrameListAsync</c> with an empty list, which copies nothing at
    /// all. A reviewer reading this as a missed guard is reading the one case where the shared
    /// helper already does the right thing.
    /// </para>
    /// <para>
    /// The paths are read from <see cref="Rows"/> filtered by the selection, not from
    /// <see cref="SelectedRows"/> in its own order: a <c>ListBox</c> appends in click order, and
    /// spec 12.4's list is the one the user is looking at.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanCopySelectedPaths))]
    private Task CopySelectedPathsAsync()
        => _shell.CopyFrameListAsync([.. SelectedPathsInTableOrder()]);

    private bool CanCopySelectedPaths() => SelectedRows.Count > 0;

    /// <summary>design-spec 12.4's "reveal in Explorer" for the selection. Enabled at exactly one
    /// selected row, because <c>explorer.exe /select</c> highlights one file and a two-row
    /// selection has no single answer; the per-row menu still reveals any single row.</summary>
    /// <remarks><c>RelayCommand.Execute</c> ignores <c>CanExecute</c> (TRACKING section 6 item
    /// 13), which is why the count check is repeated in the body rather than left to the
    /// enablement.</remarks>
    [RelayCommand(CanExecute = nameof(CanRevealSelected))]
    private void RevealSelected()
    {
        if (SelectedRows.Count == 1)
        {
            _shell.RevealInExplorer(SelectedRows[0].FilePath);
        }
    }

    private bool CanRevealSelected() => SelectedRows.Count == 1;

    // ---- the outlier filter (design-spec 12.4, P12 R3, ruling Q14) ----------------------------

    /// <summary>
    /// Applies the comp's "show" action. Re-projects <see cref="Rows"/> from capture order through
    /// the filter and the current sort, and for <see cref="FrameOutlierFilter.Hfr"/> sorts HFR
    /// descending first, because the question the action answers is "which are the worst" and a
    /// user who had to click the header twice to get there was doing the tool's work
    /// (panel-workflow.md T3: the first header click is ascending).
    /// </summary>
    /// <remarks>
    /// Clearing the filter restores every row and leaves the sort where the filter put it (ruling
    /// Q14): re-sorting on clear would move rows under a pointer about to click one, and the sort
    /// header still says what it says.
    /// <para>
    /// This is a plain setter, not a toggle. The comp's findings action reads show/shown and
    /// toggles (ruling Q14); the caller owns that, because only it knows which finding its line
    /// is about, and it passes <see cref="FrameOutlierFilter.None"/> to turn its own filter off.
    /// </para>
    /// </remarks>
    public void SetOutlierFilter(FrameOutlierFilter filter)
    {
        OutlierFilter = filter;

        if (filter == FrameOutlierFilter.Hfr)
        {
            SortKey = "median_hfr";
            Descending = true;
        }

        Project();
        UpdateSortGlyphs();
    }

    /// <summary>Restores every row. Bound to the toolbar's clear affordance and reached by Escape
    /// through <see cref="ClearFilterOrSelection"/>.</summary>
    [RelayCommand]
    private void ClearOutlierFilter() => SetOutlierFilter(FrameOutlierFilter.None);

    /// <summary>Ruling Q14: Escape clears the filter before the selection. One press never does
    /// both, so a user who filtered and then selected steps back out the way they came in.
    /// </summary>
    /// <remarks>
    /// Disabled when there is neither a filter nor a selection, and that enablement is load
    /// bearing rather than cosmetic. The table's Escape is a <c>KeyBinding</c>, and a
    /// <c>KeyBinding</c> whose command reports it cannot execute leaves the key event unhandled,
    /// so an empty press bubbles out of the table to <c>TargetDetailView</c>'s own handler, which
    /// closes the Details drawer or navigates back. Without the guard the frame table would
    /// silently swallow the page's Escape from the moment the user clicked a row, because focus
    /// is then inside the table and the table is the deeper element (review P2-1, ruling (b)).
    /// <para>
    /// The body repeats the guard because <c>RelayCommand.Execute</c> ignores <c>CanExecute</c>
    /// (TRACKING section 6 item 13, HANDOFF 4 item 8).
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanClearFilterOrSelection))]
    private void ClearFilterOrSelection()
    {
        if (IsFiltered)
        {
            ClearOutlierFilter();
            return;
        }

        if (SelectedRows.Count > 0)
        {
            SelectedRows.Clear();
        }
    }

    private bool CanClearFilterOrSelection() => IsFiltered || SelectedRows.Count > 0;

    /// <summary>Selects the row at an index into capture order, and nothing else, which is what
    /// the night strip's <c>FrameSelected</c> hands over. A row the current filter hides is not
    /// selected and the call is a no-op: the strip can name a frame the table is not showing.
    /// </summary>
    public void SelectFrameAt(int captureIndex)
    {
        if (captureIndex < 0 || captureIndex >= _captureOrder.Count)
        {
            return;
        }

        var row = _captureOrder[captureIndex];
        if (!Rows.Contains(row))
        {
            return;
        }

        SelectedRows.Clear();
        SelectedRows.Add(row);
    }

    /// <summary>R5's floor, the least a drag or an auto-fit leaves any column: enough for a cell
    /// to keep its first characters so a column can never be dragged out of existence. There is
    /// no ceiling: a wide value pushes the row wider and the horizontal scroll covers it.</summary>
    public const double ColumnFloor = 48d;

    /// <summary>Whether the profile stores a width for a column, in which case the auto-fit is
    /// measured but not applied until the stored width is cleared.</summary>
    public bool HasStoredWidth(string columnKey) => _storedWidths.ContainsKey(columnKey);

    /// <summary>Records the width the view measured for a column's widest cell and header, and
    /// applies it unless the profile stores a width for that column. A non-finite or non-positive
    /// figure is refused outright: a measurement taken before the control has a typeface produces
    /// one, and a NaN width reaches layout as a silently unmeasurable column.</summary>
    public void SetAutoFitWidth(string columnKey, double width)
    {
        if (!double.IsFinite(width) || width <= 0d || Column(columnKey) is not { } column)
        {
            return;
        }

        _autoWidths[columnKey] = Math.Max(ColumnFloor, width);
        if (!_storedWidths.ContainsKey(columnKey))
        {
            column.Width = _autoWidths[columnKey];
        }
    }

    /// <summary>A drag in progress: the column takes the width live, floored, and nothing is
    /// written until <see cref="StoreColumnWidth"/>.</summary>
    public void SetColumnWidth(string columnKey, double width)
    {
        if (double.IsFinite(width) && Column(columnKey) is { } column)
        {
            column.Width = Math.Max(ColumnFloor, width);
        }
    }

    /// <summary>The drag's release: the column's current width becomes its stored width, so it
    /// wins over the auto-fit on this night and on every later one until cleared.</summary>
    public void StoreColumnWidth(string columnKey)
    {
        if (Column(columnKey) is { } column)
        {
            _storedWidths[columnKey] = column.Width;
            _columns.WriteWidth(DisplaySettings.FramesTableId, columnKey, column.Width);
        }
    }

    /// <summary>The divider's double-click: clears the stored width and returns the column to the
    /// auto-fit last measured, or the floor when nothing has been measured yet.</summary>
    public void ResetColumnWidth(string columnKey)
    {
        if (Column(columnKey) is not { } column)
        {
            return;
        }

        if (_storedWidths.Remove(columnKey))
        {
            _columns.WriteWidth(DisplaySettings.FramesTableId, columnKey, null);
        }

        column.Width = _autoWidths.TryGetValue(columnKey, out var auto) ? auto : ColumnFloor;
    }

    private ColumnViewModel? Column(string columnKey)
        => Columns.FirstOrDefault(column => column.Key == columnKey);

    /// <summary>Highlights the row at an index into capture order. Out of range, null, or a row
    /// the current filter hides clears the highlight rather than leaving a stale one stranded:
    /// moving from a shown tick to a hidden one must put the tint out (R9, ruling Q13). That one
    /// difference apart, this is <see cref="SelectFrameAt"/>'s shape.</summary>
    public void HighlightFrameAt(int? captureIndex)
    {
        if (captureIndex is not { } index || index < 0 || index >= _captureOrder.Count)
        {
            HighlightedRow = null;
            return;
        }

        var row = _captureOrder[index];
        HighlightedRow = Rows.Contains(row) ? row : null;
    }

    /// <summary>
    /// Spec 12.4's thumbnail click (PAR-008): opens the preview at one frame of the night,
    /// <em>whatever the table is currently filtered to</em>, over the night's own capture order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not <see cref="SelectAndPreviewFrameAt"/>, which is R8's row-and-tick entry
    /// point and correctly no-ops for a row the current filter hides. The reference frame is by
    /// construction the sharpest frame of its set, so with either outlier filter on it is never in
    /// <see cref="Rows"/> and that route would make the click dead; the same holds for a rig whose
    /// own pill is unchecked. Spec 12.4 states the click with no condition (review P2-3).
    /// </para>
    /// <para>
    /// The filter is left exactly as the user set it and nothing is cleared on their behalf. The
    /// selection follows only when the current filter admits the row, through
    /// <see cref="SelectFrameAt"/>'s own guard, because selecting a row the table is not showing
    /// would put the toolbar's "Copy paths (1)" on a frame nobody can see.
    /// </para>
    /// <para>
    /// The list handed to the modal is the night's capture order rather than the filtered rows, so
    /// the arrow keys step through the whole night from there, which is the second half of the
    /// spec's own sentence.
    /// </para>
    /// </remarks>
    public void PreviewFrameAt(int captureIndex)
    {
        if (captureIndex < 0 || captureIndex >= _captureOrder.Count)
        {
            return;
        }

        SelectFrameAt(captureIndex);
        _openPreview?.Invoke(_captureOrder, captureIndex);
    }

    /// <summary>R8 and R9: selects that one row and opens the preview at it, which is what both a
    /// plain row click and a tick click do. A row the current filter hides selects nothing and
    /// opens nothing, the same no-op <see cref="SelectFrameAt"/> already performs.</summary>
    /// <remarks>The selection is read back rather than assumed, so the guards live in
    /// <see cref="SelectFrameAt"/> alone. The identity check is what stops a tick naming a hidden
    /// frame from opening the preview at whatever row happened to be selected already.</remarks>
    public void SelectAndPreviewFrameAt(int captureIndex)
    {
        var named = captureIndex >= 0 && captureIndex < _captureOrder.Count
            ? _captureOrder[captureIndex]
            : null;

        SelectFrameAt(captureIndex);

        if (named is not null && SelectedRows.Count == 1 && ReferenceEquals(SelectedRows[0], named))
        {
            OpenPreviewCommand.Execute(named);
        }
    }

    // One row carries the tint at a time, and the flag is the row's own so the template binds a
    // property rather than walking up the tree to ask the table.
    partial void OnHighlightedRowChanged(FrameRowViewModel? oldValue, FrameRowViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsHighlighted = false;
        }

        if (newValue is not null)
        {
            newValue.IsHighlighted = true;
        }
    }

    /// <summary>Flips a column's persisted visibility and queues the write. A gated-off column is
    /// refused: turning it on would change nothing visible and would teach the wrong model of why
    /// the column is missing. The picker's hint names Settings as where the group goes back on.
    /// </summary>
    [RelayCommand]
    private void ToggleColumn(ColumnViewModel? column)
    {
        if (column is null || !column.IsGroupEnabled)
        {
            return;
        }

        column.IsVisible = !column.IsVisible;
        OnPropertyChanged(nameof(VisibleColumns));

        // The persisted list is every column whose IsVisible is set, in the fixed column order,
        // NOT the rendered subset. Spec 5.8.2: a column hidden by its group being disabled "stays
        // hidden; the group toggle wins" -- it stays in the list, so turning the group back on in
        // Settings brings the user's column back. Persisting VisibleColumns instead would silently
        // delete every gated column from the document on the first unrelated toggle.
        _columns.Write(
            DisplaySettings.FramesTableId,
            [.. Columns.Where(entry => entry.IsVisible).Select(entry => entry.Key)]);
    }

    /// <summary>design-spec 12.4's "reveal in Explorer". Task 3's service, which launches a
    /// process with a path argument and writes nothing.</summary>
    [RelayCommand]
    private void RevealFrame(FrameRowViewModel? row)
    {
        if (row is not null)
        {
            _shell.RevealInExplorer(row.FilePath);
        }
    }

    /// <summary>design-spec 12.4's "open with default application".</summary>
    [RelayCommand]
    private void OpenFrameWithDefaultApplication(FrameRowViewModel? row)
    {
        if (row is not null)
        {
            _shell.OpenWithDefaultApplication(row.FilePath);
        }
    }

    /// <summary>design-spec 12.4's "copy path": the absolute path, one frame, no dialog.</summary>
    [RelayCommand]
    private Task CopyFramePathAsync(FrameRowViewModel? row)
        => row is null ? Task.CompletedTask : _shell.CopyTextAsync(row.FilePath);

    /// <summary>design-spec 11.5's preview modal, opened on the table's current rows in their
    /// current sort order and positioned at the clicked row. Disabled while the seam is unbound,
    /// which is what that should look like on screen rather than a button that does nothing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenPreview))]
    private void OpenPreview(FrameRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        // Rows, not _captureOrder: spec 11.5's list is the one the user is looking at. A row that
        // is somehow not in it would give -1, which no navigation list can use.
        var index = Rows.IndexOf(row);
        if (index >= 0)
        {
            _openPreview?.Invoke([.. Rows], index);
        }
    }

    private bool CanOpenPreview(FrameRowViewModel? row) => _openPreview is not null;

    /// <summary>design-spec 12.4's "show raw headers". The panel view-model is built the first
    /// time a row's flag turns on, never per row at construction, so a 400-frame session does not
    /// hold 400 header queries (Task 6 handoff); collapsing and re-expanding the same row reuses
    /// the panel it already built, so the query still runs at most once per row.</summary>
    [RelayCommand]
    private void ToggleRawHeaders(FrameRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        row.AreRawHeadersExpanded = !row.AreRawHeadersExpanded;

        if (row.AreRawHeadersExpanded && row.RawHeaders is null)
        {
            row.RawHeaders = new RawHeaderPanelViewModel(
                row.ImageId, _getHeaders, row.Row, logger: _logger, lifetime: _lifetimeToken);
            row.RawHeaders.Load();
        }
    }

    /// <summary>Unsubscribes from the shared column writer and cancels any raw header read still in
    /// flight. The card owning this table disposes it through
    /// <c>SessionCardViewModel.DisposeChildren</c>, which already casts its two children to
    /// <see cref="IDisposable"/>; without this a collapsed card's table would stay reachable from
    /// the process-wide writer for the life of the process.</summary>
    /// <remarks>Idempotent, which <see cref="IDisposable"/> requires and this method did not hold:
    /// <c>_lifetime.Cancel()</c> runs after <c>_lifetime.Dispose()</c>, so a second call threw
    /// <see cref="ObjectDisposedException"/> from inside a disposal (Task 5 implementer escalation
    /// 3). No production path disposes twice today, which is why nothing caught it.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SelectedRows.CollectionChanged -= OnSelectedRowsChanged;
        _columns.Changed -= OnColumnsChanged;
        _targetPage.PropertyChanged -= OnTargetPageChanged;
        _unsubscribeDisplayChanged?.Invoke(OnDisplayChanged);

        // The pills are this table's own and die with it, but a handler left on one would keep the
        // table reachable from its own pill for as long as the pill is bound.
        foreach (var pill in RigPills)
        {
            pill.PropertyChanged -= OnRigPillChanged;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    // Spec 12.4 item 3: a pill toggle re-filters the rows through the one membership rule and
    // republishes the shown count. Project rather than a second filter pass, so the rig filter and
    // the outlier filter compose in the one place that decision lives.
    private void OnRigPillChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToggleOptionViewModel.IsSelected))
        {
            Project();
        }
    }

    /// <summary>
    /// Re-applies design-spec 5.8.2's metric-group gate from a display document. The seam Phase 9
    /// FIXER item 7 needed on this type: it touches <see cref="ColumnViewModel.IsGroupEnabled"/>
    /// and nothing else, so the persisted visible list is untouched and a gated column stays in it
    /// exactly as the spec describes.
    /// </summary>
    public void ApplyGroupGates(DisplaySettings display)
    {
        foreach (var column in FrameColumns.All)
        {
            var row = Columns.FirstOrDefault(entry => entry.Key == column.Key);
            if (row is not null)
            {
                row.IsGroupEnabled = FrameColumns.IsGroupEnabled(column, display);
            }
        }

        OnPropertyChanged(nameof(VisibleColumns));
    }

    // SettingsStore.DisplayChanged, raised on whichever thread saved. Only the groups half is
    // taken: the columns half arrives through DisplayColumnWriter.Changed above, so one column
    // click (which raises both) is applied once here and once there, never twice to the same
    // property.
    private void OnDisplayChanged(object? sender, DisplaySettings display)
        => _post(() =>
        {
            if (!_lifetimeToken.IsCancellationRequested)
            {
                ApplyGroupGates(display);
            }
        });

    // Another live table over the same table id queued a write. Adopt its list verbatim: it is the
    // document as it will be persisted, and the persisted list carries gated keys too (spec 5.8.2),
    // so IsGroupEnabled is left alone and the gate re-applies itself through IsShown.
    private void OnColumnsChanged(string tableId, string[] keys)
    {
        if (!string.Equals(tableId, DisplaySettings.FramesTableId, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var column in Columns)
        {
            column.IsVisible = keys.Contains(column.Key, StringComparer.Ordinal);
        }

        OnPropertyChanged(nameof(VisibleColumns));
    }

    // Spec 12.4's "Compare to" flip. Re-grades over _captureOrder and not Rows: the filtered
    // subset is a projection and a hidden row must come back already graded. Nothing is
    // re-projected and nothing is re-sorted, because a baseline is not a filter and not an order.
    private void OnTargetPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(TargetPageState.GradingBaseline), StringComparison.Ordinal))
        {
            return;
        }

        var baseline = _targetPage.GradingBaseline;
        foreach (var row in _captureOrder)
        {
            row.ApplyBaseline(baseline);
        }

        RecomputeTally();
    }

    // The tally, over the loaded night's frames. Called at construction and on every baseline
    // flip, and by nothing else: the counts do not move when the outlier filter does.
    private void RecomputeTally()
    {
        var good = 0;
        var watch = 0;
        var reject = 0;
        var ungraded = 0;
        var total = 0d;
        var scored = 0;

        foreach (var row in _captureOrder)
        {
            if (row.RowScore is not { } score)
            {
                ungraded++;
                continue;
            }

            total += score;
            scored++;

            switch (row.ScoreBand)
            {
                case QualityBand.Watch:
                    watch++;
                    break;
                case QualityBand.Reject:
                    reject++;
                    break;
                default:
                    good++;
                    break;
            }
        }

        GoodCount = good;
        WatchCount = watch;
        RejectCount = reject;
        UngradedCount = ungraded;
        MeanScore = scored == 0 ? null : total / scored;

        OnPropertyChanged(nameof(TallyText));
        OnPropertyChanged(nameof(HasTally));
        OnPropertyChanged(nameof(MeanScoreText));
        OnPropertyChanged(nameof(HasMeanScore));
        OnPropertyChanged(nameof(HasUngraded));
    }

    private void ApplySort()
    {
        Project();
        UpdateSortGlyphs();
    }

    // The one place Rows is rebuilt: filter, then sort, then the two stable tie-breaks. Every
    // caller goes through here, so a filtered table and a sorted table cannot disagree about what
    // is on screen, and a table that has never been sorted still applies its filter.
    //
    // Nulls last in both directions, always: flipping the direction must not march a block of
    // unmeasured frames through the middle of the list. Ties break on capture date then file name
    // so the order is total and a re-sort of the same key is stable.
    //
    // The selection is reconciled against the result afterwards. A row the filter removed must not
    // stay selected and contribute its path to a copy of rows the user cannot see, and the
    // survivors come back in the table's order, which is also what re-selects them in the ListBox
    // after the Clear that rebuilding Rows in place makes it drop.
    private void Project()
    {
        IEnumerable<FrameRowViewModel> source = _captureOrder.Where(Matches);

        if (SortKey.Length > 0 && SortSelectors.TryGetValue(SortKey, out var selector))
        {
            var ordered = source.OrderBy(row => selector(row) is null);
            ordered = Descending
                ? ordered.ThenByDescending(selector)
                : ordered.ThenBy(selector);

            source = ordered
                .ThenBy(row => row.Row.CaptureDate ?? DateTime.MaxValue)
                .ThenBy(row => row.Row.FileName, StringComparer.OrdinalIgnoreCase);
        }

        var selected = SelectedRows.ToList();

        Rows.Clear();
        foreach (var row in source)
        {
            Rows.Add(row);
        }

        var keep = Rows.Where(selected.Contains).ToList();
        if (!SelectedRows.SequenceEqual(keep))
        {
            SelectedRows.Clear();
            foreach (var row in keep)
            {
                SelectedRows.Add(row);
            }
        }

        // R9: the filter can remove the hovered row while the pointer is still over its tick, so
        // the tint goes out with it rather than staying on a row nobody can see.
        if (HighlightedRow is { } highlighted && !Rows.Contains(highlighted))
        {
            HighlightedRow = null;
        }

        OnPropertyChanged(nameof(ShownCountText));
    }

    // Design-spec 12.4's two findings and its rig pills, as the only membership rule the table
    // has. The two filters compose: a row is shown when both admit it (spec 12.4 item 3), and
    // ShownCountText reports the result with no change of its own. None and all pills checked
    // shows every row, which is what a fresh table and a cleared filter both hold.
    private bool Matches(FrameRowViewModel row)
        => RigAdmits(row)
            && OutlierFilter switch
            {
                FrameOutlierFilter.Hfr => row.IsHfrOutlier,
                FrameOutlierFilter.Eccentricity => row.IsEccentricityOutlier,
                FrameOutlierFilter.Fwhm => row.IsFwhmOutlier,
                FrameOutlierFilter.Stars => row.IsStarsOutlier,
                FrameOutlierFilter.GuidingRms => row.IsGuidingRmsOutlier,
                _ => true,
            };

    // Questions.md Q11: unlike the web's RigTogglePills, which refuses to remove the final rig,
    // unchecking the last pill is allowed and the table goes empty. The toolbar then reads
    // "0 of n shown", which is a readable state and a reversible one; a control that silently
    // refuses a click is worse.
    private bool RigAdmits(FrameRowViewModel row)
        => RigPills.Count == 0
            || RigPills[row.RigIndex].IsSelected;

    private IEnumerable<string> SelectedPathsInTableOrder()
        => Rows.Where(SelectedRows.Contains).Select(row => row.PathForCopy);

    // The selection drives two enablements and two labels, and it is mutated from three places:
    // the ListBox, SelectFrameAt, and Project's reconciliation.
    private void OnSelectedRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CopySelectedPathsText));
        OnPropertyChanged(nameof(ShownCountText));
        CopySelectedPathsCommand.NotifyCanExecuteChanged();
        RevealSelectedCommand.NotifyCanExecuteChanged();

        // Escape's enablement is what decides whether the key event stops here or reaches the
        // page, so it has to follow the selection as closely as the two buttons do.
        ClearFilterOrSelectionCommand.NotifyCanExecuteChanged();

        // Task 7 (user ruling U1): null on an empty or multi-row selection, else that one row's
        // position in capture order, which is the index space the night strip's ticks use.
        var singleIndex = SelectedRows.Count == 1 ? _captureOrder.IndexOf(SelectedRows[0]) : -1;
        SelectedCaptureIndex = singleIndex >= 0 ? singleIndex : null;
    }

    // The Time column carries its zone's GMT offset (spec 5.8.1, fixer-list item 20); the rest
    // take HeaderTitles' display title or keep FrameColumns.All's verbatim spec 12.4 title.
    private static string HeaderTitle(FrameColumn column, string zoneLabel)
        => column.Key == "time"
            ? $"Time ({zoneLabel})"
            : HeaderTitles.TryGetValue(column.Key, out var titled) ? titled : column.Title;

    // The direction glyph on the active column, kept on the columns themselves so the header
    // template stays a plain binding, exactly as the dashboard's header does.
    private void UpdateSortGlyphs()
    {
        foreach (var column in Columns)
        {
            column.SortGlyph = column.Key == SortKey
                ? Descending ? "\u25BC" : "\u25B2"
                : "";
        }
    }
}
