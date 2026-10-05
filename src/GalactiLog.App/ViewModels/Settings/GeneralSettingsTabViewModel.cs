using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Services;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>
/// The spine every Settings tab that edits the <c>general</c> document sits on: the first read,
/// the publish, the serialized write chain, the immediate-save shape with its roll-back, and the
/// follow of another writer's <c>GeneralChanged</c>.
/// </summary>
/// <remarks>
/// <para>
/// Design-lessons rule 1. Phase 9 Task 5 wrote this machinery once for the Library tab; Task 6
/// needs it three more times (Location, Display, Storage) and Task 9's wizard is the fifth
/// surface. Three copies of a write chain is how four incompatible retry schemes happen, so the
/// spine is extracted here and the three new tabs are built on it. <c>LibraryTabViewModel</c> is
/// not migrated onto it by this task: it is Task 5's file, it carries a dirty filter block and a
/// scan subscription this base knows nothing about, and moving it is a refactor the fixer should
/// make with that tab's own tests in front of it.
/// </para>
/// <para>
/// Every collaborator is a delegate rather than a <c>SettingsStore</c>, so a tab constructs in a
/// unit test with lambdas and no database (design-spec 18.3). Every write goes through
/// <c>SettingsStore.MutateGeneral</c>, which reads, mutates, validates and writes the whole
/// document in one critical section: a <c>GetGeneral</c> plus <c>SaveGeneral</c> pair would leave
/// a window in which another writer of the same document is lost (Task 5 review escalation).
/// </para>
/// <para>
/// <c>SettingsStore.ValidateGeneral</c> stays the enforcement point. Whatever inline validation a
/// derived tab does is a usability layer in front of it (design-lessons rule 2), and every save
/// path here still catches <see cref="SettingsValidationException"/> as the backstop.
/// </para>
/// <para>
/// <strong>The publish contract, which every derived tab is bound by.</strong> A publish is any
/// copy of stored state into the controls: <see cref="Publish(Action)"/>, and through it
/// <see cref="ApplyDocument"/>, <see cref="Apply"/>, the continuation of <see cref="Load"/>, the
/// <c>GeneralChanged</c> follow, and the roll-back half of <see cref="ImmediateSave"/>. All of
/// them are <strong>serialized</strong>: each arrives through the injected post seam, and the seam
/// must deliver them one at a time, so no two publishes ever overlap. <c>UiPost.Default</c>
/// satisfies this by construction, because the dispatcher runs its queue on one thread; an inline
/// seam (<c>action =&gt; action()</c>, which every unit test here uses) satisfies it only as long
/// as the tab starts <strong>one</strong> background pass. That is why a tab needing a second
/// document overrides <see cref="ReadCompanionDocuments"/> rather than starting a second
/// <c>Task.Run</c> of its own: two parallel reads under an inline seam publish concurrently, and
/// the guard state below is not a lock.
/// </para>
/// <para>
/// Task 6 review finding 2 is what that paragraph is paying for: the guard used to be a
/// save-and-restore of a plain bool, two overlapping publishes left it stuck true, and every
/// later save on the tab was silently dropped. The guard is now a depth counter and
/// <see cref="Publish(Action)"/> carries a <c>Debug.Assert</c> that fires the moment two publishes
/// overlap, so a future tab that breaks the contract fails loudly in a debug build instead of
/// going quiet in a release one. Task 9's wizard is the fifth surface on this type and inherits
/// the whole paragraph.
/// </para>
/// </remarks>
public abstract partial class GeneralSettingsTabViewModel : ObservableObject, IDisposable
{
    private readonly Func<GeneralSettings> _load;
    private readonly Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> _mutate;
    private readonly Action<EventHandler<GeneralSettings>>? _unsubscribeGeneralChanged;
    private readonly Action<Action> _post;

    // One tab-lifetime source every background window is linked to, the shape the Library tab
    // established: disposing the tab cancels a read that is still parked.
    private readonly CancellationTokenSource _lifetime = new();

    // The write chain, in the shape GraphSettingsWriter and the Library tab already use: every
    // save is queued behind the previous one so two controls changed in quick succession cannot
    // interleave into a lost update, and each one re-reads the document inside the queued write
    // rather than mutating a snapshot taken when the user clicked.
    private readonly Lock _writeGate = new();
    private Task _persist = Task.CompletedTask;

