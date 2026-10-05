using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Mosaics;

/// <summary>
/// Every collaborator of the Mosaics page (spec 12.17) as a delegate, so the page, its rows and
/// its add panel form construct in a unit test with no database (spec 18.3). Each member defaults
/// to an inert answer, so a test sets only the ones it exercises. <c>AppHost</c> binds every one
/// to <c>MosaicRepository</c>, <c>MosaicQueries</c>, <c>ScanCoordinator</c>,
/// <c>CustomColumnRepository</c>, <c>TargetSearchQuery</c>, <c>SettingsStore</c> and
/// <c>ActivityRepository</c>.
/// </summary>
public sealed record MosaicsBackend
{
    /// <summary>Normally <c>SettingsStore.GetGeneral</c>: the three spec 5.8.1 mosaic keys.</summary>
    public Func<GeneralSettings> General { get; init; } = () => new GeneralSettings();

    /// <summary>Normally <c>SettingsStore.MutateGeneral</c>, which returns what it wrote.</summary>
    public Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> MutateGeneral { get; init; } = mutate => mutate(new GeneralSettings());

    /// <summary>Normally <c>ScanCoordinator.RunMosaicDetectionAsync</c>; null means a scan or another
    /// pass held the lease.</summary>
    public Func<Action<int, int, string>, CancellationToken, Task<MosaicDetectionResult?>> RunDetection { get; init; }
        = (_, _) => Task.FromResult<MosaicDetectionResult?>(null);

    /// <summary>Normally <c>MosaicRepository.ListPending</c>.</summary>
    public Func<IReadOnlyList<MosaicSuggestionRow>> ListPending { get; init; } = () => [];

    /// <summary>Normally <c>MosaicQueries.SuggestionSessions</c>.</summary>
    public Func<MosaicSuggestionRow, IReadOnlyList<SuggestionSessionRow>> SuggestionSessions { get; init; } = _ => [];

    /// <summary>Normally <c>MosaicQueries.TargetNames</c>.</summary>
    public Func<IReadOnlyCollection<Guid>, IReadOnlyDictionary<Guid, string>> TargetNames { get; init; }
        = _ => new Dictionary<Guid, string>();

    /// <summary>Normally <c>MosaicRepository.Accept</c>.</summary>
    public Func<Guid, IReadOnlyList<string>, Guid> Accept { get; init; } = (_, _) => Guid.NewGuid();

    /// <summary>Normally <c>MosaicRepository.Dismiss</c>.</summary>
    public Action<Guid> Dismiss { get; init; } = _ => { };

    /// <summary>Normally <c>MosaicQueries.List</c>.</summary>
    public Func<IReadOnlyList<MosaicListRow>> ListMosaics { get; init; } = () => [];

    /// <summary>Normally <c>MosaicQueries.Detail</c>, read when a row expands.</summary>
    public Func<Guid, MosaicDetail?> Detail { get; init; } = _ => null;

    /// <summary>Normally <c>MosaicRepository.Create</c>.</summary>
    public Func<string, Guid> Create { get; init; } = _ => Guid.NewGuid();

    /// <summary>Normally <c>MosaicRepository.Rename</c>.</summary>
    public Action<Guid, string> Rename { get; init; } = (_, _) => { };

    /// <summary>Normally <c>MosaicRepository.Delete</c>.</summary>
    public Action<Guid> Delete { get; init; } = _ => { };

    /// <summary>Spec 12.17's Remove panel: its included nights leave the mosaic, then the panel
    /// goes. Normally <c>MosaicRepository.RemoveNight</c> per included night, then
    /// <c>DeletePanel</c>.</summary>
    public Action<PanelDetail> RemovePanel { get; init; } = _ => { };

    /// <summary>Normally <c>MosaicRepository.AddPanelWithTarget</c>.</summary>
    public Func<Guid, Guid, string, PanelAddResult> AddPanelWithTarget { get; init; } = (_, _, _) => new PanelAddResult(Guid.NewGuid(), 0, 0);

    /// <summary>Normally <c>TargetSearchQuery.Search</c>.</summary>
    public Func<string, IReadOnlyList<TargetSearchResult>> SearchTargets { get; init; } = _ => [];

    /// <summary>Normally <c>CustomColumnRepository.List</c>.</summary>
    public Func<IReadOnlyList<CustomColumnDefinition>> CustomColumns { get; init; } = () => [];

    /// <summary>Normally <c>CustomColumnRepository.ValuesForMosaics</c>.</summary>
    public Func<IReadOnlyCollection<Guid>, IReadOnlyList<CustomValueRow>> MosaicValues { get; init; } = _ => [];

    /// <summary>Normally <c>CustomColumnRepository.SetValue</c>. Null draws no custom cell.</summary>
    public Func<Guid, CustomValueKey, string?, CustomWriteResult>? WriteValue { get; init; }

