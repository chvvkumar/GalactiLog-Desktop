namespace GalactiLog.Core.Io;

/// <summary>Where the app data root came from (spec 17.2's resolution order).</summary>
public enum AppDataRootSource
{
    /// <summary>The test-only <c>Build</c> parameter.</summary>
    ExplicitOverride,

    /// <summary><c>GALACTILOG_APPDATA</c>.</summary>
    EnvironmentVariable,

    /// <summary>The data location pointer's <c>data_root</c>.</summary>
    Pointer,

    /// <summary><c>%LOCALAPPDATA%\GalactiLogData</c>.</summary>
    Default,
}

/// <summary>
/// The two well-known folder names spec 17.2 computes from the profile: the default data root and
/// the legacy one an install from before Phase 10 Task 9 used.
/// </summary>
/// <remarks>
/// A record rather than two static methods so a test can resolve against temp directories instead
/// of the machine's real profile. <see cref="System"/> is the production value and the only one
/// the application itself ever uses.
/// </remarks>
/// <param name="DefaultRoot">Normally <c>%LOCALAPPDATA%\GalactiLogData</c>.</param>
/// <param name="LegacyRoot">Normally <c>%LOCALAPPDATA%\GalactiLog</c>, which is also the Velopack
/// install root and is therefore removed in full by an uninstall.</param>
public sealed record AppDataRootFolders(string DefaultRoot, string LegacyRoot)
{
    /// <summary>The real profile's pair.</summary>
    public static AppDataRootFolders System { get; } = new(
        AppDataRootResolver.DefaultRoot(),
        AppDataRootResolver.LegacyRoot());
}

/// <summary>
/// Where the app data root came from, plus anything the caller has to act on: a pending move the
/// pointer records, the previous root a completed move left behind, and a one-line reason when a
/// pointer was present and ignored.
/// </summary>
/// <param name="Root">The resolved app data root.</param>
/// <param name="Source">Which rule in spec 17.2's order supplied it.</param>
/// <param name="PendingRoot">A move the user asked for, or null. Always null for an override.
/// </param>
/// <param name="PreviousRoot">The root a completed move copied out of, or null. Always null for an
/// override.</param>
/// <param name="PointerWarning">Why a pointer that existed was ignored, or null.</param>
public sealed record AppDataRootResolution(
    string Root,
    AppDataRootSource Source,
    string? PendingRoot,
    string? PreviousRoot,
    string? PointerWarning);

/// <summary>
/// Spec 17.2's resolution order, as one function. The rule it exists to enforce: the application
/// never silently creates a second empty catalogue. A pointer that names a folder the user's data
/// used to be in is a disconnected drive nine times out of ten, and starting with an empty library
/// is how a user concludes their catalogue is gone, so that case throws rather than falling back.
/// </summary>
/// <remarks>
/// <see cref="Resolve"/> performs no write and creates no directory. It reads at most one file.
/// </remarks>
public static class AppDataRootResolver
{
    /// <summary>The verification and CI override spec 17.2 describes.</summary>
    public const string EnvironmentVariableName = "GALACTILOG_APPDATA";

    /// <summary>The folder name under <c>%LOCALAPPDATA%</c> the data root defaults to. A sibling
    /// of the Velopack install root, not a child of it, so an uninstall cannot reach it.</summary>
    public const string DefaultRootFolderName = "GalactiLogData";

    /// <summary>The folder name installs from before this version used, which is also the
    /// Velopack install root.</summary>
    public const string LegacyRootFolderName = "GalactiLog";

    /// <summary>The reason an unavailable pointer root reports.</summary>
    public const string FolderDoesNotExist = "the folder does not exist";

