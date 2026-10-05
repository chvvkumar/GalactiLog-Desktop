using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GalactiLog.App.Services;
using GalactiLog.App.Services.Tray;
using GalactiLog.App.ViewModels;
using GalactiLog.App.ViewModels.Tray;
using GalactiLog.App.Views;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App;

public partial class App : Application
{
    // Set by Program.Main once AppHost.Build succeeds, before the window shows.
    // OnFrameworkInitializationCompleted below is the only code that reads it: it resolves the
    // root view-model and the two startup services, and everything else is constructor-injected
    // from there. Views and view-models never touch it, so nothing in the application is a
    // service locator and every view-model stays constructible in a test (design-spec 18.3).
    public static IServiceProvider? Services { get; set; }

    // Spec 12.11 behaviour 3 (Phase 11 Task 1). Set by Program.Main once the single-instance claim
    // is held, which is before AppHost.Build and therefore before Services above. The second
    // static hand-off from Main to the Application, and read by exactly one method,
    // OnFrameworkInitializationCompleted, for the same reason Services is: the gate has to exist
    // before a container does, so it cannot come from one. Null under the headless test harness.
    public static ISingleInstanceGate? Gate { get; set; }

    // Spec 12.11 behaviour 9 (Phase 11 Task 4). Set by Program.Main from the launch's command
    // line, before the gate and therefore before Services above. The third and last static
    // hand-off from Main to the Application. Nothing here reads it: AppHost.Build composes it with
    // general.start_minimized into the one StartupState, which is what the window decision below
    // and spec 12.8's "Started minimized" field both read, so the page and the window cannot
    // disagree. Null under the headless test harness. Main assigns it before the CLI branch, so
    // it is non-null on the CLI path as well; it is simply never read there, because that branch
    // returns before Avalonia is built and the only reader is AppHost's StartupState factory.
    //
    // No test may assign this (phase review finding P9). AppHost.Build composes it into the one
    // StartupState at registration time, AppHost.Build is called from many cases across
    // GalactiLog.App.Tests, and xUnit runs collections in parallel, so an assignment made by one
    // case would leak into every host built after it in the assembly with no way to scope it.
    // Program.Main is the only assignment in the solution. A case that must vary it takes an
    // assembly-level collection fixture that restores the previous value.
    public static StartupArguments? StartupArguments { get; set; }

    // Spec 10.5's shutdown budget.
    internal static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(5);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Ruling Q13: the stored general.theme is applied here, once, before any window is
            // built, and it carries spec 13's global LiveCharts2 configuration with it.
            // ThemeManager.ApplyStored merges the stored theme's dictionary, sets the theme
            // variant, and then calls ChartTheme.Apply() last and unconditionally, which is the
            // order ruling Q19 fixes and which this line used to perform on its own against
            // whatever App.axaml had merged by hand. Here rather than in AppHost.Build, which
            // spec 13 names: Program.Main builds the host before Avalonia starts, so
            // Application.Current and the merged token dictionary do not exist during Build, and
            // Build is also the CLI's entry point. This line runs immediately before the main
            // window is constructed, which is the earliest point the tokens exist and a point the
            // CLI structurally never reaches (ruling Q2).
            //
            // StartupTheme is resolvable only when a container exists. The headless test harness
            // has none, so null there applies the default theme, which is what this line did
            // before. AppHost read the id once on Program.Main's thread, so nothing opens a
            // database in front of the first frame.
            Theme.ThemeManager.ApplyStored(Services?.GetService<Theme.StartupTheme>());

