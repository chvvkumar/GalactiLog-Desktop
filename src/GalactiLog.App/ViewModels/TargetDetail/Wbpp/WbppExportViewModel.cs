using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.App.Services;
using GalactiLog.Core.Io;
using GalactiLog.Core.Settings;
using GalactiLog.Core.Wbpp;
using GalactiLog.Data.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GalactiLog.App.ViewModels.TargetDetail.Wbpp;

/// <summary>
/// Spec 12.13's Export for stacking state, shared by every step of
/// <see cref="WbppExportWizardViewModel"/>: the level trees of the checked nights, a host slot for
/// the quality panel, the staging folder, the totals, the script Generate and the in-app copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>The application copies frames only into a staging folder the user picked in the wizard,
/// creates new files only, and never overwrites, deletes, moves or renames anything on either
/// side.</b> <see cref="RunCopyAsync"/> is the one caller of <c>AppWriter.BeginStagingCopy</c>
/// outside <c>AppWriter.cs</c>; the other write is <c>AppWriter.BeginExport(path).WriteAllText</c>
/// at a path a save dialog returned (spec 2.1.1). Nothing here writes under a scan root.
/// </para>
/// <para>
/// Every collaborator is a delegate (design-spec 18.3): the page reaches no store, no repository
/// and no window. The two platform dialogs are settable seams, null in a headless test, which is
/// what makes Generate a no-op there rather than a null reference.
/// </para>
/// <para>
/// <b>No <c>ConfigureAwait(false)</c> anywhere in this type.</b> Generate publishes the script
/// text, the Script part's visibility and the footer after its awaits, and a continuation off the
/// UI thread reaching an observable write or a <c>CanExecute</c> notification is the Phase 14B
/// Clear log crash: "Call from invalid thread" through <c>Button.get_Command</c>, which kills the
/// process with nothing logged and which the headless harness does not reproduce.
/// </para>
/// </remarks>
public sealed partial class WbppExportViewModel : ObservableObject, IDisposable
{
    /// <summary>Spec 12.13's refusal for a staging folder that equals, lies under or contains a
    /// configured scan root.</summary>
    public const string ScanRootRefusal =
        "That folder is inside your library, so the next scan would catalogue the staged copy as a "
        + "second set of frames. Choose a folder outside every library folder.";

    /// <summary>Spec 12.13's refusal for a staging folder that equals, lies under or contains one
    /// of this export's chosen source folders.</summary>
    public const string SourceRefusal =
        "That folder is one of the folders this export copies from, so the copy would write into "
        + "its own source. Choose a folder outside every library folder.";

    /// <summary>Spec 12.13's empty-export warning, shown when the filter leaves no frame at
    /// all.</summary>
    public const string EmptyExportWarning =
        "The quality filter excludes every frame in the selected folders, so this export would copy "
        + "nothing. Loosen it below, or switch it off to copy everything.";

    /// <summary>
    /// The second sentence of spec 12.13's empty-export paragraph, for a selection whose levels
    /// hold no frames at all. Composed in the port's own plain words because the spec states the
    /// rule ("is not blamed on the filter and says so instead") and not the sentence.
    /// </summary>
    public const string NoFramesAtAllWarning =
        "The selected folders hold no frames, so this export would copy nothing.";

    /// <summary>Spec 12.13's refusal for an exclusion pattern carrying a character a quoted
    /// literal cannot carry safely.</summary>
    public const string PatternRefusal =
        "A folder pattern cannot contain a quote, a dollar sign, a backtick or a line break";

    /// <summary>Spec 12.13's reason on a disabled Generate while no staging folder is set.
    /// </summary>
    public const string NoStagingReason = "Choose a staging folder first";

    /// <summary>
    /// What a failed write states. Spec 12.13 gives no sentence for it, so this is composed in the
    /// port's own plain words and recorded in the task report.
    /// </summary>
    /// <remarks><c>File.WriteAllText</c> throws on a read-only folder, a full disk, a locked file
    /// or a removed removable drive, and this page states a sentence for every other refusal
    /// rather than letting one escape the command.</remarks>
    public const string WriteFailure =
        "The script could not be written to that location. It may be read-only, in use, or on a "
        + "drive that is no longer available. Choose another location and generate again.";

    /// <summary>Spec 12.13's one sentence under the footer's figures, which says why the footer
    /// and the panel's tally describe different sets.</summary>
    public const string FooterNote =
        "Frames counts this target's light frames. The size counts every catalogued file in the "
        + "chosen folders; sidecars and files GalactiLog never read are copied too and are not counted.";

    /// <summary>Where spec 12.13's long-path warning starts. Windows refuses a path at or past
    /// this length unless long paths are enabled.</summary>
    public const int LongPathThreshold = 260;

    /// <summary>How long Save as defaults confirms for, the web's "Saved!" figure.</summary>
    public static readonly TimeSpan SavedConfirmationDefault = TimeSpan.FromSeconds(1);

    private readonly string _groupKey;
    private readonly string _targetName;
    private readonly IReadOnlyList<DateOnly> _nights;
    private readonly Func<string, IReadOnlyList<DateOnly>, IReadOnlyList<string>, CancellationToken,
        WbppPaths> _readPaths;
    private readonly Func<string, DateOnly, SessionDetail?> _getDetail;
    private readonly Func<GeneralSettings> _getGeneral;
    private readonly Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> _mutateGeneral;
    private readonly AppWriter _appWriter;
    private readonly Func<string, Task> _copyText;
    private readonly Func<IReadOnlyList<WbppFrame>, IReadOnlyDictionary<Guid, FrameGrading>, string,
        Action<IReadOnlyCollection<Guid>>, object>? _createQualityPanel;
    private readonly Action<Action> _post;
    private readonly ILogger _logger;

    // The night's frames as the query returned them, which is what an excluded image id is
    // resolved against. The catalogue itself is never held: the query returns the built index
    // (seam-review ruling 6) and a 200,000 frame library never materialises here.
    private IReadOnlyDictionary<DateOnly, IReadOnlyList<WbppFramePath>> _framesByNight =
        new Dictionary<DateOnly, IReadOnlyList<WbppFramePath>>();

    private ContaminationIndex? _catalogue;
    private HashSet<Guid> _excludedImageIds = [];

    // The staging folder as it was last accepted, which is the value Generate uses. Null while it
    // is unset or while the typed one stands refused, which is what keeps Generate disabled.
    private string? _committedStaging;

    // The value the general document holds, so a commit that does not change it writes nothing.
    private string? _storedStaging;

    // Cancelled when the window closes. WbppPathsQuery.Read is a synchronous read that takes on
    // the order of 1.7 seconds on a 200,000 row library, so the page can be dismissed while the
    // first read is still in flight and a publish after that would raise property changes on a
    // page nothing is bound to.
    private readonly CancellationTokenSource _closing = new();

    private bool _suppressExclusionCheck;
    private bool _disposed;

