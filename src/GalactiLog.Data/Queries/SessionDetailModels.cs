namespace GalactiLog.Data.Queries;

/// <summary>One metric's session range (spec 12.4 "Session ranges"). All three null when no frame
/// carries the metric; null means "not measured", never zero.</summary>
public sealed record MetricRangeSummary(double? Min, double? Max, double? Median);

/// <summary>Spec 12.4's per-filter medians, for the card body and the chart overlay.</summary>
/// <param name="RigLabel">The rig whose frames this row was computed over, on a multi-rig night
/// (spec 12.4 item 2, ruling C4), and null on a single-rig one. That null is what makes "a
/// single-rig night renders exactly as it does today" a property of the data rather than a branch
/// in the view. Trailing and optional, because this record is built positionally here and in
/// several test fixtures.</param>
public sealed record FilterMedians(
    string FilterName,
    double? MedianHfr,
    double? MedianEccentricity,
    double? MedianFwhm,
    double? MedianGuidingRmsArcsec,
    double? MedianDetectedStars,
    string? RigLabel = null);

/// <summary>Spec 12.4's per-filter detail row. Grouped by (filter, exposure_time), so one filter
/// shot at two exposures yields two rows, which is what the web application does and what makes
/// the Exposure column meaningful.</summary>
/// <param name="RigLabel">The rig whose frames this row was computed over, on a multi-rig night,
/// and null on a single-rig one. Same rule and same reason as
/// <see cref="FilterMedians.RigLabel"/>.</param>
public sealed record FilterDetailRow(
    string FilterName,
    int FrameCount,
    double IntegrationSeconds,
    double? MedianHfr,
    double? MedianEccentricity,
    double? ExposureTime,
    string? RigLabel = null);

/// <summary>Spec 12.16's per-filter acquisition figures, the AstroBin CSV's only data source.
/// Grouped, ordered and rig-split by exactly the rules <see cref="FilterDetailRow"/> is, so row
/// <c>n</c> here is row <c>n</c> there and the two can never disagree about which group a figure
/// belongs to. Every nullable member is null when no frame of the group carries the value, which
/// the CSV renders as a blank cell and never as zero.</summary>
/// <param name="ModalGain">The <c>camera_gain</c> occurring most often over the group's frames, a
/// tie broken by the smaller value. Not the first frame's gain: a night imaged by two rigs at two
/// gains has a majority, and reading the first frame reports whichever rig started.</param>
/// <param name="MedianSensorTemp">Median <c>sensor_temp</c>. The CSV rounds it; this is the
/// unrounded figure, so the one rounding rule lives with the one formatter.</param>
/// <param name="MedianSkyQuality">Median <c>sky_quality</c>, which is filled from the NINA weather
/// sidecar only and never from a FITS card (ruling B7).</param>
/// <param name="MedianFwhm">Median <c>fwhm</c>, already arcseconds and taking no plate scale
/// multiply (spec 7.1.1).</param>
/// <param name="RigLabel">The rig whose frames this row was computed over, on a multi-rig night,
/// and null on a single-rig one. Same rule and same reason as
/// <see cref="FilterMedians.RigLabel"/>, and trailing for the same reason.</param>
public sealed record FilterAcquisition(
    string FilterName,
    int FrameCount,
    double? ExposureTime,
    int? ModalGain,
    double? MedianSensorTemp,
    double? MedianSkyQuality,
    double? MedianFwhm,
    double? MedianAmbientTemp,
    string? RigLabel = null);

