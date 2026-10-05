using GalactiLog.App.Services;
using GalactiLog.App.Tests.TestSupport;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GalactiLog.App.Tests.Services;

/// <summary>
/// The roadmap's Phase 11 row 1 Verify line, written out: a second launch does not become a second
/// application, it asks the running one to come to the front, and the gate sits ahead of
/// <c>AppHost.Build</c> in <c>Program.Main</c> (design-spec 12.11 behaviours 1 to 3, 4.4, 15).
/// </summary>
/// <remarks>
/// <para>
/// Every case that creates a real kernel object passes a scope built from
/// <see cref="Guid.NewGuid"/>, so the names are unique to that case. No test claims or signals a
/// kernel object under the production name: that would reach the GalactiLog the user is actually
/// running on this machine and bring its window to the front during a test run. The one case that
/// names the unsuffixed names never calls <c>TryAcquire</c> or <c>StartListening</c>, so it
/// composes two strings and creates nothing.
/// </para>
/// <para>
/// Nothing here sleeps or blocks on the activation path. Each activation is awaited through a
/// <c>TaskCompletionSource</c> with a bounded <c>WaitAsync</c>, and each absence is asserted after
/// an awaited delay (<c>TRACKING.md</c> section 2 item 8).
/// </para>
/// </remarks>
public class SingleInstanceGateTests
{
    // Long enough that a loaded build machine does not fail a real signal, short enough that a
    // broken listener fails the case rather than the run.
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    // What an absence is asserted after. A signal travels through one kernel event and one thread
    // wake, so a delivery that has not happened in this time did not happen.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

    private static string UniqueScope() => "test." + Guid.NewGuid().ToString("N");

    [Fact]
    public void TryAcquire_OnTheFirstGate_ReturnsTrue()
    {
        using var first = new SingleInstanceGate(UniqueScope());

        Assert.True(first.TryAcquire(requestActivation: true));
    }

    [Fact]
    public void TryAcquire_OnASecondGateWithTheSameScope_ReturnsFalse()
    {
        var scope = UniqueScope();
        using var first = new SingleInstanceGate(scope);
        using var second = new SingleInstanceGate(scope);

        Assert.True(first.TryAcquire(requestActivation: true));
        Assert.False(second.TryAcquire(requestActivation: true));
    }

    /// <summary>
    /// The <c>GALACTILOG_APPDATA</c> isolation rule. A run against a different library is a
    /// different application and claims its own name, which is what keeps a verification run from
    /// colliding with the user's real instance.
    /// </summary>
    [Fact]
    public void TryAcquire_OnASecondGateWithADifferentScope_ReturnsTrue()
    {
        using var first = new SingleInstanceGate(UniqueScope());
        using var second = new SingleInstanceGate(UniqueScope());

        Assert.True(first.TryAcquire(requestActivation: true));
        Assert.True(second.TryAcquire(requestActivation: true));
    }

    /// <summary>
    /// Releasing the claim lets the next launch take it.
    /// </summary>
    /// <remarks>
    /// This is an orderly <c>Dispose</c> and not a killed owner (review fix round 1: it used to
    /// claim it was the abandoned-owner case and it is not). A crash cannot be reproduced
    /// in-process, because it closes the handle from outside the process. What the two paths share
    /// is the mechanism this case does exercise: nothing ever waits on the mutex, so there is no
    /// abandonment to observe, and the claim ends when the last handle closes, whoever closes it.
    /// The killed owner itself is observed on the manual bar, where a real process is ended.
    /// </remarks>
    [Fact]
    public void Dispose_ReleasesTheClaim_SoAThirdGateCanAcquire()
    {
        var scope = UniqueScope();
        var first = new SingleInstanceGate(scope);
        Assert.True(first.TryAcquire(requestActivation: true));

        using (var second = new SingleInstanceGate(scope))
        {
            Assert.False(second.TryAcquire(requestActivation: true));
        }

        first.Dispose();

        using var third = new SingleInstanceGate(scope);
        Assert.True(third.TryAcquire(requestActivation: true));
    }

    [Fact]
    public void TryAcquire_CalledTwiceOnTheOwner_StaysTheOwner()
    {
        using var owner = new SingleInstanceGate(UniqueScope());

        Assert.True(owner.TryAcquire(requestActivation: true));
        Assert.True(owner.TryAcquire(requestActivation: true));
    }

