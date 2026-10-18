using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Wbpp;

namespace GalactiLog.App.ViewModels.TargetDetail.Wbpp;

/// <summary>Where a row of a night's folder tree stands against the night's chosen row.</summary>
public enum FolderZone
{
    /// <summary>The chosen row itself: the folder the night copies from.</summary>
    Chosen,

    /// <summary>Under the chosen row: copied unless its tick, or an ancestor's, is cleared.</summary>
    Copied,

    /// <summary>A level that is not under the chosen row: offered, not copied.</summary>
    NotCopied,

    /// <summary>A disk folder that is not under the chosen row: not drawn.</summary>
    Hidden,
}

/// <summary>
/// One folder of a night's tree on spec 12.13's Folders to copy step: a chain row (one of the
/// night's levels, with a radio) or a disk row (a folder found inside one), with its tick, its
/// expander, its catalogue figures and its children.
/// </summary>
/// <remarks>
/// <para>
/// Every figure is read off the <see cref="FolderLevel"/> the Core side computed. Nothing is
/// re-derived, so a row and the footer, which are computed from the same record, cannot disagree.
/// A folder the catalogue does not know states <see cref="NotCataloguedText"/> instead of a zero it
/// does not have.
/// </para>
/// <para>
/// The tree is lazy. A row lists its directory the first time it is expanded and never again, so
/// collapsing, re-expanding, unticking or re-ticking reads nothing and loses no nested tick. The
/// read is the page's, reached through the <c>list</c> callback, because this file may not touch
/// the disk and the answer has to come back on the UI thread. A chain row's chain children are
/// merged into its listing in place, so a folder never appears twice, and are kept when the disk
/// does not name them.
/// </para>
/// <para>
/// Every row under the chosen row carries a tick, chain rows included; the chosen row and the rows
/// above it carry none. A tick is kept when the pick moves but applies only while the row is in the
/// <see cref="FolderZone.Copied"/> zone.
/// </para>
/// </remarks>
public sealed partial class FolderNodeViewModel : ObservableObject
{
    /// <summary>What a folder the catalogue holds no file under reads in place of its figures.
    /// </summary>
    public const string NotCataloguedText = "not catalogued";

    /// <summary>What a listed folder with no folder inside it states where its children would be.
    /// </summary>
    public const string NoFoldersText = "No folders inside";

    /// <summary>What a folder whose children could not be read states where they would be.
    /// </summary>
    public const string UnreadableText = "Could not read the folders inside";

    /// <summary>The pill on the chosen row.</summary>
    public const string CopiesFromHereText = "copies from here";

    /// <summary>The tag on an unticked row under the chosen one.</summary>
    public const string SkippedText = "skipped";

    /// <summary>
    /// What an other-target entry reads when its display name is this page's own target's name.
    /// Two different group keys can present one display name, a resolved target beside its
    /// unresolved <c>obj:</c> twin being the usual pair, so the entry is correct and the bare name
    /// would read as the page contradicting itself about whose frames these are.
    /// </summary>
    public const string SameNameSuffix = " (separate entry, same name)";

    private readonly string _ownTargetName;
    private readonly Action<FolderNodeViewModel> _list;
    private readonly Action<FolderNodeViewModel> _changed;
    private readonly List<FolderNodeViewModel> _chainChildren = [];
    private FolderNodeViewModel? _zonedAgainst;
    private bool _listRequested;

    /// <param name="folder">The folder and its figures, as <c>FolderLevels.ForSession</c> or
    /// <c>FolderLevels.Subfolders</c> described it.</param>
    /// <param name="isChain">Whether this folder is one of the night's levels, which carries a
    /// radio.</param>
    /// <param name="index">The level's position in <c>SessionLevels.Levels</c>, or -1 for a disk
    /// row.</param>
    /// <param name="groupName">The radio group, one per night, so a pick on one night cannot clear
    /// another night's.</param>
    /// <param name="ownTargetName">The page's own target's display name, which decides whether an
    /// other-target entry needs <see cref="SameNameSuffix"/>.</param>
    /// <param name="parent">The row this one sits inside, or null for a root.</param>
    /// <param name="list">Called once, the first time this row is expanded, so the page reads the
    /// folders inside and answers through <see cref="SetChildren"/>.</param>
    /// <param name="changed">Called when this row's radio is picked or its tick moves.</param>
    public FolderNodeViewModel(
        SubfolderLevel folder,
        bool isChain,
        int index,
        string groupName,
        string ownTargetName,
        FolderNodeViewModel? parent,
        Action<FolderNodeViewModel> list,
        Action<FolderNodeViewModel> changed)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(changed);

