using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Io;
using GalactiLog.Core.Mosaics;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Mosaics;

/// <summary>
/// Spec 12.17's mosaic detail page (Phase 18), hosted on the shell's detail overlay beside the
/// Target detail page (spec 12 shell): the header with inline rename, the mosaic-scope custom
/// cells, Composite, the overflow menu's Export panels (CSV) and Delete mosaic; the summary line;
/// the notes; the available labels banner; and the sessions region, one
/// <see cref="PanelViewModel"/> per panel.
/// </summary>
/// <remarks>
/// <para>
/// Every figure comes from one read, <c>MosaicQueries.Detail</c>, made off the UI thread by
/// <see cref="ReloadAsync"/>. Every include, remove, add and delete re-reads it, so the summary
/// line, the panel figures, the deficits and the available counts follow at once; so does the end
/// of a scan or a detection job, which can add frames to a night a panel already holds.
/// </para>
/// <para>
/// The page raises <see cref="BackRequested"/> to close and <see cref="OpenTargetRequested"/> to
/// open a target, which closes this page (spec 12 shell). <c>MainWindowViewModel.OpenMosaic</c>
/// builds it; the Mosaics table row, the dashboard's mosaic link and the Create mosaic dialog all
/// reach it there.
/// </para>
/// <para>
/// This file is the one <c>AppWriter.BeginExport</c> caller of Phase 18 (spec 2.1.1), allowlisted
/// by full path in <c>FileSafetyTest</c>: Export panels writes one new file at the path the save
/// dialog returned and nothing else.
/// </para>
/// </remarks>
public sealed partial class MosaicDetailViewModel : ObservableObject, IDisposable
{
    /// <summary>Composite's tooltip while the arranger has no filter (spec 12.17).</summary>
    public const string NoFramesTooltip = "No frames to composite";

    /// <summary>Composite's tooltip when no panel's best frame in the filter carries a plate scale.</summary>
    public const string NoPlateScaleTooltip = "No panel carries a plate scale";

    /// <summary>Delete mosaic's confirm sentence.</summary>
    public const string DeleteConfirmText = MosaicRowViewModel.DeleteConfirmText;

    /// <summary>What a failed Export panels write shows under the header.</summary>
    public const string ExportFailedText = "The file could not be written.";

    /// <summary>What a failed read shows under the header.</summary>
    public const string LoadFailedText = "The mosaic could not be loaded.";

    /// <summary>What a read that found no mosaic shows in place of the page: a history Back can
    /// reopen a mosaic deleted since (mouse-navigation decision 4).</summary>
    public const string NotFoundText = "This mosaic no longer exists; it may have been deleted since this page was opened.";

    /// <summary>The CSV's header line (spec 12.17).</summary>
    public const string CsvHeader = "panel_label,targets,frames,integration_seconds,filters";

    // Spec 12.17: "the page also re-reads when a scan or a detection job ends".
    private static readonly HashSet<string> RereadKinds =
        new([ScanStatusService.ScanJobKind, ScanStatusService.MosaicDetectionJobKind], StringComparer.Ordinal);

    private readonly AppWriter _appWriter;
    private readonly JobRegistry? _jobs;
    private readonly CompositeService _composite;
    private readonly Func<CompositeLightboxViewModel, Task> _openComposite;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<Guid, PanelViewModel> _panelsById = [];
    private CustomCellGroup _cells = CustomCellGroup.Empty;
    private MosaicDetail? _detail;
    private int _generation;
    private bool _disposed;

    // Set by a confirmed Delete mosaic: closing then flushes nothing into the deleted mosaic.
    private bool _deleted;

