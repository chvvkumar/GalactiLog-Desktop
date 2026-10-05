namespace GalactiLog.App.Services;

/// <summary>
/// Spec 12.11 behaviour 9 and spec 4.4 step 2: what the GUI launch's command line asked for, and
/// what is left of it for Avalonia.
/// </summary>
/// <remarks>
/// <para>
/// A record with a static parser rather than a branch inside <c>Program.Main</c>, for the reason
/// <c>AppHost.IsUsableAppDataOverride</c> and <c>AppHost.FollowDefaultPageSize</c> are static:
/// <c>Program.Main</c> cannot be exercised by a test, so every rule that lives in it is a rule no
/// case can assert. This type is the assertable half, and the ordering rules that have to stay in
/// <c>Main</c> are asserted over its source text instead.
/// </para>
/// <para>
/// One switch is recognized and nothing else. Anything this parser does not know is left in
/// <see cref="Remaining"/>, so <c>Program.Main</c> hands it to <c>CliDispatcher</c> exactly as it
/// did before this type existed: <c>--help</c>, an unknown verb and every known verb reach the
/// dispatcher byte for byte, which is what keeps the whole of spec 15 and
/// <c>GalactiLog.Cli.Tests</c> unchanged.
/// </para>
/// </remarks>
/// <param name="Minimized">Whether <c>--minimized</c> was present.</param>
/// <param name="Remaining">The arguments with every recognized GUI switch removed, which is what
/// is handed to Avalonia's lifetime and, when anything is left, to the CLI dispatcher.</param>
public sealed record StartupArguments(bool Minimized, string[] Remaining)
{
    /// <summary>
    /// Spec 12.11 behaviour 9's one switch. The literal is declared on
    /// <see cref="VelopackStartupShortcut"/>, which is what writes it into the Startup shortcut,
    /// and referenced here so the shortcut that writes one spelling and the parser that reads it
    /// cannot drift onto two. The literal exists in exactly one file in <c>src</c> and
    /// <c>StartupArgumentsTests.TheMinimizedLiteral_AppearsInExactlyOneSourceFile</c> is what
    /// keeps it there.
    /// </summary>
    public static string MinimizedSwitch => VelopackStartupShortcut.MinimizedArgument;

    /// <summary>
    /// Parses a launch's command line. Never returns null and never throws.
    /// </summary>
    /// <remarks>
    /// The switch is matched with <see cref="StringComparer.OrdinalIgnoreCase"/>, so a shortcut
    /// edited by hand or a shell that upper-cases still starts into the tray;
    /// <c>CliDispatcher</c> matches its verbs case-insensitively as well, through
    /// <c>ToLowerInvariant</c>, so the two agree about case. Every occurrence is removed, not just
    /// the first.
    /// </remarks>
    /// <param name="args">The process arguments, as <c>Program.Main</c> received them.</param>
    public static StartupArguments Parse(string[] args)
    {
        if (args is null || args.Length == 0)
        {
            return new StartupArguments(false, []);
        }

        var minimized = false;
        var remaining = new List<string>(args.Length);
        foreach (var argument in args)
        {
            if (string.Equals(argument, MinimizedSwitch, StringComparison.OrdinalIgnoreCase))
            {
                minimized = true;
                continue;
            }

            remaining.Add(argument);
        }

        return new StartupArguments(minimized, [.. remaining]);
    }
}

/// <summary>
/// Spec 12.11 behaviour 9's resolved answer: whether this process came up with no window at all.
/// </summary>
/// <remarks>
/// <para>
/// Composed once, in <c>AppHost.Build</c>, which is the one place that holds both halves of the
/// rule: the switch <c>Program.Main</c> parsed into <c>App.StartupArguments</c> before the host
/// was built, and the <c>general</c> document <c>Build</c> already read for itself. The rule is
/// <c>App.ShouldShowWindowAtStartup</c> and lives there alone; this record carries its answer to
/// the two readers that need it, <c>App.OnFrameworkInitializationCompleted</c> and spec 12.8's
/// "Started minimized" field, so a page and a window cannot disagree about what happened.
/// </para>
/// <para>
/// A record with one property rather than a <c>bool</c> in the container, so a later start-up fact
/// adds a property here rather than a second unnamed <c>bool</c> registration.
/// </para>
/// </remarks>
/// <param name="StartedMinimized">True when this process came up with no window, whether the
/// <c>--minimized</c> argument or <c>general.start_minimized</c> caused it.</param>
public sealed record StartupState(bool StartedMinimized);
