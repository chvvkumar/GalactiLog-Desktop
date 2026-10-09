using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GalactiLog.App.ViewModels.Dashboard;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Settings;
using GalactiLog.Data.Ingest;
using GalactiLog.Data.Queries;

namespace GalactiLog.App.ViewModels.TargetDetail;

/// <summary>
/// One row of design-spec 12.4's frame table: a display projection of Task 2's
/// <see cref="FrameRow"/> read model. Formatting only. No business logic, no queries, and nothing
/// that opens a file: <see cref="FilePath"/> is text, handed to <c>ShellIntegration</c> or to the
/// clipboard and to nothing else (design-spec 2.1).
/// </summary>
/// <remarks>
/// There is deliberately no <c>Cell(string columnKey)</c> indexer. The view binds each cell
/// explicitly by property, the way <c>TargetListView.axaml</c> already does, so compiled bindings
/// check all 32 paths at build time; a string-keyed lookup would move them from build errors to
/// runtime silence.
/// </remarks>
public sealed partial class FrameRowViewModel : ObservableObject
{
    /// <param name="row">Task 2's read model for one frame.</param>
    /// <param name="columns">The owning table's column objects, shared by every row. The row cells
    /// bind their visibility straight to these rather than walking up to the table's DataContext
    /// from inside an item template, which is the pattern <c>TargetRowViewModel</c> established.
    /// </param>
    /// <param name="zone">The display zone, resolved once per table by
    /// <see cref="SessionTimeFormat.Resolve"/>: it is a system lookup and must not run per row.
    /// </param>
    /// <param name="use24Hour"><c>general.use_24h_time</c> (design-spec 5.8.1).</param>
    /// <param name="baseline">Spec 12.4's "Compare to" choice the six graded cells and the row
    /// score are built against. The table's current value at construction; a later flip goes
    /// through <see cref="ApplyBaseline"/>.</param>
    /// <param name="datePrefixed">P25 R7: true on a table whose detail spans more than one
    /// night, when the time cell carries the frame's local capture date as "MM-dd ".</param>
    public FrameRowViewModel(
        FrameRow row,
        IReadOnlyList<ColumnViewModel> columns,
        TimeZoneInfo zone,
        bool use24Hour,
        GradingBaseline baseline = GradingBaseline.Session,
        bool datePrefixed = false)
    {
        Row = row;
        Columns = columns;
        var clock = SessionTimeFormat.Format(row.CaptureDate, zone, use24Hour);
        TimeText = MetricText.Cell(datePrefixed && row.CaptureDate is { } captured
            ? NightStripViewModel.ToLocal(captured, zone).ToString("MM-dd ", CultureInfo.InvariantCulture) + clock
            : clock);
        ApplyBaseline(baseline);
    }

    /// <summary>The read model behind the row. Task 6's raw-header query keys on
    /// <see cref="ImageId"/> from here; Phase 8's preview modal navigates the owning table's row
    /// list.</summary>
    public FrameRow Row { get; }

    /// <summary>The owning table's 32 columns, in design-spec 12.4's order, for per-cell
    /// visibility.</summary>
    public IReadOnlyList<ColumnViewModel> Columns { get; }

    /// <summary>The <c>images.id</c> of this frame. Task 6 queries raw headers by it.</summary>
    public Guid ImageId => Row.ImageId;

    /// <summary>The absolute path, for reveal, open-with and copy-path. Never opened here.
    /// </summary>
    public string FilePath => Row.FilePath;

    // ---- the rig (PAR-004) --------------------------------------------------------------------

    /// <summary>The canonical <c>"{telescope} / {camera}"</c> label this frame was taken with, and
    /// the cell of spec 12.4's conditional Rig column. One spelling, built once in the query, so
    /// the column, the pills and the insight prefixes cannot disagree.</summary>
    public string RigText => Row.Rig;

    /// <summary>This frame's rig index in the night's first-capture rig order, set by the owning
    /// table, which is the one place that holds the night's rig list. Zero on a single-rig night,
    /// which is what it means there.</summary>
    public int RigIndex { get; init; }

    // ---- the two outlier verdicts (P12 R3) ----------------------------------------------------

    /// <summary>Task 2's per-frame HFR verdict, computed by the same rule and the same threshold
    /// the <c>hfr_outliers</c> insight prints (P12 R3). Drives the HFR cell's ink and the HFR
    /// outlier filter; the count of rows this is true for equals the insight's count by
    /// construction.</summary>
    public bool IsHfrOutlier => Row.IsHfrOutlier;

    public bool IsEccentricityOutlier => Row.IsEccentricityOutlier;

    /// <summary>The three P24 R22 flags, read by the outlier filter alone: the row's ink and
    /// <see cref="IsOutlier"/> keep spec 12.4's two.</summary>
    public bool IsFwhmOutlier => Row.IsFwhmOutlier;

