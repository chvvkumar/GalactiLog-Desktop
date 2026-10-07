using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Mosaics;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>One row of the Create mosaic dialog's Nights table (spec 12.17): one checked night's
/// LIGHT frames of the target that carry one frame label, with the panel label the reader gives
/// them.</summary>
public sealed partial class CreateMosaicRowViewModel : ObservableObject
{
    /// <param name="night">The night.</param>
    /// <param name="frameLabel">The frames' stored <c>panel_label</c>, null for none.</param>
    /// <param name="frames">How many LIGHT frames the row stands for.</param>
    public CreateMosaicRowViewModel(DateOnly night, string? frameLabel, int frames)
    {
        Night = night;
        FrameLabel = frameLabel;
        Frames = frames;
        Label = frameLabel ?? "";
    }

    /// <summary>The night.</summary>
    public DateOnly Night { get; }

    /// <summary>The frames' own label, which the row is written with whatever panel it lands in.</summary>
    public string? FrameLabel { get; }

    /// <summary>The LIGHT frame count.</summary>
    public int Frames { get; }

    /// <summary>The Night column.</summary>
    public string NightText => Night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The Frame label column: "No label" for null.</summary>
    public string FrameLabelText => FrameLabel ?? "No label";

    /// <summary>The Panel label box, prefilled with the frame label, empty when there is none.</summary>
    [ObservableProperty]
    public partial string Label { get; set; }
}

/// <summary>
/// Spec 12.17's Create mosaic dialog, opened from the Target detail page's overflow entry over
/// the checked nights (spec 12.4, ruling R12). New mosaic with a prefilled name, or Add to
/// existing; one row per (night, frame label) pair; Create writes in one transaction and, on
/// success, closes the dialog and opens the mosaic detail page.
/// </summary>
/// <remarks>
/// Built over data the host has already read, so the dialog issues no query of its own and
/// constructs in tests without a database (spec 18.3). Nothing it holds is stored (spec 12.17,
/// "What is stored and what is not").
/// </remarks>
public sealed partial class CreateMosaicViewModel : ObservableObject, IModalPageViewModel
{
    private readonly Func<string?, Guid?, IReadOnlyList<(DateOnly Date, string? FrameLabel, string Label)>, Guid> _create;
    private readonly Action<Guid> _openMosaic;
    private readonly ILogger _logger;

    /// <inheritdoc />
    public event EventHandler<bool>? CloseRequested;

    /// <param name="targetName">The target's primary name: the subline, and the name prefill's
    /// base when no frame carries a panel token.</param>
    /// <param name="nights">The checked nights, any order.</param>
    /// <param name="frames">Normally <c>MosaicQueries.NightFrames</c> over those nights.</param>
    /// <param name="existing">Normally <c>MosaicQueries.MosaicsIncludingTarget</c>.</param>
    /// <param name="keywords"><c>general.mosaic_keywords</c>, for the token rule of spec 7.7.</param>
    /// <param name="create">Normally <c>MosaicRepository.CreateFromNights</c> bound to the target:
    /// the new name or null, the existing mosaic or null, and the rows. Returns the mosaic id.</param>
    /// <param name="openMosaic">Normally <c>MainWindowViewModel.OpenMosaic</c>.</param>
    /// <param name="logger">Optional. A write that threw is logged.</param>
    public CreateMosaicViewModel(
        string targetName,
        IReadOnlyList<DateOnly> nights,
        IReadOnlyList<NightFrameGroup> frames,
        IReadOnlyList<MosaicLink> existing,
        IReadOnlyList<string> keywords,
        Func<string?, Guid?, IReadOnlyList<(DateOnly Date, string? FrameLabel, string Label)>, Guid> create,
        Action<Guid> openMosaic,
        ILogger? logger = null)
    {
        _create = create;
        _openMosaic = openMosaic;
        _logger = logger ?? NullLogger.Instance;

        Subline = nights.Count == 1 ? $"{targetName}, 1 night" : $"{targetName}, {nights.Count} nights";
        Existing = existing;

        // Spec 12.17's Name rule: the base the most LIGHT frames carry, ties to the ordinally
        // first, the target's primary name when no frame carries a token.
        var prefillBase = frames
            .Select(group => (Base: PanelTokens.Strip(group.ObjectName, keywords), group.Frames))
            .Where(item => item.Base is not null)
            .GroupBy(item => item.Base!, StringComparer.Ordinal)
            .Select(group => (group.Key, Frames: group.Sum(item => item.Frames)))
            .OrderByDescending(item => item.Frames)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Key)
            .FirstOrDefault() ?? targetName;
        Name = nights.Count == 0
            ? prefillBase
            : $"{prefillBase} {MosaicDetection.DateRangeSuffix(nights.Min(), nights.Max())}";

