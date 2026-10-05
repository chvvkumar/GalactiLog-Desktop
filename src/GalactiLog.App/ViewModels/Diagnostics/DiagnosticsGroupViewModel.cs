using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GalactiLog.App.ViewModels.Diagnostics;

/// <summary>
/// One of spec 12.8's seven field groups: a title, its labelled readouts, and whether the user
/// has it expanded.
/// </summary>
/// <remarks>
/// A refresh replaces the contents of <see cref="Fields"/> and <see cref="WatcherRoots"/> in
/// place. The group list itself is built once, so the expander state a user has set survives
/// every refresh.
/// </remarks>
public sealed partial class DiagnosticsGroupViewModel(string title) : ObservableObject
{
    /// <summary>The group name, exactly as spec 12.8's table words it. It is also this group's
    /// key: it is what the group is constructed from and the only thing that tells one group from
    /// another.</summary>
    public string Title { get; } = title;

    /// <summary>
    /// Spec 12.12's help topic for this group, derived from the group key rather than stored, so
    /// the seven group headers keep one <c>DataTemplate</c> and one markup site (ruling Q2).
    /// </summary>
    public string HelpTopicId => TopicIdFor(Title);

    /// <summary>
    /// Every topic id <see cref="TopicIdFor"/> can produce, in spec 12.8's group order. The help
    /// placement census reads this rather than restating the seven ids, because two copies of a
    /// list drift.
    /// </summary>
    public static IReadOnlyList<string> HelpTopicIds { get; } =
        [.. DiagnosticsViewModel.GroupTitles.Select(TopicIdFor)];

    /// <summary>
    /// The help topic id for a group key. Total on purpose and throwing on an unknown key: a
    /// Phase 15 diagnostics group is then a compile-time decision rather than a silently missing
    /// glyph.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The key is not one of spec 12.8's seven
    /// groups.</exception>
    public static string TopicIdFor(string groupKey) => groupKey switch
    {
        "Database" => "diagnostics.database",
        "Scan" => "diagnostics.scan",
        "Resolver" => "diagnostics.resolver",
        "Unresolved" => "diagnostics.unresolved",
        "Errors" => "diagnostics.errors",
        "Versions" => "diagnostics.versions",
        "Paths" => "diagnostics.paths",
        _ => throw new ArgumentOutOfRangeException(
            nameof(groupKey),
            groupKey,
            "A diagnostics group carries a help topic. Add its id to HelpTopics and to this "
            + "switch, and the placement census will then require it to be placed."),
    };

    /// <summary>The group's labelled readouts, in spec order.</summary>
    public ObservableCollection<DiagnosticsFieldViewModel> Fields { get; } = [];

    /// <summary>The Scan group's per-root watcher rows. Empty for the other six groups, which is
    /// what keeps the group template generic rather than a switch over seven shapes.</summary>
    public ObservableCollection<WatcherRootViewModel> WatcherRoots { get; } = [];

    /// <summary>
    /// A whole view-model the group hosts below its fields, or null. The Unresolved group uses it
    /// to host the shared <c>UnresolvedNamesViewModel</c>, so spec 9.7's retry button on this page
    /// is the existing one and not a second entry point into <c>UnresolvedRetry</c>.
    /// </summary>
    public object? Content { get; init; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}