    public bool IsStarsOutlier => Row.IsStarsOutlier;

    public bool IsGuidingRmsOutlier => Row.IsGuidingRmsOutlier;

    /// <summary>Either flag, and what lifts the whole row's line to the primary ink (the comp's
    /// <c>.tbl.fr .row.out .c</c>). Light, not colour: the row is brought forward by weight of ink
    /// alone, and the worse ink stays on the one cell whose metric is out of line, so the eye is
    /// sent to the number rather than to the row (P12 direction: colour is spent only on data;
    /// review ruling (a)).</summary>
    public bool IsOutlier => IsHfrOutlier || IsEccentricityOutlier;

    /// <summary>The absolute path this row contributes to a multi-row copy. Identical to
    /// <see cref="FilePath"/> today and named separately so the copy's contract is one property
    /// rather than a use of a general one: spec 12.4 is explicit that copying a frame list writes
    /// absolute paths and nothing else, and P12 R2 extends that wording to the selection copy.
    /// Never opened, never written (spec 2.1).</summary>
    public string PathForCopy => Row.FilePath;

    // ---- the 32 cells, in spec 12.4's column order --------------------------------------------
    // An absent value renders MetricText.Missing, never a blank (spec.md item 6); a real zero is 0.

    /// <summary><c>capture_date</c> in <c>general.timezone</c>, formatted per
    /// <c>general.use_24h_time</c>. Computed once at construction rather than per binding pass,
    /// because it is the only cell whose formatting costs a time-zone conversion.</summary>
    public string TimeText { get; }

    public string FileName => Row.FileName;

    public string FilterText => MetricText.Cell(Row.FilterUsed);

    /// <summary>Seconds, with no unit: the comp puts the unit in the header ("Exp s") and never
    /// in the cell, so a column of figures shares one decimal axis, and the missing dash when the
    /// frame recorded no exposure. This was the only one of the 32 cells that carried a suffix.
    /// </summary>
    public string ExposureText => MetricText.Cell(Row.ExposureTime, "0.##");

    /// <summary>design-spec 12.4's HFR column. <c>median_hfr</c> is the per-frame HFR the
    /// ingester recorded, not a session aggregate.</summary>
    public string MedianHfrText => MetricText.Cell(Row.MedianHfr, "0.00");

    /// <summary>The raw per-frame eccentricity. Not pooled on the session's eccentricity source
    /// (Task 2 deviation D4): spec 12.4's table has no per-frame source column and this cell
    /// grades nothing, so nothing here compares two sources against one baseline.</summary>
    public string EccentricityText => MetricText.Cell(Row.Eccentricity, "0.00");

    /// <summary>design-spec 7.1.1: the FWHM column reads <c>fwhm</c>, never
    /// <c>median_fwhm</c>.</summary>
    public string FwhmText => MetricText.Cell(Row.Fwhm, "0.00");

    public string DetectedStarsText => MetricText.Cell(Row.DetectedStars, "N0");

    public string GuidingRmsText => MetricText.Cell(Row.GuidingRmsArcsec, "0.00");

    public string GuidingRmsRaText => MetricText.Cell(Row.GuidingRmsRaArcsec, "0.00");

    public string GuidingRmsDecText => MetricText.Cell(Row.GuidingRmsDecArcsec, "0.00");

    public string AduMeanText => MetricText.Cell(Row.AduMean, "N0");

    public string AduMedianText => MetricText.Cell(Row.AduMedian, "N0");

    public string AduStdevText => MetricText.Cell(Row.AduStdev, "N0");

    public string AduMinText => MetricText.Cell(Row.AduMin, "N0");

    public string AduMaxText => MetricText.Cell(Row.AduMax, "N0");

    public string FocuserPositionText => MetricText.Cell(Row.FocuserPosition, "N0");

    public string FocuserTempText => MetricText.Cell(Row.FocuserTemp, "0.0");

    public string AmbientTempText => MetricText.Cell(Row.AmbientTemp, "0.0");

    public string DewPointText => MetricText.Cell(Row.DewPoint, "0.0");

    public string HumidityText => MetricText.Cell(Row.Humidity, "0");

    public string PressureText => MetricText.Cell(Row.Pressure, "0");

    public string WindSpeedText => MetricText.Cell(Row.WindSpeed, "0.0");

    public string WindDirectionText => MetricText.Cell(Row.WindDirection, "0");

    public string WindGustText => MetricText.Cell(Row.WindGust, "0.0");

    public string CloudCoverText => MetricText.Cell(Row.CloudCover, "0");

    public string SkyQualityText => MetricText.Cell(Row.SkyQuality, "0.00");

    public string AirmassText => MetricText.Cell(Row.Airmass, "0.00");

