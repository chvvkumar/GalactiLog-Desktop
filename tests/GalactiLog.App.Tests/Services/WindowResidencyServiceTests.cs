using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.App.Views;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;
using Xunit;
using RecordingPost = GalactiLog.App.Tests.TestSupport.DiagnosticsViewModelTestFactory.RecordingPost;

namespace GalactiLog.App.Tests.Services;

// Design-spec 12.11 behaviours 3, 5, 6 and 7. WindowResidencyService is the one owner of whether
// the window is on screen and the one path out of the process, so these cases are about exactly
// two things: which closes become hides, and how many times the lifetime is asked to shut down.
//
// Plain [Fact] where no window is needed and [AvaloniaFact] where one is, which is the same split
// App.DrainForShutdown's own suite uses: nothing here needs a database, because every seam is a
// delegate.
public class WindowResidencyServiceTests
{
    private sealed class Harness
    {
        public GeneralSettings General { get; set; } = new();

        public int ShutdownRequests { get; private set; }

        public bool ShutdownSucceeds { get; set; } = true;

        public RecordingLogger Logger { get; } = new();

        public Exception? ReadThrows { get; set; }

        /// <summary>The post seam, inline unless a case supplies a queueing one. Only the finding
        /// P1 case below needs a queue: a synchronous post is structurally blind to a guard that
        /// compares against an applied value.</summary>
        public Action<Action>? Post { get; set; }

        public WindowResidencyService Create() => new(
            () => ReadThrows is null ? General : throw ReadThrows,
            post: Post ?? (action => action()),
            requestShutdown: () =>
            {
                ShutdownRequests++;
                return ShutdownSucceeds;
            },
            logger: Logger);
    }

    // ---- closing ------------------------------------------------------------------------------

    [AvaloniaFact]
    public void HandleUserClose_WithCloseToTrayOn_HidesAndReportsHandled()
    {
        var harness = new Harness { General = new GeneralSettings { CloseToTray = true } };
        var residency = harness.Create();
        var window = new Window();
        window.Show();
        residency.Attach(window);

        Assert.True(residency.HandleUserClose());

        Assert.False(window.IsVisible);
        Assert.False(residency.IsWindowVisible);
        Assert.Equal(0, harness.ShutdownRequests);

        window.Close();
    }

    // Deviation from the brief's "ReportsNotHandled": ShutdownMode is OnExplicitShutdown (ruling
    // Q3), so letting this close through would leave a process with no window and no way out.
    // Spec 12.11 behaviour 5 says close-to-tray off ends the process and behaviour 7 says it does
    // so through the one exit path, which is what this asserts: the platform close is cancelled
    // and the shutdown closes the window itself after the drain.
    [AvaloniaFact]
    public void HandleUserClose_WithCloseToTrayOff_DoesNotHide_AndAsksForTheOneShutdown()
    {
        var harness = new Harness { General = new GeneralSettings { CloseToTray = false } };
        var residency = harness.Create();
        var window = new Window();
        window.Show();
        residency.Attach(window);

        Assert.True(residency.HandleUserClose());

        Assert.True(window.IsVisible);
        Assert.Equal(1, harness.ShutdownRequests);
        Assert.True(residency.IsExiting);

        window.Close();
    }

    [AvaloniaFact]
    public void HandleUserClose_ReadsTheSettingLive_NotTheValueCapturedAtConstruction()
    {
        var harness = new Harness { General = new GeneralSettings { CloseToTray = true } };
        var residency = harness.Create();
        var window = new Window();
        window.Show();
        residency.Attach(window);

        Assert.True(residency.HandleUserClose());
        Assert.False(window.IsVisible);

        // The General tab writes close_to_tray while the window is open, and the next close has
        // to honour it. A value captured at construction would hide again here.
        harness.General = new GeneralSettings { CloseToTray = false };
        residency.ShowAndActivate();

        Assert.True(residency.HandleUserClose());
        Assert.True(window.IsVisible);
        Assert.Equal(1, harness.ShutdownRequests);

        window.Close();
    }

