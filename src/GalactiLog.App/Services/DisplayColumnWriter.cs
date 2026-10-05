using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.Services;

/// <summary>
/// The one writer of the <c>display</c> document (design-spec 5.8.2): its column lists through
/// <see cref="Write(string, IReadOnlyList{string})"/> and every other key through
/// <see cref="Write(Func{DisplaySettings, DisplaySettings})"/>, which P13 R5's
/// <c>target_page</c> disclosures use. Each write is a load-modify-save of the whole document, so a
/// per-view-model chain would let a dashboard write, a frame-table write and a section toggle
/// interleave and drop one of them. This owns a single chain for the process.
/// <para>
/// The name is narrower than the behaviour: it writes the whole display document, not only
/// <c>display.columns</c>. The rename to <c>DisplaySettingsWriter</c> is a Phase 14 candidate
/// (fixer-list section 2 item 2), so the second overload is not a mistake to be corrected back out
/// (phase review P3-4).
/// </para>
/// <para>
/// Extracted out of <c>TargetListViewModel</c>'s private <c>_persist</c> field, whose behaviour it
/// reproduces exactly: the keys are captured on the calling thread so a queued write records the
/// state of the click that queued it, the write runs off the UI thread, a failure is logged and
/// dropped because the click has already changed what is on screen, and the next click tries
/// again.
/// </para>
/// <para>
/// Ruling Q18: a small concrete type over one document, not a generic settings-document writer.
/// The same shape as <see cref="GraphSettingsWriter"/>, which is the other document with a single
/// writer, and ruling Q5 gave this one the same general overload that writer already had rather
/// than a second writer type over the same document.
/// </para>
/// </summary>
/// <param name="getDisplay">Normally <c>SettingsStore.GetDisplay</c>. Called inside the queued
/// write, never on the calling thread, so the load-modify-save reads the document as it is at
/// write time and a concurrent write to another table's entry survives.</param>
/// <param name="saveDisplay">Normally <c>SettingsStore.SaveDisplay</c>, which round-trips
/// <c>DisplaySettings.ExtensionData</c>, so an unrecognized key is preserved (design-spec 5.8).
/// The only write either table performs, and it goes to the database, never to a file.</param>
public sealed class DisplayColumnWriter(
    Func<DisplaySettings> getDisplay,
    Action<DisplaySettings> saveDisplay,
    ILogger? logger = null)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string[]> _lastWritten = new(StringComparer.Ordinal);
    private Task _persist = Task.CompletedTask;

    /// <summary>The tail of the write chain, so a test can await it instead of sleeping. Nothing
    /// in the application awaits it: a column toggle must not wait on SQLite.</summary>
    public Task Pending
    {
        get { lock (_gate) { return _persist; } }
    }

    /// <summary>
    /// Raised with the table id and the ordered visible-key list every time a write is queued, so
    /// a second live table over the same table id learns of the first one's toggle instead of
    /// keeping the list it was built with (phase review item 1). Without it, table B's next toggle
    /// rewrites the document from B's stale list and reverts table A's hide.
    /// <para>
    /// Raised inside <see cref="Write"/> under the same gate that records
    /// <see cref="LastWritten"/>, so a handler can never observe a key list older than the one the
    /// writer has already recorded. Write is called from the UI thread (a column-picker click), so
    /// handlers run there too; a handler must therefore be a cheap in-memory update and must not
    /// call back into <see cref="Write"/>.
    /// </para>
    /// </summary>
    public event Action<string, string[]>? Changed;

    /// <summary>
    /// Queues a write of one table's ordered visible-key list. The keys are copied here, on the
    /// calling thread, so the queued write records the state of the click that queued it rather
    /// than whatever is on screen when it runs.
    /// </summary>
    public void Write(string tableId, IReadOnlyList<string> orderedVisibleKeys)
    {
        string[] keys = [.. orderedVisibleKeys];

        lock (_gate)
        {
            _lastWritten[tableId] = keys;
            _persist = _persist.ContinueWith(_ => Run(tableId, keys), TaskScheduler.Default);
            Changed?.Invoke(tableId, keys);
        }
    }

    /// <summary>
    /// Queues a load-modify-save of the whole display document, for the keys that are not columns
    /// (P13 R5's <c>target_page</c>). Same chain, same thread rules and same failure handling as
    /// the column write above, because two chains over one document lose updates: a column toggle
    /// and a section toggle would each read the document, replace their own half and save, and
    /// whichever saved second would drop the other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mutation is applied to the document as loaded inside the queued write rather than to a
    /// snapshot taken now, which is the rule <c>GraphSettingsWriter.Write</c> follows and the
    /// reason a nested <c>with</c> expression over <c>TargetPage</c> cannot clobber a concurrent
    /// write to another key of the same object.
    /// </para>
    /// <para>
    /// It raises no <see cref="Changed"/> event: that event carries a table id and a key list, and
    /// nothing written through here has either.
    /// </para>
    /// <para>
    /// The type's name is now narrower than what it writes. Renaming it to
    /// <c>DisplaySettingsWriter</c> touches <c>AppHost</c>, three view-models and their tests,
    /// which is churn this phase did not budget; it is a Phase 14 candidate and is recorded as one.
    /// </para>
    /// </remarks>
    /// <param name="mutate">Applied to the document as loaded inside the queued write.</param>
    public void Write(Func<DisplaySettings, DisplaySettings> mutate)
    {
        lock (_gate)
        {
            _persist = _persist.ContinueWith(_ => Run(mutate), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// The keys this process last queued for a table, or null if it has not written that table
    /// yet. A table built after another one toggled a column must start from this rather than
    /// from the display document <c>AppHost</c> read at startup, which is a snapshot and is stale
    /// the moment anything writes (review finding 1): otherwise the new table shows a column the
    /// user hid, and its own first toggle rewrites the list from the snapshot and silently
    /// reverts the hide.
    /// <para>
    /// Recorded at queue time, not at write time, so a table constructed while a write is still
    /// in flight sees the intended state rather than the last one that reached SQLite. Reading it
    /// is not a settings read: nothing here touches the database, which is what keeps it legal on
    /// a view-model construction path (phase review item 2).
    /// </para>
    /// </summary>
    public string[]? LastWritten(string tableId)
    {
        lock (_gate)
        {
            return _lastWritten.TryGetValue(tableId, out var keys) ? [.. keys] : null;
        }
    }

    // Phase 24 R5 (ruling R9): the width memo, the same shape as _lastWritten and for the same
    // reason. A null entry is a width this process cleared back to auto-fit.
    private readonly Dictionary<string, Dictionary<string, double?>> _lastWrittenWidths = new(StringComparer.Ordinal);

    /// <summary>Queues a load-modify-save of one column's stored width, null returning that column
    /// to auto-fit, and records it so a table built afterwards starts from it rather than from the
    /// startup snapshot (<see cref="LastWritten"/>'s reason).</summary>
    public void WriteWidth(string tableId, string columnKey, double? width)
    {
        lock (_gate)
        {
            if (!_lastWrittenWidths.TryGetValue(tableId, out var table))
            {
                table = new Dictionary<string, double?>(StringComparer.Ordinal);
                _lastWrittenWidths[tableId] = table;
            }

            table[columnKey] = width;
            _persist = _persist.ContinueWith(
                _ => Run(display => display.WithColumnWidth(tableId, columnKey, width)),
                TaskScheduler.Default);
        }
    }

    /// <summary>The widths this process has queued for a table, a null value meaning cleared,
    /// empty when it has written none. A table overlays this on the document it was handed.</summary>
    public IReadOnlyDictionary<string, double?> LastWrittenWidths(string tableId)
    {
        lock (_gate)
        {
            return _lastWrittenWidths.TryGetValue(tableId, out var table)
                ? new Dictionary<string, double?>(table, StringComparer.Ordinal)
                : new Dictionary<string, double?>(StringComparer.Ordinal);
        }
    }

    private void Run(Func<DisplaySettings, DisplaySettings> mutate)
    {
        try
        {
            saveDisplay(mutate(getDisplay()));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "Persisting the display document failed; what is on screen is unchanged");
        }
    }

    private void Run(string tableId, string[] keys)
    {
        try
        {
            var display = getDisplay();
            var columns = new Dictionary<string, string[]>(display.Columns)
            {
                [tableId] = keys,
            };

            saveDisplay(display with { Columns = columns });
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "Persisting the {TableId} column list failed; the columns on screen are unchanged",
                tableId);
        }
    }
}