    /// <param name="mosaicId">The mosaic this page shows.</param>
    /// <param name="backend">Every data collaborator, as delegates; the Mosaics page's own record.</param>
    /// <param name="appWriter">The application's one <c>AppWriter</c>, for Export panels.</param>
    /// <param name="jobs">The job registry, followed for the end of a scan or a detection job.
    /// Null in a test that does not exercise it.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="delay">The debounce seam the notes, the custom cells and the target searches
    /// wait on.</param>
    /// <param name="logger">A failed read or write is logged, never thrown on the UI thread.</param>
    /// <param name="composite">Spec 11.6's cache and build, for Composite's enablement and the
    /// lightbox. Null in a test that does not exercise it: a private service, enough for the
    /// enablement, whose drawing reads no file and ends as a cancel.</param>
    /// <param name="openComposite">Shows the composite lightbox and completes when it closes,
    /// disposing it. Null in a test: the lightbox is disposed at once.</param>
    public MosaicDetailViewModel(
        Guid mosaicId,
        MosaicsBackend backend,
        AppWriter appWriter,
        JobRegistry? jobs = null,
        Action<Action>? post = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? logger = null,
        CompositeService? composite = null,
        Func<CompositeLightboxViewModel, Task>? openComposite = null)
    {
        Id = mosaicId;
        Backend = backend;
        _appWriter = appWriter;
        _jobs = jobs;
        // Inert: the default drawing touches no file and ends as a cancel.
        _composite = composite ?? new CompositeService(
            new JobRegistry(action => action()), (_, _, _, _) => { }, (_, _, _, _) => throw new OperationCanceledException());
        _openComposite = openComposite ?? (lightbox =>
        {
            lightbox.Dispose();
            return Task.CompletedTask;
        });
        Post = post ?? UiPost.Default;
        Delay = delay ?? Task.Delay;
        Logger = logger ?? NullLogger.Instance;
        Name = "";
        RenameText = "";
        SummaryText = "";
        Notes = new AutosaveField(notes => backend.SetNotes(mosaicId, notes), Delay, Post, Logger);
        AddPanel = new AddPanelViewModel(
            mosaicId, [], backend.SearchTargets, backend.AddPanelWithTarget, () => _ = ReloadAsync(),
            Delay, Post, Logger);
        Arranger = new ArrangerViewModel(mosaicId, backend, readOnly: false, Post, Delay, Logger);

        // The arranger raises SelectedFilter on every frame set it takes and every filter change.
        Arranger.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ArrangerViewModel.SelectedFilter))
            {
                RefreshComposite();
            }
        };

        if (jobs is not null)
        {
            jobs.Recent.CollectionChanged += OnRecentChanged;
        }

        _ = ReloadAsync();
    }

    /// <summary>The mosaic's id.</summary>
    public Guid Id { get; }

    internal MosaicsBackend Backend { get; }

    internal Action<Action> Post { get; }

    internal Func<TimeSpan, CancellationToken, Task> Delay { get; }

    internal ILogger Logger { get; }

    /// <summary>Raised by Back and by a confirmed Delete mosaic: the shell closes the overlay.</summary>
    public event EventHandler? BackRequested;

    /// <summary>Raised with a target id by a night row's target link: the shell opens that target's
    /// page, which closes this one.</summary>
    public event EventHandler<Guid>? OpenTargetRequested;

    internal void RequestOpenTarget(Guid targetId) => OpenTargetRequested?.Invoke(this, targetId);

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The last read, so a test awaits it rather than sleeping.</summary>
    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <summary>True when the last read threw. The page keeps what it showed and offers Retry.</summary>
    [ObservableProperty]
    public partial bool LoadFailed { get; private set; }

    /// <summary>True when the last read found no mosaic for <see cref="Id"/>: the view shows
    /// <see cref="NotFoundText"/> and Back, and hides the arranger, the notes and the sessions
    /// region, never a blank page.</summary>
    [ObservableProperty]
    public partial bool IsNotFound { get; private set; }

    [RelayCommand]
    private Task Retry() => ReloadAsync();

    // ---- the header ------------------------------------------------------------------------------

    /// <summary>The mosaic's name, in the page heading style.</summary>
    [ObservableProperty]
    public partial string Name { get; private set; }

    /// <summary>"n panels, integration total, f frames" (spec 12.17).</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; }

    /// <summary>A header-level failure: a refused or failed Delete, or a failed export write.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Every mosaic-scope custom column in display order, ungated by any picker, each the
    /// shared cell editor of spec 12.15 with the column's name as its caption.</summary>
    public IReadOnlyList<CustomValueViewModel> CustomCells => _cells.Cells;

    /// <summary>Composite's enablement (spec 12.17, ruling R15): at least one panel has a
    /// positioned best frame in the arranger's filter and a plate scale exists.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompositeCommand))]
    public partial bool CanComposite { get; private set; }

    /// <summary>Composite's tooltip, the first disabled state that applies; null when enabled.</summary>
    [ObservableProperty]
    public partial string? CompositeTooltip { get; private set; } = NoFramesTooltip;

    /// <summary>Composite's tooltip when no panel is included in <paramref name="filter"/>.</summary>
    public static string NoPositionedPanelTooltip(string filter) => $"No panel has a positioned frame in {filter}";

    /// <summary>Opens the composite lightbox for the arranger's selected filter.</summary>
    [RelayCommand(CanExecute = nameof(CanComposite))]
    private Task Composite()
    {
        if (_disposed || !CanComposite || CompositeRequestNow() is not { } request)
        {
            return Task.CompletedTask;
        }

        return _openComposite(new CompositeLightboxViewModel(request, _composite, _appWriter, Post, Logger));
    }

    // The request for the arranger's selected filter, as the filter's available spelling (the cache
    // key takes it as passed), over the frame set and the geometry the arranger's load brought.
    private CompositeRequest? CompositeRequestNow()
        => _detail is { } detail && Arranger.SelectedFilter is { } filter && Arranger.FrameSet is { } frames
            ? new CompositeRequest(Id, Name, filter, [.. detail.Panels.Select(panel => (panel.Id, panel.Label))], frames, Arranger.Geometry)
            : null;

    // Spec 12.17: recomputed whenever the frame set loads, the filter changes or the page re-reads.
    private void RefreshComposite()
    {
        if (CompositeRequestNow() is not { } request)
        {
            CanComposite = false;
            CompositeTooltip = NoFramesTooltip;
            return;
        }

        var block = _composite.Select(request).Block;
        CanComposite = block == CompositeBlock.None;
        CompositeTooltip = block switch
        {
            CompositeBlock.NoPlateScale => NoPlateScaleTooltip,
            CompositeBlock.NoPositionedPanel => NoPositionedPanelTooltip(request.Filter),
            _ => null,
        };
    }

    // Rename.

    [ObservableProperty]
    public partial bool IsRenaming { get; private set; }

    [ObservableProperty]
    public partial string RenameText { get; set; }

    [ObservableProperty]
    public partial string? RenameError { get; private set; }

    partial void OnRenameTextChanged(string value) => RenameError = null;

    [RelayCommand]
    private void BeginRename()
    {
        RenameText = Name;
        RenameError = null;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenaming = false;
        RenameError = null;
    }

    /// <summary>Enter or the save button. The name sentences of spec 12.17 refuse a blank or taken
    /// name inline.</summary>
    [RelayCommand]
    private void SaveRename()
    {
        var name = RenameText.Trim();
        if (name.Length == 0)
        {
            RenameError = MosaicMessages.EmptyName;
            return;
        }

        RenameError = Write(() => Backend.Rename(Id, name));
        if (RenameError is null)
        {
            Name = name;
            IsRenaming = false;
        }
    }

    // Delete mosaic, the two-press inline confirm in a strip under the identity line.

    [ObservableProperty]
    public partial bool DeletePending { get; private set; }

    /// <summary>The overflow entry arms the strip; its Confirm deletes and closes the page.</summary>
    [RelayCommand]
    private void DeleteMosaic()
    {
        if (_disposed)
        {
            return;
        }

        if (!DeletePending)
        {
            DeletePending = true;
            return;
        }

        DeletePending = false;
        Error = TryWrite(() => Backend.Delete(Id));
        if (Error is null)
        {
            _deleted = true;
            BackRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The overflow menu entry: arms the strip and nothing else, so choosing the entry
    /// twice never deletes without the strip's Confirm.</summary>
    [RelayCommand]
    private void ArmDeleteMosaic() => DeletePending = true;

    [RelayCommand]
    private void CancelDelete() => DeletePending = false;

    // Export panels (CSV).

    /// <summary>Opens the platform save dialog for the suggested file name and returns the chosen
    /// absolute path, or null when cancelled. Set by the view; null in a headless test that does
    /// not exercise it, which makes Export panels a no-op there.</summary>
    public Func<string, Task<string?>>? ExportDestinationPicker { get; set; }

    /// <summary>Spec 12.17's Export panels (CSV): one new file at the path the save dialog
    /// returned, built from the figures already on the page with no query of its own.</summary>
    [RelayCommand]
    private async Task ExportPanelsAsync()
    {
        if (_detail is not { } detail || ExportDestinationPicker is not { } picker || _disposed)
        {
            return;
        }

        Error = null;
        var path = await picker(ExportFileName(detail.Name)).ConfigureAwait(true);
        if (path is null || _disposed)
        {
            return;
        }

        var csv = BuildCsv(detail);
        try
        {
            using var writer = _appWriter.BeginExport(path);
            writer.WriteAllText(path, csv);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Writing the mosaic panels CSV failed");
            Error = ExportFailedText;
        }
    }

    /// <summary><c>&lt;name&gt;_panels.csv</c>, every character outside A to Z, a to z and 0 to 9
    /// replaced by <c>_</c>.</summary>
    public static string ExportFileName(string name) => SafeFileName(name) + "_panels.csv";

    /// <summary>Spec 12.17's file name rule, shared by Export panels and the composite's Download:
    /// every character outside A to Z, a to z and 0 to 9 replaced by <c>_</c>.</summary>
    internal static string SafeFileName(string text)
        => string.Concat(text.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));

    /// <summary>The CSV text: the header, then one row per panel in <c>sort_order</c>; every line
    /// ends with <c>\n</c>. <c>File.WriteAllText</c> writes it as UTF-8 without a byte order mark.</summary>
    public static string BuildCsv(MosaicDetail detail)
    {
        var builder = new StringBuilder(CsvHeader).Append('\n');
        foreach (var row in MosaicQueries.CsvRows(detail))
        {
            builder
                .Append(CsvField(row.Label)).Append(',')
                .Append(CsvField(string.Join("; ", row.Targets))).Append(',')
                .Append(row.Frames.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Seconds(row.IntegrationSeconds)).Append(',')
                .Append(CsvField(string.Join("; ", row.IntegrationByFilter.Select(pair => $"{pair.Key}: {Seconds(pair.Value)}"))))
                .Append('\n');
        }

        return builder.ToString();
    }

    private static string Seconds(double seconds)
        => Math.Round(seconds, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    private static string CsvField(string value)
        => value.IndexOfAny([',', '"', '\n', '\r']) < 0 ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    // ---- the arranger ------------------------------------------------------------------------------

    /// <summary>Spec 12.17's arranger in row 2, fed by every read. Its layout save calls
    /// <c>UpdateLayout</c> directly and not through <see cref="Write"/>: it changes no figure, so it
    /// triggers no re-read.</summary>
    public ArrangerViewModel Arranger { get; }

    // ---- notes -----------------------------------------------------------------------------------

    /// <summary>The notes box, autosaved one second after the last keystroke; an emptied box stores
    /// null.</summary>
    public AutosaveField Notes { get; }

    // ---- the available labels banner --------------------------------------------------------------

    /// <summary>One chip per (target, label) pair no panel carries.</summary>
    public ObservableCollection<AvailableLabelViewModel> AvailableLabels { get; } = [];

    /// <summary>The banner collapses to nothing when there is no label.</summary>
    public bool HasAvailableLabels => AvailableLabels.Count > 0;

    // ---- the sessions region ----------------------------------------------------------------------

    /// <summary>One row per panel in <c>sort_order</c>.</summary>
    public ObservableCollection<PanelViewModel> Panels { get; } = [];

    /// <summary>The add panel form, shared with the Mosaics table row.</summary>
    public AddPanelViewModel AddPanel { get; }

    /// <summary>True while the add panel form is open under the sessions header.</summary>
    [ObservableProperty]
    public partial bool IsAddPanelOpen { get; private set; }

    [RelayCommand]
    private void ToggleAddPanel() => IsAddPanelOpen = !IsAddPanelOpen;

    /// <summary>A refused or failed Include all available.</summary>
    [ObservableProperty]
    public partial string? SessionsError { get; private set; }

    private bool CanIncludeAllAvailable() => Panels.Any(panel => panel.HasAvailable);

    /// <summary>Includes every available triple of every panel, panels taken in <c>sort_order</c>
    /// so a triple available in two panels goes to the first.</summary>
    [RelayCommand(CanExecute = nameof(CanIncludeAllAvailable))]
    private void IncludeAllAvailable() => SessionsError = Write(() => Backend.IncludeAllAvailable(Id));

    /// <summary>Every panel label of the mosaic, for the As new panel prefill.</summary>
    internal IReadOnlyList<string> PanelLabels => [.. Panels.Select(panel => panel.Label)];

    // ---- reading and writing ----------------------------------------------------------------------

    /// <summary>Runs one write and, when it succeeded, re-reads the page. Null on success,
    /// otherwise the inline sentence.</summary>
    internal string? Write(Action write)
    {
        var error = TryWrite(write);
        if (error is null)
        {
            _ = ReloadAsync();
        }

        return error;
    }

    private string? TryWrite(Action write)
    {
        if (_disposed)
        {
            return null;
        }

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
            Logger.LogWarning(ex, "A mosaic write failed");
            return MosaicMessages.CouldNotSave;
        }
    }

    /// <summary>Reads the mosaic, its custom column definitions and its values off the UI thread,
    /// then applies them. A newer read supersedes an older one.</summary>
    public Task ReloadAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        var generation = ++_generation;
        return PendingLoad = LoadAsync(generation);
    }

    private async Task LoadAsync(int generation)
    {
        MosaicDetail? detail;
        IReadOnlyList<CustomColumnDefinition> columns;
        IReadOnlyList<CustomValueRow> values;
        try
        {
            (detail, columns, values) = await Task.Run(
                () => (Backend.Detail(Id), Backend.CustomColumns(), Backend.MosaicValues([Id])),
                _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Reading a mosaic failed");
            Post(() =>
            {
                if (!_disposed && generation == _generation)
                {
                    LoadFailed = true;
                }
            });
            return;
        }

        Post(() =>
        {
            if (!_disposed && generation == _generation)
            {
                Apply(detail, columns, values);
            }
        });
    }

    private void Apply(MosaicDetail? detail, IReadOnlyList<CustomColumnDefinition> columns, IReadOnlyList<CustomValueRow> values)
    {
        LoadFailed = false;
        IsNotFound = detail is null;
        if (detail is null)
        {
            return;
        }

        _detail = detail;
        Name = detail.Name;
        SummaryText = string.Join(", ",
            MosaicsPageViewModel.Plural(detail.Panels.Count, "panel"),
            MetricText.Integration(detail.IntegrationSeconds) + " total",
            MosaicsPageViewModel.Plural(detail.Frames, "frame"));
        Notes.Reseed(detail.Notes);

        _cells = CustomCellFactory.Reconcile(
            _cells,
            CustomColumnSet.MosaicRow(columns),
            CustomValueKey.ForMosaic(Id),
            detail.Name,
            column => values.FirstOrDefault(value => value.ColumnId == column.Id)?.Value,
            Backend.WriteValue,
            Delay,
            Post,
            Logger);
        OnPropertyChanged(nameof(CustomCells));

        AvailableLabels.Clear();
        foreach (var label in detail.AvailableLabels)
        {
            AvailableLabels.Add(new AvailableLabelViewModel(label, this));
        }

        OnPropertyChanged(nameof(HasAvailableLabels));

        // Panel rows are kept by id across a re-read and updated in place, and the collection is
        // reconciled rather than cleared, so a panel's container (and the focus inside it), its
        // expansion, a half-typed Add nights search and an open As new panel row survive an
        // include elsewhere on the page or a scan's re-read. Each panel keeps its night rows by
        // triple the same way (PanelViewModel.Apply).
        var labels = detail.Panels.Select(panel => panel.Label).ToList();
        var rows = new List<PanelViewModel>();
        foreach (var panel in detail.Panels)
        {
            if (_panelsById.TryGetValue(panel.Id, out var row))
            {
                row.Apply(panel);
            }
            else
            {
                row = new PanelViewModel(panel, this);
                _panelsById[panel.Id] = row;
            }

            rows.Add(row);
        }

        foreach (var gone in _panelsById.Keys.Except(detail.Panels.Select(panel => panel.Id)).ToList())
        {
            _panelsById[gone].Dispose();
            _panelsById.Remove(gone);
        }

        Reconcile(Panels, rows);

        // SetLabels re-prefills the label box and leaves a label the reader is typing alone.
        AddPanel.SetLabels(labels);
        IncludeAllAvailableCommand.NotifyCanExecuteChanged();
        Arranger.Apply(detail);
        RefreshComposite();
    }

    /// <summary>Makes <paramref name="target"/> hold <paramref name="wanted"/> in order with the
    /// fewest collection changes: rows that left are removed, new rows inserted, moved rows moved.
    /// A row that stays is never removed and re-added, so its container is kept.</summary>
    internal static void Reconcile<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted)
        where T : class
    {
        var keep = wanted.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!keep.Contains(target[index]))
            {
                target.RemoveAt(index);
            }
        }

        for (var index = 0; index < wanted.Count; index++)
        {
            var current = target.IndexOf(wanted[index]);
            if (current < 0)
            {
                target.Insert(index, wanted[index]);
            }
            else if (current != index)
            {
                target.Move(current, index);
            }
        }
    }

    // Spec 12.17: a scan or a detection job's end re-reads the page.
    private void OnRecentChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed || e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
        {
            return;
        }

        if (e.NewItems.OfType<JobViewModel>().Any(job => RereadKinds.Contains(job.Kind)))
        {
            _ = ReloadAsync();
        }
    }

    /// <summary>Closing the page flushes a note or a custom cell typed and not yet saved, bounded
    /// so a locked database cannot hang the window, then releases everything it holds.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // The notes, the custom cells and a pending layout write flush together under one bound.
        // A deleted mosaic flushes nothing, its layout write included.
        if (!_deleted)
        {
            try
            {
                Task.WhenAll(Notes.FlushAsync(), _cells.FlushAsync(), Arranger.FlushAsync()).Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Flushing the mosaic page on close failed");
            }
        }
        else
        {
            Arranger.Discard();
        }

        // Releases the tiles' thumbnails; the flush above already ran or dropped the layout write.
        Arranger.Dispose();

        _disposed = true;
        if (_jobs is not null)
        {
            _jobs.Recent.CollectionChanged -= OnRecentChanged;
        }

        Notes.Dispose();
        _cells.Dispose();
        AddPanel.Dispose();
        foreach (var panel in _panelsById.Values)
        {
            panel.Dispose();
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

/// <summary>One chip of spec 12.17's available labels banner: "&lt;label&gt; on &lt;target&gt;"
/// and Add panel, which runs the add panel rule for that target and label.</summary>
public sealed partial class AvailableLabelViewModel : ObservableObject
{
    private readonly MosaicDetailViewModel _page;

    internal AvailableLabelViewModel(AvailableLabel label, MosaicDetailViewModel page)
    {
        Label = label;
        _page = page;
    }

    public AvailableLabel Label { get; }

    public string Text => $"{Label.Label} on {Label.TargetName}";

    /// <summary>A refused or failed Add panel, shown beside the chip.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    [RelayCommand]
    private void AddPanel()
        => Error = _page.Write(() => _page.Backend.AddPanelWithTarget(_page.Id, Label.TargetId, Label.Label));
}