    [AvaloniaFact]
    public void HandleUserClose_WhileExiting_ReportsNotHandled_SoAShutdownsOwnCloseIsNotCancelled()
    {
        // Without this rule, close-to-tray cancels the application's own shutdown and the process
        // can never be closed from the title bar.
        var harness = new Harness { General = new GeneralSettings { CloseToTray = true } };
        var residency = harness.Create();
        var window = new Window();
        window.Show();
        residency.Attach(window);

        residency.OnShutdownRequested();

        Assert.False(residency.HandleUserClose());
        Assert.True(window.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void HandleUserClose_WhenTheSettingCannotBeRead_ClosesNormally()
    {
        var harness = new Harness { ReadThrows = new InvalidOperationException("no settings") };
        var residency = harness.Create();
        var window = new Window();
        window.Show();
        residency.Attach(window);

        Assert.False(residency.HandleUserClose());
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning);

        window.Close();
    }

    [AvaloniaFact]
    public void HandleUserClose_DuringAPlatformShutdown_ReportsNotHandled()
    {
        // Review minor finding 1. An OS shutdown or a log-off can reach a window's Closing without
        // ShutdownRequested having been raised first, so the reason has to be checked as well as
        // the flag: hiding the window during a Windows shutdown would block the shutdown.
        var harness = new Harness { General = new GeneralSettings { CloseToTray = true } };
        var residency = harness.Create();
        var window = new Window();
        window.Show();
        residency.Attach(window);

        Assert.False(residency.HandleUserClose(platformIsShuttingDown: true));

        Assert.True(window.IsVisible);
        Assert.Equal(0, harness.ShutdownRequests);

        window.Close();
    }

    [AvaloniaFact]
    public void TheShell_OnAnOsShutdownClose_DoesNotHide()
    {
        var harness = new Harness { General = new GeneralSettings { CloseToTray = true } };
        var residency = harness.Create();
        var window = new MainWindow();
        window.Show();
        residency.Attach(window);

        var args = WindowClosingProbe.RaiseClosing(window, WindowCloseReason.OSShutdown);

        Assert.False(args.Cancel);
        Assert.True(window.IsVisible);

        window.Close();
    }

    [Fact]
    public void HandleUserClose_WithNoWindowAttached_ReportsNotHandled()
    {
        var residency = new Harness { General = new GeneralSettings { CloseToTray = true } }.Create();

        Assert.False(residency.HandleUserClose());
    }

    // ---- minimizing ---------------------------------------------------------------------------

    [AvaloniaFact]
    public void HandleMinimized_WithMinimizeToTrayOn_HidesAndRestoresTheWindowStateToNormal()
    {
        var harness = new Harness { General = new GeneralSettings { MinimizeToTray = true } };
        var residency = harness.Create();
        var window = new Window();
        window.Show();
        residency.Attach(window);
        window.WindowState = WindowState.Minimized;

        Assert.True(residency.HandleMinimized());

        Assert.False(window.IsVisible);

        // Spec 12.11 behaviour 6: reopening restores it at the size it had, so the minimized
        // state must not survive the hide.
        Assert.Equal(WindowState.Normal, window.WindowState);

        window.Close();
    }

    [AvaloniaFact]
    public void HandleMinimized_WithMinimizeToTrayOff_LeavesTheWindowMinimized()
    {
        var harness = new Harness { General = new GeneralSettings { MinimizeToTray = false } };
        var residency = harness.Create();
        var window = new Window();
        window.Show();
        residency.Attach(window);
        window.WindowState = WindowState.Minimized;

        Assert.False(residency.HandleMinimized());

        Assert.True(window.IsVisible);
        Assert.Equal(WindowState.Minimized, window.WindowState);

        window.Close();
    }

    [Fact]
    public void HandleMinimized_WithNoWindowAttached_DoesNothing()
    {
        var residency = new Harness { General = new GeneralSettings { MinimizeToTray = true } }.Create();

        Assert.False(residency.HandleMinimized());
    }

    // ---- showing ------------------------------------------------------------------------------

    [AvaloniaFact]
    public void ShowAndActivate_AfterHide_ShowsTheWindowAgain()
    {
        var harness = new Harness { General = new GeneralSettings { CloseToTray = true } };
        var residency = harness.Create();
        var window = new Window();
        window.Show();
        residency.Attach(window);

        Assert.True(residency.HandleUserClose());
        Assert.False(window.IsVisible);

        residency.ShowAndActivate();

        Assert.True(window.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void ShowAndActivate_OnAMinimizedWindow_RestoresIt()
    {
        var residency = new Harness().Create();
        var window = new Window();
        window.Show();
        residency.Attach(window);
        window.WindowState = WindowState.Minimized;

        residency.ShowAndActivate();

        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.True(window.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void ShowAndActivate_CalledTwice_IsSafe()
    {
        var residency = new Harness().Create();
        var window = new Window();
        window.Show();
        residency.Attach(window);

        residency.ShowAndActivate();
        residency.ShowAndActivate();

        Assert.True(window.IsVisible);
        Assert.True(residency.IsWindowVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void ShowAndActivate_WhileExiting_DoesNothing()
    {
        // Task 1 review, important finding. Once an accepted shutdown proceeds the lifetime closes
        // every window, and Show() on a closed window throws, so an activation delivered after the
        // drain must be dropped. The guard is here rather than at the three callers, because the
        // tray icon's click had already been written without it.
        var harness = new Harness { General = new GeneralSettings { CloseToTray = true } };
        var residency = harness.Create();
        var window = new Window();
        window.Show();
        residency.Attach(window);
        Assert.True(residency.HandleUserClose());
        Assert.False(window.IsVisible);

        residency.OnShutdownRequested();
        residency.ShowAndActivate();

        Assert.False(window.IsVisible);
        Assert.False(residency.IsWindowVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void ShowAndActivate_AfterARefusedExit_StillShowsTheWindow()
    {
        // The other side of the same guard: a refused shutdown reverts the flag, so an activation
        // after a refused exit is honoured rather than dropped for the life of the process.
        var harness = new Harness
        {
            General = new GeneralSettings { CloseToTray = true },
            ShutdownSucceeds = false,
        };
        var residency = harness.Create();
        var window = new Window();
        window.Show();
        residency.Attach(window);
        Assert.True(residency.HandleUserClose());

        residency.RequestExit();
        residency.ShowAndActivate();

        Assert.True(window.IsVisible);

        window.Close();
    }

    [Fact]
    public void TheExitingGuard_LivesInTheServiceAndNotAtItsCallers()
    {
        // Design-lessons rule 2, made structural. ShowAndActivate has three callers (the tray
        // menu's Open, the tray icon's click, and the single-instance activation), and a check
        // each of them has to remember is forgotten at caller N+1: that is the defect the Task 1
        // review found. This census is what fails when a fourth caller reintroduces its own copy.
        //
        // TrayIconViewModel is the one file outside the service allowed to name IsExiting, and it
        // reads it for Exit, which is a different action: RequestExit, not ShowAndActivate.
        Assert.Equal(
            ["TrayIconViewModel.cs", "WindowResidencyService.cs"],
            SourceScan.FilesMatching(@"\bIsExiting\b"));
    }

    [Fact]
    public void ShowAndActivate_WithNoWindowAttached_DoesNothing()
    {
        var residency = new Harness().Create();

        // Spec 12.11 behaviour 9 starts the process with no window at all, and behaviour 3's
        // activation can arrive before one exists, so this is a real state and not a test-only
        // one.
        residency.ShowAndActivate();

        Assert.False(residency.IsWindowVisible);
    }

    [AvaloniaFact]
    public void IsWindowVisible_FollowsHideAndShow()
    {
        var harness = new Harness { General = new GeneralSettings { CloseToTray = true } };
        var residency = harness.Create();
        var window = new Window();
        residency.Attach(window);

        Assert.False(residency.IsWindowVisible);

        // The desktop lifetime, not this service, shows the first window, so the property has to
        // follow the window rather than only the calls this service makes.
        window.Show();
        Assert.True(residency.IsWindowVisible);

        Assert.True(residency.HandleUserClose());
        Assert.False(residency.IsWindowVisible);

        residency.ShowAndActivate();
        Assert.True(residency.IsWindowVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void IsWindowVisible_WithAHideAndAShowInOneDispatcherTurn_EndsTrue()
    {
        // Phase review finding P1, and the one case in the suite that can see it. UiPost.Default
        // is Dispatcher.UIThread.Post and never runs inline, so the same-value guard used to
        // compare the requested value against the APPLIED property: a hide followed by a show
        // before the queue drained posted false, then found IsWindowVisible still true and
        // returned without posting, and the queued false landed afterwards, leaving the property
        // false with the window on screen. Every other case here binds action => action(), which
        // applies each mutation before the next call and therefore cannot reach it.
        //
        // HandleMinimized's HideWindow-then-WindowState pair is the production shape that reaches
        // it, and ScanCompletionWatcher reads this property as spec 12.11 behaviour 10's first
        // condition, so a stale answer shows a completion notice with the window up or suppresses
        // one that was due.
        var post = new RecordingPost();
        var harness = new Harness { Post = post.Post };
        var residency = harness.Create();
        var window = new Window();
        window.Show();
        residency.Attach(window);
        post.Drain();

        Assert.True(residency.IsWindowVisible);

        window.Hide();
        window.Show();

        // Both transitions reached the seam: the second is not swallowed by a guard reading a
        // property the first has not been applied to yet.
        Assert.Equal(2, post.Queued);

        post.Drain();

        Assert.True(window.IsVisible);
        Assert.True(residency.IsWindowVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void Attach_CalledTwice_ReplacesTheBindingAndLogs()
    {
        var harness = new Harness();
        var residency = harness.Create();
        var first = new Window();
        var second = new Window();
        residency.Attach(first);
        residency.Attach(second);

        Assert.Same(second, residency.AttachedWindow);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("residency service"));

        first.Close();
        second.Close();
    }

    [AvaloniaFact]
    public void Attach_OnTheShell_BindsTheWindowsOwnClosePolicy()
    {
        // The shell's two overrides are what convert a close and a minimize; Attach is what gives
        // them the service to ask. One owner of "what a close means", reached from the window.
        var harness = new Harness { General = new GeneralSettings { CloseToTray = true } };
        var residency = harness.Create();
        var window = new MainWindow();
        window.Show();
        residency.Attach(window);

        var args = WindowClosingProbe.RaiseClosing(window, WindowCloseReason.WindowClosing);

        Assert.True(args.Cancel);
        Assert.False(window.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void TheShell_WhileExiting_DoesNotCancelItsOwnClose()
    {
        var harness = new Harness { General = new GeneralSettings { CloseToTray = true } };
        var residency = harness.Create();
        var window = new MainWindow();
        window.Show();
        residency.Attach(window);
        residency.OnShutdownRequested();

        var args = WindowClosingProbe.RaiseClosing(window, WindowCloseReason.ApplicationShutdown);

        Assert.False(args.Cancel);

        window.Close();
    }

    [AvaloniaFact]
    public void TheShell_WithNoResidencyService_ClosesNormally()
    {
        // The headless harness and any surface that builds this window without a host.
        var window = new MainWindow();
        window.Show();

        var args = WindowClosingProbe.RaiseClosing(window, WindowCloseReason.WindowClosing);

        Assert.False(args.Cancel);

        window.Close();
    }

    // ---- exiting ------------------------------------------------------------------------------

    [Fact]
    public void RequestExit_AsksTheLifetimeToShutDownExactlyOnce()
    {
        var harness = new Harness();
        var residency = harness.Create();

        residency.RequestExit();

        Assert.Equal(1, harness.ShutdownRequests);
        Assert.True(residency.IsExiting);
    }

    [Fact]
    public void RequestExit_CalledTwice_AsksOnce()
    {
        var harness = new Harness();
        var residency = harness.Create();

        residency.RequestExit();
        residency.RequestExit();

        Assert.Equal(1, harness.ShutdownRequests);
    }

    [Fact]
    public void RequestExit_WhenTheShutdownIsRefused_LogsAndLetsTheNextPressAskAgain()
    {
        // Review finding 1, coordinator ruling. The flag sticks only after a request that was
        // accepted: a stuck flag would make every later Exit a no-op, make the tray menu's Open a
        // no-op, and stop the next title-bar close being converted, which under
        // ShutdownMode.OnExplicitShutdown destroys the window for real and leaves a running
        // process with no window and no way out.
        var harness = new Harness { ShutdownSucceeds = false };
        var residency = harness.Create();

        residency.RequestExit();

        Assert.False(residency.IsExiting);
        Assert.Equal(1, harness.ShutdownRequests);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("refused"));

        residency.RequestExit();

        Assert.Equal(2, harness.ShutdownRequests);

        harness.ShutdownSucceeds = true;
        residency.RequestExit();

        Assert.Equal(3, harness.ShutdownRequests);
        Assert.True(residency.IsExiting);
    }

    [AvaloniaFact]
    public void HandleUserClose_AfterARefusedShutdown_StillConvertsTheNextClose()
    {
        // The other half of finding 1, from the user's side: a refused shutdown must not leave the
        // window unclosable-to-tray, and it must not leave the next close destroying the window
        // while the process stays alive.
        var harness = new Harness
        {
            General = new GeneralSettings { CloseToTray = true },
            ShutdownSucceeds = false,
        };
        var residency = harness.Create();
        var window = new Window();
        window.Show();
        residency.Attach(window);

        residency.RequestExit();

        Assert.True(residency.HandleUserClose());
        Assert.False(window.IsVisible);

        window.Close();
    }

    [Fact]
    public void OnShutdownRequested_MarksTheServiceExiting_WithoutAskingForAShutdown()
    {
        // A shutdown the platform started (a logout, a session end). The drain still runs, and the
        // window closes that follow it must not be converted back into hides.
        var harness = new Harness();
        var residency = harness.Create();

        residency.OnShutdownRequested();

        Assert.True(residency.IsExiting);
        Assert.Equal(0, harness.ShutdownRequests);
    }
}
