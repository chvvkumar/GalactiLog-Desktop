using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Wbpp;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.TargetDetail.Wbpp;

/// <summary>
/// One metric cell of spec 12.13's verdict table: the value at the metric's own decimals, the band
/// its deviation falls in, whether this frame failed that gate, and the mark that sits in the
/// cell's fixed trailing slot.
/// </summary>
/// <remarks>
/// <para>
/// Immutable, and not an <see cref="ObservableObject"/>: a baseline flip, a constraint change or a
/// master-switch flip replaces the five objects on the row, which is five property notifications
/// rather than twenty-five. It is the shape <see cref="GradedCellViewModel"/> already uses for the
/// frame table's cells.
/// </para>
/// <para>
/// <b>The band is read, never recomputed</b> (W10). The deviation comes from the
/// <see cref="FrameGrading"/> <c>SessionDetailQuery</c> already computed for both baselines, and
/// <see cref="FrameQuality.BandForZ"/> is the one band ladder in this solution, the same member
/// <see cref="GradedCellViewModel"/> calls. No threshold, no band table and no group baseline is
/// declared here.
/// </para>
/// <para>
/// A null grade, a null deviation inside one and a metric that carries no baseline at all
/// (guiding RMS) are all the neutral band, which is the primary ink and no class. That is not an
/// error and not a fall back to another metric's grade.
/// </para>
/// </remarks>
public sealed class VerdictCellViewModel
{
    /// <summary>Spec 12.13's failure mark: "a mark in a fixed trailing slot present on every cell,
    /// so the digits keep one right edge whether a row failed or not". The slot is drawn on every
    /// cell and this is the string it carries when the frame failed that gate. The same glyph
    /// <c>ActivityRowViewModel</c> already uses for an error.</summary>
    public const string FailureMark = "×";

    /// <param name="text">The value at the metric's own decimals, or the absent-metric placeholder.</param>
    /// <param name="grade">The deviation the session query computed against the selected baseline,
    /// or null for a frame with no grading and for guiding RMS, which has no baseline.</param>
    /// <param name="failureText">The gate's own failure sentence when this frame violated it and
    /// the filter is on, else null.</param>
    public VerdictCellViewModel(string text, MetricGrade? grade, string? failureText)
    {
        Text = text;
        Band = FrameQuality.BandForZ(grade?.Z);
        Tooltip = failureText;
    }

    /// <summary>The cell's formatted value, or the placeholder when the frame carries no figure for
    /// this metric.</summary>
    public string Text { get; }

    /// <summary>The band spec 12.4's ladder puts this deviation in, neutral for an ungraded
    /// cell.</summary>
    public QualityBand Band { get; }

    public bool IsBetter => Band == QualityBand.Better;

    public bool IsWatch => Band == QualityBand.Watch;

    public bool IsReject => Band == QualityBand.Reject;

    /// <summary>Spec 12.13: "A metric cell that failed a gate is drawn in the error ink". The view
    /// declares that ink after the three band inks, so it beats them whatever the band says
    /// (HANDOFF 5.2 item 22).</summary>
    public bool IsFailed => Tooltip is not null;

    /// <summary>What the cell's trailing slot carries: the mark on a failed gate, the empty string
    /// otherwise. The slot itself is present either way.</summary>
    public string Mark => IsFailed ? FailureMark : "";

    /// <summary>The failed gate's own failure sentence, or null, which Avalonia renders as no
    /// tooltip.</summary>
    public string? Tooltip { get; }

    public bool HasTooltip => Tooltip is not null;
}

/// <summary>
/// One row of spec 12.13's verdict table: the Copy override, the verdict word and its ink, the
/// filter, the five graded metric cells, the file with its path beneath it, and the night.
/// </summary>
/// <remarks>
/// <para>
/// One row per frame for the panel's whole life, refreshed in place through <see cref="Apply"/>
/// rather than rebuilt. Two reasons: the Copy check box is bound two way, and replacing the object
/// under it turns a click into a re-realised control with a stale binding; and an override
/// "survives a re-sort" (spec 12.13), which is trivially true when the sort reorders the same
/// objects.
/// </para>
/// <para>
/// The row holds no override of its own. <see cref="IsCopied"/> is a view surface over the panel's
/// one override map, which is keyed by <see cref="WbppFrame.ImageId"/> and is the only record of
/// what the user reversed.
/// </para>
/// </remarks>
public sealed partial class VerdictRowViewModel : ObservableObject
{
    private readonly Action<VerdictRowViewModel> _toggled;