            // Spec 12.11: hiding the window must not end the process, and a session that starts
            // into the tray has no window at all (behaviour 9). OnLastWindowClose, the framework
            // default, exits when the last window CLOSES; explicit is what makes the exit path
            // single, because with OnExplicitShutdown there is exactly one way out of the process
            // and it is WindowResidencyService.RequestExit. Set before desktop.MainWindow is
            // assigned, so no window can ever be closed under the old mode (ruling Q3).
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Spec 12.11 behaviour 9 (ruling Q10 (a)). A start into the tray builds no window at
            // all: not a window that is built and then hidden, because building it resolves
            // MainWindowViewModel and through it the shell's rail and the first page, which is the
            // startup cost this mode exists to avoid on a machine that boots into a scan. What is
            // deferred is that and nothing more (phase review finding P7): the status bar is
            // resolved on every start regardless, because TrayIconViewModel takes it, and so is
            // ThumbnailWorker below. desktop.MainWindow is therefore left null and the residency
            // service builds the shell on the first ShowAndActivate, which is what the tray menu's
            // Open, the tray icon's click and a second launch's activation all call.
            //
            // The classic desktop lifetime runs its main loop with a null MainWindow under
            // ShutdownMode.OnExplicitShutdown and does not exit. That is asserted rather than
            // assumed, by Services/StartMinimizedTests.
            // TheClassicDesktopLifetime_WithOnExplicitShutdown_RunsWithNoMainWindow.
            //
            // StartupState is resolvable only when a container exists. The headless test harness
            // has none, so that start keeps exactly the behaviour it had before this task and
            // gets a window.
            var showWindow = Services?.GetService<StartupState>() is not { StartedMinimized: true };

            // The shell, as one expression with two call sites: built here on an ordinary start,
            // and built by the residency service on the first activation of a start that had
            // none. Assigning desktop.MainWindow is part of building it either way, because
            // ModalHost's owner window and the clipboard writer both read the lifetime's
            // MainWindow (AppHost), and a shell the lifetime does not know about would leave the
            // setup wizard, every dialog and Copy without one.
            //
            // Services is null under the headless test harness, so the window opens with a
            // null DataContext there and the App tests set their own (design-spec 18.3).
            Window NewShell()
            {
                var shell = new MainWindow
                {
                    DataContext = Services?.GetRequiredService<MainWindowViewModel>(),
                };
                desktop.MainWindow = shell;

                // Spec 12.1's first-run branch and spec 12.11 behaviour 9's last clause, "the
                // wizard appears the first time the user opens the window". Here, inside the one
                // shell construction, rather than beside the service starts below: the wizard
                // needs an owner window, so "a window was built" is exactly its precondition, and
                // this is the only place a window is ever built. A start into the tray therefore
                // shows no wizard while it has no window, and shows it on the first Open from the
                // tray, both without a second decision to keep in step (ruling Q20, Task 4
                // review Important 2).
                //
                // Posted rather than opened inline, on both paths, because ModalHost needs an
                // owner that exists and is visible: on an ordinary start the lifetime shows this
                // window after OnFrameworkInitializationCompleted returns, and on the deferred
                // path WindowResidencyService.ShowAndActivate shows it immediately after this
                // factory returns. Either way the post runs afterwards.
                //
                // The flag is read off the UI thread and before the post, never inside the posted
                // closure: SettingsStore.GetGeneral opens a SQLite context and the first-frame
                // path must not carry one for a single boolean (Task 9 review, minor finding 9).
                // Inside the Services branch, so the headless test harness never reaches it.
                if (Services is { } wizardServices)
                {
                    var settings = wizardServices.GetRequiredService<GalactiLog.Data.SettingsStore>();
                    var setupComplete = Task.Run(() => settings.GetGeneral().SetupComplete);
                    Dispatcher.UIThread.Post(
                        () => _ = ShowSetupWizardIfNeededAsync(wizardServices, setupComplete));
                }

                return shell;
            }

            if (showWindow)
            {
                NewShell();
            }