    // Only the newest read may write to the bindings.
    private int _generation;

    // The thread currently inside MutateGeneral, or 0. The store raises GeneralChanged
    // synchronously on the thread that wrote, so a notification arriving on this thread is this
    // tab's own write coming back and must not be treated as a change made elsewhere. The chain
    // runs one write at a time, so one thread id is enough.
    private int _writingThreadId;

    // A depth counter, not a flag, and moved through Interlocked. Task 6 review finding 2: a
    // save-and-restore of a plain bool can be interleaved by two publishers so that the inner
    // one's finally restores `true`, and IsApplying then stays true for the life of the tab, which
    // silently suppresses every later save. A counter cannot stick: whatever order two
    // increments and two decrements land in, it returns to zero.
    private int _applyDepth;

    // 1 while a publish is running. Purely a debug tripwire for the single-publisher contract in
    // this type's remarks; nothing in the release path reads it.
    private int _publishing;

    private bool _ready;
    private bool _disposed;

    /// <param name="load">Normally <c>SettingsStore.GetGeneral</c>. Called off the UI thread.
    /// </param>
    /// <param name="mutateGeneral">Normally <c>SettingsStore.MutateGeneral</c>. Called off the UI
    /// thread, inside the write chain.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A failed read or write is logged, never rethrown on the UI
    /// thread.</param>
    /// <param name="subscribeGeneralChanged">Normally
    /// <c>handler =&gt; settingsStore.GeneralChanged += handler</c>. Each tab is a DI singleton
    /// whose state outlives one visit to the Settings page, and the other tabs and Task 9's wizard
    /// write the same document, so without this a tab shows a stale value indefinitely and its
    /// next save writes it back (Task 5 review finding I3). Null leaves the tab on its single
    /// constructor read, which is what a unit test that is not about external changes wants.
    /// </param>
    /// <param name="unsubscribeGeneralChanged">The matching detach, called from
    /// <see cref="Dispose"/>.</param>
    protected GeneralSettingsTabViewModel(
        Func<GeneralSettings> load,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> mutateGeneral,
        Action<Action>? post = null,
        ILogger? logger = null,
        Action<EventHandler<GeneralSettings>>? subscribeGeneralChanged = null,
        Action<EventHandler<GeneralSettings>>? unsubscribeGeneralChanged = null)
    {
        _load = load;
        _mutate = mutateGeneral;
        _post = post ?? UiPost.Default;
        Logger = logger ?? NullLogger.Instance;
        _unsubscribeGeneralChanged = unsubscribeGeneralChanged;

        // Subscribed before the first read, so a write that lands while that read is in flight is
        // not missed. The generation check in Publish is what keeps the two from fighting.
        subscribeGeneralChanged?.Invoke(OnGeneralChanged);
    }

    /// <summary>True while the first read is in flight. The view shows one line and no controls,
    /// so a slow read does not look like an empty configuration.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool IsLoading { get; private set; }

    /// <summary>A read that threw. The tab renders one neutral line in place of its controls
    /// (design-spec 12.10: a failure is reported, never a silent default). Carries no exception
    /// text; the log has that.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool LoadFailed { get; private set; }

    /// <summary>
    /// Whether the editor may be shown at all. Every section of every derived tab binds it.
    /// </summary>
    /// <remarks>
    /// Task 5 review finding I1: a live editor over a document that failed to read shows default
    /// values, and one edit would write those defaults over whatever the user actually had. A
    /// failed read is exactly the state in which a tab knows least about what it would be
    /// overwriting, so the editor is not offered at all.
    /// </remarks>
    public bool IsReady => !IsLoading && !LoadFailed;

    /// <summary>The last error from a save, shown on the tab. Cleared by the next successful
    /// save.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    public partial string? ErrorMessage { get; protected set; }

    /// <summary>The last success line, the port of the web's toasts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string? StatusMessage { get; protected set; }

    public bool HasErrorMessage => ErrorMessage is not null;

    public bool HasStatusMessage => StatusMessage is not null;

    /// <summary>The document as it was last read or last written.</summary>
    protected GeneralSettings Saved { get; private set; } = new();

    /// <summary>True while a read document is being copied into the controls. Every change
    /// handler checks it, so seeding a control is not mistaken for a user gesture.</summary>
    protected bool IsApplying => Volatile.Read(ref _applyDepth) > 0;

