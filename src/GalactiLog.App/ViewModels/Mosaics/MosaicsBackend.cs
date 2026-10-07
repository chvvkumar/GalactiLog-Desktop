using GalactiLog.Core.Mosaics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;
using GalactiLog.Data.Repositories;

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

    /// <summary>Spec 12.17's Remove panel, by panel id. Normally <c>MosaicRepository.RemovePanel</c>,
    /// one transaction.</summary>
    public Action<Guid> RemovePanel { get; init; } = _ => { };

    /// <summary>Normally <c>MosaicRepository.AddPanelWithTarget</c>.</summary>
    public Func<Guid, Guid, string, PanelAddResult> AddPanelWithTarget { get; init; } = (_, _, _) => new PanelAddResult(Guid.NewGuid(), 0, 0);

    // ---- the mosaic detail page (spec 12.17, Phase 18 Task 5) ----------------------------------

    /// <summary>Normally <c>MosaicRepository.SetNotes</c>; a blank value stores null.</summary>
    public Action<Guid, string?> SetNotes { get; init; } = (_, _) => { };

    /// <summary>(panel, target, night, frame label). Normally <c>MosaicRepository.IncludeNight</c>.</summary>
    public Action<Guid, Guid, DateOnly, string?> IncludeNight { get; init; } = (_, _, _, _) => { };

    /// <summary>(panel, target, night, frame label). Normally <c>MosaicRepository.RemoveNight</c>.</summary>
    public Action<Guid, Guid, DateOnly, string?> RemoveNight { get; init; } = (_, _, _, _) => { };

    /// <summary>By panel id. Normally <c>MosaicRepository.IncludeAll</c>.</summary>
    public Func<Guid, int> IncludeAll { get; init; } = _ => 0;

    /// <summary>By mosaic id. Normally <c>MosaicRepository.IncludeAllAvailable</c>.</summary>
    public Func<Guid, int> IncludeAllAvailable { get; init; } = _ => 0;

    /// <summary>(mosaic, from panel, target, night, frame label, new label). Normally
    /// <c>MosaicRepository.IncludeAsNewPanel</c>.</summary>
    public Func<Guid, Guid, Guid, DateOnly, string?, string, Guid> IncludeAsNewPanel { get; init; }
        = (_, _, _, _, _, _) => Guid.NewGuid();

    /// <summary>(panel, target). Normally <c>MosaicRepository.AddTargetNights</c>.</summary>
    public Func<Guid, Guid, int> AddTargetNights { get; init; } = (_, _) => 0;

    /// <summary>By panel id; refuses while the panel has an included row. Normally
    /// <c>MosaicRepository.DeletePanel</c>.</summary>
    public Action<Guid> DeletePanel { get; init; } = _ => { };

    /// <summary>Normally <c>TargetSearchQuery.Search</c>.</summary>
    public Func<string, IReadOnlyList<TargetSearchResult>> SearchTargets { get; init; } = _ => [];

    /// <summary>Normally <c>CustomColumnRepository.List</c>.</summary>
    public Func<IReadOnlyList<CustomColumnDefinition>> CustomColumns { get; init; } = () => [];

    /// <summary>Normally <c>CustomColumnRepository.ValuesForMosaics</c>.</summary>
    public Func<IReadOnlyCollection<Guid>, IReadOnlyList<CustomValueRow>> MosaicValues { get; init; } = _ => [];

    /// <summary>Normally <c>CustomColumnRepository.SetValue</c>. Null draws no custom cell.</summary>
    public Func<Guid, CustomValueKey, string?, CustomWriteResult>? WriteValue { get; init; }

    // ---- the arranger (spec 12.17, Phase 19A) ----------------------------------------------------

    /// <summary>The available filters, the default filter and each panel's best frame per filter
    /// (spec 11.4). Normally <c>PanelFrameQuery.ForMosaic</c>.</summary>
    public Func<Guid, PanelFrameSet> PanelFrames { get; init; }
        = _ => new PanelFrameSet([], null, new Dictionary<Guid, IReadOnlyDictionary<string, BestFrame>>());

    /// <summary>The geometry of the given best frames by image id, ids not found absent (spec 11.6:
    /// one read per frame-set load, shared by the Composite button and the build). Normally
    /// <c>PanelFrameQuery.Geometry</c>.</summary>
    public Func<IReadOnlyCollection<Guid>, IReadOnlyDictionary<Guid, PanelGeometry>> FrameGeometry { get; init; }
        = _ => new Dictionary<Guid, PanelGeometry>();

    /// <summary>(target, label, nights): the best frame of one suggestion entry, for the read-only
    /// preview. Normally <c>PanelFrameQuery.ForSuggestionEntry</c>.</summary>
    public Func<Guid, string, IReadOnlyCollection<DateOnly>, BestFrame?> SuggestionBestFrame { get; init; } = (_, _, _) => null;

    /// <summary>(mosaic, rotation angle, every panel's layout), one transaction. Normally
    /// <c>MosaicRepository.UpdateLayout</c>.</summary>
    public Action<Guid, double, IReadOnlyList<(Guid PanelId, double? X, double? Y, int Rotation, bool FlipH)>> UpdateLayout { get; init; }
        = (_, _, _) => { };

    /// <summary>Builds the slot that shows one frame's thumbnail on a tile, decoded at the tile's
    /// display width (spec 12.17, plan risk 1). <c>AppHost</c> binds it to the thumbnail worker and
    /// cache; a test passes a slot with a stub decode. The default throws, so a test that reaches it
    /// without binding it fails loudly.</summary>
    public Func<string, ThumbnailSlotViewModel> ThumbnailFor { get; init; }
        = _ => throw new InvalidOperationException("No thumbnail worker");

    /// <summary>(message, details): one spec 10.9 <c>mosaic_action_failed</c> row, normally
    /// <c>ActivityRepository.EmitStandalone</c> pinned to <c>user_action</c> and warning.</summary>
    public Action<string, object> EmitActionFailed { get; init; } = (_, _) => { };
}

/// <summary>One entry of the campaign gap select (spec 12.17).</summary>
public sealed record CampaignGapChoice(int Days, string Label)
{
    /// <summary>The seven entries: <see cref="GeneralSettings.MosaicCampaignGapChoices"/> paired
    /// with spec 12.17's labels in the same order.</summary>
    public static IReadOnlyList<CampaignGapChoice> All { get; } =
    [
        .. GeneralSettings.MosaicCampaignGapChoices.Zip(
            ["No grouping", "1 week", "2 weeks", "1 month", "3 months", "6 months", "1 year"],
            (days, label) => new CampaignGapChoice(days, label)),
    ];
}
