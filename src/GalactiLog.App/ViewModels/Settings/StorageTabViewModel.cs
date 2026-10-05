using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using Microsoft.Extensions.Logging;

namespace GalactiLog.App.ViewModels.Settings;

/// <summary>One preview resolution choice (design-spec 11.3): the stored width and its label.
/// </summary>
public sealed record PreviewResolutionOption(int Pixels, string Label);

/// <summary>
/// Design-spec 12.7's Storage tab: the thumbnail cache path with a picker and a free-space
/// readout, the preview resolution, the preview cache size in megabytes, and the thumbnail width.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here creates, moves or deletes anything on disk (design-spec 2.1). The free-space read
/// is <c>DriveInfo</c>, which has no write members. Relocating the cache moves no files by design
/// (design-spec 11.3), and the one directory creation on a cache-path change already lives in
/// <c>AppHost</c>'s <c>GeneralChanged</c> handler and goes through <c>AppWriter</c>. The folder
/// picker is an Avalonia storage-provider call in the view, handed here as a plain string.
/// </para>
/// <para>
/// The free-space read runs off the UI thread: a disconnected network path can block for seconds
/// inside <c>DriveInfo</c>. <see cref="ReadVolumeSpace"/> and <see cref="FormatVolumeSpace"/> are
/// static because Task 9's setup wizard shows the same readout for the same folder, and two
/// answers to "how much room is left", one of which blocks the UI thread, is exactly the
/// duplication the collision map's designated-owner table exists to prevent.
/// </para>
/// </remarks>
public sealed partial class StorageTabViewModel : GeneralSettingsTabViewModel
{
    /// <summary>Design-spec 11.3's four preview resolutions. 0 means the camera's native
    /// resolution.</summary>
    public static readonly IReadOnlyList<PreviewResolutionOption> PreviewResolutions =
    [
        new(1600, "1600 px"),
        new(2400, "2400 px"),
        new(4000, "4000 px"),
        new(0, "Native"),
    ];

    /// <summary>Design-spec 5.8.1 and 11.3: the eviction bound is
    /// <c>max(preview_cache_mb, 100)</c>, so a smaller stored value is honoured as 100.</summary>
    public const int MinimumCacheMb = ThumbnailCache.MinimumCacheMb;

    /// <summary>The web's own upper bound on the same input.</summary>
    public const int MaximumCacheMb = 51200;

    /// <summary>Shown when a volume cannot be read at all.</summary>
    public const string FreeSpaceUnavailable = "Free space unavailable";

    /// <summary>Design-spec 11.3's relocation sentence, defined once. The setup wizard's step 2
    /// shows the same note and reads this constant rather than repeating it, so the two surfaces
    /// cannot drift (Task 9 review, minor finding 1).</summary>
    public const string RelocationNoteText =
        "Changing this location does not move existing files. The new location fills up on demand and "
        + "the old one is left alone, because the application does not move user-visible files.";

    /// <summary>Design-spec 11.3, on screen rather than in a comment. The view binds this rather
    /// than repeating the sentence, so the note cannot be quietly deleted from one of them.
    /// </summary>
    public string RelocationNote { get; } = RelocationNoteText;

    /// <summary>Design-spec 11.3's cache key includes the width, so a width change needs no
    /// purge. On screen rather than in a comment.</summary>
    public string NoPurgeNote { get; } =
        "Changing the thumbnail width or the preview resolution changes the cache key, so nothing needs "
        + "purging: new images are written under new keys and the old ones age out.";

    /// <summary>Design-spec 5.8.1's <c>preview_cache_mb</c> note, on screen. FIXER item 20: the
    /// tab states the floor rather than accepting a value it will not honour.</summary>
    public string CacheFloorNote { get; } =
        "Values below 100 MB are treated as 100 MB: a smaller bound evicts each preview as it is written "
        + "and turns the preview modal into a render loop. Eviction applies to previews only; frame and "
        + "reference thumbnails are not evicted.";

