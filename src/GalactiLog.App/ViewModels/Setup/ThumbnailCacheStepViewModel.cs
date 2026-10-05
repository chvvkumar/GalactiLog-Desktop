using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.ViewModels.Settings;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Setup;

/// <summary>
/// Design-spec 12.1's step 2: the data location, then the thumbnail cache location, each with a
/// folder picker, plus a free-space readout for the chosen cache volume. Persists
/// <c>general.thumbnail_cache_dir</c>; the data location is not a settings key.
/// </summary>
/// <remarks>
/// <para>
/// The type name predates the second field. Phase 10 Task 9 extended this step into the wizard's
/// "Storage locations" step and deliberately did not rename the type, which would touch the wizard
/// view-model, the window XAML, the window code-behind and two test files for no behaviour change
/// (questions-task9.md Q9.6, ruled (a)).
/// </para>
/// <para>
/// Every data location sentence, refusal and rule is <c>StorageTabViewModel</c>'s, called rather
/// than copied, for the same reason the cache path validation is.
/// </para>
/// <para>
/// The path validation and the free-space read are <c>StorageTabViewModel</c>'s
/// (<c>ValidateCacheDir</c>, <c>ReadVolumeSpace</c>, <c>FormatVolumeSpace</c>), called rather than
/// copied: a path the wizard accepts and the Settings tab refuses, or two different answers to
/// "how much room is left", are exactly what the collision map's designated-owner table exists to
/// prevent (design-lessons rule 1). The store's own <c>ValidateGeneral</c> stays the enforcement
/// point behind both.
/// </para>
/// <para>
/// Next is always enabled here, because the default is valid. The step still refuses a path the
/// store would reject, so the user sees which rule was broken at the field instead of at the save.
/// </para>
/// <para>
/// Nothing on this path creates the directory. <c>AppHost</c>'s <c>GeneralChanged</c> handler
/// already calls <c>AppWriter.CreateDirectory(appWriter.ThumbnailCacheRoot)</c> after a general
/// save, which is the one sanctioned creation in the application (spec 2.1).
/// </para>
/// </remarks>
public sealed partial class ThumbnailCacheStepViewModel : SetupStepViewModel
{
    private readonly Func<string, (long Total, long Free)?> _volumeSpace;
    private readonly Func<string> _defaultCacheRoot;
    private readonly Func<string, string?>? _requestDataRootMove;
    private readonly Action? _cancelDataRootMove;

    // Not an [ObservableProperty]: the constructor has to seed the field without running the
    // change handler, which would start a free-space read for a path Load is about to replace and
    // leave two ungenerationed reads racing for the last word (Task 9 review, minor finding 3).
    private string _cachePath = "";

    /// <param name="volumeSpace">Normally <c>StorageTabViewModel.ReadVolumeSpace</c>. Called off
    /// the UI thread.</param>
    /// <param name="defaultCacheRoot">The app data default,
    /// <c>%LOCALAPPDATA%\GalactiLogData\thumbnails</c> for whatever app data root is in effect, which
    /// is what an empty <c>thumbnail_cache_dir</c> means. Not the effective cache root: a re-run
    /// with a custom path would then compare that path against itself and store "" for it. A
    /// delegate, so the wizard constructs in a test with no profile directory.</param>
    /// <param name="dataRoot">Where the app data root came from, read once. Null leaves the data
    /// location block hidden, which is what a test that is not about it wants.</param>
    /// <param name="requestDataRootMove">The one recorder of a picked data location. Returns null
    /// on success and a refusal to show otherwise.</param>
    /// <param name="cancelDataRootMove">Drops a requested move.</param>
    public ThumbnailCacheStepViewModel(
        Func<string, (long Total, long Free)?> volumeSpace,
        Func<string> defaultCacheRoot,
        Action<Action>? post = null,
        ILogger? logger = null,
        Func<AppDataRootResolution>? dataRoot = null,
        Func<string, string?>? requestDataRootMove = null,
        Action? cancelDataRootMove = null)
        : base(post, logger)
    {
        _volumeSpace = volumeSpace;
        _defaultCacheRoot = defaultCacheRoot;
        _requestDataRootMove = requestDataRootMove;
        _cancelDataRootMove = cancelDataRootMove;
        FreeSpaceText = StorageTabViewModel.FreeSpaceUnavailable;

        var resolution = dataRoot?.Invoke();
        HasDataLocation = resolution is not null;
        DataRootPath = resolution?.Root ?? "";
        DataRootSourceText = resolution is null ? "" : StorageTabViewModel.DataRootSourceLabel(resolution.Source);
        PendingDataRoot = resolution?.PendingRoot;
    }

    /// <inheritdoc />
    public override string Title => "Storage locations";

    /// <summary>Design-spec 11.3, on screen: relocating the cache moves nothing. The Storage tab's
    /// own sentence, read from its one definition rather than copied, so the two surfaces cannot
    /// say different things.</summary>
    public string RelocationNote { get; } = StorageTabViewModel.RelocationNoteText;

    /// <summary>The cache location, seeded with the resolved default by <see cref="Load"/>.
    /// </summary>
    public string CachePath
    {
        get => _cachePath;
        set
        {
            if (SetProperty(ref _cachePath, value))
            {
                OnCachePathChanged(value);
            }
        }
    }

    /// <summary>The inline refusal for the typed path, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPathError))]
    public partial string? PathError { get; private set; }

    public bool HasPathError => PathError is not null;

    /// <summary>The chosen volume's free-space line, or "Free space unavailable".</summary>
    [ObservableProperty]
    public partial string FreeSpaceText { get; private set; }

    /// <inheritdoc />
    public override bool CanAdvance => PathError is null;

