using Avalonia;
using Avalonia.Headless;
using Avalonia.Logging;
using GalactiLog.App;
using GalactiLog.App.Tests;
using GalactiLog.App.Tests.TestSupport;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace GalactiLog.App.Tests;

public static class TestAppBuilder
{
    /// <summary>
    /// The headless app every case in this assembly runs on, with the Phase 15A diagnostic sink
    /// attached when <c>GALACTILOG_TEST_DIAG_DIR</c> names a directory to write into.
    /// </summary>
    /// <remarks>
    /// With the variable unset this is the plain builder it has always been: no log sink, no
    /// exception handler, no file. With it set, Avalonia's own logging from Warning up is routed to
    /// <see cref="HeadlessDiagnostics"/> and the exception handlers of
    /// <c>work/phase15a/flake-investigation.md</c> section 9.6 are attached after platform setup.
    /// Neither branch touches the headless options, the theme or the fonts.
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .AfterSetup(_ => ChartThrottles.Watch());

        return HeadlessDiagnostics.IsEnabled
            ? builder
                .LogToDelegate(HeadlessDiagnostics.Log, LogEventLevel.Warning)
                .AfterSetup(_ => HeadlessDiagnostics.Install())
            : builder;
    }
}
