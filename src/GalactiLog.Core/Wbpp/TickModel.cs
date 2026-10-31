namespace GalactiLog.Core.Wbpp;

/// <summary>One row of the folder tree: its level, whether it is ticked, and its child rows in display order.</summary>
public sealed record TickNode(FolderLevel Level, bool Ticked, IReadOnlyList<TickNode> Children);

/// <summary>
/// The tick model: turns a tree of ticked and unticked rows into the copy sources of one night.
/// Pure, no I/O, and it assumes nothing about folder names or depth.
/// </summary>
public static class TickModel
{
    /// <summary>
    /// Walks depth first in child order from each root. A ticked row whose parent is unticked, or
    /// which is a root, is one source. Its SkippedFolders are the unticked rows reached from it
    /// through ticked rows only, null when there are none. A ticked row under a ticked row adds
    /// nothing, and the walk continues under unticked rows, so a ticked row under an unticked row
    /// under a source is a source of its own.
    /// </summary>
    public static IReadOnlyList<ChosenLevel> CopySources(DateOnly night, IReadOnlyList<TickNode> roots)
        => [.. roots.SelectMany(r => Sources(night, r, parentTicked: false))];

    private static IEnumerable<ChosenLevel> Sources(DateOnly night, TickNode node, bool parentTicked)
    {
        if (node.Ticked && !parentTicked)
        {
            IReadOnlyList<FolderLevel> skipped = [.. Excluded(node)];
            yield return new ChosenLevel(night, node.Level, skipped.Count == 0 ? null : skipped);
        }
        foreach (var source in node.Children.SelectMany(c => Sources(night, c, node.Ticked)))
            yield return source;
    }

    private static IEnumerable<FolderLevel> Excluded(TickNode ticked)
    {
        foreach (var child in ticked.Children)
        {
            if (!child.Ticked) yield return child.Level;
            else foreach (var level in Excluded(child)) yield return level;
        }
    }
}
