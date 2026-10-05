using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.Theme;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>One theme the picker offers: the stored id and its label.</summary>
public sealed record ThemeOption(string Id, string Label);

/// <summary>One root font size choice (design-spec 14.4): the stored id, its label and its
/// pixel size.</summary>
public sealed record TextSizeOption(string Id, string Label, double Pixels);

/// <summary>One content width choice (design-spec 5.8.1): the stored id and its label.</summary>
public sealed record ContentWidthOption(string Id, string Label);

/// <summary>One dashboard page size choice (design-spec 12.2).</summary>
public sealed record PageSizeOption(int Size, string Label);

/// <summary>One cross-session chart scope choice (design-spec 5.8.3).</summary>
public sealed record ChartSessionsOption(int Sessions, string Label);

/// <summary>
/// Design-spec 12.7's Display tab: the theme picker, text size, content width, the dashboard's
/// default page size, the cross-session chart's default scope, the six metric groups with their
/// field checkboxes, and a column picker per persisted table.
/// </summary>
/// <remarks>
/// <para>
/// Two save shapes, matching the web. Everything except the metric groups saves immediately and
/// optimistically, with a roll-back on failure. The metric groups are the one part behind a Save
/// button, because six groups and 27 field checkboxes are edited together and a half-applied set
/// is not a state anyone wants persisted.
/// </para>
/// <para>
/// Three documents are written from this one tab and each goes through its own single writer:
/// <c>general</c> through <c>SettingsStore.MutateGeneral</c> (the base class),
/// <c>display.groups</c> through <c>SettingsStore.SaveDisplay</c>, and
/// <c>display.columns</c> through <c>DisplayColumnWriter</c>, which is that document half's one
/// serialized chain. <c>graph.default_chart_sessions</c> goes through
/// <c>GraphSettingsWriter</c>, the one writer of the graph document, so a default written here and
/// a metric toggled on a chart cannot lose one another (design-lessons rule 2).
/// </para>
/// </remarks>
public sealed partial class DisplayTabViewModel : GeneralSettingsTabViewModel
{
    /// <summary>
    /// Design-spec 14.4's four root font sizes, with the web's labels. The pixel figures come from
    /// <c>MainWindowViewModel.ResolveRootFontSize</c> rather than being spelled again here: that
    /// method is what actually sets <c>MainWindow.FontSize</c>, and a second table would be a
    /// picker that could disagree with the window it drives.
    /// </summary>
    public static readonly IReadOnlyList<TextSizeOption> TextSizes =
    [
        new("small", "Small", MainWindowViewModel.ResolveRootFontSize("small")),
        new("medium", "Medium", MainWindowViewModel.ResolveRootFontSize("medium")),
        new("large", "Large", MainWindowViewModel.ResolveRootFontSize("large")),
        new("x-large", "Extra Large", MainWindowViewModel.ResolveRootFontSize("x-large")),
    ];

    /// <summary>
    /// Design-spec 5.8.1's three content widths (Phase 5 ruling Q2). The web offers four; the port
    /// offers the three the spec names, and <c>MainWindowViewModel.ResolveContentMaxWidth</c>
    /// already implements exactly these.
    /// </summary>
    public static readonly IReadOnlyList<ContentWidthOption> ContentWidths =
    [
        new("normal", "Normal (1200 px)"),
        new("wide", "Wide (1600 px)"),
        new("extra-wide", "Extra wide (unbounded)"),
    ];

    /// <summary>
    /// Spec 5.8.1's four choices. Phase 14C Task 4 fix pass (task4-review.md, "RULED, one list"):
    /// this used to be its own literal, `[10, 25, 50, 100, 250]`, the web's larger
    /// <c>TargetFeed.tsx</c> <c>PAGE_SIZES</c> list rather than the spec's amended one, and a
    /// stored value outside it was appended here as its own selectable entry. Two page-size lists
    /// in one application meant a stored 10 paged the list by 10 while the dashboard pager's own
    /// select, which never offered 10, read 50. One declaration now:
    /// <see cref="TargetListViewModel.PageSizeOptions"/> is the reference the pager's select reads
    /// directly, and this is that same list, not a second one that happens to agree with it today.
    /// </summary>
    public static readonly IReadOnlyList<int> PageSizes = TargetListViewModel.PageSizeOptions;