    /// <param name="groupKey">The page's group key, which keys both queries.</param>
    /// <param name="targetName">The target's primary name, for the header, the script header and
    /// the suggested file name.</param>
    /// <param name="nights">The checked nights in the ledger's own order, newest first, exactly as
    /// <c>TargetDetailViewModel.SelectedNights</c> holds them. This page grows no selection of its
    /// own (W9).</param>
    /// <param name="readPaths">Normally <c>WbppPathsQuery.Read</c>. Called once, off the UI
    /// thread, per page open (W3).</param>
    /// <param name="getDetail">Normally <c>SessionDetailQuery.Get</c>. Called off the UI thread,
    /// once per checked night, for the frames the quality panel judges.</param>
    /// <param name="getGeneral">Normally <c>SettingsStore.GetGeneral</c>. Read again at every
    /// staging commit, so a scan root added while the page is open is tested against.</param>
    /// <param name="mutateGeneral">Normally <c>SettingsStore.MutateGeneral</c>. Every write of the
    /// general document is one call of this (W11).</param>
    /// <param name="appWriter">The application's one <c>AppWriter</c>. This is the file that names
    /// <c>BeginExport</c> (W4, the user's explicit yes on 2026-09-20).</param>
    /// <param name="copyText">Normally <c>ShellIntegration.CopyTextAsync</c>, for Copy
    /// script.</param>
    /// <param name="createQualityPanel">The quality panel's factory. Null leaves the slot empty,
    /// which means every frame is included and is what a build without Task 3b shows.</param>
    /// <param name="post">How to reach the UI thread. Defaults to <c>UiPost.Default</c>.</param>
    /// <param name="logger">Optional. A night that failed to load is logged and counted, never
    /// rethrown on the UI thread.</param>
    public WbppExportViewModel(
        string groupKey,
        string targetName,
        IReadOnlyList<DateOnly> nights,
        Func<string, IReadOnlyList<DateOnly>, IReadOnlyList<string>, CancellationToken, WbppPaths> readPaths,
        Func<string, DateOnly, SessionDetail?> getDetail,
        Func<GeneralSettings> getGeneral,
        Func<Func<GeneralSettings, GeneralSettings>, GeneralSettings> mutateGeneral,
        AppWriter appWriter,
        Func<string, Task> copyText,
        Func<IReadOnlyList<WbppFrame>, IReadOnlyDictionary<Guid, FrameGrading>, string,
            Action<IReadOnlyCollection<Guid>>, object>? createQualityPanel = null,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(nights);
        ArgumentNullException.ThrowIfNull(readPaths);
        ArgumentNullException.ThrowIfNull(getDetail);
        ArgumentNullException.ThrowIfNull(getGeneral);
        ArgumentNullException.ThrowIfNull(mutateGeneral);
        ArgumentNullException.ThrowIfNull(appWriter);
        ArgumentNullException.ThrowIfNull(copyText);

        _groupKey = groupKey;
        _targetName = targetName;
        _nights = nights;
        _readPaths = readPaths;
        _getDetail = getDetail;
        _getGeneral = getGeneral;
        _mutateGeneral = mutateGeneral;
        _appWriter = appWriter;
        _copyText = copyText;
        _createQualityPanel = createQualityPanel;
        _post = post ?? UiPost.Default;
        _logger = logger ?? NullLogger.Instance;

        var general = _getGeneral();

        // The four wbpp keys are read through Task 3a's tolerant readers and never parsed here:
        // no JsonElement walk, no TryGetProperty, no comparison against a stored literal
        // (seam-review ruling 7).
        // The stored value is read here and COMMITTED BY NOTHING until the first RecheckStaging,
        // which Publish runs: a stored folder that has since come to breach the confinement rule
        // would otherwise leave Generate available from construction until the first read returns,
        // which on the library W3 sizes the design for is a real window.
        _storedStaging = Normalise(WbppSettingsRead.StagingPath(general.WbppStagingPathDocument));
        _stagingPath = _storedStaging ?? "";
        _stagingFolderText = _storedStaging ?? "";
        _exclusionsText = string.Join(
            Environment.NewLine,
            WbppSettingsRead.Exclusions(general.WbppExclusionsDocument));
        DefaultScriptType = ScriptGenerator.ParseOs(
            WbppSettingsRead.DefaultOs(general.WbppDefaultOsDocument));

        // Spec 12.13: the disclosure is closed on open except while the staging folder is unset,
        // when it opens itself so the field is on screen rather than folded away.
        _isSettingsOpen = _storedStaging is null;

        RefreshFooter();
        Load();
    }

    // ------------------------------------------------------------------ the header and the load

    /// <summary>The target's primary name.</summary>
    public string TargetName => _targetName;

    /// <summary>How many nights were checked.</summary>
    public int NightCount => _nights.Count;

    /// <summary>The first read, exposed so a case settles on it rather than on a timer. Null only
    /// before the constructor has started it.</summary>
    public Task? PendingLoad { get; private set; }

    /// <summary>Whether the levels are still being computed. Spec 12.13's Opening state: the level
    /// rows are listed with a loading line and the footer states no totals.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SublineText))]
    private bool _isLoading = true;

    /// <summary>Spec 12.13's header subline: the target, the checked night count and the total
    /// LIGHT frame count across those nights.</summary>
    public string SublineText
    {
        get
        {
            var nights = _nights.Count == 1 ? "1 night" : string.Create(
                CultureInfo.InvariantCulture, $"{_nights.Count:N0} nights");
            return IsLoading
                ? _targetName + ", " + nights
                : _targetName + ", " + nights + ", " + WbppPathText.Frames(TotalFrameCount);
        }
    }

    /// <summary>Every checked night's row, in the ledger's own order.</summary>
    [ObservableProperty]
    private IReadOnlyList<SessionLevelViewModel> _sessions = [];

    /// <summary>The total LIGHT frame count across the checked nights, which is the subline's
    /// figure.</summary>
    public int TotalFrameCount { get; private set; }

    // One pass, off the UI thread, started from the constructor. The query is a synchronous SQLite
    // read and this page is constructed on the UI thread from a command.
    private void Load()
    {
        var groupKey = _groupKey;
        var nights = _nights;
        var scanRoots = _getGeneral().ScanRoots;
        var token = _closing.Token;

        // Off the UI thread, always: the path query is a synchronous SQLite read measured at
        // around 1.7 seconds on a 200,000 row library, so running it in the constructor or inside
        // a command body would freeze the window for that long.
        PendingLoad = Task.Run(
            () =>
        {
            var sessions = new List<SessionLevels>();
            var frames = new List<WbppFrame>();
            var gradings = new Dictionary<Guid, FrameGrading>();
            IReadOnlyDictionary<DateOnly, IReadOnlyList<WbppFramePath>> byNight =
                new Dictionary<DateOnly, IReadOnlyList<WbppFramePath>>();
            ContaminationIndex? catalogue = null;

            try
            {
                // One read per page open (W3), over every requested night at once. The result
                // carries the built index and no catalogue row.
                var paths = _readPaths(groupKey, nights, scanRoots, token);
                byNight = paths.Nights;
                catalogue = paths.Catalogue;

                foreach (var night in nights)
                {
                    var nightFrames = byNight.TryGetValue(night, out var list) ? list : [];
                    sessions.Add(FolderLevels.ForSession(night, groupKey, nightFrames, paths.Catalogue));
                }
            }
            catch (OperationCanceledException)
            {
                // The window closed while the read was still in flight. A silent close, never an
                // error sentence: there is no page left to state one on.
                return;
            }
            catch (Exception ex)
            {
                // A read that failed leaves the page readable and empty rather than taking the
                // window down. Never rethrown on the UI thread.
                _logger.LogWarning(ex, "Reading the export paths for {Target} failed", groupKey);
            }

            foreach (var night in nights)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                SessionDetail? detail;
                try
                {
                    detail = _getDetail(groupKey, night);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reading {Date} for the export page failed", night);
                    detail = null;
                }

                if (detail is null)
                {
                    continue;
                }

                var light = byNight.TryGetValue(night, out var nightFrames) ? nightFrames : [];
                foreach (var frame in LightFramesOnly(night, detail, light, gradings))
                {
                    frames.Add(frame);
                }
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            _post(() => Publish(sessions, byNight, catalogue, frames, gradings));
        },
            token);
    }

