using System.Reflection;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.Services;

/// <summary>
/// Version, git SHA and release channel of the running build. One reader, so spec 12.7's About
/// tab, spec 12.8's Diagnostics Versions group and spec 16.3's bundle cannot disagree about which
/// build is running (design-lessons rule 1).
/// </summary>
/// <remarks>
/// <para>
/// Two constructors on purpose: every test builds one by hand with the four values, and
/// <see cref="FromProcess"/> is the one place the process is actually read. No test reaches
/// Velopack's real locator through the hand-built form.
/// </para>
/// <para>
/// This type names no Velopack type. The channel and the installed flag are asked of
/// <see cref="IUpdateChecker"/>, whose one production implementation is the single file in the
/// solution that names <c>UpdateManager</c>.
/// </para>
/// </remarks>
public sealed class BuildInfo
{
    /// <summary>The longest a git SHA can be, and the cap on what
    /// <see cref="ParseGitSha(string?)"/> returns, so a malformed informational version cannot
    /// become an unbounded label on the About tab.</summary>
    public const int MaxGitShaLength = 40;

    /// <summary>
    /// What <see cref="Channel"/> reports for a process the updater did not install: a
    /// <c>dotnet run</c>, a <c>dotnet test</c>, and any unpacked build.
    /// </summary>
    /// <remarks>
    /// The word is <c>local</c> rather than <see cref="DiagnosticsService.Unknown"/> because an
    /// uninstalled build genuinely has no channel. On a support bundle "this build has no channel"
    /// is a different and more useful fact than "the channel could not be read".
    /// </remarks>
    public const string LocalChannel = "local";

    /// <param name="version">Spec 12.7's "Version".</param>
    /// <param name="gitSha">Spec 12.7's "git SHA".</param>
    /// <param name="channel">Spec 12.7's "release channel".</param>
    /// <param name="isInstalled">Whether the updater installed this process.</param>
    public BuildInfo(string version, string gitSha, string channel, bool isInstalled)
    {
        Version = version;
        GitSha = gitSha;
        Channel = channel;
        IsInstalled = isInstalled;
    }

    /// <summary>Spec 12.7's "Version". The assembly version, or
    /// <see cref="DiagnosticsService.Unknown"/>.</summary>
    public string Version { get; }

    /// <summary>
    /// Spec 12.7's "git SHA". The part of <see cref="AssemblyInformationalVersionAttribute"/>
    /// after the first <c>+</c>, which is what the SDK appends from
    /// <c>SourceRevisionId</c>, or <see cref="DiagnosticsService.Unknown"/> when the informational
    /// version carries no <c>+</c> segment.
    /// </summary>
    /// <remarks>
    /// Ruling Q13 with its correction: a build from a git working copy already carries the HEAD
    /// SHA, because the SDK's SourceLink support sets <c>SourceRevisionId</c> itself and
    /// <c>IncludeSourceRevisionInInformationalVersion</c> is set explicitly in the csproj. The
    /// release workflow passes <c>-p:SourceRevisionId</c> anyway (Task 5), because explicit is
    /// better than implicit for the artefact that ships. <c>unknown</c> is therefore what a build
    /// outside a checkout, or one with SourceLink disabled, reports, and it is the same literal
    /// the web's health endpoint uses for absent build metadata.
    /// </remarks>
    public string GitSha { get; }

    /// <summary>
    /// Spec 12.7's "release channel": the channel the running build was installed from, read from
    /// the update manager, or <see cref="LocalChannel"/> when the process is not an installed
    /// build.
    /// </summary>
    /// <remarks>
    /// Spec 17.1's "the installed application checks its own channel only, so a stable install
    /// never offers itself a prerelease" is the update manager's own default behaviour, so this is
    /// a readout and never an override. Nothing in this application sets
    /// <c>UpdateOptions.ExplicitChannel</c> (ruling Q12), and a test scans <c>src/**</c> to prove
    /// it.
    /// </remarks>
    public string Channel { get; }

    /// <summary>False for a <c>dotnet run</c>, a <c>dotnet test</c>, and any unpacked build.
    /// Every update path is gated on it, which is what keeps a developer build and the whole test
    /// suite off the network.</summary>
    public bool IsInstalled { get; }

    /// <summary>
    /// Reads the running process: the assembly version, the informational version's commit
    /// segment, and the channel and installed flag the updater reports.
    /// </summary>
    /// <param name="checker">The update seam to ask. <c>AppHost</c> passes the registered
    /// singleton so the process builds one update manager rather than two; null builds the
    /// production checker, which is what a test that wants the real uninstalled answer gets.
    /// </param>
    /// <param name="logger">Optional. A probe that failed is logged, never rethrown.</param>
    /// <remarks>
    /// Never throws. A process the updater did not install can report nothing or raise from the
    /// locator, and a host must not fail to build over a version label, so any failure reports
    /// <see cref="LocalChannel"/> and not installed.
    /// </remarks>
    public static BuildInfo FromProcess(IUpdateChecker? checker = null, ILogger? logger = null)
    {
        var assembly = typeof(BuildInfo).Assembly;
        var version = ReadVersion(assembly.GetName().Version);
        var gitSha = ParseGitSha(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

        var channel = LocalChannel;
        var isInstalled = false;
        try
        {
            var probe = checker ?? new VelopackUpdateChecker(logger);
            isInstalled = probe.IsInstalled;

            // An uninstalled build has no channel to report, whatever the manager's default
            // channel happens to be for this platform.
            if (isInstalled && probe.Channel is { Length: > 0 } reported)
            {
                channel = reported;
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "The update channel could not be read; reporting the local build.");
        }

        return new BuildInfo(version, gitSha, channel, isInstalled);
    }

    /// <summary>The assembly version as text, or <see cref="DiagnosticsService.Unknown"/>.
    /// Separate from <see cref="FromProcess"/> so the absent case has a test.</summary>
    internal static string ReadVersion(Version? version)
        => version?.ToString() ?? DiagnosticsService.Unknown;

    /// <summary>
    /// The commit segment of an informational version: everything after the first <c>+</c>,
    /// trimmed, capped at <see cref="MaxGitShaLength"/>, or
    /// <see cref="DiagnosticsService.Unknown"/> when there is no such segment.
    /// </summary>
    internal static string ParseGitSha(string? informationalVersion)
    {
        if (string.IsNullOrEmpty(informationalVersion))
        {
            return DiagnosticsService.Unknown;
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0 || plus == informationalVersion.Length - 1)
        {
            return DiagnosticsService.Unknown;
        }

        var sha = informationalVersion[(plus + 1)..].Trim();
        if (sha.Length == 0)
        {
            return DiagnosticsService.Unknown;
        }

        return sha.Length > MaxGitShaLength ? sha[..MaxGitShaLength] : sha;
    }
}