    /// <summary>
    /// Questions.md Q23, which amends design-spec 5.8.3: the choices are 1, 3, 5, 10 and 20, and
    /// <strong>0 is deliberately not offered</strong>. Design-spec 5.8.3 says the "All sessions"
    /// toggle "widens the scope to every session for the rest of the process and is deliberately
    /// not persisted", so a persisted 0 would contradict the paragraph that defines the key. The
    /// shipped default stays 1, which spec 5.8.3 states twice.
    /// </summary>
    public static readonly IReadOnlyList<ChartSessionsOption> ChartSessions =
    [
        new(1, "Latest session"),
        new(3, "Latest 3 sessions"),
        new(5, "Latest 5 sessions"),
        new(10, "Latest 10 sessions"),
        new(20, "Latest 20 sessions"),
    ];

    private readonly Func<DisplaySettings> _loadDisplay;
    private readonly Action<DisplaySettings> _saveDisplay;
    private readonly Func<GraphSettings> _loadGraph;
    private readonly GraphSettingsWriter _graph;
    private readonly Action<string> _applyTheme;
    private readonly DisplayColumnWriter _columns;
    private readonly Func<IReadOnlyList<CustomColumnDefinition>>? _loadCustomColumns;
    private readonly Action<EventHandler<DisplaySettings>>? _unsubscribeDisplayChanged;
    private readonly Action<EventHandler>? _unsubscribeCustomColumnsChanged;

    // The display document as it was last read or written, so a groups save never overwrites the
    // columns half with a snapshot taken before some table toggled a column.
    private DisplaySettings _display = new();

    // The custom columns as they were on this tab's one background pass (task6c-report.md).
    // RevertGroups reuses this rather than reading again: reverting the metric groups touches
    // neither the catalogue nor the ledger picker's row set.
    private IReadOnlyList<CustomColumnDefinition> _customColumns = [];

    // The newest definition-change request, so a re-read that finishes out of order is dropped
    // rather than publishing an older list over a newer one. See OnCustomColumnsChanged.
    private int _customColumnsGeneration;

    // The thread currently inside SaveDisplay, or 0. SettingsStore raises DisplayChanged
    // synchronously on the writing thread, so this tab's own groups save comes straight back to
    // OnDisplayChanged; the same recognition the base class performs for GeneralChanged.
    private int _savingDisplayThreadId;

