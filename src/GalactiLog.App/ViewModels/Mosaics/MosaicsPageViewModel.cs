using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Mosaics;

/// <summary>
/// Spec 12.17's Mosaics page (Phase 18): the detection keywords with Run Detection and the
/// suggestions list, with the mosaics table in <see cref="Table"/>. A rail destination between
/// Dashboard and Statistics (ruling R3).
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
    internal const string DetectionRefusedByScanSummary = "A scan is running. Detection runs when it ends.";
    internal const string DetectionRefusedByOtherSummary = "Another catalogue task is running. Detection runs when it ends.";
    internal const string DetectionFailedSummary = "Mosaic detection failed. See the log for details.";

    /// <summary>Spec 12.17's empty suggestions sentence.</summary>
    public const string NoSuggestionsText = "No suggestions. Run Detection looks for panels in your target names and sky positions.";

    /// <summary>What a failed suggestions read shows in place of the list.</summary>
    public const string SuggestionsLoadFailedText = "The suggestions could not be loaded.";

    private static readonly HashSet<string> ReloadKinds =
        new([DetectionJobKind, AcceptJobKind, DismissJobKind, DeleteJobKind], StringComparer.Ordinal);

    private readonly JobRegistry _jobs;
    private readonly ScanStatusService? _scanStatus;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<SuggestionRowViewModel> _suggestions = [];
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
        Table = new MosaicsTableViewModel(this, display, columnWriter);

        jobs.Running.CollectionChanged += OnRunningChanged;
        jobs.Recent.CollectionChanged += OnRecentChanged;
        if (scanStatus is not null)
        {
            scanStatus.PropertyChanged += OnScanStatusChanged;
        }

        _ = ReloadAsync();
    }

    internal MosaicsBackend Backend { get; }

    internal Action<Action> Post { get; }

    internal Func<TimeSpan, CancellationToken, Task> Delay { get; }

    internal ILogger Logger => _logger;

    /// <summary>The right column: the mosaics table.</summary>
    public MosaicsTableViewModel Table { get; }

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
    public IReadOnlyList<CampaignGapChoice> GapChoices => CampaignGapChoice.All;

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
                // The lease was held: by a scan, or by another catalogue task such as a rebuild.
                job.Finish(
                    JobResult.Cancelled,
                    ScanRunning ? DetectionRefusedByScanSummary : DetectionRefusedByOtherSummary);
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

    /// <summary>Spec 12.17's filter text. Not stored; survives a reload. It narrows the list only
    /// while its box is shown.</summary>
    [ObservableProperty]
    public partial string FilterText { get; set; }

    partial void OnFilterTextChanged(string value) => RefreshVisibleSuggestions();

    /// <summary>The filter box shows only while there are more than four suggestions.</summary>
    public bool ShowFilter => _suggestions.Count > 4;

    /// <summary>"Suggestions (n)", or "Suggestions (m of n)" while the filter narrows the list.</summary>
    public string SuggestionsHeading => VisibleSuggestions.Count == _suggestions.Count
        ? $"Suggestions ({_suggestions.Count})"
        : $"Suggestions ({VisibleSuggestions.Count} of {_suggestions.Count})";

    /// <summary>True when the last suggestions read threw: the failure sentence shows instead of
    /// the list and instead of the empty sentence.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSuggestions))]
    public partial bool SuggestionsLoadFailed { get; private set; }

    public bool HasNoSuggestions => _suggestions.Count == 0 && !SuggestionsLoadFailed;

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

    /// <summary>Spec 12.17's bulk rule: one job, "k of n" progress, one mosaic_action_failed row
    /// per failed item, the summary "&lt;verb&gt; k of n", failed only when every item failed. The
    /// reload is the registry subscription's, like every other mosaic job's.</summary>
    internal async Task RunBulkAsync<T>(
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

        // The filter narrows only while its box is shown: once the list falls to four or fewer the
        // box hides, and a narrowing nobody can see or clear would hide rows.
        var filter = ShowFilter ? FilterText.Trim() : "";
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
    private Task RetrySuggestions() => ReloadAsync();

    // ---- loading -------------------------------------------------------------------------

    /// <summary>Reads the suggestions (unless <paramref name="suggestions"/> is false) and the
    /// mosaics table off the UI thread, then replaces both lists. Expanded rows, checked panels and
    /// selections reset (spec 12.17's reload rule); the filter text survives. The two reads fail
    /// independently: each list shows its own failure sentence.</summary>
    public Task ReloadAsync(bool suggestions = true)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        return PendingLoad = LoadAsync(suggestions);
    }

    private sealed record LoadedSuggestions(
        IReadOnlyList<(MosaicSuggestionRow Row, IReadOnlyList<SuggestionSessionRow> Sessions)> Rows,
        IReadOnlyDictionary<Guid, string> TargetNames);

    private async Task LoadAsync(bool suggestions)
    {
        var (pending, table) = await Task.Run(() => (suggestions ? ReadSuggestions() : null, Table.Read())).ConfigureAwait(false);
        Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (suggestions)
            {
                ApplySuggestions(pending);
            }

            Table.Apply(table);
        });
    }

    // Null when the read threw.
    private LoadedSuggestions? ReadSuggestions()
    {
        try
        {
            var rows = Backend.ListPending().Select(row => (row, Backend.SuggestionSessions(row))).ToList();
            var ids = rows.SelectMany(entry => entry.row.Panels.Select(panel => panel.TargetId)).ToHashSet();
            IReadOnlyDictionary<Guid, string> names = ids.Count == 0 ? new Dictionary<Guid, string>() : Backend.TargetNames(ids);
            return new LoadedSuggestions([.. rows], names);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The mosaic suggestions could not be loaded");
            return null;
        }
    }

    private void ApplySuggestions(LoadedSuggestions? loaded)
    {
        foreach (var row in _suggestions)
        {
            row.PropertyChanged -= OnSuggestionChanged;
        }

        _suggestions.Clear();
        DismissAllPending = false;
        SuggestionsLoadFailed = loaded is null;
        foreach (var (row, sessions) in loaded?.Rows ?? [])
        {
            var added = new SuggestionRowViewModel(row, sessions, loaded!.TargetNames, this);
            added.PropertyChanged += OnSuggestionChanged;
            _suggestions.Add(added);
        }

        RefreshVisibleSuggestions();
    }

    // ---- gates and subscriptions -------------------------------------------------------------

    private void NotifyActionGates()
    {
        AcceptAllCommand.NotifyCanExecuteChanged();
        DismissAllCommand.NotifyCanExecuteChanged();
        foreach (var row in _suggestions)
        {
            row.NotifyGates();
        }

        Table.NotifyGates();
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
        if (_scanStatus is not null)
        {
            _scanStatus.PropertyChanged -= OnScanStatusChanged;
        }

        Table.Dispose();
        _lifetime.Dispose();
    }

    internal static string Plural(int count, string noun)
        => count.ToString("N0", CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? "" : "s");
}
