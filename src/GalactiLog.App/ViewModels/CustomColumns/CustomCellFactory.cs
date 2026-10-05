using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.CustomColumns;

/// <summary>
/// The one builder and the one reconciler of a custom column cell (spec 12.15). Four surfaces draw
/// cells: the dashboard target row, the dashboard night expander, the Nights ledger row and the
/// session pane's rig line. All four reach their cells through <see cref="Reconcile"/>.
/// </summary>
/// <remarks>
/// <para>
/// Design lesson 1, at the fourth occurrence. Each surface used to carry its own build, its own
/// stored-value lookup and its own answer to the one question that matters on a refresh: rebuild the
/// cells or reseed the ones already on screen. Two surfaces reseeded and two rebuilt, so the same
/// value in the same kind of cell survived a refresh on the Target page and lost the last keystrokes
/// on the dashboard. The decision belongs to one place, beside <see cref="CustomCellGroup"/>, which
/// already owns a cell's lifetime (ruling C20).
/// </para>
/// <para>
/// The rule: a cell is reused when its column and its key are unchanged, because those two are what
/// a cell cannot change without becoming a different cell. <c>CustomValueViewModel.Key</c> is
/// readonly and the editor kind comes from the column's type, so anything else about a refresh is a
/// value to adopt rather than a reason to rebuild. <c>ValueCount</c> is deliberately not compared: it
/// moves on every write a cell itself makes, and comparing it would rebuild the group under the
/// reader's own keystroke.
/// </para>
/// </remarks>
public static class CustomCellFactory
{
    /// <summary>
    /// Brings <paramref name="existing"/> into line with <paramref name="columns"/> and returns the
    /// group the surface should now hold: <paramref name="existing"/> itself when its cells already
    /// draw exactly these columns on this key, with each cell reseeded from
    /// <paramref name="stored"/>, otherwise a replacement, with <paramref name="existing"/> flushed
    /// and disposed.
    /// </summary>
    /// <param name="existing">The group the surface holds now. <see cref="CustomCellGroup.Empty"/>
    /// on a surface that has none yet; it is never disposed, which the group itself enforces.</param>
    /// <param name="columns">The columns this surface draws, in display order, from
    /// <see cref="CustomColumnSet"/>. Empty leaves the surface with no cell.</param>
    /// <param name="key">The value slot every cell in this group writes. Null where the surface
    /// cannot key a value, which is an unresolved <c>obj:</c> group with no target id, and leaves the
    /// surface with no cell rather than one that silently writes nothing.</param>
    /// <param name="subject">What the automation name says after the column name: the target's name
    /// on a target row, the ISO night on a night row, the night and the rig label on a rig row.
    /// </param>
    /// <param name="stored">This slot's stored value for one column, or null when it holds none.
    /// </param>
    /// <param name="write">Normally <c>CustomColumnRepository.SetValue</c>. Null leaves the surface
    /// with no cell, on the same reasoning as a null <paramref name="key"/>.</param>
    /// <param name="delay">The debounce seam, handed to each cell.</param>
    /// <param name="post">How a cell reaches the UI thread.</param>
    /// <param name="logger">Optional. A write that throws is logged by the cell itself.</param>
    /// <returns>The group to hold. Reference equal to <paramref name="existing"/> exactly when the
    /// cells were reseeded in place, which is what tells a caller whether to raise a change.
    /// </returns>
    public static CustomCellGroup Reconcile(
        CustomCellGroup existing,
        IReadOnlyList<CustomColumnDefinition> columns,
        CustomValueKey? key,
        string subject,
        Func<CustomColumnDefinition, string?> stored,
        Func<Guid, CustomValueKey, string?, CustomWriteResult>? write,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(stored);

        // The four guards every surface used to write out for itself. A surface computes its key and
        // hands its columns; whether that adds up to a drawable cell is decided here.
        var drawn = key is null || write is null ? [] : columns;

        if (Reusable(existing.Cells, drawn, key))
        {
            // A refresh reseeds the cells already on screen, so a value half typed into one of them,
            // and its caret, survive a scan-driven reload, a definition republish and a page of rows
            // being reloaded under the row: CustomValueViewModel.Reseed refuses while the field is
            // dirty or while a write of the cell's own is still in flight.
            for (var index = 0; index < drawn.Count; index++)
            {
                existing.Cells[index].Reseed(stored(drawn[index]));
            }

            return existing;
        }

        var replacement = drawn.Count == 0
            ? CustomCellGroup.Empty
            : new CustomCellGroup(drawn.Select(column => new CustomValueViewModel(
                column,
                key!.Value,
                stored(column),
                subject,
                write!,
                delay,
                post,
                logger)));

        existing.Dispose();
        return replacement;
    }

    // Whether the cells on screen already draw exactly these columns on this key. The key is
    // compared because a cell's key is fixed for its life: reseeding one slot's values into cells
    // keyed on another would send the next keystroke to the wrong row. Name, type and options are
    // compared because a rename or an option change has to reach the label and the dropdown.
    private static bool Reusable(
        IReadOnlyList<CustomValueViewModel> cells,
        IReadOnlyList<CustomColumnDefinition> columns,
        CustomValueKey? key)
        => cells.Count == columns.Count
           && (cells.Count == 0 || cells[0].Key == key)
           && cells.Select(cell => cell.Column).Zip(columns).All(pair =>
               pair.First.Id == pair.Second.Id
               && string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
               && pair.First.Type == pair.Second.Type
               && pair.First.Options.SequenceEqual(pair.Second.Options, StringComparer.Ordinal));
}
