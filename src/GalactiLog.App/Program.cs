using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using GalactiLog.App.Services;
using GalactiLog.Cli;
using GalactiLog.Core.Io;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Velopack;

namespace GalactiLog.App;

internal static class Program
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint FileTypeUnknown = 0;

    /// <summary>MB_OK.</summary>
    private const uint MbOk = 0x00000000;

    /// <summary>MB_ICONERROR.</summary>
    private const uint MbIconError = 0x00000010;

    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();

        // Spec 12.11 behaviour 9 and spec 4.4 step 2 (Phase 11 Task 4). Parsed here, after
        // Velopack's hooks (spec 17.1 keeps that line first) and ahead of the CLI branch below,
        // because --minimized is a GUI launch switch and not a verb: spec 12.11 behaviour 8's
        // Startup shortcut launches "GalactiLog.exe --minimized", and before this parse existed
        // that reached CliDispatcher, printed the usage text on stderr and exited 2, which made
        // the shortcut useless.
        //
        // The switch is consumed only when it is the whole command line (ruling Q8 (a)). Two
        // separate rules make that true, and both are needed:
        //
        //   guiArgs decides the BRANCH. guiArgs.Length > 0 and args.Length > 0 differ in exactly
        //   one input, an argument list that is nothing but the switch, possibly repeated.
        //
        //   args is what the DISPATCHER reads. The stripped array never reaches CliDispatcher, so
        //   "--minimized scan C:\x" still arrives as three tokens whose first is not a verb and
        //   still exits 2 with the usage text, "scan C:\x --minimized" keeps every positional
        //   argument RunScan was given before, and "--minimized --help" is still an unknown first
        //   token rather than a --help.
        //
        // Together: every input other than the one above takes the branch it took before and
        // reaches CliDispatcher byte for byte (spec 4.4, spec 15). That sentence is what a
        // reviewer needs in order to be sure nothing in GalactiLog.Cli.Tests can regress, and
        // GalactiLog.Cli.Tests cannot assert it for itself, because the rule lives here.
        var startup = StartupArguments.Parse(args);
        var guiArgs = startup.Remaining;

        // The third static Main hands the Application, beside App.Services and App.Gate, and set
        // before both. AppHost.Build composes it with general.start_minimized into the one
        // StartupState the window decision and spec 12.8's field both read. The whole record
        // rather than a bare bool, so a second GUI switch later adds a property rather than a
        // fourth static.
        App.StartupArguments = startup;

        if (guiArgs.Length > 0)
        {
            // Only attach the parent console when at least one of stdout/stderr has no
            // valid target of its own (FILE_TYPE_UNKNOWN: the App is a WinExe subsystem
            // binary launched with no console, per design-spec 4.4/15). AttachConsole rebinds
            // both std handles to the attached console, which would clobber a stream the
            // caller had already redirected to a pipe or a file (e.g. `... | cat` on stdout
            // while stderr stays a real console). So the already-valid handle is saved before
            // attaching and restored with SetStdHandle afterward, letting a piped stdout and
            // a console stderr coexist.
            var stdOutHandle = GetStdHandle(StdOutputHandle);
            var stdErrHandle = GetStdHandle(StdErrorHandle);
            var stdOutType = GetFileType(stdOutHandle);
            var stdErrType = GetFileType(stdErrHandle);

            if (stdOutType == FileTypeUnknown || stdErrType == FileTypeUnknown)
            {
                AttachConsole(AttachParentProcess);

                if (stdOutType != FileTypeUnknown)
                {
                    SetStdHandle(StdOutputHandle, stdOutHandle);
                }

                if (stdErrType != FileTypeUnknown)
                {
                    SetStdHandle(StdErrorHandle, stdErrHandle);
                }
            }

            IHost? host = null;
            CliDispatcher.TryRun(
                // args, never guiArgs: the branch above is the only thing the parse decides.
                // Handing the dispatcher the stripped array would turn "--minimized scan C:\x"
                // into a real scan that opens the database and writes rows, and would take a
                // positional argument away from "scan C:\x --minimized" (ruling Q8 (a)).
                args,
                () =>
                {
                    host = AppHost.Build(cliMode: true);
                    return host.Services;
                },
                out var exitCode);
            host?.Dispose();
            Log.CloseAndFlush();
            return exitCode;
        }

        // Spec 12.11 behaviour 2 (Phase 11 Task 1). Deliberately here: before AppHost.Build, so a
        // second launch opens no database, runs no migration, writes no log line and creates no
        // second catalogue; and after the CLI block above, so a CLI verb beside a running GUI is
        // untouched (spec 15). The gate is owned by Main rather than by the container for the same
        // reason: it has to exist before a container does.
        var gate = new SingleInstanceGate(SingleInstanceGate.ScopeFromEnvironment());

        // Spec 12.11 behaviour 1's second sentence (Phase 11 Task 4): a launch whose command line
        // carries the minimized switch asks for nothing and exits 0 silently. It is the Startup
        // shortcut firing while the user already has the application open, and a boot must not
        // yank their window to the front.
        if (!gate.TryAcquire(requestActivation: !startup.Minimized))
        {
            // Exit 0 (spec 15's table): the launch did what it was asked, which was to bring the
            // application up. Nothing else runs. No Log.CloseAndFlush, because Serilog was never
            // configured on this path and calling it would be the first evidence on disk that this
            // process existed.
            gate.Dispose();
            return 0;
        }

        App.Gate = gate;

        // Build the host before the window shows (design-spec 4.3, 17.2): migration,
        // Serilog, and the services the GUI needs must be ready before any window
        // materializes. A build failure is fatal; there is nothing safe to show.
        IHost guiHost;
        try
        {
            guiHost = AppHost.Build(cliMode: false);
        }
        catch (AppDataRootUnavailableException ex)
        {
            // Not a fallback: falling back to the default would create a second, empty catalogue
            // beside the user's real one (design-spec 17.2). The process exits and tells the user
            // where to look.
            //
            // The claim is released before the message box goes up, so a launch the user makes
            // while reading it is a real start rather than a silent exit 0 against a process that
            // is about to die.
            gate.Dispose();
            ReportDataRootUnavailable(ex);
            Log.CloseAndFlush();
            return 6;
        }
        catch (Exception ex)
        {
            gate.Dispose();
            Console.Error.WriteLine(ex.Message);
            Log.CloseAndFlush();
            return 4;
        }

        App.Services = guiHost.Services;

        // Spec 11.3's first eviction trigger, "on application start" (ruling Q9). Here rather
        // than in AppHost.Build or in OnFrameworkInitializationCompleted: it enumerates a
        // directory that can hold thousands of files and deletes some of them, so running it on
        // the dispatcher is a visible startup stall and running it synchronously on this thread
        // delays the first window for no benefit. A sweep still running when the first preview is
        // requested is harmless, because the two serialize on the cache's own lock. Fire and
        // forget: a failed sweep must never keep the window from showing.
        var thumbnailCache = guiHost.Services.GetRequiredService<ThumbnailCache>();
        _ = Task.Run(() =>
        {
            try
            {
                thumbnailCache.EvictPreviews();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Start-up preview cache sweep failed");
            }
        });

        // The stripped array, not args: a consumed GUI switch is removed before Avalonia's own
        // argument handling sees it, so nothing in the framework has to know about a token this
        // application invented (spec 4.4 step 2).
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(guiArgs);
        gate.Dispose();
        guiHost.Dispose();
        Log.CloseAndFlush();
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// Reports an unreachable data location on the GUI path: stderr, plus a Win32 message box,
    /// because Avalonia is not started at this point and starting it to report that the data root
    /// is unreachable would add a second failure surface to a failure path.
    /// </summary>
    private static void ReportDataRootUnavailable(AppDataRootUnavailableException ex)
    {
        Console.Error.WriteLine(ex.UserMessage);
        MessageBoxW(nint.Zero, ex.UserMessage, "GalactiLog", MbOk | MbIconError);
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int nStdHandle, nint hHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(nint hFile);
}