        foreach (var row in frames
                     .GroupBy(group => (group.Night, group.FrameLabel))
                     .OrderByDescending(group => group.Key.Night)
                     .ThenBy(group => group.Key.FrameLabel ?? "", StringComparer.Ordinal))
        {
            var item = new CreateMosaicRowViewModel(row.Key.Night, row.Key.FrameLabel, row.Sum(group => group.Frames));
            item.PropertyChanged += OnRowChanged;
            Rows.Add(item);
        }
    }

    /// <summary>The heading's subline: "&lt;target name&gt;, n nights", "1 night" at one.</summary>
    public string Subline { get; }

    /// <summary>The New mosaic radio, checked on open; false is Add to existing.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool IsNew { get; set; } = true;

    /// <summary>The Name box.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string Name { get; set; }

    /// <summary>The refusal under the Name box, or null.</summary>
    [ObservableProperty]
    public partial string? NameError { get; private set; }

    /// <summary>A refusal of the rows, or a failed write, beside Create; null when none.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The Mosaic combo box: every mosaic with a row of either status naming this target,
    /// by name.</summary>
    public IReadOnlyList<MosaicLink> Existing { get; }

    /// <summary>Whether Add to existing is offered.</summary>
    public bool CanUseExisting => Existing.Count > 0;

    /// <summary>Add to existing's tooltip while it is disabled, null otherwise.</summary>
    public string? ExistingHint => CanUseExisting ? null : "No existing mosaic includes this target.";

    /// <summary>The chosen existing mosaic, or null.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial MosaicLink? SelectedExisting { get; set; }

    /// <summary>The Nights table, newest night first.</summary>
    public ObservableCollection<CreateMosaicRowViewModel> Rows { get; } = [];

    partial void OnNameChanged(string value) => NameError = null;

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => CreateCommand.NotifyCanExecuteChanged();

    private bool CanCreate()
        => Rows.Count > 0
           && Rows.All(row => !string.IsNullOrWhiteSpace(row.Label))
           && (IsNew ? !string.IsNullOrWhiteSpace(Name) : SelectedExisting is not null);

    /// <summary>Spec 12.17's Create: one transaction through the repository, which groups the rows
    /// by trimmed label case insensitively into panels and writes each row with its frames' own
    /// label. A taken name shows under the Name box, any other refusal beside Create.</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        if (!CanCreate())
        {
            return;
        }

        NameError = null;
        Error = null;
        Guid id;
        try
        {
            id = _create(
                IsNew ? Name.Trim() : null,
                IsNew ? null : SelectedExisting!.MosaicId,
                [.. Rows.Select(row => (row.Night, row.FrameLabel, row.Label))]);
        }
        catch (DuplicateMosaicNameException ex)
        {
            NameError = ex.Message;
            return;
        }
        catch (MosaicWriteException ex)
        {
            Error = ex.Message;
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Creating a mosaic from the checked nights failed");
            Error = MosaicMessages.CouldNotSave;
            return;
        }

        CloseRequested?.Invoke(this, true);
        _openMosaic(id);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