    /// <summary>
    /// A claim on a disposed gate would own two kernel objects that nothing releases. The gate
    /// gives the fail-open answer and creates nothing, which the second claim below proves: a
    /// fresh gate on the same scope still wins (review fix round 1).
    /// </summary>
    [Fact]
    public void TryAcquire_AfterDispose_CreatesNothing()
    {
        var scope = UniqueScope();
        var disposed = new SingleInstanceGate(scope);
        disposed.Dispose();

        Assert.True(disposed.TryAcquire(requestActivation: true));

        using var fresh = new SingleInstanceGate(scope);
        Assert.True(fresh.TryAcquire(requestActivation: true));
    }

    /// <summary>
    /// Spec 12.11 behaviour 1's "writes a log line" half, on the gate's side of it. The gate logs
    /// through the process-wide <c>Serilog.Log</c>, which is a silent logger until
    /// <c>AppHost.Build</c> configures it and which a losing launch never reaches, so the
    /// assertion below is the stronger one: a losing launch writes nothing even with a live sink
    /// installed (review fix round 1).
    /// </summary>
    [Fact]
    public void ALosingLaunch_WritesNoLogLine()
    {
        var scope = UniqueScope();
        using var owner = new SingleInstanceGate(scope);
        Assert.True(owner.TryAcquire(requestActivation: true));

        // Serilog's Log.Logger is process-global. Cross-collection parallelization is off for this
        // assembly (AssemblyInfo.cs), so nothing else is running while this swap is in place, and
        // the previous logger is restored before the case returns.
        var sink = new CapturingSink();
        var previous = Log.Logger;
        var capturing = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        Log.Logger = capturing;

        try
        {
            using var second = new SingleInstanceGate(scope);
            Assert.False(second.TryAcquire(requestActivation: true));
        }
        finally
        {
            Log.Logger = previous;
            capturing.Dispose();
        }

        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task TryAcquire_WithRequestActivation_SignalsTheOwner()
    {
        var scope = UniqueScope();
        using var owner = new SingleInstanceGate(scope);
        Assert.True(owner.TryAcquire(requestActivation: true));

        var activated = new TaskCompletionSource();
        owner.StartListening(() => activated.TrySetResult());

        using var second = new SingleInstanceGate(scope);
        Assert.False(second.TryAcquire(requestActivation: true));

        await activated.Task.WaitAsync(SignalTimeout);
    }

    /// <summary>
    /// The <c>--minimized</c> half of behaviour 1: the Startup shortcut firing while the user is
    /// already running the application asks for nothing and exits silently.
    /// </summary>
    [Fact]
    public async Task TryAcquire_WithoutRequestActivation_DoesNotSignalTheOwner()
    {
        var scope = UniqueScope();
        using var owner = new SingleInstanceGate(scope);
        Assert.True(owner.TryAcquire(requestActivation: true));

        var activated = new TaskCompletionSource();
        owner.StartListening(() => activated.TrySetResult());

        using var second = new SingleInstanceGate(scope);
        Assert.False(second.TryAcquire(requestActivation: false));

        await Task.Delay(SettleDelay);
        Assert.False(activated.Task.IsCompleted);
    }

    /// <summary>The auto-reset rule: each signal is consumed exactly once, so two launches produce
    /// two activations rather than one.</summary>
    [Fact]
    public async Task TwoLosingLaunches_ProduceTwoActivations()
    {
        var scope = UniqueScope();
        using var owner = new SingleInstanceGate(scope);
        Assert.True(owner.TryAcquire(requestActivation: true));

        var both = new TaskCompletionSource();
        var count = 0;
        owner.StartListening(() =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                both.TrySetResult();
            }
        });

        using (var second = new SingleInstanceGate(scope))
        {
            Assert.False(second.TryAcquire(requestActivation: true));
        }

        using (var third = new SingleInstanceGate(scope))
        {
            Assert.False(third.TryAcquire(requestActivation: true));
        }