/// <summary>
/// Spec 12.4's "A multi-rig night": one rig of the night, a rig being the (telescope, camera) pair
/// after alias mapping. The list is ordered by first capture time within the night and
/// <see cref="Index"/> is the position in that list, which is the rig index every surface of the
/// page reads: the label rows, the frame table's Rig column, the thumbnail strip's boxes, the rig
/// pills and section 13's dash patterns.
/// </summary>
/// <param name="Label">The canonical <c>"{telescope} / {camera}"</c> label, the same spelling
/// <see cref="FrameRow.Rig"/> carries. One spelling, built once, so the insight prefixes, the
/// pills and the label rows cannot disagree.</param>
/// <param name="FrameCount">Frames of this rig alone.</param>
/// <param name="IntegrationSeconds">Exposure seconds of this rig alone.</param>
/// <param name="ReferenceImageId">The <c>images.id</c> of the rig's reference frame (PAR-008), the
/// first of <see cref="ReferenceCandidates"/>, or null when the rig has no candidate at all.</param>
/// <param name="ReferenceFramePath">That frame's absolute path, the first of
/// <see cref="ReferenceCandidates"/>.</param>
/// <param name="ReferenceCandidates">Up to three absolute paths in ranked order (spec 12.4's
/// decode walk). The query ranks; the view-model walks at most three and takes the first that
/// decodes, so the list rather than the winner alone is what has to reach the page.</param>
/// <param name="Ranges">Spec 12.4's five session ranges over this rig's own frames, in the fixed
/// order the session's own ranges are built in: HFR, eccentricity, FWHM, guiding RMS, sensor
/// temperature. One member rather than five, because the labels and the formats live with the
/// order in the one place that renders them. Null on a single-rig night, where the ranges table is
/// the session's own and unchanged.</param>
public sealed record RigGroup(
    int Index,
    string Label,
    string? Telescope,
    string? Camera,
    int FrameCount,
    double IntegrationSeconds,
    Guid? ReferenceImageId,
    string? ReferenceFramePath,
    IReadOnlyList<string>? ReferenceCandidates = null,
    IReadOnlyList<MetricRangeSummary>? Ranges = null);

/// <summary>Semantic level a session insight renders at. The view maps this to a theme token.
/// Only <see cref="Warning"/> is produced in v1: spec 12.4's two insight families are both
/// warnings, and the web application's good/info verdicts are out of scope (questions.md Q6).
/// The other two members exist so the view's token switch is total when Phase 9 adds one.</summary>
public enum InsightLevel
{
    Info,
    Good,
    Warning,
}

/// <summary>One session insight sentence (spec 12.4).</summary>
/// <param name="Level">The semantic token the card picks. The view never parses the message.</param>
/// <param name="Kind">The stable machine key: "hfr_outliers", "eccentricity_outliers" or
/// "eccentricity_vs_rig". Test assertions and any future filtering key on this, not on the
/// prose.</param>
/// <param name="Message">The sentence the card renders, always built with
/// <c>CultureInfo.InvariantCulture</c> so a decimal point survives a European machine.</param>
public sealed record SessionInsight(InsightLevel Level, string Kind, string Message);

/// <summary>One metric's grading under one baseline: the signed MAD deviation and the median it
/// was measured against, which is the figure spec 12.4's cell tooltip names.</summary>
/// <param name="Z">The signed deviation, positive for worse, null when the cell is ungraded.</param>
/// <param name="BaselineMedian">The baseline's own median, null when the baseline has none. It is
/// carried beside the deviation because the tooltip quotes it and a second lookup at the view
/// would have to re-derive which baseline the deviation came from.</param>
public sealed record MetricGrade(double? Z, double? BaselineMedian);

/// <summary>Spec 12.4's per-frame deviations, both baselines at once, so flipping "Compare to"
/// re-reads a value this query already returned rather than issuing a second query.</summary>
/// <remarks>
/// <para>
/// The last three members carry no rig twin on purpose. Spec 12.4's "Compare to" block says the
/// toggle governs the sharpness and roundness metrics only, and that the signal metrics, detected
/// stars, median ADU and guiding RMS, are always graded against the night's own baseline whichever
/// way the toggle is set, because they drift with the sky and a catalogue-wide baseline for them
/// would grade the weather.
/// </para>
/// <para>
/// Departure from the web, deliberate: <c>SessionAccordionCard.tsx</c> does not grade the guiding
/// RMS columns at all. The spec does, against the session baseline, and
/// <c>FrameQuality.Metrics</c> already computes a <c>guiding_rms_arcsec</c> baseline, so the grade
/// costs one more <c>MadZ</c> call per frame (questions.md Q7).
/// </para>
/// <para>
/// These are the grading bands and they are not the two outlier flags on the same row. The flags
/// answer "this night's rules flagged this frame" and the bands answer "how far from the baseline
/// this value sits"; the outlier filter, the pills' counts and the night strip's tall ticks all
/// keep reading the flags.
/// </para>
/// </remarks>
public sealed record FrameGrading(
    MetricGrade SessionHfr, MetricGrade RigHfr,
    MetricGrade SessionEccentricity, MetricGrade RigEccentricity,
    MetricGrade SessionFwhm, MetricGrade RigFwhm,
    MetricGrade DetectedStars,
    MetricGrade AduMedian,
    MetricGrade GuidingRms);

