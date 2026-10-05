using System.Security.Cryptography;
using System.Text;
using GalactiLog.Core.Io;
using Serilog;

namespace GalactiLog.App.Services;

/// <summary>
/// Spec 12.11 behaviours 1 to 3, implemented with two named kernel objects and no IPC channel: a
/// mutex proves which process is the application, and an auto-reset event carries the one bit a
/// second launch has, which is whether it wants the running window brought to the front.
/// </summary>
/// <remarks>
/// <para>
/// No named pipe, no socket, no memory-mapped file and no file on disk (ruling Q7). Spec 19.2
/// says this application has no server and no listening socket, and spec 2.1 says it creates no
/// file it was not asked to create; a kernel object satisfies both, and it leaves nothing to
/// drain at shutdown beyond one background thread that is waiting on a handle.
/// </para>
/// <para>
/// The gate fails open. Every call into the kernel-object API is wrapped, and a failure is logged
/// at warning and treated as "this process is the application": a machine policy that forbids the
/// named object, or any other refusal, must not make GalactiLog unstartable. The cost of failing
/// open is two windows; the cost of failing closed is no window at all.
/// </para>
/// <para>
/// An abandoned mutex is not a failure either. If the owning process was killed, Windows destroys
/// the object once the last handle closes, and the next <c>new Mutex(true, name, out createdNew)</c>
/// reports <c>createdNew: true</c>. A crash therefore never leaves the application unstartable.
/// </para>
/// <para>
/// Diagnostics go to the process-wide <c>Serilog.Log</c> rather than to an injected logger (review
/// fix round 1). That is what makes both halves true at once. <c>Program.Main</c> constructs this
/// gate before <c>AppHost.Build</c> configures Serilog, and Serilog's static logger is a silent
/// logger until then, so a losing launch writes no log line (behaviour 1); while the owner's
/// listener starts after <c>AppHost.Build</c>, so a fail-open claim or a listener that died reaches
/// the real sink instead of vanishing into a null logger, which would leave behaviour 3 silently
/// dead.
/// </para>
/// </remarks>
public sealed class SingleInstanceGate : ISingleInstanceGate
{
    /// <summary>
    /// The mutex base name. <c>Local\</c> rather than <c>Global\</c>: two Windows sessions are two
    /// users with two profiles and two libraries, each entitled to one instance, and
    /// <c>Global\</c> would ask for a privilege this application does not have.
    /// </summary>
    internal const string MutexBaseName = @"Local\GalactiLog.SingleInstance";

    /// <summary>The activation event base name, scoped the same way as the mutex.</summary>
    internal const string ActivationBaseName = @"Local\GalactiLog.Activate";

    /// <summary>Hex characters of the scope hash kept. Well under the kernel name length cap.
    /// </summary>
    private const int ScopeLength = 16;

    // A listener waiting on a kernel handle returns the instant the stop event is set, so this is
    // a diagnostic ceiling rather than a budget anyone waits out. It is not part of spec 10.5's
    // five second shutdown drain and takes no share of it.
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly string _mutexName;
    private readonly string _activationName;
    private readonly object _sync = new();

    private Mutex? _mutex;
    private EventWaitHandle? _activation;
    private EventWaitHandle? _stop;
    private Thread? _listener;
    private bool _disposed;

    /// <param name="scope">What makes this application's instance distinct from another GalactiLog
    /// running against a different library, normally <see cref="ScopeFromEnvironment"/>. Null or
    /// empty leaves the base names unsuffixed.</param>
    public SingleInstanceGate(string? scope = null)
    {
        var suffix = string.IsNullOrEmpty(scope) ? string.Empty : "." + scope;
        _mutexName = MutexBaseName + suffix;
        _activationName = ActivationBaseName + suffix;
    }

    /// <summary>The composed mutex name. Test seam only.</summary>
    internal string MutexName => _mutexName;

    /// <summary>The composed activation event name. Test seam only.</summary>
    internal string ActivationName => _activationName;

    // Read under the same lock that sets it, so TryAcquire and Dispose agree about the answer
    // rather than racing on a plain field read.
    private bool Disposed
    {
        get
        {
            lock (_sync)
            {
                return _disposed;
            }
        }
    }

