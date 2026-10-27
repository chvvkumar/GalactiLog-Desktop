using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Io;
using GalactiLog.Core.Wbpp;

namespace GalactiLog.App.ViewModels.TargetDetail.Wbpp;

/// <summary>
/// The small text rules spec 12.13's level tree and its session rows share: a folder's own name,
/// the path above it, a frame count and a byte figure.
/// </summary>
/// <remarks>One home for four one-line rules, so the row, the tree and the footer cannot render
/// the same figure two ways. <c>MetricText.Bytes</c> is the application's one byte rendering and
/// is reached through here rather than copied.</remarks>
internal static class WbppPathText
{
    /// <summary>The folder's own name, which is the last component of an absolute folder path.
    /// </summary>
    public static string OwnName(string path) => FolderLevels.BaseName(path);

    /// <summary>Everything above the folder's own name, in the secondary ink. Empty when the
    /// folder has no parent to state.</summary>
    public static string LeadingPath(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed);
        return string.IsNullOrEmpty(parent) ? "" : parent + Path.DirectorySeparatorChar;
    }

    /// <summary>A LIGHT frame count, singular and plural.</summary>
    public static string Frames(int count) => count == 1
        ? "1 light frame"
        : string.Create(CultureInfo.InvariantCulture, $"{count:N0} light frames");

    /// <summary>A byte figure, or the literal <c>unknown</c> when it is not known. Never a partial
    /// sum: spec 12.13's total is all or nothing.</summary>
    public static string Bytes(long? bytes) => bytes is long value ? MetricText.Bytes(value) : "unknown";
}

/// <summary>Where a night's calibration scan stands.</summary>
public enum CalibrationScanState { Idle, Scanning, Done }

/// <summary>
/// One checked night on spec 12.13's Folders to copy list: its row, its unavailable sentence, its
/// folder tree with a tick on every row, and the copy sources those ticks make.
/// </summary>
/// <remarks>
/// <para>
/// A night whose <see cref="SessionLevels.Unavailable"/> is non-null offers no tree and
/// contributes no copy operation. <see cref="SessionLevels.Levels"/> is empty there and
/// <see cref="SessionLevels.DefaultLevelIndex"/> means nothing, so nothing here indexes the list
/// without asking <see cref="HasLevels"/> first.
/// </para>
/// <para>
/// The leftover count is read from <see cref="SessionLevels.FramesWithoutRoot"/> and never
/// subtracted from two figures. A frame lying under no configured root contributes no chain and no
/// level, so a subtraction of the level counts from the night's total would disagree with the seam
/// the moment one frame fell under two levels.
/// </para>
/// </remarks>
public sealed partial class SessionLevelViewModel : ObservableObject
{
    /// <summary>Spec 12.13's sentence for a night with no catalogued frame.</summary>
    public const string NoFramesText = "No frames found for this night";

    /// <summary>Spec 12.13's sentence for a night whose frames sit in the scan root itself.
    /// </summary>
    public const string FramesInRootText =
        "These frames sit in the library root itself, so there is no folder to copy";

    /// <summary>Spec 12.13's sentence for a night spread across several scan roots. A night whose
    /// every frame lies under no configured root reads this too (seam-review ruling 3).</summary>
    public const string SeveralScanRootsText =
        "This night's frames are spread across more than one library folder, so no single folder holds them all";

    private readonly Action<SessionLevelViewModel> _chosenChanged;
    private readonly string _ownTargetName;
    private readonly Action<FolderNodeViewModel> _list;

    /// <param name="levels">The night's levels, as <c>FolderLevels.ForSession</c> produced
    /// them.</param>
    /// <param name="ownTargetName">The page's own target's display name, passed through to the
    /// rows for the same-name note on a contamination entry.</param>
    /// <param name="chosenChanged">Called when a tick on this night moves, so the page can
    /// recompute its totals, re-run the staging check and withdraw a generated script.</param>
    /// <param name="listSubfolders">The page's lister: reads the folders inside a row, off the UI
    /// thread, and answers through <see cref="FolderNodeViewModel.SetChildren"/>. Null lists
    /// nothing: every row answers at once with its chain children alone.</param>
    public SessionLevelViewModel(
        SessionLevels levels,
        string ownTargetName,
        Action<SessionLevelViewModel> chosenChanged,
        Action<SessionLevelViewModel, FolderNodeViewModel>? listSubfolders = null)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(chosenChanged);

        Levels = levels;
        _chosenChanged = chosenChanged;
        _ownTargetName = ownTargetName;

