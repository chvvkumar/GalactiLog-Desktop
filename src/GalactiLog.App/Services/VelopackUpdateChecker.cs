using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace GalactiLog.App.Services;

/// <summary>
/// The one file in the solution that names <c>UpdateManager</c>, <c>GithubSource</c> and
/// <c>UpdateInfo</c>. Everything above it talks to <see cref="IUpdateChecker"/>, which is what
/// keeps <c>GalactiLog.App.Tests</c> off the network and out of the updater's locator.
/// </summary>
/// <remarks>
/// <para>
/// <c>Program.Main</c> names <c>VelopackApp</c> and must: its hooks run and exit during install,
/// update and uninstall (spec 17.1). That is the one other Velopack reference in <c>src/**</c>,
/// and one test censuses both names, this file against the three types below and
/// <c>Program.cs</c> against <c>VelopackApp</c>.
/// </para>
/// <para>
/// Nothing here opens a stream or writes a file. The updater stages its packages under
/// <c>%LOCALAPPDATA%</c> through its own installed hooks, outside this codebase's
/// <c>System.IO</c> surface (spec 2.1).
/// </para>
/// </remarks>
public sealed class VelopackUpdateChecker : IUpdateChecker
{
    /// <summary>
    /// Spec 17.1's update feed of record. A constant, not a setting: a settings key for the feed
    /// would be a second source of truth about where this application's updates come from.
    /// </summary>
    public const string RepositoryUrl = "https://github.com/chvvkumar/GalactiLog-Desktop";

    /// <summary>Spec 17.4's channel on <c>main</c>. The two others, <c>alpha</c> on <c>snd</c>
    /// and <c>rc</c> on <c>dev</c>, are published as GitHub prereleases.</summary>
    public const string StableChannel = "stable";

    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private UpdateManager? _manager;
    private bool _probed;

    /// <param name="logger">Optional. A manager that could not be built is logged, never
    /// rethrown: an application must not fail to start over an updater that is not there.</param>
    public VelopackUpdateChecker(ILogger? logger = null) => _logger = logger ?? NullLogger.Instance;

    /// <inheritdoc />
    public bool IsInstalled => Manager?.IsInstalled ?? false;

    /// <summary>
    /// The channel this build was installed from, read from the updater's own locator.
    /// </summary>
    /// <remarks>
    /// The locator rather than <c>UpdateManager.Channel</c>, which is protected in Velopack 1.2.0
    /// and not readable from outside the manager. <c>VelopackLocator.Current</c> is the same value
    /// the manager itself reads, and it needs no manager, which is what lets
    /// <see cref="BuildInfo"/> report a channel on a build where the manager cannot be built at
    /// all.
    /// </remarks>
    public string Channel => ReadChannel() ?? BuildInfo.LocalChannel;

    /// <inheritdoc />
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var manager = Manager;
        if (manager is null || !manager.IsInstalled)
        {
            return null;
        }

        var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null)
        {
            return null;
        }

        var asset = info.TargetFullRelease;
        return new AvailableUpdate(
            asset?.Version?.ToString() ?? DiagnosticsService.Unknown,
            ReleaseNotesOf(asset),
            info);
    }

    /// <inheritdoc />
    public Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken ct)
    {
        var manager = Manager;
        return manager is null || update.Handle is not UpdateInfo info
            ? Task.CompletedTask
            : manager.DownloadUpdatesAsync(info, progress, ct);
    }

    /// <inheritdoc />
    public void ApplyAndRestart(AvailableUpdate update)
    {
        var manager = Manager;
        if (manager is null || update.Handle is not UpdateInfo info)
        {
            return;
        }

        manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }

    // Built once, on first use, and never rebuilt. Null means the updater could not be reached,
    // which is every path's "not installed".
    private UpdateManager? Manager
    {
        get
        {
            lock (_gate)
            {
                if (!_probed)
                {
                    _probed = true;
                    _manager = TryCreate();
                }

                return _manager;
            }
        }
    }

    private UpdateManager? TryCreate()
    {
        try
        {
            // Spec 17.4's alpha and rc builds are published as GitHub prereleases, so the feed has
            // to be asked for them; a stable install asks for releases only. The channel itself
            // still comes from the install and never from here: this flag only widens which
            // GitHub releases the source enumerates.
            var prerelease = !string.Equals(
                ReadChannel(), StableChannel, StringComparison.OrdinalIgnoreCase);

            // options: null is deliberate and load bearing. Spec 17.1: "the installed application
            // checks its own channel only, so a stable install never offers itself a prerelease".
            // That is the manager's own default behaviour -- it reads the channel the application
            // was installed from -- and UpdateOptions.ExplicitChannel only overrides it. Setting
            // ExplicitChannel from a setting or a constant would break the guarantee, not
            // implement it (ruling Q12), and a test scans src/** to prove nothing here sets it.
            return new UpdateManager(
                new GithubSource(RepositoryUrl, accessToken: null, prerelease), options: null);
        }
        catch (Exception ex)
        {
            // A process the updater did not install has no locator to read, which is the normal
            // case for a developer build. Debug, not warning: it is the designed outcome there.
            _logger.LogDebug(ex, "The update manager could not be built; this build checks nothing.");
            return null;
        }
    }

    private string? ReadChannel()
    {
        try
        {
            return VelopackLocator.Current?.Channel is { Length: > 0 } channel ? channel : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The updater's locator could not be read.");
            return null;
        }
    }

    // Spec 19.2: no embedded browser. The notes are carried as text and rendered as text; the
    // markdown form is preferred because the HTML form would render as tags.
    private static string? ReleaseNotesOf(VelopackAsset? asset)
        => asset?.NotesMarkdown is { Length: > 0 } markdown ? markdown : null;
}