    /// <summary>Design-spec 11.3's four preview resolutions, as an instance path a compiled
    /// binding can reach.</summary>
    public IReadOnlyList<PreviewResolutionOption> PreviewResolutionChoices => PreviewResolutions;

    /// <summary>What the tab says when a typed cache size was clamped on commit.</summary>
    public const string ClampedToFloorNote = "Raised to the 100 MB minimum.";

    /// <summary>What the tab says when a typed cache size was clamped to the maximum.</summary>
    public const string ClampedToCeilingNote = "Lowered to the 51200 MB maximum.";

    // ---- the data location (spec 17.2, Phase 10 Task 9) ---------------------------------------

    /// <summary>The data location must be an absolute path.</summary>
    public const string DataRootMustBeAbsolute = "The data location must be an absolute path.";

    /// <summary>The data location must not be an entire volume.</summary>
    public const string DataRootMustNotBeDriveRoot =
        "The data location must not be a bare drive or UNC share root.";

    /// <summary>The picked folder is the one already in use.</summary>
    public const string DataRootAlreadyCurrent = "That is already GalactiLog's data location.";

    /// <summary>The picked folder and the current one are nested.</summary>
    public const string DataRootNested =
        "The new location must not be inside the current one, and the current one must not be inside it.";

    /// <summary>Returned by the host, not by <see cref="ValidateDataRoot"/>: only the host may
    /// touch disk. Two catalogues are never merged (spec 17.2).</summary>
    public const string DataRootHasDatabase =
        "That folder already holds a GalactiLog database. Choose an empty folder, or start GalactiLog "
        + "with that folder as its data location instead.";

    /// <summary>Spec 17.2, on screen: why the data location is outside the folder the installer
    /// manages.</summary>
    public const string DataRootNoteText =
        "This is where GalactiLog keeps its database, settings, logs and thumbnail cache. It is outside "
        + "the folder the installer manages, so uninstalling GalactiLog does not remove it.";

    /// <summary>What a requested move says, with the destination and the current root.</summary>
    public const string PendingMoveNoteFormat =
        "GalactiLog will copy its data to {0} the next time it starts. Nothing is deleted from {1}.";

    /// <summary>What a completed move says about the folder that still holds a copy (ruling
    /// Q9.4: nothing is ever deleted).</summary>
    public const string PreviousRootNoteFormat =
        "A copy of the data is still at {0}. Nothing was deleted. Remove it yourself when you no longer "
        + "want it.";

    /// <summary>Where the data location came from, in the user's words. The wizard's storage step
    /// calls this one rather than spelling the four phrases a second time.</summary>
    public static string DataRootSourceLabel(AppDataRootSource source) => source switch
    {
        AppDataRootSource.Pointer => "Chosen location",
        AppDataRootSource.EnvironmentVariable => "Set by GALACTILOG_APPDATA",
        AppDataRootSource.ExplicitOverride => "Set for this test run",
        _ => "Default location",
    };

    private readonly Func<string, string?>? _requestDataRootMove;
    private readonly Action? _cancelDataRootMove;

    private readonly Func<string, (long Total, long Free)?> _volumeSpace;

