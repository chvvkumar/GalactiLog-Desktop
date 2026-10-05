using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    /// <remarks>The seam's own rule, not a second spelling of it: the staging entry names are built
    /// on <see cref="FolderLevels.BaseName"/>, and the rename note compares a name from here against
    /// one of those, so a root path has to answer the same on both sides.</remarks>
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

/// <summary>
/// One checked night on spec 12.13's Folders to copy list: its row, its unavailable sentence, its
/// level tree and its chosen level.
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

    /// <param name="levels">The night's levels, as <c>FolderLevels.ForSession</c> produced
    /// them.</param>
    /// <param name="ownTargetName">The page's own target's display name, passed through to the
    /// level rows for the same-name note on a contamination entry.</param>
    /// <param name="chosenChanged">Called when this night's pick moves, so the page can recompute
    /// its totals, re-run the staging check and withdraw a generated script.</param>
    public SessionLevelViewModel(
        SessionLevels levels,
        string ownTargetName,
        Action<SessionLevelViewModel> chosenChanged)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(chosenChanged);

        Levels = levels;
        _chosenChanged = chosenChanged;
        GroupName = "WbppLevels_" + levels.Night.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        Rows = HasLevels
            ? [.. levels.Levels.Select((level, index) =>
                new LevelRowViewModel(level, index, GroupName, ownTargetName, Choose))]
            : [];

        // The default pick is Task 2's, never re-derived here. The backing field first, then the
        // row, so the night does not report a change before the page has finished building it.
        _chosenIndex = HasLevels ? levels.DefaultLevelIndex : -1;
        if (HasLevels)
        {
            Rows[_chosenIndex].IsChosen = true;
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

    /// <summary>This night's own radio group, so a pick here cannot clear another night's.
    /// </summary>
    public string GroupName { get; }

    /// <summary>The level tree, in <see cref="SessionLevels.Levels"/>' own order: depth ascending,
    /// then the path under <c>OrdinalIgnoreCase</c>. Empty when no level is available.</summary>
    public IReadOnlyList<LevelRowViewModel> Rows { get; }

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

    /// <summary>Whether the level tree is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    private bool _isExpanded;

    /// <summary>The tree toggle's label, which states what pressing it does, as every other
    /// disclosure on this page and in the session pane does.</summary>
    public string ToggleLabel => IsExpanded ? "Done" : "Change folder";

    /// <summary>Opens and closes this night's level tree. Not a change: it withdraws no generated
    /// script.</summary>
    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    /// <summary>The picked level's index in <see cref="SessionLevels.Levels"/>, or -1 when this
    /// night offers none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChosenLeadingPath))]
    [NotifyPropertyChangedFor(nameof(ChosenOwnName))]
    [NotifyPropertyChangedFor(nameof(ChosenOtherNightsBadge))]
    [NotifyPropertyChangedFor(nameof(ChosenOtherTargetsBadge))]
    [NotifyPropertyChangedFor(nameof(HasChosenOtherNightsBadge))]
    [NotifyPropertyChangedFor(nameof(HasChosenOtherTargetsBadge))]
    [NotifyPropertyChangedFor(nameof(ChosenOtherNightsTooltip))]
    [NotifyPropertyChangedFor(nameof(ChosenOtherTargetsTooltip))]
    private int _chosenIndex;

    /// <summary>This night's pick, or null when it offers none and contributes nothing.</summary>
    public ChosenLevel? Chosen => HasLevels ? new ChosenLevel(Night, Levels.Levels[ChosenIndex]) : null;

    /// <summary>
    /// Whether every level this night offers drags another night's or another target's frames
    /// along, which is spec 12.13's Every level contaminated state.
    /// </summary>
    /// <remarks>In that state there is no clean level to move to, so the cost is a fact about the
    /// export rather than something the tree can talk the user out of, and the spec shows the
    /// chosen level's badge on the row itself: the tree is closed when the page opens, and a cost
    /// visible only after pressing Change folder on every night is a cost no user reads.</remarks>
    public bool EveryLevelIsContaminated =>
        HasLevels && Levels.Levels.All(level => level.IsContaminated);

    private LevelRowViewModel? ChosenRow => HasLevels ? Rows[ChosenIndex] : null;

    /// <summary>The chosen level's own other-nights badge, shown on the session row in the state
    /// above and empty otherwise. The string is the row's, never recomputed here.</summary>
    public string ChosenOtherNightsBadge =>
        EveryLevelIsContaminated && ChosenRow is { HasOtherNights: true } row ? row.OtherNightsBadge : "";

    /// <summary>The chosen level's own other-targets badge, on the same rule.</summary>
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

    /// <summary>The chosen folder's leading path, drawn in the secondary ink. Split once here, so
    /// the row does not compose the same string twice.</summary>
    public string ChosenLeadingPath => Chosen is { } chosen ? WbppPathText.LeadingPath(chosen.Level.Path) : "";

    /// <summary>The chosen folder's own name, drawn in the primary ink.</summary>
    public string ChosenOwnName => Chosen is { } chosen ? WbppPathText.OwnName(chosen.Level.Path) : "";

    /// <summary>
    /// The name this night will be copied under, when it differs from the chosen folder's own
    /// name, and empty otherwise. Spec 12.13's rename note, shown on the rows it happens to and on
    /// no others.
    /// </summary>
    /// <remarks>The name itself is <c>FolderLevels.StagingNames</c>' answer, set by the page.
    /// Nothing here re-derives, re-cases or re-suffixes it: the collision rule, the date prefix and
    /// the numeric suffix all live in Task 2's one member.</remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStagingName))]
    private string _stagingName = "";

    /// <summary>Whether this night states a rename.</summary>
    public bool HasStagingName => StagingName.Length > 0;

    /// <summary>Why the rename happened, for the note's tooltip.</summary>
    public string StagingNameTooltip =>
        "Another chosen folder has the same name, so this night is copied under a distinct one";

    /// <summary>Records the staging entry name Task 2 gave this night, and states it only when it
    /// differs from the folder's own name.</summary>
    /// <param name="entryName">The entry name, exactly as <c>FolderLevels.StagingNames</c>
    /// returned it.</param>
    public void SetStagingEntryName(string entryName)
        => StagingName = string.Equals(entryName, ChosenOwnName, StringComparison.Ordinal) ? "" : entryName;

    private void Choose(int index)
    {
        if (ChosenIndex == index)
        {
            return;
        }

        ChosenIndex = index;
    }

    partial void OnChosenIndexChanged(int value)
    {
        foreach (var row in Rows)
        {
            row.IsChosen = row.Index == value;
        }

        _chosenChanged(this);
    }
}
