using GalactiLog.App.Services;

namespace GalactiLog.App.ViewModels.Diagnostics;

/// <summary>
/// One configured scan root as spec 12.8's Scan group shows it: the path, plus whether a watcher
/// is attached and whether the directory is reachable right now.
/// </summary>
/// <remarks>
/// A projection of <see cref="WatcherRootState"/> and nothing more. The state itself is read once
/// per refresh by <c>DiagnosticsService.Snapshot</c>, which is the one reader of
/// <c>WatcherService.DescribeRoots</c>; this type never touches the watcher.
/// </remarks>
/// <param name="Root">The configured root, in <c>general.scan_roots</c> order.</param>
/// <param name="StatusText">The two flags as one sentence, so the row needs no converter.</param>
/// <param name="Watching">Whether a watcher is attached to this root.</param>
/// <param name="Reachable">Whether the directory exists right now.</param>
public sealed record WatcherRootViewModel(string Root, string StatusText, bool Watching, bool Reachable)
{
    /// <summary>Builds the display row for one root.</summary>
    public static WatcherRootViewModel From(WatcherRootState state) => new(
        state.Root,
        Describe(state),
        state.Watching,
        state.Reachable);

    private static string Describe(WatcherRootState state) => (state.Watching, state.Reachable) switch
    {
        (true, true) => "watching",
        (true, false) => "watching, directory unreachable",
        (false, true) => "not watching",
        (false, false) => "not watching, directory unreachable",
    };
}