    public string PierSideText => MetricText.Cell(Row.PierSide);

    public string RotatorPositionText => MetricText.Cell(Row.RotatorPosition, "0.0");

    public string SensorTempText => MetricText.Cell(Row.SensorTemp, "0.0");

    public string CameraGainText => MetricText.Cell(Row.CameraGain, "0");

    // ---- the guiding RMS source disclosure (spec 12.4, Phase 15A) -------------------------------

    /// <summary>design-spec 12.4's guiding provenance mark: the dagger appears only when the
    /// source is <c>phd2</c>, never for <c>csv</c>. A <c>csv</c> figure is the frame's own
    /// sidecar and needs no disclosure; the dagger marks the figure that was measured somewhere
    /// other than beside the frame. Compared against <see cref="Phd2Correlation.Phd2Source"/>
    /// rather than a second literal (review P3-4): Task 5's correlation pass stamps that constant
    /// and a rename must move both readers together.</summary>
    public bool HasGuidingRmsSource
        => string.Equals(Row.GuidingRmsSource, Phd2Correlation.Phd2Source, StringComparison.Ordinal);

    /// <summary>The dagger, U+2020, not an emoji and not a private-use glyph: the house style
    /// forbids decorative characters and the source sentence is in the tooltip and the accessible
    /// name. Present in all six embedded faces (ruling F4), so no fallback face ever shows a box.
    /// </summary>
    public string GuidingRmsSourceGlyph => HasGuidingRmsSource ? "†" : "";

    /// <summary>design-spec 12.4's exact sentence, read by the tooltip (the dagger itself is
    /// decorative to a screen reader). Null rather than empty when the source is not
    /// <c>phd2</c>, so the tooltip binding carries no text and shows nothing.</summary>
    public string? GuidingRmsSourceTooltip => HasGuidingRmsSource ? "from a PHD2 guide log" : null;

    /// <summary>Review P2-3: the value cell's accessible name must carry the figure, not replace
    /// it with the sentence. Null when the source is not <c>phd2</c>, so
    /// <c>AutomationProperties.Name</c> carries no override and the cell falls back to its own
    /// text, which is the figure alone, exactly what a <c>csv</c> or unmeasured row's name should
    /// be. "arcsec" is spelled out here, unlike the visual cell, because the unit lives in the
    /// column header for a sighted reader but a screen reader announcing this cell's name has no
    /// other route to it.</summary>
    public string? GuidingRmsAccessibleName
        => HasGuidingRmsSource ? $"{GuidingRmsText} arcseconds, from a PHD2 guide log" : null;

    // ---- the raw header seam (Task 6) ---------------------------------------------------------

    /// <summary>Whether design-spec 12.4's "show raw headers" panel is open for this frame. Set
    /// by <see cref="FrameTableViewModel.ToggleRawHeadersCommand"/>. Never persisted: it is per
    /// row, per visit.
    /// <para>
    /// Task 6 owns what renders behind it, and owns the per-frame header query keyed on
    /// <see cref="ImageId"/>. In this task the flag exists and the region in
    /// <c>FrameTableView.axaml</c> is empty.
    /// </para></summary>
    [ObservableProperty]
    public partial bool AreRawHeadersExpanded { get; set; }

    /// <summary>Spec 12.4's raw header panel for this frame. Null until
    /// <see cref="FrameTableViewModel.ToggleRawHeadersCommand"/> first turns
    /// <see cref="AreRawHeadersExpanded"/> on, which is what keeps a 400-row session from holding
    /// 400 header queries: only a row a user actually opens ever gets one (Task 6 handoff).
    /// </summary>
    [ObservableProperty]
    public partial RawHeaderPanelViewModel? RawHeaders { get; set; }

