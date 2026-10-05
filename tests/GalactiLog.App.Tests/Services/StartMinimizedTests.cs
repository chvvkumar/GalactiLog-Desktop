using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GalactiLog.App.Tests.Services;

// Spec 12.11 behaviour 9 (Phase 11 Task 4): the process starts into the tray with no window, and
// the file watcher, the interval scheduler and the update loop start exactly as they do on an
// ordinary start. The second half is the one that matters: a minimized start that does not scan
// is a feature that does nothing.
//
// Three shapes of assertion here, each for a reason.
//
//   1. One AvaloniaFact over a real ClassicDesktopStyleApplicationLifetime, which is the premise
//      the whole design rests on and the one thing ruling Q10 (a) asked to be proved rather than
//      assumed.
//   2. Plain [Fact]s over App.ShouldShowWindowAtStartup, the decision extracted out of
//      OnFrameworkInitializationCompleted so a test can reach it, exactly as App.DrainForShutdown
//      and App.ShowSetupWizardIfNeededAsync already are.
//   3. SourceScan text reads over Program.cs and App.axaml.cs, because what they prove is an
//      ordering and an absence inside two methods no in-process test can call: Program.Main is a
//      process entry point, and OnFrameworkInitializationCompleted needs a desktop lifetime the
//      headless harness does not build (Application.Current.ApplicationLifetime is null under it,
//      and Avalonia refuses to have one assigned after initialization).
public class StartMinimizedTests
{
    private static string ProgramSource() =>
        SourceScan.StripComments(
            File.ReadAllText(Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "Program.cs")));

    private static string AppSource() =>
        SourceScan.StripComments(
            File.ReadAllText(Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", "App.axaml.cs")));

    // ---- the premise --------------------------------------------------------------------------

    /// <summary>
    /// Ruling Q10 (a)'s premise, asserted before anything is built on it: the classic desktop
    /// lifetime under <c>ShutdownMode.OnExplicitShutdown</c> enters and stays in its main loop
    /// with a null <c>MainWindow</c>, so a start into the tray does not exit at boot.
    /// </summary>
    /// <remarks>
    /// The lifetime is constructed here rather than taken from <c>Application.Current</c>: the
    /// headless harness leaves <c>ApplicationLifetime</c> null and Avalonia throws on an attempt
    /// to assign one after initialization, so this is the only way an in-process case can exercise
    /// the real type. Two jobs are posted before <c>Start</c>: the first records that the loop was
    /// entered, the second ends it. If a null <c>MainWindow</c> made the lifetime exit at once,
    /// <c>Start</c> would return with neither job having run.
    /// </remarks>
    [AvaloniaFact]
    public void TheClassicDesktopLifetime_WithOnExplicitShutdown_RunsWithNoMainWindow()
    {
        using var lifetime = new ClassicDesktopStyleApplicationLifetime
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
            MainWindow = null,
        };

        var exits = 0;
        lifetime.Exit += (_, _) => exits++;

        var loopEntered = false;
        var exitsBeforeTheExplicitShutdown = -1;
        Dispatcher.UIThread.Post(() =>
        {
            loopEntered = true;
            exitsBeforeTheExplicitShutdown = exits;
            Dispatcher.UIThread.Post(() => lifetime.Shutdown());
        });

        var code = lifetime.Start([]);

        Assert.True(loopEntered, "the lifetime exited before running a single posted job");
        Assert.Equal(0, exitsBeforeTheExplicitShutdown);
        Assert.Equal(1, exits);
        Assert.Equal(0, code);
    }

    // ---- the decision -------------------------------------------------------------------------

    [Fact]
    public void ANormalStart_ShowsTheWindow()
    {
        Assert.True(App.ShouldShowWindowAtStartup(minimizedSwitch: false, new GeneralSettings()));
    }

    [Fact]
    public void StartMinimized_ByArgument_ShowsNoWindow()
    {
        Assert.False(App.ShouldShowWindowAtStartup(minimizedSwitch: true, new GeneralSettings()));
    }

    [Fact]
    public void StartMinimized_BySetting_ShowsNoWindow()
    {
        Assert.False(App.ShouldShowWindowAtStartup(
            minimizedSwitch: false, new GeneralSettings { StartMinimized = true }));
    }

    // Spec 12.11 behaviour 9: the argument applies to one launch, the setting to every launch, and
    // the argument wins when they disagree. It can only ask for the tray, so a launch that carries
    // it starts into the tray whatever the stored setting says.
    [Fact]
    public void TheArgument_WinsOverTheStoredSetting()
    {
        Assert.False(App.ShouldShowWindowAtStartup(
            minimizedSwitch: true, new GeneralSettings { StartMinimized = false }));
        Assert.False(App.ShouldShowWindowAtStartup(
            minimizedSwitch: true, new GeneralSettings { StartMinimized = true }));
    }

    // Spec 12.11 behaviour 9's last sentence and ruling Q20. The wizard is not shown on a start
    // into the tray, and that is the same decision as the window's: no window, no owner for
    // ModalHost, no wizard. The setup_complete flag does not enter into it.
    [Fact]
    public void StartMinimized_OnAFirstRun_DoesNotShowTheSetupWizard()
    {
        var firstRun = new GeneralSettings { SetupComplete = false, StartMinimized = true };

        Assert.False(App.ShouldShowWindowAtStartup(minimizedSwitch: false, firstRun));

        // The one wizard post lives inside the one shell factory, so a start that builds no window
        // performs neither the settings read nor the post.
        // The call site, not the declaration further down the file.
        var app = AppSource();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            app, @"_ = ShowSetupWizardIfNeededAsync\("));
        Assert.True(
            IsInsideTheShellFactory(
                app, app.IndexOf("_ = ShowSetupWizardIfNeededAsync(", StringComparison.Ordinal)),
            "the setup wizard must be posted from the one place a window is built.");
    }

    /// <summary>
    /// Spec 12.11 behaviour 9's last clause, "the wizard appears the first time the user opens the
    /// window" (Task 4 review, Important 2). The wizard post is inside <c>NewShell</c>, which is
    /// what the residency service calls on the first <c>ShowAndActivate</c> of a start that built
    /// no window, so opening from the tray on a first run reaches it.
    /// </summary>
    [Fact]
    public void StartMinimized_ThenFirstOpen_ShowsTheSetupWizard()
    {
        var app = AppSource();

        var factory = app.IndexOf("Window NewShell()", StringComparison.Ordinal);
        var deferred = app.IndexOf("residency.AttachLater(NewShell)", StringComparison.Ordinal);
        var wizard = app.IndexOf("_ = ShowSetupWizardIfNeededAsync(", StringComparison.Ordinal);

        Assert.True(factory >= 0, "App.axaml.cs no longer builds the shell through one factory.");
        Assert.True(deferred >= 0, "App.axaml.cs no longer defers the shell build to the residency service.");
        Assert.True(wizard >= 0, "App.axaml.cs no longer posts the setup wizard.");
        Assert.True(IsInsideTheShellFactory(app, wizard));
        Assert.False(
            IsInsideTheWindowBranch(app, wizard),
            "the wizard must not be gated on the startup window decision; it is gated on a window existing.");
    }

    [Fact]
    public void StartMinimized_DoesNotSuppressTheSetupWizardOnALaterNormalStart()
    {
        var firstRun = new GeneralSettings { SetupComplete = false, StartMinimized = false };

        Assert.True(App.ShouldShowWindowAtStartup(minimizedSwitch: false, firstRun));
    }

    // ---- the window on first activation --------------------------------------------------------

    /// <summary>
    /// Ruling Q10 (a)'s other half: a start that built no window builds it on the first
    /// <c>ShowAndActivate</c>, and the residency service stays the one owner of that.
    /// </summary>
    [AvaloniaFact]
    public void StartMinimized_ThenShowAndActivate_ShowsTheWindow()
    {
        var builds = 0;
        var residency = new WindowResidencyService(
            () => new GeneralSettings(),
            post: action => action(),
            logger: NullLogger<WindowResidencyService>.Instance);

        Window Build()
        {
            builds++;
            return new Window();
        }

        residency.AttachLater(Build);
        Assert.Equal(0, builds);
        Assert.False(residency.IsWindowVisible);

        residency.ShowAndActivate();

        Assert.Equal(1, builds);
        Assert.True(residency.IsWindowVisible);
        Assert.NotNull(residency.AttachedWindow);
        Assert.True(residency.AttachedWindow!.IsVisible);
    }

    [AvaloniaFact]
    public void StartMinimized_ThenTwoActivations_BuildOneWindow()
    {
        var builds = 0;
        var residency = new WindowResidencyService(
            () => new GeneralSettings(),
            post: action => action(),
            logger: NullLogger<WindowResidencyService>.Instance);

        residency.AttachLater(() =>
        {
            builds++;
            return new Window();
        });

        residency.ShowAndActivate();
        var first = residency.AttachedWindow;
        residency.ShowAndActivate();

        Assert.Equal(1, builds);
        Assert.Same(first, residency.AttachedWindow);
    }

    // An ordinary start attaches a window and never consults a factory, so nothing can build a
    // second shell behind the one the lifetime is already showing.
    [AvaloniaFact]
    public void ANormalStart_NeverCallsTheDeferredFactory()
    {
        var builds = 0;
        var residency = new WindowResidencyService(
            () => new GeneralSettings(),
            post: action => action(),
            logger: NullLogger<WindowResidencyService>.Instance);

        var window = new Window();
        window.Show();
        residency.Attach(window);
        residency.AttachLater(() =>
        {
            builds++;
            return new Window();
        });

        residency.ShowAndActivate();

        Assert.Equal(0, builds);
        Assert.Same(window, residency.AttachedWindow);
    }

    // A shell that cannot be constructed must not take the process down and must not be retried on
    // every later tray click.
    [AvaloniaFact]
    public void AFailedDeferredBuild_IsReportedOnceAndDoesNotThrow()
    {
        var builds = 0;
        var logger = new RecordingLogger();
        var residency = new WindowResidencyService(
            () => new GeneralSettings(),
            post: action => action(),
            logger: logger);

        residency.AttachLater(Window () =>
        {
            builds++;
            throw new InvalidOperationException("no shell today");
        });

        residency.ShowAndActivate();
        residency.ShowAndActivate();

        Assert.Equal(1, builds);
        Assert.Null(residency.AttachedWindow);
        Assert.False(residency.IsWindowVisible);
    }

    // ---- the services start regardless of the window --------------------------------------------

    // The roadmap row's Verify clause, behavioural half: the watcher and the scheduler start and
    // do their work with no window in the process at all. They take no window, no dispatcher and
    // no residency service, which is the structural reason a start into the tray scans; the
    // SourceScan case below is what keeps them out of the window branch.
    [Fact]
    public async Task StartMinimized_StartsTheWatcherAndTheScheduler()
    {
        Assert.False(App.ShouldShowWindowAtStartup(
            minimizedSwitch: true, new GeneralSettings { StartMinimized = true }));

        using var settings = new SettingsFixture();
        settings.Save(general => general with
        {
            ScanRoots = [settings.Root],
            WatcherEnabled = true,
            AutoScanIntervalMinutes = 60,
            StartMinimized = true,
        });

        var delay = new FakeDelay();
        var sources = new List<FakeWatcherSource>();
        var coordinator = ScanCoordinatorTestFactory.Create(settings);

        using var watcher = new WatcherService(
            settings.Store,
            (files, token) => coordinator.RunTargetedAsync(ScanTrigger.Watcher, files, token),
            token => coordinator.RunAsync(ScanTrigger.Watcher, null, token),
            NullLogger<WatcherService>.Instance,
            path => { var source = new FakeWatcherSource(path); sources.Add(source); return source; },
            delay.Delay);
        var scheduler = new ScanScheduler(
            settings.Store,
            () => coordinator.IsRunning,
            token => coordinator.RunAsync(ScanTrigger.Scheduler, null, token),
            NullLogger<ScanScheduler>.Instance,
            delay.Delay);

        watcher.Start();
        scheduler.Start();

        Assert.NotEmpty(sources);
        Assert.All(sources, source => Assert.True(source.Started));
        Assert.NotNull(scheduler.LoopTask);

        scheduler.Stop();
        watcher.Stop();
        await scheduler.LoopTask!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// The roadmap row's Verify clause, structural half, and the most important assertion in this
    /// task: the four <c>Start()</c> calls sit in the services block and none of them is inside
    /// the window branch this task added, so a start into the tray runs every one of them.
    /// </summary>
    [Fact]
    public void StartMinimized_StartsTheWatcherTheSchedulerAndTheUpdateLoop()
    {
        var app = AppSource();

        var windowDecision = app.IndexOf("var showWindow =", StringComparison.Ordinal);
        Assert.True(windowDecision >= 0, "App.axaml.cs no longer decides whether to show a window.");

        foreach (var start in new[]
                 {
                     "GetRequiredService<WatcherService>().Start()",
                     "GetRequiredService<ScanScheduler>().Start()",
                     "GetRequiredService<UpdateService>().Start()",
                     "GetRequiredService<ScanCompletionWatcher>().Start()",
                 })
        {
            var index = app.IndexOf(start, StringComparison.Ordinal);
            Assert.True(index >= 0, $"App.axaml.cs no longer starts {start}.");

            // The one branch on the window decision that may contain a Start() call is none.
            // Asserted by reading the text between the decision and the call: a Start() moved
            // inside "if (showWindow)" would have that opening brace unclosed before it.
            Assert.False(
                IsInsideTheWindowBranch(app, index),
                $"{start} must not be gated on whether a window was shown (spec 12.11 behaviour 9).");
        }
    }

    // ---- Program.Main's argument routing --------------------------------------------------------

    /// <summary>
    /// Spec 4.4 step 2 and ruling Q8 (a): the GUI switches are consumed before the CLI branch, so
    /// <c>GalactiLog.exe --minimized</c> starts the GUI instead of printing the usage text and
    /// exiting 2. Nothing precedes <c>VelopackApp.Build().Run()</c> (spec 17.1).
    /// </summary>
    [Fact]
    public void Program_ParsesTheStartupSwitchAfterVelopackAndBeforeTheCliBranch()
    {
        var program = ProgramSource();

        var velopack = program.IndexOf("VelopackApp.Build().Run()", StringComparison.Ordinal);
        var parse = program.IndexOf("StartupArguments.Parse(args)", StringComparison.Ordinal);
        var cli = program.IndexOf("CliDispatcher.TryRun", StringComparison.Ordinal);

        Assert.True(velopack >= 0, "Program.cs no longer runs Velopack's hooks first.");
        Assert.True(parse >= 0, "Program.cs does not parse the startup arguments.");
        Assert.True(cli >= 0, "Program.cs no longer dispatches the CLI the expected way.");
        Assert.True(velopack < parse, "Nothing may precede VelopackApp.Build().Run() (spec 17.1).");
        Assert.True(parse < cli, "The GUI switches must be consumed before the CLI branch.");
    }

    /// <summary>
    /// The CLI branch reads the stripped array, which is what makes
    /// <c>GalactiLog.exe --minimized</c> a GUI launch and leaves every other input on the branch
    /// it already took (spec 15).
    /// </summary>
    [Fact]
    public void Program_TakesTheCliBranchOnlyWhenArgumentsRemain()
    {
        var program = ProgramSource();

        Assert.Contains("var guiArgs = startup.Remaining", program, StringComparison.Ordinal);
        Assert.Contains("if (guiArgs.Length > 0)", program, StringComparison.Ordinal);
        Assert.DoesNotContain("if (args.Length > 0)", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ruling Q8 (a)'s other half, and the rule <c>GalactiLog.Cli.Tests</c> cannot assert for
    /// itself because it lives in <c>Program.Main</c>: the parse decides the branch and nothing
    /// else, so the dispatcher is handed the original array. Handing it the stripped one would
    /// turn <c>--minimized scan C:\x</c> into a real scan that opens the database and writes rows,
    /// would take a positional argument away from <c>scan C:\x --minimized</c>, and would turn
    /// <c>--minimized --help</c> from an unknown first token into a <c>--help</c>.
    /// </summary>
    [Fact]
    public void Program_HandsTheOriginalArgumentsToTheCliDispatcher()
    {
        var program = ProgramSource();

        Assert.Matches(@"CliDispatcher\.TryRun\(\s*args\s*,", program);
        Assert.DoesNotMatch(@"CliDispatcher\.TryRun\(\s*guiArgs", program);
    }

    /// <summary>
    /// A consumed switch is removed from the array Avalonia's lifetime receives, so the framework
    /// never sees a token this application invented.
    /// </summary>
    [Fact]
    public void Program_HandsTheStrippedArgumentsToAvalonia()
    {
        Assert.Contains(
            "StartWithClassicDesktopLifetime(guiArgs)", ProgramSource(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Spec 12.11 behaviour 1: a second launch carrying the switch is the Startup shortcut firing
    /// while the user is already running the application, so it asks for no activation and exits 0
    /// silently rather than yanking their window to the front.
    /// </summary>
    [Fact]
    public void Program_RequestsNoActivationOnAMinimizedLaunch()
    {
        Assert.Contains(
            "gate.TryAcquire(requestActivation: !startup.Minimized)",
            ProgramSource(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The started-minimized fact reaches the rest of the application through one static, set by
    /// <c>Main</c> before the host is built and before the window decision is taken.
    /// </summary>
    [Fact]
    public void Program_HandsTheStartupArgumentsToTheApplication()
    {
        var program = ProgramSource();

        var handOff = program.IndexOf("App.StartupArguments = startup", StringComparison.Ordinal);
        var build = program.IndexOf("AppHost.Build(cliMode: false", StringComparison.Ordinal);

        Assert.True(handOff >= 0, "Program.cs does not hand the startup arguments to the Application.");
        Assert.True(build >= 0, "Program.cs no longer builds the GUI host the expected way.");
        Assert.True(handOff < build, "The switch must be readable before AppHost.Build composes it.");
    }

    // ---- App.axaml.cs's window decision ---------------------------------------------------------

    /// <summary>
    /// Ruling Q10 (a) as landed: the shell is built only when the decision says to show it, and
    /// the residency service is the one that builds it otherwise.
    /// </summary>
    [Fact]
    public void App_BuildsNoWindowOnAStartIntoTheTray()
    {
        var app = AppSource();

        Assert.Contains(
            "var showWindow = Services?.GetService<StartupState>() is not { StartedMinimized: true }",
            app,
            StringComparison.Ordinal);
        Assert.Contains("residency.AttachLater(NewShell)", app, StringComparison.Ordinal);

        // One construction expression for the shell, reached from two places, so a start into the
        // tray cannot end up with a differently built window from an ordinary start.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(app, @"new MainWindow\b"));
    }

    /// <summary>
    /// <c>WindowResidencyService</c> stays the one owner of whether a window is on screen: nothing
    /// in <c>src</c> calls <c>Show()</c> outside it.
    /// </summary>
    /// <remarks>
    /// The pattern is the bare member call, not a named receiver (review minor finding): a second
    /// owner introduced as <c>shell.Show()</c>, <c>_window.Show()</c> or <c>MainWindow.Show()</c>
    /// has to appear in this census too. The allowlist is a name list, the shape
    /// <c>FileSafetyTest</c>'s own groups use. <c>ShowDialog</c> is a different member and is not
    /// matched, which is correct: <c>ModalHost</c> owns modals and this rule is about the shell.
    /// </remarks>
    [Fact]
    public void TheShell_IsShownByTheResidencyServiceAlone()
    {
        string[] allowlist = ["WindowResidencyService.cs"];

        Assert.Equal(allowlist, SourceScan.FilesMatching(@"\.Show\(\)"));
    }

    private static bool IsInsideTheWindowBranch(string source, int index)
        => IsInsideABlockOpenedBy(source, "if (showWindow)", index);

    private static bool IsInsideTheShellFactory(string source, int index)
        => IsInsideABlockOpenedBy(source, "Window NewShell()", index);

    /// <summary>
    /// Whether <paramref name="index"/> falls inside the braced block that follows an occurrence of
    /// <paramref name="opener"/>.
    /// </summary>
    /// <remarks>
    /// Brace counting over comment-stripped source, with string, verbatim-string, interpolated and
    /// character literals skipped so an unbalanced brace inside a log template or a path cannot
    /// move a block boundary (review minor finding). Comments are already gone, because every
    /// caller passes text that came through <c>SourceScan.StripComments</c>.
    /// </remarks>
    private static bool IsInsideABlockOpenedBy(string source, string opener, int index)
    {
        var start = source.IndexOf(opener, StringComparison.Ordinal);
        while (start >= 0 && start < index)
        {
            var open = source.IndexOf('{', start + opener.Length);
            if (open < 0)
            {
                return false;
            }

            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                var c = source[i];

                if (c == '"' || c == '\'')
                {
                    i = SkipLiteral(source, i);
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (index > open && index < i)
                        {
                            return true;
                        }

                        break;
                    }
                }
            }

            start = source.IndexOf(opener, start + 1, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>Returns the index of the closing quote of the literal that starts at
    /// <paramref name="start"/>, or the last index of the source if it is unterminated.</summary>
    private static int SkipLiteral(string source, int start)
    {
        var quote = source[start];
        var verbatim = start > 0 && quote == '"' && source[start - 1] == '@';

        for (var i = start + 1; i < source.Length; i++)
        {
            var c = source[i];

            if (verbatim)
            {
                if (c != quote)
                {
                    continue;
                }

                // "" inside a verbatim string is an escaped quote, not the end of it.
                if (i + 1 < source.Length && source[i + 1] == quote)
                {
                    i++;
                    continue;
                }

                return i;
            }

            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == quote)
            {
                return i;
            }
        }

        return source.Length - 1;
    }
}
