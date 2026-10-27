using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GalactiLog.Core.Wbpp;

namespace GalactiLog.App.ViewModels.TargetDetail.Wbpp;

/// <summary>
/// One folder of a night's tree on spec 12.13's Folders to copy step: a chain row (one of the
/// night's levels), a disk row (a folder found inside one) or a row created for a folder the
/// calibration scan matched, with its tick, its expander, its figures and its children.
/// </summary>
/// <remarks>
/// <para>
/// Every figure is read off the <see cref="FolderLevel"/> the Core side computed. Nothing is
/// re-derived, so a row and the footer, which are computed from the same record, cannot disagree.
/// A folder the catalogue does not know states <see cref="NotCataloguedText"/> instead of a zero it
/// does not have.
/// </para>
/// <para>
/// Every row carries a tick, and the ticks alone say what is copied. A ticked row whose parent is
/// unticked, or which is a root, is a copy source. An unticked row whose parent is ticked is an
/// excluded directory of that source, and a ticked row under it is a source of its own. A ticked
/// row under a ticked parent adds nothing. Ticking a row ticks every row beneath it except the
/// folders holding only other targets' or other nights' lights; unticking one leaves the ticks
/// beneath it alone.
/// </para>
/// <para>
/// The tree is lazy. A row lists its directory the first time it is expanded, and never again,
/// so collapsing, re-expanding, unticking or re-ticking reads nothing and loses no nested tick.
/// The read is the page's, reached through the <c>list</c> callback, because this file may not
/// touch the disk and the answer has to come back on the UI thread. The rows already added under a
/// row, its chain children and any folder created for calibration, are merged into its listing in
/// place, so a folder never appears twice, and are kept when the disk does not name them.
/// </para>
/// </remarks>
public sealed partial class FolderNodeViewModel : ObservableObject
{
    /// <summary>What a folder the catalogue holds no file under reads in place of its figures.
    /// </summary>
    public const string NotCataloguedText = "not catalogued";

    /// <summary>What a folder whose children could not be read states where they would be.
    /// </summary>
    public const string UnreadableText = "Could not read the folders inside";

    /// <summary>The tag on an unticked row under a ticked one: an excluded directory.</summary>
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
    private bool _listRequested;
    private bool _seeding;
    private bool _listedEmpty;

    /// <param name="folder">The folder and its figures, as <c>FolderLevels.ForSession</c> or
    /// <c>FolderLevels.Subfolders</c> described it.</param>
    /// <param name="isChain">Whether this folder is one of the night's levels.</param>
    /// <param name="index">The level's position in <c>SessionLevels.Levels</c>, or -1 for a disk
    /// row.</param>
    /// <param name="ticked">The row's starting tick, set without a report. An unticked row that
    /// would not start ticked on its own facts carries its reason.</param>
    /// <param name="ownTargetName">The page's own target's display name, which decides whether an
    /// other-target entry needs <see cref="SameNameSuffix"/>.</param>
    /// <param name="parent">The row this one sits inside, or null for a root.</param>
    /// <param name="list">Called once, the first time this row is expanded, so the page reads the
    /// folders inside and answers through <see cref="SetChildren"/>.</param>
    /// <param name="changed">Called when the user moves this row's tick, and when a listing under
    /// a ticked row lands an unticked row.</param>
    public FolderNodeViewModel(
        SubfolderLevel folder,
        bool isChain,
        int index,
        bool ticked,
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
        Parent = parent;
        _ownTargetName = ownTargetName;
        _list = list;
        _changed = changed;
        FolderName = WbppPathText.OwnName(Level.Path);

        _isIncluded = ticked;
        _reasonTag = ticked || StartsTicked(Level) ? null : ReasonFor(Level);
    }

    /// <summary>Whether a folder starts ticked: false only when it holds none of this night's own
    /// frames and some other target's or other night's.</summary>
    public static bool StartsTicked(FolderLevel level)
        => !(level.FrameCount == 0 && (level.OtherTargets.Count > 0 || level.OtherNights.Count > 0));

    /// <summary>Why a folder starts unticked: the first other target's lights, else another night,
    /// else null.</summary>
    public static string? ReasonFor(FolderLevel level)
        => level.OtherTargets.Count > 0 ? level.OtherTargets[0] + "'s lights"
            : level.OtherNights.Count > 0 ? "other night"
            : null;