        // Open on load, unless the night has no tree to open: its toggle is hidden, so an open
        // panel there could not be closed.
        _isExpanded = HasLevels;

        _list = listSubfolders is { } lister
            ? row => lister(this, row)
            : row => row.SetChildren([]);

        // Levels arrive depth ascending, so every parent is built before its children. The parent
        // is the deepest level already built that contains this one.
        var rows = new List<FolderNodeViewModel>(HasLevels ? levels.Levels.Count : 0);
        for (var index = 0; HasLevels && index < levels.Levels.Count; index++)
        {
            var level = levels.Levels[index];
            var parent = rows
                .Where(row => PathConfinement.IsUnderOrEqual(row.Level.Path, level.Path)
                    && !string.Equals(row.Level.Path, level.Path, StringComparison.OrdinalIgnoreCase))
                .MaxBy(row => row.Level.Path.Length);
            var row = new FolderNodeViewModel(
                new SubfolderLevel(level, true), true, index, false, ownTargetName, parent, _list, OnRowChanged);
            parent?.AddChild(row);
            rows.Add(row);
        }

        Rows = rows;
        Roots = [.. rows.Where(row => row.Parent is null)];

        // The default level is Task 2's, never re-derived here. Seeded, not ticked, so the night
        // reports nothing before the page has finished building it; every other chain row is
        // unticked, which makes the default level the night's one source.
        if (HasLevels)
        {
            Rows[levels.DefaultLevelIndex].Seed(true);
            Rows[levels.DefaultLevelIndex].SeedBeneath();
        }