    /// <param name="loadGeneral">Normally <c>SettingsStore.GetGeneral</c>.</param>
    /// <param name="mutateGeneral">Normally <c>SettingsStore.MutateGeneral</c>.</param>
    /// <param name="loadDisplay">Normally <c>SettingsStore.GetDisplay</c>. Called off the UI
    /// thread.</param>
    /// <param name="saveDisplay">Normally <c>SettingsStore.SaveDisplay</c>, which now raises
    /// <c>DisplayChanged</c> (FIXER item 7) so a live frame table re-gates its columns.</param>
    /// <param name="loadGraph">Normally <c>SettingsStore.GetGraph</c>, for
    /// <c>default_chart_sessions</c>.</param>
    /// <param name="graph">The one writer of the graph document.</param>
    /// <param name="columns">The one writer of <c>display.columns</c>.</param>
    /// <param name="dashboardColumns">The live dashboard target list's own column rows, or null
    /// on a surface with no dashboard. See <see cref="ColumnPickerViewModel.ForDashboard"/>.
    /// </param>
    /// <param name="toggleDashboardColumn">The live target list's own toggle.</param>
    /// <param name="availableThemes">Normally <c>() =&gt; ThemeManager.Available</c>.</param>
    /// <param name="applyTheme">Normally <c>ThemeManager.Apply</c>, which merges the theme
    /// dictionary, sets the theme variant and then calls <c>ChartTheme.Apply()</c> in that order
    /// (FIXER item 8, ruling Q19).</param>
    /// <param name="loadCustomColumns">Normally <c>CustomColumnRepository.List</c>. Read on this
    /// tab's own background pass alongside <paramref name="loadDisplay"/> and
    /// <paramref name="loadGraph"/> (Task 6 review finding 1: one pass, one publish). Optional and
    /// trailing, so a test surface with no custom column table keeps a ledger picker that is always
    /// empty.</param>
    /// <param name="subscribeCustomColumnsChanged">Normally
    /// <c>CustomColumnRepository.Changed</c>. A delegate pair rather than the repository itself, for
    /// the reason <paramref name="subscribeDisplayChanged"/> is one, and with a second reason of its
    /// own: this tab is a lazily built singleton behind a memoized navigation item, and a handler
    /// the composition root attached by resolving this tab would build it, and its construction
    /// reads four documents. Subscribed from this constructor, so a tab nobody has visited
    /// subscribes nothing and costs nothing.</param>
    /// <param name="unsubscribeCustomColumnsChanged">The matching detach, run from
    /// <see cref="DisposeCore"/>.</param>
    public DisplayTabViewModel(
        Func<GeneralSettings> loadGeneral,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> mutateGeneral,
        Func<DisplaySettings> loadDisplay,
        Action<DisplaySettings> saveDisplay,
        Func<GraphSettings> loadGraph,
        GraphSettingsWriter graph,
        DisplayColumnWriter columns,
        Func<IReadOnlyList<string>> availableThemes,
        Action<string> applyTheme,
        IReadOnlyList<ColumnViewModel>? dashboardColumns = null,
        Action<ColumnViewModel>? toggleDashboardColumn = null,
        Action<Action>? post = null,
        ILogger? logger = null,
        Action<EventHandler<GeneralSettings>>? subscribeGeneralChanged = null,
        Action<EventHandler<GeneralSettings>>? unsubscribeGeneralChanged = null,
        Action<EventHandler<DisplaySettings>>? subscribeDisplayChanged = null,
        Action<EventHandler<DisplaySettings>>? unsubscribeDisplayChanged = null,
        Func<IReadOnlyList<CustomColumnDefinition>>? loadCustomColumns = null,
        Action<EventHandler>? subscribeCustomColumnsChanged = null,
        Action<EventHandler>? unsubscribeCustomColumnsChanged = null)
        : base(loadGeneral, mutateGeneral, post, logger, subscribeGeneralChanged, unsubscribeGeneralChanged)
    {
        _loadDisplay = loadDisplay;
        _saveDisplay = saveDisplay;
        _loadGraph = loadGraph;
        _graph = graph;
        _applyTheme = applyTheme;
        _columns = columns;
        _loadCustomColumns = loadCustomColumns;
        _unsubscribeDisplayChanged = unsubscribeDisplayChanged;
        _unsubscribeCustomColumnsChanged = unsubscribeCustomColumnsChanged;

        Themes = [.. availableThemes().Select(id => new ThemeOption(id, ThemeManager.LabelFor(id)))];

        // Assigned before anything can observe them; the applying guard covers the first publish.
        SelectedTheme = Themes.FirstOrDefault();
        SelectedTextSize = TextSizes[0];
        SelectedContentWidth = ContentWidths[2];
        SelectedPageSize = null;
        SelectedChartSessions = ChartSessions[0];

        FramesColumns = ColumnPickerViewModel.ForFrames(new DisplaySettings(), columns);

        // Built empty here, the same placeholder-then-refill shape FramesColumns takes: the real
        // custom column list is not known until the background pass below completes.
        LedgerColumns = ColumnPickerViewModel.ForLedger(new DisplaySettings(), columns, []);
        DashboardColumns = dashboardColumns is null || toggleDashboardColumn is null
            ? null
            : ColumnPickerViewModel.ForDashboard(dashboardColumns, toggleDashboardColumn);

        subscribeDisplayChanged?.Invoke(OnDisplayChanged);
        subscribeCustomColumnsChanged?.Invoke(OnCustomColumnsChanged);

        // One background pass, one publish (Task 6 review finding 1). The display and graph
        // documents are read by ReadCompanionDocuments below, on the thread this call owns, and
        // land in the same Apply as the general document. Starting a second Task.Run here is what
        // the base type's publish contract forbids.
        Load();
    }

