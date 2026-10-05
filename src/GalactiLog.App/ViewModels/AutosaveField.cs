using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels;

/// <summary>
/// A text field that saves itself one second after the user stops typing, port of the web
/// application's <c>useNotesAutosave</c>. Owns three things a caller keeps getting wrong when it
/// hand-rolls them: the debounce window is restarted per keystroke and fires once; the write runs
/// off the UI thread and is chained onto the previous write so two saves cannot interleave into a
/// lost update; and a refresh from the database never clobbers text the user has edited but not
/// yet saved.
/// </summary>
public sealed partial class AutosaveField : ObservableObject, IDisposable
{
    /// <summary>Spec 12.4's "1 second idle debounce". One constant, asserted by a test, so target
    /// notes and session notes cannot drift to different windows.</summary>
    public static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(1);

    private readonly Action<string?> _save;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    // One field-lifetime source every debounce window is linked to, the shape DashboardViewModel
    // established: disposing the field cancels a window that is already open.
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _window;

    // Guards the write chain below. Save is reachable from the UI thread (FlushAsync) and from a
    // thread-pool thread (an elapsed debounce window) at the same time, so the read-modify-write
    // of _chain has to be atomic: without it both writes attach to the same antecedent and run in
    // parallel, which is exactly the lost update the chain exists to prevent (review finding 1).
    private readonly Lock _gate = new();

    // The serialised write chain. Every save is a continuation of the previous one, so two
    // windows that fire close together cannot interleave into a lost update. Same mechanism as
    // TargetListViewModel's column persist chain.
    private Task _chain = Task.CompletedTask;

    // The text of the newest write attached to the chain, null until the first one. FlushAsync
    // compares against it so closing the page during that write joins it instead of queueing a
    // duplicate of the same text.
    private string? _queuedText;

    // The last value adopted FROM the database. Not updated by a save: that is what makes a
    // refresh carrying the pre-save text a no-op instead of a revert.
    private string? _lastAdopted;

    private bool _isDirty;
    private bool _isReseeding;
    private bool _disposed;
    private int _inFlight;

    /// <param name="save">Persists the text. Invoked on a thread-pool thread. Throwing is
    /// expected and handled: the field keeps what is on screen, logs, and surfaces
    /// <see cref="LastFailure"/>.</param>
    /// <param name="delay">The debounce seam, the same shape <c>ScanScheduler</c> and
    /// <c>DashboardViewModel</c> already use and tests already fake.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed save is logged, never rethrown.</param>
    public AutosaveField(
        Action<string?> save,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        _save = save;
        _delay = delay ?? Task.Delay;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        // Assigned through the reseed guard, so constructing the field does not open a debounce
        // window against text nobody typed.
        _isReseeding = true;
        Text = "";
        _isReseeding = false;
    }

    /// <summary>The edited text. Setting it from the view starts the idle window.</summary>
    [ObservableProperty]
    public partial string Text { get; set; }

    /// <summary>Spec 12.4's saving indicator.</summary>
    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    /// <summary>Set when a save threw. The view renders a warning callout; the next successful
    /// save clears it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    public partial Exception? LastFailure { get; private set; }

    /// <summary>Drives the warning callout, so the view binds <c>IsVisible</c> and needs no
    /// converter.</summary>
    public bool HasFailure => LastFailure is not null;

    /// <summary>True while the user has typing that has not been saved. Blocks
    /// <see cref="Reseed"/>.</summary>
    public bool IsDirty => _isDirty;

    /// <summary>The in-flight debounce-and-write, so a test can await it instead of sleeping.
    /// Mirrors <c>TargetListViewModel.PendingPersist</c>.</summary>
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Adopts a value that came from the database. Ignored while <see cref="IsDirty"/> is true,
    /// and ignored when the value equals the last one adopted, so a page refresh that arrives
    /// just after a save cannot revert the field to the pre-save text.
    /// </summary>
    public void Reseed(string? serverValue)
    {
        if (_isDirty || _disposed)
        {
            return;
        }

        if (string.Equals(serverValue ?? "", _lastAdopted ?? "", StringComparison.Ordinal))
        {
            return;
        }

        _lastAdopted = serverValue;
        _isReseeding = true;
        try
        {
            Text = serverValue ?? "";
        }
        finally
        {
            _isReseeding = false;
        }
    }

