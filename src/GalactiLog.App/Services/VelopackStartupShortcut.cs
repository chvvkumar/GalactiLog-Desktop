using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Velopack.Locators;
using Velopack.Windows;

// Velopack.Windows.Shortcuts and Velopack.Windows.ShellLink carry two compiler warnings this
// file, and only this file, accepts:
//
// CS0618: Shortcuts is marked obsolete because Velopack now creates Desktop and StartMenuRoot
// shortcuts automatically during install. It does not create a Startup shortcut, so this is
// still the only API that can, and there is no non-obsolete replacement for that one location.
//
// CA1416: Shortcuts and ShellLink are attributed [SupportedOSPlatform("windows")]. This whole
// application is Windows-only (a WinExe over Avalonia.Desktop, with no other target platform),
// so the warning is a false positive here rather than a real portability gap, and it is
// suppressed only in the one file confined to naming these types (spec 2.1.1, 2.1.3).
//
// File-wide and unrestored on purpose: FileSafetyTest's StartupShortcutPatterns group already
// makes this the one file in src/** that may say Shortcuts, ShortcutLocation or ShellLink, so a
// narrower disable/restore pair around each call site would suppress the same warning at the
// same four lines with no narrower a blast radius, for four extra pragma pairs to keep in sync.
#pragma warning disable CS0618
#pragma warning disable CA1416

namespace GalactiLog.App.Services;

/// <summary>
/// Spec 12.11 behaviour 8's one implementation of <see cref="IStartupShortcut"/>: the Startup
/// shortcut, created and removed by Velopack's own COM-based shortcut API and by nothing else.
/// </summary>
/// <remarks>
/// <para>
/// This shortcut is not a user file: this application is its author, and the only mutations it
/// performs on it are its own creation and its own removal (spec 2.1.1, 19.2).
/// </para>
/// <para>
/// <c>Velopack.Windows.Shortcuts</c> and <c>Velopack.Windows.ShellLink</c> are what the Velopack
/// documentation itself calls "legacy" classes whose stability is not guaranteed. That is the
/// reason this seam exists at all, and the reason the whole surface is confined to this one file
/// rather than spread across the application by convention (design-lessons rule 2).
/// <c>GalactiLog.Core.Tests.Architecture.FileSafetyTest</c> polices the confinement structurally:
/// no other file in <c>src/**</c> may name <c>Shortcuts</c>, <c>ShortcutLocation</c> or
/// <c>ShellLink</c>. <c>VelopackLocator</c> is deliberately outside that group (coordinator
/// ruling on this task): it is not one of the "legacy" types, and <see cref="VelopackUpdateChecker"/>
/// already read it for the update channel before this file existed.
/// </para>
/// <para>
/// This file, like <see cref="VelopackUpdateChecker"/>, is one of only two files in the solution
/// that name the updater's own types. Everything else talks to <see cref="IStartupShortcut"/>,
/// which is what keeps <c>GalactiLog.App.Tests</c> off the real Startup folder: every test binds
/// a recording double, and this type is built only with a <see cref="BuildInfo"/> whose
/// <see cref="BuildInfo.IsInstalled"/> is false, which is what a <c>dotnet run</c> and a
/// <c>dotnet test</c> both are.
/// </para>
/// </remarks>
public sealed class VelopackStartupShortcut : IStartupShortcut
{
    /// <summary>
    /// The argument the Startup shortcut carries, so a machine boot brings the application up
    /// into the tray rather than onto the screen (spec 12.11 behaviour 9). Declared once, here;
    /// Phase 11 Task 4's <c>Program.Main</c> references this constant rather than repeating the
    /// literal, so the shortcut that writes one spelling and the parser that reads it cannot
    /// drift onto two.
    /// </summary>
    public const string MinimizedArgument = "--minimized";

    /// <summary>
    /// The executable name handed to Velopack. Relative, not a composed path: composing the
    /// install root here would be a second answer to "where is this application installed", and
    /// <see cref="VelopackLocator"/> already has the first one.
    /// </summary>
    public const string StubExecutableName = "GalactiLog.exe";

    private readonly BuildInfo _buildInfo;
    private readonly ILogger _logger;

    /// <param name="buildInfo">The one reader of build identity (design-lessons rule 1). Read
    /// once per call to <see cref="IsSupported"/>, never cached, because a property of the build
    /// cannot change while the process runs.</param>
    /// <param name="logger">Optional. Every Velopack call is wrapped, so a machine where the
    /// Startup folder is redirected by policy, or where COM refuses, reports a failure instead of
    /// crashing.</param>
    public VelopackStartupShortcut(BuildInfo buildInfo, ILogger? logger = null)
    {
        _buildInfo = buildInfo;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="BuildInfo.IsInstalled"/> and nothing else: no filesystem probe, no
    /// <see cref="VelopackLocator.IsCurrentSet"/> read, and no catch that turns a different
    /// failure into this answer. It is the same predicate the About tab's update button reads.
    /// </remarks>
    public bool IsSupported => _buildInfo.IsInstalled;

    /// <inheritdoc />
    public bool? Exists()
    {
        // Guards every member below: this is what keeps a dotnet run, a dotnet test and the whole
        // verification suite off the real Startup folder even if a caller forgot to bind a
        // recording double first.
        if (!IsSupported)
        {
            return null;
        }

        try
        {
            var shortcuts = new Shortcuts(VelopackLocator.Current);

            // ShortcutLocation.Startup and nothing else, ever: this enum is [Flags] and an
            // omitted location argument on the sibling calls below defaults to
            // Desktop | StartMenuRoot, which would silently report on or create shortcuts nobody
            // asked for.
            var found = shortcuts.FindShortcuts(StubExecutableName, ShortcutLocation.Startup);
            if (!found.TryGetValue(ShortcutLocation.Startup, out var link))
            {
                return false;
            }

            // Logged at debug on every read, because Target and Arguments are exactly what the
            // manual bar needs to confirm and what a support bundle would otherwise be missing.
            _logger.LogDebug(
                "Startup shortcut found: Target={Target} Arguments={Arguments}",
                link.Target,
                link.Arguments);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the Startup shortcut failed.");
            return null;
        }
    }

    /// <inheritdoc />
    public bool Apply(bool enabled)
    {
        // True rather than false on a build that cannot carry a shortcut (phase review finding
        // P5): nothing was attempted, so nothing failed, and the General tab no longer has to
        // re-test IsSupported at its own call site to tell the two apart. The guard itself stays
        // exactly where it was, so neither Velopack's locator nor its shortcut API is reached and
        // a dotnet run, a dotnet test and the verification suite still cannot touch the real
        // Startup folder.
        if (!IsSupported)
        {
            return true;
        }

        return enabled ? Create() : Remove();
    }

    private bool Create()
    {
        try
        {
            // updateOnly: false, because an absent shortcut must be created here, not skipped.
            new Shortcuts(VelopackLocator.Current).CreateShortcut(
                StubExecutableName,
                ShortcutLocation.Startup,
                updateOnly: false,
                programArguments: MinimizedArgument,
                icon: null);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Creating the Startup shortcut failed.");
            return false;
        }
    }

    private bool Remove()
    {
        try
        {
            // DeleteShortcuts, not RemoveShortcutForThisExe: the link was created for the stub's
            // name, and RemoveShortcutForThisExe removes links for the running executable, which
            // on an installed build is the "current\" copy an update replaces.
            new Shortcuts(VelopackLocator.Current).DeleteShortcuts(
                StubExecutableName, ShortcutLocation.Startup);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Removing the Startup shortcut failed.");
            return false;
        }
    }
}