    /// <summary>
    /// The night's LIGHT frames, projected onto <see cref="WbppFrame"/> values, with their grades
    /// collected as they pass.
    /// </summary>
    /// <remarks>
    /// <b>This is the one place spec 12.13's "Filters apply to light frames only" is enforced.</b>
    /// <see cref="WbppFrame"/> carries no frame type and <c>QualityFilter.Evaluate</c> judges
    /// whatever it is handed, so the gate cannot live downstream of here: a non-LIGHT row that got
    /// past this method would be judged, tallied, excludable and deductible from the footer. The
    /// test is membership of the night's own LIGHT set, which <c>WbppPathsQuery</c> selected with
    /// <c>SqlFragments.LightFrameOnly</c> and which the levels themselves were built from, so the
    /// panel, the per-file excludes and the level tree all describe one set of frames.
    /// </remarks>
    /// <param name="night">The night these rows belong to.</param>
    /// <param name="detail">The night's detail, as the session query returned it.</param>
    /// <param name="lightFrames">That night's LIGHT frames from the path query, which is the
    /// admission list.</param>
    /// <param name="gradings">Filled with the grade of every admitted frame that carries
    /// one.</param>
    private static List<WbppFrame> LightFramesOnly(
        DateOnly night,
        SessionDetail detail,
        IReadOnlyList<WbppFramePath> lightFrames,
        Dictionary<Guid, FrameGrading> gradings)
    {
        var admitted = new HashSet<Guid>(lightFrames.Select(frame => frame.ImageId));
        var frames = new List<WbppFrame>(detail.Frames.Count);
        foreach (var row in detail.Frames)
        {
            if (!admitted.Contains(row.ImageId))
            {
                continue;
            }

            frames.Add(ToFrame(night, row));
            if (row.Grading is { } grading)
            {
                gradings[row.ImageId] = grading;
            }
        }

        return frames;
    }

    // FrameRow to WbppFrame, member for member. Rig is copied through unchanged (seam-review
    // ruling 1): SessionDetailQuery composes the port's one rig label and nothing here composes a
    // second spelling of it.
    private static WbppFrame ToFrame(DateOnly night, FrameRow row) => new(
        row.ImageId,
        night,
        row.FilePath,
        row.FileName,
        row.CaptureDate,
        row.FilterUsed,
        row.Rig,
        null,
        null,
        row.MedianHfr,
        row.Eccentricity,
        row.Fwhm,
        row.DetectedStars,
        row.GuidingRmsArcsec);

    private void Publish(
        IReadOnlyList<SessionLevels> sessions,
        IReadOnlyDictionary<DateOnly, IReadOnlyList<WbppFramePath>> byNight,
        ContaminationIndex? catalogue,
        IReadOnlyList<WbppFrame> frames,
        IReadOnlyDictionary<Guid, FrameGrading> gradings)
    {
        if (_disposed)
        {
            return;
        }

        _framesByNight = byNight;
        _catalogue = catalogue;
        Frames = frames;
        Gradings = gradings;

        // The rig key is the Rig of the first frame of the selection in ledger order, resolved
        // before the panel is built so there is no transition to arbitrate. Unknown / Unknown is a
        // legitimate key; the default slot is reached only by a selection with no frame at all.
        RigKey = frames.Count > 0 ? frames[0].Rig : WbppQualityByRig.DefaultRigKey;

        Sessions =
            [.. sessions.Select(levels => new SessionLevelViewModel(levels, _targetName, OnLevelChanged))];
        TotalFrameCount = sessions.Sum(levels => levels.TotalFrameCount);
        IsLoading = false;

        RefreshStagingNames();
        RecheckStaging(StagingPath);
        RefreshFooter();

        // The slot is filled last, so the panel is built over a page whose levels and rig key are
        // already settled. Null leaves it empty, which is the correct reading of "no filter".
        if (_createQualityPanel is { } create)
        {
            QualityPanel = create(Frames, Gradings, RigKey, SetExcludedFrames);
        }
    }

    // ------------------------------------------------------------------ the quality panel's slot

    /// <summary>The quality panel, or null while nothing has been built into the slot.</summary>
    [ObservableProperty]
    private object? _qualityPanel;

    /// <summary>Every LIGHT frame of the checked nights, in ledger order then capture order.
    /// </summary>
    public IReadOnlyList<WbppFrame> Frames { get; private set; } = [];

    /// <summary>The grades the session query already computed, keyed by image id. A frame the
    /// grading could not judge carries no entry.</summary>
    public IReadOnlyDictionary<Guid, FrameGrading> Gradings { get; private set; } =
        new Dictionary<Guid, FrameGrading>();

    /// <summary>The rig key this page's filter is stored under: the <c>Rig</c> of the first frame
    /// of the selection in ledger order, and the literal <c>default</c> only when the selection
    /// holds no frame at all.</summary>
    public string RigKey { get; private set; } = WbppQualityByRig.DefaultRigKey;

    /// <summary>
    /// Takes the excluded image ids from the panel and refreshes the totals, the empty warning,
    /// the long-path figure and the script withdrawal. Called on the UI thread.
    /// </summary>
    /// <param name="excludedImageIds">The frames the filter and its overrides leave out. An empty
    /// collection is "nothing excluded", which is what a page with no panel holds throughout.
    /// </param>
    public void SetExcludedFrames(IReadOnlyCollection<Guid> excludedImageIds)
    {
        ArgumentNullException.ThrowIfNull(excludedImageIds);

        var next = new HashSet<Guid>(excludedImageIds);
        if (next.SetEquals(_excludedImageIds))
        {
            // Spec 12.13: a re-sort and a baseline toggle are not changes and withdraw nothing.
            // Neither moves the excluded set, so the page tells them apart by the set itself
            // rather than by a flag the panel would have to remember to raise.
            return;
        }

        _excludedImageIds = next;
        WithdrawScript();
        RefreshFooter();
    }

    // ------------------------------------------------------------------ the levels and the totals