        foreach (var row in rows)
        {
            row.IsExpanded = true;
        }
    }

    /// <summary>The night's levels as the Core side computed them.</summary>
    public SessionLevels Levels { get; }

    /// <summary>The night itself.</summary>
    public DateOnly Night => Levels.Night;

    /// <summary>The night, rendered invariantly, which is how the ledger shows it.</summary>
    public string DateText => Levels.Night.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>This night's LIGHT frame count.</summary>
    public string FrameCountText => WbppPathText.Frames(Levels.TotalFrameCount);

    /// <summary>The chain rows, one per level, in <see cref="SessionLevels.Levels"/>' own order:
    /// depth ascending, then the path under <c>OrdinalIgnoreCase</c>. Empty when no level is
    /// available.</summary>
    public IReadOnlyList<FolderNodeViewModel> Rows { get; }

    /// <summary>The chain rows no other level contains, which are the top of the tree under the
    /// scan root.</summary>
    public IReadOnlyList<FolderNodeViewModel> Roots { get; }

    /// <summary>Whether this night offers a level tree at all.</summary>
    public bool HasLevels => Levels.Unavailable is null;

    /// <summary>The scan root that anchors the top of the tree, as a non-selectable line. Empty
    /// when no level is available.</summary>
    public string ScanRoot => Levels.ScanRoot;

    /// <summary>Spec 12.13's own sentence for this night's unavailable state, or empty when levels
    /// are available.</summary>
    public string UnavailableText => Levels.Unavailable switch
    {
        LevelsUnavailable.NoFrames => NoFramesText,
        LevelsUnavailable.FramesInRootItself => FramesInRootText,
        LevelsUnavailable.SeveralScanRoots => SeveralScanRootsText,

        // Seam-review ruling 3: this state is reached only when EVERY frame of the night lies
        // under no configured root, and spec 12.13's states table says it reads like the row
        // above.
        LevelsUnavailable.NoScanRoot => SeveralScanRootsText,
        _ => "",
    };

    /// <summary>Whether this night states an unavailable sentence instead of a tree.</summary>
    public bool HasUnavailableText => !HasLevels;

    /// <summary>
    /// Spec 12.13's "the row states how many frames those levels leave behind", for a night with
    /// SOME frames under no configured root. Composed in the port's own plain words because the
    /// spec states the rule and not the sentence.
    /// </summary>
    public string LeftoverText => Levels.FramesWithoutRoot switch
    {
        <= 0 => "",
        1 => "1 frame of this night lies under no library folder and is left out of this export",
        var count => string.Create(
            CultureInfo.InvariantCulture,
            $"{count:N0} frames of this night lie under no library folder and are left out of this export"),
    };

    /// <summary>Whether this night leaves frames behind. A night whose every frame is rootless is
    /// an unavailable row instead and states nothing here.</summary>
    public bool HasLeftoverText => HasLevels && Levels.FramesWithoutRoot > 0;

    /// <summary>
    /// The same rule for <see cref="SessionLevels.FramesInRootItself"/>: a night where SOME frames
    /// sit directly in the scan root and the rest sit deeper keeps its levels, and no level can
    /// carry the ones in the root. Composed in the port's own plain words, as the leftover
    /// sentence beside it is, because the spec states the rule and not the sentence. The two are
    /// independent and both are stated when both counts are non-zero.
    /// </summary>
    public string RootFramesText => Levels.FramesInRootItself switch
    {
        <= 0 => "",
        1 => "1 frame of this night sits in the library root itself and is left out of this export",
        var count => string.Create(
            CultureInfo.InvariantCulture,
            $"{count:N0} frames of this night sit in the library root itself and are left out of this export"),
    };

    /// <summary>Whether this night states frames left in the scan root. A night whose every frame
    /// is in the root is an unavailable row instead and states nothing here.</summary>
    public bool HasRootFramesText => HasLevels && Levels.FramesInRootItself > 0;

    /// <summary>Whether the night's tree is open. Open when the page loads, on a night that offers
    /// one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    private bool _isExpanded;

    /// <summary>The tree toggle's label, which states what pressing it does, as every other
    /// disclosure on this page and in the session pane does.</summary>
    public string ToggleLabel => IsExpanded ? "Collapse" : "Expand";

    /// <summary>Opens and closes this night's level tree. Not a change: it withdraws no generated
    /// script.</summary>
    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    /// <summary>The page's group key, which describes a row created for a calibration folder.
    /// </summary>
    internal string TargetKey { get; init; } = "";

    /// <summary>The index in <see cref="SessionLevels.Levels"/> of the shallowest ticked chain
    /// row, else the default level's, or -1 when this night offers none. The calibration scan and
    /// the foreign read are bounded by it.</summary>
    /// <remarks><see cref="Rows"/> is ordered depth first, then path, so the first ticked row is
    /// the shallowest.</remarks>
    public int ChosenIndex => !HasLevels ? -1
        : Rows.FirstOrDefault(row => row.IsIncluded)?.Index ?? Levels.DefaultLevelIndex;

    /// <summary>This night's copy sources, as the tick model reads them off the tree: one per
    /// ticked row whose parent is unticked, each with the unticked rows under it as skipped
    /// folders. Empty when the night offers no tree or nothing is ticked.</summary>
    public IReadOnlyList<ChosenLevel> CopySources
        => HasLevels ? TickModel.CopySources(Night, [.. Roots.Select(ToTickNode)]) : [];

    private static TickNode ToTickNode(FolderNodeViewModel row)
        => new(row.Level, row.IsIncluded, [.. row.Children.Select(ToTickNode)]);

    /// <summary>Whether the copy takes <paramref name="path"/>.</summary>
    public bool Covers(string path) => FolderLevels.Covers(CopySources, path);

    // Other targets' and other nights' LIGHT files under the night's start level, as the page read
    // them after load. Empty until then.
    private IReadOnlyList<WbppFramePath> _foreign = [];

    /// <summary>Takes the foreign LIGHT files the page read for this night and states each row's
    /// share of them. Called on the UI thread.</summary>
    public void SetForeignLights(IReadOnlyList<WbppFramePath> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        _foreign = frames;
        foreach (var root in Roots)
        {
            CountForeign(root);
        }
    }

    /// <summary>States the foreign file count on a row and every row under it, for rows a listing
    /// built after the foreign files arrived.</summary>
    public void CountForeign(FolderNodeViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.SetForeignCount(_foreign.Count(frame => PathConfinement.IsUnderOrEqual(row.Level.Path, frame.FilePath)));
        foreach (var child in row.Children)
        {
            CountForeign(child);
        }
    }

    /// <summary>The foreign LIGHT files the copy leaves out one by one: those it would otherwise
    /// take, under a source and under none of its skipped folders, which are left out whole.
    /// </summary>
    public IReadOnlyList<WbppFramePath> ForeignExcluded
    {
        get
        {
            var sources = CopySources;
            return [.. _foreign.Where(frame => FolderLevels.Covers(sources, frame.FilePath))];
        }
    }

    // ------------------------------------------------------------------ the skips

    /// <summary>How many folders this night's sources skip, or empty for none.</summary>
    public string SkippedText => SkippedCount switch
    {
        <= 0 => "",
        1 => "1 folder skipped",
        var count => string.Create(CultureInfo.InvariantCulture, $"{count:N0} folders skipped"),
    };

    /// <summary>How many excluded directories this night's sources carry: the unticked rows
    /// directly under a ticked one.</summary>
    public int SkippedCount => CopySources.Sum(source => source.SkippedFolders?.Count ?? 0);

    /// <summary>Whether this night states a skip.</summary>
    public bool HasSkippedText => SkippedText.Length > 0;

    /// <summary>The line under the tree stating what this night copies. Set by the page through
    /// <see cref="SetSummary"/>.</summary>
    [ObservableProperty]
    private string _summaryText = "";

    /// <summary>Records what this night alone copies, as the page computed it.</summary>
    /// <param name="totals">This night's totals, from <c>FolderLevels.Totals</c> over its own
    /// sources alone, so the line and the footer cannot disagree.</param>
    /// <param name="skippedCount">How many excluded directories this night's sources carry.</param>
    /// <param name="calibration">This night's selected calibration, which the line adds in short
    /// form. Null adds nothing.</param>
    /// <remarks>The size reads <c>unknown</c> as everywhere else, never a partial sum.</remarks>
    public void SetSummary(ExportTotals totals, int skippedCount, CalibrationTotals? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(totals);

        var text = "This night copies " + WbppPathText.Frames(totals.FrameCount) + ", " + WbppPathText.Bytes(totals.SizeBytes);
        text = skippedCount switch
        {
            <= 0 => text,
            1 => text + " after skipping 1 folder",
            _ => text + string.Create(CultureInfo.InvariantCulture, $" after skipping {skippedCount:N0} folders"),
        };

        List<string> plus = [];
        if (calibration is { FlatCount: > 0 } flats)
        {
            plus.Add(Counted(flats.FlatCount, "flat") + ", " + WbppPathText.Bytes(flats.FlatBytes));
        }

        if (calibration is { DarkFlatCount: > 0 } darkFlats)
        {
            plus.Add(Counted(darkFlats.DarkFlatCount, "flat-dark") + ", " + WbppPathText.Bytes(darkFlats.DarkFlatBytes));
        }

        SummaryText = plus.Count == 0 ? text : text + " plus " + string.Join(", ", plus);
    }

    private static string Counted(int count, string noun)
        => string.Create(CultureInfo.InvariantCulture, $"{count:N0} {noun}{(count == 1 ? "" : "s")}");

    // A tick moved. The ticks on the row and on every calibration row beneath it are the user's
    // from here on, so a re-match and a Rescan restore them by folder. A row created for
    // calibration that the user ticks is kept by a new result, as a matched folder is.
    private void OnRowChanged(FolderNodeViewModel row)
    {
        if (row.Row is null && _created.ContainsKey(row.Level.Path))
        {
            _userTicks[row.Level.Path] = row.IsIncluded;
        }

        foreach (var node in row.Descendants().Prepend(row))
        {
            if (node.Row is { } calibration)
            {
                _userTicks[calibration.Folder] = node.IsIncluded;
            }
        }

        RaiseTickFacts();
        _chosenChanged(this);
    }

    // Every fact read off the ticks, raised at once.
    private void RaiseTickFacts()
    {
        OnPropertyChanged(nameof(ChosenIndex));
        OnPropertyChanged(nameof(ChosenLeadingPath));
        OnPropertyChanged(nameof(ChosenOwnName));
        OnPropertyChanged(nameof(ChosenOtherNightsBadge));
        OnPropertyChanged(nameof(ChosenOtherTargetsBadge));
        OnPropertyChanged(nameof(HasChosenOtherNightsBadge));
        OnPropertyChanged(nameof(HasChosenOtherTargetsBadge));
        OnPropertyChanged(nameof(ChosenOtherNightsTooltip));
        OnPropertyChanged(nameof(ChosenOtherTargetsTooltip));
        OnPropertyChanged(nameof(SkippedText));
        OnPropertyChanged(nameof(HasSkippedText));
        OnPropertyChanged(nameof(CalibrationTally));
    }

    /// <summary>
    /// Whether every level this night offers drags another night's or another target's frames
    /// along, which is spec 12.13's Every level contaminated state.
    /// </summary>
    /// <remarks>In that state there is no clean level to move to, so the cost is a fact about the
    /// export rather than something the tree can talk the user out of, and the spec shows the
    /// chosen level's badge on the row itself: a collapsed tree hides the row badges, and a cost
    /// visible only inside an open tree on every night is a cost a user can miss.</remarks>
    public bool EveryLevelIsContaminated =>
        HasLevels && Levels.Levels.All(level => level.IsContaminated);

    // The source holding the most of this night's lights, which the session row names.
    private ChosenLevel? Primary => CopySources.MaxBy(source => source.Level.FrameCount);

    private FolderNodeViewModel? ChosenRow => Primary is { } primary
        ? Rows.FirstOrDefault(row => string.Equals(row.Level.Path, primary.Level.Path, StringComparison.OrdinalIgnoreCase))
        : null;

    /// <summary>The primary source's own other-nights badge, shown on the session row in the state
    /// above and empty otherwise. The string is the row's, never recomputed here.</summary>
    public string ChosenOtherNightsBadge =>
        EveryLevelIsContaminated && ChosenRow is { HasOtherNights: true } row ? row.OtherNightsBadge : "";

    /// <summary>The primary source's own other-targets badge, on the same rule.</summary>
    public string ChosenOtherTargetsBadge =>
        EveryLevelIsContaminated && ChosenRow is { HasOtherTargets: true } row ? row.OtherTargetsBadge : "";

    /// <summary>Whether the session row states an other-nights cost.</summary>
    public bool HasChosenOtherNightsBadge => ChosenOtherNightsBadge.Length > 0;

    /// <summary>Whether the session row states an other-targets cost.</summary>
    public bool HasChosenOtherTargetsBadge => ChosenOtherTargetsBadge.Length > 0;

    /// <summary>The nights themselves, for the session row badge's tooltip.</summary>
    public string ChosenOtherNightsTooltip => ChosenRow?.OtherNightsTooltip ?? "";

    /// <summary>The targets themselves, for the session row badge's tooltip.</summary>
    public string ChosenOtherTargetsTooltip => ChosenRow?.OtherTargetsTooltip ?? "";

    /// <summary>The primary source's leading path, drawn in the secondary ink. Split once here,
    /// so the row does not compose the same string twice.</summary>
    public string ChosenLeadingPath => Primary is { } primary ? WbppPathText.LeadingPath(primary.Level.Path) : "";

    /// <summary>The primary source's own name, drawn in the primary ink: the source holding the
    /// most of this night's lights. Empty when nothing is ticked.</summary>
    public string ChosenOwnName => Primary is { } primary ? WbppPathText.OwnName(primary.Level.Path) : "";

    // ------------------------------------------------------------------ the calibration block

    /// <summary>Whether the night shows the Calibration found block: the page has a walker and the
    /// night has levels to scan.
    /// </summary>
    public bool HasCalibrationBlock { get; init; }

    /// <summary>The catalogue, for the size of a folder the block owns. Null reads the scanned
    /// size instead.</summary>
    internal ContaminationIndex? Catalogue { get; init; }

    /// <summary>This night's own catalogued LIGHT files.</summary>
    internal IReadOnlyList<WbppFramePath> NightFrames { get; init; } = [];

    /// <summary>Asked for when the user presses Rescan.</summary>
    internal Action<SessionLevelViewModel>? RescanRequested { get; set; }

    /// <summary>Asked for when the user presses Look in.</summary>
    internal Action<SessionLevelViewModel>? LookHigherRequested { get; set; }

    /// <summary>Where the scan stands.</summary>
    [ObservableProperty]
    private CalibrationScanState _scanState;

    /// <summary>"Reading 916 headers under Date_2025-10-15" while the scan runs, else empty.
    /// </summary>
    [ObservableProperty]
    private string _progressText = "";

    /// <summary>"Scanned Date_2025-10-15, 916 headers, 4 s", with the unreadable files counted.
    /// </summary>
    [ObservableProperty]
    private string _scanSentence = "";

    /// <summary>The files the scan could not read, one path per line.</summary>
    [ObservableProperty]
    private string _issuesTooltip = "";

    /// <summary>One line per group of light sets differing only in filter.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _lightSetLines = [];

    /// <summary>One line per light set filter with no flat.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _warnings = [];

    /// <summary>The tree rows carrying a calibration row, in the matcher's row order. Empty before
    /// a match and after a result is dropped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalibrationTally))]
    private IReadOnlyList<FolderNodeViewModel> _calibrationRows = [];

    /// <summary>"1 of 2 calibration folders ticked", or empty when the match found none.</summary>
    public string CalibrationTally => CalibrationRows.Count switch
    {
        0 => "",
        1 => string.Create(CultureInfo.InvariantCulture, $"{CalibrationRows.Count(row => row.IsIncluded):N0} of 1 calibration folder ticked"),
        var count => string.Create(
            CultureInfo.InvariantCulture, $"{CalibrationRows.Count(row => row.IsIncluded):N0} of {count:N0} calibration folders ticked"),
    };

    /// <summary>"No calibration frames under Date_2025-10-15" after a scan that found none, else
    /// empty.</summary>
    [ObservableProperty]
    private string _noCalibrationText = "";

    /// <summary>"Look in 2025, 36,210 files" while a light set lacks flats and a parent is left to
    /// climb to, else null.</summary>
    [ObservableProperty]
    private string? _lookHigherText;

    /// <summary>Whether a scan is running. Cleared when its result is taken, ahead of
    /// <see cref="ScanState"/>, which turns Done only after the page has matched and published the
    /// result, so an observer of Done always sees the finished rows.</summary>
    internal bool IsScanning { get; private set; }

    /// <summary>The last mutation of a scan's publish: the block reads Done.</summary>
    internal void MarkDone() => ScanState = CalibrationScanState.Done;

    /// <summary>The level the last scan reached, or -1 before one finished.</summary>
    public int ScannedLevelIndex { get; private set; } = -1;

    /// <summary>The last match, or null before one.</summary>
    public CalibrationMatch? Match { get; private set; }

    /// <summary>Every file the scan found, cached so a pick change re-matches without walking.
    /// </summary>
    internal IReadOnlyList<ScannedFrame> ScannedFrames { get; private set; } = [];

    /// <summary>The roots walked so far, one per climb step.</summary>
    internal IReadOnlyList<string> ScannedRoots { get; private set; } = [];

    /// <summary>The headers read over every step so far.</summary>
    internal int HeadersRead { get; private set; }

    /// <summary>The files and folders the scan could not read.</summary>
    internal IReadOnlyList<ScanIssue> Issues { get; private set; } = [];

    // The user's own ticks on calibration rows by folder, kept across a re-match and a Rescan.
    private readonly Dictionary<string, bool> _userTicks = new(StringComparer.OrdinalIgnoreCase);

    // The rows created for calibration folders the tree had not drawn, by path.
    private readonly Dictionary<string, FolderNodeViewModel> _created = new(StringComparer.OrdinalIgnoreCase);

    // The rows carrying the current match's calibration rows, by folder.
    private readonly Dictionary<string, FolderNodeViewModel> _calibrationNodes = new(StringComparer.OrdinalIgnoreCase);

    // The parent level the last listing counted, and its count.
    private int _lookHigherIndex = -1;
    private int _lookHigherCount;

    [RelayCommand]
    private void Rescan() => RescanRequested?.Invoke(this);

    [RelayCommand]
    private void LookHigher() => LookHigherRequested?.Invoke(this);

    private string? ScannedLevelPath => ScannedLevelIndex >= 0 ? Levels.Levels[ScannedLevelIndex].Path : null;

    /// <summary>Starts the block's running state. A fresh scan drops the last result; a climb
    /// keeps it on screen until the new one lands.</summary>
    internal void BeginScan(string folder, bool fresh)
    {
        IsScanning = true;
        ScanState = CalibrationScanState.Scanning;
        ReportProgress(fresh ? 0 : HeadersRead, folder);
        LookHigherText = null;
        if (!fresh)
        {
            return;
        }

        ScannedLevelIndex = -1;
        Match = null;
        ScannedFrames = [];
        ScannedRoots = [];
        HeadersRead = 0;
        Issues = [];
        ScanSentence = "";
        IssuesTooltip = "";
        ClearResult();
    }

    /// <summary>States the running header count.</summary>
    internal void ReportProgress(int headers, string folder)
        => ProgressText = string.Create(CultureInfo.InvariantCulture, $"Reading {headers:N0} headers under {WbppPathText.OwnName(folder)}");

    /// <summary>Takes a finished scan. The page matches it next.</summary>
    internal void CompleteScan(
        IReadOnlyList<ScannedFrame> frames, IReadOnlyList<string> roots, int levelIndex, int headers,
        IReadOnlyList<ScanIssue> issues, TimeSpan elapsed)
    {
        ScannedFrames = frames;
        ScannedRoots = roots;
        ScannedLevelIndex = levelIndex;
        HeadersRead = headers;
        Issues = issues;

        var name = WbppPathText.OwnName(Levels.Levels[levelIndex].Path);
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"Scanned {name}, {headers:N0} header{(headers == 1 ? "" : "s")}, {Math.Round(elapsed.TotalSeconds):N0} s");
        ScanSentence = issues.Count switch
        {
            0 => text,
            1 => text + ", skipped 1 file that could not be read",
            var count => text + string.Create(CultureInfo.InvariantCulture, $", skipped {count:N0} files that could not be read"),
        };
        IssuesTooltip = string.Join(Environment.NewLine, issues.Select(issue => issue.Path));
        ProgressText = "";
        IsScanning = false;
    }

    /// <summary>A scan that failed: the night exports its lights alone.</summary>
    internal void FailScan(string folder)
    {
        ScannedLevelIndex = -1;
        Match = null;
        ScannedFrames = [];
        ScannedRoots = [];
        ScanSentence = "Could not scan " + WbppPathText.OwnName(folder);
        IssuesTooltip = "";
        ProgressText = "";
        ClearResult();
        IsScanning = false;
    }

    /// <summary>Takes the file count the listing of a parent level found.</summary>
    internal void SetLookHigherCount(int parentIndex, int count)
    {
        _lookHigherIndex = parentIndex;
        _lookHigherCount = count;
        RefreshLookHigher();
    }

    /// <summary>Publishes a match: the calibration rows in the tree (created along their path
    /// where the tree had no row, with the user's ticks restored by folder), light set lines,
    /// warnings and the Look in offer.</summary>
    internal void ApplyMatch(CalibrationMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        Match = match;
        Reconcile(match.Rows);
        LightSetLines = CalibrationMatcher.LightSetLines(match.LightSets);
        Warnings = match.MissingFlatWarnings;
        NoCalibrationText = match.Rows.Count == 0 ? "No calibration frames under " + WbppPathText.OwnName(ScannedLevelPath ?? "") : "";
        RefreshLookHigher();
        RaiseTickFacts();
    }

    /// <summary>Brings the tree to a new result (design.md Part C, "Lifecycle"): rows that are no
    /// longer matched lose their calibration row; rows created for a folder no longer matched, that
    /// the user never ticked and that hold no row, are removed; each matched folder gets its row,
    /// created along its path when missing, and its tick: on its first match the user's tick, else
    /// the matcher's default; on a later match the user's tick or the new default, for a folder
    /// holding none of this night's lights.</summary>
    private void Reconcile(IReadOnlyList<CalibrationRow> rows)
    {
        var folders = new HashSet<string>(rows.Select(row => row.Folder), StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, node) in _calibrationNodes.ToList())
        {
            if (!folders.Contains(folder))
            {
                node.SetCalibrationRow(null);
                _calibrationNodes.Remove(folder);
            }
        }

        foreach (var (path, node) in _created.OrderByDescending(pair => pair.Key.Length).ToList())
        {
            if (!folders.Contains(path) && !_userTicks.ContainsKey(path) && node.Children.Count == 0)
            {
                node.Parent!.RemoveChild(node);
                _created.Remove(path);
            }
        }

        var nodes = new List<FolderNodeViewModel>(rows.Count);
        foreach (var row in rows)
        {
            if (FindOrCreate(row.Folder) is not { } node)
            {
                continue;
            }

            var first = node.Row is null;
            var wasTicked = node.IsIncluded;
            node.SetCalibrationRow(row);
            if (first)
            {
                if (_userTicks.TryGetValue(row.Folder, out var ticked))
                {
                    node.Seed(ticked);
                }
                else if (row.DefaultTicked)
                {
                    // Ruling 2: a matched folder is ticked wherever it sits, and copies what is
                    // beneath it.
                    node.Seed(true);
                    if (!wasTicked)
                    {
                        node.SeedBeneath();
                    }
                }
                else if (node.Level.FrameCount == 0)
                {
                    // A folder holding this night's lights keeps its tick.
                    node.Seed(false);
                }

                // The path to a ticked calibration row opens; the night's levels keep the user's
                // state.
                if (node.IsIncluded)
                {
                    for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
                    {
                        if (!ancestor.IsChain)
                        {
                            ancestor.IsExpanded = true;
                        }
                    }
                }
            }
            else if (node.Level.FrameCount == 0)
            {
                node.Seed(_userTicks.TryGetValue(row.Folder, out var ticked) ? ticked : row.DefaultTicked);
            }

            _calibrationNodes[row.Folder] = node;
            nodes.Add(node);
        }

        CalibrationRows = nodes;
        foreach (var node in _created.Values.Concat(_calibrationNodes.Values))
        {
            node.RefreshCalibrationText();
        }
    }

    // The row for a folder: the deepest existing row that contains or is it, then one created row
    // per missing segment beneath. Null when no root contains the folder.
    private FolderNodeViewModel? FindOrCreate(string folder)
    {
        var node = Roots.FirstOrDefault(root => PathConfinement.IsUnderOrEqual(root.Level.Path, folder));
        while (node is not null && !SameFolder(node.Level.Path, folder)
            && node.Children.FirstOrDefault(child => PathConfinement.IsUnderOrEqual(child.Level.Path, folder)) is { } next)
        {
            node = next;
        }

        if (node is null || SameFolder(node.Level.Path, folder))
        {
            return node;
        }

        var path = node.Level.Path;
        foreach (var segment in Path.GetRelativePath(node.Level.Path, folder).Split(Path.DirectorySeparatorChar))
        {
            path = Path.Combine(path, segment);
            var level = Describe(path, node.Level.DepthFromRoot + 1);
            var row = new FolderNodeViewModel(
                level, false, -1, node.IsIncluded && FolderNodeViewModel.StartsTicked(level.Level), _ownTargetName, node, _list, OnRowChanged);
            var placed = node.AddChild(row);
            if (ReferenceEquals(placed, row))
            {
                _created[path] = row;
            }

            node = placed;
        }

        return node;
    }

    private static bool SameFolder(string left, string right)
        => PathConfinement.IsUnderOrEqual(left, right) && PathConfinement.IsUnderOrEqual(right, left);

    // A created row's figures, from the catalogue as a listed row's are.
    private SubfolderLevel Describe(string path, int depth) => Catalogue is { } catalogue
        ? FolderLevels.Subfolders(Night, TargetKey, [path], depth, NightFrames, catalogue)[0]
        : new SubfolderLevel(new FolderLevel(path, depth, 0, [], [], 0), false);

    /// <summary><see cref="CalibrationScan.LongestRelative"/> over this night's scanned frames.</summary>
    public int LongestScannedRelative(string folder) => CalibrationScan.LongestRelative(folder, ScannedFrames);

    /// <summary>The calibration frames the copy skips one by one: the unselected frames of every
    /// ticked calibration row it copies. Their selected siblings are counted once, in
    /// <see cref="CalibrationTotals"/>; the lights total counts catalogued lights only.</summary>
    public IReadOnlyList<WbppFramePath> CalibrationInPlaceExcludes()
        => Match is null
            ? []
            : [.. CalibrationScan.InPlaceExcludes(CopySources, TickedRows())
                .Select(frame => new WbppFramePath(Guid.Empty, frame.FilePath, frame.Size))];

    /// <summary>What the night's selected calibration and its uncatalogued files add to the copy.
    /// </summary>
    public CalibrationTotals CalibrationTotals()
    {
        if (Match is null)
        {
            return new CalibrationTotals(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        // The lights figure already counts catalogued files, so the scan's size-only light files
        // are not counted again as other files.
        // ponytail: only LIGHT paths the page knows are recognised; a catalogued non-light file in a
        // light folder would be counted twice. A per-file catalogue lookup closes it.
        var known = new HashSet<string>(
            NightFrames.Concat(_foreign).Select(frame => frame.FilePath), StringComparer.OrdinalIgnoreCase);
        return CalibrationScan.Totals(
            CopySources,
            TickedRows(),
            [.. ScannedFrames.Where(frame => !known.Contains(frame.FilePath))]);
    }

    private List<(CalibrationRow Row, bool Ticked)> TickedRows()
        => [.. CalibrationRows.Select(node => (node.Row!, node.IsIncluded))];

    private void ClearResult()
    {
        Match = null;
        Reconcile([]);
        LightSetLines = [];
        Warnings = [];
        NoCalibrationText = "";
        LookHigherText = null;
        RaiseTickFacts();
    }

    private void RefreshLookHigher()
        => LookHigherText = !IsScanning
            && Match is { EveryFilterHasAFlat: false } match
            && match.LightSets.Count > 0
            && ScannedLevelIndex >= 0
            && CalibrationClimb.ManualNext(Levels, ScannedLevelIndex) is int parent
                ? "Look in " + WbppPathText.OwnName(Levels.Levels[parent].Path)
                    + (parent == _lookHigherIndex
                        ? string.Create(CultureInfo.InvariantCulture, $", {_lookHigherCount:N0} file{(_lookHigherCount == 1 ? "" : "s")}")
                        : "")
                : null;
}