    /// <summary>The theme picker (design-spec 14). One theme ships, so it renders one entry.
    /// </summary>
    public IReadOnlyList<ThemeOption> Themes { get; }

    // Instance passthroughs over the three static tables above. Compiled bindings resolve a path
    // against the x:DataType instance, so a static member is not reachable from XAML without an
    // x:Static every time; one property each keeps the views plain.

    /// <summary>Design-spec 14.4's four root font sizes.</summary>
    public IReadOnlyList<TextSizeOption> TextSizeChoices => TextSizes;

    /// <summary>Design-spec 5.8.1's three content widths.</summary>
    public IReadOnlyList<ContentWidthOption> ContentWidthChoices => ContentWidths;

    /// <summary>Questions.md Q23's five chart scopes. 0 is not among them.</summary>
    public IReadOnlyList<ChartSessionsOption> ChartSessionsChoices => ChartSessions;

    [ObservableProperty]
    public partial ThemeOption? SelectedTheme { get; set; }

    [ObservableProperty]
    public partial TextSizeOption SelectedTextSize { get; set; }

    [ObservableProperty]
    public partial ContentWidthOption SelectedContentWidth { get; set; }

    /// <summary>The five web page sizes, plus the stored value when it is not one of them.
    /// Rebuilt on every publish, so the extra entry cannot accumulate.</summary>
    public ObservableCollection<PageSizeOption> PageSizeOptions { get; } = [];

    [ObservableProperty]
    public partial PageSizeOption? SelectedPageSize { get; set; }

    [ObservableProperty]
    public partial ChartSessionsOption SelectedChartSessions { get; set; }

    /// <summary>The six metric groups (design-spec 5.8.2). The one part of this tab behind a Save
    /// button.</summary>
    public ObservableCollection<DisplayMetricGroupViewModel> Groups { get; } = [];

    /// <summary>Design-spec 12.2's dashboard column picker, or null on a surface with no
    /// dashboard.</summary>
    public ColumnPickerViewModel? DashboardColumns { get; }

    /// <summary>Design-spec 12.4's frame table column picker.</summary>
    public ColumnPickerViewModel FramesColumns { get; }

    /// <summary>
    /// Spec 12.15's Nights ledger column picker (Task 6c). Unlike <see cref="FramesColumns"/>,
    /// whose row set is the fixed <c>FrameColumns.All</c>, this picker's rows are the custom
    /// columns themselves, so the property is replaced (never mutated in place) each time
    /// <see cref="ApplyDisplayDocument"/> rebuilds it from a fresh read, and the view's binding
    /// follows because this is an <see cref="ObservableProperty"/>.
    /// </summary>
    [ObservableProperty]
    public partial ColumnPickerViewModel LedgerColumns { get; private set; }

    /// <summary>Whether the metric group block has unsaved edits.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveGroupsCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertGroupsCommand))]
    public partial bool GroupsDirty { get; private set; }

    /// <summary>The stored display document changed under an unsaved metric group edit. The edit
    /// is neither overwritten nor discarded; Revert is the way to take the stored values.
    /// </summary>
    [ObservableProperty]
    public partial bool GroupsChangedElsewhere { get; private set; }

    /// <summary>
    /// A read of the display document that threw. Cleared by the next successful apply of that
    /// document, whether from the first read or from another writer's <c>DisplayChanged</c>
    /// (review minor 4): a latched flag left a populated editor whose Save button could never be
    /// pressed again.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveGroupsCommand))]
    public partial bool DisplayLoadFailed { get; private set; }