    private void OnLevelChanged(SessionLevelViewModel session)
    {
        _ = session;
        RefreshStagingNames();

        // A level change moves the source set the staging rule's second clause tests against, so
        // the check is re-run: a folder that was legal under one pick can become illegal under a
        // shallower one.
        RecheckStaging(StagingPath);
        WithdrawScript();
        RefreshFooter();
    }

    private IReadOnlyList<ChosenLevel> ChosenLevels() =>
        [.. Sessions.Select(session => session.Chosen).OfType<ChosenLevel>()];

    private IReadOnlyDictionary<DateOnly, IReadOnlyList<WbppFramePath>> ExcludedByNight()
    {
        var excluded = new Dictionary<DateOnly, IReadOnlyList<WbppFramePath>>();
        if (_excludedImageIds.Count == 0)
        {
            return excluded;
        }

        foreach (var (night, frames) in _framesByNight)
        {
            var dropped = frames.Where(frame => _excludedImageIds.Contains(frame.ImageId)).ToList();
            if (dropped.Count > 0)
            {
                excluded[night] = dropped;
            }
        }

        return excluded;
    }

    private void RefreshStagingNames()
    {
        var chosen = ChosenLevels();
        if (chosen.Count == 0)
        {
            // Nothing to name, so nothing states a rename: the names are cleared rather than left
            // standing at whatever the last refresh set (review P3-9). Latent today, because the
            // levels are fixed at load and a night with levels always has a pick.
            foreach (var session in Sessions)
            {
                session.SetStagingEntryName("");
            }

            return;
        }

        // The names are Task 2's, including a date prefix and a _2 suffix. Nothing here re-derives,
        // re-cases or re-suffixes one: each row is told its own name and decides only whether to
        // state it.
        var names = FolderLevels.StagingNames(chosen);
        var index = 0;
        foreach (var session in Sessions)
        {
            if (session.Chosen is null)
            {
                continue;
            }

            session.SetStagingEntryName(names[index]);
            index++;
        }
    }

    // ------------------------------------------------------------------ the footer

    /// <summary>The copy's LIGHT frame count, or empty while the levels are still being computed.
    /// The footer's figures read nothing, not zero, until then.</summary>
    [ObservableProperty]
    private string _frameCountText = "";

    /// <summary>How many folders the copy writes. A night whose levels are unavailable is excluded
    /// from it.</summary>
    [ObservableProperty]
    private string _folderCountText = "";

    /// <summary>The copy's byte total, or the literal <c>unknown</c> when any contributing size is
    /// unknown. All or nothing, with the filter on and with it off.</summary>
    [ObservableProperty]
    private string _sizeText = "";

    /// <summary>The staging folder on its own line under the figures.</summary>
    [ObservableProperty]
    private string _stagingFolderText = "";

    /// <summary>Spec 12.13's own sentence under the figures.</summary>
    public string FooterNoteText => FooterNote;

    /// <summary>The totals themselves, exposed so a case asserts the figures rather than parsing
    /// the strings. Null while the levels are still being computed.</summary>
    public ExportTotals? Totals { get; private set; }

    /// <summary>Spec 12.13's empty-export warning, or the no-frames sentence beside it, and empty
    /// when neither applies.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyExportWarning))]
    private string _emptyExportText = "";

    /// <summary>Whether the empty-export callout is on screen.</summary>
    public bool HasEmptyExportWarning => EmptyExportText.Length > 0;

    /// <summary>Spec 12.13's long-path warning with the measured length substituted, and empty
    /// below the ceiling.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLongPathWarning))]
    private string _longPathText = "";

    /// <summary>Whether the long-path warning is on screen. It never disables Generate.</summary>
    public bool HasLongPathWarning => LongPathText.Length > 0;

    /// <summary>The longest destination path this export would write, or 0 while it is not
    /// measurable. Task 2's arithmetic, never computed here.</summary>
    public int LongestDestinationLength { get; private set; }

    private void RefreshFooter()
    {
        if (IsLoading)
        {
            FrameCountText = "";
            FolderCountText = "";
            SizeText = "";
            return;
        }

        var chosen = ChosenLevels();
        var excludedUnder = FolderLevels.ExcludedUnderLevels(chosen, ExcludedByNight());
        var totals = FolderLevels.Totals(chosen, excludedUnder);
        Totals = totals;

        FrameCountText = WbppPathText.Frames(totals.FrameCount);
        FolderCountText = totals.FolderCount == 1
            ? "1 folder"
            : string.Create(CultureInfo.InvariantCulture, $"{totals.FolderCount:N0} folders");
        SizeText = WbppPathText.Bytes(totals.SizeBytes);
        StagingFolderText = _committedStaging ?? "";

        // Spec 12.13: the filter is blamed only when the filter is what emptied the export. A
        // selection whose levels hold no frames at all says so instead, because telling a user to
        // loosen a filter that excluded nothing sends them to the wrong control.
        var held = chosen.Sum(level => level.Level.FrameCount);
        EmptyExportText = held <= 0
            ? NoFramesAtAllWarning
            : totals.FrameCount <= 0 ? EmptyExportWarning : "";

        RefreshLongPath(chosen);
        GenerateCommand.NotifyCanExecuteChanged();
    }

    // Recomputed on a level change and on a staging folder change, because both move the figure,
    // and on nothing else. A figure computed once at load shows a clean page to a user who then
    // picks the level that breaks their copy.
    private void RefreshLongPath(IReadOnlyList<ChosenLevel> chosen)
    {
        LongestDestinationLength = 0;
        LongPathText = "";

        if (_catalogue is not { } catalogue || CopyDestination is not { } staging || chosen.Count == 0)
        {
            return;
        }

        var names = FolderLevels.StagingNames(chosen);
        var longest = 0;
        for (var i = 0; i < chosen.Count; i++)
        {
            longest = Math.Max(
                longest,
                FolderLevels.LongestDestinationLength(chosen[i], staging, names[i], catalogue));
        }

        LongestDestinationLength = longest;
        if (longest >= LongPathThreshold)
        {
            LongPathText = "The longest file this copy would create is "
                + longest.ToString(CultureInfo.InvariantCulture)
                + " characters. Windows refuses a path of 260 characters or more unless long paths "
                + "are enabled, and the script stops at the first file it cannot create, leaving a "
                + "partial copy. Choose a shorter staging folder, or a deeper folder for the nights "
                + "with the longest paths.";
        }
    }

    // ------------------------------------------------------------------ the settings disclosure

    /// <summary>Whether the settings disclosure is open. It opens itself while no staging folder
    /// is set.</summary>
    [ObservableProperty]
    private bool _isSettingsOpen;

    /// <summary>Opens and closes the settings disclosure. Not a change: it withdraws no generated
    /// script.</summary>
    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    /// <summary>The staging folder field, which keeps whatever the user typed even when it is
    /// refused, so it can be corrected.</summary>
    [ObservableProperty]
    private string _stagingPath;

    /// <summary>The refusal sentence against the staging folder, or empty when it stands.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStagingError))]
    private string _stagingError = "";