    /// <summary>False until the first successful read has published. Nothing may save before the
    /// tab knows what is actually stored.</summary>
    /// <remarks><see cref="PublishGeneral"/> is the only writer of it.</remarks>
    protected bool IsReadyToSave => _ready;

    protected ILogger Logger { get; }

    /// <summary>The tab-lifetime token every background read is linked to.</summary>
    protected CancellationToken Lifetime => _lifetime.Token;

    /// <summary>The in-flight read, so a test awaits it instead of sleeping.</summary>
    internal Task? PendingLoad { get; private set; }

    /// <summary>The tail of the write chain, so a test awaits a save instead of sleeping.
    /// </summary>
    internal Task PendingWrite
    {
        get { lock (_writeGate) { return _persist; } }
    }

    /// <summary>Whether <see cref="Dispose"/> has run. Read by the lazy-tab tests.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>Copies a read document into the tab's controls. Always called inside
    /// <see cref="Apply"/>, so no change handler mistakes it for a user gesture.</summary>
    protected abstract void ApplyDocument(GeneralSettings general);

    /// <summary>
    /// Whether the tab holds an edit that a reload would throw away. The default is false, which
    /// is right for a tab whose every control saves immediately; the Display tab overrides it for
    /// its metric-group block, which is the one part behind a Save button.
    /// </summary>
    protected virtual bool HasPendingEdits => false;

    /// <summary>Called instead of a reload when <see cref="HasPendingEdits"/> is true. The edit is
    /// neither overwritten nor discarded; the tab says so.</summary>
    protected virtual void OnStoredDocumentChangedElsewhere()
    {
    }

    /// <summary>Extra teardown for a derived tab. Called once, after the base has cancelled its
    /// lifetime and detached its settings subscription.</summary>
    protected virtual void DisposeCore()
    {
    }

    /// <summary>Reaches the UI thread through the injected seam.</summary>
    protected void Post(Action action) => _post(action);

    /// <summary>
    /// Runs a seeding mutation with the user-gesture guard set. Nestable: the guard is a depth
    /// counter, so an inner <see cref="Apply"/> cannot clear an outer one's flag.
    /// </summary>
    /// <remarks>Publish-thread only. See the type's remarks for the contract.</remarks>
    protected void Apply(Action mutate)
    {
        Interlocked.Increment(ref _applyDepth);
        try
        {
            mutate();
        }
        finally
        {
            Interlocked.Decrement(ref _applyDepth);
        }
    }

    /// <summary>
    /// Runs one publish: a whole document copied into the controls, inside <see cref="Apply"/>.
    /// Every publish a derived tab performs goes through this, so the single-publisher contract
    /// has one enforcement point rather than a convention each tab remembers.
    /// </summary>
    /// <remarks>
    /// The <c>Debug.Assert</c> is the tripwire for that contract (Task 6 review finding 2). It
    /// fires when a second publish starts while one is still running, which is what an unordered
    /// pair of background reads produces under an inline post seam and what
    /// <c>UiPost.Default</c> makes impossible in production. It is a debug check on purpose: the
    /// release path must not pay for it, and a release build degrades to the depth counter above,
    /// which is already safe against the corruption the assert catches.
    /// </remarks>
    protected void Publish(Action publish)
    {
        var alreadyPublishing = Interlocked.CompareExchange(ref _publishing, 1, 0) != 0;
        Debug.Assert(
            !alreadyPublishing,
            $"{GetType().Name} published from two places at once. Publishes are serialized by "
            + "contract: every one of them goes through the post seam, and the seam delivers them "
            + "one at a time. A tab that reads a second document must read it on the same "
            + "background pass (see ReadCompanionDocuments), never as a second parallel read.");

        try
        {
            Apply(publish);
        }
        finally
        {
            Volatile.Write(ref _publishing, alreadyPublishing ? 1 : 0);
        }
    }

    /// <summary>Starts the background read of the general document. A derived constructor calls
    /// this last, once its controls exist.</summary>
    protected void Load()
    {
        if (_disposed)
        {
            return;
        }

        IsLoading = true;
        var generation = ++_generation;
        var token = _lifetime.Token;
        PendingLoad = Task.Run(
            () =>
            {
                try
                {
                    var general = _load();

                    // Task 6 review finding 1: whatever else the tab needs is read here, on this
                    // same pass, and published in the same post. A derived tab that started a
                    // second parallel read would produce two overlapping publishes, which is the
                    // race the contract above forbids.
                    var companions = ReadCompanionDocuments();
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _post(() => PublishGeneral(generation, general, companions));
                }
                catch (OperationCanceledException)
                {
                    // The tab went away while the read was in flight.
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Reading the general settings for a Settings tab failed");
                    _post(() => PublishGeneral(generation, null, null));
                }
            },
            token);
    }

