using System.Globalization;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// One graded metric cell of spec 12.4's frame table: the text the column already formatted, the
/// band its deviation falls in, and the grading tooltip. Six per row, so the markup binds one
/// object per cell rather than fifteen booleans.
/// </summary>
/// <remarks>
/// <para>
/// Immutable, and not an <c>ObservableObject</c>: a "Compare to" flip replaces the six objects on
/// the row, which is six property notifications rather than thirty-six. It is the shape
/// <c>PreviewFrameViewModel</c> already uses for a snapshot.
/// </para>
/// <para>
/// There is no <c>IsNeutral</c>. Spec 12.4's band table gives neutral <c>ColorTextPrimary</c>,
/// "which is to say no mark", so the absence of the three classes is the neutral ink and a fourth
/// class would be a mark the spec refuses. Nothing here takes a tinted fill either (ruling C3).
/// </para>
/// </remarks>
public sealed class GradedCellViewModel
{
    /// <summary>An ungraded cell: the column's text, the neutral band and no tooltip. The shape a
    /// row takes before any grading has been applied to it, and the shape every cell of a frame
    /// the query returned no <c>FrameGrading</c> for keeps.</summary>
    public GradedCellViewModel(string text)
    {
        Text = text;
        Band = QualityBand.Neutral;
    }

    /// <param name="text">The cell's existing formatted text, built by the row's own formatter so
    /// one column has one format.</param>
    /// <param name="grade">The metric's deviation and the median it was measured against.</param>
    /// <param name="label">The column's own title from <c>FrameColumns</c>, read from there rather
    /// than retyped.</param>
    /// <param name="format">The column's numeric format, the same one <paramref name="text"/> was
    /// built with, so the tooltip's median reads in the cell's own format.</param>
    /// <param name="baselineWord">The literal <c>session</c> or <c>rig</c>: the baseline the
    /// deviation was actually measured against, which for the three signal metrics is always
    /// <c>session</c> whatever the toggle says.</param>
    public GradedCellViewModel(
        string text,
        MetricGrade? grade,
        string label,
        string format,
        string baselineWord)
    {
        Text = text;
        Band = FrameQuality.BandForZ(grade?.Z);

        // Spec 12.4: "A cell whose deviation is null, whose value is null, or whose baseline has
        // no median carries no grading tooltip at all, rather than one that says nothing: an empty
        // claim reads as a claim." Tooltip stays null and the markup binds ToolTip.Tip to it,
        // which Avalonia renders as no tooltip.
        if (grade?.Z is not { } z || grade.BaselineMedian is not { } median
            || text.Length == 0 || text == MetricText.Missing)
        {
            return;
        }

        // "MAD units", never sigma (ruling C2, spec 12.4): there is no 1.4826 consistency scaling
        // anywhere in this port or in the web application.
        var deviation = Math.Abs(z).ToString("0.0", CultureInfo.InvariantCulture);

        // At or above the median is "worse" and only below it is "better" (Task 3 review P3). The
        // test was z > 0, so a value exactly on the baseline median rendered "0.0 MAD units better
        // than the session median", claiming an improvement of nothing. Zero is not an
        // improvement, and a deviation of exactly zero is reachable whenever a frame carries the
        // group's own median value.
        var direction = z >= 0 ? "worse" : "better";
        var baselineMedian = median.ToString(format, CultureInfo.InvariantCulture);

        Tooltip = string.Create(
            CultureInfo.InvariantCulture,
            $"{label} {text}, {deviation} MAD units {direction} than the {baselineWord} median {baselineMedian}");
    }

    /// <summary>The cell's formatted value, the column's own rendering.</summary>
    public string Text { get; }

    /// <summary>The band spec 12.4's table puts this deviation in. Neutral for an ungraded
    /// cell.</summary>
    public QualityBand Band { get; }

    public bool IsBetter => Band == QualityBand.Better;

    public bool IsWatch => Band == QualityBand.Watch;

    public bool IsReject => Band == QualityBand.Reject;

    /// <summary>Spec 12.4's cell tooltip, or null when the cell carries no grading claim.</summary>
    public string? Tooltip { get; }

    public bool HasTooltip => Tooltip is not null;

    /// <summary>The literal baseline word spec 12.4's tooltip names, from the active
    /// toggle.</summary>
    public static string WordFor(GradingBaseline baseline)
        => baseline == GradingBaseline.Rig ? "rig" : "session";
}
