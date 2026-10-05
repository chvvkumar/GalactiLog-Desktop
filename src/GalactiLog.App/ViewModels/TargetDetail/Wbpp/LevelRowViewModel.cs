using System.Globalization;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.Core.Wbpp;

namespace GalactiLog.App.ViewModels.TargetDetail.Wbpp;

/// <summary>
/// One candidate export folder on spec 12.13's level tree: its indent, its folder name, its cost
/// badges, its byte total and its radio.
/// </summary>
/// <remarks>
/// <para>
/// Every figure here is read off the <see cref="FolderLevel"/> the Core side computed. Nothing is
/// re-derived: the depth, the frame count, the two contamination lists and the subtree bytes all
/// arrive filled, and a row that recomputed one of them could disagree with the footer's totals,
/// which are computed from the same record.
/// </para>
/// <para>
/// The indent follows <see cref="FolderLevel.DepthFromRoot"/> and not the row's position in the
/// list. The web indents by list index, which is correct only while every level sits at its own
/// depth; a night whose frames lie in sibling folders has several levels at one depth, and
/// indenting those by index would draw them as a staircase into each other rather than as
/// siblings.
/// </para>
/// </remarks>
public sealed partial class LevelRowViewModel : ObservableObject
{
    /// <summary>Pixels of indent per level of depth. The pin the layout case asserts.</summary>
    public const double IndentStep = 14d;

    /// <summary>
    /// What an other-target entry reads when its display name is this page's own target's name.
    /// Two different group keys can present one display name, a resolved target beside its
    /// unresolved <c>obj:</c> twin being the usual pair, so the entry is correct and the bare name
    /// would read as the page contradicting itself about whose frames these are.
    /// </summary>
    public const string SameNameSuffix = " (separate entry, same name)";

    private readonly Action<int> _choose;

    private readonly string _ownTargetName;

    /// <param name="level">The level itself, as <c>FolderLevels.ForSession</c> produced it.</param>
    /// <param name="index">Its position in <c>SessionLevels.Levels</c>, which is the night's
    /// chosen index when this row is picked.</param>
    /// <param name="groupName">The radio group this row belongs to, one per night, so a pick on
    /// one night cannot clear another night's.</param>
    /// <param name="ownTargetName">The page's own target's display name, which decides whether an
    /// other-target entry needs the same-name note of <see cref="OtherTargetsTooltip"/>.</param>
    /// <param name="choose">Called with <paramref name="index"/> when this row becomes the
    /// night's pick.</param>
    public LevelRowViewModel(
        FolderLevel level,
        int index,
        string groupName,
        string ownTargetName,
        Action<int> choose)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(choose);

        Level = level;
        Index = index;
        GroupName = groupName;
        _ownTargetName = ownTargetName;
        _choose = choose;
        FolderName = WbppPathText.OwnName(level.Path);
    }

    /// <summary>The level this row draws.</summary>
    public FolderLevel Level { get; }

    /// <summary>This row's position in its night's level list.</summary>
    public int Index { get; }

    /// <summary>The radio group name, one per night.</summary>
    public string GroupName { get; }

    /// <summary>The folder's own name, the last component of <see cref="FolderLevel.Path"/>.
    /// </summary>
    public string FolderName { get; }

    /// <summary>The row's left margin, <see cref="IndentStep"/> per level of depth.</summary>
    public Thickness Indent => new(Level.DepthFromRoot * IndentStep, 0, 0, 0);

    /// <summary>The night's own LIGHT frames under this folder.</summary>
    public string FrameCountText => WbppPathText.Frames(Level.FrameCount);

    /// <summary>The total size of every catalogued file under this folder, or the literal
    /// <c>unknown</c> when any one contributing size is null. Never a partial sum.</summary>
    public string BytesText => WbppPathText.Bytes(Level.SubtreeBytes);

    /// <summary>Whether this level drags another night's frames along.</summary>
    public bool HasOtherNights => Level.OtherNights.Count > 0;

    /// <summary>Whether this level drags another target's frames along.</summary>
    public bool HasOtherTargets => Level.OtherTargets.Count > 0;

    /// <summary>Spec 12.13's cost badge for other nights, singular and plural.</summary>
    public string OtherNightsBadge => Badge(Level.OtherNights.Count, "night", "nights");

    /// <summary>Spec 12.13's cost badge for other targets, singular and plural.</summary>
    public string OtherTargetsBadge => Badge(Level.OtherTargets.Count, "target", "targets");

    /// <summary>The nights themselves, for the badge's tooltip.</summary>
    public string OtherNightsTooltip => string.Join(", ", Level.OtherNights);

    /// <summary>The targets themselves, for the badge's tooltip. An entry whose display name is
    /// this page's own target's name carries <see cref="SameNameSuffix"/>.</summary>
    public string OtherTargetsTooltip => string.Join(
        ", ",
        Level.OtherTargets.Select(name =>
            string.Equals(name, _ownTargetName, StringComparison.OrdinalIgnoreCase)
                ? name + SameNameSuffix
                : name));

    /// <summary>Whether this row is the night's pick. Two-way from the radio, and set from the
    /// session when a pick is made anywhere in the group.</summary>
    [ObservableProperty]
    private bool _isChosen;

    partial void OnIsChosenChanged(bool value)
    {
        if (value)
        {
            _choose(Index);
        }
    }

    private static string Badge(int count, string singular, string plural) => count switch
    {
        <= 0 => "",
        1 => "+1 other " + singular,
        _ => string.Create(CultureInfo.InvariantCulture, $"+{count} other {plural}"),
    };
}