    /// <summary>
    /// Reads any document this tab needs besides <c>general</c>, on the background thread
    /// <see cref="Load"/> already owns, and returns what to apply once the read has landed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs off the publish thread, so it must not touch a binding; the returned action runs on
    /// the publish thread inside <see cref="Apply"/>, so it must not perform I/O. Returning null
    /// means the tab needs nothing else, which is the default.
    /// </para>
    /// <para>
    /// An override owns its own failures: it catches, logs, and returns an action that puts the
    /// tab into whatever failed state it defines. Letting an exception out of here fails the
    /// general read as well, which is almost never what a companion document's failure means.
    /// </para>
    /// </remarks>
    protected virtual Action? ReadCompanionDocuments() => null;

    /// <summary>
    /// The optimistic immediate save the web performs on every control that has no Save button:
    /// the control changes first, the write follows, and a failure rolls the control back and
    /// reports why. A control that stays changed after a refused save is a lie.
    /// </summary>
    /// <param name="mutate">Applied to the stored document inside the store's own gate.</param>
    /// <param name="successMessage">The status line on success.</param>
    /// <param name="rollBack">Restores the control. Run inside <see cref="Apply"/>, so the
    /// roll-back cannot queue a second save of its own.</param>
    protected void ImmediateSave(Func<GeneralSettings, GeneralSettings> mutate, string successMessage, Action rollBack)
    {
        // Publish-thread only, like every other member on the publish path: a gesture arriving
        // while a publish is seeding the controls is that publish, not the user.
        if (!_ready || IsApplying || _disposed)
        {
            return;
        }

        _ = Write(
            mutate,
            onSuccess: () => StatusMessage = successMessage,
            onFailure: () =>
            {
                StatusMessage = null;
                Apply(rollBack);
            });
    }

    /// <summary>
    /// The same optimistic immediate save as
    /// <see cref="ImmediateSave(Func{GeneralSettings, GeneralSettings}, string, Action)"/>, with
    /// one addition: <paramref name="preWrite"/> runs first, inside the queued write chain but
    /// entirely before <c>SettingsStore.MutateGeneral</c> is even called, so it never runs while
    /// that method's process-wide write gate is held (Phase 11 Task 3 review, Important 1).
    /// </summary>
    /// <param name="preWrite">Runs first, off the UI thread, on this tab's own write chain. A
    /// caller with a non-local effect that must agree with the key it is about to save (a COM
    /// call, a filesystem write) belongs here rather than inside <paramref name="mutate"/>:
    /// <paramref name="mutate"/> still runs inside <c>SettingsStore.MutateGeneral</c>'s write
    /// gate, whose own contract says that callback must be pure and fast. Throwing here is caught
    /// exactly where a thrown <paramref name="mutate"/> would be, so the document is never read,
    /// mutated or saved and the normal failure path (roll back, report why) runs unchanged.
    /// </param>
    /// <param name="mutate">Applied to the stored document inside the store's own gate.</param>
    /// <param name="successMessage">The status line on success.</param>
    /// <param name="rollBack">Restores the control. Run inside <see cref="Apply"/>, so the
    /// roll-back cannot queue a second save of its own.</param>
    protected void ImmediateSave(
        Action preWrite,
        Func<GeneralSettings, GeneralSettings> mutate,
        string successMessage,
        Action rollBack)
    {
        if (!_ready || IsApplying || _disposed)
        {
            return;
        }

        _ = Write(
            preWrite,
            mutate,
            onSuccess: () => StatusMessage = successMessage,
            onFailure: () =>
            {
                StatusMessage = null;
                Apply(rollBack);
            });
    }

    /// <summary>Queues one load-modify-save behind whatever is already queued.</summary>
    protected Task Write(Func<GeneralSettings, GeneralSettings> mutate, Action onSuccess, Action onFailure)
    {
        lock (_writeGate)
        {
            _persist = _persist.ContinueWith(
                _ => RunWrite(null, mutate, onSuccess, onFailure),
                TaskScheduler.Default);
            return _persist;
        }
    }

