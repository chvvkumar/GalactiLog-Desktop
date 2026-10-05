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
