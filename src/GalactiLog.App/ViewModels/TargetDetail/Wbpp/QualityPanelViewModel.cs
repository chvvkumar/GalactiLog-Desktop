using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Controls.Table;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Wbpp;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.TargetDetail.Wbpp;

/// <summary>One frame the panel judges, with the grades the session query already computed. A null
/// <paramref name="Grading"/> is a frame the query graded nothing for: every cell of it is
/// uncoloured, which is not an error.</summary>
public sealed record QualityPanelFrame(WbppFrame Frame, FrameGrading? Grading);

/// <summary>
/// design-spec 12.13's quality filter: the chips toolbar and the verdict table. Five constraint
/// chips with their lifecycle, the three eccentricity presets, the master switch, the tally line,
/// the baseline segment, and a sortable per-frame table with a per-row override. Port of
/// <c>frontend/src/components/wbpp/WbppQualityPanel.tsx</c>.
/// </summary>
/// <remarks>
/// <para>
/// It loads its state from <c>general.wbpp_quality_by_rig</c> for one rig key when it is built and
/// writes every change back to that one rig's entry through one coalesced
/// <c>SettingsStore.MutateGeneral</c>, reached as a delegate (design-spec 18.3). Every other rig's
/// entry is carried through untouched as its own raw JSON, which is
/// <c>WbppSettingsRead.WriteQualityForRig</c>'s own job. It writes no other key and reads none.
/// </para>
/// <para>
/// <b>It computes no deviation</b> (W10). The cell colours are read off the
/// <see cref="FrameGrading"/> <c>SessionDetailQuery</c> computed for both baselines, and the
/// baseline toggle selects between the pairs and nothing else. Spec 12.13: the toggle "colours
/// cells and nothing else: the constraints alone decide a verdict, and the toggle never moves a
/// frame between Copy and Exclude."
/// </para>
/// <para>
/// <b>It never parses a settings value itself</b> (seam-review ruling 7).
/// <c>WbppSettingsRead.ReadQualityByRig</c> and <c>WbppSettingsRead.WriteQualityForRig</c> are the
/// whole of this panel's contact with the stored shape; no <c>System.Text.Json</c> member is named
/// here.
/// </para>
/// <para>
/// <b>The rig key never changes while the page is open</b>, which is why there is no transition to
/// arbitrate and no counterpart to the web application's rig-transition flag (spec 12.13). The
/// caller resolves it before the panel is built.
/// </para>
/// <para>
/// <b>It never learns about a folder level at all.</b> Spec 12.13's "changing a night's chosen
/// level preserves overrides, because an override is a property of a frame rather than of a
/// folder" therefore holds here by construction: there is no member by which a level change could
/// reach an override.
/// </para>
/// <para>
/// <b>There is no loading state.</b> The host builds this panel only after its frames have been
/// read, so spec 12.13's "Filter on, frames not yet read" row is the host's, and the host shows its
/// own loading line in the slot until it builds the panel.
/// </para>
/// </remarks>
public sealed partial class QualityPanelViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The window a burst of edits is coalesced into. Spec 12.13: "the writes go through one queued
    /// <c>SettingsStore.MutateGeneral</c> per change, coalesced, so a threshold typed digit by
    /// digit is one save and not four."
    /// </summary>
    /// <remarks>Spec 12.4's own one second idle debounce, referenced rather than restated, which is
    /// what <c>ActivityViewModel.DebounceWindow</c> already does for <c>DashboardViewModel</c>'s:
    /// one figure, asserted by a case, so a threshold field and a notes field cannot drift to
    /// different windows.</remarks>
    public static readonly TimeSpan SaveWindow = AutosaveField.IdleWindow;

    /// <summary>Spec 12.13's chip order and metric column order, which are one list so the toolbar
    /// and the table can never disagree about what exists.</summary>
    private static readonly WbppMetric[] MetricOrder =
        [WbppMetric.Hfr, WbppMetric.Ecc, WbppMetric.Fwhm, WbppMetric.Stars, WbppMetric.Rms];

    /// <summary>The metric each sortable metric column key names. Copy is absent from every table
    /// here: spec 12.13 makes it the one unsortable column, because "inclusion is a per-row
    /// decision, not a metric".</summary>
    private static readonly Dictionary<string, WbppMetric> MetricColumns = new(StringComparer.Ordinal)
    {
        ["hfr"] = WbppMetric.Hfr,
        ["ecc"] = WbppMetric.Ecc,
        ["fwhm"] = WbppMetric.Fwhm,
        ["stars"] = WbppMetric.Stars,
        ["rms"] = WbppMetric.Rms,
    };

    private static readonly HashSet<string> SortableColumns = new(StringComparer.Ordinal)
    {
        "verdict", "filter", "hfr", "ecc", "fwhm", "stars", "rms", "file", "night",
    };

    private readonly IReadOnlyList<QualityPanelFrame> _frames;
    private readonly string _rigKey;
    private readonly Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> _mutateGeneral;
    private readonly Action<IReadOnlyCollection<Guid>>? _excludedChanged;
    private readonly ILogger _logger;

    // Every debounce window is linked to this, so disposing the panel stops a window already open.
    private readonly CancellationTokenSource _lifetime = new();

    // FIXER LIST F11's one restartable background window, rather than a fourth hand-rolled write
    // chain: three of those already ship (DisplayColumnWriter, GraphSettingsWriter, AutosaveField)
    // and a fourth copy is design lesson 1 arriving from the wrong direction.
    private readonly Debouncer _saveWindow;

    // The order of record for the constraints: the order the user added them in, which is what
    // WbppQualityState.Constraints preserves and what spec 12.13's "in the user's own chip order"
    // means. The five chips read their own entry out of it; it is never the metrics' display order.
    private readonly List<RawConstraint> _constraints = [];

    // Spec 12.13's per-row overrides, keyed by image id so one survives a re-sort.
    private readonly Dictionary<Guid, bool> _overrides = [];

    // One row per frame for the panel's whole life; Rows is a sorted projection of this list.
    private readonly List<VerdictRowViewModel> _rows;

    private IReadOnlyList<FrameVerdict> _verdicts = [];

    // The state the newest edit composed, captured on the UI thread. A queued write records the
    // state of the edit that queued it, not whatever is on screen when it runs (the
    // DisplayColumnWriter rule).
    private WbppQualityState _captured = WbppQualityState.Default;

    // 1 while _captured has not reached a write yet. Exchanged rather than assigned, because the
    // debounced write runs on the pool and Dispose runs on the UI thread.
    private int _unsaved;

    // True while the constructor adopts the stored state, so building the panel does not queue a
    // write of the three values it has just read.
    private bool _adopting;

    private bool _disposed;

    /// <param name="frames">Every LIGHT frame of the checked nights, in the panel's own frame
    /// order, each with the grades the session query computed for it.</param>
    /// <param name="rigKey">The rig this panel's filter is stored under, resolved by the caller
    /// from <c>WbppFrame.Rig</c>. Never composed here: a second rig-label spelling is a design
    /// lesson 1 finding. Null or blank is normalised to <c>WbppQualityByRig.DefaultRigKey</c> by
    /// the reader itself.</param>
    /// <param name="getGeneral">Normally <c>settingsStore.GetGeneral</c>. Read once, at
    /// construction.</param>
    /// <param name="mutateGeneral">Normally <c>settingsStore.MutateGeneral</c> (design-spec 18.3).
    /// Invoked on a thread-pool thread. Throwing is expected and handled: the edit has already
    /// changed what is on screen, so the failure is logged and dropped and the next edit tries
    /// again.</param>
    /// <param name="excludedChanged">Raised on the UI thread with the image id of every frame the
    /// copy will not include, in the panel's own frame order, every time that set changes, and once
    /// on construction so the host's footer is correct before the user touches anything. Null
    /// leaves the panel silent, which is what a case that is not about the host wants.</param>
    /// <param name="delay">The debounce seam every view-model takes, so a case drives the window
    /// instead of sleeping. Normally <c>Task.Delay</c>.</param>
    /// <param name="logger">Optional. A failed settings write is logged at warning, never rethrown
    /// onto the UI thread.</param>
    /// <remarks>There is no UI-thread post seam here, unlike every other view-model that writes:
    /// nothing in this panel marshals, because every publish it performs already runs on the thread
    /// the edit came in on, and the one pool-thread step writes no binding.</remarks>
    public QualityPanelViewModel(
        IReadOnlyList<QualityPanelFrame> frames,
        string rigKey,
        Func<GeneralSettings> getGeneral,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> mutateGeneral,
        Action<IReadOnlyCollection<Guid>>? excludedChanged = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(getGeneral);
        ArgumentNullException.ThrowIfNull(mutateGeneral);

        _frames = frames;
        _rigKey = rigKey;
        _mutateGeneral = mutateGeneral;
        _excludedChanged = excludedChanged;
        _logger = logger ?? NullLogger.Instance;
        _saveWindow = new Debouncer(_lifetime.Token, delay ?? Task.Delay, SaveWindow);

        Chips = [.. MetricOrder.Select(metric => new ConstraintChipViewModel(metric, OnChipEdited))];
        Columns = BuildColumns();
        _rows = [.. frames.Select((entry, index) => new VerdictRowViewModel(entry.Frame, index, OnRowToggled))];
        Rows = [];
        _sortKey = "";

        // Section 7.1's load, in order and on the calling thread. The three values are adopted into
        // the backing fields rather than through the properties, so constructing the panel does not
        // queue a write of what it has just read; FrameListDialogViewModel's constructor and
        // TargetPageState's own already do exactly this.
        _adopting = true;
        var stored = WbppSettingsRead.ReadQualityByRig(getGeneral().WbppQualityByRigDocument).For(rigKey);
        _isFilterEnabled = stored.Enabled;
        _isSessionBaseline = stored.Baseline == QualityBaseline.Session;

        // One entry per metric, the first of any duplicates. The stored shape is deliberately
        // tolerant (spec 5.8.1) and WbppSettingsRead's reader folds nothing, while a chip is one
        // per metric: a hand-edited document carrying two entries for one metric would otherwise
        // gate the frames on both while the chip showed one, and RebuildConstraints only ever
        // replaces the first, so the second would be a gate the user could neither see nor remove.
        foreach (var constraint in stored.Constraints)
        {
            if (!_constraints.Exists(entry => entry.Metric == constraint.Metric))
            {
                _constraints.Add(constraint);
            }
        }

        AdoptChips();
        _adopting = false;

        // The first verdict pass, and the one excludedChanged the host's footer needs before the
        // user touches anything.
        Reevaluate();
        Project();
        PublishTally();
        RaiseExcluded();
    }

    /// <summary>The five metric chips, in spec 12.13's fixed order.</summary>
    public IReadOnlyList<ConstraintChipViewModel> Chips { get; }

    /// <summary>The ten table columns, left to right: Copy, Verdict, Filter, HFR, Ecc, FWHM, Stars,
    /// RMS, File, Night. Copy alone is unsortable.</summary>
    public IReadOnlyList<ColumnViewModel> Columns { get; }

    /// <summary>The rows in their current sort order, chronological by capture time until a column
    /// is clicked.</summary>
    public ObservableCollection<VerdictRowViewModel> Rows { get; }

    /// <summary>The tail of the write chain, so a case awaits it instead of sleeping. Nothing in
    /// the application awaits it: a chip click must not wait on SQLite.</summary>
    public Task PendingSave { get; private set; } = Task.CompletedTask;

    /// <summary>Spec 12.13's "Enable filters" master switch. Everything else in the toolbar goes
    /// inert while it is off; the check box itself stays live so it can turn things back
    /// on.</summary>
    [ObservableProperty]
    private bool _isFilterEnabled;

    /// <summary>Spec 12.13's baseline segment, true for "This session" and false for "This rig".
    /// One settable bool, bound two way by both buttons, which is the shipped segment idiom
    /// (HANDOFF 5.2 item 23).</summary>
    [ObservableProperty]
    private bool _isSessionBaseline;

    /// <summary>The column key the rows are ordered by, empty before the first sort.</summary>
    [ObservableProperty]
    private string _sortKey;

    [ObservableProperty]
    private bool _descending;

    /// <summary>
    /// Spec 12.13's tally, verbatim: "n copy, n excluded, n unmeasured of N frames", with
    /// ", n overridden" appended when any row carries an override.
    /// </summary>
    /// <remarks>Built from one <see cref="QualityTotals"/> and from nothing this panel derived, so
    /// the line cannot disagree with the table and the overrides are not counted a second way. The
    /// first three figures always sum to the total; the overridden count sits outside that sum,
    /// "because an override is a property of a row rather than a fourth verdict".</remarks>
    [ObservableProperty]
    private string _tallyText = "";

    /// <summary>The tally's own record, so a case reads the figures rather than parsing the
    /// sentence.</summary>
    [ObservableProperty]
    private QualityTotals _totals = new(0, 0, 0, 0, 0);

    /// <summary>Whether there is a table to draw. False renders spec 12.13's empty state
    /// instead.</summary>
    public bool HasFrames => _frames.Count > 0;

    /// <summary>Spec 12.13's empty state, verbatim. An instance property rather than a constant so
    /// the view binds it through its <c>x:DataType</c> like everything else on the panel.</summary>
    public string EmptyText => "No light frames in the selected nights";

    /// <summary>Spec 12.13's sentence under the table, verbatim.</summary>
    public string FooterNote => "Filters apply to light frames only. All other files are copied unchanged.";

    /// <summary>The image ids of every frame the copy will not include, in the panel's own frame
    /// order. The same set <see cref="_excludedChanged"/> carries, exposed so a case reads it
    /// without a callback.</summary>
    public IReadOnlyCollection<Guid> ExcludedImageIds =>
    [
        .. _verdicts
            .Where(verdict => !QualityFilter.IsIncluded(verdict, IsFilterEnabled, _overrides))
            .Select(verdict => verdict.Frame.ImageId),
    ];

    /// <summary>
    /// Sorts by a column key, flipping direction on a repeat click. A key that is not sortable, the
    /// Copy column included, is ignored rather than clearing the sort, which is the shape
    /// <c>FrameTableViewModel.SortBy</c> already uses.
    /// </summary>
    [RelayCommand]
    private void SortBy(string? columnKey)
    {
        if (columnKey is null || !SortableColumns.Contains(columnKey))
        {
            return;
        }

        if (string.Equals(SortKey, columnKey, StringComparison.Ordinal))
        {
            Descending = !Descending;
        }
        else
        {
            SortKey = columnKey;
            Descending = false;
        }

        Project();
    }

    /// <summary>
    /// Cancels the open save window and flushes the pending state once, so a page closed inside the
    /// window still saves the last edit.
    /// </summary>
    /// <remarks>A flush rather than a drop, which is what <c>AutosaveField.FlushAsync</c> does for
    /// the notes field: spec 12.13 says every change is written back, and a change the user made
    /// less than a second before closing the page is still a change they made. The write is
    /// synchronous here because <see cref="IDisposable.Dispose"/> has nowhere to put a task, and it
    /// is one settings row. The phase review weighed moving the flush behind the window close and
    /// accepted the synchronous one-row write as written (ruling 3), so this is deliberate and not
    /// an oversight to tidy later.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Cancel first, so the parked window cannot also fire behind the flush.
        _saveWindow.Dispose();
        _lifetime.Cancel();
        Write();
        _lifetime.Dispose();
    }

    // ---- the constraint set, the verdicts and the rows ----------------------------------------

    // One chip edit. Spec 12.13: an override "is cleared when the constraint set changes, because a
    // verdict the user reversed against one filter says nothing about another". Every chip edit is
    // a constraint-set change: added, disabled, re-enabled, comparator moved, value moved, null
    // included.
    private void OnChipEdited(ConstraintChipViewModel chip)
    {
        RebuildConstraints(chip);
        _overrides.Clear();
        Reevaluate();
        Project();
        PublishTally();
        RaiseExcluded();
        QueueSave();
    }

    // The stored list is the order of record. An existing entry is replaced where it stands, so the
    // order the user added the chips in survives every later edit; a newly added chip is appended,
    // which is what makes it the newest in that order.
    private void RebuildConstraints(ConstraintChipViewModel chip)
    {
        var index = _constraints.FindIndex(entry => entry.Metric == chip.Metric);
        if (chip.Constraint is not { } constraint)
        {
            if (index >= 0)
            {
                _constraints.RemoveAt(index);
            }

            return;
        }

        if (index >= 0)
        {
            _constraints[index] = constraint;
        }
        else
        {
            _constraints.Add(constraint);
        }
    }

    private void AdoptChips()
    {
        foreach (var chip in Chips)
        {
            chip.Adopt(_constraints.FirstOrDefault(entry => entry.Metric == chip.Metric));
        }
    }

    private void Reevaluate()
        => _verdicts = QualityFilter.EvaluateAll(_frames.Select(entry => entry.Frame), _constraints);

    // Refreshes every row in place from the current verdicts, overrides, baseline and master
    // switch, then re-applies the sort. In place rather than rebuilt: the Copy check box is bound
    // two way, and an override survives a re-sort because the sort reorders the same objects.
    private void Project()
    {
        var baseline = IsSessionBaseline ? QualityBaseline.Session : QualityBaseline.Rig;
        for (var i = 0; i < _rows.Count; i++)
        {
            var verdict = _verdicts[i];
            _rows[i].Apply(
                verdict,
                QualityFilter.IsIncluded(verdict, IsFilterEnabled, _overrides),
                _frames[i].Grading,
                baseline,
                IsFilterEnabled);
        }

        var ordered = new List<VerdictRowViewModel>(_rows);
        ordered.Sort(Compare);

        Rows.Clear();
        foreach (var row in ordered)
        {
            Rows.Add(row);
        }

        UpdateSortGlyphs();
    }

    private void OnRowToggled(VerdictRowViewModel row)
    {
        // Spec 12.13: the override "works in both directions", so the box's own new value is the
        // override, whichever way it went.
        _overrides[row.ImageId] = row.IsCopied;
        Project();
        PublishTally();
        RaiseExcluded();
    }

    private void PublishTally()
    {
        var totals = QualityFilter.Totals(_verdicts, IsFilterEnabled, _overrides);
        Totals = totals;

        var head = string.Create(
            CultureInfo.InvariantCulture,
            $"{totals.Copy} copy, {totals.Exclude} excluded, {totals.Unmeasured} unmeasured of {totals.Total} frames");

        TallyText = totals.Overridden == 0
            ? head
            : head + string.Create(CultureInfo.InvariantCulture, $", {totals.Overridden} overridden");
    }

    private void RaiseExcluded() => _excludedChanged?.Invoke(ExcludedImageIds);

    // Generated by [ObservableProperty]. The master switch changes what is included and what the
    // verdict column reads, and it is not a constraint, so spec 12.13's "Turning the master Enable
    // filters switch off and on again preserves overrides" holds by this method not touching them.
    partial void OnIsFilterEnabledChanged(bool value)
    {
        if (_adopting)
        {
            return;
        }

        Project();
        PublishTally();
        RaiseExcluded();
        QueueSave();
    }

    // Generated by [ObservableProperty]. Spec 12.13: the baseline "colours cells and nothing else:
    // the constraints alone decide a verdict, and the toggle never moves a frame between Copy and
    // Exclude". It therefore repaints and saves, and raises no excludedChanged: the set it would
    // carry is the same set, and a host told the frames moved would withdraw a generated script for
    // nothing.
    partial void OnIsSessionBaselineChanged(bool value)
    {
        if (_adopting)
        {
            return;
        }

        Project();
        QueueSave();
    }

    // ---- the sort (ported from WbppQualityPanel.tsx's comparator) ------------------------------

    private int Compare(VerdictRowViewModel a, VerdictRowViewModel b)
    {
        var ordered = CompareOnKey(a, b);
        if (ordered != 0)
        {
            return ordered;
        }

        // Rule 6: every comparison falls back to chronological on a tie, then to the panel's own
        // frame order, which makes the order total so two runs of one sort draw the same rows in
        // the same places. List.Sort is not a stable sort, so the last tie-break has to be one no
        // two rows can share.
        ordered = Chronological(a, b);
        return ordered != 0 ? ordered : a.Index.CompareTo(b.Index);
    }

    private int CompareOnKey(VerdictRowViewModel a, VerdictRowViewModel b)
    {
        if (SortKey.Length == 0)
        {
            return Chronological(a, b);
        }

        var direction = Descending ? -1 : 1;

        if (MetricColumns.TryGetValue(SortKey, out var metric))
        {
            // Rule 5: a missing value sinks to the bottom in either direction. A frame with no
            // measurement is not the smallest one.
            var left = QualityFilter.ValueOf(a.Frame, metric);
            var right = QualityFilter.ValueOf(b.Frame, metric);
            if (left is null && right is null)
            {
                return 0;
            }

            if (left is null)
            {
                return 1;
            }

            if (right is null)
            {
                return -1;
            }

            return left.Value.CompareTo(right.Value) * direction;
        }

        return SortKey switch
        {
            "file" => string.CompareOrdinal(a.FileName, b.FileName) * direction,
            "night" => string.CompareOrdinal(a.NightText, b.NightText) * direction,
            "filter" => string.CompareOrdinal(a.FilterKey, b.FilterKey) * direction,

            // Rule 4: by group, not by value. The excluded rows come first ascending, flipped when
            // descending, and the rows inside a group stay chronological so they read as the night
            // unfolded. The group is the effective inclusion, overrides included, which is what the
            // Copy column answers.
            "verdict" => ((a.IsCopied ? 1 : 0) - (b.IsCopied ? 1 : 0)) * direction,
            _ => 0,
        };
    }

    // Rule 1: chronological by capture time, nulls last, whichever direction the sort runs in.
    private static int Chronological(VerdictRowViewModel a, VerdictRowViewModel b)
    {
        var left = a.Frame.CaptureDate;
        var right = b.Frame.CaptureDate;
        if (left is null && right is null)
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        return left.Value.CompareTo(right.Value);
    }

    private void UpdateSortGlyphs()
    {
        foreach (var column in Columns)
        {
            column.SortGlyph = string.Equals(column.Key, SortKey, StringComparison.Ordinal)
                ? Descending ? "▼" : "▲"
                : "";
        }
    }

    private static IReadOnlyList<ColumnViewModel> BuildColumns() =>
    [
        new("copy", "Copy", isVisible: true),
        new("verdict", "Verdict", isVisible: true),
        new("filter", TableHeads.Filter, isVisible: true),
        new("hfr", TableHeads.Hfr, isVisible: true, isNumeric: true) { Tip = TableHeads.HfrTip },
        new("ecc", TableHeads.Ecc, isVisible: true, isNumeric: true) { Tip = TableHeads.EccTip },
        new("fwhm", TableHeads.Fwhm, isVisible: true, isNumeric: true) { Tip = TableHeads.FwhmTip },
        new("stars", TableHeads.Stars, isVisible: true, isNumeric: true),
        new("rms", TableHeads.Rms, isVisible: true, isNumeric: true) { Tip = TableHeads.RmsTip },
        new("file", "File", isVisible: true),
        new("night", TableHeads.Night, isVisible: true),
    ];

    // ---- the coalesced write (section 7.2) ----------------------------------------------------

    // Composes the whole next state on the UI thread and restarts the one window over it.
    // Restarting is what coalesces a burst: the window is cancelled and reopened per keystroke and
    // fires once.
    private void QueueSave()
    {
        if (_adopting || _disposed)
        {
            return;
        }

        _captured = new WbppQualityState(
            IsFilterEnabled,
            IsSessionBaseline ? QualityBaseline.Session : QualityBaseline.Rig,
            [.. _constraints]);
        Interlocked.Exchange(ref _unsaved, 1);
        PendingSave = _saveWindow.Restart(RunSaveWindowAsync);
    }

    private async Task RunSaveWindowAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            // No ConfigureAwait(false) anywhere in this view-model (W12): every continuation stays
            // on the captured context, so nothing that can reach an [ObservableProperty] write ends
            // up on a pool thread. The write itself is pushed onto the pool explicitly below
            // instead, which is what keeps a settings write off the UI thread without the
            // continuation rule being bent.
            await _saveWindow.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A later edit, or Dispose, superseded this window.
            return;
        }

        if (cancellationToken.IsCancellationRequested || !_saveWindow.IsCurrent(generation))
        {
            return;
        }

        await Task.Run(Write, CancellationToken.None);
    }

    // Reads the captured state and replaces this rig's entry, leaving every other rig's untouched.
    // The document the writer edits is the one loaded INSIDE the mutation, not the one this panel
    // read when it opened, which is what carries the other entries through even if another surface
    // wrote one between the edit and the save.
    //
    // WriteQualityForRig and not a read, a With and a write of the whole typed map (the settings
    // review's seam change): replacing only this rig's sub-object carries every other rig's raw
    // JSON through untouched, including anything a later build of GalactiLog stored there that this
    // build's typed map cannot represent.
    private void Write()
    {
        if (Interlocked.Exchange(ref _unsaved, 0) == 0)
        {
            return;
        }

        var state = _captured;
        try
        {
            _mutateGeneral(general => general with
            {
                WbppQualityByRigDocument = WbppSettingsRead.WriteQualityForRig(
                    general.WbppQualityByRigDocument, _rigKey, state),
            });
        }
        catch (Exception ex)
        {
            // The edit has already changed what is on screen and the next edit tries again. Never
            // rethrown onto the UI thread: a failed settings write must not take the page down
            // while the user is still choosing frames.
            _logger.LogWarning(ex, "A WBPP quality filter save failed; the filter on screen is kept");
        }
    }
}