    /// <summary>Whether a staging refusal is on screen.</summary>
    public bool HasStagingError => StagingError.Length > 0;

    /// <summary>The excluded folder patterns, one per line. A pattern edit applies to this export
    /// only until Save as defaults is pressed.</summary>
    [ObservableProperty]
    private string _exclusionsText;

    /// <summary>The refusal sentence against an exclusion pattern, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExclusionError))]
    private string _exclusionError = "";

    /// <summary>Whether an exclusion refusal is on screen.</summary>
    public bool HasExclusionError => ExclusionError.Length > 0;

    /// <summary>Whether Save as defaults is confirming. One debounced flag, held for
    /// <see cref="SavedConfirmationDuration"/>.</summary>
    [ObservableProperty]
    private bool _isSavedConfirmed;

    /// <summary>How long the Save as defaults confirmation stays on screen. Settable so a case
    /// does not wait a second for it.</summary>
    public TimeSpan SavedConfirmationDuration { get; set; } = SavedConfirmationDefault;

    /// <summary>The patterns the generator is handed, one per non-blank line of the field.
    /// </summary>
    public IReadOnlyList<string> Exclusions =>
        [.. ExclusionsText
            .Split('\n')
            .Select(line => line.Trim('\r', ' ', '\t'))
            .Where(line => line.Length > 0)];

    partial void OnExclusionsTextChanged(string? oldValue, string newValue)
    {
        if (_suppressExclusionCheck)
        {
            return;
        }

        // Refused as it is entered: the list keeps its previous contents. The refused set is
        // ScriptGenerator.IsRefusedPatternChar, which the Task 4 security review ruled has exactly
        // one home, and which also refuses a backslash and the typographic quotes a narrower copy
        // here would admit. It is tested PER LINE, because a pattern reaches both scripts inside a
        // quoted literal while the field's own line breaks are its separator and stay legal.
        foreach (var line in newValue.Split('\n'))
        {
            if (!line.Trim('\r').Any(ScriptGenerator.IsRefusedPatternChar))
            {
                continue;
            }

            _suppressExclusionCheck = true;
            ExclusionsText = oldValue ?? "";
            _suppressExclusionCheck = false;
            ExclusionError = PatternRefusal;
            return;
        }

        ExclusionError = "";
        WithdrawScript();
    }

    /// <summary>
    /// Spec 12.13's Save as defaults: writes <c>wbpp_exclusions</c> in one <c>MutateGeneral</c> and
    /// writes nothing else.
    /// </summary>
    /// <remarks><b>The staging key is not written here</b> (review P2-1). <see cref="Commit"/> is
    /// its writer on every accepted change and deliberately never clears it, so the key is already
    /// current at every moment this command can run; writing <c>_committedStaging</c> from here
    /// would store JSON null, and so silently forget a remembered folder, whenever the field is
    /// blank or its value stands refused. The command carries no guard and is pressable in exactly
    /// that state. The quality filter persists itself on every change, in the panel, and is not
    /// part of this write either.</remarks>
    [RelayCommand]
    private async Task SaveAsDefaultsAsync()
    {
        var exclusions = Exclusions;

        _mutateGeneral(general => general with
        {
            WbppExclusionsDocument = WbppSettingsRead.WriteExclusions(exclusions),
        });

        IsSavedConfirmed = true;
        await Task.Delay(SavedConfirmationDuration).ConfigureAwait(true);
        if (!_disposed)
        {
            IsSavedConfirmed = false;
        }
    }

    // ------------------------------------------------------------------ the staging folder

    /// <summary>Opens the platform folder picker and returns the chosen absolute path, or null
    /// when the user cancelled, when there is no top level, or when the chosen location is not on
    /// the filesystem. Set by the window; null in a headless test.</summary>
    /// <remarks>
    /// <para>
    /// <b>The setter notifies</b> (launched-app look B1). The window assigns this seam in its
    /// <c>OnOpened</c>, which is after the Browse button has bound the command and read its
    /// can-execute against a null picker; as a plain auto-property nothing re-raised it, so the
    /// only route to the folder picker was permanently disabled on the shipped page while every
    /// case that installed the seam before showing the view passed. The guard stays in the command
    /// body as well, because <c>RelayCommand.Execute</c> ignores <c>CanExecute</c>.
    /// </para>
    /// <para>
    /// The notification goes through <see cref="_post"/> rather than straight out of the setter:
    /// a <c>CanExecute</c> notification reaching a command off the UI thread is the Phase 14B
    /// crash this type's own remarks name, and a settable seam cannot state which thread its
    /// caller is on.
    /// </para>
    /// </remarks>
    public Func<Task<string?>>? StagingFolderPicker
    {
        get => _stagingFolderPicker;
        set
        {
            _stagingFolderPicker = value;
            _post(PickStagingFolderCommand.NotifyCanExecuteChanged);
        }
    }

    private Func<Task<string?>>? _stagingFolderPicker;

    /// <summary>Opens the platform save dialog for the suggested file name and returns the chosen
    /// absolute path, or null on the same three conditions. Set by the window; null in a headless
    /// test, which is what makes Generate a no-op there rather than a null reference.</summary>
    /// <remarks>A plain auto-property on purpose: no <c>CanExecute</c> reads this seam.
    /// <c>CanGenerate</c> rests on the committed staging folder alone and the picker is read at
    /// execution time, so the B1 shape cannot arise here and a notification would raise nothing.
    /// </remarks>
    public Func<string, Task<string?>>? ScriptDestinationPicker { get; set; }

    /// <summary>Commits the typed staging folder, which the field's focus loss raises.</summary>
    [RelayCommand]
    private void CommitStaging() => Commit(StagingPath);

    /// <summary>Opens the folder picker and commits what it returns.</summary>
    [RelayCommand(CanExecute = nameof(CanPickStagingFolder))]
    private async Task PickStagingFolderAsync()
    {
        // RelayCommand.Execute ignores CanExecute, so the rule that makes the command correct is
        // in the body as well as in the guard.
        if (StagingFolderPicker is not { } picker)
        {
            return;
        }

        var chosen = await picker().ConfigureAwait(true);
        if (chosen is null)
        {
            return;
        }

        StagingPath = chosen;
        Commit(chosen);
    }

    // Raised from the StagingFolderPicker setter, which is the one site the answer moves.
    private bool CanPickStagingFolder() => StagingFolderPicker is not null;

    private void Commit(string value)
    {
        var folder = Normalise(value);
        RecheckStaging(value);

        // A refused value is not stored and not used; the field keeps what was typed so it can be
        // corrected, and Generate stays disabled.
        if (_committedStaging is null || folder is null)
        {
            return;
        }

        if (string.Equals(folder, _storedStaging, StringComparison.OrdinalIgnoreCase))
        {
            // A commit that does not change the value writes nothing.
            return;
        }

        // Remembered at once: the first choice writes the key without the user finding Save as
        // defaults, and every later edit writes the same way. One MutateGeneral, one key.
        _storedStaging = folder;
        _mutateGeneral(general => general with
        {
            WbppStagingPathDocument = WbppSettingsRead.WriteStagingPath(folder),
        });
    }