    /// <summary>The in-flight free-space read, so a test awaits it instead of blocking on it.
    /// </summary>
    internal Task? PendingFreeSpace { get; private set; }

    // ---- the data location block (spec 12.1 step 2, 17.2) -------------------------------------

    /// <summary>Whether the step was given the data location seams.</summary>
    public bool HasDataLocation { get; }

    /// <summary>The resolved app data root. Fixed for the life of the process.</summary>
    public string DataRootPath { get; }

    /// <summary>Where the root came from, in the user's words.</summary>
    public string DataRootSourceText { get; }

    /// <summary>Spec 17.2's note, read from the Storage tab's one definition.</summary>
    public string DataRootNote { get; } = StorageTabViewModel.DataRootNoteText;

    /// <summary>The destination of a requested move, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingMove))]
    [NotifyPropertyChangedFor(nameof(PendingMoveNote))]
    [NotifyCanExecuteChangedFor(nameof(CancelDataRootMoveCommand))]
    public partial string? PendingDataRoot { get; private set; }

    public bool HasPendingMove => PendingDataRoot is not null;

    /// <summary>What the pending move says, naming both paths. The Storage tab's format string.
    /// </summary>
    public string PendingMoveNote => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        StorageTabViewModel.PendingMoveNoteFormat,
        PendingDataRoot,
        DataRootPath);

    /// <summary>The refusal for the last picked data location, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDataRootError))]
    public partial string? DataRootError { get; private set; }

    public bool HasDataRootError => DataRootError is not null;

    /// <summary>Called by the window's data location picker. Records a move for the next start and
    /// changes nothing on disk. Next stays enabled either way: the current location is valid, a
    /// pending move is not an error, and a refusal shows at the field.</summary>
    public void SetDataRoot(string path)
    {
        var refusal = StorageTabViewModel.ValidateDataRoot(path, DataRootPath);
        if (refusal is not null)
        {
            DataRootError = refusal;
            return;
        }

        var hostRefusal = _requestDataRootMove?.Invoke(path);
        if (hostRefusal is not null)
        {
            DataRootError = hostRefusal;
            return;
        }

        DataRootError = null;
        // The normalized spelling the pointer stores, so the note on screen and the document on
        // disk cannot name the same folder two ways (review finding M7).
        PendingDataRoot = DataRootPointer.Normalize(path) ?? path;
    }

    /// <summary>Drops a requested move. Nothing on disk changes either way.</summary>
    [RelayCommand(CanExecute = nameof(HasPendingMove))]
    public void CancelDataRootMove()
    {
        // The command's CanExecute carries the rule; the body repeats it, because a command can be
        // invoked directly (TRACKING section 6 item 13).
        if (!HasPendingMove)
        {
            return;
        }

        _cancelDataRootMove?.Invoke();
        PendingDataRoot = null;
        DataRootError = null;
    }

    /// <summary>Called by the view's folder picker. A picker result is a user gesture like typing
    /// one.</summary>
    public void SetCachePath(string path) => CachePath = path;

    /// <inheritdoc />
    public override void Load(GeneralSettings general)
    {
        // Spec 12.1: the box shows the resolved %LOCALAPPDATA%\GalactiLogData\thumbnails rather than
        // an empty field, because "empty means the default" is a storage encoding, not something
        // a first-run user should have to know.
        CachePath = general.ThumbnailCacheDir.Trim().Length > 0
            ? general.ThumbnailCacheDir
            : SafeDefaultRoot();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A path equal to the resolved default is stored as the empty string, which is spec 5.8.1's
    /// own encoding for "the default location". Writing the resolved path instead would pin the
    /// cache to one app data root, so a later run under a different <c>GALACTILOG_APPDATA</c>
    /// would keep writing into the first one. A path the user actually chose is stored verbatim.
    /// </remarks>
    public override GeneralSettings Apply(GeneralSettings general)
    {
        var typed = CachePath.Trim();
        var stored = typed.Length == 0 || IsTheDefaultRoot(typed) ? "" : typed;
        return general with { ThumbnailCacheDir = stored };
    }

    private void OnCachePathChanged(string value)
    {
        PathError = StorageTabViewModel.ValidateCacheDir(value);
        RaiseCanAdvanceChanged();
        RefreshFreeSpace(value);
    }

    // Off the UI thread, always: a disconnected network path can block inside DriveInfo for
    // seconds. A volume that cannot be read degrades to one line rather than throwing. The same
    // shape as StorageTabViewModel.RefreshFreeSpace, which is the pattern this step copies.
    private void RefreshFreeSpace(string path)
    {
        var target = path.Trim().Length > 0 ? path.Trim() : SafeDefaultRoot();
        if (target.Length == 0)
        {
            FreeSpaceText = StorageTabViewModel.FreeSpaceUnavailable;
            return;
        }

        var token = Lifetime;
        PendingFreeSpace = Task.Run(
            () =>
            {
                (long Total, long Free)? space;
                try
                {
                    space = _volumeSpace(target);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Reading the free space on {CacheRoot} failed", target);
                    space = null;
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                Post(() =>
                {
                    if (!IsDisposed)
                    {
                        FreeSpaceText = StorageTabViewModel.FormatVolumeSpace(space);
                    }
                });
            },
            token);
    }

    private bool IsTheDefaultRoot(string typed)
    {
        var resolvedDefault = SafeDefaultRoot();
        if (resolvedDefault.Length == 0)
        {
            return false;
        }

        try
        {
            return string.Equals(
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(typed)),
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(resolvedDefault)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    private string SafeDefaultRoot()
    {
        try
        {
            return _defaultCacheRoot() ?? "";
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The default thumbnail cache root could not be resolved");
            return "";
        }
    }
}
