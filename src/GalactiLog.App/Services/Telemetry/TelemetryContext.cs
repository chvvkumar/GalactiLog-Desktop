using System.Diagnostics;

namespace GalactiLog.App.Services.Telemetry;

/// <summary>Process-wide values the crash marker and the telemetry report read without DI.</summary>
public static class TelemetryContext
{
    /// <summary>The page key of the current navigation entry, set by
    /// <c>MainWindowViewModel.Navigate</c>. Never a detail key.</summary>
    public static volatile string CurrentPage = "";

    /// <summary>Starts when the type is first touched. <see cref="CrashHook.Install"/> touches it
    /// in <c>Program.Main</c> right after the host is built, so it measures from there, not from
    /// process start.</summary>
    public static readonly Stopwatch Uptime = Stopwatch.StartNew();
}