    // The whole confinement rule, in both directions, against the scan roots as configured at this
    // moment and against the levels chosen at this moment (seam-review ruling 4). One rule,
    // PathConfinement.IsUnderOrEqual: no StartsWith over a path and no separator-appending prefix
    // test lives anywhere in this task. Never clears the stored key: a value that has come to
    // breach the rule is reported and left alone.
    private void RecheckStaging(string value)
    {
        var before = _committedStaging;
        var folder = Normalise(value);
        if (folder is null)
        {
            _committedStaging = null;
            StagingError = "";
        }
        else
        {
            var refusal = RefusalFor(folder);
            _committedStaging = refusal is null ? folder : null;
            StagingError = refusal ?? "";
        }

        if (!string.Equals(before, _committedStaging, StringComparison.OrdinalIgnoreCase))
        {
            // Spec 12.13 names the staging folder among the five changes that withdraw the Script
            // part, and it does not distinguish a change to a legal value from one to a refused or
            // blanked value: the part would otherwise keep stating a run command for a plan the
            // page can no longer generate. This is the one place the committed value moves, so it
            // covers the typed commit, the picker and the level-change re-check together.
            WithdrawScript();
        }

        AfterStagingChanged();
    }

    private string? RefusalFor(string folder)
    {
        // Both sides canonical: PathConfinement.IsUnderOrEqual documents its arguments as
        // Path.GetFullPath results, and a root or a source spelled differently from the staging
        // folder would otherwise pass both clauses.
        foreach (var root in _getGeneral().ScanRoots)
        {
            if (Conflicts(Canonical(root), folder))
            {
                return ScanRootRefusal;
            }
        }

        foreach (var chosen in ChosenLevels())
        {
            if (Conflicts(Canonical(chosen.Level.Path), folder))
            {
                return SourceRefusal;
            }
        }

        return null;
    }

    private static bool Conflicts(string other, string folder)
        => PathConfinement.IsUnderOrEqual(other, folder) || PathConfinement.IsUnderOrEqual(folder, other);