    /// <summary>R9's hover tint, set by <see cref="FrameTableViewModel.HighlightedRow"/> and by
    /// nothing else. Not a selection: Copy paths, Reveal and the toolbar counts never read it.
    /// </summary>
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }

    // ---- spec 12.4's per-frame quality grading (PAR-003) --------------------------------------
    //
    // Six graded cells and one row score, all rebuilt by ApplyBaseline. These are not the two
    // outlier flags above: the flags answer "this night's rules flagged this frame" and the bands
    // answer "how far from the baseline this value sits", and spec 12.4 keeps both marks. Where a
    // cell carries both, the flag's metric-worst ink wins, because it is the verdict the outlier
    // filter, the pills' counts and the night strip's tall ticks all agree on.

    /// <summary>The HFR cell under the active baseline.</summary>
    [ObservableProperty]
    public partial GradedCellViewModel HfrCell { get; private set; } = new("");

    /// <summary>The eccentricity cell under the active baseline.</summary>
    [ObservableProperty]
    public partial GradedCellViewModel EccentricityCell { get; private set; } = new("");

    /// <summary>The FWHM cell under the active baseline.</summary>
    [ObservableProperty]
    public partial GradedCellViewModel FwhmCell { get; private set; } = new("");

    /// <summary>The detected stars cell, always graded against the night's own baseline.</summary>
    [ObservableProperty]
    public partial GradedCellViewModel DetectedStarsCell { get; private set; } = new("");

    /// <summary>The guiding RMS cell, always graded against the night's own baseline. The spec
    /// grades it and the web application does not (questions.md Q7).</summary>
    [ObservableProperty]
    public partial GradedCellViewModel GuidingRmsCell { get; private set; } = new("");

    /// <summary>The median ADU cell, always graded against the night's own baseline.</summary>
    [ObservableProperty]
    public partial GradedCellViewModel AduMedianCell { get; private set; } = new("");

    /// <summary>Spec 12.4's row score, 0 to 100 and higher is better, or null when the frame has
    /// no graded axis at all.</summary>
    [ObservableProperty]
    public partial double? RowScore { get; private set; }

    /// <summary>The band the row score falls in, on the score's own ladder and not the deviation
    /// ladder. Null exactly when <see cref="RowScore"/> is null.</summary>
    [ObservableProperty]
    public partial QualityBand? ScoreBand { get; private set; }

    /// <summary>The row's tint, as the two classes the markup selects on (R23). Better and
    /// neutral rows carry neither and take no fill.</summary>
    [ObservableProperty]
    public partial bool IsScoreWatch { get; private set; }

    [ObservableProperty]
    public partial bool IsScoreReject { get; private set; }

    /// <summary>
    /// Rebuilds the six graded cells and the row score against <paramref name="baseline"/>. Called
    /// once at construction with the table's current baseline and again on every "Compare to"
    /// flip, over the table's capture order rather than its filtered rows, so a hidden row comes
    /// back already graded.
    /// </summary>
    /// <remarks>
    /// The signal axis of the score is the detected stars deviation, which is session graded
    /// whichever way the toggle is set; sharpness and roundness follow the toggle. That is the
    /// web's own composition in <c>SessionAccordionCard.tsx</c> and it follows from spec 12.4's
    /// rule that the signal metrics are always session graded.
    /// </remarks>
    public void ApplyBaseline(GradingBaseline baseline)
    {
        var grading = Row.Grading;
        var word = GradedCellViewModel.WordFor(baseline);
        var rig = baseline == GradingBaseline.Rig;

        var hfr = rig ? grading?.RigHfr : grading?.SessionHfr;
        var eccentricity = rig ? grading?.RigEccentricity : grading?.SessionEccentricity;
        var fwhm = rig ? grading?.RigFwhm : grading?.SessionFwhm;

        HfrCell = new GradedCellViewModel(MedianHfrText, hfr, HfrTitle, "0.00", word);
        EccentricityCell = new GradedCellViewModel(
            EccentricityText, eccentricity, EccentricityTitle, "0.00", word);
        FwhmCell = new GradedCellViewModel(FwhmText, fwhm, FwhmTitle, "0.00", word);

        // The three signal metrics always name "session": that is the baseline the number was
        // measured against, whatever the toggle says (spec 12.4's Compare to block).
        DetectedStarsCell = new GradedCellViewModel(
            DetectedStarsText, grading?.DetectedStars, DetectedStarsTitle, "N0", "session");
        GuidingRmsCell = new GradedCellViewModel(
            GuidingRmsText, grading?.GuidingRms, GuidingRmsTitle, "0.00", "session");
        AduMedianCell = new GradedCellViewModel(
            AduMedianText, grading?.AduMedian, AduMedianTitle, "N0", "session");

        RowScore = FrameQuality.CombinedScore(grading?.DetectedStars.Z, hfr?.Z, eccentricity?.Z);
        ScoreBand = FrameQuality.BandForScore(RowScore);
        IsScoreWatch = ScoreBand == QualityBand.Watch;
        IsScoreReject = ScoreBand == QualityBand.Reject;
    }

    // The six graded columns' own header texts, so a tooltip label and a header cell cannot
    // disagree and no label is retyped here. Resolved once for the process rather than per cell
    // per row: ApplyBaseline runs six times a row over a whole night's frames.
    private static string Title(string columnKey)
        => FrameColumns.All.First(column => column.Key == columnKey).Title;

    private static readonly string HfrTitle = Title("median_hfr");

    private static readonly string EccentricityTitle = Title(FrameQuality.EccentricityMetric);

    private static readonly string FwhmTitle = Title("fwhm");

    private static readonly string DetectedStarsTitle = Title("detected_stars");

    private static readonly string GuidingRmsTitle = Title("guiding_rms_arcsec");

    private static readonly string AduMedianTitle = Title("adu_median");
}
