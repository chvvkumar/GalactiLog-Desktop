using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Wbpp;

namespace GalactiLog.App.ViewModels.TargetDetail.Wbpp;

/// <summary>
/// One folder in a night's skip panel: its tick, its expander, its catalogue figures and, once
/// listed, the folders inside it.
/// </summary>
/// <remarks>
/// <para>
/// The tree is lazy. A row lists its children the first time it is expanded and never again: a
/// row that has listed keeps what it listed, so collapsing and re-expanding, or unticking and
/// re-ticking, reads nothing and loses no nested tick. The read itself is the page's, reached
/// through the <c>list</c> callback, because this file may not touch the disk and the result has
/// to come back on the UI thread.
/// </para>
/// <para>
/// Every figure is read off the <see cref="SubfolderLevel"/> the Core side computed, as the level
/// rows are, so the panel and the footer cannot disagree about a folder. A folder the catalogue
/// does not know states <see cref="NotCataloguedText"/> instead of a zero it does not have.
/// </para>
/// </remarks>
public sealed partial class SubfolderRowViewModel : ObservableObject
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

    private readonly Action<SubfolderRowViewModel> _list;
    private readonly Action _changed;
    private readonly string _ownTargetName;
    private bool _listRequested;

    /// <param name="folder">The folder and its figures, as <c>FolderLevels.Subfolders</c>
    /// described it.</param>
    /// <param name="ownTargetName">The page's own target's display name, for the same-name note
    /// on an other-target entry.</param>
    /// <param name="list">Called once, the first time this row is expanded, so the page reads
    /// the folders inside and answers through <see cref="SetChildren"/>.</param>
    /// <param name="changed">Called when this row's tick, or a tick anywhere under it, moves.
    /// </param>
    public SubfolderRowViewModel(
        SubfolderLevel folder,
        string ownTargetName,
        Action<SubfolderRowViewModel> list,
        Action changed)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(changed);

        Level = folder.Level;
        IsCatalogued = folder.IsCatalogued;
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

    /// <summary>The folder's own name, the last component of <see cref="FolderLevel.Path"/>.
    /// </summary>
    public string FolderName { get; }

    /// <summary>The night's own LIGHT frames under this folder, or <see cref="NotCataloguedText"/>.
    /// </summary>
    public string FrameCountText => IsCatalogued ? WbppPathText.Frames(Level.FrameCount) : NotCataloguedText;

    /// <summary>The catalogued bytes under this folder, or empty when the catalogue does not know
    /// it, because the frame count already says so.</summary>
    public string BytesText => IsCatalogued ? WbppPathText.Bytes(Level.SubtreeBytes) : "";

    /// <summary>Whether this folder holds another night's frames.</summary>
    public bool HasOtherNights => Level.OtherNights.Count > 0;

    /// <summary>Whether this folder holds another target's frames.</summary>
    public bool HasOtherTargets => Level.OtherTargets.Count > 0;

    /// <summary>The other-nights badge, in the level tree's own wording.</summary>
    public string OtherNightsBadge => LevelRowViewModel.Badge(Level.OtherNights.Count, "night", "nights");

    /// <summary>The other-targets badge, in the level tree's own wording.</summary>
    public string OtherTargetsBadge => LevelRowViewModel.Badge(Level.OtherTargets.Count, "target", "targets");

    /// <summary>The nights themselves, for the badge's tooltip.</summary>
    public string OtherNightsTooltip => string.Join(", ", Level.OtherNights);

    /// <summary>The targets themselves, for the badge's tooltip, with
    /// <see cref="LevelRowViewModel.SameNameSuffix"/> on an entry named as this page's target.
    /// </summary>
    public string OtherTargetsTooltip => string.Join(
        ", ",
        Level.OtherTargets.Select(name =>
            string.Equals(name, _ownTargetName, StringComparison.OrdinalIgnoreCase)
                ? name + LevelRowViewModel.SameNameSuffix
                : name));

    /// <summary>Whether this folder is copied. Unticked, it and everything under it is left out.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsChildren))]
    private bool _isIncluded = true;

    /// <summary>Whether this row's children are open. The first opening lists them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandLabel))]
    [NotifyPropertyChangedFor(nameof(ShowsChildren))]
    private bool _isExpanded;

    /// <summary>The expander's glyph: <c>+</c> while closed, <c>-</c> while open.</summary>
    public string ExpandLabel => IsExpanded ? "-" : "+";

    /// <summary>Whether the children are on screen: only while open and ticked, because the
    /// folders under an unticked one are left out with it.</summary>
    public bool ShowsChildren => IsExpanded && IsIncluded;

    /// <summary>Whether this row's children are being read.</summary>
    [ObservableProperty]
    private bool _isListing;

    /// <summary><see cref="NoFoldersText"/>, <see cref="UnreadableText"/>, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChildrenText))]
    private string _childrenText = "";

    /// <summary>Whether a sentence stands where the children would be.</summary>
    public bool HasChildrenText => ChildrenText.Length > 0;

    /// <summary>The folders inside this one, in the lister's order. Empty until listed.</summary>
    [ObservableProperty]
    private IReadOnlyList<SubfolderRowViewModel> _children = [];

    /// <summary>Opens and closes this row's children.</summary>
    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    /// <summary>Takes the page's answer to the listing. Called on the UI thread.</summary>
    /// <param name="listed">The folders inside, or null when they could not be read.</param>
    public void SetChildren(IReadOnlyList<SubfolderLevel>? listed)
    {
        if (listed is null)
        {
            ChildrenText = UnreadableText;
        }
        else if (listed.Count == 0)
        {
            ChildrenText = NoFoldersText;
        }
        else
        {
            ChildrenText = "";
            Children = [.. listed.Select(folder => new SubfolderRowViewModel(folder, _ownTargetName, _list, _changed))];
        }

        IsListing = false;
    }

    /// <summary>The levels of the unticked folders under this row whose ancestors up to it are all
    /// ticked. A folder under an unticked one is left out with it and is not named again, which is
    /// the top-most rule <see cref="ChosenLevel.SkippedFolders"/> documents.</summary>
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

    partial void OnIsIncludedChanged(bool value) => _changed();
}