    /// <summary><c>%LOCALAPPDATA%\GalactiLogData</c>.</summary>
    public static string DefaultRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DefaultRootFolderName);

    /// <summary><c>%LOCALAPPDATA%\GalactiLog</c>, for the adoption path and for the message the
    /// unavailable-root report shows.</summary>
    public static string LegacyRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyRootFolderName);

    /// <summary>Spec 12.8's Paths group reports the source as one of these words.</summary>
    public static string SourceLabel(AppDataRootSource source) => source switch
    {
        AppDataRootSource.ExplicitOverride => "override",
        AppDataRootSource.EnvironmentVariable => "environment",
        AppDataRootSource.Pointer => "pointer",
        _ => "default",
    };

    // Review item 6, moved here verbatim from AppHost.IsUsableAppDataOverride: honor a root only
    // when it is fully qualified (rejects empty, relative, and drive-relative values like "C:foo")
    // and not a bare drive or UNC share root (AppWriter.IsDriveOrShareRoot, already used for the
    // same check on general.thumbnail_cache_dir). Resolving a relative value against the process's
    // current directory is not the "absolute directory" spec 17.2 asks for, so that case is
    // rejected here instead of silently normalized.
    /// <summary>Whether a candidate root is one this application will use.</summary>
    public static bool IsUsableRoot(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            return !AppWriter.IsDriveOrShareRoot(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Spec 17.2's order: the explicit override, then <c>GALACTILOG_APPDATA</c>, then the
    /// pointer's <c>data_root</c>, then <c>%LOCALAPPDATA%\GalactiLogData</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the override or the environment variable supplies the root, the pointer is neither
    /// read nor carried: every test in the suite and every packaging verification run passes one
    /// of those, so none of them can touch a real pointer file or a real library.
    /// </para>
    /// <para>
    /// A pointer that exists but is malformed, empty or names an unusable path is treated as
    /// absent: the default applies and the reason is carried on
    /// <see cref="AppDataRootResolution.PointerWarning"/>. A pointer naming a usable path whose
    /// directory is missing throws <see cref="AppDataRootUnavailableException"/> instead, because
    /// falling back there is what would create the second, empty catalogue.
    /// </para>
    /// </remarks>
    /// <param name="explicitOverride">The test-only <c>Build</c> parameter, or null.</param>
    /// <param name="environmentValue">The <c>GALACTILOG_APPDATA</c> value, or null.</param>
    /// <param name="pointerPath">Where the pointer document lives.</param>
    /// <param name="folders">The default and legacy folder pair. Defaults to the real profile's.
    /// </param>
    public static AppDataRootResolution Resolve(
        string? explicitOverride,
        string? environmentValue,
        string pointerPath,
        AppDataRootFolders? folders = null)
    {
        if (explicitOverride is { Length: > 0 })
        {
            return new AppDataRootResolution(
                Path.GetFullPath(explicitOverride), AppDataRootSource.ExplicitOverride, null, null, null);
        }

        if (IsUsableRoot(environmentValue))
        {
            return new AppDataRootResolution(
                Path.GetFullPath(environmentValue!), AppDataRootSource.EnvironmentVariable, null, null, null);
        }

        var defaultRoot = (folders ?? AppDataRootFolders.System).DefaultRoot;
        var document = DataRootPointer.Read(pointerPath);
        var pending = DataRootPointer.Normalize(document?.PendingRoot);
        var previous = DataRootPointer.Normalize(document?.PreviousRoot);

        if (document is null)
        {
            // Absent and malformed are both "no pointer", but only one of them is worth a line in
            // the log: a file that is there and cannot be read is a fault the user can act on.
            var warning = UserFiles.Exists(pointerPath)
                ? $"The data location pointer at '{pointerPath}' is empty or not valid JSON. "
                    + "The default location is being used instead."
                : null;
            return new AppDataRootResolution(
                Path.GetFullPath(defaultRoot), AppDataRootSource.Default, null, null, warning);
        }

        var stored = document.DataRoot;
        if (stored is null)
        {
            // The key is absent, which is what a pointer holding only a pending move looks like.
            // Not a fault, so no warning.
            return new AppDataRootResolution(
                Path.GetFullPath(defaultRoot), AppDataRootSource.Default, pending, previous, null);
        }

        if (!IsUsableRoot(stored))
        {
            return new AppDataRootResolution(
                Path.GetFullPath(defaultRoot),
                AppDataRootSource.Default,
                pending,
                previous,
                $"The data location pointer names '{stored}', which is not an absolute folder path. "
                + "The default location is being used instead.");
        }

        var root = Path.GetFullPath(stored);
        if (!UserFiles.DirectoryExists(root))
        {
            throw new AppDataRootUnavailableException(root, pointerPath, FolderDoesNotExist);
        }

        return new AppDataRootResolution(root, AppDataRootSource.Pointer, pending, previous, null);
    }
}
