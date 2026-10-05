using GalactiLog.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.Services;

/// <summary>Which part of spec 17.1's update flow the application is in.</summary>
public enum UpdatePhase
{
    /// <summary>No update is known. The status bar indicator is hidden.</summary>
    Idle,

    /// <summary>A check is in flight. The indicator is hidden: a background check the user did
    /// not ask for is not a notification.</summary>
    Checking,

    /// <summary>A check returned an update and the download has not started yet.</summary>
    Available,

    /// <summary>The package is downloading. The indicator carries the percent.</summary>
    Downloading,

    /// <summary>The package is downloaded and waiting for the user to confirm the restart.
    /// </summary>
    ReadyToApply,

    /// <summary>The last check or download failed. The indicator stays hidden: a failed
    /// background check is a log line and an About tab field, not a banner to dismiss.</summary>
    Failed,
}

/// <summary>
/// What <see cref="UpdateService"/> currently knows. One immutable value, published on the UI
/// thread, so a subscriber never reads a half-updated pair.
/// </summary>
/// <param name="Phase">Where in spec 17.1's flow the application is.</param>
/// <param name="AvailableVersion">The version the last check found, or null.</param>
/// <param name="DownloadPercent">0 to 100 while <see cref="UpdatePhase.Downloading"/>.</param>
/// <param name="ReleaseNotes">The notes the last check carried, or null. Spec 12.7's About tab
/// falls back through ruling Q19's three steps when this is null.</param>
/// <param name="LastError">The message of the last failure, or null.</param>
/// <param name="LastCheckedUtc">When the last check completed, or null before the first one.
/// </param>
public sealed record UpdateState(
    UpdatePhase Phase,
    string? AvailableVersion,
    double DownloadPercent,
    string? ReleaseNotes,
    string? LastError,
    DateTimeOffset? LastCheckedUtc)
{
    /// <summary>What a service that has checked nothing reports.</summary>
    public static readonly UpdateState Nothing = new(UpdatePhase.Idle, null, 0, null, null, null);
}

/// <summary>
/// One update the feed offered. <paramref name="Handle"/> carries the update manager's own
/// descriptor back to <see cref="IUpdateChecker.DownloadAsync"/> and
/// <see cref="IUpdateChecker.ApplyAndRestart"/> without <see cref="UpdateService"/> or any test
/// naming a Velopack type.
/// </summary>
/// <param name="Version">The version offered, as the feed spells it.</param>
/// <param name="ReleaseNotes">The notes the feed carried, or null.</param>
/// <param name="Handle">The implementation's own descriptor. Opaque above this interface.</param>
public sealed record AvailableUpdate(string Version, string? ReleaseNotes, object Handle);

/// <summary>
/// The update feed as this application uses it. The seam exists so <c>GalactiLog.App.Tests</c>
/// never builds a real update manager and never reaches the network: every test binds a stub, and
/// a source scan asserts that exactly one file in <c>src/**</c> names the Velopack types.
/// </summary>
public interface IUpdateChecker
{
    /// <summary>Whether the updater installed this process. False for a <c>dotnet run</c>, a
    /// <c>dotnet test</c> and any unpacked build; every update path is gated on it.</summary>
    bool IsInstalled { get; }

    /// <summary>The channel this build was installed from (spec 17.4: <c>alpha</c>, <c>rc</c>,
    /// <c>stable</c>). A readout, never an override.</summary>
    string Channel { get; }

    /// <summary>One check against the feed. Null when there is no update.</summary>
    Task<AvailableUpdate?> CheckAsync(CancellationToken ct);

    /// <summary>Downloads the package, reporting whole percents.</summary>
    Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken ct);

    /// <summary>Applies the downloaded package and restarts the process. Does not return on the
    /// success path.</summary>
    void ApplyAndRestart(AvailableUpdate update);
}