/// <summary>One frame table row (spec 12.4's eight-group table) and one point of the per-session
/// chart. Every column of that table has a field here; the view decides which are visible.</summary>
/// <param name="FilterUsed">Canonical filter name, or null when the frame carries no FILTER
/// card.</param>
/// <param name="GuidingRmsSource">Drives the source glyph (spec 12.4): non-null means the figure
/// came from the named source and must not be silently compared with another.</param>
/// <param name="Rig">The canonical rig label, <c>"{telescope} / {camera}"</c> with "Unknown" for a
/// missing half. Used by the chart's per-rig split and by the multi-rig insight prefix.</param>
/// <param name="IsHfrOutlier">True when this frame's <c>median_hfr</c> exceeds the session's
/// median HFR times 1.5, which is the same rule and the same threshold the <c>hfr_outliers</c>
/// insight prints (P12 R3). False on every frame of a session whose insight is silent, so the
/// count of true values equals the insight's count by construction.</param>
/// <param name="IsEccentricityOutlier">True when this frame's pooled eccentricity is at or beyond
/// <c>FrameQuality.ZReject</c> MAD above its own (telescope, camera, filter) group's session
/// median. Same rule, same baselines and the same graded set the <c>eccentricity_outliers</c>
/// insight counts over (P12 R3).</param>
/// <param name="IsFwhmOutlier">The eccentricity rule over <c>fwhm</c> (P24 R22): at or beyond
/// <c>FrameQuality.ZReject</c> MAD above the group's session median.</param>
/// <param name="IsStarsOutlier">The same rule over <c>detected_stars</c> with the sign flipped:
/// at or beyond <c>FrameQuality.ZReject</c> MAD below the group's session median, because few
/// stars is the bad side.</param>
/// <param name="IsGuidingRmsOutlier">The same rule over <c>guiding_rms_arcsec</c>, above the
/// group's session median.</param>
/// <remarks>
/// <para><c>median_fwhm</c> is absent by design (spec 7.1.1): Task 6's raw header panel is the
/// only place it appears, and it reads it from <c>raw_headers</c>.</para>
/// <para>
/// The two flags are a return-shape change and not a new computation (P12 R3). Nothing here
/// grades a frame that the session insights did not already grade; the verdict the insight
/// counted is simply carried on the row it belongs to.
/// </para>
/// <para>
/// Both flags carry the session-wide rule only (P12 ruling Q8). The per-rig sentences a multi-rig
/// night also emits are not mirrored on any row, because a row belongs to one rig and a second
/// pair of flags would give the frame table two different answers about the same frame.
/// </para>
/// <para>
/// <c>Grading</c> is trailing and optional because <c>SessionDetailQuery.Read</c> builds the row
/// positionally and several test files target-type it; it is built null there, exactly as the two
/// flags are built false, and <c>Get</c> fills it in the same <c>with</c> expression that sets
/// them.
/// </para>
/// </remarks>
public sealed record FrameRow(
    Guid ImageId,
    string FilePath,
    string FileName,
    DateTime? CaptureDate,
    string? FilterUsed,
    double? ExposureTime,
    double? MedianHfr,
    double? Eccentricity,
    double? Fwhm,
    int? DetectedStars,
    double? GuidingRmsArcsec,
    double? GuidingRmsRaArcsec,
    double? GuidingRmsDecArcsec,
    string? GuidingRmsSource,
    double? AduMean,
    double? AduMedian,
    double? AduStdev,
    int? AduMin,
    int? AduMax,
    int? FocuserPosition,
    double? FocuserTemp,
    double? AmbientTemp,
    double? DewPoint,
    double? Humidity,
    double? Pressure,
    double? WindSpeed,
    double? WindDirection,
    double? WindGust,
    double? CloudCover,
    double? SkyQuality,
    double? Airmass,
    string? PierSide,
    double? RotatorPosition,
    double? SensorTemp,
    int? CameraGain,
    string Rig,
    bool IsHfrOutlier,
    bool IsEccentricityOutlier,
    bool IsFwhmOutlier = false,
    bool IsStarsOutlier = false,
    bool IsGuidingRmsOutlier = false,
    FrameGrading? Grading = null,
    string? EccentricitySource = null,
    double? ArcsecPerPixel = null);

/// <summary>One night's slice of a detail's <see cref="SessionDetail.Frames"/> list (P25 R3).</summary>
public sealed record NightSpan(DateOnly Night, int FirstFrameIndex, int FrameCount);