    /// <summary>Why this row started unticked, or null; cleared for good once the user ticks it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReasonTag))]
    [NotifyPropertyChangedFor(nameof(RowTag))]
    private string? _reasonTag;

    /// <summary>Whether this row carries a reason.</summary>
    public bool HasReasonTag => ReasonTag is not null;

    /// <summary>The tag an unticked row reads: its reason, else <see cref="SkippedText"/> when it
    /// is an excluded directory, else nothing.</summary>
    public string RowTag => IsIncluded ? "" : ReasonTag ?? (IsSkipped ? SkippedText : "");

    /// <summary>The tick this row takes when an ancestor is ticked: its calibration row's default
    /// when it carries one, else its catalogue facts.</summary>
    public bool DefaultTick => Row is { } row ? row.DefaultTicked : StartsTicked(Level);

    // Other targets' and other nights' LIGHT files under this folder, as the page read them.
    private int _foreignCount;

    /// <summary>Records how many foreign LIGHT files lie under this folder, which turns the frame
    /// count into "k of n files".</summary>
    public void SetForeignCount(int count)
    {
        if (_foreignCount == count)
        {
            return;
        }

        _foreignCount = count;
        OnPropertyChanged(nameof(FrameCountText));
    }

    /// <summary>The folder this row draws, which is what an untick puts on its source as an
    /// excluded directory.</summary>
    public FolderLevel Level { get; }

    /// <summary>Whether the catalogue holds any file under this folder.</summary>
    public bool IsCatalogued { get; }

    /// <summary>Whether this folder is one of the night's levels.</summary>
    public bool IsChain { get; }

    /// <summary>This row's position in its night's level list, or -1 for a disk row.</summary>
    public int Index { get; }

    /// <summary>The folder's own name, the last component of <see cref="FolderLevel.Path"/>.
    /// </summary>
    public string FolderName { get; }

    /// <summary>The row this one sits inside, or null for a root.</summary>
    public FolderNodeViewModel? Parent { get; }

    /// <summary>The night's own LIGHT frames under this folder, or <see cref="NotCataloguedText"/>;
    /// empty for a folder the catalogue does not know that carries calibration, because
    /// <see cref="CalibrationText"/> stands in its place.</summary>
    public string FrameCountText => !IsCatalogued ? (HasCalibration ? "" : NotCataloguedText)
        : _foreignCount > 0 ? string.Create(
            CultureInfo.InvariantCulture, $"{Level.FrameCount:N0} of {Level.FrameCount + _foreignCount:N0} files")
        : WbppPathText.Frames(Level.FrameCount);

    /// <summary>The total size of every catalogued file under this folder, or the literal
    /// <c>unknown</c> when any one contributing size is null, never a partial sum. Empty when the
    /// catalogue does not know the folder, because the frame count already says so.</summary>
    public string BytesText => IsCatalogued ? WbppPathText.Bytes(Level.SubtreeBytes) : "";

    /// <summary>The matcher's row for this folder, or null when it holds no calibration file the
    /// matcher saw.</summary>
    public CalibrationRow? Row { get; private set; }

    /// <summary>Sets or clears this folder's calibration row.</summary>
    public void SetCalibrationRow(CalibrationRow? row)
    {
        Row = row;
        OnPropertyChanged(nameof(Row));
        OnPropertyChanged(nameof(NoteTag));
        OnPropertyChanged(nameof(HasNoteTag));
        RefreshCalibrationText();
    }

    /// <summary>Raises the calibration figures again, for an interior row once the rows beneath it
    /// have changed.</summary>
    internal void RefreshCalibrationText()
    {
        OnPropertyChanged(nameof(CalibrationText));
        OnPropertyChanged(nameof(HasCalibration));
        OnPropertyChanged(nameof(FrameCountText));
    }