    // True while Apply is publishing the panel's answer, so re-rendering a row does not feed that
    // answer back into the override map as though the user had clicked it.
    private bool _applying;

    /// <param name="frame">The frame this row judges. Read for its sort keys and its file
    /// identity; never mutated.</param>
    /// <param name="index">The row's position in the panel's own frame order, which is the final
    /// tie-break of every sort so two runs of one sort draw the same order.</param>
    /// <param name="toggled">Raised when the user clicks the Copy check box, on the UI thread.</param>
    public VerdictRowViewModel(WbppFrame frame, int index, Action<VerdictRowViewModel> toggled)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(toggled);

        Frame = frame;
        Index = index;
        _toggled = toggled;

        NightText = MetricText.Date(frame.Night);
        FilterText = string.IsNullOrEmpty(frame.FilterUsed) ? MetricText.Missing : frame.FilterUsed;

        // The web sorts a missing filter as the empty string, which is what puts the unfiltered
        // frames at one end rather than scattering them; the placeholder above is a rendering.
        FilterKey = frame.FilterUsed ?? "";

        _verdictText = "";
        _verdictTooltip = "";
        _hfrCell = Blank();
        _eccentricityCell = Blank();
        _fwhmCell = Blank();
        _detectedStarsCell = Blank();
        _guidingRmsCell = Blank();
    }

    /// <summary>The frame this row judges.</summary>
    public WbppFrame Frame { get; }

    /// <summary>The row's position in the panel's own frame order.</summary>
    public int Index { get; }

    /// <summary>The override key, and the accessible identity of the Copy check box.</summary>
    public Guid ImageId => Frame.ImageId;

    /// <summary>The file name (spec 12.13's File column, first line).</summary>
    public string FileName => Frame.FileName;

    /// <summary>The full path, drawn beneath the name in the tertiary ink "because one name can
    /// live in several folders" (spec 12.13).</summary>
    public string FilePath => Frame.FilePath;

    /// <summary>The session date as <c>yyyy-MM-dd</c>, invariant.</summary>
    public string NightText { get; }

    /// <summary>The frame's filter, or the application's absent-metric placeholder.</summary>
    public string FilterText { get; }

    /// <summary>What the Filter column sorts on: the filter name, with null as the empty
    /// string.</summary>
    public string FilterKey { get; }

    /// <summary>
    /// Spec 12.13's Copy column: the per-row override. It "starts at the verdict's own answer and a
    /// click reverses it for that frame alone", and it works in both directions, so a Copy frame
    /// ticked off is excluded and an Exclude or Unmeasured frame ticked on is copied.
    /// </summary>
    [ObservableProperty]
    private bool _isCopied;

    /// <summary>The verdict word: <c>Copy</c>, <c>Unmeasured</c> or <c>Exclude</c>. A departure
    /// from the web, which draws three glyphs; spec 12.13 states the words, so the words
    /// ship.</summary>
    [ObservableProperty]
    private string _verdictText;

    /// <summary>The verdict's tooltip: <c>"Copy"</c>, <c>"Unmeasured: none of the constrained
    /// metrics recorded"</c>, or <c>"Exclude: "</c> and the failure sentences joined by
    /// <c>", "</c>.</summary>
    [ObservableProperty]
    private string _verdictTooltip;

    /// <summary>Drives the success ink on the verdict word.</summary>
    [ObservableProperty]
    private bool _isVerdictCopy;

    /// <summary>Drives the warning ink on the verdict word.</summary>
    [ObservableProperty]
    private bool _isVerdictUnmeasured;

    /// <summary>Drives the error ink on the verdict word.</summary>
    [ObservableProperty]
    private bool _isVerdictExclude;

    /// <summary>The HFR cell, graded against the selected baseline.</summary>
    [ObservableProperty]
    private VerdictCellViewModel _hfrCell;

    /// <summary>The eccentricity cell, graded against the selected baseline.</summary>
    [ObservableProperty]
    private VerdictCellViewModel _eccentricityCell;

    /// <summary>The FWHM cell, graded against the selected baseline.</summary>
    [ObservableProperty]
    private VerdictCellViewModel _fwhmCell;

    /// <summary>The detected stars cell. Spec 12.13: always graded against the frame's own night
    /// whichever baseline is selected, "because it drifts with the sky".</summary>
    [ObservableProperty]
    private VerdictCellViewModel _detectedStarsCell;

    /// <summary>The guiding RMS cell. Spec 12.13: it "has no baseline and is never coloured", so
    /// this cell is always the neutral band and reads the primary ink.</summary>
    [ObservableProperty]
    private VerdictCellViewModel _guidingRmsCell;

    /// <summary>
    /// Publishes the panel's current answer for this frame: the effective inclusion, the verdict
    /// word and ink, and the five cells against the selected baseline.
    /// </summary>
    /// <param name="verdict">This frame's verdict from <c>QualityFilter.EvaluateAll</c>.</param>
    /// <param name="included">What <c>QualityFilter.IsIncluded</c> answers for it, overrides and
    /// the master switch included. This is what the Copy box shows and what the verdict word reads
    /// from, so a Copy frame the user ticked off reads Exclude.</param>
    /// <param name="grading">The grades the session query computed, or null for a frame it graded
    /// nothing for, which leaves every cell uncoloured and throws nothing.</param>
    /// <param name="baseline">Which half of the grading pair the three sharpness and roundness
    /// cells read.</param>
    /// <param name="filterEnabled">Spec 12.13: a failure mark is shown only while the filter is
    /// enabled.</param>
    public void Apply(
        FrameVerdict verdict,
        bool included,
        FrameGrading? grading,
        QualityBaseline baseline,
        bool filterEnabled)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        _applying = true;
        try
        {
            IsCopied = included;
        }
        finally
        {
            _applying = false;
        }

        // The web's own three arms: the effective keep first, then the frame's own reason.
        IsVerdictCopy = included;
        IsVerdictUnmeasured = !included && verdict.Verdict == Verdict.Unmeasured;
        IsVerdictExclude = !included && verdict.Verdict != Verdict.Unmeasured;

        if (IsVerdictCopy)
        {
            VerdictText = "Copy";
            VerdictTooltip = "Copy";
        }
        else if (IsVerdictUnmeasured)
        {
            VerdictText = "Unmeasured";
            VerdictTooltip = "Unmeasured: none of the constrained metrics recorded";
        }
        else
        {
            VerdictText = "Exclude";
            VerdictTooltip = "Exclude: " + string.Join(", ", verdict.Failures.Select(failure => failure.Text));
        }

        HfrCell = Cell(WbppMetric.Hfr, verdict, grading, baseline, filterEnabled);
        EccentricityCell = Cell(WbppMetric.Ecc, verdict, grading, baseline, filterEnabled);
        FwhmCell = Cell(WbppMetric.Fwhm, verdict, grading, baseline, filterEnabled);
        DetectedStarsCell = Cell(WbppMetric.Stars, verdict, grading, baseline, filterEnabled);
        GuidingRmsCell = Cell(WbppMetric.Rms, verdict, grading, baseline, filterEnabled);
    }

    // Generated by [ObservableProperty]. A click reaches the panel; a publish from Apply does not.
    partial void OnIsCopiedChanged(bool value)
    {
        if (_applying)
        {
            return;
        }

        _toggled(this);
    }

    private VerdictCellViewModel Cell(
        WbppMetric metric,
        FrameVerdict verdict,
        FrameGrading? grading,
        QualityBaseline baseline,
        bool filterEnabled)
    {
        var value = QualityFilter.ValueOf(Frame, metric);
        var text = value is { } present ? QualityFilter.Format(metric, present) : MetricText.Missing;

        // Spec 12.13: a failure mark is shown only while the filter is enabled.
        var failure = filterEnabled
            ? verdict.Failures.FirstOrDefault(entry => entry.Metric == metric)
            : null;

        return new VerdictCellViewModel(text, GradeFor(metric, grading, baseline), failure?.Text);
    }

    // Task 3b brief section 5's table, which is spec 12.13's own sentence: the baseline selects
    // between the pairs for HFR, eccentricity and FWHM; detected stars reads its one grade under
    // both; guiding RMS is never coloured and reads none.
    private static MetricGrade? GradeFor(WbppMetric metric, FrameGrading? grading, QualityBaseline baseline)
    {
        if (grading is null)
        {
            return null;
        }

        var session = baseline == QualityBaseline.Session;
        return metric switch
        {
            WbppMetric.Hfr => session ? grading.SessionHfr : grading.RigHfr,
            WbppMetric.Ecc => session ? grading.SessionEccentricity : grading.RigEccentricity,
            WbppMetric.Fwhm => session ? grading.SessionFwhm : grading.RigFwhm,
            WbppMetric.Stars => grading.DetectedStars,
            _ => null,
        };
    }

    private static VerdictCellViewModel Blank() => new(MetricText.Missing, null, null);
}