/// <summary>Everything an expanded session card shows (spec 12.4), read in one round trip when
/// the card expands.</summary>
/// <param name="MedianHfrArcsec">Median HFR in arcseconds, pooling only plate-scaled frames, with
/// <paramref name="HfrArcsecExcludedCount"/> disclosed, on the same rule as the totals row.</param>
/// <param name="HfrArcsecExcludedCount">Frames carrying an HFR but no usable plate scale.</param>
/// <param name="Gain">The gain of the session's first frame by capture time, matching the web
/// application. A session shot at two gains reports the first; the frame table's Gain column is
/// where the per-frame truth lives.</param>
/// <param name="Eccentricity">Pooled on the session's modal <c>eccentricity_source</c> only
/// (spec 7.2), so its minimum, maximum and median all describe one source.</param>
/// <param name="EccentricitySource">The modal <c>eccentricity_source</c> every eccentricity
/// figure on this card pooled, including the per-filter medians and the outlier baselines. Null
/// when no frame carries an eccentricity, and also null when the frames that carry one recorded
/// no source at all, so the card needs a wording for "source not recorded".</param>
/// <param name="EccentricityExcludedCount">Frames carrying an eccentricity from a non-modal
/// source, excluded from every eccentricity figure. A count to disclose, not an error.</param>
/// <param name="ExposureTimes">Distinct non-null exposure times present, ascending (spec 12.4
/// "exposure times present").</param>
/// <param name="Notes">The <c>session_notes.notes</c> text for (target, date), or null. Always
/// null for an <c>obj:</c> group, which has no target id to key a note on.</param>
/// <param name="Rigs">Spec 12.4's rig groups (PAR-004), one per rig of the night in first-capture
/// order, always at least one for a night that has frames. Trailing and optional so no positional
/// construction site moves; null reads as "no rig information", which is what a fixture that
/// predates this phase carries.</param>
/// <param name="ReferenceImageId">The night's own reference frame (PAR-008), ranked over every
/// LIGHT frame of the night by the same rule each rig's own pick uses. On a single-rig night the
/// night's pick and the one rig's pick are the same frame.</param>
/// <param name="ReferenceFramePath">That frame's absolute path, the first of
/// <paramref name="ReferenceCandidates"/>.</param>
/// <param name="ReferenceCandidates">Up to three absolute paths in ranked order, for spec 12.4's
/// decode walk.</param>
/// <param name="FilterAcquisitions">Spec 12.16's per-filter acquisition figures, index for index
/// the same groups as <paramref name="FilterDetails"/>. Trailing and optional so no positional
/// construction site moves; a null or omitted argument normalises to an empty list, so a site that
/// leaves it out is never read by the page as a failed night.</param>
public sealed record SessionDetail(
    string GroupKey,
    DateOnly SessionDate,
    int FrameCount,
    double IntegrationSeconds,
    MetricRangeSummary Hfr,
    double? MedianHfrArcsec,
    int HfrArcsecExcludedCount,
    MetricRangeSummary Eccentricity,
    string? EccentricitySource,
    int EccentricityExcludedCount,
    MetricRangeSummary Fwhm,
    MetricRangeSummary GuidingRmsArcsec,
    MetricRangeSummary SensorTemp,
    int? Gain,
    IReadOnlyList<double> ExposureTimes,
    DateTime? FirstFrameTime,
    DateTime? LastFrameTime,
    IReadOnlyList<FilterMedians> FilterMedians,
    IReadOnlyList<FilterDetailRow> FilterDetails,
    double? MedianAirmass,
    double? MedianAmbientTemp,
    double? MedianHumidity,
    IReadOnlyList<SessionInsight> Insights,
    IReadOnlyList<FrameRow> Frames,
    string? Notes,
    IReadOnlyList<RigGroup>? Rigs = null,
    Guid? ReferenceImageId = null,
    string? ReferenceFramePath = null,
    IReadOnlyList<string>? ReferenceCandidates = null,
    IReadOnlyList<FilterAcquisition>? FilterAcquisitions = null)
{
    // A default parameter value must be a compile-time constant, so the empty-list default is
    // applied here instead of at the parameter.
    public IReadOnlyList<FilterAcquisition>? FilterAcquisitions { get; init; } = FilterAcquisitions ?? [];

    /// <summary>One span per night in <see cref="Frames"/>, oldest first; a single night is one
    /// span over all its frames, so more than one span is what "merged" means (P25 R3).</summary>
    public IReadOnlyList<NightSpan> Nights { get; init; } = [new NightSpan(SessionDate, 0, Frames.Count)];
}
