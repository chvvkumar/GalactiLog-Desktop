using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Services;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.CustomColumns;

/// <summary>
/// Spec 12.15's one custom column cell, on every surface that draws one: the dashboard target row,
/// the dashboard night expander, the Nights ledger row and the session pane's rig rows. The editor
/// kind comes from the column's type and from nothing else, which is what keeps the four surfaces
/// from drifting apart.
/// </summary>
/// <remarks>
/// <para>
/// The text kind writes through <see cref="AutosaveField"/>, the one debounced, chained,
/// reseed-guarded write field in this solution, rather than through a click-to-edit state machine.
/// This is design lesson 1 applied at the second occurrence: the web hand-rolls a
/// confirm-then-commit dance per cell and carries two comments recording the bugs it grew
/// (<c>InlineEditCell.tsx</c> lines 18 to 27 and 77 to 82). No second debounce and no
/// <c>TimeSpan</c> literal appears in this file.
/// </para>
/// <para>
/// No <c>ConfigureAwait(false)</c> anywhere in this file. Every continuation here reaches an
/// <c>[ObservableProperty]</c> write, which is the shape the Phase 14B Clear log crash established
/// (TRACKING section 5): the publish back to the UI thread is the <c>post</c> seam, never a bare
/// continuation.
/// </para>
/// </remarks>
public sealed partial class CustomValueViewModel : ObservableObject, IDisposable
{
    /// <summary>Spec 12.15: the dropdown's first entry, and the only way a dropdown value is
    /// cleared (departure 3, against the web's own truthiness guard which never clears at all).
    /// </summary>
    public const string NotSet = "Not set";

    private readonly Func<Guid, CustomValueKey, string?, CustomWriteResult> _write;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    // Guards the at-once write chain below, for the reason AutosaveField's own gate states: the
    // read of _pending and the assignment back to it have to be one operation.
    private readonly Lock _gate = new();

    // What the catalogue holds. A refused write reverts to this; a successful one records the value
    // it wrote. Assigned on the UI thread through _post and read on a thread-pool thread inside
    // Write, so it is volatile rather than a plain field.
    private volatile string? _stored;

    // Set while the constructor, a reseed or a refusal revert assigns IsChecked or Selected, so a
    // value this view-model put there itself never queues a write back.
    private bool _suppress;

    // The in-flight commit of the two at-once kinds. The text kind's is AutosaveField's own.
    private Task _pending = Task.CompletedTask;

    private bool _disposed;

