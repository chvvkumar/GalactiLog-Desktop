using GalactiLog.App.ViewModels.CustomColumns;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// Spec 12.4 item 2 and ruling C4's rig label row: the full-width row that opens one rig's block
/// on a multi-rig night, carrying the rig's canonical label and its own frame count.
/// </summary>
/// <remarks>
/// <para>
/// One type for both tables. The filter table and the ranges table each draw this row, and before
/// the Phase 14A fixer each carried its own private constructor, its own factory and its own copy
/// of the three properties, with the markup copied twice as well and differing only in
/// <c>Grid.ColumnSpan</c>. That was the same idiom written three times in one pass (phase review
/// P3-3, design lesson 1): the spine is built at the second occurrence, not the sixth. The two row
/// types now hold one of these and the two tables share one keyed <c>DataTemplate</c>,
/// <c>RigLabelRowTemplate</c> in <c>Theme/Controls.axaml</c>, placed by each table with its own
/// column span.
/// </para>
/// <para>
/// <see cref="FramesText"/> is the third home of the frame-count sentence as well: the two row
/// types and <c>RigViewModel</c> each spelled it out. It is written here once and read by all
/// three, so "1 frame" and "22 frames" cannot drift apart between the label row and the tooltip
/// on the box above it.
/// </para>
/// </remarks>
/// <param name="Label">The rig's canonical <c>"{telescope} / {camera}"</c> label, the one spelling
/// the frame rows, the insight prefixes, the pills and the thumbnail tooltips all carry.</param>
/// <param name="FrameCountText">"22 frames", beside the label.</param>
public sealed record RigLabelRowViewModel(string Label, string FrameCountText)
{
    /// <summary>
    /// Spec 12.15's rig-scope custom column cells, drawn as a trailing strip by
    /// <c>RigLabelRowTemplate</c>. Null, and empty, on every night that carries no rig-scope
    /// column, which is what keeps the row drawing exactly what it draws today.
    /// </summary>
    /// <remarks>
    /// Ruling C26: takes no part in this record's equality (see the <see cref="Equals"/> override
    /// below). A rig line's identity is its label and its frame count; a list of live editors is
    /// not part of what the row IS, and <c>Cells</c> is a reference-typed list compared by
    /// reference, so two rows built from equal values but separately allocated cell lists (or one
    /// with cells and one without, since ruling C23 fix pass 1 draws the strip on the filter
    /// table's row alone) must never be compared into sameness or difference by it. Fix pass 1
    /// first hit this on
    /// <c>RigPresentationTests.ARigLabelRow_IsOneSharedRowTypeInBothTables</c>, a pre-Phase-20 pin
    /// that is about the two tables sharing one row TYPE, which stays true; it does not need, and
    /// must not require, the two rows' cell lists to be the same instance or both null.
    /// </remarks>
    public IReadOnlyList<CustomValueViewModel>? Cells { get; init; }

    /// <summary>The cells, never null, so the template binds this and needs no converter.</summary>
    public IReadOnlyList<CustomValueViewModel> CellList => Cells ?? [];

    /// <summary>Whether the trailing cell strip is drawn at all. A visible strip with no items
    /// still takes the row's 8 pixel spacing, which would move every session pane layout pin on a
    /// library that has no rig-scope column.</summary>
    public bool HasCells => CellList.Count > 0;

    /// <summary>Builds the row for one rig.</summary>
    public static RigLabelRowViewModel For(
        string label, int frameCount, IReadOnlyList<CustomValueViewModel>? cells = null)
        => new(label, FramesText(frameCount)) { Cells = cells };

    /// <summary>"1 frame" or "22 frames", the one spelling of a rig's frame count.</summary>
    public static string FramesText(int count)
        => count == 1 ? "1 frame" : $"{MetricText.Count(count)} frames";

    /// <summary>
    /// Ruling C26, written out explicitly rather than left to the record's own synthesis: a
    /// record's compiler-generated equality compares every auto-implemented property, not only the
    /// ones the primary constructor declares, so declaring <see cref="Cells"/> outside the
    /// constructor is not by itself enough to keep it out of this comparison. Two rows are equal
    /// when their label and frame count are, whatever either one's <see cref="Cells"/> holds.
    /// </summary>
    public bool Equals(RigLabelRowViewModel? other)
        => other is not null
           && string.Equals(Label, other.Label, StringComparison.Ordinal)
           && string.Equals(FrameCountText, other.FrameCountText, StringComparison.Ordinal);

    /// <summary>Matches <see cref="Equals"/>: over the label and the frame count alone.</summary>
    public override int GetHashCode() => HashCode.Combine(Label, FrameCountText);
}
