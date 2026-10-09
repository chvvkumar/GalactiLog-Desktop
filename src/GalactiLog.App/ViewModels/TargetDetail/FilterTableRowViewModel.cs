using GalactiLog.App.ViewModels.CustomColumns;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// One row of the comp's single filter table: design-spec 12.4's per-filter medians and its
/// per-filter detail rows joined by filter name, so the user reads one filter's figures in one
/// place instead of the same filter list twice 400 pixels apart (panel-density.md section 5,
/// panel-workflow.md T4).
/// </summary>
/// <remarks>
/// <para>
/// A presentation join, and the only arithmetic in it is the two totals the comp shows. No mean,
/// no median and no re-derivation: every metric here is one <c>FilterMedians</c> or one
/// <c>FilterDetailRow</c> already carried. A filter shot at one exposure is one row with all nine
/// cells filled. A filter shot at two exposures is its medians row, whose Exp cell reads the number
/// of exposures in tertiary ink and whose Frames and Int cells add up that filter's own exposure
/// rows, followed by one indented sub-row per exposure carrying that exposure's HFR, Ecc, Exp,
/// Frames and Int.
/// </para>
/// <para>
/// The two totals are sums of the rows rendered directly beneath them, taken here rather than in
/// the query's join (P12 Task 5 review ruling 8), so a reader can add the visible sub-rows up and
/// get the number above them. A count of frames and a count of seconds are exactly additive; no
/// other cell on this row would be, which is why the other five stay the medians the query
/// computed.
/// </para>
/// <para>
/// Every figure is unit free: the nine units live in the column headers, exactly as the ledger's
/// do, so a cell is a figure and a column is a unit. An absent figure is
/// <see cref="MetricText.Missing"/>, never a blank.
/// </para>
/// </remarks>
public sealed class FilterTableRowViewModel
{
    /// <summary>The joined row for a filter that has a medians row.</summary>
    /// <param name="medians">The filter's medians.</param>
    /// <param name="swatch">The filter's dot, resolved through the one filter-colour lookup
    /// (<c>ChartSelectionViewModel.FilterTint</c>), never parsed again here.</param>
    /// <param name="details">That filter's detail rows, one per exposure, in the order the query
    /// delivered them. One row fills the Exp, Frames and Int cells from itself; more than one
    /// makes the Exp cell a count and the other two the sums of the sub-rows rendered beneath;
    /// none leaves all three missing.</param>
    public FilterTableRowViewModel(
        FilterMedians medians,
        FilterSwatchViewModel swatch,
        IReadOnlyList<FilterDetailRow> details)
    {
        FilterName = medians.FilterName;
        Swatch = swatch;
        MedianHfrText = MetricText.Cell(medians.MedianHfr, "0.00");
        MedianEccentricityText = MetricText.Cell(medians.MedianEccentricity, "0.00");
        MedianFwhmText = MetricText.Cell(medians.MedianFwhm, "0.00");
        MedianGuidingRmsText = MetricText.Cell(medians.MedianGuidingRmsArcsec, "0.00");
        MedianDetectedStarsText = MetricText.Cell(medians.MedianDetectedStars, "N0");

        if (details.Count == 1)
        {
            ExposureTimeText = MetricText.Cell(details[0].ExposureTime, "0.##");
        }
        else if (details.Count > 1)
        {
            ExposureTimeText = MetricText.Count(details.Count);
            IsExposureCount = true;
        }

        if (details.Count > 0)
        {
            FrameCountText = MetricText.Count(details.Sum(row => row.FrameCount));
            IntegrationText = MetricText.Cell(details.Sum(row => row.IntegrationSeconds) / 3600d, "0.0");
        }
    }

