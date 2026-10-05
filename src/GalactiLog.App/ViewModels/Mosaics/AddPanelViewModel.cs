using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Mosaics;

/// <summary>
/// Spec 12.17's add panel form, shared by an expanded Mosaics table row and the mosaic detail
/// page's header: a debounced target search (<see cref="TargetSearchForm"/>), a panel label
/// prefilled with the first free "Panel n", and Add, which puts the chosen target's nights into
/// the panel of that label.
/// </summary>
public sealed partial class AddPanelViewModel : TargetSearchForm
{
    private readonly Guid _mosaicId;
    private readonly Func<Guid, Guid, string, PanelAddResult> _add;
    private readonly Action _added;

    /// <param name="mosaicId">The mosaic the panel goes into.</param>
    /// <param name="labels">The mosaic's panel labels, for the prefill.</param>
    /// <param name="search">Normally <c>TargetSearchQuery.Search</c>.</param>
    /// <param name="add">Normally <c>MosaicRepository.AddPanelWithTarget</c>.</param>
    /// <param name="added">Called on the UI thread after a successful Add, so the host re-reads the
    /// mosaic and hands the new labels back through <see cref="SetLabels"/>.</param>
    /// <param name="delay">The debounce seam; the dashboard's own window.</param>
    /// <param name="post">How to reach the UI thread.</param>
    /// <param name="logger">A failed search is logged and leaves the list as it was.</param>
    public AddPanelViewModel(
        Guid mosaicId,
        IReadOnlyList<string> labels,
        Func<string, IReadOnlyList<TargetSearchResult>> search,
        Func<Guid, Guid, string, PanelAddResult> add,
        Action added,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<Action>? post = null,
        ILogger? logger = null)
        : base(search, delay, post, logger)
    {
        _mosaicId = mosaicId;
        _add = add;
        _added = added;
        Label = "";
        SetLabels(labels);
    }

    /// <summary>The chosen target, or null.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial SearchResultViewModel? Chosen { get; private set; }

    /// <summary>The "Panel label" box.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string Label { get; set; }

    /// <summary>"n nights already in another panel were skipped.", or null.</summary>
    [ObservableProperty]
    public partial string? Caption { get; private set; }

    /// <summary>A refused or failed Add, shown inline.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Replaces the mosaic's labels and re-prefills the label box.</summary>
    public void SetLabels(IReadOnlyList<string> labels) => Label = NextLabel(labels);

    /// <summary>"Panel n" for the smallest n of 1 or more that no label carries, case insensitively.</summary>
    internal static string NextLabel(IReadOnlyList<string> labels)
    {
        var taken = labels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = 1;
        while (taken.Contains(string.Create(CultureInfo.InvariantCulture, $"Panel {n}")))
        {
            n++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"Panel {n}");
    }

    protected override void OnChosen(SearchResultViewModel result) => Chosen = result;

    // A pick belongs to the text it was made from: typing again drops it.
    protected override void OnSearchEdited() => Chosen = null;

    private bool CanAdd() => Chosen?.TargetId is not null && !string.IsNullOrWhiteSpace(Label);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (!CanAdd() || IsDisposed)
        {
            return;
        }

        Caption = null;
        Error = null;
        try
        {
            var result = _add(_mosaicId, Chosen!.TargetId!.Value, Label.Trim());
            Caption = result.Skipped switch
            {
                0 => null,
                1 => "1 night already in another panel was skipped.",
                var skipped => $"{skipped} nights already in another panel were skipped.",
            };
            Chosen = null;
            SearchText = "";
            _added();
        }
        catch (MosaicWriteException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Adding a panel to a mosaic failed");
            Error = MosaicMessages.CouldNotSave;
        }
    }
}
