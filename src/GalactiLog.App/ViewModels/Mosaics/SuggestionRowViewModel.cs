using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;

namespace GalactiLog.App.ViewModels.Mosaics;

/// <summary>One target link on an expanded suggestion row (spec 12.17).</summary>
public sealed record SuggestionTargetLink(Guid TargetId, string Name);

/// <summary>
/// One pending suggestion on the Mosaics page (spec 12.17): the badges, the review notes, the
/// session table with a check box per panel, the totals line over the checked panels, Accept with
/// the checked labels and Dismiss behind the two-press confirm.
/// </summary>
/// <remarks>Checked panels, the expansion and the selection are view state: a reload builds new
/// rows, which is what resets them (spec 12.17's reload rule). The page disposes a row it drops,
/// which releases the tile preview's thumbnails.</remarks>
public sealed partial class SuggestionRowViewModel : ObservableObject, IDisposable
{
    /// <summary>The Dismiss confirm sentence.</summary>
    public const string DismissConfirmText = "Dismiss this suggestion? It comes back only if new nights of these panels are catalogued.";

    private readonly MosaicsPageViewModel _page;
    private readonly IReadOnlyList<SuggestionSessionRow> _sessions;
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);

    internal SuggestionRowViewModel(
        MosaicSuggestionRow row,
        IReadOnlyList<SuggestionSessionRow> sessions,
        IReadOnlyDictionary<Guid, string> targetNames,
        MosaicsPageViewModel page)
    {
        Row = row;
        _page = page;
        _sessions = sessions;
        Labels = [.. row.Panels.Select(panel => panel.Label).Distinct(StringComparer.OrdinalIgnoreCase)];
        _checked.UnionWith(Labels);
        Targets = [.. row.Panels.Select(panel => panel.TargetId).Distinct()
            .Select(id => new SuggestionTargetLink(id, targetNames.TryGetValue(id, out var name) ? name : id.ToString()))];
        Sessions = [.. sessions.Where(session => session.InCampaign).Select(session => new SuggestionSessionViewModel(session, this))];
        SortSessions();
        Preview = new ArrangerViewModel(Guid.Empty, page.Backend, readOnly: true, page.Post, page.Delay, page.Logger);
    }

    /// <summary>The stored suggestion.</summary>
    public MosaicSuggestionRow Row { get; }

    public string Name => Row.SuggestedName;

    /// <summary>The distinct panel labels, in entry order.</summary>
    public IReadOnlyList<string> Labels { get; }

    /// <summary>The labels Accept passes: every label until the reader unchecks one.</summary>
    public IReadOnlyList<string> CheckedLabels => [.. Labels.Where(_checked.Contains)];

    public bool IsHighConfidence => string.Equals(Row.Confidence, "high", StringComparison.OrdinalIgnoreCase);

    public string ConfidenceText => IsHighConfidence ? "High confidence" : "Low confidence";

    public string ConfidenceTooltip => IsHighConfidence
        ? "Name and position agree on these panels"
        : "Single signal or conflicting evidence; review before accepting";

    /// <summary>"name", "position" or "name + position" for the stored source.</summary>
    public string SourceText => Row.DiscoverySource switch
    {
        "both" => "name + position",
        var source => source,
    };

    public IReadOnlyList<string> Flags => Row.Flags;

    public bool HasFlags => Row.Flags.Count > 0;

    public IReadOnlyList<SuggestionTargetLink> Targets { get; }

    [RelayCommand]
    private void OpenTarget(SuggestionTargetLink? link)
    {
        if (link is not null)
        {
            _page.RequestOpenTarget(link.TargetId);
        }
    }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    // The preview is fed on the first expansion and on every check change after it, so a page of
    // collapsed rows runs no best frame query.
    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_previewFed)
        {
            FeedPreview();
        }
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    // ---- the panel check boxes and the totals line ----------------------------------------

    public bool IsChecked(string label) => _checked.Contains(label);

    /// <summary>Checks or unchecks one panel: every session row of that label at once.</summary>
    public void SetChecked(string label, bool value)
    {
        if (value ? !_checked.Add(label) : !_checked.Remove(label))
        {
            return;
        }

        OnCheckedChanged();
    }

    /// <summary>The session table's header box: every panel at once.</summary>
    public bool AllPanelsChecked
    {
        get => _checked.Count == Labels.Count;
        set
        {
            if (value)
            {
                _checked.UnionWith(Labels);
            }
            else
            {
                _checked.Clear();
            }

            OnCheckedChanged();
        }
    }

    private void OnCheckedChanged()
    {
        foreach (var session in Sessions)
        {
            session.NotifyChecked();
        }

        AcceptError = null;
        if (_previewFed)
        {
            FeedPreview();
        }

        OnPropertyChanged(nameof(AllPanelsChecked));
        OnPropertyChanged(nameof(CheckedLabels));
        OnPropertyChanged(nameof(TotalsText));
        OnPropertyChanged(nameof(FramesText));
        OnPropertyChanged(nameof(FramesAreZero));
        OnPropertyChanged(nameof(IntegrationText));
        OnPropertyChanged(nameof(AcceptText));
        OnPropertyChanged(nameof(AcceptTooltip));
        AcceptCommand.NotifyCanExecuteChanged();
    }

    private IEnumerable<SuggestionSessionRow> CheckedSessions
        => _sessions.Where(session => session.InCampaign && _checked.Contains(session.Label));

    /// <summary>"&lt;p&gt; panels, " over the checked labels that have at least one row.</summary>
    public string TotalsText
    {
        get
        {
            var panels = CheckedSessions.Select(session => session.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return MosaicsPageViewModel.Plural(panels, "panel") + ", ";
        }
    }

    /// <summary>The frames figure, in the warning ink at zero.</summary>
    public string FramesText => MosaicsPageViewModel.Plural(CheckedSessions.Sum(session => session.Frames), "frame");

    public bool FramesAreZero => !CheckedSessions.Any(session => session.Frames > 0);

    public string IntegrationText => ", " + MetricText.Integration(CheckedSessions.Sum(session => session.IntegrationSeconds));

    /// <summary>"+k more nights": the distinct (target, night) pairs outside the campaign, or empty.</summary>
    public string MoreNightsText
    {
        get
        {
            var outside = _sessions.Where(session => !session.InCampaign).Select(session => (session.TargetId, session.Night)).Distinct().Count();
            return outside == 0 ? "" : $"+{outside} more night{(outside == 1 ? "" : "s")}";
        }
    }

    public bool HasMoreNights => MoreNightsText.Length > 0;

    // ---- the tile preview ---------------------------------------------------------------------

    /// <summary>Spec 12.17's read-only tile preview: one tile per checked label in label order,
    /// ordinal and case insensitive, whatever the session table's sort.</summary>
    public ArrangerViewModel Preview { get; }

    private bool _previewFed;

    private void FeedPreview()
    {
        _previewFed = true;
        var backend = _page.Backend;
        Preview.ApplyPreview(
        [
            .. CheckedLabels.Order(StringComparer.OrdinalIgnoreCase).Select(label =>
            {
                // The entry's target, and the label's in-campaign rows on it, so the integration
                // matches the frame the tile shows.
                var target = Row.Panels.First(panel => string.Equals(panel.Label, label, StringComparison.OrdinalIgnoreCase)).TargetId;
                var rows = _sessions.Where(session => session.InCampaign && session.TargetId == target && string.Equals(session.Label, label, StringComparison.OrdinalIgnoreCase)).ToList();
                IReadOnlyCollection<DateOnly> nights = [.. rows.Select(session => session.Night).Distinct()];
                return new PreviewTile(label, rows.Sum(session => session.IntegrationSeconds), () => backend.SuggestionBestFrame(target, label, nights));
            }),
        ]);
    }

    public void Dispose() => Preview.Dispose();

    // ---- the session table -----------------------------------------------------------------

    public ObservableCollection<SuggestionSessionViewModel> Sessions { get; }

    [ObservableProperty]
    public partial string SessionSortKey { get; private set; } = "panel";

    [ObservableProperty]
    public partial bool SessionSortAscending { get; private set; } = true;

    /// <summary>A header click: ascending on a new column, reversed on the same column.</summary>
    [RelayCommand]
    private void SortSessionsBy(string? key)
    {
        if (key is null)
        {
            return;
        }

        SessionSortAscending = key != SessionSortKey || !SessionSortAscending;
        SessionSortKey = key;
        SortSessions();
    }

    private void SortSessions()
    {
        Comparison<SuggestionSessionRow> by = SessionSortKey switch
        {
            "object" => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.ObjectName, b.ObjectName),
            "night" => (a, b) => a.Night.CompareTo(b.Night),
            "filter" => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Filter ?? "", b.Filter ?? ""),
            "frames" => (a, b) => a.Frames.CompareTo(b.Frames),
            "integration" => (a, b) => a.IntegrationSeconds.CompareTo(b.IntegrationSeconds),
            _ => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Label, b.Label),
        };

        // OrderBy is stable, so rows that tie keep the query's own order.
        var comparer = Comparer<SuggestionSessionRow>.Create(by);
        var sorted = SessionSortAscending
            ? Sessions.OrderBy(session => session.Data, comparer).ToList()
            : Sessions.OrderByDescending(session => session.Data, comparer).ToList();
        for (var index = 0; index < sorted.Count; index++)
        {
            var current = Sessions.IndexOf(sorted[index]);
            if (current != index)
            {
                Sessions.Move(current, index);
            }
        }
    }

    // ---- Accept and Dismiss ------------------------------------------------------------------

    /// <summary>"Accept", or "Accept (c of n)" while only c of the n labels are checked.</summary>
    public string AcceptText => _checked.Count == Labels.Count ? "Accept" : $"Accept ({_checked.Count} of {Labels.Count})";

    public string? AcceptTooltip => _checked.Count == 0 ? MosaicMessages.NoPanelChecked : null;

    /// <summary>A refusal under the row: a taken name, or a write that threw.</summary>
    [ObservableProperty]
    public partial string? AcceptError { get; private set; }

    private bool CanAccept() => _page.CanAct && _checked.Count > 0;

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private void Accept()
    {
        if (!_page.CanAct)
        {
            return;
        }

        if (_checked.Count == 0)
        {
            AcceptError = MosaicMessages.NoPanelChecked;
            return;
        }

        var labels = CheckedLabels;
        AcceptError = _page.TryWrite(() => _page.Backend.Accept(Row.Id, labels));
        if (AcceptError is null)
        {
            _page.RemoveSuggestion(this, reloadMosaics: true);
        }
    }

    [ObservableProperty]
    public partial bool DismissPending { get; private set; }

    private bool CanDismiss() => _page.CanAct;

    /// <summary>The first press arms the confirm; the second dismisses (spec 7.7).</summary>
    [RelayCommand(CanExecute = nameof(CanDismiss))]
    private void Dismiss()
    {
        if (!_page.CanAct)
        {
            return;
        }

        if (!DismissPending)
        {
            DismissPending = true;
            return;
        }

        DismissPending = false;
        AcceptError = _page.TryWrite(() => _page.Backend.Dismiss(Row.Id));
        if (AcceptError is null)
        {
            _page.RemoveSuggestion(this, reloadMosaics: false);
        }
    }

    [RelayCommand]
    private void CancelDismiss() => DismissPending = false;

    internal void NotifyGates()
    {
        AcceptCommand.NotifyCanExecuteChanged();
        DismissCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One row of a suggestion's session table (spec 12.17). Its check box is its panel's:
/// checking it checks every row of that label.</summary>
public sealed partial class SuggestionSessionViewModel(SuggestionSessionRow data, SuggestionRowViewModel owner) : ObservableObject
{
    public SuggestionSessionRow Data { get; } = data;

    public bool IsChecked
    {
        get => owner.IsChecked(Data.Label);
        set => owner.SetChecked(Data.Label, value);
    }

    public string NightText => MetricText.Date(Data.Night);

    /// <summary>The canonical filter, "-" for frames that carry none.</summary>
    public string FilterText => MetricText.Cell(Data.Filter);

    public string FramesText => MetricText.Count(Data.Frames);

    public string IntegrationText => MetricText.Integration(Data.IntegrationSeconds);

    internal void NotifyChecked() => OnPropertyChanged(nameof(IsChecked));
}