            // GUI only, by construction: the CLI branch of Program.Main returns before
            // BuildAvaloniaApp() is reached, so spec 15's "the CLI never starts the watcher
            // or the scheduler" needs no runtime check here. Services is null under the
            // headless test harness, which starts neither.
            if (Services is { } services)
            {
                // Spec 12.11: the one owner of whether the window is on screen and the one path
                // out of the process. Bound to TryShutdown and never to Shutdown, because the
                // Avalonia documentation only promises that TryShutdown raises ShutdownRequested,
                // and ShutdownRequested is where the one drain lives.
                var residency = services.GetRequiredService<WindowResidencyService>();
                if (desktop.MainWindow is { } shell)
                {
                    residency.Attach(shell);
                }
                else
                {
                    // Spec 12.11 behaviour 9. The factory goes to the residency service rather
                    // than to the three callers of ShowAndActivate, because this service is the
                    // one owner of whether a window is on screen and one of those callers is
                    // TrayIconViewModel, where no factory could be placed without giving the tray
                    // its own answer to that question (design-lessons rules 1 and 2).
                    residency.AttachLater(NewShell);
                }

                // Spec 12.11 behaviour 4 (ruling R30): the application's own Shell_NotifyIcon,
                // because Avalonia 11.3's TrayIcon misplaces its menu under DPI scaling. Eager,
                // since the icon outlives any window (behaviour 9); removed on Exit.
                var tray = services.GetRequiredService<TrayIconViewModel>();
                if (OperatingSystem.IsWindows())
                {
                    // Typed as IDisposable: CA1416 cannot see the guard above from inside the lambda.
                    IDisposable trayIcon = new NativeTrayIcon(tray, services.GetRequiredService<ILogger<NativeTrayIcon>>());
                    desktop.Exit += (_, _) => trayIcon.Dispose();
                }

                services.GetRequiredService<WatcherService>().Start();
                services.GetRequiredService<ScanScheduler>().Start();

                // Spec 17.1's update loop (Phase 10 Task 4), started here for the same reason the
                // two above are: the CLI branch of Program.Main returns before Avalonia is built,
                // so the CLI structurally never checks for updates (spec 15). A build the updater
                // did not install starts no loop at all, so a dotnet run reaches no network, and
                // a failed check logs at warning rather than disrupting startup.
                services.GetRequiredService<UpdateService>().Start();

                // Spec 12.11 behaviour 10 (Phase 11 Task 5), started here for the same reason the
                // three above are: the CLI branch of Program.Main returns before Avalonia is
                // built, so a CLI scan never notifies (spec 15). Off by default, so a start with
                // the setting untouched subscribes and does nothing.
                services.GetRequiredService<ScanCompletionWatcher>().Start();

                // Spec 12.11 behaviour 3 (Phase 11 Task 1). Started here, beside the three above
                // and after the window exists, because a second launch's activation has to reach a
                // window rather than a half-built application. The gate raises this on its own
                // thread, so it is posted like every other cross-thread publish in this
                // application, and it calls the one residency service rather than touching the
                // window, so "bring the application up" has one implementation shared with the
                // tray icon's click and the tray menu's Open (design-lessons rule 1).
                //
                // The exiting check is inside ShowAndActivate and not here (Task 1 review, the
                // important finding): with three callers, a guard each one has to remember is
                // forgotten at caller N+1, and it already had been by the tray icon's click above.
                // The service reads the flag at the moment the window would be shown, which is
                // what the post preserves: an activation arriving while an accepted shutdown is in
                // flight is dropped rather than resurrecting a window the drain is tearing down,
                // and a refused shutdown reverts the flag so the next launch is honoured.
                Gate?.StartListening(() => Dispatcher.UIThread.Post(residency.ShowAndActivate));

                // Resolved here rather than inside the handler below: the worker's two pump
                // tasks belong to the running application, and a session that never opened a
                // preview would otherwise construct it at shutdown purely in order to dispose
                // it. Everything the drain touches is now live before the first window paints.
                var thumbnails = services.GetRequiredService<ThumbnailWorker>();

                // Spec 10.5: the process must not exit with a scan mid-write. Subscribed
                // here rather than in AppHost because the desktop lifetime is what raises it.
                desktop.ShutdownRequested += (_, _) =>
                {
                    // Spec 12.11 behaviours 5 and 7. Set FIRST, so the window closes the lifetime
                    // performs after this handler are not converted back into hides by
                    // MainWindow.OnClosing: Avalonia documents that a ShutdownRequested which is
                    // not cancelled proceeds to close every non-owned window, raising each one's
                    // Closing event. Without this the application would cancel its own shutdown.
                    residency.OnShutdownRequested();

                    // Spec 12.11 behaviour 10. Outside the drain's budget, like the residency
                    // flag above: detaching an event handler returns at once and there is nothing
                    // to wait for, so DrainForShutdown's signature, body and budget are unchanged
                    // and it still has exactly one call site in src.
                    services.GetRequiredService<ScanCompletionWatcher>().Stop();

                    // Spec 12.11 behaviour 3 (Phase 11 Task 1). Outside the drain's budget for the
                    // same reason as the two above: it sets one event and joins one thread that is
                    // waiting on a kernel handle, which returns at once, so DrainForShutdown's
                    // signature, body and budget are unchanged and it still has exactly one call
                    // site in src. After residency.OnShutdownRequested() rather than before it,
                    // because that flag must be set first for the lifetime's own window closes
                    // (Task 2's rule) and stopping a listener cannot affect it either way.
                    Gate?.StopListening();

                    DrainForShutdown(
                        services.GetRequiredService<WatcherService>(),
                        services.GetRequiredService<ScanScheduler>(),
                        services.GetRequiredService<ScanCoordinator>(),
                        thumbnails,
                        services.GetRequiredService<UpdateService>());
                };

                // Spec 12.1's setup wizard is posted from NewShell above, which is the one place a
                // window is built and therefore the one place the wizard has an owner. It is not
                // repeated here, and it is not gated on anything the watcher and the scheduler are
                // gated on: those start as they always have, because on a first run there are no
                // roots for either to act on and gating them on the wizard would add a second
                // startup ordering to reason about (questions.md Q35).
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Spec 12.11 behaviour 9: whether this start puts a window on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Either the switch or the stored setting starts into the tray, and the switch wins when they
    /// disagree, because the switch is this launch and the setting is every launch. That is an
    /// "or" and not a precedence table: the switch can only ask for the tray, so a launch that
    /// carries it starts into the tray whatever the setting says, and a launch that does not
    /// carries no opinion for the setting to be overridden by.
    /// </para>
    /// <para>
    /// Internal, static and free of Avalonia types, so App.Tests can exercise the decision without
    /// a lifetime, exactly as <see cref="DrainForShutdown(WatcherService, ScanScheduler,
    /// ScanCoordinator, ThumbnailWorker?, UpdateService?)"/> and
    /// <see cref="ShowSetupWizardIfNeededAsync"/> are. It is the one owner of the rule:
    /// <c>AppHost.Build</c> calls it once to compose <see cref="StartupState"/>, which spec 12.8's
    /// "Started minimized" field then reports, so the Diagnostics page says what actually
    /// happened rather than what was asked for.
    /// </para>
    /// <para>
    /// The settings document arrives by value, from the one read <c>AppHost.Build</c> already
    /// performs at the top of that method. No second settings read is added to the startup path
    /// (phase review item 2).
    /// </para>
    /// </remarks>
    /// <param name="minimizedSwitch">Whether this launch carried <c>--minimized</c>.</param>
    /// <param name="general">The <c>general</c> document, as <c>AppHost.Build</c> read it.</param>
    internal static bool ShouldShowWindowAtStartup(bool minimizedSwitch, GeneralSettings general)
        => !minimizedSwitch && !general.StartMinimized;

    /// <summary>
    /// Spec 12.1's first-run branch: opens the setup wizard when <c>general.setup_complete</c> is
    /// false, on the one <c>ModalHost</c>, over the main window.
    /// </summary>
    /// <remarks>
    /// Internal and free of Avalonia types beyond the service lookups, so App.Tests can exercise
    /// the decision without a window, the way <see cref="DrainForShutdown(WatcherService,
    /// ScanScheduler, ScanCoordinator, ThumbnailWorker?, UpdateService?)"/> is. A failed settings
    /// read or a failed
    /// show is logged and swallowed: a wizard that could not be opened must not take the
    /// application's startup down with it, and the flag stays false so the next start tries again.
    /// </remarks>
    /// <param name="services">The application's service provider.</param>
    /// <param name="setupComplete"><c>general.setup_complete</c>, read off the UI thread by the
    /// caller so the first-frame path carries no SQLite read.</param>
    internal static async Task ShowSetupWizardIfNeededAsync(
        IServiceProvider services, Task<bool> setupComplete)
    {
        try
        {
            if (await setupComplete.ConfigureAwait(true))
            {
                return;
            }

            await services.GetRequiredService<SetupWizardService>().ShowAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "The setup wizard could not be shown at startup");
        }
    }