/// <summary>
/// Spec 17.1's update flow: a check on start and every six hours, a download with its progress in
/// the status bar, a prompt before anything is applied, and nothing applied while the user is
/// mid-scan.
/// </summary>
/// <remarks>
/// <para>
/// The loop is <see cref="ScanScheduler"/>'s shape, reused rather than re-invented: an injected
/// delay, a lifetime token, an idempotent <see cref="Start"/> under a gate, a <see cref="Stop"/>
/// that cancels outside the gate, and a catch in the loop body so one failure does not kill it.
/// This is the second occurrence of that pattern and it builds on the first (design-lessons
/// rule 1).
/// </para>
/// <para>
/// Mid-scan suppression, spec 17.1's "nothing is applied silently while the user is mid-scan",
/// is exactly three rules. Checking and downloading are never suppressed: they touch the network
/// and the app data directory only, and a machine that scans continuously would otherwise never
/// learn about an update. Prompting is suppressed while <see cref="ScanStatusService.IsRunning"/>
/// is true; the phase still reaches <see cref="UpdatePhase.ReadyToApply"/> and the status bar
/// still shows it. And <see cref="ApplyAndRestart"/> re-reads the running scan in its own body,
/// because applying an update restarts the process and a scan mid-write would lose work.
/// </para>
/// <para>
/// Ruling Q2: <c>ScanStatusService.ScanFinished</c>, which is the event this class subscribes to,
/// keeps its <c>EventHandler?</c> signature and carries no outcome. Phase 14B Task 5 widened
/// <c>ScanCoordinator.ScanFinished</c> to <c>EventHandler&lt;ScanFinishedEventArgs&gt;</c> and gave
/// it the run's outcome, under the coordinator's override of that task's escalation 2; the App
/// layer's re-raise deliberately did not follow it, so nothing here changed. The running state is
/// read live inside the handler rather than assumed false, because the coordinator schedules a
/// pending follow-up scan before raising the event.
/// </para>
/// </remarks>
public sealed class UpdateService : IDisposable
{
    /// <summary>Spec 17.1: "CheckForUpdatesAsync on start and every 6 hours".</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly BuildInfo _buildInfo;
    private readonly Func<IUpdateChecker> _checker;
    private readonly ScanStatusService _scanStatus;
    private readonly ActivityRepository _activity;
    private readonly Func<Task<bool>>? _showPrompt;
    private readonly ILogger _logger;
    private readonly Action<Action> _post;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly Lock _gate = new();
    private CancellationTokenSource? _lifetimeCts;
    private UpdateState _state = UpdateState.Nothing;
    private AvailableUpdate? _pending;
    private string? _announcedVersion;
    private string? _promptedVersion;
    private bool _promptOpen;
    private int _checking;
    private bool _disposed;

    /// <param name="buildInfo">The running build, for the two activity events' current version
    /// and for the channel fallback. The one reader of build identity (design-lessons rule 1).
    /// </param>
    /// <param name="checker">The feed seam, as a factory so the host resolves it lazily and a
    /// test binds a stub. Never a Velopack type.</param>
    /// <param name="scanStatus">The one App-layer subscriber to <c>ScanCoordinator</c>. This
    /// service reads it and never subscribes to the coordinator itself.</param>
    /// <param name="activity">Spec 5.12's feed, for <c>update_available</c> and
    /// <c>update_applied</c>.</param>
    /// <param name="showPrompt">Opens spec 17.1's confirmation and completes with whether a
    /// window was actually shown. A delegate rather than the service object, because the prompt's
    /// page calls <see cref="ApplyAndRestart"/> back: the two types would otherwise reference
    /// each other. Null shows no prompt, which is what a unit test with no window gets.
    /// <para>
    /// The result is "a window opened", not "the user confirmed": a user who answered Later has
    /// decided, and this service must not ask again for that version, while a prompt
    /// <c>ModalHost</c> could not show over any window has decided nothing (review round 1, M5).
    /// </para></param>
    /// <param name="logger">Optional. A failed check is logged at warning and never rethrown.
    /// </param>
    /// <param name="post">How to reach the UI thread. Defaults to the one dispatcher seam.</param>
    /// <param name="delay">How to wait out the interval. A delegate rather than a
    /// <c>PeriodicTimer</c>, for the reason <see cref="ScanScheduler"/> states: a scheduler built
    /// on a wall clock can only be tested by actually waiting.</param>
    public UpdateService(
        BuildInfo buildInfo,
        Func<IUpdateChecker> checker,
        ScanStatusService scanStatus,
        ActivityRepository activity,
        Func<Task<bool>>? showPrompt = null,
        ILogger? logger = null,
        Action<Action>? post = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _buildInfo = buildInfo;
        _checker = checker;
        _scanStatus = scanStatus;
        _activity = activity;
        _showPrompt = showPrompt;
        _logger = logger ?? NullLogger.Instance;
        _post = post ?? UiPost.Default;
        _delay = delay ?? Task.Delay;

        // Subscribed for the life of the service, not only while the loop runs: the handler does
        // nothing until a check has reached ReadyToApply, and a subscription that came and went
        // with Start would be a second lifetime to reason about.
        _scanStatus.ScanFinished += OnScanFinished;
    }