    /// <param name="load">Normally <c>SettingsStore.GetGeneral</c>.</param>
    /// <param name="mutateGeneral">Normally <c>SettingsStore.MutateGeneral</c>.</param>
    /// <param name="volumeSpace">How the free-space readout is measured. Defaults to
    /// <see cref="ReadVolumeSpace"/>. Always invoked off the UI thread.</param>
    /// <param name="defaultCacheRoot">What an empty <c>thumbnail_cache_dir</c> resolves to, so the
    /// readout and the watermark can name the real folder. Normally
    /// <c>() =&gt; appWriter.ThumbnailCacheRoot</c>.</param>
    public StorageTabViewModel(
        Func<GeneralSettings> load,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> mutateGeneral,
        Func<string, (long Total, long Free)?>? volumeSpace = null,
        Func<string>? defaultCacheRoot = null,
        Action<Action>? post = null,
        ILogger? logger = null,
        Action<EventHandler<GeneralSettings>>? subscribeGeneralChanged = null,
        Action<EventHandler<GeneralSettings>>? unsubscribeGeneralChanged = null,
        Func<AppDataRootResolution>? dataRoot = null,
        Func<RelocationOutcome>? lastRelocation = null,
        Func<string, string?>? requestDataRootMove = null,
        Action? cancelDataRootMove = null)
        : base(load, mutateGeneral, post, logger, subscribeGeneralChanged, unsubscribeGeneralChanged)
    {
        _volumeSpace = volumeSpace ?? ReadVolumeSpace;
        _defaultCacheRoot = defaultCacheRoot;
        _requestDataRootMove = requestDataRootMove;
        _cancelDataRootMove = cancelDataRootMove;

        // Read once here: the resolved root cannot change while the process runs, so a Func<> on
        // the view would be a repeated read of a value that is already fixed.
        var resolution = dataRoot?.Invoke();
        var relocation = lastRelocation?.Invoke();
        HasDataLocation = resolution is not null;
        DataRootPath = resolution?.Root ?? "";
        DataRootSourceText = resolution is null ? "" : DataRootSourceLabel(resolution.Source);
        PendingDataRoot = resolution?.PendingRoot;
        // A failed relocation's From is the root still in use, so only a completed one leaves a
        // copy behind worth naming.
        var previousRoot = resolution?.PreviousRoot
            ?? (relocation is { Moved: true } ? relocation.From : null);
        PreviousDataRootNote = previousRoot is { Length: > 0 }
            ? string.Format(CultureInfo.InvariantCulture, PreviousRootNoteFormat, previousRoot)
            : null;
        // A pointer that exists and could not be read is shown here rather than only logged: the
        // application is running on the default root and the file the user would repair is named
        // in the warning (review finding I2).
        DataRootError = relocation?.Failure ?? resolution?.PointerWarning;

        ThumbnailCacheDir = "";
        PreviewCacheMbText = "";
        ThumbnailWidthText = "";
        SelectedPreviewResolution = PreviewResolutions[1];
        FreeSpaceText = "";

        Load();
    }

    private readonly Func<string>? _defaultCacheRoot;

    // ---- the data location section (spec 12.7, 17.2) ------------------------------------------

    /// <summary>Whether the tab was given the data location seams. False in a test that builds
    /// the tab with none of them, and the section is hidden.</summary>
    public bool HasDataLocation { get; }

    /// <summary>The resolved app data root. Read once in the constructor: it cannot change while
    /// the process runs.</summary>
    public string DataRootPath { get; }

    /// <summary>Where the root came from, in the user's words.</summary>
    public string DataRootSourceText { get; }

    /// <summary>Spec 17.2, on screen rather than in a comment.</summary>
    public string DataRootNote { get; } = DataRootNoteText;

    /// <summary>The destination of a requested move, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingMove))]
    [NotifyPropertyChangedFor(nameof(PendingMoveNote))]
    [NotifyCanExecuteChangedFor(nameof(CancelDataRootMoveCommand))]
    public partial string? PendingDataRoot { get; private set; }

    public bool HasPendingMove => PendingDataRoot is not null;

    /// <summary>What the pending move says, naming both paths.</summary>
    public string PendingMoveNote => string.Format(
        CultureInfo.InvariantCulture, PendingMoveNoteFormat, PendingDataRoot, DataRootPath);

    /// <summary>Names the folder a completed move left a copy in, or null. Nothing was deleted
    /// (ruling Q9.4).</summary>
    public string? PreviousDataRootNote { get; }

    /// <summary>The refusal for the last picked path, or the last relocation's failure.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDataRootError))]
    public partial string? DataRootError { get; private set; }

    public bool HasDataRootError => DataRootError is not null;

    /// <summary><c>general.thumbnail_cache_dir</c>. Empty means the default location under the
    /// app data root (design-spec 5.8.1).</summary>
    [ObservableProperty]
    public partial string ThumbnailCacheDir { get; set; }