    /// <param name="column">The definition. <c>Type</c> chooses the editor kind and nothing else
    /// does.</param>
    /// <param name="key">The value slot this cell writes. Fixed for the cell's life.</param>
    /// <param name="stored">The stored value, or null when the slot holds none.</param>
    /// <param name="subject">What the automation name says after the column name: the target's
    /// name on a target row, the ISO night on a night row, the night and the rig label on a rig
    /// row. Composed by the surface, never here.</param>
    /// <param name="write">Normally <c>CustomColumnRepository.SetValue</c>, bound by AppHost.
    /// Invoked on a thread-pool thread. A delegate rather than the repository so the cell drives in
    /// a unit test with no database (spec 18.3).</param>
    /// <param name="delay">The debounce seam, handed straight to the inner
    /// <see cref="AutosaveField"/>.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A write that throws is logged, never rethrown.</param>
    public CustomValueViewModel(
        CustomColumnDefinition column,
        CustomValueKey key,
        string? stored,
        string subject,
        Func<Guid, CustomValueKey, string?, CustomWriteResult> write,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        Column = column;
        Key = key;
        _stored = stored;
        _write = write;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        Label = column.Name;
        AutomationName = $"{column.Name}, {subject}";

        // The kind comes from the column's type and from nothing else: not from the scope, not from
        // the stored value. CustomColumnDefinition.Type is an enum and the repository never returns
        // a row outside it, so there is no fourth state and no unknown-type branch.
        IsCheckBox = column.Type is CustomColumnType.Boolean;
        IsTextBox = column.Type is CustomColumnType.Text;
        IsComboBox = column.Type is CustomColumnType.Dropdown;

        _suppress = true;
        try
        {
            if (IsTextBox)
            {
                // AutosaveField.Attach turns empty text into null before calling save, and
                // CustomColumnRepository.SetValue deletes on a null value, so an emptied cell
                // deletes its row with no special case here. The two halves meet there; a reader
                // who does not know it will add a third check.
                Text = new AutosaveField(value => Write(value), delay, _post, _logger);
                Text.Reseed(stored);
                Choices = [];
            }
            else if (IsComboBox)
            {
                Choices = [NotSet, .. column.Options];
                Selected = ChoiceFor(stored);
            }
            else
            {
                Choices = [];
                IsChecked = IsTrue(stored);
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>The definition this cell edits.</summary>
    public CustomColumnDefinition Column { get; }

    /// <summary>The value slot this cell writes (spec 5.20's key rule).</summary>
    public CustomValueKey Key { get; }

    /// <summary>The check box kind. Exactly one of the three flags is true.</summary>
    public bool IsCheckBox { get; }

    /// <inheritdoc cref="IsCheckBox"/>
    public bool IsTextBox { get; }

    /// <inheritdoc cref="IsCheckBox"/>
    public bool IsComboBox { get; }

    /// <summary>The check box. Setting it writes at once. Cleared is <c>false</c>, never unset:
    /// spec 12.15's no-third-state rule, which is what makes the filter's No arm answerable.
    /// </summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>The text field. Bound two-way by the text kind and written through
    /// <see cref="AutosaveField"/>; null on the other two kinds.</summary>
    public AutosaveField? Text { get; }

    /// <summary>The combo box entries: <see cref="NotSet"/> then the column's options in order.
    /// Empty on the other two kinds.</summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>The combo box's selection. Setting it to <see cref="NotSet"/> deletes the value.
    /// </summary>
    [ObservableProperty]
    public partial string? Selected { get; set; }

    /// <summary>The column's name: the watermark of the text kind and the first part of
    /// <see cref="AutomationName"/>.</summary>
    public string Label { get; }

    /// <summary>"Priority, NGC 7000". Spec 12.15's screen reader rule. The check box announces its
    /// own checked state and nothing here overrides it with a word of its own.</summary>
    public string AutomationName { get; }

    /// <summary>The refusal sentence while the last write was refused, null otherwise. The editor's
    /// tooltip and its <c>AutomationProperties.HelpText</c>. Nothing here opens a dialog and
    /// nothing pushes a toast: a cell that cannot be written is the cell that has to say so
    /// (spec 12.15).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRefused))]
    public partial string? Refusal { get; private set; }

    /// <summary>Drives the editor's <c>refused</c> class, so the view binds it and needs no
    /// converter.</summary>
    public bool IsRefused => Refusal is not null;

    /// <summary>The in-flight commit, so a case awaits it instead of sleeping. Mirrors
    /// <c>AutosaveField.PendingSave</c>.</summary>
    internal Task PendingWrite => IsTextBox ? Text!.PendingSave : AtOncePending;

    // The two at-once kinds' chain, read under the gate that owns it: a toggle assigns it on the UI
    // thread while a chained write finishes on a pool thread.
    private Task AtOncePending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>Adopts a value that came from the catalogue, through the inner field's own reseed
    /// guard on the text kind, so a page refresh never clobbers typing, and through the same
    /// suppression the constructor uses on the other two, so adopting a value never queues a write.
    /// </summary>
    /// <remarks>
    /// The stored value advances even when the inner field refuses the adoption, and the two halves
    /// do not have to agree (review P3-6): this field tracks what the catalogue holds and the text
    /// on screen tracks what the user typed, and while the user is typing those are two different
    /// things. Nothing compares them any more, now that <see cref="Write"/>'s equality guard is
    /// gone; the only reader is the revert, which has to land on what the catalogue holds now.
    /// </remarks>
    public void Reseed(string? stored)
    {
        if (_disposed)
        {
            return;
        }

        // A write of this cell's own is still on its way to the catalogue, so the value offered here
        // was read before it and is older than what the cell holds. The text kind's own dirty flag
        // says the same thing for the same reason; these two kinds have no dirty flag because they
        // commit on the click, so the in-flight chain is what stands for one. Without this, a reseed
        // driven by another surface's read puts the pre-click state back under the reader's hand and
        // the next click writes what the catalogue already holds.
        if (!IsTextBox && !AtOncePending.IsCompleted)
        {
            return;
        }

        _stored = stored;
        if (IsTextBox)
        {
            Text!.Reseed(stored);
            return;
        }

        _suppress = true;
        try
        {
            if (IsCheckBox)
            {
                IsChecked = IsTrue(stored);
            }
            else
            {
                Selected = ChoiceFor(stored);
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>
    /// Spec 12.15's Escape on the text kind: drops what was typed, puts the stored value back and
    /// writes nothing. The other two kinds commit at once and have nothing to cancel, so this is a
    /// no-op on them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It drops an unsaved edit and closes an open idle window. A write already sent cannot be
    /// recalled, and this does not pretend otherwise: where the idle window had already elapsed, the
    /// typed value is on its way to the catalogue and the cell shows it again as soon as the write
    /// lands, never the pre-edit text. So Escape undoes typing, not a save.
    /// </para>
    /// One call to <see cref="AutosaveField.Abandon"/>, which is the spine that owns the dirty flag
    /// and the open window (ruling C19). An earlier shape of this method assigned the field's text
    /// directly and had <see cref="Write"/> drop any value equal to the stored one; that guard was
    /// a global change of meaning taken for one local problem, and it read a comparand that moves
    /// asynchronously, so it dropped a real write whenever a value returned to the previously
    /// stored one while an earlier write was still in flight (review P1-1). It is gone.
    /// </remarks>
    public void CancelEdit()
    {
        if (_disposed || !IsTextBox)
        {
            return;
        }

        Text!.Abandon(_stored ?? "");
    }

    /// <summary>Saves now, on every kind. Called when the page closes or the row is recycled.
    /// </summary>
    /// <remarks>
    /// Ruling C20 declares the group's flush as every cell, and a check box or a dropdown has an
    /// in-flight write exactly as a text box does: it is on the chain rather than in a debounce
    /// window, so there is nothing to hurry along, but there is something to wait for. Returning a
    /// completed task on those two kinds made the bounded flush at page close and at process exit
    /// return at once for a card whose only pending work was a toggle, so a toggle made against a
    /// locked database could be cut short by the log closing and the process returning.
    /// </remarks>
    public Task FlushAsync() => IsTextBox ? Text!.FlushAsync() : AtOncePending;

    /// <remarks>
    /// Cancels the text kind's parked debounce window. An at-once write already on the chain is
    /// deliberately left to finish (review P3-3): it carries a value the user really set, and
    /// <see cref="Key"/> is readonly and fixed for the cell's life, so a recycled row cannot write
    /// it into another row's slot. What it can no longer do is reach the screen, because the
    /// publish back checks the disposed flag.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Text?.Dispose();
    }

    // Generated by [ObservableProperty]. The check box commits every toggle at once, and stores
    // the literal false rather than deleting: spec 12.15's no-third-state rule.
    partial void OnIsCheckedChanged(bool value)
    {
        if (_suppress || _disposed)
        {
            return;
        }

        WriteAtOnce(value ? CustomColumnSlug.True : CustomColumnSlug.False);
    }

    // Generated by [ObservableProperty]. "Not set" is the only way a dropdown value is cleared, and
    // it sends null, which the repository turns into a delete (departure 3).
    partial void OnSelectedChanged(string? value)
    {
        if (_suppress || _disposed)
        {
            return;
        }

        WriteAtOnce(value is null or NotSet ? null : value);
    }

    // The two at-once kinds commit on the click that made the change, and the delegate is a
    // synchronous SQLite write, so it must not run on the thread that clicked. The text kind
    // reaches Write from AutosaveField's own chain, which is already off the UI thread.
    //
    // Chained rather than dispatched, for the reason AutosaveField.cs states over its own chain:
    // two changes inside one write's duration would otherwise open two transactions over the same
    // slot, land in an undefined order and let the older one win (review P2-1). The read of
    // _pending and the assignment back to it are one operation under the gate, because a toggle
    // arrives on the UI thread while a chained write is finishing on a pool thread.
    private void WriteAtOnce(string? value)
    {
        lock (_gate)
        {
            _pending = _pending.ContinueWith(_ => Write(value), TaskScheduler.Default);
        }
    }

    // The only site that calls the write delegate and the only site that sets Refusal. Two call
    // sites would be two chances to forget the refusal handling.
    private void Write(string? value)
    {
        var accepted = false;
        string? refusal;
        try
        {
            var result = _write(Column.Id, Key, value);
            accepted = result.Ok;

            // A refused result with no message would revert the cell in silence: no error ink, no
            // tooltip, nothing announced. Two statuses carry a null message by design, because each
            // was a programming error when it was written and neither is one now: a column the
            // catalogue no longer holds is reachable from the dashboard after a database reset, and
            // the key-shape refusal is reachable from any surface that composes a key wrongly. The
            // fallback is here, at the one site that sets Refusal, so no status added later can be
            // silent either.
            refusal = result.Ok
                ? null
                : result.Message
                  ?? (result.Status is CustomWriteStatus.ColumnNotFound
                      ? CustomColumnMessages.ColumnGone
                      : CustomColumnMessages.CouldNotSave);
        }
        catch (Exception ex)
        {
            // A write that threw leaves the cell exactly where a refusal does: the stored value
            // back on screen with a sentence on it. AutosaveField takes the same view of its own
            // save delegate, which is why nothing is rethrown here.
            //
            // The exception's own message never reaches the screen. Refusal is bound to a tooltip
            // and to AutomationProperties.HelpText, so `SQLite Error 5: 'database is locked'` would
            // be drawn in a cell and read aloud by a screen reader. The detail goes to the log; the
            // reader gets the one approved sentence, from the same file as every other one.
            _logger.LogWarning(ex, "A custom column value write failed; the cell keeps the stored value");
            refusal = CustomColumnMessages.CouldNotSave;
        }

        _post(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (accepted)
            {
                _stored = value;
                Refusal = null;

                // The cell shows what the catalogue holds. On the ordinary path this is already on
                // screen and the inner field refuses the adoption anyway, because the value is
                // still being typed and the field is dirty. It matters on one path: Escape after
                // this write was sent and before it landed put the pre-edit text on screen and left
                // the field clean, and the write could not be recalled, so the screen disagreed with
                // the store until the next page load. The field is clean there, so this adopts.
                if (IsTextBox)
                {
                    Text!.Reseed(value);
                }

                return;
            }

            Refusal = refusal;
            RevertToStored();
        });
    }

    // Puts the stored value back through the same suppression the constructor uses, so the revert
    // does not queue a second write.
    private void RevertToStored()
    {
        if (IsTextBox)
        {
            CancelEdit();
            return;
        }

        _suppress = true;
        try
        {
            if (IsCheckBox)
            {
                IsChecked = IsTrue(_stored);
            }
            else
            {
                Selected = ChoiceFor(_stored);
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    private static bool IsTrue(string? stored)
        => string.Equals(stored, CustomColumnSlug.True, StringComparison.Ordinal);

    // A stored value outside the column's options can only come from a hand-edited catalogue; it
    // renders as "Not set" rather than as a blank combo box with no matching entry.
    private string ChoiceFor(string? stored)
        => stored is not null && Choices.Contains(stored, StringComparer.Ordinal) ? stored : NotSet;
}