    /// <summary>The same queued write, with <paramref name="preWrite"/> run first and outside
    /// <c>SettingsStore.MutateGeneral</c>'s write gate. See
    /// <see cref="ImmediateSave(Action, Func{GeneralSettings, GeneralSettings}, string, Action)"/>.
    /// </summary>
    protected Task Write(
        Action preWrite, Func<GeneralSettings, GeneralSettings> mutate, Action onSuccess, Action onFailure)
    {
        lock (_writeGate)
        {
            _persist = _persist.ContinueWith(
                _ => RunWrite(preWrite, mutate, onSuccess, onFailure),
                TaskScheduler.Default);
            return _persist;
        }
    }

    /// <summary>
    /// The tab is owned by <see cref="SettingsViewModel"/>, which is a DI singleton, so the host
    /// owns its lifetime. Cancels the tab-lifetime source and drops the settings subscription, so
    /// nothing it started can publish into a tab whose database is gone.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _unsubscribeGeneralChanged?.Invoke(OnGeneralChanged);
        DisposeCore();
        _lifetime.Dispose();
    }

    // Runs on the UI thread, through the post seam.
    // The one writer of _ready (Task 6 review finding 2), and the one place a first read reaches
    // the controls. Publish-thread only.
    private void PublishGeneral(int generation, GeneralSettings? general, Action? companions)
    {
        if (generation != _generation || _disposed)
        {
            return;
        }

        if (general is not null)
        {
            Saved = general;
            Publish(() =>
            {
                ApplyDocument(general);

                // Inside the same Apply and the same publish, so a companion document can never
                // land between the general document and the controls that read both.
                companions?.Invoke();
            });

            _ready = true;
        }
        else
        {
            // A failed general read still has to let a companion report its own outcome.
            Publish(() => companions?.Invoke());
        }

        LoadFailed = general is null;
        IsLoading = false;
    }

    // Another writer changed the general document. Raised on whichever thread saved, so it is
    // posted like every other publish.
    private void OnGeneralChanged(object? sender, GeneralSettings general)
    {
        // This tab's own write coming back through the store's event.
        if (Environment.CurrentManagedThreadId == Volatile.Read(ref _writingThreadId))
        {
            return;
        }

        _post(() =>
        {
            if (_disposed)
            {
                return;
            }

            Saved = general;
            if (HasPendingEdits)
            {
                OnStoredDocumentChangedElsewhere();
                return;
            }

            Publish(() => ApplyDocument(general));
        });
    }

    private void RunWrite(
        Action? preWrite, Func<GeneralSettings, GeneralSettings> mutate, Action onSuccess, Action onFailure)
    {
        if (_disposed)
        {
            return;
        }

        GeneralSettings next;
        try
        {
            // Runs before MutateGeneral is even called, so a caller's non-local effect (a COM
            // call, a filesystem write) never runs under that method's process-wide write gate
            // (Phase 11 Task 3 review, Important 1). A thrown exception here is caught by the
            // same two catch blocks below as a thrown mutate, so the failure path is identical
            // either way: nothing is read, mutated or saved.
            preWrite?.Invoke();

            // MutateGeneral raises GeneralChanged synchronously on this thread before it returns,
            // so the tab's own handler has to recognize its own write. That is what
            // _writingThreadId is for.
            Volatile.Write(ref _writingThreadId, Environment.CurrentManagedThreadId);
            try
            {
                next = _mutate(mutate);
            }
            finally
            {
                Volatile.Write(ref _writingThreadId, 0);
            }

            _post(() =>
            {
                if (_disposed)
                {
                    return;
                }

                Saved = next;
                ErrorMessage = null;
                onSuccess();
            });
        }
        catch (SettingsValidationException ex)
        {
            // The store refused it. This is the backstop behind every inline message a derived
            // tab shows, and it is what makes those messages a usability layer rather than the
            // only check (design-lessons rule 2).
            _post(() =>
            {
                if (_disposed)
                {
                    return;
                }

                ErrorMessage = ex.Message;
                onFailure();
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Saving a Settings tab's general settings failed");
            _post(() =>
            {
                if (_disposed)
                {
                    return;
                }

                ErrorMessage = "The settings could not be saved. See the log for details.";
                onFailure();
            });
        }
    }
}