    /// <summary>
    /// Raised on the UI thread when the update state changes. Already marshalled by the time a
    /// subscriber sees it: do not post again.
    /// </summary>
    public event EventHandler<UpdateState>? StateChanged;

    /// <summary>The current state. Seeded synchronously, so a status bar built mid-download does
    /// not read as idle for the frame before the next event arrives.</summary>
    public UpdateState State
    {
        get { lock (_gate) { return _state; } }
    }

    /// <summary>
    /// Whether an explicit "check for updates" may run right now (spec 12.11 behaviour 4: the tray
    /// menu's Check for updates has "the same conditions on when it may be used" as the About
    /// tab's button).
    /// </summary>
    /// <remarks>
    /// The one definition of that condition, read by spec 12.7's About tab and by the tray menu
    /// (ruling Q4). Two surfaces each repeating "not disposed, installed, not already checking"
    /// is how a menu that checks while a check is in flight happens, and it is the second
    /// occurrence of the rule rather than the sixth (design-lessons rule 1). It is an affordance
    /// answer only: <see cref="CheckNowAsync"/> self-guards, so a caller that ignores this still
    /// cannot double a check.
    /// </remarks>
    public bool CanCheckNow
        => !_disposed && _buildInfo.IsInstalled && State.Phase != UpdatePhase.Checking;

    /// <summary>
    /// The channel the feed reports for this build (spec 17.4). A readout: nothing in this
    /// application overrides it, which is how spec 17.1's "a stable install never offers itself a
    /// prerelease" is structural rather than conventional (ruling Q12).
    /// </summary>
    public string Channel
    {
        get
        {
            try
            {
                return _checker().Channel is { Length: > 0 } channel ? channel : _buildInfo.Channel;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The update channel could not be read; reporting the build's own.");
                return _buildInfo.Channel;
            }
        }
    }

    // Test seam only, matching ScanScheduler: lets a test await the loop's completion after
    // Stop() instead of polling it.
    internal Task? LoopTask { get; private set; }

    /// <summary>
    /// Starts spec 17.1's loop: one check now, then one every <see cref="CheckInterval"/>.
    /// Idempotent, and it never throws: a failed probe leaves the application with no update loop
    /// rather than no application.
    /// </summary>
    /// <remarks>
    /// Called from <c>App.axaml.cs</c> beside the watcher and scheduler starts, never from
    /// <c>AppHost</c>, so the CLI branch of <c>Program.Main</c> structurally never checks for
    /// updates (spec 15). A build the updater did not install starts no loop at all, which is
    /// what keeps <c>dotnet run</c> and the whole test suite off the network.
    /// </remarks>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        if (!ProbeIsInstalled())
        {
            _logger.LogDebug(
                "This build was not installed by the updater, so no update check loop was started.");
            return;
        }

        CancellationToken ct;
        lock (_gate)
        {
            if (_lifetimeCts is not null)
            {
                return;
            }

            _lifetimeCts = new CancellationTokenSource();
            ct = _lifetimeCts.Token;
        }