        Level = folder.Level;
        IsCatalogued = folder.IsCatalogued;
        IsChain = isChain;
        Index = index;
        GroupName = groupName;
        Parent = parent;
        _ownTargetName = ownTargetName;
        _list = list;
        _changed = changed;
        FolderName = WbppPathText.OwnName(Level.Path);
    }

    /// <summary>The folder this row draws, which is what an untick puts on the chosen level.
    /// </summary>
    public FolderLevel Level { get; }

    /// <summary>Whether the catalogue holds any file under this folder.</summary>
    public bool IsCatalogued { get; }

    /// <summary>Whether this folder is one of the night's levels.</summary>
    public bool IsChain { get; }

    /// <summary>Whether this row carries a radio: every level does.</summary>
    public bool ShowsRadio => IsChain;

    /// <summary>Whether this row carries a tick: every row under the chosen one does, chain rows
    /// included.</summary>
    public bool ShowsTick => Zone == FolderZone.Copied;

    /// <summary>This row's position in its night's level list, or -1 for a disk row.</summary>
    public int Index { get; }

    /// <summary>The radio group name, one per night.</summary>
    public string GroupName { get; }

    /// <summary>The folder's own name, the last component of <see cref="FolderLevel.Path"/>.
    /// </summary>
    public string FolderName { get; }

    /// <summary>The row this one sits inside, or null for a root.</summary>
    public FolderNodeViewModel? Parent { get; }

    /// <summary>The night's own LIGHT frames under this folder, or <see cref="NotCataloguedText"/>.
    /// </summary>
    public string FrameCountText => IsCatalogued ? WbppPathText.Frames(Level.FrameCount) : NotCataloguedText;

    /// <summary>The total size of every catalogued file under this folder, or the literal
    /// <c>unknown</c> when any one contributing size is null, never a partial sum. Empty when the
    /// catalogue does not know the folder, because the frame count already says so.</summary>
    public string BytesText => IsCatalogued ? WbppPathText.Bytes(Level.SubtreeBytes) : "";

    /// <summary>Whether this folder holds another night's frames.</summary>
    public bool HasOtherNights => Level.OtherNights.Count > 0;

    /// <summary>Whether this folder holds another target's frames.</summary>
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
    /// <remarks>A write of true reports the pick; a write of false reports nothing, because the
    /// row that took the pick has already reported it.</remarks>
    [ObservableProperty]
    private bool _isChosen;

    /// <summary>Whether this folder is copied. Unticked, it and everything under it is left out.
    /// </summary>
    /// <remarks>Kept on every row, but applies only while the row is in the
    /// <see cref="FolderZone.Copied"/> zone.</remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsChildren))]
    [NotifyPropertyChangedFor(nameof(IsSkipped))]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    private bool _isIncluded = true;

    /// <summary>Whether this row's children are open. The first opening lists them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandLabel))]
    [NotifyPropertyChangedFor(nameof(ShowsChildren))]
    private bool _isExpanded;

    /// <summary>The expander's glyph: <c>+</c> while closed, <c>-</c> while open.</summary>
    public string ExpandLabel => IsExpanded ? "-" : "+";

    /// <summary>Whether the children are on screen: while open, except under a tick that is
    /// cleared and applies, because the folders under a skipped one are left out with it.
    /// </summary>
    public bool ShowsChildren => IsExpanded && (IsIncluded || Zone != FolderZone.Copied);

    /// <summary>Whether this row's directory is being read.</summary>
    [ObservableProperty]
    private bool _isListing;

    /// <summary><see cref="NoFoldersText"/>, <see cref="UnreadableText"/>, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChildrenText))]
    private string _childrenText = "";

    /// <summary>Whether a sentence stands where the children would be.</summary>
    public bool HasChildrenText => ChildrenText.Length > 0;

    /// <summary>The folders inside this one: the chain children until listed, then the listing in
    /// the lister's order with the chain children merged in place.</summary>
    [ObservableProperty]
    private IReadOnlyList<FolderNodeViewModel> _children = [];

    /// <summary>The levels directly inside this one.</summary>
    public IReadOnlyList<FolderNodeViewModel> ChainChildren => _chainChildren;

    /// <summary>Where this row stands against the night's chosen row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTick))]
    [NotifyPropertyChangedFor(nameof(IsSkipped))]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    [NotifyPropertyChangedFor(nameof(ShowsChildren))]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    [NotifyPropertyChangedFor(nameof(IsChosenZone))]
    private FolderZone _zone;

    /// <summary>Whether this row is drawn: every row but a disk row outside the chosen one.
    /// </summary>
    public bool IsVisible => Zone != FolderZone.Hidden;

    /// <summary>Whether this row is the chosen one, which carries the highlight and the pill.
    /// </summary>
    public bool IsChosenZone => Zone == FolderZone.Chosen;

    /// <summary>Whether this row is left out of the copy by its own tick.</summary>
    public bool IsSkipped => Zone == FolderZone.Copied && !IsIncluded;

    /// <summary>The row's opacity: dimmed when offered but not copied, or skipped.</summary>
    public double RowOpacity => Zone == FolderZone.NotCopied || IsSkipped ? 0.55 : 1.0;

    /// <summary>Opens and closes this row's children.</summary>
    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    /// <summary>Adds a level directly inside this one, while the session builds the chain.
    /// </summary>
    internal void AddChainChild(FolderNodeViewModel child)
    {
        _chainChildren.Add(child);
        Children = [.. _chainChildren];
    }

    /// <summary>Takes the page's answer to the listing. Called on the UI thread.</summary>
    /// <param name="listed">The folders inside, or null when they could not be read.</param>
    /// <remarks>A listed folder that is a chain child is that chain child, never a second row; a
    /// chain child the listing did not name follows the listing. An unreadable directory keeps its
    /// chain children, so the night's levels always show.</remarks>
    public void SetChildren(IReadOnlyList<SubfolderLevel>? listed)
    {
        if (listed is null)
        {
            ChildrenText = UnreadableText;
            IsListing = false;
            return;
        }

        var rows = new List<FolderNodeViewModel>(listed.Count + _chainChildren.Count);
        foreach (var folder in listed)
        {
            var chain = _chainChildren.FirstOrDefault(child =>
                string.Equals(child.Level.Path, folder.Level.Path, StringComparison.OrdinalIgnoreCase));
            rows.Add(chain ?? new FolderNodeViewModel(
                folder, false, -1, GroupName, _ownTargetName, this, _list, _changed));
        }

        rows.AddRange(_chainChildren.Where(child => !rows.Contains(child)));
        Children = rows;
        ChildrenText = rows.Count == 0 ? NoFoldersText : "";
        IsListing = false;

        if (_zonedAgainst is { } chosen)
        {
            foreach (var row in rows)
            {
                row.ApplyZone(chosen);
            }
        }
    }

    /// <summary>Places this row and every row under it against the night's chosen row.</summary>
    /// <param name="chosen">The night's chosen row.</param>
    public void ApplyZone(FolderNodeViewModel chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);

        _zonedAgainst = chosen;
        Zone = ReferenceEquals(this, chosen) ? FolderZone.Chosen
            : IsUnder(chosen) ? FolderZone.Copied
            : IsChain ? FolderZone.NotCopied
            : FolderZone.Hidden;

        foreach (var child in Children)
        {
            child.ApplyZone(chosen);
        }
    }

    /// <summary>The levels of the unticked rows under this one whose ancestors up to it are all
    /// ticked. A folder under an unticked one is left out with it and is not named again, which is
    /// the top-most rule <see cref="ChosenLevel.SkippedFolders"/> documents.</summary>
    /// <remarks>Called on the chosen row only, so every row it visits is in the
    /// <see cref="FolderZone.Copied"/> zone and every tick it reads applies.</remarks>
    public IEnumerable<FolderLevel> TopMostSkipped()
    {
        foreach (var child in Children)
        {
            if (!child.IsIncluded)
            {
                yield return child.Level;
            }
            else
            {
                foreach (var level in child.TopMostSkipped())
                {
                    yield return level;
                }
            }
        }
    }

    /// <summary>A cost badge, singular and plural, or empty for none.</summary>
    internal static string Badge(int count, string singular, string plural) => count switch
    {
        <= 0 => "",
        1 => "+1 other " + singular,
        _ => string.Create(CultureInfo.InvariantCulture, $"+{count} other {plural}"),
    };

    private bool IsUnder(FolderNodeViewModel chosen)
    {
        for (var node = Parent; node is not null; node = node.Parent)
        {
            if (ReferenceEquals(node, chosen))
            {
                return true;
            }
        }

        return false;
    }

    partial void OnIsChosenChanged(bool value)
    {
        if (value)
        {
            _changed(this);
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _listRequested)
        {
            return;
        }

        _listRequested = true;
        IsListing = true;
        _list(this);
    }

    partial void OnIsIncludedChanged(bool value) => _changed(this);
}