    /// <summary>
    /// Spec 10.5's shutdown drain (FIXER LIST 4). Stops the two triggers FIRST, so nothing can
    /// start a new scan while the drain runs, then cancels the run in flight (which also
    /// clears the pending flag, review item 2) and waits for the coordinator to go idle.
    /// Returns whether it reached idle inside the budget.
    /// </summary>
    /// <remarks>
    /// Blocking on the UI thread is deliberate and bounded: the process is exiting, and the
    /// alternative is a scan killed mid-write. Internal and free of Avalonia types so
    /// App.Tests can exercise it without a window.
    /// </remarks>
    internal static bool DrainForShutdown(
        WatcherService watcher,
        ScanScheduler scheduler,
        ScanCoordinator coordinator,
        ThumbnailWorker? thumbnails = null,
        UpdateService? updates = null)
        => DrainForShutdown(watcher, scheduler, coordinator, thumbnails, ShutdownDrainTimeout, updates);

    /// <summary>The drain above with the budget as an argument, so a test can assert the
    /// sharing arithmetic without spending five real seconds.</summary>
    internal static bool DrainForShutdown(
        WatcherService watcher,
        ScanScheduler scheduler,
        ScanCoordinator coordinator,
        ThumbnailWorker? thumbnails,
        TimeSpan budget,
        UpdateService? updates = null)
    {
        watcher.Stop();
        scheduler.Stop();

        // Spec 17.1's update loop (Phase 10 Task 4). Stopped with the other two triggers, and
        // deliberately outside the shared budget below: it holds no database write and no partial
        // file, so cancelling it is the whole of its shutdown and there is nothing to wait for.
        // A download in flight is abandoned; the updater stages its own packages and the next
        // start checks again.
        updates?.Stop();

        // Phase 8 Task 5, TRACKING section 6 item 6. Spec 10.5's budget is five seconds for the
        // whole shutdown, not five per subsystem, so the worker and the scan share one clock:
        // cancelling the worker returns at once (its renders unwind on their own and every
        // outstanding callback is released rather than left pending; the completions are posted
        // through the dispatcher that is being shut down, so they may never be delivered, and no
        // window outlives them to show a spinner), the scan gets the budget it has always had, and
        // the worker's pumps are joined with whatever is left of it. Nothing to join left means
        // nothing is waited for.
        //
        // The one budget bounds the user-visible half. If it is exhausted here, DisposeWithin
        // returns false without disposing the worker's cancellation source, and the container's
        // own ThumbnailWorker.Dispose() at guiHost.Dispose() waits a fresh five seconds. That is
        // after the window is gone, so it delays process exit and nothing else.
        var elapsed = Stopwatch.StartNew();
        thumbnails?.Cancel();

        coordinator.Cancel();
        var idle = coordinator.WaitForIdleAsync(budget).GetAwaiter().GetResult();

        thumbnails?.DisposeWithin(budget - elapsed.Elapsed);

        if (!idle)
        {
            // The scan_runs row is left "running"; ScanRunRepository.MarkInterrupted closes it
            // at the next start (review item 3).
            Serilog.Log.Warning(
                "Shutdown drain budget of {Seconds}s elapsed with a scan still running; exiting anyway",
                budget.TotalSeconds);
        }
        return idle;
    }
}