    /// <summary>(message, details): one spec 10.9 <c>mosaic_action_failed</c> row, normally
    /// <c>ActivityRepository.EmitStandalone</c> pinned to <c>user_action</c> and warning.</summary>
    public Action<string, object> EmitActionFailed { get; init; } = (_, _) => { };
}

/// <summary>One entry of the campaign gap select (spec 12.17).</summary>
public sealed record CampaignGapChoice(int Days, string Label);

/// <summary>
/// Spec 12.17's Mosaics page (Phase 18): the detection keywords with Run Detection, the
/// suggestions list, and the mosaics table. A rail destination between Dashboard and Statistics
/// (ruling R3).
/// </summary>
/// <remarks>
/// <para>
/// <b>The reload rule (ruling R5)</b> is one subscription: the registry's recent list. Whenever a
/// job of one of the four mosaic kinds finishes, whatever started it (Run Detection here, a scan's
/// detection pass through <see cref="ScanStatusService"/>, or a bulk job on this page), the page
/// reloads both lists. The page therefore never reloads from a second path after its own job.
/// </para>
/// <para>
/// <b>The detail seam (Task 5).</b> A row's click raises <see cref="MosaicOpenRequested"/> with the
/// mosaic id, and a suggestion's target link raises <see cref="TargetOpenRequested"/>. The shell
/// subscribes when it builds the page, the way it routes the Statistics page's events.
/// </para>
/// </remarks>
public sealed partial class MosaicsPageViewModel : ObservableObject, IDisposable
{
    /// <summary>Spec 7.7's detection job kind, shared with the scan path.</summary>
    public const string DetectionJobKind = ScanStatusService.MosaicDetectionJobKind;

    /// <summary>Spec 12.17's bulk accept job (ruling R13).</summary>
    public const string AcceptJobKind = "mosaic_accept";

    /// <summary>Spec 12.17's bulk dismiss job.</summary>
    public const string DismissJobKind = "mosaic_dismiss";

    /// <summary>Spec 12.17's Delete selected job.</summary>
    public const string DeleteJobKind = "mosaic_delete";

    internal const string ScanRunningTooltip = "A scan is running. Detection runs when it ends.";
    internal const string DetectionRunningTooltip = "Detection is running.";
    internal const string DetectionRefusedSummary = "A scan is running. Detection runs when it ends.";
    internal const string DetectionFailedSummary = "Mosaic detection failed. See the log for details.";

    /// <summary>Spec 12.17's empty suggestions sentence.</summary>
    public const string NoSuggestionsText = "No suggestions. Run Detection looks for panels in your target names and sky positions.";

    /// <summary>Spec 12.17's empty table sentence.</summary>
    public const string NoMosaicsText = "No mosaics yet.";

    /// <summary>Spec 12.17's failed load sentence.</summary>
    public const string LoadFailedText = "The mosaics could not be loaded.";

    private static readonly HashSet<string> ReloadKinds =
        new([DetectionJobKind, AcceptJobKind, DismissJobKind, DeleteJobKind], StringComparer.Ordinal);

    private readonly JobRegistry _jobs;
    private readonly DisplayColumnWriter _columnWriter;
    private readonly ScanStatusService? _scanStatus;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<SuggestionRowViewModel> _suggestions = [];
    private IReadOnlyList<CustomColumnDefinition> _definitions = [];
    private bool _disposed;

    /// <param name="backend">Every data collaborator, as delegates.</param>
    /// <param name="jobs">Spec 12's job registry: the page registers its four job kinds and follows
    /// the registry's lists for the detection gate and the reload rule.</param>
    /// <param name="display">The display document, read once for the sort and the column list.</param>
    /// <param name="columnWriter">The one serialized writer of the display document.</param>
    /// <param name="scanStatus">Greys Run Detection while a scan runs.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="delay">The debounce seam the add panel forms' target search waits on.</param>
    /// <param name="logger">A failed load or write is logged, never thrown on the UI thread.</param>
    public MosaicsPageViewModel(
        MosaicsBackend backend,
        JobRegistry jobs,
        DisplaySettings display,
        DisplayColumnWriter columnWriter,
        ScanStatusService? scanStatus = null,
        Action<Action>? post = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? logger = null)
    {
        Backend = backend;
        _jobs = jobs;
        _columnWriter = columnWriter;
        _scanStatus = scanStatus;
        Post = post ?? UiPost.Default;
        Delay = delay ?? Task.Delay;
        _logger = logger ?? NullLogger.Instance;

        var general = backend.General();
        Keywords = new ObservableCollection<string>(general.MosaicKeywords);
        _positionTolerance = (decimal)general.MosaicPositionToleranceArcmin;
        _selectedGap = GapChoices.FirstOrDefault(choice => choice.Days == general.MosaicCampaignGapDays) ?? GapChoices[0];
        NewKeyword = "";
        FilterText = "";
        NewMosaicName = "";

        var sort = display.MosaicsSort;
        SortKey = sort.Key;
        SortAscending = sort.Ascending;
        Picker = ColumnPickerViewModel.ForMosaics(display, columnWriter, []);
        _display = display;
        UpdateSortGlyphs();

        jobs.Running.CollectionChanged += OnRunningChanged;
        jobs.Recent.CollectionChanged += OnRecentChanged;
        columnWriter.Changed += OnColumnsWritten;
        if (scanStatus is not null)
        {
            scanStatus.PropertyChanged += OnScanStatusChanged;
        }

        _ = ReloadAsync();
    }

