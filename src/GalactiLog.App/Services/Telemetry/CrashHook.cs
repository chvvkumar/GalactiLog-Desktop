using Avalonia.Threading;
using Serilog;

namespace GalactiLog.App.Services.Telemetry;

/// <summary>
/// Process-wide unhandled exception handlers that write the crash marker. Installed once from
/// <c>Program.Main</c>. A handler never throws.
/// </summary>
public static class CrashHook
{
    // Set by the first fatal write. The dispatcher event and the AppDomain event can both fire for
    // one exception; the second write is skipped so the marker keeps the first, closer source.
    private static int _fatalWritten;

    private static Action<Exception, string>? _fatal;

    public static void Install(string dataRoot, string version, string sha)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Fatal(ex, "appdomain");
            }
        };

        _fatal = Fatal;

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                // The process keeps running. A fatal marker already on disk wins.
                e.SetObserved();
                if (Volatile.Read(ref _fatalWritten) == 0)
                {
                    CrashMarker.Write(e.Exception, "unobserved", dataRoot, version, sha,
                        TelemetryContext.CurrentPage, UptimeSeconds());
                }

                Log.Error(e.Exception, "Unhandled exception ({Source})", "unobserved");
            }
            catch
            {
                // A crash handler never throws.
            }
        };

        void Fatal(Exception ex, string source)
        {
            try
            {
                if (Interlocked.Exchange(ref _fatalWritten, 1) == 0)
                {
                    CrashMarker.Write(ex, source, dataRoot, version, sha,
                        TelemetryContext.CurrentPage, UptimeSeconds());
                }

                Log.Fatal(ex, "Unhandled exception ({Source})", source);
            }
            catch
            {
                // A crash handler never throws.
            }
        }
    }

    /// <summary>
    /// Subscribes Avalonia's UI thread event. Separate from <see cref="Install"/> because reading
    /// <see cref="Dispatcher.UIThread"/> before the platform is set up would create the dispatcher
    /// without the Win32 implementation; Program calls this from AppBuilder.AfterSetup. Avalonia
    /// raises the event before the exception unwinds the dispatcher. Handled stays false: the
    /// process still dies, and the AppDomain handler that follows finds the flag set.
    /// </summary>
    public static void InstallDispatcher()
    {
        if (_fatal is { } fatal)
        {
            Dispatcher.UIThread.UnhandledException += (_, e) => fatal(e.Exception, "dispatcher");
        }
    }

    private static long UptimeSeconds() => (long)TelemetryContext.Uptime.Elapsed.TotalSeconds;
}
