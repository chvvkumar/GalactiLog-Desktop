using GalactiLog.App.ViewModels.TargetDetail;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.Preview;

/// <summary>
/// One badge of spec 11.5's metadata strip (PAR-011): a label, a value, and the grading marks the
/// frame table's own cell already carries.
/// </summary>
/// <remarks>
/// Immutable and not an <c>ObservableObject</c>, for the same reason
/// <see cref="PreviewFrameViewModel"/> is: the strip is rebuilt from the frame on screen rather
/// than mutated in place, and stepping replaces the whole list through <c>Current</c>.
/// <para>
/// Nothing here computes a band. The band and the tooltip are handed over by the frame row the
/// modal was opened from, which is spec 11.5's "the modal does not grade anything itself".
/// </para>
/// </remarks>
/// <param name="Label">The frame table's own column header, read from
/// <see cref="FrameColumns"/> rather than retyped, so a badge and a table header cannot disagree.
/// </param>
/// <param name="Value">The frame table's own formatted cell text, so a number cannot read
/// differently in two places.</param>
/// <param name="Band">Spec 12.4's band for this metric on this frame.
/// <see cref="QualityBand.Neutral"/> for an ungraded badge and for the two identity badges, which
/// are never graded.</param>
/// <param name="IsWorst">Spec 11.5: a badge in the watch or reject band additionally renders its
/// value in <c>metric-worst</c>.</param>
/// <param name="Tooltip">The frame table cell's own deviation sentence, or null when the cell
/// carries none. A null tooltip renders no tooltip rather than a neutral claim.</param>
public sealed record PreviewBadge(
    string Label, string Value, QualityBand Band, bool IsWorst, string? Tooltip)
{
    /// <summary>Spec 12.4's better band. Neutral draws no mark at all, so there is no
    /// <c>IsNeutral</c>: the absence of the three classes is the neutral ink.</summary>
    public bool IsBetter => Band == QualityBand.Better;

    public bool IsWatch => Band == QualityBand.Watch;

    public bool IsReject => Band == QualityBand.Reject;
}

/// <summary>
/// One entry of spec 11.5's navigation list: the identity of a frame the preview modal can show,
/// and the read model its header panel needs.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="FrameRowViewModel"/>. A row carries 32 formatted columns, a column
/// list and a raw header panel of its own, all of which belong to spec 12.4's table. Keeping the
/// list on this type is what lets a later caller (a Phase 9 page with no frame table) hand the same
/// service a list without building 32-column rows first: <see cref="FrameRow"/> is a
/// <c>Data.Queries</c> read model, so this type stays off the view-model layer entirely.
/// <para>
/// Immutable and not an <c>ObservableObject</c>: the list is a snapshot taken when the modal
/// opens and nothing in it changes while the modal is open.
/// </para>
/// </remarks>
/// <param name="ImageId"><c>images.id</c>, which is what <c>FrameHeadersQuery.Get</c> takes.</param>
/// <param name="FilePath">The absolute frame path. Rendered through the thumbnail cache and handed
/// to <c>ShellIntegration</c>; nothing in the modal opens it.</param>
/// <param name="FileName">The frame's file name, for the window's caption.</param>
/// <param name="Row">The frame's read model, which is what the header panel's Derived metrics
/// section renders (spec 12.4, spec 7.3): <c>FrameHeadersQuery</c> deliberately selects only
/// <c>raw_headers</c>, <c>provenance</c> and the two FWHM values, so without this the section would
/// be empty and no provenance string would appear. Null renders that section empty, which is what a
/// caller holding only an image id gets.</param>
public sealed record PreviewFrameViewModel(
    Guid ImageId, string FilePath, string FileName, FrameRow? Row = null)
{
    /// <summary>Spec 11.5's metadata strip for this frame, in the spec's fixed order: filter,
    /// exposure, HFR, eccentricity, FWHM, detected stars, guiding RMS. A badge whose value is null
    /// is absent from the list rather than present and empty, so a frame with no guiding leaves no
    /// empty badge.
    /// <para>
    /// Empty for a frame built from anything but a frame table row. The values are the table's own
    /// cell text and the bands are the table's own grading, so a caller holding only an image id
    /// gets no strip rather than a second formatting of the same numbers.
    /// </para></summary>
    public IReadOnlyList<PreviewBadge> Badges { get; init; } = [];

    /// <summary>Projects a frame table row (spec 12.4) into a navigation entry. The one place the
    /// two types meet, and therefore the one place the strip is built.</summary>
    public static PreviewFrameViewModel From(FrameRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(row.ImageId, row.FilePath, row.FileName, row.Row) { Badges = StripFor(row) };
    }

    // Spec 11.5's seven badges in their fixed order. There is no verdict badge: the web's verdict
    // comes from its stacking-export gates, which this port does not ship (task1-report.md
    // departure 5). There is no session badge either: the page the modal opens from shows one
    // night at a time.
    private static IReadOnlyList<PreviewBadge> StripFor(FrameRowViewModel row)
    {
        List<PreviewBadge> badges = [];

        // The two identity badges. Never graded: spec 12.4's band table grades metrics, and a
        // filter name is not a metric.
        Add(badges, "filter_used", row.FilterText);
        Add(badges, "exposure_time", row.ExposureText);

        // The five graded badges. Every one reads Task 3's graded cell, which already carries the
        // text, the band and the deviation sentence the frame table's own cell shows. Nothing is
        // recomputed: spec 11.5's "the modal does not grade anything itself".
        AddGraded(badges, "median_hfr", row.HfrCell);
        AddGraded(badges, "eccentricity", row.EccentricityCell);
        AddGraded(badges, "fwhm", row.FwhmCell);
        AddGraded(badges, "detected_stars", row.DetectedStarsCell);
        AddGraded(badges, "guiding_rms_arcsec", row.GuidingRmsCell);

        return badges;
    }

    private static void Add(List<PreviewBadge> badges, string columnKey, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        badges.Add(new(LabelFor(columnKey), value, QualityBand.Neutral, IsWorst: false, Tooltip: null));
    }

    // The graded half of the strip, straight off Task 3's cell. A frame opened from a surface with
    // no grading, and a frame the grading left ungraded, arrives here as a neutral cell with a null
    // tooltip, which is exactly what spec 11.5 asks for: the value, no band and no tooltip rather
    // than a neutral claim.
    //
    // Spec 11.5: "A badge in the watch or reject band additionally renders its value in
    // metric-worst." Better and neutral are not worst, so the two bands the sentence names are the
    // two this reads.
    private static void AddGraded(List<PreviewBadge> badges, string columnKey, GradedCellViewModel cell)
    {
        if (string.IsNullOrWhiteSpace(cell.Text))
        {
            return;
        }

        badges.Add(new(
            LabelFor(columnKey), cell.Text, cell.Band, cell.IsWatch || cell.IsReject, cell.Tooltip));
    }

    private static string LabelFor(string columnKey)
        => FrameColumns.All.First(column => column.Key == columnKey).Title;
}