    private void AfterStagingChanged()
    {
        StagingFolderText = _committedStaging ?? "";
        RefreshLongPath(ChosenLevels());
        OnPropertyChanged(nameof(CopyDestination));

        // GenerateDisabledReason and IsGenerateAvailable are expressions over the committed value,
        // so this is the one site that can raise them. Without it the footer keeps the
        // choose-a-folder sentence beside an enabled Generate for the rest of the visit.
        OnPropertyChanged(nameof(GenerateDisabledReason));
        OnPropertyChanged(nameof(IsGenerateAvailable));
        GenerateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The typed or picked staging folder as one canonical absolute path, or null when the field is
    /// blank or holds something no path can be made of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Path.GetFullPath</c> is pure string work over the value and the process's current
    /// directory: it opens nothing, probes nothing and lists nothing, so it is legal in this file
    /// and the containment census does not move. Without it a folder typed as <c>C:/lib/staged</c>,
    /// with a dot-dot segment, or with a trailing separator sits inside a scan root and is accepted
    /// by both clauses of <see cref="RefusalFor"/>, which is the one outcome the staging rule
    /// exists to prevent.
    /// </para>
    /// <para>
    /// <b>An 8.3 short component is not expanded</b>, because expanding one needs the disk and
    /// nothing here touches a user path. A staging folder spelled with a short component can
    /// therefore still name a folder inside a scan root spelled long; the generator's own guard
    /// compares the same spellings, so the limit is recorded in the task report rather than papered
    /// over with a probe.
    /// </para>
    /// </remarks>
    private static string? Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            // GetFullPath keeps a trailing separator, so the same folder typed twice with and
            // without one would read as two values and write the key again; the trim is the
            // framework's own and leaves a drive or share root, which IS its trailing separator,
            // alone.
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
        }
        catch (ArgumentException)
        {
            // A string no path can be made of reads as unset rather than throwing out of a commit.
            return null;
        }
    }

    // A configured root or a chosen source in the same canonical form. A value GetFullPath cannot
    // take is compared as it stands, which still refuses an exact match and never widens the rule.
    private static string Canonical(string value)
    {
        try
        {
            return string.IsNullOrWhiteSpace(value) ? value : Path.GetFullPath(value);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    // ------------------------------------------------------------------ the in-app copy

    /// <summary>The review block for a destination that is a whole drive or share.</summary>
    public const string DriveRootRefusal =
        "That folder is the top of a drive or share. Choose a folder inside it, or keep the "
        + "subfolder box on.";

    /// <summary>Whether the copy and the script land in a subfolder named after the target. On by
    /// default and never persisted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyDestination))]
    private bool _isSubfolderOn = true;

    /// <summary>The subfolder's name: the target name as the script file name sanitizes it.
    /// </summary>
    public string SubfolderName => ScriptGenerator.SanitizeScriptName(_targetName);

    // The subfolder moves every destination path, so the long-path figure and any script follow it.
    partial void OnIsSubfolderOnChanged(bool value)
    {
        RefreshLongPath(ChosenLevels());
        WithdrawScript();
    }

    /// <summary>The committed staging folder, plus <see cref="SubfolderName"/> while the box is on,
    /// or null while no staging folder stands. Nothing creates it before the commit.</summary>
    public string? CopyDestination => _committedStaging is not { } root
        ? null
        : IsSubfolderOn ? Path.Join(root, SubfolderName) : root;

    /// <summary>The copy's io, a seam so a case injects its own disk.</summary>
    internal Func<AppWriter.StagingWriter, StagingIo> StagingIoFor { get; set; } = StagingIo.For;

    /// <summary>
    /// The review blocks against <see cref="CopyDestination"/>: unset, a drive or share root, or inside
    /// or around a scan root or a chosen source. Null when the copy may proceed.
    /// </summary>
    public string? CopyDestinationRefusal()
    {
        if (CopyDestination is not { } destination)
        {
            return NoStagingReason;
        }

        var root = Path.GetPathRoot(destination) ?? "";
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(root),
                Path.TrimEndingDirectorySeparator(destination),
                StringComparison.OrdinalIgnoreCase))
        {
            return DriveRootRefusal;
        }

        return RefusalFor(destination);
    }

    /// <summary>
    /// The copy of the chosen folder levels into <see cref="CopyDestination"/> through a
    /// new-file-only staging writer. The caller has locked the wizard before this runs.
    /// </summary>
    public async Task<StagingCopyResult> RunCopyAsync(IProgress<StagingProgress> progress, CancellationToken ct)
    {
        var destination = CopyDestination ?? throw new InvalidOperationException(NoStagingReason);
        var chosen = ChosenLevels();
        var operations = FolderLevels.CopyOperations(
            chosen, FolderLevels.ExcludedUnderLevels(chosen, ExcludedByNight()));
        var request = new StagingCopyRequest(operations, destination, Exclusions);

        using var writer = _appWriter.BeginStagingCopy(destination);
        var copier = new StagingCopier(StagingIoFor(writer));

        // Off the UI thread: the copier's first pass enumerates every source tree synchronously.
        var result = await Task.Run(() => copier.RunAsync(request, progress, ct), CancellationToken.None)
            .ConfigureAwait(true);

        // Closed mid-copy (a shutdown close disposes the wizard): no window is left to list the
        // partial files, so the log does.
        if (_disposed && result.PartialPaths.Count > 0)
        {
            _logger.LogInformation(
                "The staging copy stopped with {Count} partial files: {Paths}",
                result.PartialPaths.Count,
                string.Join("; ", result.PartialPaths));
        }

        return result;
    }

    /// <summary>Save report: one new text file at the path the save dialog returned.
    /// True when it was written.</summary>
    public async Task<bool> SaveReportAsync(string text)
    {
        if (ScriptDestinationPicker is not { } picker)
        {
            return false;
        }

        var path = await picker("copy_report_" + SubfolderName + ".txt").ConfigureAwait(true);
        return path is not null && !_disposed && WriteOneFile(path, text);
    }

    // The page's one export write, shared by the script and the report: a scoped writer disposed
    // when the write ends (spec 2.1.1). A failure is stated as WriteFailure, never thrown.
    private bool WriteOneFile(string destination, string text)
    {
        try
        {
            using var writer = _appWriter.BeginExport(destination);
            writer.WriteAllText(destination, text);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Writing an export file failed");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Writing an export file was refused");
        }

        GenerateError = WriteFailure;
        return false;
    }

    // ------------------------------------------------------------------ the script part

    /// <summary>Where the last Generate wrote its script, or null.</summary>
    [ObservableProperty]
    private string? _scriptPath;

    /// <summary>The one string the file was written from, or null while no Generate has written
    /// one. Copy script and Show script read this and never regenerate.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScript))]
    [NotifyCanExecuteChangedFor(nameof(CopyScriptCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyRunCommandCommand))]
    private string? _scriptText;

    /// <summary>Whether the Script part is on screen.</summary>
    public bool HasScript => ScriptText is not null;

    /// <summary>The flavour the last Generate wrote, or null while no script stands. Run script
    /// reads this: only a PowerShell script is run from the page.</summary>
    [ObservableProperty]
    private WbppScriptType? _generatedScriptType;

    /// <summary>The run command for the flavour that was generated, stated exactly and matching
    /// the script's own header comment byte for byte.</summary>
    [ObservableProperty]
    private string _runCommandText = "";

    /// <summary>Whether the script text itself is revealed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScriptToggleLabel))]
    private bool _isScriptShown;

    /// <summary>One label for one toggle: Show script or Hide script.</summary>
    public string ScriptToggleLabel => IsScriptShown ? "Hide script" : "Show script";

    /// <summary>Reveals and hides the script text. Not a change: it withdraws nothing.</summary>
    [RelayCommand]
    private void ToggleScript() => IsScriptShown = !IsScriptShown;

    /// <summary>Writes the one string the file was written from to the clipboard.</summary>
    [RelayCommand(CanExecute = nameof(HasScript))]
    private async Task CopyScriptAsync()
    {
        if (ScriptText is not { } text)
        {
            return;
        }

        await _copyText(text).ConfigureAwait(true);
    }

    /// <summary>Writes the run command line to the clipboard.</summary>
    [RelayCommand(CanExecute = nameof(HasScript))]
    private async Task CopyRunCommandAsync()
    {
        if (!HasScript || RunCommandText.Length == 0)
        {
            return;
        }

        await _copyText(RunCommandText).ConfigureAwait(true);
    }

    // Spec 12.13: changing a level, a constraint, an override, an exclusion or the staging folder
    // after a Generate withdraws the Script part, because it would otherwise describe a plan no
    // longer on the page. The file already on disk is not touched: this application wrote it once
    // and never revisits it.
    internal void WithdrawScript()
    {
        // A refusal a Generate stated names the level, the exclusion or the staging folder as they
        // were when it ran, so it is withdrawn with the plan it was about (review P3-8). Ahead of
        // the early return, because a refused Generate leaves no script text behind and the
        // sentence would otherwise stand in the footer while the user corrects what it named.
        GenerateError = "";

        if (ScriptText is null)
        {
            return;
        }

        ScriptText = null;
        ScriptPath = null;
        GeneratedScriptType = null;
        RunCommandText = "";
        IsScriptShown = false;
    }

    // ------------------------------------------------------------------ Generate

    /// <summary>The flavour <c>general.wbpp_default_os</c> marks as the default. Written by
    /// nothing on this page.</summary>
    public WbppScriptType DefaultScriptType { get; }

    /// <summary>Whether the PowerShell flavour is the one <c>general.wbpp_default_os</c> marks as
    /// the default. Fixed for the page's life: the key is read once and written by nothing here.
    /// </summary>
    public bool PowerShellIsDefault => DefaultScriptType == WbppScriptType.PowerShell;

    /// <summary>Whether the Bash flavour is the marked default.</summary>
    public bool BashIsDefault => DefaultScriptType == WbppScriptType.Bash;

    /// <summary>Whether Generate can run at all, which is the split control's own enabled state.
    /// Raised from the one site the committed staging folder moves.</summary>
    public bool IsGenerateAvailable => _committedStaging is not null;

    /// <summary>The reason on a disabled Generate, and <b>null</b> when it is available. Null and
    /// not empty, because the reason is bound to <c>ToolTip.Tip</c> as well as to the footer line
    /// and Avalonia shows a tooltip for any non-null Tip: an empty string pops an empty box over an
    /// enabled button. The same rule, written the same way, as
    /// <c>TargetDetailViewModel.NoNightCheckedHint</c> on the other side of the flyout. The footer
    /// TextBlock renders nothing for null exactly as it did for empty.</summary>
    public string? GenerateDisabledReason => _committedStaging is null ? NoStagingReason : null;

    /// <summary>The refusal a Generate reported, or empty. A quoting refusal names the value it
    /// refused; a staging refusal is reported as the ordinary staging refusal instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGenerateError))]
    private string _generateError = "";

    /// <summary>Whether a Generate refusal is on screen.</summary>
    public bool HasGenerateError => GenerateError.Length > 0;

    /// <summary>
    /// Spec 12.13's Generate flow: build the text, open a save dialog, write one new file through
    /// the scoped export writer, then show the Script part.
    /// </summary>
    /// <param name="type">The flavour the split control's menu chose.</param>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync(WbppScriptType type)
    {
        // Both rules that make this command correct are repeated here, because RelayCommand.Execute
        // ignores CanExecute: a keyboard path to an enabled-looking control still writes nothing.
        // CopyDestination, not the bare root: one staging folder for both routes.
        if (CopyDestination is not { } staging || ScriptDestinationPicker is not { } picker)
        {
            return;
        }

        GenerateError = "";

        var chosen = ChosenLevels();
        var excludedUnder = FolderLevels.ExcludedUnderLevels(chosen, ExcludedByNight());
        var operations = FolderLevels.CopyOperations(chosen, excludedUnder);
        var fileName = ScriptGenerator.FileNameFor(type, _targetName);

        string text;
        try
        {
            // The catch is around the generate call and not around the write, because the write
            // must not run at all. Each exception is caught by its own type and never by its
            // message: a message is text Task 4 may edit, and a page that read one would break
            // silently the first time it did.
            text = ScriptGenerator.Generate(
                type,
                new WbppScriptInput(operations, staging, _targetName, Exclusions, fileName));
        }
        catch (WbppUnsafeValueException ex)
        {
            // The exception carries the offending input itself as well as its kind, so the page
            // names the file or the pattern it refused, as spec 12.13's states row requires, and
            // never parses a message.
            GenerateError = QuotingRefusal(ex.ValueName, ex.Value);
            return;
        }
        catch (WbppStagingRootException)
        {
            // The page's own check of spec 12.13 runs first and should make this unreachable. It
            // is caught anyway and reported as the same refusal, never as a crash: the generator's
            // guard is design lesson 2 holding the guarantee at a second choke point, and a page
            // that fell over when the second guard fired would turn a caught mistake into a dead
            // window.
            //
            // The committed value is withdrawn through RecheckStaging, the one site that moves it,
            // so the Script part goes with it (review P3-7): a part left on screen would state a
            // run command for a plan this Generate has just refused. It is withdrawn as a blank
            // rather than re-checked against the field, because the page's own rule is the wider of
            // the two and would accept the very value the generator refused, leaving Generate
            // enabled with no sentence. The refusal is then stated over the blank's empty one.
            RecheckStaging("");
            StagingError = SourceRefusal;
            return;
        }

        var destination = await picker(fileName).ConfigureAwait(true);
        if (destination is null)
        {
            // A cancelled dialog writes nothing, changes nothing on the page and reports nothing.
            return;
        }

        if (_disposed)
        {
            // The window was dismissed while the dialog was open. Nothing is written for a page
            // that is gone, and nothing is published onto one nothing is bound to.
            return;
        }

        // One new file at exactly the path the dialog returned. The dialog's own overwrite prompt
        // is the only way an existing file is replaced (spec 2.1.1).
        if (!WriteOneFile(destination, FileTextFor(type, text)))
        {
            return;
        }

        ScriptPath = destination;
        ScriptText = text;
        GeneratedScriptType = type;
        RunCommandText = RunCommandFor(type, fileName);
        IsScriptShown = false;
    }

    private bool CanGenerate(WbppScriptType type)
    {
        _ = type;
        return _committedStaging is not null;
    }

    /// <summary>
    /// The byte order mark, as the one character U+FEFF. <c>ExportWriter.WriteAllText</c> encodes
    /// UTF-8 without a mark, and Windows PowerShell 5.1 reads a mark-less <c>.ps1</c> as ANSI, so
    /// a script carrying any non-ASCII byte misreads its own paths and copies nothing while
    /// reporting success. Prefixing the character makes the UTF-8 encoder emit EF BB BF.
    /// </summary>
    public const char BomPrefix = (char)0xFEFF;

    /// <summary>
    /// What is written to the file for one flavour: the PowerShell script behind a byte order
    /// mark, and the Bash script exactly as the generator produced it.
    /// </summary>
    /// <remarks>The one place the written text can differ from the shown and copied text. Show
    /// script and Copy script read <see cref="ScriptText"/>, which never carries the mark: a mark
    /// pasted into a terminal is an invisible character at the start of the first command. Line
    /// endings are whatever the generator emitted and are never normalised here.</remarks>
    /// <param name="type">The generated flavour.</param>
    /// <param name="script">The generator's own return.</param>
    public static string FileTextFor(WbppScriptType type, string script)
        => type == WbppScriptType.PowerShell ? BomPrefix + script : script;

    /// <summary>
    /// Spec 12.13's run command, stated exactly and identical to the line the script's own header
    /// comment carries.
    /// </summary>
    /// <param name="type">The generated flavour.</param>
    /// <param name="fileName">The name the script was generated for, which is the name its header
    /// states. The same value reaches the generator, so the two lines cannot disagree.</param>
    public static string RunCommandFor(WbppScriptType type, string fileName) => type switch
    {
        WbppScriptType.Bash => "chmod +x " + fileName + " && ./" + fileName,
        _ => BuildPowerShellRunCommand(fileName),
    };

    private static string BuildPowerShellRunCommand(string fileName)
    {
        // The apostrophes are doubled inside a double-quoted -Command string, which is the form the
        // generator's own header line uses.
        var quoted = fileName.Replace("'", "''");
        return @"powershell -ExecutionPolicy Bypass -Command ""Unblock-File -LiteralPath '.\"
            + quoted + @"'; & '.\" + quoted + @"'""";
    }

    /// <summary>
    /// The sentence a quoting refusal shows: which kind of value was refused, in the port's own
    /// words, and the value itself, so spec 12.13's "the page names the file and says the export
    /// cannot quote it" is true of a frame path as well as of a pattern.
    /// </summary>
    /// <remarks>An unknown name falls through to the token itself, so a name Task 4 adds later
    /// reads awkwardly rather than silently. A refused value still carries the carriage return or
    /// line feed it was refused for, so the control characters are written as their escapes rather
    /// than emitted into a label.</remarks>
    /// <param name="valueName">The <c>ValueName</c> the exception carried.</param>
    /// <param name="value">The <c>Value</c> the exception carried, which is the offending input
    /// itself. Empty leaves the sentence naming the kind alone.</param>
    public static string QuotingRefusal(string valueName, string value)
    {
        var what = valueName switch
        {
            "exclusion pattern" => "one of the excluded folder patterns",
            "excluded file" => "one of the frames the filter excluded",
            "SourcePath" => "one of the folders this export copies from",
            "EntryName" => "one of the staging folder names",
            "StagingRoot" => "the staging folder",
            "TargetName" => "the target name",
            "FileName" => "the script file name",
            _ => valueName,
        };

        var named = string.IsNullOrEmpty(value) ? what : what + ", " + Visible(value) + ",";
        return "This export cannot be written, because " + named
            + " carries a character no script can quote safely. Correct it and generate again.";
    }

    // The refused value with its control characters written as their escapes: a file name carrying
    // a line feed would otherwise break the sentence across two lines and read as two failures.
    private static string Visible(string value) => value
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    // ------------------------------------------------------------------ closing

    /// <summary>Cancels the first read, stops anything still in flight from publishing onto a
    /// closed page, and disposes whatever was built into the quality panel's slot.</summary>
    /// <remarks>
    /// <para>
    /// Idempotent, the shape <c>FrameListDialogViewModel.Dispose</c> already uses: the modal host's
    /// cleanup lambda and a test harness both reach this, and a second <c>Cancel</c> on a disposed
    /// source throws out of a dispose path. The source itself is deliberately not disposed, because
    /// a read still in flight holds its token.
    /// </para>
    /// <para>
    /// The slot is the only reference to the panel once the page is gone, so the panel's own
    /// debouncer, cancellation source and pending settings flush are unreachable unless this
    /// disposes it.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _closing.Cancel();
        (QualityPanel as IDisposable)?.Dispose();
    }
}