    /// <summary>The calibration this row copies, read off its <see cref="CalibrationRow"/>: "k of
    /// n files" and the selected bytes when the matcher selected some of its files, else every
    /// file and its bytes. A row without a calibration row of its own sums the rows beneath it,
    /// each by its selection when it has one, else by its totals. Empty when there is no
    /// calibration here.</summary>
    public string CalibrationText
    {
        get
        {
            if (Row is { } row)
            {
                return row.SelectedCount > 0
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"{row.SelectedCount:N0} of {row.TotalCount:N0} files, {WbppPathText.Bytes(row.SelectedBytes)}")
                    : Files(row.TotalCount, row.TotalBytes);
            }

            var rows = Descendants().Select(node => node.Row).OfType<CalibrationRow>().ToList();
            return rows.Count == 0 ? "" : Files(
                rows.Sum(r => r.SelectedCount > 0 ? r.SelectedCount : r.TotalCount),
                rows.Sum(r => r.SelectedCount > 0 ? r.SelectedBytes : r.TotalBytes));
        }
    }

    /// <summary>Whether this row states calibration.</summary>
    public bool HasCalibration => CalibrationText.Length > 0;

    /// <summary>The matcher's note on this folder's calibration row, drawn as a tag.</summary>
    public string NoteTag => Row?.Note ?? "";

    /// <summary>Whether this row carries the match note.</summary>
    public bool HasNoteTag => Row is not null;

    private static string Files(int count, long bytes) => count == 1
        ? "1 file, " + WbppPathText.Bytes(bytes)
        : string.Create(CultureInfo.InvariantCulture, $"{count:N0} files, {WbppPathText.Bytes(bytes)}");

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

    /// <summary>This row's tick. Two-way from the check box; the user's write reports through
    /// <c>changed</c>, a seed does not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSkipped))]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    [NotifyPropertyChangedFor(nameof(RowTag))]
    private bool _isIncluded;

    /// <summary>Whether this row's children are open. The first opening lists them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandLabel))]
    [NotifyPropertyChangedFor(nameof(ShowsChildren))]
    private bool _isExpanded;

    /// <summary>The expander's glyph: <c>+</c> while closed, <c>-</c> while open.</summary>
    public string ExpandLabel => IsExpanded ? "-" : "+";

    /// <summary>Whether the children are on screen: while open, whatever the ticks, except on a
    /// leaf, which has none.</summary>
    public bool ShowsChildren => IsExpanded && !IsLeaf;

    /// <summary>Whether this folder was listed and holds no folder, with no row added beneath it
    /// since.</summary>
    public bool IsLeaf => _listedEmpty && Children.Count == 0;

    /// <summary>Whether this row draws its expander: every row but a leaf.</summary>
    public bool ShowsExpander => !IsLeaf;

    /// <summary>Whether this row's directory is being read.</summary>
    [ObservableProperty]
    private bool _isListing;

    /// <summary><see cref="UnreadableText"/>, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChildrenText))]
    private string _childrenText = "";

    /// <summary>Whether a sentence stands where the children would be.</summary>
    public bool HasChildrenText => ChildrenText.Length > 0;

    /// <summary>The folders inside this one: the rows added through <see cref="AddChild"/> until
    /// listed, then the listing in the lister's order with those rows merged in place.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLeaf))]
    [NotifyPropertyChangedFor(nameof(ShowsExpander))]
    [NotifyPropertyChangedFor(nameof(ShowsChildren))]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    private IReadOnlyList<FolderNodeViewModel> _children = [];

    /// <summary>Every row under this one, depth first.</summary>
    public IEnumerable<FolderNodeViewModel> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var node in child.Descendants())
            {
                yield return node;
            }
        }
    }

    /// <summary>Whether any row under this one is ticked.</summary>
    public bool HasTickedDescendant => Descendants().Any(node => node.IsIncluded);

    /// <summary>The levels directly inside this one.</summary>
    public IReadOnlyList<FolderNodeViewModel> ChainChildren => _chainChildren;

    /// <summary>Whether this row is an excluded directory: unticked under a ticked parent.
    /// </summary>
    public bool IsSkipped => !IsIncluded && Parent is { IsIncluded: true };

    /// <summary>The row's opacity: dimmed when unticked with no ticked row beneath it. An unticked
    /// row on the path to a ticked one is path, not a decision, and is not dimmed.</summary>
    public double RowOpacity => IsIncluded || HasTickedDescendant ? 1.0 : 0.55;

    /// <summary>Opens and closes this row's children.</summary>
    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    /// <summary>Adds a row directly inside this one: a level while the session builds the chain,
    /// or a folder created for calibration. A later listing merges it in place.</summary>
    /// <returns>The row now in the tree for that folder: <paramref name="child"/>, or the child
    /// already there under the same path ignoring case, which is never added twice.</returns>
    internal FolderNodeViewModel AddChild(FolderNodeViewModel child)
    {
        if (Children.FirstOrDefault(node =>
            string.Equals(node.Level.Path, child.Level.Path, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            return existing;
        }

        if (child.IsChain)
        {
            _chainChildren.Add(child);
        }

        Children = [.. Children, child];
        return child;
    }

    /// <summary>Drops a row directly inside this one.</summary>
    internal void RemoveChild(FolderNodeViewModel child)
    {
        _chainChildren.Remove(child);
        Children = [.. Children.Where(node => !ReferenceEquals(node, child))];
    }

    /// <summary>Takes the page's answer to the listing. Called on the UI thread.</summary>
    /// <param name="listed">The folders inside, or null when they could not be read.</param>
    /// <remarks>A listed folder that is already a child, by path ignoring case, is that child, never
    /// a second row; a child the listing did not name follows the listing. An unreadable directory
    /// keeps its children, so the night's levels always show. An empty listing over no child makes
    /// this row a leaf. A new row starts ticked when this row is ticked and its own facts allow it,
    /// so a listing under a ticked row lands ticked and one under an unticked row lands unticked.
    /// </remarks>
    public void SetChildren(IReadOnlyList<SubfolderLevel>? listed)
    {
        if (listed is null)
        {
            ChildrenText = UnreadableText;
            IsListing = false;
            return;
        }

        var rows = new List<FolderNodeViewModel>(listed.Count + Children.Count);
        var built = new List<FolderNodeViewModel>(listed.Count);
        foreach (var folder in listed)
        {
            var existing = Children.FirstOrDefault(child =>
                string.Equals(child.Level.Path, folder.Level.Path, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new FolderNodeViewModel(
                    folder, false, -1, IsIncluded && StartsTicked(folder.Level), _ownTargetName, this, _list, _changed);
                built.Add(existing);
            }

            rows.Add(existing);
        }

        rows.AddRange(Children.Where(child => !rows.Contains(child)));
        _listedEmpty = listed.Count == 0;

        Children = rows;
        ChildrenText = "";
        IsListing = false;

        // A listed row landing unticked under a ticked one is an excluded directory the session
        // has not heard of yet.
        if (IsIncluded && built.Any(row => !row.IsIncluded))
        {
            _changed(this);
        }
    }

    /// <summary>Sets this row's tick and its reason as the constructor does, without a report and
    /// without touching the rows beneath it.</summary>
    internal void Seed(bool ticked)
    {
        _seeding = true;
        try
        {
            IsIncluded = ticked;
        }
        finally
        {
            _seeding = false;
        }

        ReasonTag = ticked || StartsTicked(Level) ? null : ReasonFor(Level);
    }

    /// <summary>Seeds every row beneath this one, depth first: a row carrying a calibration row by
    /// its default wherever it sits; any other row by its own facts while its parent is ticked,
    /// else unticked, so the rows under a folder the foreign-lights rule unticks are not drawn
    /// ticked.</summary>
    internal void SeedBeneath()
    {
        foreach (var child in Children)
        {
            child.Seed((IsIncluded || child.Row is not null) && child.DefaultTick);
            child.SeedBeneath();
        }
    }

    /// <summary>A cost badge, singular and plural, or empty for none.</summary>
    internal static string Badge(int count, string singular, string plural) => count switch
    {
        <= 0 => "",
        1 => "+1 other " + singular,
        _ => string.Create(CultureInfo.InvariantCulture, $"+{count} other {plural}"),
    };

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            RequestList();
        }
    }

    // The one latch: lists on the first expand, whatever the ticks, and never again.
    private void RequestList()
    {
        if (_listRequested)
        {
            return;
        }

        _listRequested = true;
        IsListing = true;
        _list(this);
    }

    partial void OnIsIncludedChanged(bool value)
    {
        // A child's excluded-directory state reads this row's tick.
        foreach (var child in Children)
        {
            child.OnPropertyChanged(nameof(IsSkipped));
            child.OnPropertyChanged(nameof(RowTag));
        }

        if (!_seeding && value)
        {
            ReasonTag = null;
            SeedBeneath();
        }

        for (var node = Parent; node is not null; node = node.Parent)
        {
            node.OnPropertyChanged(nameof(HasTickedDescendant));
            node.OnPropertyChanged(nameof(RowOpacity));
        }

        if (!_seeding)
        {
            _changed(this);
        }
    }
}
