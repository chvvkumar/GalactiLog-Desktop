using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Core.Io;
using GalactiLog.Core.Scanning;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// One auto-scan interval choice: the stored minute count and the label the web shows for it.
/// A top-level record rather than a nested one so the view's <c>ComboBox.ItemTemplate</c> can
/// name it in <c>x:DataType</c> without nested-type syntax.
/// </summary>
public sealed record IntervalOption(int Minutes, string Label);

/// <summary>
/// The auto-scan interval choices: the seven presets, and the one rule for publishing them into a
/// collection a selector is bound to.
/// </summary>
/// <remarks>
/// Design-lessons rule 1. The Library tab (spec 12.7) and the setup wizard's step 4 (spec 12.1)
/// offer the same seven options and each carried the same twenty lines of "clear the list, refill
/// it, then pick one", which is the shape that rendered the Library tab's "Scan interval" select
/// empty on a first visit (task5c-review.md P3-6) and, elsewhere in this phase, wrote "All rigs"
/// back over a chosen rig. One body now, and it is the body that keeps a bound selection alive.
/// </remarks>
public static class IntervalChoices
{
    /// <summary>
    /// The auto-scan interval presets, from <c>ScanManager.tsx</c>'s <c>INTERVALS</c>, verbatim.
    /// One list for both surfaces, so the wizard and the tab cannot disagree about what "4 hours"
    /// is.
    /// </summary>
    public static readonly IReadOnlyList<IntervalOption> Presets =
    [
        new(60, "1 hour"),
        new(120, "2 hours"),
        new(240, "4 hours"),
        new(360, "6 hours"),
        new(480, "8 hours"),
        new(720, "12 hours"),
        new(1440, "24 hours"),
    ];

    /// <summary>
    /// Brings <paramref name="options"/> to the seven presets plus at most one entry for a stored
    /// minute count that is not one of them, and returns the option to select.
    /// </summary>
    /// <param name="options">The collection a selector is bound to. Added to and removed from,
    /// never cleared.</param>
    /// <param name="minutes"><c>general.auto_scan_interval_minutes</c>.</param>
    /// <returns>The option the caller assigns to its own selection property. Returned rather than
    /// assigned here because the two callers hold it in two different properties.</returns>
    /// <remarks>
    /// <para>
    /// No preset instance is ever replaced and the collection is never cleared. Clearing a
    /// collection a selector is bound to clears that selector's own selection, and the assignment
    /// that follows cannot always put it back: assigning the option the property already holds
    /// announces no change, and <see cref="IntervalOption"/> is a record, so even a fresh equal
    /// instance compares equal. That pair, not either half alone, is what left a select blank for
    /// a whole visit, and one body is what makes it unwritable a fourth time.
    /// </para>
    /// <para>
    /// Publish-thread only, like every other member that touches a bound collection.
    /// </para>
    /// </remarks>
    public static IntervalOption Publish(ObservableCollection<IntervalOption> options, int minutes)
    {
        if (options.Count == 0)
        {
            foreach (var preset in Presets)
            {
                options.Add(preset);
            }
        }

        var chosen = options.FirstOrDefault(option => option.Minutes == minutes);
        if (chosen is null)
        {
            // Spec 5.8.1 allows any positive minute count, and a document edited by hand can hold
            // one. Shown as its own entry rather than snapped to the nearest preset, which would
            // silently rewrite a value the user chose.
            chosen = new IntervalOption(minutes, $"{minutes} minutes");
            options.Add(chosen);
        }

        // At most one such entry ever stands, so a later publish whose value is a preset again, or
        // a different hand-edited count, drops the one before it. Removing an option nobody has
        // selected leaves the selection alone, which clearing the whole collection would not.
        for (var i = options.Count - 1; i >= Presets.Count; i--)
        {
            if (!ReferenceEquals(options[i], chosen))
            {
                options.RemoveAt(i);
            }
        }

        return chosen;
    }
}

/// <summary>
/// Design-spec 12.7's Library tab: the scan roots list, the include and exclude path lists, the
/// name rule editor with its test-a-path box, the include-calibration checkbox, the auto-scan
/// enabled checkbox and interval, the watcher enabled checkbox, and a manual scan with progress
/// and cancel.
/// </summary>
/// <remarks>
/// <para>
/// Every collaborator is a delegate rather than a <c>SettingsStore</c> or a
/// <c>ScanCoordinator</c>, the rule every view-model in this application follows, so the tab
/// constructs in a unit test with lambdas and no database (design-spec 18.3).
/// </para>
/// <para>
/// Two save shapes, matching the web. The filter block (roots, include and exclude paths, name
/// rules) has one explicit Save with dirty tracking and Revert, because those four lists are
/// edited together and a half-applied filter set is not a state anyone wants persisted. The four
/// scalar controls save immediately and optimistically, and a save that fails rolls its control
/// back: a checkbox that stays ticked after a failed save is a lie.
/// </para>
/// <para>
/// FIXER LIST F18. The read, the publish, the serialized write chain, the immediate-save shape
/// with its roll-back, the <c>GeneralChanged</c> follow and the tab lifetime all belong to
/// <see cref="GeneralSettingsTabViewModel"/>, which this tab wrote first and Task 6 extracted
/// (design-lessons rule 1). What is left here is the Library tab's own material: the four lists,
/// the dirty filter block with its Save and Revert, the inline validation, the scan buttons and
/// the "Run setup again" link.
/// </para>
/// <para>
/// Every save goes through <c>SettingsStore.MutateGeneral</c>, which reads, mutates, validates
/// and writes the whole document in one critical section, never a partial write and never a
/// read and a write with a window between them, and the inline validation below is a usability
/// layer in front of
/// <c>SettingsStore.ValidateGeneral</c>, never a replacement for it: the store stays the choke
/// point (design-lessons rule 2), and every save path here still catches
/// <see cref="SettingsValidationException"/> as the backstop.
/// </para>
/// <para>
/// Nothing in this type enumerates, creates or deletes anything on disk. Folder pickers are
/// Avalonia storage-provider calls made in the view and handed here as plain strings, which is
/// what keeps the tab constructible with no window (HANDOFF.md section 5).
/// </para>
/// </remarks>
public sealed partial class LibraryTabViewModel : GeneralSettingsTabViewModel, IPendingEdits
{
    private readonly Func<ScanRunOptions, CancellationToken, Task<ScanRunOutcome>> _runScan;
    private readonly Action _cancelScan;
    private readonly ScanStatusService? _scanStatus;