    /// <summary>The inline path message, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCacheDirError))]
    public partial string? CacheDirError { get; private set; }

    public bool HasCacheDirError => CacheDirError is not null;

    /// <summary>The free-space line for whatever volume the cache path lives on, or
    /// <see cref="FreeSpaceUnavailable"/>.</summary>
    [ObservableProperty]
    public partial string FreeSpaceText { get; private set; }

    /// <summary><c>general.preview_resolution</c> (design-spec 11.3). 0 is native.</summary>
    [ObservableProperty]
    public partial PreviewResolutionOption SelectedPreviewResolution { get; set; }

    /// <summary><c>general.preview_cache_mb</c> as typed. Committed on blur or Enter and clamped
    /// to 100 to 51200, which is what the web's own input does.</summary>
    [ObservableProperty]
    public partial string PreviewCacheMbText { get; set; }

    /// <summary>Set when the last commit clamped the typed value, so the clamp is visible rather
    /// than silent (FIXER item 20).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCacheMbNote))]
    public partial string? CacheMbNote { get; private set; }

    public bool HasCacheMbNote => CacheMbNote is not null;

    /// <summary><c>general.thumbnail_width</c> (design-spec 11.3), default 800.</summary>
    [ObservableProperty]
    public partial string ThumbnailWidthText { get; set; }

    /// <summary>The inline thumbnail width message, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnailWidthError))]
    public partial string? ThumbnailWidthError { get; private set; }

    public bool HasThumbnailWidthError => ThumbnailWidthError is not null;

    /// <summary>The in-flight free-space read, so a test awaits it instead of blocking on it.
    /// </summary>
    internal Task? PendingFreeSpace { get; private set; }