    /// <summary>
    /// Abandons the edit in progress and puts <paramref name="restore"/> on screen, writing
    /// nothing: the open window is cancelled, the text is assigned through the reseed guard so no
    /// new window opens, the value counts as the last one adopted, and the field is clean again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 20 ruling C19. Escape on a custom column cell, and a cell reverting after the
    /// repository refused its write, both have to undo an edit without saving it. Neither can use
    /// <see cref="Reseed"/>, which is refused while <see cref="IsDirty"/> is true, and that refusal
    /// is exactly what protects typing from a page refresh and must stay. Assigning
    /// <see cref="Text"/> directly is not the answer either: to this field that is an ordinary
    /// keystroke, so it opens a fresh window that later writes the restored value back.
    /// </para>
    /// <para>
    /// The mechanism therefore belongs here, in the one type that owns the dirty flag and the
    /// window, rather than in a consumer guessing at them from outside. It takes the value as an
    /// argument rather than reverting to the last adopted one, because a refusal revert must land
    /// on what the store holds now, which is not the last adopted value after a successful write.
    /// </para>
    /// <para>
    /// A write already attached to the chain is not cancelled, and is not meant to be: it is on its
    /// way to the store and the caller reverts to what the store will hold, not to what it held.
    /// </para>
    /// </remarks>
    internal void Abandon(string restore)
    {
        if (_disposed)
        {
            return;
        }

        _window?.Cancel();
        _isDirty = false;
        _lastAdopted = restore;
        _isReseeding = true;
        try
        {
            Text = restore;
        }
        finally
        {
            _isReseeding = false;
        }
    }

    /// <summary>Saves now, without waiting out the window. Called when the page closes: a note
    /// typed and immediately navigated away from must not be lost. Returns the in-flight write
    /// when the newest text is already being written, so closing during that write joins it
    /// rather than repeating it.</summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            if (!_isDirty)
            {
                return _chain;
            }

            // _isDirty stays true until the write that carried this text completes, so a flush
            // that lands mid-write would otherwise queue the same text a second time.
            var text = Text;
            if (string.Equals(_queuedText, text, StringComparison.Ordinal))
            {
                return _chain;
            }

            // Closes the open window so its own delayed save cannot fire a second time behind
            // this one. The parked delay ends cancelled and its continuation is skipped.
            _window?.Cancel();
            return Attach(text);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _window?.Dispose();
        _lifetime.Dispose();
    }

    // Generated by [ObservableProperty]. A reseed assigns the same property without opening a
    // window: an adopted value is already what the database holds.
    partial void OnTextChanged(string value)
    {
        if (_isReseeding || _disposed)
        {
            return;
        }

        _isDirty = true;
        _window?.Cancel();
        _window?.Dispose();
        _window = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        PendingSave = RunWindowAsync(value, _window.Token);
    }

    private async Task RunWindowAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await _delay(IdleWindow, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A later keystroke, a flush, or Dispose superseded this window.
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await Save(text).ConfigureAwait(false);
    }

    private Task Save(string text)
    {
        lock (_gate)
        {
            return Attach(text);
        }
    }

    // Appends one write to the chain. Must be called under _gate: the read of _chain and the
    // assignment back to it are one operation, and _queuedText records what the newest write
    // carries so FlushAsync can recognise it.
    //
    // Empty text saves as null, matching the web application's notes-or-null rule, so
    // targets.notes returns to null rather than holding an empty string.
    private Task Attach(string text)
    {
        var value = string.IsNullOrEmpty(text) ? null : text;
        _queuedText = text;

        _post(() =>
        {
            _inFlight++;
            IsSaving = true;
        });

        _chain = _chain.ContinueWith(
            _ =>
            {
                Exception? failure = null;
                try
                {
                    _save(value);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    _logger.LogWarning(ex, "An autosave write failed; the text on screen is kept");
                }

                _post(() =>
                {
                    if (--_inFlight == 0)
                    {
                        IsSaving = false;
                    }

                    if (failure is not null)
                    {
                        LastFailure = failure;
                        return;
                    }

                    LastFailure = null;

                    // Only the newest text clears the flag: a keystroke that landed while this
                    // write was in flight has already opened its own window.
                    if (string.Equals(Text, text, StringComparison.Ordinal))
                    {
                        _isDirty = false;
                    }
                });
            },
            TaskScheduler.Default);

        return _chain;
    }
}
