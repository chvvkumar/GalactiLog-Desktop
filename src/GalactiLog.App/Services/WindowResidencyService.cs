using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.Views;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.Services;

/// <summary>
/// Spec 12.11: the one owner of whether the main window is on screen, and the one path out of
/// the process. Every hide, every show and every exit in the application goes through this type.
/// </summary>
/// <remarks>
/// <para>
/// Design-lessons rule 2 applied to process lifetime. Spec 12.11 behaviour 7 says "there is one
/// drain and one exit"; that is enforced here by making <see cref="RequestExit"/> the only caller
/// of the lifetime's <c>TryShutdown</c>, rather than by a convention each surface that offers an
/// Exit affordance has to remember. The tray menu's Exit, a title-bar close with close-to-tray
/// off, and any later exit affordance all reach the same two lines.
/// </para>
/// <para>
/// No Avalonia type appears in the constructor. <see cref="Attach(Window)"/> and
/// <see cref="AttachLater(Func{Window})"/>, the two ways a window reaches this service, are the
/// only members that name one, so the service is constructible in a unit test with no window at
/// all, exactly as <c>App.DrainForShutdown</c> is. "No window" is a real state and not a test-only
/// one: spec 12.11 behaviour 9 starts the process into the tray with no window, and behaviour 3's
/// activation can arrive before the window exists.
/// </para>
/// </remarks>
public sealed partial class WindowResidencyService : ObservableObject
{
    private readonly Func<GeneralSettings> _readGeneral;
    private readonly Action<Action> _post;
    private readonly Func<bool> _requestShutdown;
    private readonly ILogger _logger;

    private Window? _window;

    // Spec 12.11 behaviour 9 (Phase 11 Task 4). Non-null only between a start that built no
    // window and the first activation, which builds one. Cleared as it is called, so a factory
    // that throws is not retried on every later activation.
    private Func<Window>? _windowFactory;

    // 1 once an exit has been asked for, or once the platform's own shutdown is in flight.
    // Interlocked rather than a bool, so a second Exit while the drain runs is refused exactly
    // once rather than racing on a read-modify-write.
    private int _exiting;

    // The last value handed to the post seam, written synchronously at the post rather than read
    // back from the applied property. Plain field and not volatile: every writer of it is on the
    // UI thread (Attach, and the attached window's PropertyChanged).
    private bool _pendingVisible;

