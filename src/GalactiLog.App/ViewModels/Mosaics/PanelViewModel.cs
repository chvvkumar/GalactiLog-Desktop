using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Mosaics;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;

namespace GalactiLog.App.ViewModels.Mosaics;

/// <summary>
/// One panel row of the mosaic detail page's sessions region (spec 12.17): the label, the targets,
/// the figures, the deficit against the leading panel, the available count, Include all and Delete
/// panel, and, expanded, the Included and Available tables with Add nights from any target.
/// </summary>
/// <remarks>The page keeps one instance per panel id across re-reads and hands each fresh
/// <see cref="PanelDetail"/> to <see cref="Apply"/>, so the expanded state and the forms survive.
/// Expanded state is view state and is never stored.</remarks>
public sealed partial class PanelViewModel : ObservableObject, IDisposable
{
    /// <summary>Delete panel's tooltip while the panel still has an included night.</summary>
    public const string DeleteDisabledTooltip = "Remove its included nights first.";

    private readonly MosaicDetailViewModel _page;

    internal PanelViewModel(PanelDetail panel, MosaicDetailViewModel page)
    {
        _page = page;
        Panel = panel;
        AddNights = new AddNightsViewModel(panel.Id, page);
        Apply(panel);
    }

    /// <summary>The panel as last read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(Label), nameof(TargetsText), nameof(IntegrationText), nameof(FramesText), nameof(NightsText),
        nameof(DeficitText), nameof(HasAvailable), nameof(AvailableText), nameof(CanDelete), nameof(DeleteTooltip),
        nameof(DeleteConfirmText))]
    public partial PanelDetail Panel { get; private set; }

    public Guid Id => Panel.Id;

    public string Label => Panel.Label;

    /// <summary>The primary names of the panel's targets, joined with ", ".</summary>
    public string TargetsText => string.Join(", ", Panel.TargetNames);

    public string IntegrationText => MetricText.Integration(Panel.IntegrationSeconds);

    public string FramesText => MetricText.Count(Panel.Frames);

    /// <summary>The distinct (target, night) pairs among the included rows.</summary>
    public string NightsText => MetricText.Count(Panel.NightsIncluded);

    /// <summary>"2h 10m behind" below the leading panel; empty on the leader and while every panel
    /// is zero (the query already makes the deficit zero there).</summary>
    public string DeficitText => Panel.DeficitSeconds > 0 ? MetricText.Integration(Panel.DeficitSeconds) + " behind" : "";

    /// <summary>True while the Available table has a row.</summary>
    public bool HasAvailable => Panel.Available.Count > 0;

    /// <summary>"n available", the warning-ink tag.</summary>
    public string AvailableText => MetricText.Count(Panel.Available.Count) + " available";

    /// <summary>Included rows, newest first, as the query orders them.</summary>
    public ObservableCollection<PanelSessionViewModel> Included { get; } = [];

    /// <summary>Available triples, newest first.</summary>
    public ObservableCollection<PanelSessionViewModel> Available { get; } = [];

    /// <summary>Add nights from any target, under the Available table.</summary>
    public AddNightsViewModel AddNights { get; }

    /// <summary>A refused or failed write on this row.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    internal MosaicDetailViewModel Page => _page;

    internal void Apply(PanelDetail panel)
    {
        Panel = panel;
        Error = null;
        Sync(Included, panel.Included, included: true);
        Sync(Available, panel.Available, included: false);
        IncludeAllCommand.NotifyCanExecuteChanged();
        DeletePanelCommand.NotifyCanExecuteChanged();
        if (!CanDelete)
        {
            DeletePending = false;
        }
    }

    // Night rows are kept by triple (target, night, frame label compared case insensitively) and
    // updated in place, so an open As new panel row keeps its label and its inline refusal across
    // a re-read.
    private void Sync(ObservableCollection<PanelSessionViewModel> rows, IReadOnlyList<PanelNight> nights, bool included)
    {
        var existing = rows.ToDictionary(row => TripleKey(row.Night));
        var wanted = new List<PanelSessionViewModel>();
        foreach (var night in nights)
        {
            if (existing.TryGetValue(TripleKey(night), out var row))
            {
                row.Night = night;
            }
            else
            {
                row = new PanelSessionViewModel(night, this, included);
            }

            wanted.Add(row);
        }

        MosaicDetailViewModel.Reconcile(rows, wanted);
    }

    private static (Guid, DateOnly, string) TripleKey(PanelNight night)
        => (night.TargetId, night.Date, (night.FrameLabel ?? "").ToUpperInvariant());

    /// <summary>Runs a write on this panel and re-reads the page.</summary>
    internal string? Write(Action write) => _page.Write(write);

    // Expand.

    [ObservableProperty]
    public partial bool IsExpanded { get; private set; }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    // Include all.

    [RelayCommand(CanExecute = nameof(HasAvailable))]
    private void IncludeAll() => Error = Write(() => _page.Backend.IncludeAll(Id));

    // Delete panel, two-press, only while no night is included.

    public bool CanDelete => Panel.Included.Count == 0;

    public string? DeleteTooltip => CanDelete ? null : DeleteDisabledTooltip;

    public string DeleteConfirmText => $"Delete panel {Label}?";

    [ObservableProperty]
    public partial bool DeletePending { get; private set; }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void DeletePanel()
    {
        if (!CanDelete)
        {
            return;
        }

        if (!DeletePending)
        {
            DeletePending = true;
            return;
        }

        DeletePending = false;
        Error = Write(() => _page.Backend.DeletePanel(Id));
    }

    [RelayCommand]
    private void CancelDeletePanel() => DeletePending = false;

    public void Dispose() => AddNights.Dispose();
}