    /// <summary>A filter's detail row on its own: either the indented sub-row of a filter shot at
    /// more than one exposure, or the whole row for a filter that has detail rows and no medians
    /// row at all, which happens when every frame of that filter has a null metric.</summary>
    /// <param name="detail">The detail row.</param>
    /// <param name="swatch">Null for a sub-row, which belongs to the filter named above it; the
    /// filter's dot for an orphan top-level row.</param>
    /// <param name="isSubRow">Whether this row is indented under a medians row.</param>
    public FilterTableRowViewModel(FilterDetailRow detail, FilterSwatchViewModel? swatch, bool isSubRow)
    {
        IsSubRow = isSubRow;
        Swatch = swatch;

        // A sub-row is named by its exposure, because the filter is the row above it. An orphan
        // row is named by its filter, because there is no row above it.
        FilterName = isSubRow
            ? MetricText.Format(detail.ExposureTime, "0.##", " s")
            : detail.FilterName;

        MedianHfrText = MetricText.Cell(detail.MedianHfr, "0.00");
        MedianEccentricityText = MetricText.Cell(detail.MedianEccentricity, "0.00");
        ExposureTimeText = MetricText.Cell(detail.ExposureTime, "0.##");
        FrameCountText = MetricText.Count(detail.FrameCount);
        IntegrationText = MetricText.Cell(detail.IntegrationSeconds / 3600d, "0.0");

        // FWHM, guiding RMS and detected stars stay missing: a FilterDetailRow carries no such
        // figure and inventing one would be arithmetic this join refuses.
    }

    // Spec 12.4 item 2's rig label row. Private, because the only legal way to build one is the
    // factory below: a label row has no filter, no swatch and no figure, and a constructor
    // overload taking two plain values would be one mistyped argument away from a filter row.
    private FilterTableRowViewModel(RigLabelRowViewModel row)
    {
        FilterName = "";
        LabelRow = row;
    }

    /// <summary>
    /// Spec 12.4 item 2 and ruling C4: the row that opens one rig's block on a multi-rig night.
    /// A full-width row carrying the rig's label in the <c>section</c> type and its frame count.
    /// A single-rig night has no label row at all.
    /// </summary>
    /// <param name="cells">Spec 12.15's rig-scope custom column cells (Phase 20 Task 6b), trailing
    /// and optional so every existing call site is unchanged. Null and empty on a library with no
    /// rig-scope column.</param>
    public static FilterTableRowViewModel RigLabelRow(
        string rigLabel, int frameCount, IReadOnlyList<CustomValueViewModel>? cells = null)
        => new(RigLabelRowViewModel.For(rigLabel, frameCount, cells));

    /// <summary>The shared label row this row is, or null on every row that is not one. The
    /// ranges table's rows hold the same type and the two tables share one
    /// <c>DataTemplate</c> over it (phase review P3-3).</summary>
    public RigLabelRowViewModel? LabelRow { get; }

    /// <summary>True on spec 12.4's rig label row, which the view renders as one full-width row
    /// instead of the table row.</summary>
    public bool IsRigLabel => LabelRow is not null;

    /// <summary>The rig's canonical label. Empty on every row that is not a label row.</summary>
    public string RigLabel => LabelRow?.Label ?? "";

    /// <summary>"22 frames", beside the label. Empty on every row that is not a label row.
    /// </summary>
    public string RigFrameCountText => LabelRow?.FrameCountText ?? "";

    /// <summary>The filter name, or the exposure on a sub-row.</summary>
    public string FilterName { get; }

    /// <summary>The filter's dot. Null on a sub-row.</summary>
    public FilterSwatchViewModel? Swatch { get; }

    /// <summary>Indented under the medians row of the same filter.</summary>
    public bool IsSubRow { get; }

    /// <summary>The Exp cell holds a count of exposures rather than one exposure, which the view
    /// renders in tertiary ink (the comp).</summary>
    public bool IsExposureCount { get; }

    public string MedianHfrText { get; } = MetricText.Missing;

    public string MedianEccentricityText { get; } = MetricText.Missing;

    public string MedianFwhmText { get; } = MetricText.Missing;

    public string MedianGuidingRmsText { get; } = MetricText.Missing;

    public string MedianDetectedStarsText { get; } = MetricText.Missing;

    public string ExposureTimeText { get; } = MetricText.Missing;

    public string FrameCountText { get; } = MetricText.Missing;

    public string IntegrationText { get; } = MetricText.Missing;
}
