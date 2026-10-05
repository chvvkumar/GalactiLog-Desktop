namespace GalactiLog.App.ViewModels.CustomColumns;

/// <summary>
/// The one owner of a set of custom column cells (ruling C20). A cell holds a debounce window and
/// an <see cref="IDisposable"/>; a surface that held a bare list of them would owe a flush and a
/// dispose at every row recycle, every refresh and every page close, and the fifth surface added
/// later would owe them too and would forget one. A row, a card or a page holds one of these
/// instead, replaces it by disposing the old one, and disposes it when it goes.
/// </summary>
/// <remarks>
/// Not a collection type and not observable: the group is built once per row from
/// <see cref="CustomColumnSet"/>'s answer and replaced wholesale, never added to. A refresh reseeds
/// the cells already in <see cref="Cells"/> rather than rebuilding the group, so the caret and a
/// pending edit survive it. <see cref="CustomCellFactory.Reconcile"/> is what decides between the
/// two, for every surface.
/// </remarks>
public sealed class CustomCellGroup : IDisposable
{
    private bool _disposed;

    /// <param name="cells">The cells, in the order the surface draws them. Materialized once, so a
    /// deferred query is not re-enumerated per flush.</param>
    public CustomCellGroup(IEnumerable<CustomValueViewModel> cells) => Cells = [.. cells];

    /// <summary>The group a surface uses where it has no custom columns, so no caller writes a null
    /// check or builds an empty list of its own. Holds nothing, owns nothing, and cannot be disposed:
    /// see <see cref="Dispose"/>.</summary>
    public static CustomCellGroup Empty { get; } = new([]);

    /// <summary>The cells, in display order.</summary>
    public IReadOnlyList<CustomValueViewModel> Cells { get; }

    /// <summary>Saves every cell now. Called before a page closes, or the last second of typing in
    /// any of them is lost.</summary>
    public Task FlushAsync() => Task.WhenAll(Cells.Select(cell => cell.FlushAsync()));

    /// <summary>
    /// Starts a flush of every cell and then disposes each. Safe to call twice, and a no-op on
    /// <see cref="Empty"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The flush is started and not awaited, because <see cref="IDisposable"/> cannot wait and a
    /// row recycle must not block the UI thread. The order is what makes it safe:
    /// <c>AutosaveField.FlushAsync</c> attaches the pending text to its write chain before this
    /// returns, and disposing cancels only the parked debounce window, never the chain, so the
    /// attached write still lands. A surface that must know the write finished awaits
    /// <see cref="FlushAsync"/> first and disposes after.
    /// </para>
    /// <para>
    /// <see cref="Empty"/> returns early here rather than by a guard at each call site. It is a
    /// process-wide instance, so disposing it would mark it disposed for every surface at once; the
    /// rule was written out at five call sites, remembered at three of them and forgotten at two,
    /// which is design lesson 2 applied to a lifetime rule. Enforced in the type, no caller can get
    /// it wrong whether or not the caller knows the instance is shared.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed || ReferenceEquals(this, Empty))
        {
            return;
        }

        _disposed = true;
        _ = FlushAsync();

        foreach (var cell in Cells)
        {
            cell.Dispose();
        }
    }
}
