using System.Runtime.ExceptionServices;
using Avalonia.Threading;

namespace GalactiLog.App.Tests.TestSupport;

/// <summary>
/// The opt-in diagnostic sink this assembly's headless app writes to, so an exception that escapes
/// an Avalonia layout or render pass leaves a record instead of vanishing.
/// </summary>
/// <remarks>
/// <para>
/// Phase 15A, <c>work/phase15a/flake-investigation.md</c> section 9. In Avalonia 11.3.21
/// <c>LayoutManager.ExecuteLayoutPass</c> resets <c>_running</c> in a <c>finally</c> but clears
/// <c>_queued</c> only on the normal path, so one exception escaping a layout pass latches that
/// window's layout queue for the life of the window, and
/// <c>MediaContext.FireInvokeOnRenderCallbacks</c> drops every callback queued behind the one that
/// threw. The investigation could not name the thrower because <c>TestAppBuilder</c> never wired a
/// log sink and nothing recorded exceptions reaching the dispatcher.
/// </para>
/// <para>
/// Switched on by the environment variable <c>GALACTILOG_TEST_DIAG_DIR</c>, whose value is the
/// directory the records are written into. With the variable unset nothing is installed, no handler
/// runs, no file is created, and a developer run is unaffected. Writes are serialized, capped, and
/// never throw into a test: a diagnostic that can fail a case is worse than no diagnostic.
/// </para>
/// </remarks>
internal static class HeadlessDiagnostics
{
    /// <summary>The frame markers that mark a stack as belonging to a layout or render pass.</summary>
    private static readonly string[] PassFrames =
    [
        "LayoutManager", "MediaContext", "MeasureOverride", "ArrangeOverride",
        "MeasureCore", "ArrangeCore", "Render(",
    ];

    /// <summary>
    /// The line budget for the whole process. First-chance exceptions fire on every throw in the
    /// suite, so an unbounded sink would become the run's bottleneck and its largest artifact.
    /// </summary>
    private const int MaxLines = 20000;

    private static readonly Lock Gate = new();

    [ThreadStatic]
    private static bool t_writing;

    private static StreamWriter? s_writer;
    private static int s_lines;
    private static bool s_installed;

    /// <summary>The directory named by <c>GALACTILOG_TEST_DIAG_DIR</c>, or null when it is unset.</summary>
    public static string? Directory
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("GALACTILOG_TEST_DIAG_DIR");
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    /// <summary>Whether the caller asked for diagnostics on this run.</summary>
    public static bool IsEnabled => Directory is not null;

    /// <summary>
    /// Attaches the three exception handlers, once per process. Called from
    /// <c>TestAppBuilder.BuildAvaloniaApp</c>'s <c>AfterSetup</c> hook so the dispatcher exists;
    /// it changes no platform option, no theme and no font, and nothing a case can observe.
    /// </summary>
    public static void Install()
    {
        lock (Gate)
        {
            if (s_installed || !IsEnabled)
            {
                return;
            }

            s_installed = true;
        }

        // Handled is deliberately never set: the exception carries on exactly as it did before,
        // and Avalonia's own filter returns false, so the stack is not even unwound.
        Dispatcher.UIThread.UnhandledException += (_, e) => Record("dispatcher", e.Exception);
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        TaskScheduler.UnobservedTaskException += (_, e) => Record("unobserved-task", e.Exception);
        Write($"{Stamp()} | harness | handlers installed");
    }

    /// <summary>
    /// The delegate Avalonia's own logging is routed to by
    /// <c>LogToDelegate(..., LogEventLevel.Warning)</c>.
    /// </summary>
    public static void Log(string message) => Write($"{Stamp()} | avalonia | {message}");

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (t_writing || s_lines >= MaxLines)
        {
            return;
        }

        // The stack of the throw site, not a walk of the current one: a first chance handler runs
        // on every throw in the process, including the many a test expects, so the filter has to be
        // cheap. A throw from below a pass still names the pass once the pass frame is on the
        // exception's own stack.
        var stack = e.Exception.StackTrace;
        if (stack is null)
        {
            return;
        }

        foreach (var frame in PassFrames)
        {
            if (stack.Contains(frame, StringComparison.Ordinal))
            {
                Record("first-chance", e.Exception);
                return;
            }
        }
    }

    private static void Record(string kind, Exception exception)
    {
        var onUi = Dispatcher.UIThread.CheckAccess();

        // ToString rather than StackTrace: an unobserved task hands over an AggregateException
        // whose own stack is null and whose inner exception carries everything worth reading.
        Write(
            $"{Stamp()} | RECORD {kind} | thread {Environment.CurrentManagedThreadId} | ui {onUi} | " +
            $"{exception.GetType().FullName} | {exception.Message}{Environment.NewLine}{exception}");
    }

    private static string Stamp() => DateTime.UtcNow.ToString("O");

    private static void Write(string line)
    {
        if (!IsEnabled || t_writing)
        {
            return;
        }

        t_writing = true;
        try
        {
            lock (Gate)
            {
                if (s_lines >= MaxLines)
                {
                    return;
                }

                s_lines++;
                s_writer ??= Open();
                if (s_lines == MaxLines)
                {
                    line = $"{line}{Environment.NewLine}{Stamp()} | harness | line budget reached, sink closed";
                }

                s_writer?.WriteLine(line);
            }
        }
        catch (Exception)
        {
            // A sink that throws would fail the case it is trying to explain. Swallow and carry on.
        }
        finally
        {
            t_writing = false;
        }
    }

    private static StreamWriter? Open()
    {
        var directory = Directory;
        if (directory is null)
        {
            return null;
        }

        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            $"avalonia-diag-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        return new StreamWriter(path, append: true) { AutoFlush = true };
    }
}