    /// <summary>
    /// Spec 12.11 behaviour 1. Claims the application for this process, and on a losing launch
    /// signals the owner when <paramref name="requestActivation"/> asks for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The claim is <c>new Mutex(initiallyOwned: true, name, out createdNew)</c> and the answer is
    /// <c>createdNew</c>. There is no <c>WaitOne</c>: this is an ownership question and not a
    /// lock, and a wait here would park the second launch behind the first for the whole
    /// application lifetime instead of returning at once.
    /// </para>
    /// <para>
    /// The owner creates its activation event here rather than in <see cref="StartListening"/>, so
    /// a second launch arriving between the claim and the first window appearing has something to
    /// signal and is not told the application does not exist.
    /// </para>
    /// <para>
    /// A loser never retries and never becomes the owner. The owner can exit between the claim
    /// check and the open, in which case there is nothing to signal and the user repeats the
    /// launch; a retry loop here would be a startup path that can hang.
    /// </para>
    /// </remarks>
    /// <param name="requestActivation">False for a launch that carried <c>--minimized</c>, which
    /// asks for nothing and exits silently (spec 12.11 behaviour 1).</param>
    /// <returns>True when this process owns the application.</returns>
    public bool TryAcquire(bool requestActivation)
    {
        // Already the owner. A second claim would leak the first mutex handle, and re-claiming a
        // name this process already holds would report createdNew: false against itself.
        //
        // A disposed gate answers the same way and creates nothing (review fix round 1): a claim
        // taken here would own two kernel objects that nothing releases, and the fail-open answer
        // is the right one for a caller that has already thrown this gate away.
        if (_mutex is not null || Disposed)
        {
            return true;
        }

        try
        {
            var mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                if (requestActivation)
                {
                    SignalOwner();
                }

                return false;
            }

            _mutex = mutex;
            _activation = new EventWaitHandle(
                initialState: false, EventResetMode.AutoReset, _activationName, out _);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex, "The single-instance claim could not be taken; continuing as the application");
            return true;
        }
    }

    /// <summary>
    /// Spec 12.11 behaviour 3. Starts the one background thread that waits on the activation event
    /// and calls back once per signal. Ignored when listening already started, when the claim was
    /// never taken, and after <see cref="Dispose"/>.
    /// </summary>
    /// <remarks>
    /// One thread waiting on two handles, not a poll: there is no interval, no timer and no
    /// repeated read of anything. The thread is a background thread, so it can never hold the
    /// process open, and it exits on the private stop event rather than on an abort.
    /// </remarks>
    /// <param name="onActivationRequested">Raised on the listener thread. The caller marshals; a
    /// callback that throws is logged and the watch continues.</param>
    public void StartListening(Action onActivationRequested)
    {
        ArgumentNullException.ThrowIfNull(onActivationRequested);

        EventWaitHandle activation;
        EventWaitHandle stop;

        lock (_sync)
        {
            if (_disposed || _listener is not null)
            {
                return;
            }

            if (_activation is not { } claimed)
            {
                // Either the claim was never taken, or it failed open. Nothing signals this
                // process, so there is nothing to wait for.
                Log.Debug("Activation listening was requested with no claim held");
                return;
            }

            activation = claimed;

            try
            {
                stop = new EventWaitHandle(initialState: false, EventResetMode.ManualReset);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "The activation listener could not be started");
                return;
            }

            _stop = stop;
            var listener = new Thread(() => Listen(activation, stop, onActivationRequested))
            {
                IsBackground = true,
                Name = "GalactiLog activation listener",
            };
            _listener = listener;

            // Started inside the lock (review fix round 1): a StopListening or Dispose interleaved
            // between the assignment and the start would join a thread that was never started,
            // and Thread.Join on an unstarted thread throws. The thread's own body takes no lock,
            // so starting it here cannot deadlock against this one.
            listener.Start();
        }
    }

    /// <summary>
    /// Stops the watch and joins the listener thread. Idempotent, safe before
    /// <see cref="StartListening"/>, and safe after <see cref="Dispose"/>.
    /// </summary>
    /// <remarks>
    /// Called from the <c>ShutdownRequested</c> handler in <c>App.axaml.cs</c> and outside spec
    /// 10.5's drain budget: setting an event and joining a thread that is waiting on a handle
    /// returns at once, so <c>App.DrainForShutdown</c>'s signature, body and budget are unchanged.
    /// </remarks>
    public void StopListening() => StopAndJoin();

    // Returns whether the listener is known to be gone: true when none was running, and true when
    // one was and the join succeeded. False means a thread is still waiting on the two handles,
    // which is what Dispose needs to know before it disposes either of them.
    private bool StopAndJoin()
    {
        Thread? listener;
        EventWaitHandle? stop;

        lock (_sync)
        {
            listener = _listener;
            stop = _stop;
            _listener = null;
            _stop = null;
        }

        if (stop is null)
        {
            return true;
        }

        try
        {
            stop.Set();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The activation listener could not be signalled to stop");
        }

        if (listener is not null && !listener.Join(StopTimeout))
        {
            // The handle is deliberately left undisposed: disposing it under a thread that is
            // still waiting on it trades one stuck thread for an exception in another. The
            // finalizer releases it when the thread finally lets go.
            Log.Warning(
                "The activation listener did not stop within {Seconds}s", StopTimeout.TotalSeconds);
            return false;
        }

        stop.Dispose();
        return true;
    }

    /// <summary>
    /// Releases the claim and the two kernel objects. <c>Program.Main</c> calls it beside
    /// <c>guiHost.Dispose()</c>, and on a losing launch immediately before <c>return 0</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mutex handle is closed without <c>ReleaseMutex</c>. A mutex taken with
    /// <c>initiallyOwned: true</c> belongs to the thread that created it and releasing it from
    /// another thread throws, while closing the last handle destroys the object and lets the next
    /// launch claim it, which is the same path a crash takes.
    /// </para>
    /// <para>
    /// Neither handle is disposed when the listener did not stop (review fix round 1), for the
    /// reason the stop handle already applied: a listener that is still waiting on the activation
    /// event would meet a disposed handle. The gate is marked disposed either way, so the claim is
    /// never taken again through this instance, and the handles reach their finalizers instead.
    /// The process is exiting on this path in production, which releases both regardless.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        var stopped = StopAndJoin();

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (!stopped)
        {
            return;
        }

        try
        {
            _activation?.Dispose();
            _mutex?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The single-instance claim could not be released cleanly");
        }

        _activation = null;
        _mutex = null;
    }

    /// <summary>
    /// The kernel-object name suffix for this process's library, derived from
    /// <c>GALACTILOG_APPDATA</c>, or null when the variable is unset or blank.
    /// </summary>
    /// <remarks>
    /// The gate runs before <c>AppHost.Build</c> and therefore before spec 17.2's four-step app
    /// data root resolution, so it cannot ask which root resolved and reads the raw environment
    /// value instead. Two spellings of one folder producing two instances is a far smaller problem
    /// than a verification run, which <c>HANDOFF.md</c> section 3.5 requires to set this variable
    /// to a temp folder, claiming the same name as the user's real instance and activating the
    /// user's real window.
    /// </remarks>
    /// <returns>The scope suffix, or null.</returns>
    internal static string? ScopeFromEnvironment()
        => ScopeFrom(Environment.GetEnvironmentVariable(AppDataRootResolver.EnvironmentVariableName));

    /// <summary>
    /// <see cref="ScopeFromEnvironment"/> with the value supplied, so a test asserts the rule
    /// without mutating process state.
    /// </summary>
    /// <remarks>
    /// The value is trimmed and case-folded before hashing, and only the hash reaches the name: a
    /// named kernel object lives in a namespace other processes can enumerate, and a user's folder
    /// path in it is an information leak.
    /// </remarks>
    /// <param name="environmentValue">The <c>GALACTILOG_APPDATA</c> value, or null.</param>
    /// <returns>The scope suffix, or null when the value is null, empty or whitespace.</returns>
    internal static string? ScopeFrom(string? environmentValue)
    {
        if (string.IsNullOrWhiteSpace(environmentValue))
        {
            return null;
        }

        var normalized = environmentValue.Trim().ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..ScopeLength].ToLowerInvariant();
    }

    // The loser's half of behaviour 1. Opened rather than created: creating it would succeed
    // against a name whose owner has just exited and signal an event nobody waits on, which reads
    // in a log as an activation that was delivered.
    private void SignalOwner()
    {
        try
        {
            // CA1416: EventWaitHandle.OpenExisting is attributed [SupportedOSPlatform("windows")].
            // This application is Windows-only (a WinExe over Avalonia.Desktop with no other
            // target platform, spec 19.2), so the warning is a false positive rather than a real
            // portability gap, and it is suppressed on the one statement that raises it rather
            // than on the type, which would push the attribute out to Program.Main.
#pragma warning disable CA1416
            using var activation = EventWaitHandle.OpenExisting(_activationName);
#pragma warning restore CA1416
            activation.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            Log.Debug(
                "The running instance exited before its activation could be requested");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "The running instance could not be asked to activate");
        }
    }

    // The stop handle is index 0 so that a stop signalled at the same moment as an activation wins:
    // WaitAny reports the lowest signalled index, and a callback raised after StopListening
    // returned would reach a disposed application.
    private void Listen(EventWaitHandle activation, EventWaitHandle stop, Action onActivationRequested)
    {
        var handles = new WaitHandle[] { stop, activation };

        while (true)
        {
            int signalled;
            try
            {
                signalled = WaitHandle.WaitAny(handles);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "The activation listener stopped waiting");
                return;
            }

            if (signalled == 0)
            {
                return;
            }

            try
            {
                onActivationRequested();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "An activation request could not be handled");
            }
        }
    }
}