    /// <summary>
    /// A volume's total and available bytes, or null when the volume cannot be read. Read-only:
    /// <c>DriveInfo</c> has no write members and nothing here creates the directory.
    /// </summary>
    /// <remarks>Call it off the UI thread: a disconnected network path can block inside
    /// <c>DriveInfo</c> for seconds. Task 9's wizard calls this one rather than writing a second
    /// free-space read.</remarks>
    public static (long Total, long Free)? ReadVolumeSpace(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || Path.GetPathRoot(path) is not { Length: > 0 } root)
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.TotalSize, drive.AvailableFreeSpace) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The free-space line for a volume, or <see cref="FreeSpaceUnavailable"/> for a volume that
    /// could not be read. Formatted through <c>MetricText.Bytes</c>, which is the application's
    /// one byte formatter, so a figure here and the same figure on the Statistics storage card
    /// cannot disagree.
    /// </summary>
    public static string FormatVolumeSpace((long Total, long Free)? space)
        => space is not { } value
            ? FreeSpaceUnavailable
            : $"{MetricText.Bytes(value.Free)} free of {MetricText.Bytes(value.Total)}";

    /// <summary>
    /// Design-spec 5.8.1's <c>thumbnail_cache_dir</c> rule, as one function: empty is legal and
    /// means the default location, anything else must be an absolute path that is not a bare drive
    /// or UNC share root. Returns null when the path is acceptable.
    /// </summary>
    /// <remarks>These are the two refusals <c>SettingsStore.ValidateGeneral</c> already enforces,
    /// mirrored so the user sees which one before the save is refused. The store stays the
    /// enforcement point (design-lessons rule 2). Task 9's wizard calls this one.</remarks>
    public static string? ValidateCacheDir(string? path)
    {
        var candidate = path?.Trim() ?? "";
        if (candidate.Length == 0)
        {
            return null;
        }

        if (!Path.IsPathRooted(candidate))
        {
            return "The cache location must be empty or an absolute path.";
        }

        try
        {
            if (AppWriter.IsDriveOrShareRoot(Path.GetFullPath(candidate)))
            {
                return "The cache location must not be a bare drive or UNC share root.";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return $"'{candidate}' is not a valid path: {ex.Message}";
        }

        return null;
    }

    /// <summary>
    /// The data location rules: an absolute path that is not a bare drive or UNC share root, not
    /// the current root, not inside it, and not a folder the current root is inside. Null when the
    /// path is acceptable.
    /// </summary>
    /// <remarks>Pure string and <c>Path</c> work, so the wizard calls this one rather than writing
    /// a second set of rules. The one rule that needs disk, a destination that already holds a
    /// database, belongs to the host and returns <see cref="DataRootHasDatabase"/>.</remarks>
    public static string? ValidateDataRoot(string? path, string currentRoot)
    {
        var candidate = path?.Trim() ?? "";
        if (candidate.Length == 0 || !Path.IsPathFullyQualified(candidate))
        {
            return DataRootMustBeAbsolute;
        }

        string full;
        try
        {
            full = Path.GetFullPath(candidate);
            if (AppWriter.IsDriveOrShareRoot(full))
            {
                return DataRootMustNotBeDriveRoot;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return $"'{candidate}' is not a valid path: {ex.Message}";
        }

        if (currentRoot.Trim().Length == 0)
        {
            return null;
        }

        string current;
        try
        {
            current = Path.GetFullPath(currentRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        if (string.Equals(
                Path.TrimEndingDirectorySeparator(full),
                Path.TrimEndingDirectorySeparator(current),
                StringComparison.OrdinalIgnoreCase))
        {
            return DataRootAlreadyCurrent;
        }

        return PathConfinement.IsUnderOrEqual(current, full) || PathConfinement.IsUnderOrEqual(full, current)
            ? DataRootNested
            : null;
    }

    /// <summary>Design-spec 5.8.1's <c>preview_cache_mb</c> clamp, which is the web's
    /// <c>Math.min(51200, Math.max(100, parsed))</c>. Null for a blank or non-numeric value, which
    /// the caller treats as "revert the field".</summary>
    public static int? ClampCacheMb(string? text)
        => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Min(MaximumCacheMb, Math.Max(MinimumCacheMb, parsed))
            : null;

    /// <summary>Sets the cache path from the view's folder picker. The picker returns a path and
    /// nothing else happens to it: no enumeration, no creation, no move.</summary>
    public void SetThumbnailCacheDir(string path) => ThumbnailCacheDir = path;

    /// <summary>
    /// Records the data location the view's folder picker returned. It moves nothing and writes
    /// nothing else: the copy happens on the next start (spec 17.2).
    /// </summary>
    public void SetDataRoot(string path)
    {
        var refusal = ValidateDataRoot(path, DataRootPath);
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

    /// <inheritdoc />
    protected override void ApplyDocument(GeneralSettings general)
    {
        var previousPath = ThumbnailCacheDir;
        ThumbnailCacheDir = general.ThumbnailCacheDir;
        SelectedPreviewResolution =
            PreviewResolutions.FirstOrDefault(option => option.Pixels == general.PreviewResolution)
            ?? PreviewResolutions[1];
        PreviewCacheMbText = general.PreviewCacheMb.ToString(CultureInfo.InvariantCulture);
        ThumbnailWidthText = general.ThumbnailWidth.ToString(CultureInfo.InvariantCulture);

        // The inline errors are not cleared here: each field's own change handler recomputes its
        // message from the value being seeded, so a hand-edited document holding an invalid path
        // or width shows why rather than looking accepted.
        CacheMbNote = null;

        // Exactly one volume probe per publish. Assigning the path above already triggered one
        // when the value changed; an unchanged value raises nothing, and the readout still has to
        // be right on first show.
        if (string.Equals(previousPath, general.ThumbnailCacheDir, StringComparison.Ordinal))
        {
            RefreshFreeSpace(general.ThumbnailCacheDir);
        }
    }

    // ---- the immediate saves ------------------------------------------------------------------

    partial void OnThumbnailCacheDirChanged(string value)
    {
        var trimmed = value.Trim();
        CacheDirError = ValidateCacheDir(trimmed);

        // Not while the constructor is seeding the control, before the document has been read:
        // that probe would be of the default root and the first publish would replace it.
        if (IsApplying || IsReadyToSave)
        {
            RefreshFreeSpace(trimmed);
        }

        if (CacheDirError is not null)
        {
            return;
        }

        var previous = Saved.ThumbnailCacheDir;
        ImmediateSave(
            general => general with { ThumbnailCacheDir = trimmed },
            // Design-spec 11.3, said out loud at the moment it becomes true.
            "Cache location saved. Existing files were not moved.",
            () => ThumbnailCacheDir = previous);
    }

    partial void OnSelectedPreviewResolutionChanged(PreviewResolutionOption oldValue, PreviewResolutionOption newValue)
    {
        var pixels = newValue.Pixels;
        ImmediateSave(
            general => general with { PreviewResolution = pixels },
            pixels == 0 ? "Preview resolution set to native" : $"Preview resolution set to {pixels} px",
            () => SelectedPreviewResolution = oldValue);
    }

    partial void OnPreviewCacheMbTextChanged(string value) => CommitCacheMb(value);

    // The web commits this input on blur or Enter; the view binds with UpdateSourceTrigger
    // LostFocus plus an Enter key binding, so a property change here is already a commit.
    private void CommitCacheMb(string value)
    {
        // A publish seeding the field, a revert, or the clamp below writing the corrected number
        // back are all writes this method made or the base class made; only a user commit gets
        // past here. Without this guard the clamp's own write-back re-enters and clears the note
        // it had just set.
        if (IsApplying)
        {
            return;
        }

        var previous = Saved.PreviewCacheMb;
        var clamped = ClampCacheMb(value);
        if (clamped is null)
        {
            // Blank or not a number: the field reverts, exactly as the web's input does.
            CacheMbNote = null;
            Apply(() => PreviewCacheMbText = previous.ToString(CultureInfo.InvariantCulture));
            return;
        }

        var typed = int.Parse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
        CacheMbNote = typed < MinimumCacheMb
            ? ClampedToFloorNote
            : typed > MaximumCacheMb ? ClampedToCeilingNote : null;

        if (clamped.Value != typed)
        {
            Apply(() => PreviewCacheMbText = clamped.Value.ToString(CultureInfo.InvariantCulture));
        }

        var value1 = clamped.Value;
        ImmediateSave(
            general => general with { PreviewCacheMb = value1 },
            $"Preview cache limit set to {value1} MB",
            () => PreviewCacheMbText = previous.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnThumbnailWidthTextChanged(string value)
    {
        var previous = Saved.ThumbnailWidth;
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            ThumbnailWidthError = "Enter a whole number of pixels.";
            return;
        }

        if (parsed <= 0)
        {
            ThumbnailWidthError = "The thumbnail width must be greater than zero.";
            return;
        }

        ThumbnailWidthError = null;
        ImmediateSave(
            general => general with { ThumbnailWidth = parsed },
            // Design-spec 11.3: the key includes the width, so nothing is invalidated.
            $"Thumbnail width set to {parsed} px. No purge is needed.",
            () => ThumbnailWidthText = previous.ToString(CultureInfo.InvariantCulture));
    }

    // ---- the free-space readout ---------------------------------------------------------------

    // Off the UI thread, always: a disconnected network path can block inside DriveInfo. The read
    // is a pure query and a volume that cannot be read degrades to one line rather than throwing.
    private void RefreshFreeSpace(string path)
    {
        var target = path.Trim().Length > 0 ? path.Trim() : _defaultCacheRoot?.Invoke() ?? "";
        if (target.Length == 0)
        {
            FreeSpaceText = FreeSpaceUnavailable;
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
                    // A volumeSpace delegate that throws is still a "cannot be read" answer, not a
                    // crash on the UI thread.
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
                        FreeSpaceText = FormatVolumeSpace(space);
                    }
                });
            },
            token);
    }
}
