using CommunityToolkit.Mvvm.ComponentModel;

namespace GalactiLog.App.ViewModels.Dashboard;

/// <summary>
/// One column of a persisted table (design-spec 5.8.2): the dashboard target list (design-spec
/// 12.2) and the Target detail frame table (design-spec 12.4). It carries the two independent
/// gates spec 5.8.2 states separately, the persisted visible-key list and the metric-group
/// toggle, and the view binds the conjunction.
/// </summary>
/// <remarks>
/// There are six of these, not seven. Spec 12.2's column list ends with "and a Sessions
/// expander", but the expander is not a persistable column key: the documented default list in
/// spec 5.8.2 holds exactly six keys, and the expander is always present. A reviewer counting to
/// seven is counting the expander.
/// </remarks>
public sealed partial class ColumnViewModel : ObservableObject
{
    public ColumnViewModel(
        string key,
        string title,
        bool isVisible,
        bool canHide = true,
        bool isGroupEnabled = true,
        bool isNumeric = false)
    {
        Key = key;
        Title = title;
        CanHide = canHide;
        IsNumeric = isNumeric;
        _isVisible = isVisible || !canHide;

        // True by default, so the dashboard's six columns and every existing construction site
        // are unchanged: only the frame table has metric groups to be gated by.
        _isGroupEnabled = isGroupEnabled;
    }

    /// <summary>The persisted key. On the dashboard: <c>name</c>, <c>designation</c>,
    /// <c>palette</c>, <c>integration</c>, <c>equipment</c> or <c>last_session</c>. On the frame
    /// table: one of <c>FrameColumns.All</c>'s 32 keys.</summary>
    public string Key { get; }

    public string Title { get; }

    /// <summary>False for <c>name</c> only (coordinator ruling Q5): hiding every column leaves a
    /// table of unidentifiable rows and spec 12.2 offers no affordance back out of it.</summary>
    public bool CanHide { get; }

    /// <summary>design-spec 14.4: a numeric column is right-aligned with tabular figures, and its
    /// header cell aligns the same way, over the figures rather than at the opposite edge. Before
    /// this existed the frame table's header template hardcoded
    /// <c>HorizontalContentAlignment="Left"</c> for all 32 columns while the row cells
    /// right-aligned 26 of them, which is why "Integration" sat at the far end of its own column
    /// (panel-density.md section 2, item 5). The dashboard's six columns default to false and are
    /// unchanged.
    /// <para>
    /// Not observable, and deliberately so: a column's kind never changes.
    /// </para></summary>
    public bool IsNumeric { get; }

    /// <summary>Whether this column is in the table's persisted visible-key list. This is what
    /// the column picker shows and what is written back to <c>display.columns</c>; it is not on
    /// its own what the table renders.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown), nameof(SizeGroup))]
    private bool _isVisible;

    /// <summary>False when this column's metric group or field flag is off (design-spec 5.8.2).
    /// Independent of <see cref="IsVisible"/>: the persisted list and the group toggle are two
    /// separate gates and the group one wins. Always true for a table with no metric groups.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown), nameof(SizeGroup))]
    private bool _isGroupEnabled;

    /// <summary>What the view binds. A column is rendered only when it is in the persisted
    /// visible list AND its metric group is enabled (design-spec 5.8.2: "A column key present in
    /// the list but hidden by its metric group being disabled stays hidden; the group toggle
    /// wins").
    /// <para>
    /// Named <c>IsShown</c> rather than <c>IsEffectivelyVisible</c> on purpose: Avalonia's
    /// <c>Control.IsEffectivelyVisible</c> carries that name, and the view tests filter rendered
    /// controls by it in the same helper methods that assert on these columns.
    /// </para>
    /// </summary>
    public bool IsShown => IsVisible && IsGroupEnabled;

    /// <summary>The shared size group this column's header cell and row cells join while it shows,
    /// and <c>null</c> while it is hidden (Phase 14C Task 3, the dashboard target list).
    /// <para>
    /// A <c>SharedSizeGroup</c> in Avalonia only ever grows: once the group has measured a column
    /// it keeps reserving that width after every cell in it goes invisible, so hiding a column left
    /// its gap behind in the header and in every row alike. A user maximum does not clamp a shared
    /// minimum either, measured twice now, so the group is what has to go: a hidden column becomes a plain <c>Auto</c> column
    /// whose only content is invisible, which measures nothing, and rejoins its group unchanged
    /// when it comes back. Derived from <see cref="IsShown"/>, so the cells' visibility and the
    /// column's width cannot disagree.
    /// </para>
    /// <para>
    /// The frame table does not bind this: its columns are fixed-width cells rather than a
    /// shared-size spine.
    /// </para></summary>
    public string? SizeGroup => IsShown ? Key : null;

    /// <summary>The active-sort marker for this column's header, set by
    /// <see cref="TargetListViewModel"/>: empty unless this column is the sort key. It lives here
    /// so the header template needs no converter and no knowledge of the sort enum.</summary>
    [ObservableProperty]
    private string _sortGlyph = "";

    /// <summary>The frame table's column width in device-independent pixels, bound by its header
    /// cell and every row cell (Phase 24 R5). Owned by <c>FrameTableViewModel</c>, which seeds it
    /// and applies the auto-fit, the drag and the stored width. NaN, Avalonia's automatic width,
    /// for the dashboard, whose columns are a shared-size spine and never bind it.</summary>
    [ObservableProperty]
    private double _width = double.NaN;
}