    /// <param name="readGeneral">Normally <c>SettingsStore.GetGeneral</c>. Read live on every
    /// close, never captured at construction: the Settings General tab writes
    /// <c>close_to_tray</c> while the window is open and the next close must honour it.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="requestShutdown">Bound in <c>App.axaml.cs</c> to
    /// <c>IClassicDesktopStyleApplicationLifetime.TryShutdown</c>, never to <c>Shutdown</c>. The
    /// Avalonia documentation states that <c>TryShutdown</c> raises <c>ShutdownRequested</c> and
    /// says nothing of the kind about <c>Shutdown</c>, and <c>ShutdownRequested</c> is where the
    /// one drain lives. Null leaves the service with no way out of the process, which is what the
    /// headless test harness and any surface with no desktop lifetime want.</param>
    /// <param name="logger">Optional. A refused shutdown and a replaced window binding are
    /// logged, never thrown.</param>
    public WindowResidencyService(
        Func<GeneralSettings> readGeneral,
        Action<Action>? post = null,
        Func<bool>? requestShutdown = null,
        ILogger? logger = null)
    {
        _readGeneral = readGeneral;
        _post = post ?? UiPost.Default;
        _requestShutdown = requestShutdown ?? (() => false);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Whether a window is currently on screen. False from construction until the first show, and
    /// false again after every hide. Spec 12.11 behaviour 10 reads it; nothing else may keep its
    /// own copy.
    /// </summary>
    [ObservableProperty]
    public partial bool IsWindowVisible { get; private set; }

    /// <summary>
    /// True between <see cref="RequestExit"/> and process exit, and true while the platform's own
    /// shutdown is in flight. <see cref="HandleUserClose"/> returns false while it is set, so a
    /// shutdown's own window close is never converted back into a hide (spec 12.11 behaviour 5:
    /// "the application's own shutdown is never converted into a hide").
    /// </summary>
    public bool IsExiting => Volatile.Read(ref _exiting) != 0;

    /// <summary>The window this service currently speaks for, or null. Test seam only.</summary>
    internal Window? AttachedWindow => _window;

    /// <summary>
    /// Binds the main window. Called once, from <c>App.OnFrameworkInitializationCompleted</c>. A
    /// second call replaces the binding and logs.
    /// </summary>
    /// <remarks>
    /// Takes <see cref="Window"/> rather than <c>MainWindow</c> so the service's signature stays
    /// free of the view type, and casts inside: the shell's close and minimize policy lives in
    /// <c>MainWindow</c>'s two overrides, which call back into this service. Visibility is the one
    /// thing read from the window itself, because the desktop lifetime shows the first window and
    /// this service never sees that call.
    /// </remarks>
    public void Attach(Window window)
    {
        if (_window is { } previous)
        {
            _logger.LogWarning(
                "A second window was attached to the residency service; the previous binding is replaced");
            previous.PropertyChanged -= OnWindowPropertyChanged;
            if (previous is MainWindow previousShell)
            {
                previousShell.Residency = null;
            }
        }

        _window = window;
        window.PropertyChanged += OnWindowPropertyChanged;
        if (window is MainWindow shell)
        {
            shell.Residency = this;
        }

        SetWindowVisible(window.IsVisible);
    }

    /// <summary>
    /// Supplies how to build the main window for a start that built none (spec 12.11 behaviour 9,
    /// ruling Q10 (a)). The first <see cref="ShowAndActivate"/> with nothing attached calls it
    /// once and attaches what it returns; every later call finds a window and never calls it
    /// again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The factory lives here rather than at the callers of <see cref="ShowAndActivate"/> because
    /// this service is the one owner of whether a window is on screen. There are three callers,
    /// one of them <c>TrayIconViewModel.Open</c>, and a caller that built its own shell would be a
    /// second owner of that question, which is the shape this type exists to prevent
    /// (design-lessons rules 1 and 2).
    /// </para>
    /// <para>
    /// Ignored once a window is attached, so an ordinary start, which calls
    /// <see cref="Attach(Window)"/> instead, can never build a second shell.
    /// </para>
    /// <para>
    /// The factory constructs a <see cref="Window"/> and registers it with the desktop lifetime,
    /// so it must run on the UI thread. That is the caller's obligation and it belongs to
    /// <see cref="ShowAndActivate"/>, not to this method: all three callers of that method are
    /// already on the UI thread (the native tray icon's click and its menu's Open, both of which run
    /// <c>TrayIconViewModel.OpenCommand</c> from the icon's window procedure, and the
    /// single-instance gate's callback, which is posted through the dispatcher), and the
    /// deferred block asserts it with <c>Dispatcher.UIThread.VerifyAccess()</c> so a later caller
    /// from a pool thread fails at the call rather than inside Avalonia.
    /// </para>
    /// </remarks>
    /// <param name="windowFactory">Builds the main window and registers it with the desktop
    /// lifetime. Bound in <c>App.axaml.cs</c>, which is the only place that may name the shell
    /// type and the lifetime.</param>
    public void AttachLater(Func<Window> windowFactory) => _windowFactory = windowFactory;

    /// <summary>
    /// Shows the window if it is hidden, restores it if it is minimized, and activates it.
    /// Idempotent. The tray menu's Open, the tray icon's click, and a second launch's activation
    /// (spec 12.11 behaviour 3) all call this and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Show()</c> on a window that was never hidden is documented as safe; <c>Show()</c> on a
    /// <strong>closed</strong> window throws, which is why nothing in this application calls
    /// <c>Close()</c> on the main window any more: a close is either converted into a hide or
    /// converted into the one shutdown.
    /// </para>
    /// <para>
    /// A no-op while <see cref="IsExiting"/> is set, and that check lives here rather than at the
    /// callers (Task 1 review, important finding; design-lessons rules 1 and 2). There are three
    /// callers now, and the lifetime does close every window once an accepted shutdown proceeds,
    /// so an activation delivered after the drain would reach <c>Show()</c> on a closed window and
    /// throw. A check each caller has to remember is forgotten at caller N+1, which is exactly
    /// what happened: the tray icon's click had no guard while the other two did.
    /// </para>
    /// <para>
    /// A refused shutdown reverts the flag (<see cref="RequestExit"/>), so an activation arriving
    /// after a refused exit is honoured rather than dropped for the life of the process.
    /// </para>
    /// </remarks>
    public void ShowAndActivate()
    {
        if (IsExiting)
        {
            _logger.LogDebug("Activation was requested while the application is exiting");
            return;
        }

        // Spec 12.11 behaviour 9: the start that built no window builds it here, on the first
        // activation, and nowhere else. Cleared before the call rather than after it, so a shell
        // that cannot be constructed is reported once instead of on every later tray click.
        if (_window is null && _windowFactory is { } factory)
        {
            // The factory constructs a Window and assigns the lifetime's MainWindow, so a caller
            // that is not on the UI thread fails here rather than somewhere inside Avalonia
            // (Task 4 review, minor finding on AttachLater's thread affinity).
            Dispatcher.UIThread.VerifyAccess();
            _windowFactory = null;
            try
            {
                Attach(factory());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The main window could not be built for a start into the tray");
                return;
            }
        }

        if (_window is not { } window)
        {
            _logger.LogDebug("Activation was requested with no window attached");
            return;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>
    /// Handles a user-initiated window close (spec 12.11 behaviours 5 and 7). Returns true when
    /// this service took responsibility for the close, in which case the caller cancels the
    /// platform close.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two outcomes, both of which cancel the platform close. With <c>close_to_tray</c> on the
    /// window is hidden and the application keeps running. With it off the close becomes the one
    /// shutdown: the platform close is still cancelled, and <see cref="RequestExit"/> then closes
    /// every window itself after the drain has run. Ending the process by letting this one close
    /// through is not available, because <c>ShutdownMode</c> is <c>OnExplicitShutdown</c> and the
    /// last window closing no longer ends anything.
    /// </para>
    /// <para>
    /// The setting is read live, through the injected delegate, rather than from a value captured
    /// at construction. That read is a SQLite read (<c>SettingsStore.GetGeneral</c> opens a
    /// context) and it happens on the UI thread inside <c>OnClosing</c>, which is a bounded
    /// single-row read on a path that is already blocking; <c>OnClosing</c> cannot await, so it is
    /// deliberately not asynchronous. A read that throws leaves the close alone rather than
    /// trapping the user in a window that will not shut.
    /// </para>
    /// </remarks>
    /// <param name="platformIsShuttingDown">True when the close came from the operating system or
    /// from the application's own shutdown rather than from the user. A <c>bool</c> rather than
    /// Avalonia's <c>WindowCloseReason</c>, so this type still names no Avalonia type outside
    /// <see cref="Attach"/>; <c>MainWindow.OnClosing</c> maps the reason.</param>
    public bool HandleUserClose(bool platformIsShuttingDown = false)
    {
        // The lifetime raises ShutdownRequested, the handler marks this service exiting and runs
        // the drain, and the lifetime then closes every window. Those closes must be allowed
        // through: without this the close-to-tray conversion would cancel the application's own
        // shutdown and the process would never exit.
        //
        // The reason is checked as well as the flag (review minor finding 1): an OS shutdown or a
        // log-off can reach a window's Closing without ShutdownRequested having been raised first,
        // and hiding the window during a Windows shutdown is the same defect in a worse place.
        if (IsExiting || platformIsShuttingDown || _window is null)
        {
            return false;
        }

        bool closeToTray;
        try
        {
            closeToTray = _readGeneral().CloseToTray;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The close-to-tray setting could not be read; closing normally");
            return false;
        }

        if (closeToTray)
        {
            HideWindow();
            return true;
        }

        // Posted rather than called inline: TryShutdown closes every window itself, and doing
        // that from inside the Closing handler of one of them would re-enter that window's close.
        // The post lets this handler return first, and the shutdown then closes the window on the
        // next dispatcher turn.
        _post(RequestExit);
        return true;
    }

    /// <summary>
    /// Handles the window reaching <c>WindowState.Minimized</c> (spec 12.11 behaviour 6). Returns
    /// true when the minimize was converted into a hide.
    /// </summary>
    /// <remarks>
    /// The window state is put back to <c>Normal</c> as part of the hide, not inside
    /// <see cref="ShowAndActivate"/>: spec 12.11 behaviour 6 says reopening restores the window at
    /// the size it had, so the minimized state must not survive the hide.
    /// </remarks>
    public bool HandleMinimized()
    {
        if (IsExiting || _window is not { } window)
        {
            return false;
        }

        bool minimizeToTray;
        try
        {
            minimizeToTray = _readGeneral().MinimizeToTray;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The minimize-to-tray setting could not be read; minimizing normally");
            return false;
        }

        if (!minimizeToTray)
        {
            return false;
        }

        HideWindow();
        window.WindowState = WindowState.Normal;
        return true;
    }

    /// <summary>
    /// The one exit path (spec 12.11 behaviour 7). Asks the lifetime to shut down, which raises
    /// <c>ShutdownRequested</c>, which runs the one drain. Idempotent: a second press of the tray
    /// Exit while the drain runs does nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No confirmation while a scan is running (ruling Q16): the drain cancels the scan and waits
    /// within its budget on this path exactly as it does on every other exit path.
    /// </para>
    /// <para>
    /// A refused request releases the flag again (review finding 1, coordinator ruling). The
    /// brief's "leave <see cref="IsExiting"/> set" and its "the user will press Exit again" cannot
    /// both hold while this method is idempotent on that same flag: a stuck flag makes every later
    /// Exit a no-op, makes the tray menu's Open a no-op, and stops
    /// <see cref="HandleUserClose"/> converting the next title-bar close, which under
    /// <c>ShutdownMode.OnExplicitShutdown</c> destroys the window for real and leaves a running
    /// process with no window and no way out. The flag therefore sticks only after a request that
    /// was accepted, and <see cref="OnShutdownRequested"/> stays the one setter that must never be
    /// released.
    /// </para>
    /// <para>
    /// A refusal is expensive rather than merely a no-op, which is the reason
    /// <c>ModalPageWindow.OnClosing</c> filters close reasons at the base class rather than
    /// leaving each dialog's <c>RefuseClose</c> to remember the rule (phase review finding P6).
    /// The lifetime raises <c>ShutdownRequested</c> before it closes any window, so by the time a
    /// dialog could veto, <see cref="OnShutdownRequested"/> has already run and the drain has
    /// already stopped the watcher, the scheduler, the update loop, the thumbnail worker, the
    /// scan completion watcher and the single-instance listener. This method then reverts
    /// <see cref="IsExiting"/> and the process continues with every subsystem stopped and no code
    /// path that restarts any of them.
    /// </para>
    /// </remarks>
    public void RequestExit()
    {
        if (Interlocked.Exchange(ref _exiting, 1) != 0)
        {
            return;
        }

        if (!_requestShutdown())
        {
            // Something cancelled the shutdown, or this surface has no lifetime to ask. Released,
            // so the next press is a real second attempt rather than a silent no-op.
            Volatile.Write(ref _exiting, 0);
            _logger.LogWarning("The application refused the requested shutdown");
        }
    }

    /// <summary>
    /// Called from the <c>ShutdownRequested</c> handler before the drain, so a shutdown the
    /// platform started (a logout, a session end) also sets <see cref="IsExiting"/>.
    /// </summary>
    public void OnShutdownRequested() => Volatile.Write(ref _exiting, 1);

    private void HideWindow() => _window?.Hide();

    // Every mutation of IsWindowVisible crosses the post seam, which is the shape every other
    // service in this application uses: UiPost.Default in production, an inline call in a test.
    //
    // The guard compares against the last REQUESTED value, never against the applied property
    // (phase review finding P1). UiPost.Default is Dispatcher.UIThread.Post and never runs inline,
    // so a hide followed by a show inside one dispatcher turn used to post false, then find
    // IsWindowVisible still true on the second call and return without posting, and the queued
    // false landed afterwards: IsWindowVisible false with the window on screen, and the reverse
    // ordering true with it hidden. HandleMinimized's HideWindow-then-WindowState pair is the
    // production shape that reaches it, and ScanCompletionWatcher reads this property as spec
    // 12.11 behaviour 10's first condition, so a stale answer shows or suppresses a notice
    // wrongly. Against the requested value the guard can only ever drop a real duplicate.
    private void SetWindowVisible(bool visible)
    {
        if (_pendingVisible == visible)
        {
            return;
        }

        _pendingVisible = visible;
        _post(() => IsWindowVisible = visible);
    }

    // The one publisher. Visibility is read from the window rather than written by this service's
    // own Show and Hide calls, because the desktop lifetime shows the first window itself and this
    // service never sees that call, and because Window.Show and Window.Hide raise this for every
    // caller. It is still one owner of the answer: nothing outside this type publishes
    // IsWindowVisible.
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == Visual.IsVisibleProperty)
        {
            SetWindowVisible(change.GetNewValue<bool>());
        }
    }
}