/// <summary>
/// One row of a panel's Included or Available table (spec 12.17): a (target, night, frame label)
/// triple with its night, target link, label, per-filter counts, frames and integration; Remove on
/// an included row; Include and As new panel on an available one.
/// </summary>
public sealed partial class PanelSessionViewModel : ObservableObject
{
    /// <summary>The Label column for a null frame label (ruling R19a).</summary>
    public const string NoLabelText = "No label";

    private readonly PanelViewModel _panel;

    internal PanelSessionViewModel(PanelNight night, PanelViewModel panel, bool included)
    {
        _night = night;
        _panel = panel;
        IsIncluded = included;
        NewPanelLabel = "";
    }

    private PanelNight _night;

    /// <summary>The triple and its figures as last read; a re-read updates them in place.</summary>
    public PanelNight Night
    {
        get => _night;
        internal set
        {
            if (SetProperty(ref _night, value))
            {
                OnPropertyChanged(nameof(NightText));
                OnPropertyChanged(nameof(TargetName));
                OnPropertyChanged(nameof(LabelText));
                OnPropertyChanged(nameof(FiltersText));
                OnPropertyChanged(nameof(FramesText));
                OnPropertyChanged(nameof(IntegrationText));
            }
        }
    }

    public bool IsIncluded { get; }

    public string NightText => MetricText.Date(Night.Date);

    public string TargetName => Night.TargetName;

    public string LabelText => Night.FrameLabel ?? NoLabelText;

    /// <summary>"&lt;filter&gt; &lt;count&gt;" joined with ", ", filters in ordinal case-insensitive order.</summary>
    public string FiltersText => string.Join(", ", Night.FramesByFilter
        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
        .ThenBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => $"{pair.Key} {MetricText.Count(pair.Value)}"));

    public string FramesText => MetricText.Count(Night.Frames);

    public string IntegrationText => MetricText.Integration(Night.IntegrationSeconds);

    /// <summary>A refused or failed write on this row, shown inline.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The target link: opens its Target detail page, which closes the mosaic page.</summary>
    [RelayCommand]
    private void OpenTarget() => _panel.Page.RequestOpenTarget(Night.TargetId);

    /// <summary>Turns an included row available. Asks nothing; Include undoes it.</summary>
    [RelayCommand]
    private void Remove()
        => Error = _panel.Write(() => _panel.Page.Backend.RemoveNight(_panel.Id, Night.TargetId, Night.Date, Night.FrameLabel));

    /// <summary>Makes an available triple included in this panel.</summary>
    [RelayCommand]
    private void Include()
        => Error = _panel.Write(() => _panel.Page.Backend.IncludeNight(_panel.Id, Night.TargetId, Night.Date, Night.FrameLabel));

    // As new panel: an inline row with a label box prefilled with the next suffix.

    [ObservableProperty]
    public partial bool IsNamingNewPanel { get; private set; }

    [ObservableProperty]
    public partial string NewPanelLabel { get; set; }

    [RelayCommand]
    private void BeginNewPanel()
    {
        NewPanelLabel = PanelTokens.NextLabelSuffix(_panel.Label, _panel.Page.PanelLabels);
        Error = null;
        IsNamingNewPanel = true;
    }

    [RelayCommand]
    private void CancelNewPanel()
    {
        IsNamingNewPanel = false;
        Error = null;
    }

    /// <summary>Makes a panel of the typed label at the end of <c>sort_order</c> and includes the
    /// triple in it, its frame label unchanged.</summary>
    [RelayCommand]
    private void CreateNewPanel()
    {
        var label = NewPanelLabel.Trim();
        if (label.Length == 0)
        {
            Error = MosaicMessages.EmptyLabel;
            return;
        }

        Error = _panel.Write(() => _panel.Page.Backend.IncludeAsNewPanel(
            _panel.Page.Id, _panel.Id, Night.TargetId, Night.Date, Night.FrameLabel, label));
        if (Error is null)
        {
            IsNamingNewPanel = false;
        }
    }
}

/// <summary>
/// A panel's Add nights from any target (spec 12.17, ruling R19): the shared target search, and
/// choosing a target writes an <c>available</c> row on this panel for each of that target's
/// triples no panel of the mosaic includes, so they appear in Available for Include.
/// </summary>
public sealed partial class AddNightsViewModel : TargetSearchForm
{
    private readonly Guid _panelId;
    private readonly MosaicDetailViewModel _page;

    internal AddNightsViewModel(Guid panelId, MosaicDetailViewModel page)
        : base(page.Backend.SearchTargets, page.Delay, page.Post, page.Logger)
    {
        _panelId = panelId;
        _page = page;
    }

    /// <summary>A refused or failed add, shown inline.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    protected override void OnChosen(SearchResultViewModel result)
    {
        var targetId = result.TargetId!.Value;
        Error = _page.Write(() => _page.Backend.AddTargetNights(_panelId, targetId));
        if (Error is null)
        {
            SearchText = "";
        }
    }
}