        await both.Task.WaitAsync(SignalTimeout);
    }

    [Fact]
    public async Task StartListening_CalledTwice_ListensOnce()
    {
        var scope = UniqueScope();
        using var owner = new SingleInstanceGate(scope);
        Assert.True(owner.TryAcquire(requestActivation: true));

        var first = new TaskCompletionSource();
        var count = 0;
        void OnActivated()
        {
            Interlocked.Increment(ref count);
            first.TrySetResult();
        }

        owner.StartListening(OnActivated);
        owner.StartListening(OnActivated);

        using var second = new SingleInstanceGate(scope);
        Assert.False(second.TryAcquire(requestActivation: true));

        await first.Task.WaitAsync(SignalTimeout);
        await Task.Delay(SettleDelay);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void StopListening_BeforeStartListening_IsSafe()
    {
        using var owner = new SingleInstanceGate(UniqueScope());
        Assert.True(owner.TryAcquire(requestActivation: true));

        owner.StopListening();
    }

    [Fact]
    public void StopListening_CalledTwice_IsSafe()
    {
        using var owner = new SingleInstanceGate(UniqueScope());
        Assert.True(owner.TryAcquire(requestActivation: true));
        owner.StartListening(() => { });

        owner.StopListening();
        owner.StopListening();
    }

    [Fact]
    public async Task StopListening_StopsDeliveringActivations()
    {
        var scope = UniqueScope();
        using var owner = new SingleInstanceGate(scope);
        Assert.True(owner.TryAcquire(requestActivation: true));

        var activated = new TaskCompletionSource();
        owner.StartListening(() => activated.TrySetResult());
        owner.StopListening();

        using var second = new SingleInstanceGate(scope);
        Assert.False(second.TryAcquire(requestActivation: true));

        await Task.Delay(SettleDelay);
        Assert.False(activated.Task.IsCompleted);
    }

    [Fact]
    public async Task Dispose_StopsTheListener()
    {
        var scope = UniqueScope();
        var owner = new SingleInstanceGate(scope);
        Assert.True(owner.TryAcquire(requestActivation: true));

        var activated = new TaskCompletionSource();
        owner.StartListening(() => activated.TrySetResult());
        owner.Dispose();

        // The claim is gone with the gate, so this one is the owner rather than a loser; it
        // signals the name the disposed gate used to listen on, which nothing waits on now.
        using var next = new SingleInstanceGate(scope);
        Assert.True(next.TryAcquire(requestActivation: true));

        using var loser = new SingleInstanceGate(scope);
        Assert.False(loser.TryAcquire(requestActivation: true));

        await Task.Delay(SettleDelay);
        Assert.False(activated.Task.IsCompleted);
    }

    [Fact]
    public void Names_AreSessionLocalAndCarryTheScope()
    {
        var scope = "abcdef0123456789";
        using var gate = new SingleInstanceGate(scope);

        Assert.StartsWith(@"Local\", gate.MutexName, StringComparison.Ordinal);
        Assert.StartsWith(@"Local\", gate.ActivationName, StringComparison.Ordinal);
        Assert.EndsWith("." + scope, gate.MutexName, StringComparison.Ordinal);
        Assert.EndsWith("." + scope, gate.ActivationName, StringComparison.Ordinal);
        Assert.NotEqual(gate.MutexName, gate.ActivationName);
    }

    /// <summary>
    /// The unsuffixed names, which is what a real launch with no <c>GALACTILOG_APPDATA</c> uses.
    /// The constructor composes two strings and touches no kernel object, and this case calls
    /// neither <c>TryAcquire</c> nor <c>StartListening</c>, so nothing here reaches the user's
    /// running application.
    /// </summary>
    [Fact]
    public void Names_WithNoScope_AreTheBaseNames()
    {
        using var gate = new SingleInstanceGate();

        Assert.Equal(SingleInstanceGate.MutexBaseName, gate.MutexName);
        Assert.Equal(SingleInstanceGate.ActivationBaseName, gate.ActivationName);
    }

    [Fact]
    public void ScopeFromEnvironment_WithNoVariable_IsNull()
    {
        Assert.Null(SingleInstanceGate.ScopeFrom(null));
        Assert.Null(SingleInstanceGate.ScopeFrom(""));
        Assert.Null(SingleInstanceGate.ScopeFrom("   "));
    }

    [Fact]
    public void ScopeFromEnvironment_WithAVariable_IsStableAcrossCalls()
    {
        var first = SingleInstanceGate.ScopeFrom(@"C:\temp\galactilog-verify");
        var second = SingleInstanceGate.ScopeFrom(@"C:\temp\galactilog-verify");

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ScopeFromEnvironment_ForTwoDifferentValues_Differs()
    {
        var first = SingleInstanceGate.ScopeFrom(@"C:\temp\one");
        var second = SingleInstanceGate.ScopeFrom(@"C:\temp\two");

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// A named kernel object lives in a namespace other processes can enumerate, so the user's
    /// folder path must not appear in it. Only the hash reaches the name.
    /// </summary>
    [Fact]
    public void ScopeFromEnvironment_DoesNotContainTheRawPath()
    {
        const string value = @"C:\Users\<user>\AppData\Local\GalactiLogVerify";
        var scope = SingleInstanceGate.ScopeFrom(value);

        Assert.NotNull(scope);
        Assert.DoesNotContain("GalactiLogVerify", scope, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", scope, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\", scope, StringComparison.Ordinal);
        Assert.Equal(16, scope.Length);

        using var gate = new SingleInstanceGate(scope);
        Assert.DoesNotContain("GalactiLogVerify", gate.MutexName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GalactiLogVerify", gate.ActivationName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The roadmap's "asserts the second launch returns without building a host", as an ordering
    /// assertion over the text of <c>Program.cs</c>. It is a source scan because the behaviour it
    /// proves is a process that did not start: no in-process test can observe a host that was
    /// never built, and starting a second real GUI from a test is what <c>HANDOFF.md</c> 5.2
    /// note 9 forbids. Same shape as <c>UpdateServiceTests.VelopackTypes_AreNamedInExactlyOneFile</c>.
    /// </summary>
    [Fact]
    public void Program_GatesBeforeBuildingTheHost()
    {
        var program = ProgramSource();

        var gate = program.IndexOf("TryAcquire", StringComparison.Ordinal);
        var build = program.IndexOf("AppHost.Build(cliMode: false", StringComparison.Ordinal);

        Assert.True(gate >= 0, "Program.cs does not call the single-instance gate.");
        Assert.True(build >= 0, "Program.cs no longer builds the GUI host the expected way.");
        Assert.True(gate < build, "The single-instance gate must be claimed before AppHost.Build.");
    }

    /// <summary>
    /// Spec 15's guarantee that a CLI verb beside a running GUI still works: the gate is taken on
    /// the GUI path only, after the CLI branch has already returned its exit code.
    /// </summary>
    [Fact]
    public void Program_GatesOnlyTheGuiPath()
    {
        var program = ProgramSource();

        var cli = program.IndexOf("CliDispatcher.TryRun", StringComparison.Ordinal);
        var gate = program.IndexOf("TryAcquire", StringComparison.Ordinal);

        Assert.True(cli >= 0, "Program.cs no longer dispatches the CLI the expected way.");
        Assert.True(gate >= 0, "Program.cs does not call the single-instance gate.");
        Assert.True(cli < gate, "The CLI branch must be reached before the single-instance gate.");
    }

    /// <summary>
    /// The wiring that carries the gate from <c>Program.Main</c> to the window, asserted over
    /// source text because no headless test builds the desktop lifetime this task's three lines
    /// live in (review fix round 1). Without it an edit that deletes any of the three breaks spec
    /// 12.11 behaviour 3 with all of the gate's own cases still green.
    /// </summary>
    [Fact]
    public void Program_HandsTheGateToTheApplication()
    {
        Assert.Contains("App.Gate = gate", ProgramSource(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The listener starts after the window exists, which is what behaviour 3's activation needs
    /// to reach, and it starts on the GUI path only, inside the lifetime block.
    /// </summary>
    [Fact]
    public void App_StartsListeningOnceTheWindowExists()
    {
        var app = AppSource();

        var window = app.IndexOf("desktop.MainWindow =", StringComparison.Ordinal);
        var start = app.IndexOf("Gate?.StartListening(", StringComparison.Ordinal);

        Assert.True(window >= 0, "App.axaml.cs no longer assigns the main window the expected way.");
        Assert.True(start >= 0, "App.axaml.cs does not start the single-instance listener.");
        Assert.True(window < start, "The activation listener must start after the window exists.");
    }

    /// <summary>
    /// The listener stops in the <c>ShutdownRequested</c> handler and outside the drain: after the
    /// handler opens and before <c>DrainForShutdown</c> is called, so it takes no share of spec
    /// 10.5's five second budget.
    /// </summary>
    [Fact]
    public void App_StopsListeningInTheShutdownHandler_OutsideTheDrain()
    {
        var app = AppSource();

        var handler = app.IndexOf("desktop.ShutdownRequested", StringComparison.Ordinal);
        Assert.True(handler >= 0, "App.axaml.cs no longer subscribes to ShutdownRequested.");

        var stop = app.IndexOf("Gate?.StopListening(", handler, StringComparison.Ordinal);
        var drain = app.IndexOf("DrainForShutdown(", handler, StringComparison.Ordinal);

        Assert.True(stop >= 0, "The single-instance listener is not stopped in the shutdown handler.");
        Assert.True(drain >= 0, "The shutdown handler no longer calls the drain.");
        Assert.True(stop < drain, "The listener must be stopped outside the drain, before it runs.");
    }

    // Comments are stripped first, so a rule named in a comment is not a match. Both files are read
    // through SourceScan, the project's one architecture text-scan helper (TRACKING item 28): no
    // second repository-root walk is defined here.
    private static string ProgramSource() => AppSourceFile("Program.cs");

    private static string AppSource() => AppSourceFile("App.axaml.cs");

    private static string AppSourceFile(string fileName)
        => SourceScan.StripComments(
            File.ReadAllText(Path.Combine(SourceScan.SrcRoot(), "GalactiLog.App", fileName)));

    /// <summary>Collects everything written to Serilog while it is installed.</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
            {
                _events.Add(logEvent);
            }
        }
    }
}
