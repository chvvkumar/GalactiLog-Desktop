using System.Diagnostics;

namespace GalactiLog.App.Services.Telemetry;

/// <summary>Process-wide values the crash marker and the telemetry report read without DI.</summary>
public static class TelemetryContext
{
    /// <summary>The page key of the current navigation entry, set by
    /// <c>MainWindowViewModel.Navigate</c>. Never a detail key.</summary>
    public static volatile string CurrentPage = "";

    /// <summary>Started when the type is first touched, which <see cref="CrashHook.Install"/>
    /// does early in <c>Program.Main</c>.</summary>
    public static readonly Stopwatch Uptime = Stopwatch.StartNew();
}