    /// <summary>The in-flight definition re-read, so a case awaits it instead of sleeping. Null
    /// until a definition change has arrived. The same seam <c>PendingLoad</c> is.</summary>
    internal Task? PendingCustomColumnsRead { get; private set; }

    /// <inheritdoc />
    protected override bool HasPendingEdits => GroupsDirty;

    /// <inheritdoc />
    protected override void OnStoredDocumentChangedElsewhere() => GroupsChangedElsewhere = true;

    /// <inheritdoc />
    protected override void ApplyDocument(GeneralSettings general)
    {
        SelectedTheme = Themes.FirstOrDefault(option => option.Id == general.Theme) ?? Themes.FirstOrDefault();
        SelectedTextSize = TextSizes.FirstOrDefault(option => option.Id == general.TextSize) ?? TextSizes[0];
        SelectedContentWidth = ContentWidths.FirstOrDefault(option => option.Id == general.ContentWidth)
            ?? ContentWidths[2];

        PageSizeOptions.Clear();
        foreach (var size in PageSizes)
        {
            PageSizeOptions.Add(new PageSizeOption(size, size.ToString()));
        }

        // Fix pass (task4-review.md P2): a stored value outside PageSizes now reads as the
        // default, 50, the same rule TargetListViewModel.NormalizePageSize applies for the
        // pager's own select, rather than being appended here as a fifth, ad-hoc entry. Two
        // surfaces reading the same key must show the same selection for the same document.
        var normalized = TargetListViewModel.NormalizePageSize(general.DefaultPageSize);
        SelectedPageSize = PageSizeOptions.Single(option => option.Size == normalized);
    }

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        _unsubscribeDisplayChanged?.Invoke(OnDisplayChanged);
        _unsubscribeCustomColumnsChanged?.Invoke(OnCustomColumnsChanged);
        DashboardColumns?.Dispose();
        FramesColumns.Dispose();
        LedgerColumns.Dispose();
    }

    // ---- the immediate saves ------------------------------------------------------------------

    partial void OnSelectedThemeChanged(ThemeOption? oldValue, ThemeOption? newValue)
    {
        if (newValue is null)
        {
            return;
        }

        var id = newValue.Id;

        // FIXER item 8, ruling Q19: the swap merges the dictionary and sets the theme variant,
        // then calls ChartTheme.Apply(), in that order. Applied before the write so the window
        // changes with the click; a refused write rolls the picker and the theme back together.
        if (!IsApplying && IsReadyToSave)
        {
            _applyTheme(id);
        }

        ImmediateSave(
            general => general with { Theme = id },
            "Theme saved",
            () =>
            {
                SelectedTheme = oldValue;
                if (oldValue is not null)
                {
                    _applyTheme(oldValue.Id);
                }
            });
    }

    partial void OnSelectedTextSizeChanged(TextSizeOption oldValue, TextSizeOption newValue)
    {
        var id = newValue.Id;
        ImmediateSave(
            general => general with { TextSize = id },
            "Text size saved",
            () => SelectedTextSize = oldValue);
    }

    partial void OnSelectedContentWidthChanged(ContentWidthOption oldValue, ContentWidthOption newValue)
    {
        var id = newValue.Id;
        ImmediateSave(
            general => general with { ContentWidth = id },
            "Content width saved",
            () => SelectedContentWidth = oldValue);
    }

    partial void OnSelectedPageSizeChanged(PageSizeOption? oldValue, PageSizeOption? newValue)
    {
        if (newValue is null)
        {
            return;
        }

        var size = newValue.Size;
        ImmediateSave(
            general => general with { DefaultPageSize = size },
            "Default page size saved",
            () => SelectedPageSize = oldValue);
    }

    partial void OnSelectedChartSessionsChanged(ChartSessionsOption oldValue, ChartSessionsOption newValue)
    {
        if (IsApplying || !IsReadyToSave)
        {
            return;
        }

        var sessions = newValue.Sessions;

        // The graph document has its own single writer (design-lessons rule 2), and the write is
        // queued rather than awaited like every other chart toggle. The roll-back is the brief's
        // requirement for this control, so the writer reports its failure back here rather than
        // only logging it (review minor 1); the callback arrives on the writer's chain thread and
        // is posted, like every other publish.
        _graph.Write(
            graph => graph with { DefaultChartSessions = sessions },
            onFailure: _ => Post(() =>
            {
                if (IsDisposed)
                {
                    return;
                }

                StatusMessage = null;
                ErrorMessage = "The default chart scope could not be saved. See the log for details.";
                Apply(() => SelectedChartSessions = oldValue);
            }));

        StatusMessage = "Default chart sessions saved";
    }

    // ---- the metric group block ---------------------------------------------------------------

    /// <summary>Writes <c>display.groups</c>. The one explicit save on this tab.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveGroups))]
    private async Task SaveGroupsAsync()
    {
        // TRACKING item 13: CanExecute is the affordance, the body is the guard.
        if (IsDisposed || !CanSaveGroups())
        {
            return;
        }

        var groups = DisplayMetricGroupViewModel.ToDocument(Groups);

        // No cancellation token on Task.Run (review minor 9): a dispose between the guard above
        // and the scheduling here would cancel the task before it starts, and AsyncRelayCommand
        // rethrows the resulting TaskCanceledException onto the synchronization context. The
        // lifetime is checked inside the body instead, where a cancellation is just a return.
        await Task.Run(
            () =>
            {
                if (Lifetime.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    // Read inside the queued write, never a snapshot taken when the user clicked,
                    // so a column toggle made since survives this write (design-spec 5.8.2's two
                    // halves are one document).
                    var current = _loadDisplay();
                    var next = current with { Groups = groups };

                    // SaveDisplay raises DisplayChanged synchronously on this thread, so the
                    // tab's own follow has to recognize its own write, exactly as the base class
                    // recognizes its own general write.
                    Volatile.Write(ref _savingDisplayThreadId, Environment.CurrentManagedThreadId);
                    try
                    {
                        _saveDisplay(next);
                    }
                    finally
                    {
                        Volatile.Write(ref _savingDisplayThreadId, 0);
                    }

                    Post(() =>
                    {
                        _display = next;
                        GroupsDirty = false;
                        GroupsChangedElsewhere = false;
                        ErrorMessage = null;
                        StatusMessage = "Metric visibility saved";

                        // The tab's own frame column picker re-gates from what was just written.
                        // Live frame tables re-gate through SettingsStore.DisplayChanged.
                        FramesColumns.ApplyGroupGates(next);
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Saving display.groups failed");
                    Post(() =>
                    {
                        StatusMessage = null;
                        ErrorMessage = "The metric visibility could not be saved. See the log for details.";
                    });
                }
            }).ConfigureAwait(false);
    }

    private bool CanSaveGroups() => GroupsDirty && !DisplayLoadFailed;

    /// <summary>Restores the metric groups to the last read or written document, exactly.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRevertGroups))]
    private void RevertGroups()
    {
        if (!GroupsDirty)
        {
            return;
        }

        ApplyDisplayDocument(_display);
        StatusMessage = null;
        ErrorMessage = null;
    }

    private bool CanRevertGroups() => GroupsDirty;

    // ---- the display document -------------------------------------------------------------

    /// <summary>
    /// Reads <c>display</c> and <c>graph</c> on the base class's own background pass and returns
    /// what to apply (Task 6 review finding 1). There is no second <c>Task.Run</c> and therefore
    /// no second publish: the whole tab lands in one <c>Apply</c>.
    /// </summary>
    protected override Action ReadCompanionDocuments()
    {
        try
        {
            var display = _loadDisplay();
            var graph = _loadGraph();

            // Task 6 review finding 1: read on this same background pass rather than a second
            // Task.Run, so the ledger picker's row set and the rest of the tab land in one publish.
            // A read that throws here falls into the same catch as the display and graph reads,
            // which is also this override's own failure state (task6c-report.md): a catalogue that
            // cannot be read leaves the rest of the tab as unusable as a display document that
            // cannot be read does today, and neither is a state worth a second flag.
            var customColumns = _loadCustomColumns?.Invoke() ?? [];
            return () =>
            {
                _customColumns = customColumns;
                ApplyDisplayDocument(display);
                SelectedChartSessions =
                    ChartSessions.FirstOrDefault(option => option.Sessions == graph.DefaultChartSessions)
                    ?? ChartSessions[0];
            };
        }
        catch (Exception ex)
        {
            // This tab's own failure state, not the general read's: a display document that
            // cannot be read leaves the appearance controls perfectly usable.
            Logger.LogWarning(ex, "Reading the display settings failed");
            return () => DisplayLoadFailed = true;
        }
    }

    // The whole display document: both halves of design-spec 5.8.2. Used by the first read and by
    // RevertGroups, and never by the DisplayChanged follow, which takes the groups half only
    // (Task 6 review finding 3). Publish-thread only, like every other apply.
    private void ApplyDisplayDocument(DisplaySettings display)
    {
        Apply(() =>
        {
            ApplyGroupsHalf(display);

            // Seeded from the writer's last queued list when this process has written one, and
            // from the stored document otherwise, the same order FrameTableViewModel uses.
            FramesColumns.ApplyVisible(
                _columns.LastWritten(DisplaySettings.FramesTableId)
                ?? display.ColumnsFor(DisplaySettings.FramesTableId));

            RebuildLedgerColumns(display);
        });
    }

    // LedgerColumns' rows are the custom columns themselves, unlike FramesColumns' fixed
    // FrameColumns.All, so ApplyVisible alone cannot pick up a column created or deleted since the
    // picker was last built: the whole picker is rebuilt from _customColumns instead, over the same
    // display document, and the old one is disposed so it drops its writer.Changed subscription.
    //
    // Its own method because the definition-change handler below needs exactly this and nothing
    // else. Publish-thread only, and inside an Apply by every caller.
    private void RebuildLedgerColumns(DisplaySettings display)
    {
        var previousLedger = LedgerColumns;
        LedgerColumns = ColumnPickerViewModel.ForLedger(display, _columns, _customColumns);
        previousLedger.Dispose();
    }

    // A column was created, renamed, re-optioned, reordered or deleted while this tab was alive.
    // Until this handler existed the picker was frozen for the life of the process: LedgerColumns is
    // rebuilt only from ApplyDisplayDocument, which runs from the constructor's Load and from
    // RevertGroups, and this tab is a singleton behind a memoized navigation item, so a reader who
    // opened Display before defining a night column read the empty sentence for ever.
    //
    // Only the ledger picker is rebuilt, deliberately, and not the whole display document: the
    // groups half would discard an unsaved metric group edit, which survives navigating away from
    // this tab, and a definition change never touched the display document at all. The same split
    // OnDisplayChanged already makes, from the other side.
    //
    // The rebuild below reads _display, which the first Load publish assigns, so a definition
    // change arriving in the window between the constructor's subscribe and that first publish
    // rebuilds the picker over a default display document. Nothing is lost by it: the first publish
    // then runs ApplyDisplayDocument, which rebuilds the picker again over the real document.
    private void OnCustomColumnsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || _loadCustomColumns is null)
        {
            return;
        }

        var read = _loadCustomColumns;
        var token = Lifetime;

        // Two definition changes in quick succession (two creates, or the reorder arrows twice)
        // start two reads, and the slower one can finish last and leave the picker on the older
        // list. The generation is the guard DashboardViewModel's queries already take through
        // Debouncer: captured at request time, read again on the publish thread, and a response
        // older than the newest request dropped. Interlocked and Volatile because the event is
        // raised on whichever thread wrote, not on the UI thread.
        var generation = Interlocked.Increment(ref _customColumnsGeneration);

        // The catalogue read is off the UI thread, the shape ReadCompanionDocuments already uses,
        // because the event is raised on whichever thread wrote and a reset raises it from the
        // dialog's own. It is not the base class's Load: that would re-read four documents for a
        // change that touched one list, and would publish the general document a second time.
        PendingCustomColumnsRead = Task.Run(
            () =>
            {
                IReadOnlyList<CustomColumnDefinition> columns;
                try
                {
                    columns = read();
                }
                catch (Exception ex)
                {
                    // The tab keeps the rows it has, which is the last list that really was read.
                    Logger.LogWarning(ex, "Re-reading the custom columns for the Nights ledger picker failed");
                    return;
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                Post(() =>
                {
                    if (IsDisposed || generation != Volatile.Read(ref _customColumnsGeneration))
                    {
                        return;
                    }

                    _customColumns = columns;
                    Apply(() => RebuildLedgerColumns(_display));
                });
            },
            token);
    }

    // The groups half alone: the six group rows and the frame picker's metric gate. Nothing here
    // touches a column's IsVisible, which is what keeps one column click from being applied twice
    // (SettingsStore.DisplayChanged's own documentation).
    private void ApplyGroupsHalf(DisplaySettings display)
    {
        _display = display;

        // A change that did not touch the groups half keeps the rows it has. A column click raises
        // DisplayChanged carrying a document whose groups are identical, and rebuilding six rows
        // for it would discard whatever per-row state they carry, which is the second half of
        // review finding 3. Structural, because MetricGroupSettings.Fields is a Dictionary and the
        // record's generated equality compares it by reference.
        if (Groups.Count > 0
            && DisplayMetricGroupViewModel.GroupsEqual(
                DisplayMetricGroupViewModel.ToDocument(Groups),
                display.Groups))
        {
            Apply(() => FramesColumns.ApplyGroupGates(display));
            GroupsDirty = false;
            GroupsChangedElsewhere = false;
            DisplayLoadFailed = false;
            return;
        }

        Apply(() =>
        {
            // The expanded state is presentation, not persisted, so a rebuild that discards it
            // would lose the user's place on a change that did not touch the groups at all. On the
            // first build there is nothing to carry over and each row keeps its own default.
            var hadRows = Groups.Count > 0;
            var expanded = Groups
                .Where(group => group.IsExpanded)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.Ordinal);

            Groups.Clear();
            foreach (var group in DisplayMetricGroupViewModel.Build(display, OnGroupEdited))
            {
                if (hadRows)
                {
                    group.IsExpanded = expanded.Contains(group.Key);
                }

                Groups.Add(group);
            }

            FramesColumns.ApplyGroupGates(display);
        });

        GroupsDirty = false;
        GroupsChangedElsewhere = false;
        DisplayLoadFailed = false;
    }

    private void OnGroupEdited()
    {
        if (IsApplying)
        {
            return;
        }

        StatusMessage = null;
        GroupsDirty = true;
    }

    // FIXER item 7, corrected by Task 6 review finding 3. Raised after every successful
    // SaveDisplay, on whichever thread wrote, so it is posted like every other publish.
    //
    // Only the groups half is applied here. The columns half arrives through
    // DisplayColumnWriter.Changed, which FramesColumns subscribes to, and one column click raises
    // both events: taking ApplyVisible from here as well would apply that click twice and would
    // tear down and rebuild the six group rows on a change that never touched them. That split is
    // what SettingsStore.DisplayChanged's own documentation requires.
    private void OnDisplayChanged(object? sender, DisplaySettings display)
    {
        // This tab's own groups save coming back through the store's event, recognized the way the
        // base class recognizes its own general write. Rebuilding from it would be harmless today
        // and wrong the moment a group row carries state the document does not.
        if (Environment.CurrentManagedThreadId == Volatile.Read(ref _savingDisplayThreadId))
        {
            return;
        }

        Post(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            _display = display;
            if (GroupsDirty)
            {
                GroupsChangedElsewhere = true;
                FramesColumns.ApplyGroupGates(display);
                return;
            }

            ApplyGroupsHalf(display);
        });
    }
}