    private DisplaySettings _display;

    internal MosaicsBackend Backend { get; }

    internal Action<Action> Post { get; }

    internal Func<TimeSpan, CancellationToken, Task> Delay { get; }

    internal ILogger Logger => _logger;

    /// <summary>Raised with a mosaic id when a table row is clicked: the shell opens the mosaic
    /// detail page (Task 5) on its detail overlay.</summary>
    public event EventHandler<Guid>? MosaicOpenRequested;

    /// <summary>Raised with a target id when a suggestion's target link is clicked: the shell
    /// opens that target's detail page.</summary>
    public event EventHandler<Guid>? TargetOpenRequested;

    internal void RequestOpenMosaic(Guid id) => MosaicOpenRequested?.Invoke(this, id);

    internal void RequestOpenTarget(Guid id) => TargetOpenRequested?.Invoke(this, id);

    /// <summary>The last load, so a test awaits it rather than sleeping.</summary>
    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    // ---- detection keywords --------------------------------------------------------------

    /// <summary><c>general.mosaic_keywords</c> in stored order.</summary>
    public ObservableCollection<string> Keywords { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddKeywordCommand))]
    public partial string NewKeyword { get; set; }

    /// <summary>The repeat refusal under the box, or null.</summary>
    [ObservableProperty]
    public partial string? KeywordError { get; private set; }

    /// <summary>"Run Detection to apply.", shown after a change until the next detection ends.</summary>
    [ObservableProperty]
    public partial bool ShowApplyCaption { get; private set; }

    partial void OnNewKeywordChanged(string value) => KeywordError = null;

    private bool CanAddKeyword() => !string.IsNullOrWhiteSpace(NewKeyword);

    [RelayCommand(CanExecute = nameof(CanAddKeyword))]
    private void AddKeyword()
    {
        var keyword = NewKeyword.Trim();
        if (keyword.Length == 0 || _disposed)
        {
            return;
        }

        if (Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
        {
            KeywordError = $"\"{keyword}\" is already a keyword.";
            return;
        }

        if (WriteGeneral(general => general with { MosaicKeywords = [.. general.MosaicKeywords, keyword] }))
        {
            NewKeyword = "";
        }
    }

    [RelayCommand]
    private void RemoveKeyword(string? keyword)
    {
        if (keyword is null || _disposed)
        {
            return;
        }

        WriteGeneral(general => general with
        {
            MosaicKeywords = [.. general.MosaicKeywords.Where(entry => !string.Equals(entry, keyword, StringComparison.OrdinalIgnoreCase))],
        });
    }

    /// <summary>The seven campaign gap entries of spec 12.17.</summary>
    public IReadOnlyList<CampaignGapChoice> GapChoices { get; } =
    [
        new(0, "No grouping"), new(7, "1 week"), new(14, "2 weeks"), new(30, "1 month"),
        new(90, "3 months"), new(180, "6 months"), new(365, "1 year"),
    ];

    private CampaignGapChoice _selectedGap;

    /// <summary>The campaign gap, written to <c>general.mosaic_campaign_gap_days</c> on selection.</summary>
    public CampaignGapChoice SelectedGap
    {
        get => _selectedGap;
        set
        {
            if (value is null || value == _selectedGap)
            {
                return;
            }

            if (WriteGeneral(general => general with { MosaicCampaignGapDays = value.Days }))
            {
                _selectedGap = value;
            }

            OnPropertyChanged();
        }
    }

    private decimal? _positionTolerance;

    /// <summary>Spec 12.17's position tolerance, 0 to 600 arcminutes, written on commit.</summary>
    public decimal? PositionTolerance
    {
        get => _positionTolerance;
        set
        {
            var clamped = Math.Clamp(value ?? 0m, 0m, 600m);
            if (clamped == _positionTolerance)
            {
                return;
            }

            if (WriteGeneral(general => general with { MosaicPositionToleranceArcmin = (double)clamped }))
            {
                _positionTolerance = clamped;
            }

            OnPropertyChanged();
        }
    }

    // The one write path for the three general keys. The keyword chips follow what was written.
    private bool WriteGeneral(Func<GeneralSettings, GeneralSettings> mutate)
    {
        try
        {
            var written = Backend.MutateGeneral(mutate);
            Keywords.Clear();
            foreach (var keyword in written.MosaicKeywords)
            {
                Keywords.Add(keyword);
            }

            ShowApplyCaption = true;
            KeywordError = null;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A mosaic detection setting could not be saved");
            KeywordError = MosaicMessages.CouldNotSave;
            return false;
        }
    }

    // ---- Run Detection -------------------------------------------------------------------

    /// <summary>True while a scan runs (spec 12.17's first disabled state).</summary>
    public bool ScanRunning => _scanStatus?.IsRunning == true;

    /// <summary>True while a <c>mosaic_detection</c> job runs, from this page or a scan.</summary>
    public bool DetectionRunning => _jobs.Running.Any(job => job.Kind == DetectionJobKind);

    /// <summary>Why Run Detection is disabled, or null while it is enabled.</summary>
    public string? RunDetectionTooltip => ScanRunning ? ScanRunningTooltip : DetectionRunning ? DetectionRunningTooltip : null;

    private bool CanRunDetection() => !_disposed && !ScanRunning && !DetectionRunning;

    [RelayCommand(CanExecute = nameof(CanRunDetection))]
    private async Task RunDetectionAsync()
    {
        if (!CanRunDetection())
        {
            return;
        }

        using var job = _jobs.Begin(DetectionJobKind, ScanStatusService.MosaicDetectionJobTitle);
        try
        {
            var result = await Backend.RunDetection(
                    (step, total, message) => job.Report(message, total > 0 ? 100.0 * step / total : null),
                    _lifetime.Token)
                .ConfigureAwait(false);
            if (result is null)
            {
                job.Finish(JobResult.Cancelled, DetectionRefusedSummary);
                return;
            }

            var count = result.SuggestionsWritten;
            job.Finish(JobResult.Succeeded, $"{count} suggestion{(count == 1 ? "" : "s")}");
        }
        catch (OperationCanceledException)
        {
            job.Finish(JobResult.Cancelled, "Mosaic detection was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mosaic detection failed");
            job.Finish(JobResult.Failed, DetectionFailedSummary);
        }
    }

    // ---- suggestions ---------------------------------------------------------------------

    /// <summary>The suggestions the filter keeps, in suggested name order.</summary>
    public ObservableCollection<SuggestionRowViewModel> VisibleSuggestions { get; } = [];

    /// <summary>Spec 12.17's filter text. Not stored; survives a reload.</summary>
    [ObservableProperty]
    public partial string FilterText { get; set; }

    partial void OnFilterTextChanged(string value) => RefreshVisibleSuggestions();

    /// <summary>The filter box shows only while there are more than four suggestions.</summary>
    public bool ShowFilter => _suggestions.Count > 4;

    /// <summary>"Suggestions (n)", or "Suggestions (m of n)" while the filter narrows the list.</summary>
    public string SuggestionsHeading => VisibleSuggestions.Count == _suggestions.Count
        ? $"Suggestions ({_suggestions.Count})"
        : $"Suggestions ({VisibleSuggestions.Count} of {_suggestions.Count})";

    public bool HasNoSuggestions => _suggestions.Count == 0;

    public bool HasVisibleSuggestions => VisibleSuggestions.Count > 0;

    private IReadOnlyList<SuggestionRowViewModel> CheckedVisible => [.. VisibleSuggestions.Where(row => row.IsSelected)];

    // The checked visible rows, or every visible row when none is checked (spec 12.17).
    private IReadOnlyList<SuggestionRowViewModel> BulkTargets
        => CheckedVisible is { Count: > 0 } chosen ? chosen : [.. VisibleSuggestions];

    /// <summary>The "Select all" box over the visible rows.</summary>
    public bool AllSuggestionsSelected
    {
        get => VisibleSuggestions.Count > 0 && VisibleSuggestions.All(row => row.IsSelected);
        set
        {
            foreach (var row in VisibleSuggestions)
            {
                row.IsSelected = value;
            }
        }
    }

    public string AcceptAllText => CheckedVisible.Count is var k and > 0 ? $"Accept ({k})" : $"Accept all ({VisibleSuggestions.Count})";

    public string DismissAllText => CheckedVisible.Count is var k and > 0 ? $"Dismiss ({k})" : $"Dismiss all ({VisibleSuggestions.Count})";

    /// <summary>True between the first and second press of Dismiss all.</summary>
    [ObservableProperty]
    public partial bool DismissAllPending { get; private set; }

    public string DismissAllConfirmText
        => $"Dismiss {BulkTargets.Count} suggestions? Each comes back only if new nights of its panels are catalogued.";

    /// <summary>True while one of this page's bulk jobs runs. Every Accept, Dismiss and Delete on
    /// the page is disabled meanwhile (spec 12.17).</summary>
    [ObservableProperty]
    public partial bool BulkRunning { get; private set; }

    partial void OnBulkRunningChanged(bool value) => NotifyActionGates();

    internal bool CanAct => !_disposed && !BulkRunning;

    private bool CanBulkSuggestions() => CanAct && VisibleSuggestions.Count > 0;

    [RelayCommand(CanExecute = nameof(CanBulkSuggestions))]
    private Task AcceptAllAsync()
    {
        if (!CanBulkSuggestions())
        {
            return Task.CompletedTask;
        }

        DismissAllPending = false;
        var items = BulkTargets.Select(row => (row.Row.Id, row.Name, Labels: row.CheckedLabels)).ToList();
        return RunBulkAsync(
            AcceptJobKind, "Accept suggestions", "accept", "Accepted", items,
            item => item.Name, item => Backend.Accept(item.Id, item.Labels));
    }

    [RelayCommand(CanExecute = nameof(CanBulkSuggestions))]
    private Task DismissAllAsync()
    {
        if (!CanBulkSuggestions())
        {
            return Task.CompletedTask;
        }

        if (!DismissAllPending)
        {
            DismissAllPending = true;
            OnPropertyChanged(nameof(DismissAllConfirmText));
            return Task.CompletedTask;
        }

        DismissAllPending = false;
        var items = BulkTargets.Select(row => (row.Row.Id, row.Name)).ToList();
        return RunBulkAsync(
            DismissJobKind, "Dismiss suggestions", "dismiss", "Dismissed", items,
            item => item.Name, item => Backend.Dismiss(item.Id));
    }

    [RelayCommand]
    private void CancelDismissAll() => DismissAllPending = false;

    // Spec 12.17's bulk rule: one job, "k of n" progress, one mosaic_action_failed row per failed
    // item, the summary "<verb> k of n", failed only when every item failed. The reload is the
    // registry subscription's, like every other mosaic job's.
    private async Task RunBulkAsync<T>(
        string kind, string title, string action, string verb, IReadOnlyList<T> items,
        Func<T, string> name, Action<T> act)
    {
        BulkRunning = true;
        using var job = _jobs.Begin(kind, title);
        var done = 0;
        try
        {
            done = await Task.Run(
                () =>
                {
                    var succeeded = 0;
                    for (var index = 0; index < items.Count; index++)
                    {
                        job.Report($"{index + 1} of {items.Count}", 100.0 * index / items.Count);
                        try
                        {
                            act(items[index]);
                            succeeded++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "A bulk mosaic {Action} item failed", action);
                            EmitFailure(action, name(items[index]), ex.Message);
                        }
                    }

                    return succeeded;
                }).ConfigureAwait(false);
        }
        finally
        {
            job.Finish(
                done == 0 && items.Count > 0 ? JobResult.Failed : JobResult.Succeeded,
                $"{verb} {done} of {items.Count}");
            Post(() => BulkRunning = false);
        }
    }

    // Guarded: a row the activity log could not take must not stop the job.
    private void EmitFailure(string action, string name, string reason)
    {
        try
        {
            Backend.EmitActionFailed($"Could not {action} {name}: {reason}", new { action, name, reason });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The mosaic_action_failed event could not be written");
        }
    }

    /// <summary>Removes one accepted or dismissed row without resetting the others (spec 12.17),
    /// and reloads the mosaics table.</summary>
    internal void RemoveSuggestion(SuggestionRowViewModel row, bool reloadMosaics)
    {
        row.PropertyChanged -= OnSuggestionChanged;
        _suggestions.Remove(row);
        RefreshVisibleSuggestions();
        if (reloadMosaics)
        {
            _ = ReloadAsync(suggestions: false);
        }
    }

    private void RefreshVisibleSuggestions()
    {
        VisibleSuggestions.Clear();
        var filter = FilterText.Trim();
        foreach (var row in _suggestions.Where(row => filter.Length == 0 || row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            VisibleSuggestions.Add(row);
        }

        OnPropertyChanged(nameof(ShowFilter));
        OnPropertyChanged(nameof(SuggestionsHeading));
        OnPropertyChanged(nameof(HasNoSuggestions));
        OnPropertyChanged(nameof(HasVisibleSuggestions));
        OnSuggestionSelectionChanged();
    }

    private void OnSuggestionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SuggestionRowViewModel.IsSelected))
        {
            OnSuggestionSelectionChanged();
        }
    }

    private void OnSuggestionSelectionChanged()
    {
        OnPropertyChanged(nameof(AllSuggestionsSelected));
        OnPropertyChanged(nameof(AcceptAllText));
        OnPropertyChanged(nameof(DismissAllText));
        OnPropertyChanged(nameof(DismissAllConfirmText));
        AcceptAllCommand.NotifyCanExecuteChanged();
        DismissAllCommand.NotifyCanExecuteChanged();
    }

    // ---- the mosaics table -----------------------------------------------------------------

    /// <summary>The rows, in the current sort.</summary>
    public ObservableCollection<MosaicRowViewModel> Mosaics { get; } = [];

    /// <summary>The column gear's picker over <c>display.columns.mosaics</c>; its rows also carry
    /// the header cells' visibility and sort glyph.</summary>
    [ObservableProperty]
    public partial ColumnPickerViewModel Picker { get; private set; }

    /// <summary>The shown mosaic-scope custom columns, in display order: the header strip.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CustomColumnDefinition> ShownCustomColumns { get; private set; } = [];

    public string MosaicsHeading => $"Mosaics ({Mosaics.Count})";

    public bool HasNoMosaics => Mosaics.Count == 0 && !LoadFailed;

    public bool ShowSelectAllMosaics => Mosaics.Count >= 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMosaics))]
    public partial bool LoadFailed { get; private set; }

    [ObservableProperty]
    public partial string SortKey { get; private set; }

    [ObservableProperty]
    public partial bool SortAscending { get; private set; }

    /// <summary>A header click: ascending on a new key, reversed on the same key, written to
    /// <c>display.sort.mosaics</c>. A key outside the five built-ins does not sort.</summary>
    [RelayCommand]
    private void SortBy(string? key)
    {
        if (key is null || !DisplaySettings.MosaicColumnKeys.Contains(key) || _disposed)
        {
            return;
        }

        SortAscending = key != SortKey || !SortAscending;
        SortKey = key;
        UpdateSortGlyphs();
        ApplySort();

        var sort = new TableSort { Key = SortKey, Ascending = SortAscending };
        _display = _display.WithSort(DisplaySettings.MosaicsTableId, sort);
        _columnWriter.Write(display => display.WithSort(DisplaySettings.MosaicsTableId, sort));
    }

    private void UpdateSortGlyphs()
    {
        foreach (var column in Picker.Columns)
        {
            column.SortGlyph = column.Key == SortKey ? (SortAscending ? "▲" : "▼") : "";
        }
    }

    private void ApplySort()
    {
        var sorted = Sorted(Mosaics, SortKey, SortAscending);
        for (var index = 0; index < sorted.Count; index++)
        {
            var current = Mosaics.IndexOf(sorted[index]);
            if (current != index)
            {
                Mosaics.Move(current, index);
            }
        }
    }

    /// <summary>Spec 12.17's order: the key ascending or descending, ties by name; Date range by
    /// the first night, a mosaic with none first ascending.</summary>
    internal static IReadOnlyList<MosaicRowViewModel> Sorted(IEnumerable<MosaicRowViewModel> rows, string key, bool ascending)
    {
        Comparison<MosaicRowViewModel> byKey = key switch
        {
            "panels" => (a, b) => a.Panels.CompareTo(b.Panels),
            "integration" => (a, b) => a.IntegrationSeconds.CompareTo(b.IntegrationSeconds),
            "frames" => (a, b) => a.Frames.CompareTo(b.Frames),
            "date_range" => (a, b) => Nullable.Compare(a.FirstNight, b.FirstNight),
            _ => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
        };

        var list = rows.ToList();
        list.Sort((a, b) =>
        {
            var order = byKey(a, b);
            if (!ascending)
            {
                order = -order;
            }

            return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        return list;
    }

    // Selection and Delete selected.

    public int SelectedMosaicCount => Mosaics.Count(row => row.IsSelected);

    public bool HasSelectedMosaics => SelectedMosaicCount > 0;

    public string DeleteSelectedText => $"Delete selected ({SelectedMosaicCount})";

    public string DeleteSelectedConfirmText
        => $"Delete {SelectedMosaicCount} mosaics? Their panels and nights are removed; no frame is touched.";

    public bool AllMosaicsSelected
    {
        get => Mosaics.Count > 0 && Mosaics.All(row => row.IsSelected);
        set
        {
            foreach (var row in Mosaics)
            {
                row.IsSelected = value;
            }
        }
    }

    [ObservableProperty]
    public partial bool DeleteSelectedPending { get; private set; }

    private bool CanDeleteSelected() => CanAct && HasSelectedMosaics;

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private Task DeleteSelectedAsync()
    {
        if (!CanDeleteSelected())
        {
            return Task.CompletedTask;
        }

        if (!DeleteSelectedPending)
        {
            DeleteSelectedPending = true;
            return Task.CompletedTask;
        }

        DeleteSelectedPending = false;
        var items = Mosaics.Where(row => row.IsSelected).Select(row => (row.Id, row.Name)).ToList();
        return RunBulkAsync(
            DeleteJobKind, "Delete mosaics", "delete", "Deleted", items,
            item => item.Name, item => Backend.Delete(item.Id));
    }

    [RelayCommand]
    private void CancelDeleteSelected() => DeleteSelectedPending = false;

    internal void OnMosaicSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedMosaicCount));
        OnPropertyChanged(nameof(HasSelectedMosaics));
        OnPropertyChanged(nameof(DeleteSelectedText));
        OnPropertyChanged(nameof(DeleteSelectedConfirmText));
        OnPropertyChanged(nameof(AllMosaicsSelected));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        if (!HasSelectedMosaics)
        {
            DeleteSelectedPending = false;
        }
    }

    /// <summary>A single Delete removed this row (spec 12.17).</summary>
    internal void RemoveMosaicRow(MosaicRowViewModel row)
    {
        Mosaics.Remove(row);
        row.Dispose();
        OnMosaicsChanged();
    }

    /// <summary>A rename moved this row's name; the sort follows it.</summary>
    internal void OnMosaicRenamed() => ApplySort();

    // Create mosaic.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreateButtonText))]
    public partial bool IsCreateOpen { get; private set; }

    public string CreateButtonText => IsCreateOpen ? "Cancel" : "Create mosaic";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string NewMosaicName { get; set; }

    [ObservableProperty]
    public partial string? CreateError { get; private set; }

    partial void OnNewMosaicNameChanged(string value) => CreateError = null;

    [RelayCommand]
    private void ToggleCreate()
    {
        IsCreateOpen = !IsCreateOpen;
        NewMosaicName = "";
        CreateError = null;
    }

    private bool CanCreate() => !string.IsNullOrWhiteSpace(NewMosaicName);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        if (!CanCreate() || _disposed)
        {
            return;
        }

        var name = NewMosaicName.Trim();
        CreateError = TryWrite(() => Backend.Create(name));
        if (CreateError is null)
        {
            IsCreateOpen = false;
            NewMosaicName = "";
            _ = ReloadAsync(suggestions: false);
        }
    }

    /// <summary>Runs one write. Null on success, otherwise the inline sentence: a refusal's own,
    /// or "The change could not be saved. Try again." for a write that threw.</summary>
    internal string? TryWrite(Action write)
    {
        try
        {
            write();
            return null;
        }
        catch (MosaicWriteException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A mosaic write failed");
            return MosaicMessages.CouldNotSave;
        }
    }

    [RelayCommand]
    private Task Retry() => ReloadAsync();

    // ---- loading -------------------------------------------------------------------------

    /// <summary>Reads the suggestions (unless <paramref name="suggestions"/> is false) and the
    /// mosaics table off the UI thread, then replaces both lists. Expanded rows, checked panels and
    /// selections reset (spec 12.17's reload rule); the filter text survives.</summary>
    public Task ReloadAsync(bool suggestions = true)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        return PendingLoad = LoadAsync(suggestions);
    }

    private sealed record Loaded(
        IReadOnlyList<(MosaicSuggestionRow Row, IReadOnlyList<SuggestionSessionRow> Sessions)>? Suggestions,
        IReadOnlyDictionary<Guid, string> TargetNames,
        IReadOnlyList<MosaicListRow>? Mosaics,
        IReadOnlyList<CustomColumnDefinition> Definitions,
        IReadOnlyList<CustomValueRow> Values);

    private async Task LoadAsync(bool suggestions)
    {
        Loaded loaded;
        try
        {
            loaded = await Task.Run(() => Read(suggestions)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The Mosaics page could not be loaded");
            Post(() => LoadFailed = true);
            return;
        }

        Post(() => Apply(loaded));
    }

    private Loaded Read(bool suggestions)
    {
        IReadOnlyList<(MosaicSuggestionRow, IReadOnlyList<SuggestionSessionRow>)>? pending = null;
        IReadOnlyDictionary<Guid, string> names = new Dictionary<Guid, string>();
        if (suggestions)
        {
            pending = [.. Backend.ListPending().Select(row => (row, Backend.SuggestionSessions(row)))];
            var ids = pending.SelectMany(entry => entry.Item1.Panels.Select(panel => panel.TargetId)).ToHashSet();
            names = ids.Count == 0 ? names : Backend.TargetNames(ids);
        }

        var mosaics = Backend.ListMosaics();
        var definitions = Backend.CustomColumns();
        var values = mosaics.Count == 0 ? [] : Backend.MosaicValues([.. mosaics.Select(row => row.Id)]);
        return new Loaded(pending, names, mosaics, definitions, values);
    }

    private void Apply(Loaded loaded)
    {
        if (_disposed)
        {
            return;
        }

        LoadFailed = false;
        if (loaded.Suggestions is { } pending)
        {
            foreach (var row in _suggestions)
            {
                row.PropertyChanged -= OnSuggestionChanged;
            }

            _suggestions.Clear();
            DismissAllPending = false;
            foreach (var (row, sessions) in pending)
            {
                var added = new SuggestionRowViewModel(row, sessions, loaded.TargetNames, this);
                added.PropertyChanged += OnSuggestionChanged;
                _suggestions.Add(added);
            }

            RefreshVisibleSuggestions();
        }

        ApplyDefinitions(loaded.Definitions);

        foreach (var row in Mosaics)
        {
            row.Dispose();
        }

        Mosaics.Clear();
        DeleteSelectedPending = false;
        var values = loaded.Values
            .Where(value => value.Key.MosaicId is not null)
            .ToLookup(value => value.Key.MosaicId!.Value);
        foreach (var row in Sorted(loaded.Mosaics!.Select(row => new MosaicRowViewModel(row, this, values[row.Id].ToList())), SortKey, SortAscending))
        {
            Mosaics.Add(row);
        }

        OnMosaicsChanged();
    }

    private void OnMosaicsChanged()
    {
        OnPropertyChanged(nameof(MosaicsHeading));
        OnPropertyChanged(nameof(HasNoMosaics));
        OnPropertyChanged(nameof(ShowSelectAllMosaics));
        OnMosaicSelectionChanged();
    }

    // The picker is rebuilt when the mosaic-scope definitions changed, so a column created on the
    // Custom columns tab reaches the gear on the next reload.
    private void ApplyDefinitions(IReadOnlyList<CustomColumnDefinition> definitions)
    {
        var mosaicScope = CustomColumnSet.MosaicRow(definitions);
        if (!mosaicScope.Select(column => (column.Id, column.Name)).SequenceEqual(
                CustomColumnSet.MosaicRow(_definitions).Select(column => (column.Id, column.Name))))
        {
            Picker.Dispose();
            Picker = ColumnPickerViewModel.ForMosaics(_display, _columnWriter, definitions);
            UpdateSortGlyphs();
        }

        _definitions = definitions;
        RefreshShownCustomColumns();
    }

    private void RefreshShownCustomColumns()
    {
        var visible = Picker.Columns.Where(column => column.IsVisible).Select(column => column.Key).ToHashSet(StringComparer.Ordinal);
        ShownCustomColumns = [.. CustomColumnSet.MosaicRow(_definitions).Where(column => visible.Contains(column.Slug))];
    }

    // A picker toggle (here or another surface) was queued for this table: the custom cells follow.
    private void OnColumnsWritten(string tableId, string[] keys)
    {
        if (_disposed || tableId != DisplaySettings.MosaicsTableId)
        {
            return;
        }

        _display = _display with
        {
            Columns = new Dictionary<string, string[]>(_display.Columns) { [tableId] = keys },
        };
        RefreshShownCustomColumns();
        foreach (var row in Mosaics)
        {
            row.ReconcileCustomCells();
        }
    }

    // ---- gates and subscriptions -------------------------------------------------------------

    private void NotifyActionGates()
    {
        AcceptAllCommand.NotifyCanExecuteChanged();
        DismissAllCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        foreach (var row in _suggestions)
        {
            row.NotifyGates();
        }

        foreach (var row in Mosaics)
        {
            row.NotifyGates();
        }
    }

    private void NotifyDetectionGate()
    {
        OnPropertyChanged(nameof(ScanRunning));
        OnPropertyChanged(nameof(DetectionRunning));
        OnPropertyChanged(nameof(RunDetectionTooltip));
        RunDetectionCommand.NotifyCanExecuteChanged();
    }

    private void OnRunningChanged(object? sender, NotifyCollectionChangedEventArgs e) => NotifyDetectionGate();

    // Spec 12.17's reload rule (ruling R5): any mosaic job's end, whatever started it.
    private void OnRecentChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed || e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
        {
            return;
        }

        var kinds = e.NewItems.OfType<JobViewModel>().Select(job => job.Kind).ToList();
        if (kinds.Contains(DetectionJobKind))
        {
            ShowApplyCaption = false;
        }

        if (kinds.Any(ReloadKinds.Contains))
        {
            _ = ReloadAsync();
        }
    }

    private void OnScanStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(ScanStatusService.IsRunning))
        {
            NotifyDetectionGate();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _jobs.Running.CollectionChanged -= OnRunningChanged;
        _jobs.Recent.CollectionChanged -= OnRecentChanged;
        _columnWriter.Changed -= OnColumnsWritten;
        if (_scanStatus is not null)
        {
            _scanStatus.PropertyChanged -= OnScanStatusChanged;
        }

        Picker.Dispose();
        foreach (var row in Mosaics)
        {
            row.Dispose();
        }

        _lifetime.Dispose();
    }

    internal static string Plural(int count, string noun)
        => count.ToString("N0", CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? "" : "s");
}