        LoopTask = RunLoopAsync(ct);
    }

    /// <summary>Ends the loop. Safe to call when it was never started.</summary>
    public void Stop()
    {
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            lifetime = _lifetimeCts;
            _lifetimeCts = null;
        }

        // Cancelled outside the lock, and never disposed: the loop still holds this token. The
        // same rule, for the same reason, as ScanScheduler.Stop.
        lifetime?.Cancel();
    }

    /// <summary>
    /// Runs one check now, off the UI thread and outside the loop's schedule. Spec 12.7's About
    /// tab update check button is the caller.
    /// </summary>
    /// <remarks>
    /// A check already in flight is not doubled: the second call returns without touching the
    /// feed. The claim is taken on the caller's thread, before the work is handed to the pool,
    /// so a second press is refused whether or not the first check has started running yet.
    /// Awaiting the returned task is what a test does instead of blocking.
    /// </remarks>
    public Task CheckNowAsync()
    {
        if (_disposed || !TryClaimCheck())
        {
            return Task.CompletedTask;
        }

        return Task.Run(() => RunClaimedCheckAsync(CancellationToken.None), CancellationToken.None);
    }

    /// <summary>
    /// Opens the confirmation for the downloaded update now, at the user's request from the
    /// status bar indicator. Refuses while a scan is running, like every other path to the
    /// prompt.
    /// </summary>
    public void PromptNow()
    {
        if (_scanStatus.IsRunning)
        {
            return;
        }

        string version;
        lock (_gate)
        {
            if (_disposed || _state.Phase != UpdatePhase.ReadyToApply || _pending is null)
            {
                return;
            }

            version = _pending.Version;
        }

        // Deliberately not gated on _promptedVersion: this is the user asking for the dialog they
        // dismissed. A second press while it is open is refused by ShowPrompt's own flag.
        ShowPrompt(version);
    }

    /// <summary>
    /// Applies the downloaded update and restarts. Refuses while a scan is running, whatever the
    /// caller is.
    /// </summary>
    /// <remarks>
    /// The running-scan check is repeated here and not left to the command's <c>CanExecute</c>
    /// (TRACKING section 6 item 13). This is the most consequential instance of that rule in the
    /// application: applying an update restarts the process, and a scan mid-write would lose the
    /// user's work.
    /// </remarks>
    public void ApplyAndRestart()
    {
        if (_scanStatus.IsRunning)
        {
            _logger.LogWarning("An update was not applied because a scan is running.");
            return;
        }

        AvailableUpdate pending;
        lock (_gate)
        {
            if (_disposed || _state.Phase != UpdatePhase.ReadyToApply || _pending is null)
            {
                return;
            }

            pending = _pending;
        }

        var channel = Channel;

        // Written BEFORE the apply, not after: ApplyUpdatesAndRestart does not return on the
        // success path, so an event emitted afterwards would never reach the database.
        try
        {
            _activity.EmitStandalone(
                category: "system", severity: "info", eventType: "update_applied",
                message: $"Applying update {pending.Version} on the {channel} channel and restarting",
                // snake_case, like every other details document in this solution.
                details: new
                {
                    version = pending.Version,
                    channel,
                    previous_version = _buildInfo.Version,
                });
        }
        catch (Exception ex)
        {
            // An activity row that could not be written must not stop the update the user just
            // confirmed.
            _logger.LogWarning(ex, "The update_applied event could not be written");
        }

        try
        {
            _checker().ApplyAndRestart(pending);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The downloaded update could not be applied");
            Mutate(state => state with { Phase = UpdatePhase.Failed, LastError = ex.Message });
        }
    }

    /// <summary>Ends the loop and detaches the scan subscription. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _scanStatus.ScanFinished -= OnScanFinished;
    }

    // ---------------------------------------------------------------- the loop

    private async Task RunLoopAsync(CancellationToken lifetimeCt)
    {
        while (!lifetimeCt.IsCancellationRequested)
        {
            // The check runs BEFORE the first wait, which is spec 17.1's "on start and every 6
            // hours". RunCheckAsync swallows and reports its own failures; this catch is the
            // second half of the same rule, so nothing at all can end the loop early.
            try
            {
                await RunCheckAsync(lifetimeCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "An update check failed; the loop continues");
            }

            try
            {
                await _delay(CheckInterval, lifetimeCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Review M2, fixed here and in ScanScheduler's identical wait together (fixer list
                // code item 6). In production _delay is Task.Delay over a constant interval, so
                // only an injected seam can fail this way; without this the loop ended anyway and
                // faulted LoopTask with nobody observing it, which is the one thing the catches
                // above exist to prevent. Ending rather than continuing: a loop whose wait does
                // not work would spin.
                _logger.LogError(ex, "The update loop's wait failed; the loop is stopping");
                break;
            }
        }
    }

    // A check already in flight is not doubled: the About tab's button and the loop's tick can
    // land together.
    private bool TryClaimCheck() => Interlocked.CompareExchange(ref _checking, 1, 0) == 0;

    private async Task RunCheckAsync(CancellationToken ct)
    {
        if (!TryClaimCheck())
        {
            return;
        }

        await RunClaimedCheckAsync(ct).ConfigureAwait(false);
    }

    // The caller holds the claim and this releases it. Split from the claim itself so
    // CheckNowAsync can take it synchronously, before the work reaches the thread pool.
    private async Task RunClaimedCheckAsync(CancellationToken ct)
    {
        try
        {
            // Review round 1, I1. An update that is downloaded and waiting for confirmation is
            // the end of this flow until the user answers, so nothing is checked while one is
            // staged. Two defects live in the alternative: a check that returned nothing, or
            // threw (a laptop that is offline at the six hour tick), would overwrite
            // ReadyToApply with Idle or Failed while _pending stayed set, which takes the status
            // bar indicator away and makes ApplyAndRestart refuse a package that is already on
            // disk; and a check that succeeded would download that same package again on every
            // tick for as long as the process runs.
            if (HasStagedUpdate())
            {
                _logger.LogDebug(
                    "An update is downloaded and waiting for confirmation; no check was made.");
                return;
            }

            var checker = _checker();
            if (!checker.IsInstalled)
            {
                _logger.LogDebug("This build was not installed by the updater; nothing was checked.");
                return;
            }

            Mutate(state => state with { Phase = UpdatePhase.Checking, LastError = null });

            var update = await checker.CheckAsync(ct).ConfigureAwait(false);
            var checkedUtc = DateTimeOffset.UtcNow;
            if (update is null)
            {
                Mutate(_ => new UpdateState(UpdatePhase.Idle, null, 0, null, null, checkedUtc));
                return;
            }

            Mutate(_ => new UpdateState(
                UpdatePhase.Available, update.Version, 0, update.ReleaseNotes, null, checkedUtc));
            Announce(update);

            Mutate(state => state with { Phase = UpdatePhase.Downloading, DownloadPercent = 0 });
            await checker
                .DownloadAsync(
                    update,
                    percent => Mutate(state => state with
                    {
                        Phase = UpdatePhase.Downloading,
                        DownloadPercent = percent,
                    }),
                    ct)
                .ConfigureAwait(false);

            lock (_gate)
            {
                _pending = update;
            }

            Mutate(state => state with { Phase = UpdatePhase.ReadyToApply, DownloadPercent = 100 });
            RequestPrompt();
        }
        catch (OperationCanceledException)
        {
            // Stop() during a check. Not a failure, and not a state change either: the process
            // is on its way out.
        }
        catch (Exception ex)
        {
            // The roadmap's "a failed check logs at warning without disrupting startup". The loop
            // continues to the next interval and the About tab shows the message.
            _logger.LogWarning(ex, "The update check failed");
            Mutate(state => state with
            {
                Phase = UpdatePhase.Failed,
                DownloadPercent = 0,
                LastError = ex.Message,
            });
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>Whether a downloaded update is waiting for the user's confirmation.</summary>
    /// <remarks>
    /// Read under the gate, so the phase and the pending update are answered as one fact. The
    /// only writer of either is a claimed check, and a claim is exclusive, so a staged update
    /// cannot appear between this read and the check that follows it.
    /// </remarks>
    private bool HasStagedUpdate()
    {
        lock (_gate)
        {
            return _pending is not null && _state.Phase == UpdatePhase.ReadyToApply;
        }
    }

    private bool ProbeIsInstalled()
    {
        try
        {
            return _checker().IsInstalled;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The updater could not be reached; no update loop was started");
            return false;
        }
    }

    // ---------------------------------------------------------------- the activity events

    // Once per distinct version, not once per check: a machine left running for a week checks 28
    // times, and 28 identical rows would push real events out of the retention window.
    private void Announce(AvailableUpdate update)
    {
        lock (_gate)
        {
            if (_announcedVersion == update.Version)
            {
                return;
            }

            _announcedVersion = update.Version;
        }

        var channel = Channel;
        try
        {
            _activity.EmitStandalone(
                category: "system", severity: "info", eventType: "update_available",
                message: $"Update {update.Version} is available on the {channel} channel",
                // snake_case, like every other details document in this solution.
                details: new
                {
                    version = update.Version,
                    channel,
                    current_version = _buildInfo.Version,
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The update_available event could not be written");
        }
    }

    // ---------------------------------------------------------------- the prompt

    private void OnScanFinished(object? sender, EventArgs e)
    {
        // Read live rather than assumed false: the coordinator schedules a pending follow-up scan
        // BEFORE raising this event, which is the defect ScanStatusService.OnScanFinished's own
        // review-fix comment records.
        if (_scanStatus.IsRunning)
        {
            return;
        }

        RequestPrompt();
    }

    private void RequestPrompt()
    {
        if (_scanStatus.IsRunning)
        {
            // Suppressed, not cancelled: the phase stays ReadyToApply and the status bar shows
            // it, so the update is offered again when the scan finishes.
            _logger.LogDebug("The update prompt was suppressed because a scan is running.");
            return;
        }

        string version;
        lock (_gate)
        {
            if (_disposed || _state.Phase != UpdatePhase.ReadyToApply || _pending is null)
            {
                return;
            }

            if (_promptedVersion == _pending.Version)
            {
                // A user who dismissed the prompt is not asked again every time a scan finishes.
                return;
            }

            version = _pending.Version;
        }

        ShowPrompt(version);
    }

    private void ShowPrompt(string version)
    {
        if (_showPrompt is null)
        {
            return;
        }

        // Review round 1, M1: one confirmation window at a time. Two quick presses of the status
        // bar indicator, or one press racing the prompt a finishing scan raises, would otherwise
        // stack two dialogs for the same version. Taken before the post, so the refusal does not
        // depend on when the dispatcher runs either closure.
        lock (_gate)
        {
            if (_promptOpen)
            {
                return;
            }

            _promptOpen = true;
        }

        // Posted rather than invoked inline, even when the caller is already on the UI thread:
        // opening a modal runs a nested dispatcher loop, and ScanStatusService's ScanFinished
        // fan-out must not park inside one.
        _post(() =>
        {
            if (_disposed)
            {
                FinishPrompt(version, shown: false);
                return;
            }

            Task<bool> showing;
            try
            {
                showing = _showPrompt();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The update prompt could not be opened");
                FinishPrompt(version, shown: false);
                return;
            }

            showing.ContinueWith(
                OnPromptClosed,
                version,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        });
    }

    private void OnPromptClosed(Task<bool> showing, object? state)
    {
        var version = (string)state!;
        if (showing.IsFaulted)
        {
            _logger.LogWarning(showing.Exception, "The update prompt failed");
            FinishPrompt(version, shown: false);
            return;
        }

        FinishPrompt(version, shown: showing.Status == TaskStatus.RanToCompletion && showing.Result);
    }

    // Review round 1, M5: the version is recorded as prompted only when a window actually opened.
    // ModalHost's no-owner path decides nothing, so an update it could not show must still be
    // offered when the next scan finishes.
    private void FinishPrompt(string version, bool shown)
    {
        lock (_gate)
        {
            _promptOpen = false;
            if (shown)
            {
                _promptedVersion = version;
            }
        }
    }

    // ---------------------------------------------------------------- state publication

    private void Mutate(Func<UpdateState, UpdateState> change)
    {
        UpdateState next;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            next = change(_state);
            _state = next;
        }

        Publish(next);
    }

    private void Publish(UpdateState state) => _post(() =>
    {
        if (_disposed)
        {
            return;
        }

        // Each target invoked and guarded on its own, for the reason ScanStatusService states: a
        // multicast delegate stops calling targets the instant one throws, so a single try around
        // one Invoke would let the status bar swallow the About tab's update.
        foreach (var handler in StateChanged?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<UpdateState>)handler).Invoke(this, state);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "An UpdateService.StateChanged subscriber threw; other subscribers still ran");
            }
        }
    });
}