    // Phase 9 Task 9's seam: spec 12.1's "Run setup again", opened through the one
    // SetupWizardService rather than a second wizard built here. Null on a tab that was not given
    // one, which hides the link.
    private readonly Func<Task>? _runSetupAgain;

    // Bumped by every edit. A save records the value it snapshotted at and clears IsDirty only if
    // nothing was edited while it ran: an edit made during an in-flight save is not in that save's
    // document, so it has to stay pending (the Filters and Equipment tabs' rule).
    private int _editVersion;

    // A Discard that arrived while a save was writing. Saved is still the pre-save document until
    // the save lands, so reverting then would show what disk no longer holds; the revert runs from
    // the save's own completion callback instead.
    private bool _discardAfterSave;

    /// <param name="load">Normally <c>SettingsStore.GetGeneral</c>. Called off the UI thread.
    /// </param>
    /// <param name="mutateGeneral">Normally <c>SettingsStore.MutateGeneral</c>, which reads,
    /// mutates, validates and writes the document in one critical section and raises
    /// <c>GeneralChanged</c> once afterwards. Called off the UI thread, inside the write chain.
    /// A <c>GetGeneral</c> plus <c>SaveGeneral</c> pair would leave a window between the read and
    /// the write in which another writer of the same document could be lost (Task 5 review
    /// escalation), which is why this is one delegate and not two.</param>
    /// <param name="runScan">Normally <c>ScanCoordinator.RunAsync(ScanTrigger.Manual, null,
    /// token, options)</c>, bound in <c>AppHost</c> exactly as <c>StatusBarViewModel</c>'s is, so
    /// the two manual-scan buttons cannot disagree about what a manual scan is. It carries the
    /// run's <c>ScanRunOptions</c> (spec 10.3, PAR-013) because this tab is the one surface that
    /// can set them: the status bar's button runs with the stored scope and no override.</param>
    /// <param name="cancelScan">Normally <c>ScanCoordinator.Cancel</c>.</param>
    /// <param name="scanStatus">The shared progress marshaller (design-spec 10.4). The manual
    /// scan button binds to this, never to <c>ScanCoordinator</c>: <c>ScanStatusService</c> is the
    /// only App-layer subscriber to the coordinator's progress events. Null disables the
    /// progress readout and leaves the button enabled, which is what a unit test that is not
    /// about scanning wants.</param>
    /// <param name="probeSupportedFiles">Task 9's shallow supported-file probe
    /// (<c>SupportedFileProbe</c>, questions.md Q33), held here so the wizard and this tab report
    /// the same count for the same folder rather than growing two probes. Task 5 does not call
    /// it: a probe enumerates a directory, and nothing on this tab touches the filesystem.
    /// </param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed load or save is logged, never rethrown on the UI
    /// thread.</param>
    /// <param name="subscribeGeneralChanged">Normally
    /// <c>handler =&gt; settingsStore.GeneralChanged += handler</c>. The tab is a DI singleton whose
    /// state outlives a visit to the Settings page, and the setup wizard writes the same document
    /// from a link on this very tab, so without this the tab shows stale lists indefinitely and
    /// the next save writes them back over the wizard's (Task 5 review finding I3). A pair
    /// of delegates rather than the store itself, the rule every view-model here follows. Null
    /// leaves the tab on its single constructor load, which is what a unit test that is not about
    /// external changes wants.</param>
    /// <param name="unsubscribeGeneralChanged">The matching
    /// <c>handler =&gt; settingsStore.GeneralChanged -= handler</c>, called from
    /// <see cref="Dispose"/>.</param>
    /// <param name="runSetupAgain">Design-spec 12.1's last sentence: "The wizard is also reachable
    /// from Settings as 'Run setup again'." Normally <c>SetupWizardService.ShowAsync</c> (Phase 9
    /// Task 9), the same service the first-run branch in <c>App.axaml.cs</c> uses, so there is one
    /// wizard and not two. Null hides the link, which is what a unit test that is not about the
    /// wizard wants.</param>
    public LibraryTabViewModel(
        Func<GeneralSettings> load,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> mutateGeneral,
        Func<ScanRunOptions, CancellationToken, Task<ScanRunOutcome>> runScan,
        Action cancelScan,
        ScanStatusService? scanStatus = null,
        Func<string, int>? probeSupportedFiles = null,
        Action<Action>? post = null,
        ILogger? logger = null,
        Action<EventHandler<GeneralSettings>>? subscribeGeneralChanged = null,
        Action<EventHandler<GeneralSettings>>? unsubscribeGeneralChanged = null,
        Func<Task>? runSetupAgain = null)
        : base(load, mutateGeneral, post, logger, subscribeGeneralChanged, unsubscribeGeneralChanged)
    {
        _runSetupAgain = runSetupAgain;
        _runScan = runScan;
        _cancelScan = cancelScan;
        _scanStatus = scanStatus;
        ProbeSupportedFiles = probeSupportedFiles;

        // Assigned before anything can observe them; the change handlers below all return early
        // while IsApplying is set, and it is set for the whole of the first publish. The three
        // boxes are non-nullable strings and need a value here. SelectedInterval deliberately
        // does not get one: it is nullable, there is nothing in IntervalOptions for it to name
        // yet, and seeding it with a preset is what left the select empty on a first visit
        // (task5c-review.md P3-6). The publish then assigned that same IntervalOption instance,
        // which announced no change, so a selector bound while the list was still empty was
        // never told to select anything and stayed blank for the rest of the visit.
        NewScanRootPath = "";
        NewIncludePath = "";
        NewExcludePath = "";

        TestPath = new TestPathViewModel(() => Saved);

        // The four "None" empty states. ObservableCollection raises Count as a property change,
        // but a compiled binding cannot negate an int, so each list gets one bool the view binds.
        ScanRoots.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasScanRoots));
        IncludePaths.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasIncludePaths));
        ExcludePaths.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasExcludePaths));
        NameRules.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNameRules));

        if (scanStatus is not null)
        {
            // ScanStatusService has already marshalled onto the UI thread; do not post again.
            scanStatus.PropertyChanged += OnScanStatusPropertyChanged;
        }

        // The spine subscribed to GeneralChanged before this constructor body ran, so a write that
        // lands while the first read is in flight is not missed; its generation check is what keeps
        // the two from fighting.
        Load();
    }

    /// <summary>Task 9's shallow supported-file probe, bound in <c>AppHost</c>. Exposed rather
    /// than called: this task holds the seam so the wizard and this tab share one probe (the
    /// collision map's designated owner table), and the wizard task is the one that uses it.
    /// </summary>
    internal Func<string, int>? ProbeSupportedFiles { get; }

    /// <summary>Spec 5.8.1's <c>general.scan_roots</c>, with add and remove (spec 10.1).
    /// </summary>
    public ObservableCollection<ScanRootRowViewModel> ScanRoots { get; } = [];

    /// <summary>Spec 10.2's <c>scan_filters.include_paths</c>.</summary>
    public ObservableCollection<FilterPathRowViewModel> IncludePaths { get; } = [];

    /// <summary>Spec 10.2's <c>scan_filters.exclude_paths</c>.</summary>
    public ObservableCollection<FilterPathRowViewModel> ExcludePaths { get; } = [];

    /// <summary>Spec 10.2's <c>scan_filters.name_rules</c>.</summary>
    public ObservableCollection<NameRuleRowViewModel> NameRules { get; } = [];

    /// <summary>Spec 10.2's test-a-path tool, over the saved configuration.</summary>
    public TestPathViewModel TestPath { get; }

    /// <summary>False renders the list's "None" empty state.</summary>
    public bool HasScanRoots => ScanRoots.Count > 0;

    /// <summary>False renders the list's "None" empty state.</summary>
    public bool HasIncludePaths => IncludePaths.Count > 0;

    /// <summary>False renders the list's "None" empty state.</summary>
    public bool HasExcludePaths => ExcludePaths.Count > 0;

    /// <summary>False renders the list's "None" empty state.</summary>
    public bool HasNameRules => NameRules.Count > 0;

    /// <summary>The seven interval presets, plus one extra entry when the stored value is not
    /// one of them. Published through <see cref="IntervalChoices.Publish"/>, which adds the
    /// presets once and then leaves them alone; only the extra entry ever moves.</summary>
    public ObservableCollection<IntervalOption> IntervalOptions { get; } = [];

    /// <summary>The add-a-scan-root box.</summary>
    [ObservableProperty]
    public partial string NewScanRootPath { get; set; }

    /// <summary>The add-an-include-path box.</summary>
    [ObservableProperty]
    public partial string NewIncludePath { get; set; }

    /// <summary>The add-an-exclude-path box.</summary>
    [ObservableProperty]
    public partial string NewExcludePath { get; set; }

    /// <summary>
    /// Another writer changed the stored document under an edit in progress (review finding I3).
    /// The tab does not overwrite the edit and does not discard it; it says so and leaves Revert as
    /// the way to take the stored values.
    /// </summary>
    /// <remarks>
    /// FIXER LIST F13: raised only by a write this tab did not make. The first read landing over an
    /// edit typed before it published is a different situation with the same consequence, and it
    /// has its own flag, <see cref="EditedBeforeFirstLoad"/>, because nothing changed elsewhere in
    /// that case.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StoredValuesNotShown))]
    [NotifyPropertyChangedFor(nameof(StoredValuesNotice))]
    public partial bool ChangedElsewhere { get; private set; }

    /// <summary>
    /// The first read published while an edit was already on screen (review finding M6). The load
    /// is fast but not instant, and what the user typed into an empty tab is a real edit: it is
    /// kept, and the stored values are the ones not on screen.
    /// </summary>
    /// <remarks>FIXER LIST F13. Split from <see cref="ChangedElsewhere"/>, which claimed a
    /// concurrent writer that does not exist in this case.</remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StoredValuesNotShown))]
    [NotifyPropertyChangedFor(nameof(StoredValuesNotice))]
    public partial bool EditedBeforeFirstLoad { get; private set; }

    /// <summary>Whether the stored filter block differs from what is on screen, for either
    /// reason. The one flag the view's notice binds.</summary>
    public bool StoredValuesNotShown => ChangedElsewhere || EditedBeforeFirstLoad;

    /// <summary>The notice itself, which names the actual reason rather than assuming one.
    /// </summary>
    public string StoredValuesNotice => ChangedElsewhere
        ? "The stored library settings changed elsewhere. Revert to load them; saving now keeps what is on screen."
        : "The stored library settings finished loading after you started editing. Revert to load them; saving now keeps what is on screen.";

    /// <summary>Whether the filter block has unsaved edits.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    [NotifyPropertyChangedFor(nameof(SaveRefusalReason))]
    [NotifyPropertyChangedFor(nameof(SaveRefusal))]
    [NotifyPropertyChangedFor(nameof(HasPendingEdits))]
    public partial bool IsDirty { get; private set; }

    /// <summary>True while a filter-block save is in flight.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(SaveRefusalReason))]
    [NotifyPropertyChangedFor(nameof(SaveRefusal))]
    public partial bool IsSaving { get; private set; }

    /// <summary>True when at least one include or exclude path is outside every configured scan
    /// root, or a scan root is not an absolute path. Refuses the save (spec 10.2).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(SaveRefusalReason))]
    [NotifyPropertyChangedFor(nameof(SaveRefusal))]
    public partial bool HasInvalidPath { get; private set; }

    /// <summary>True when at least one name rule has an empty pattern or an uncompilable regex.
    /// Refuses the save.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(SaveRefusalReason))]
    [NotifyPropertyChangedFor(nameof(SaveRefusal))]
    public partial bool HasInvalidRule { get; private set; }

    /// <summary>Spec 7.5's calibration frames. Saves immediately.</summary>
    [ObservableProperty]
    public partial bool IncludeCalibration { get; set; }

    /// <summary>Spec 10.8's scheduler. Saves immediately.</summary>
    [ObservableProperty]
    public partial bool AutoScanEnabled { get; set; }

    /// <summary>Spec 10.8's interval. Saves immediately.</summary>
    [ObservableProperty]
    public partial IntervalOption? SelectedInterval { get; set; }

    /// <summary>Spec 10.7's watcher. Saves immediately. <c>watcher_debounce_ms</c> and
    /// <c>watcher_stability_check_ms</c> are deliberately not exposed (questions.md Q22): spec
    /// 12.7's Library row names this checkbox and nothing else, and the other two are tuning
    /// constants for a file-arrival heuristic.</summary>
    [ObservableProperty]
    public partial bool WatcherEnabled { get; set; }

    /// <summary>Spec 7.6, 12.7 (Phase 15A Task 6). <c>general.phd2_scan_enabled</c>: whether a
    /// scan discovers and ingests PHD2 guide logs. Saves immediately, on the same
    /// revert-on-failure shape as <see cref="OnIncludeCalibrationChanged"/>. Ships on.</summary>
    [ObservableProperty]
    public partial bool Phd2ScanEnabled { get; set; }

    /// <summary>
    /// Spec 12.7's scan scope pair, "All frames" (PAR-013). Per run, never stored: it starts from
    /// <see cref="IncludeCalibration"/> on every load and a run that overrides it does not rewrite
    /// the key. <see cref="IncludeCalibrationCheckBox"/>'s property above stays the stored key's
    /// editor.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanScopeLightOnly))]
    public partial bool ScanScopeAllFrames { get; set; }

    /// <summary>
    /// The other arm of spec 12.7's pair, "Light frames only". Per run, never stored.
    /// </summary>
    /// <remarks>
    /// The negation of <see cref="ScanScopeAllFrames"/> rather than a second stored bool: two
    /// independent flags can disagree, and a view-model test that sets one of them would then be
    /// asserting against a state the radio group can never produce. Setting it false is the other
    /// arm being checked, which has already moved the one flag, so it is a no-op here.
    /// </remarks>
    public bool ScanScopeLightOnly
    {
        get => !ScanScopeAllFrames;
        set
        {
            if (value)
            {
                ScanScopeAllFrames = false;
            }
        }
    }

    /// <summary>
    /// Ruling D2's cleanup checkbox: "Remove catalogue rows for missing files past the safety
    /// limit". Per run and cleared on every visit to the tab as well as at the end of every run
    /// (spec 12.7), because it is a decision about one run. Database rows only, never a file
    /// (spec 2.1).
    /// </summary>
    [ObservableProperty]
    public partial bool ForceOrphanCleanup { get; set; }

    /// <summary>
    /// Spec 12.2's scan filter notice (PAR-014) on this tab, above the rule editor. Computed by
    /// <see cref="ScanFilterConfig.ShowsSetupNotice"/> over the STORED document, which is the
    /// one computation the Dashboard's notice reads too, so the two surfaces cannot disagree.
    /// </summary>
    /// <remarks>
    /// Gated on <see cref="GeneralSettingsTabViewModel.IsReady"/> for the same reason every
    /// section of this tab is: a document that failed to read must render no live control, and
    /// <c>LibraryTabViewTests</c>' failed-read case asserts that no button in the view is visible.
    /// </remarks>
    public bool ShowScanFilterNotice
        => IsReady && ScanFilterConfig.ShowsSetupNotice(Saved);

    /// <summary>
    /// Clears the per-run cleanup override. Called when the tab is shown (the view's
    /// <c>OnAttachedToVisualTree</c>) and at the end of every run, which together are spec 12.7's
    /// "cleared on every visit": the tab is lazily constructed once and then re-shown, so clearing
    /// it in the constructor alone would remember it for the life of the process.
    /// </summary>
    public void ClearPerRunOptions() => ForceOrphanCleanup = false;

    private bool _nameRulesInViewPending;

    /// <summary>
    /// Asks for the rule editor to be brought into view the next time this tab is shown. Spec
    /// 12.2's Dashboard Review "opens Settings on the Library tab with the rule editor in view",
    /// and the shell route reaches this rather than a second scroll mechanism of its own.
    /// </summary>
    /// <remarks>
    /// Phase 14B fixer, fixer list item 4 (Task 5 review escalation 2, ruled a code fix). A flag
    /// rather than an event, because the shell selects the tab before its view is attached and
    /// often before the tab itself is constructed, so there is no view to receive an event when
    /// the route runs. <c>LibraryTabView.OnAttachedToVisualTree</c> consumes it and calls the same
    /// <c>BringIntoView</c> over <c>NameRulesSection</c> the tab's own Review button does.
    /// </remarks>
    public void RequestNameRulesInView() => _nameRulesInViewPending = true;

    /// <summary>Reads the pending request and clears it, so one route scrolls once.</summary>
    internal bool ConsumeNameRulesInViewRequest()
    {
        var pending = _nameRulesInViewPending;
        _nameRulesInViewPending = false;
        return pending;
    }

    private bool _guideLogSwitchInViewPending;

    /// <summary>
    /// Asks for spec 12.7's "Read PHD2 guide logs" switch to be brought into view the next time
    /// this tab is shown. Spec 12.5's Guiding empty notice reads "No PHD2 guide logs catalogued"
    /// and offers "Enable guide log scanning", which lands here through
    /// <c>MainWindowViewModel</c>'s one Settings route.
    /// </summary>
    /// <remarks>
    /// Phase 15B Task 5c. A flag rather than an event, and consumed on the view's attach, for the
    /// reason <see cref="RequestNameRulesInView"/> carries: the shell selects the tab before its
    /// view is attached and often before the tab itself is constructed, so there is no view to
    /// receive an event when the route runs.
    /// </remarks>
    public void RequestGuideLogSwitchInView() => _guideLogSwitchInViewPending = true;

    /// <summary>Reads the pending request and clears it, so one route scrolls once.</summary>
    internal bool ConsumeGuideLogSwitchInViewRequest()
    {
        var pending = _guideLogSwitchInViewPending;
        _guideLogSwitchInViewPending = false;
        return pending;
    }

    /// <summary>The shared progress marshaller, bound directly for the message and the bar, the
    /// way <c>StatusBarViewModel</c> binds it.</summary>
    public ScanStatusService? ScanStatus => _scanStatus;

    /// <summary>Whether a scan is running, mirrored so the view needs no null checks.</summary>
    public bool IsScanRunning => _scanStatus?.IsRunning ?? false;

    /// <summary>The live progress line (spec 10.4's envelope).</summary>
    public string ScanMessage => _scanStatus?.Message ?? "";

    /// <summary>The live percentage, 0 to 100.</summary>
    public double ScanPercent => _scanStatus?.Percent ?? 0d;

    /// <summary>True while a scan is running with no determinate percentage.</summary>
    public bool IsScanIndeterminate => _scanStatus?.IsIndeterminate ?? false;

    /// <summary>
    /// Why Save is disabled, as a tooltip. The web has three separate title strings for the three
    /// refusals and so does this.
    /// </summary>
    public string SaveRefusalReason
    {
        get
        {
            if (LoadFailed)
            {
                return "The stored settings could not be read, so nothing can be saved over them.";
            }

            if (HasInvalidPath)
            {
                return "One or more paths are outside every configured library folder.";
            }

            if (HasInvalidRule)
            {
                return "One or more name rules are invalid.";
            }

            if (IsSaving)
            {
                return "Saving.";
            }

            return IsDirty ? "Save the library folders, paths and rules." : "No unsaved changes.";
        }
    }

    /// <inheritdoc />
    public string Label => "Library folders, paths and rules";

    /// <inheritdoc />
    public string NavigationKey => "library";

    /// <summary>The save bar's refusal: the Save tooltip text, but only while there is something
    /// to save and saving is not possible, so a clean tab or a savable one reports null.</summary>
    public string? SaveRefusal => IsDirty && !CanSave() ? SaveRefusalReason : null;

    Task IPendingEdits.SaveAsync() => SaveCommand.ExecuteAsync(null);

    void IPendingEdits.Discard()
    {
        if (IsSaving)
        {
            _discardAfterSave = true;
            return;
        }

        Revert();
    }

    // Runs a Discard that arrived during a save, on the UI thread, once that save has finished
    // either way. Called from both of SaveAsync's completion callbacks after IsSaving clears.
    private void RevertIfDiscardWaited()
    {
        if (_discardAfterSave)
        {
            _discardAfterSave = false;
            Revert();
        }
    }

    // PendingLoad, PendingWrite and IsDisposed belong to the spine (FIXER LIST F18).

    /// <summary>
    /// <c>LoadFailed</c> and <c>IsLoading</c> live on the spine, so the two members that depend on
    /// them are refreshed here rather than through attributes the base cannot carry for a derived
    /// tab's command. <c>IsLoading</c> is the one that matters for ordering: the spine marks the
    /// tab savable and then clears <c>IsLoading</c>, so this is the first notification after a
    /// first read published under an edit makes Save reachable.
    /// </summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName is nameof(LoadFailed) or nameof(IsLoading))
        {
            SaveCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SaveRefusalReason));
            OnPropertyChanged(nameof(SaveRefusal));

            // ShowScanFilterNotice is gated on IsReady, which these two derive, so a read that
            // fails after the view has bound must take the notice down with the sections.
            OnPropertyChanged(nameof(ShowScanFilterNotice));
        }
    }

    /// <summary>
    /// Adds a scan root (spec 10.1). Refuses a relative path, a duplicate, and a path that is a
    /// parent or a child of an existing root: each root carries its own confinement boundary, and
    /// nested roots would walk and ingest the same file twice.
    /// </summary>
    /// <returns>True when the root was added.</returns>
    /// <remarks>FIXER LIST F10: the rule and its wording are
    /// <c>ScanFilterConfig.RefuseScanRoot</c>'s, which is also what
    /// <c>ScanFilterConfig.Validate</c> enforces on the write path and what the setup wizard's
    /// first step shows, so the same gesture is refused with the same sentence wherever it is
    /// made.</remarks>
    public bool AddScanRoot(string path)
    {
        var candidate = path.Trim();
        if (candidate.Length == 0)
        {
            return false;
        }

        if (ScanFilterConfig.RefuseScanRoot(candidate, [.. ScanRoots.Select(row => row.Path)]) is { } refusal)
        {
            ErrorMessage = refusal;
            return false;
        }

        ErrorMessage = null;
        ScanRoots.Add(new ScanRootRowViewModel(candidate));
        MarkDirty();
        return true;
    }

    /// <summary>Adds an include path. A path outside every scan root is added and shown as a
    /// configuration error, never dropped (spec 10.2).</summary>
    public void AddIncludePath(string path) => AddFilterPath(path, IncludePaths, isExclude: false);

    /// <summary>Adds an exclude path, on the same terms.</summary>
    public void AddExcludePath(string path) => AddFilterPath(path, ExcludePaths, isExclude: true);

    [RelayCommand]
    private void AddScanRootFromBox()
    {
        if (AddScanRoot(NewScanRootPath))
        {
            NewScanRootPath = "";
        }
    }

    [RelayCommand]
    private void RemoveScanRoot(ScanRootRowViewModel row)
    {
        if (ScanRoots.Remove(row))
        {
            MarkDirty();
        }
    }

    [RelayCommand]
    private void AddIncludePathFromBox()
    {
        if (NewIncludePath.Trim().Length == 0)
        {
            return;
        }

        AddIncludePath(NewIncludePath);
        NewIncludePath = "";
    }

    [RelayCommand]
    private void AddExcludePathFromBox()
    {
        if (NewExcludePath.Trim().Length == 0)
        {
            return;
        }

        AddExcludePath(NewExcludePath);
        NewExcludePath = "";
    }

    [RelayCommand]
    private void RemoveFilterPath(FilterPathRowViewModel row)
    {
        var list = row.IsExclude ? ExcludePaths : IncludePaths;
        if (list.Remove(row))
        {
            MarkDirty();
        }
    }

    /// <summary>Appends a rule with the web's defaults.</summary>
    [RelayCommand]
    private void AddNameRule()
    {
        var row = NameRuleRowViewModel.NewRule();
        row.PropertyChanged += OnNameRuleChanged;
        NameRules.Add(row);
        MarkDirty();
    }

    [RelayCommand]
    private void RemoveNameRule(NameRuleRowViewModel row)
    {
        row.PropertyChanged -= OnNameRuleChanged;
        if (NameRules.Remove(row))
        {
            MarkDirty();
        }
    }

    /// <summary>
    /// Writes the whole filter block (roots, include and exclude paths, name rules) through
    /// <c>SettingsStore.SaveGeneral</c>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        // TRACKING item 13: CanExecute is the affordance, the body is the guard. A direct
        // Execute, and a click that lands between an edit and the command being notified, both
        // arrive here.
        Validate();
        if (!CanSave())
        {
            return;
        }

        IsSaving = true;
        var roots = ScanRoots.Select(row => row.Path).ToArray();
        var includePaths = IncludePaths.Select(row => row.Path).ToArray();
        var excludePaths = ExcludePaths.Select(row => row.Path).ToArray();
        var rules = NameRules.Select(row => row.ToRule()).ToArray();
        var savedVersion = _editVersion;

        await Write(
            general => general with
            {
                ScanRoots = roots,
                // Polish wave 1, ruling 1: a save of this block is a review of the rules.
                ScanFiltersReviewed = true,
                // `with` on the existing config, so scan_filters' own JsonExtensionData (an
                // unrecognized key from a future version) survives the write (spec 5.8).
                ScanFilters = general.ScanFilters with
                {
                    IncludePaths = includePaths,
                    ExcludePaths = excludePaths,
                    NameRules = rules,
                },
            },
            onSuccess: () =>
            {
                IsSaving = false;
                if (_editVersion == savedVersion)
                {
                    IsDirty = false;
                }

                StatusMessage = "Library folders, paths and rules saved";
                ChangedElsewhere = false;
                EditedBeforeFirstLoad = false;
                RevertIfDiscardWaited();
            },
            onFailure: () =>
            {
                // The edits stay on screen. The store refused them, and throwing away what the
                // user typed is not a way to report that.
                IsSaving = false;
                RevertIfDiscardWaited();
            }).ConfigureAwait(false);
    }

    // IsReadyToSave and !LoadFailed are review finding I1: no write may be queued before a load has
    // published, or after one has failed, because the four lists this save writes would then be
    // whatever the empty constructor left them.
    private bool CanSave()
        => IsReadyToSave && !LoadFailed && IsDirty && !IsSaving && !HasInvalidPath && !HasInvalidRule;

    /// <summary>Restores the filter block to the last saved document, exactly (the web's
    /// Revert).</summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert()
    {
        if (!IsDirty)
        {
            return;
        }

        ApplyFilterBlock(Saved);
        ApplyScalars(Saved);
        ErrorMessage = null;
        StatusMessage = null;
        IsDirty = false;
        ChangedElsewhere = false;
        EditedBeforeFirstLoad = false;
        Validate();
    }

    private bool CanRevert() => IsDirty;

    /// <summary>Hides the notice without touching the edit; the next outside write raises it
    /// again through the same two flags.</summary>
    [RelayCommand]
    private void DismissStoredValuesNotice()
    {
        ChangedElsewhere = false;
        EditedBeforeFirstLoad = false;
    }

    /// <summary>Spec 12.7's manual scan button.</summary>
    /// <remarks>
    /// No <see cref="CancellationToken"/> parameter (FIXER LIST F19, the first concrete hit of the
    /// F16 audit, and Task 8's deviation D10): a command built from a
    /// <c>Func&lt;CancellationToken, Task&gt;</c> cancels the in-flight token on a second
    /// <c>Execute</c>, and <c>Task.Run</c> with an already-cancelled token skips the delegate, so a
    /// second press would abort the running manual scan rather than be refused by the guard below.
    /// Cancellation belongs to <see cref="CancelScanCommand"/>, which reaches
    /// <c>ScanCoordinator.Cancel</c>.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRunScan))]
    private async Task RunScanAsync()
    {
        // TRACKING item 13 again, and the same three conditions StatusBarViewModel uses, so the
        // two buttons agree about when a scan may start. A second press is refused here.
        if (!CanRunScan())
        {
            return;
        }

        try
        {
            // Spec 10.3's two per-run arguments, built at the press and carried no further: the
            // stored include_calibration is not rewritten by either of them.
            var options = new ScanRunOptions(ScanScopeAllFrames, ForceOrphanCleanup);

            // Off the dispatcher: RunAsync validates the configured roots synchronously before it
            // returns a task, which is why the status bar's own button does the same Task.Run.
            await Task.Run(() => _runScan(options, Lifetime), Lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The user cancelled the scan. Not a failure.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The Library tab could not start a scan");
        }
        finally
        {
            // Spec 12.7: a decision about one run does not survive it.
            Post(ClearPerRunOptions);
        }
    }

    private bool CanRunScan()
        => _scanStatus is null || (!_scanStatus.IsRunning && !_scanStatus.ResolutionInProgress);

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan()
    {
        if (!CanCancelScan())
        {
            return;
        }

        _cancelScan();
    }

    private bool CanCancelScan() => _scanStatus?.IsRunning ?? false;

    /// <summary>Whether the "Run setup again" link is offered (spec 12.1's last sentence).
    /// </summary>
    public bool CanRunSetupAgain => _runSetupAgain is not null;

    /// <summary>
    /// Design-spec 12.1: "The wizard is also reachable from Settings as 'Run setup again'."
    /// Opens the one <c>SetupWizardService</c>, which is the same one the first-run branch uses.
    /// </summary>
    /// <remarks>
    /// The wizard writes the same <c>general</c> document this tab edits, so the tab's
    /// <c>GeneralChanged</c> subscription (review finding I3) is what keeps the lists here from
    /// going stale behind it. No <see cref="CancellationToken"/> parameter: a command built from a
    /// <c>Func&lt;CancellationToken, Task&gt;</c> cancels the in-flight token on a second Execute,
    /// which would abandon an open wizard rather than be refused by the body guard (Phase 9
    /// Task 8, deviation D10).
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRunSetupAgain))]
    private async Task RunSetupAgainAsync()
    {
        // TRACKING item 13: CanExecute is the affordance, the body is the guard.
        if (_runSetupAgain is not { } show)
        {
            return;
        }

        try
        {
            await show().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A wizard that could not be opened is not a reason to take the Settings page down.
            Logger.LogWarning(ex, "The Library tab could not open the setup wizard");
        }
    }

    // ---- the four immediate, optimistic saves ------------------------------------------------

    partial void OnIncludeCalibrationChanged(bool value)
        => ImmediateSave(
            general => general with { IncludeCalibration = value },
            value ? "All frames" : "Light frames only",
            () => IncludeCalibration = !value);

    partial void OnAutoScanEnabledChanged(bool value)
        => ImmediateSave(
            general => general with { AutoScanEnabled = value },
            value ? "Auto-scan enabled" : "Auto-scan disabled",
            () => AutoScanEnabled = !value);

    partial void OnSelectedIntervalChanged(IntervalOption? oldValue, IntervalOption? newValue)
    {
        if (newValue is null)
        {
            return;
        }

        var minutes = newValue.Minutes;
        ImmediateSave(
            general => general with { AutoScanIntervalMinutes = minutes },
            "Scan interval updated",
            () => SelectedInterval = oldValue);
    }

    partial void OnWatcherEnabledChanged(bool value)
        => ImmediateSave(
            general => general with { WatcherEnabled = value },
            value ? "File watching enabled" : "File watching disabled",
            () => WatcherEnabled = !value);

    partial void OnPhd2ScanEnabledChanged(bool value)
        => ImmediateSave(
            general => general with { Phd2ScanEnabled = value },
            value ? "Reading PHD2 guide logs" : "PHD2 guide logs no longer read",
            () => Phd2ScanEnabled = !value);

    // ---- the spine's three hooks -------------------------------------------------------------

    /// <summary>
    /// Copies a read document into the controls. Called by the spine for the first read and for
    /// another writer's <c>GeneralChanged</c> when the filter block is clean, always inside one
    /// publish.
    /// </summary>
    /// <remarks>
    /// Review finding M6: the first read is fast but not instant, and an edit typed before it
    /// publishes is a real edit. It is kept, the four scalar controls are still seeded from the
    /// document, and the tab says the stored values are the ones not on screen. A
    /// <c>GeneralChanged</c> arriving in that state never reaches here at all: the spine routes it
    /// to <see cref="OnStoredDocumentChangedElsewhere"/> because <see cref="HasPendingEdits"/> is
    /// true.
    /// </remarks>
    protected override void ApplyDocument(GeneralSettings general)
    {
        if (IsDirty)
        {
            // FIXER LIST F13: this is the first read landing under an edit, not a concurrent
            // writer, so it raises its own flag and not ChangedElsewhere.
            EditedBeforeFirstLoad = true;
        }
        else
        {
            ApplyFilterBlock(general);
            ChangedElsewhere = false;
            EditedBeforeFirstLoad = false;
        }

        ApplyScalars(general);
        Validate();
        SaveCommand.NotifyCanExecuteChanged();

        // The notice reads the stored document, which the spine has just moved on.
        OnPropertyChanged(nameof(ShowScanFilterNotice));
    }

    /// <summary>The filter block is the one part of this tab behind a Save button, so an edit in it
    /// is what a reload would throw away.</summary>
    public override bool HasPendingEdits => IsDirty;

    /// <summary>
    /// Another writer changed the <c>general</c> document while the filter block was dirty (review
    /// finding I3). The edit is never overwritten and never discarded; Revert is the way to take
    /// the stored values, and it is already enabled because the tab is dirty.
    /// </summary>
    /// <remarks>
    /// The tab is a DI singleton whose state outlives a visit to the Settings page, and Task 9's
    /// wizard writes this document from a link on this very tab, so the sequence this exists to
    /// stop is: open Library, run the wizard, come back, save a rule, lose the wizard's roots. The
    /// test-a-path box follows the store unconditionally either way: the spine has already moved
    /// <c>Saved</c> on, and that box tests the saved configuration.
    /// </remarks>
    protected override void OnStoredDocumentChangedElsewhere() => ChangedElsewhere = true;

    private void ApplyFilterBlock(GeneralSettings general) => Apply(() =>
    {
        ScanRoots.Clear();
        foreach (var root in general.ScanRoots)
        {
            ScanRoots.Add(new ScanRootRowViewModel(root));
        }

        IncludePaths.Clear();
        foreach (var path in general.ScanFilters.IncludePaths)
        {
            IncludePaths.Add(new FilterPathRowViewModel(path, isExclude: false));
        }

        ExcludePaths.Clear();
        foreach (var path in general.ScanFilters.ExcludePaths)
        {
            ExcludePaths.Add(new FilterPathRowViewModel(path, isExclude: true));
        }

        foreach (var row in NameRules)
        {
            row.PropertyChanged -= OnNameRuleChanged;
        }

        NameRules.Clear();
        foreach (var rule in general.ScanFilters.NameRules)
        {
            var row = new NameRuleRowViewModel(rule);
            row.PropertyChanged += OnNameRuleChanged;
            NameRules.Add(row);
        }
    });

    private void ApplyScalars(GeneralSettings general) => Apply(() =>
    {
        IncludeCalibration = general.IncludeCalibration;

        // Spec 12.7: the pair STARTS from the stored key on every load and writes nothing back.
        ScanScopeAllFrames = general.IncludeCalibration;

        AutoScanEnabled = general.AutoScanEnabled;
        WatcherEnabled = general.WatcherEnabled;
        Phd2ScanEnabled = general.Phd2ScanEnabled;

        // One body for the interval list, shared with the setup wizard's step 4: the presets added
        // once and never replaced, at most one extra entry for a hand-edited minute count, and the
        // selection taken from the list that is already there. Clearing this collection under the
        // bound selector and then assigning the option it already held is what rendered the "Scan
        // interval" select empty on a first visit to this tab (task5c-review.md P3-6).
        SelectedInterval = IntervalChoices.Publish(IntervalOptions, general.AutoScanIntervalMinutes);
    });

    private void AddFilterPath(string path, ObservableCollection<FilterPathRowViewModel> list, bool isExclude)
    {
        var candidate = path.Trim();
        if (candidate.Length == 0)
        {
            return;
        }

        if (list.Any(row => string.Equals(row.Path, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = $"'{candidate}' is already in this list.";
            return;
        }

        ErrorMessage = null;

        // Added whatever its verdict. A path outside every root is a configuration error the user
        // has to see and fix, not something this tab quietly discards (spec 10.2).
        list.Add(new FilterPathRowViewModel(candidate, isExclude));
        MarkDirty();
    }

    private void OnNameRuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NameRuleRowViewModel.ErrorText)
            || e.PropertyName == nameof(NameRuleRowViewModel.HasError))
        {
            // Written by the validation pass itself; treating it as an edit would loop.
            return;
        }

        MarkDirty();
    }

    private void MarkDirty()
    {
        if (IsApplying)
        {
            return;
        }

        StatusMessage = null;
        _editVersion++;
        IsDirty = true;
        Validate();
    }

    /// <summary>
    /// The inline half of the validation. <c>SettingsStore.SaveGeneral</c> is still the
    /// enforcement point and still runs <c>ScanFilterConfig.Validate</c> on every write; this
    /// exists so the user sees which row is wrong before the save is refused.
    /// </summary>
    private void Validate()
    {
        var roots = new List<string>();
        var resolved = new List<(ScanRootRowViewModel Row, string Full)>();
        var invalidPath = false;

        foreach (var row in ScanRoots)
        {
            var full = TryFullPath(row.Path);
            if (full is null)
            {
                row.ErrorText = "A library folder must be a full absolute path.";
                invalidPath = true;
                continue;
            }

            row.ErrorText = null;
            roots.Add(full);
            resolved.Add((row, full));
        }

        // Review finding M1. AddScanRoot refuses a duplicate or nested root, but that is one UI
        // entry point, not the rule: a hand-edited document, the setup wizard, or removing the
        // outer root and adding it back in a different order all reach a nested pair, and the
        // walker then ingests the same file twice. The rule now lives in
        // ScanFilterConfig.RefuseScanRoot and is enforced by ScanFilterConfig.Validate on the write
        // path (FIXER LIST F10); this pass is the per-row half, which marks WHICH row is wrong
        // rather than refusing the document as a whole.
        for (var i = 0; i < resolved.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (!PathConfinement.IsUnderOrEqual(resolved[j].Full, resolved[i].Full)
                    && !PathConfinement.IsUnderOrEqual(resolved[i].Full, resolved[j].Full))
                {
                    continue;
                }

                resolved[i].Row.ErrorText = string.Equals(resolved[i].Full, resolved[j].Full, StringComparison.OrdinalIgnoreCase)
                    ? $"Duplicate of the library folder '{resolved[j].Row.Path}'."
                    : $"Overlaps the library folder '{resolved[j].Row.Path}'. Library folders may not be nested.";
                invalidPath = true;
                break;
            }
        }

        var rootList = roots.Count == 0
            ? "no library folders are configured"
            : string.Join(", ", ScanRoots.Select(row => row.Path));

        foreach (var row in IncludePaths.Concat(ExcludePaths))
        {
            var full = TryFullPath(row.Path);
            if (full is null)
            {
                row.ErrorText = $"{row.FieldName} entries must be full absolute paths.";
                invalidPath = true;
                continue;
            }

            if (!roots.Any(root => PathConfinement.IsUnderOrEqual(root, full)))
            {
                // The roadmap's named assertion, and spec 10.2's wording: outside every root is a
                // configuration error surfaced here, not a silently dropped entry.
                row.ErrorText = $"Path must be inside one of the library folders ({rootList}).";
                invalidPath = true;
                continue;
            }

            row.ErrorText = null;
        }

        var invalidRule = false;
        foreach (var row in NameRules)
        {
            row.ErrorText = row.ComputeError();
            invalidRule |= row.HasError;
        }

        HasInvalidPath = invalidPath;
        HasInvalidRule = invalidRule;
    }

    // One normalization, in Core, shared with ScanFilterConfig's own checks (FIXER LIST F10).
    private static string? TryFullPath(string path) => ScanFilterConfig.TryFullPath(path);

    // ---- scan status -------------------------------------------------------------------------

    private void OnScanStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScanStatusService.IsRunning):
                OnPropertyChanged(nameof(IsScanRunning));
                OnPropertyChanged(nameof(IsScanIndeterminate));
                RunScanCommand.NotifyCanExecuteChanged();
                CancelScanCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ScanStatusService.ResolutionInProgress):
                RunScanCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ScanStatusService.Message):
                OnPropertyChanged(nameof(ScanMessage));
                break;
            case nameof(ScanStatusService.Percent):
                OnPropertyChanged(nameof(ScanPercent));
                break;
            case nameof(ScanStatusService.HasDeterminatePercent):
            case nameof(ScanStatusService.IsIndeterminate):
                OnPropertyChanged(nameof(IsScanIndeterminate));
                break;
        }
    }

    /// <summary>
    /// The tab's own teardown, run by the spine after it has cancelled the tab lifetime and dropped
    /// the <c>GeneralChanged</c> subscription: the progress subscription and every rule row's
    /// handler, so nothing it started can publish into a tab whose database is gone.
    /// </summary>
    protected override void DisposeCore()
    {
        if (_scanStatus is not null)
        {
            _scanStatus.PropertyChanged -= OnScanStatusPropertyChanged;
        }

        foreach (var row in NameRules)
        {
            row.PropertyChanged -= OnNameRuleChanged;
        }
    }
}
