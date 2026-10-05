using GalactiLog.Core.Metrics;

namespace GalactiLog.Data.Queries;

/// <summary>
/// The Statistics page's read models (spec 12.5). Records only, no behaviour: every rule that
/// produced a number lives in <see cref="StatsQuery"/>, so a view can bind these without knowing
/// which of them came from SQL and which from a C# pass.
/// </summary>
/// <remarks>
/// Names follow spec 12.5's section names rather than the web application's schema class names,
/// because spec 12.5 is what the page is built from. Two shape differences from
/// <c>schemas/stats.py</c> are deliberate and recorded here so a reviewer does not read either as
/// an omission: <c>all_frames</c> is absent (questions.md Q8, because spec 12.5's overview tiles
/// do not name it), and <c>site_coords</c> is absent (questions.md Q14: observer coordinates come
/// from <c>general.observer_latitude</c> and <c>observer_longitude</c>, never from a FITS header).
/// </remarks>
/// <param name="TotalIntegrationSeconds">Sum of <c>exposure_time</c> over LIGHT frames that carry
/// a <c>capture_date</c>.</param>
/// <param name="TotalFrames">Every LIGHT frame, deliberately with no capture-date filter while
/// every other overview figure has one. Ported verbatim from <c>_query_overview</c>, whose own
/// comment says the tile is labelled "all LIGHT frames".</param>
/// <param name="RigSessionCount">Distinct (session_date, raw telescope, raw camera) triples, not
/// distinct nights: a night imaged with two rigs counts as two. The web source carries audit note
/// AUD-019 saying so, and the raw equipment strings are used there, not the aliased ones.</param>
/// <param name="FirstSessionDate">The earliest <c>session_date</c>, not the earliest
/// <c>capture_date</c>. The web field is named <c>first_capture_date</c> and reads
/// <c>min(session_date)</c>; the port keeps the honest name.</param>
public sealed record StatsOverview(
    double TotalIntegrationSeconds,
    int TargetCount,
    int TotalFrames,
    int RigSessionCount,
    DateOnly? FirstSessionDate,
    DateOnly? LastSessionDate);

/// <summary>One canonical camera or telescope (spec 12.5's Equipment inventory row).</summary>
/// <param name="MedianFwhmArcsec">Median of the non-null, non-zero <c>fwhm</c> values. Already
/// arcseconds (it comes from the N.I.N.A. Session Metadata CSV) and never multiplied by a plate
/// scale; spec 7.1.1 is why <c>median_fwhm</c> the column appears nowhere in this file.</param>
/// <param name="Grouped">True when more than one raw name folded onto this canonical name.</param>
public sealed record EquipmentInventoryItem(
    string Name,
    int FrameCount,
    double IntegrationSeconds,
    int Nights,
    int TargetCount,
    double? AvgSessionSeconds,
    double? MedianFwhmArcsec,
    int FwhmFrameCount,
    double? MedianGuidingRmsArcsec,
    bool Grouped);

/// <summary>One canonical filter inside one equipment combination.</summary>
public sealed record EquipmentFilterMetrics(
    string FilterName,
    int FrameCount,
    double IntegrationSeconds,
    double? MedianHfr,
    double? BestHfr,
    double? MedianEccentricity,
    double? MedianFwhm,
    int FwhmFrameCount);

/// <summary>One canonical (telescope, camera) pair (spec 12.5's Equipment performance row).</summary>
/// <param name="MedianHfr">The median over the pair's own frames, not a blend of
/// <see cref="FilterBreakdown"/>'s medians. That is what makes this figure and the inventory's
/// agree, and it is the whole point of the web application's separate <c>_query_equipment_mad</c>
/// pass.</param>
/// <param name="MedianGuidingRmsArcsec">Median of the pair's non-null, non-zero
/// <c>guiding_rms_arcsec</c> values, by the same rule the inventory uses. Spec 13's "Equipment
/// performance comparison" row names it as the third series on a telescope-and-camera category
/// axis; the web application has no such chart (guiding RMS appears only in
/// <c>EquipmentInventory.tsx</c>), so <c>EquipmentComboMetrics</c> in <c>schemas/stats.py</c> has
/// no counterpart field and this one is an addition rather than a port.</param>
/// <param name="MadHfr">Raw median absolute deviation, with no 1.4826 consistency scaling, from
/// <c>GalactiLog.Core.Metrics.Statistics.Mad</c>.</param>
public sealed record EquipmentComboMetrics(
    string Telescope,
    string Camera,
    int FrameCount,
    double IntegrationSeconds,
    double? AvgSessionSeconds,
    double? MedianHfr,
    double? BestHfr,
    double? MedianEccentricity,
    double? MedianFwhm,
    int FwhmFrameCount,
    double? MedianGuidingRmsArcsec,
    double? MadHfr,
    double? MadEccentricity,
    double? MadFwhm,
    bool Grouped,
    IReadOnlyList<EquipmentFilterMetrics> FilterBreakdown);

/// <summary>Integration per canonical filter. A list rather than a dictionary because the order
/// is part of the answer: the web's <c>FilterUsageChart.tsx</c> sorts by integration descending
/// and a dictionary would not carry that across the boundary.</summary>
public sealed record FilterUsageEntry(string FilterName, double IntegrationSeconds);

public sealed record TopTargetEntry(string PrimaryName, double IntegrationSeconds);

/// <summary>One timeline bucket. <paramref name="Period"/> is <c>yyyy-MM</c>, <c>yyyy-Www</c> or
/// <c>yyyy-MM-dd</c> depending on which list it came from.</summary>
public sealed record TimelineEntry(string Period, double IntegrationSeconds);

/// <summary>One histogram column. <paramref name="High"/> is the exclusive upper bound the count
/// used, and is null only for the arcsecond histogram's unbounded overflow bucket. The pixel
/// histogram's last bucket has a real bound of 100 and a label of "5.0+", exactly as the web
/// source does.</summary>
public sealed record HfrBucket(string Label, double Low, double? High, int FrameCount);

/// <summary>Spec 12.5's Data quality row.</summary>
/// <param name="UnscaledFrameCount">Frames with a <c>median_hfr</c> and no
/// <c>arcsec_per_pixel</c>, which is exactly the set the arcsecond figures skip.</param>
/// <param name="EccentricityExcludedCount">Frames carrying an eccentricity from a source other
/// than <paramref name="EccentricitySource"/> (spec 7.2). Null when no frame carries an
/// eccentricity at all, which is the only case with nothing to pool.</param>
/// <param name="HfrPixelHistogram">Every bucket, including empty ones, unlike the web source
/// (questions.md Q4): a column chart with holes punched in its axis cannot distinguish an empty
/// bucket from a missing one.</param>
public sealed record DataQualityStats(
    double? AvgHfr,
    double? AvgHfrArcsec,
    double? BestHfr,
    double? BestHfrArcsec,
    double? AvgEccentricity,
    int UnscaledFrameCount,
    int? EccentricityExcludedCount,
    string? EccentricitySource,
    IReadOnlyList<HfrBucket> HfrPixelHistogram,
    IReadOnlyList<HfrBucket> HfrArcsecHistogram);

/// <summary>Spec 12.5's Storage row, three figures rather than the web's four.</summary>
/// <param name="FitsBytesCatalogued">Sum of <c>images.file_size</c> over every row, calibration
/// included.</param>
/// <param name="ThumbnailCacheBytes">Supplied by the caller's delegate (questions.md Q12), 0 when
/// none was supplied.</param>
/// <param name="DatabaseBytes">The SQLite file's own size plus its write-ahead log, the local
/// equivalent of <c>pg_database_size</c>.</param>
/// <remarks>
/// The web's fourth figure, <c>fits_disk_bytes</c>, is deliberately absent: it comes from
/// <c>du -sb</c> over the FITS directory, and this port neither spawns a process to measure a user
/// directory nor walks a scan root on a page load (questions.md Q11, which amends spec 12.5's
/// Storage row).
/// </remarks>
public sealed record StorageStats(
    long FitsBytesCatalogued,
    long ThumbnailCacheBytes,
    long DatabaseBytes);

/// <summary>Files added on one day, from <c>scan_runs</c> in this port rather than from
/// <c>images.session_date</c> (spec 12.5's Ingest history row, questions.md Q13).</summary>
public sealed record IngestEntry(DateOnly Date, int FilesAdded);

/// <summary>One imaging night in the calendar's range. Grouped on <c>session_date</c> alone, which
/// is deliberately not <see cref="StatsOverview.RigSessionCount"/>'s grouping.</summary>
public sealed record CalendarEntry(
    DateOnly Date,
    double IntegrationSeconds,
    int TargetCount,
    int FrameCount);

/// <summary>The whole statistics response, returned by one <see cref="StatsQuery.Get"/> call to
/// match the web application's single <c>/api/stats</c> endpoint (spec 12.5's Query paragraph).
/// </summary>
/// <param name="Cameras">Two inventory lists rather than the web's single <c>equipment</c> list
/// with a section tag, because spec 12.5's Equipment inventory row names them as two and the view
/// renders two tables.</param>
/// <param name="RigsPerNight">Distinct canonical rigs that took a LIGHT frame on each date. It is
/// on the response rather than computed by the view because it is a database aggregate; the
/// efficiency percentage that divides by it is the App layer's arithmetic (questions.md Q6).
/// </param>
/// <param name="Guiding">Spec 12.5's Guiding section, from <see cref="GuidingStatsQuery"/> and
/// returned inside this one response rather than from a second call: the web fetches
/// <c>/api/stats/guiding</c> separately and caches it separately, and this port has one page, one
/// query and one cache, so a profile remap that moves a session from one rig to another drops the
/// guiding figures with the rest. Never null; a bare database gives
/// <c>UnmappedSessionCount</c> 0, empty lists and three empty baselines. Declared last so every
/// existing positional construction keeps its argument order.</param>
public sealed record StatsResponse(
    StatsOverview Overview,
    IReadOnlyList<EquipmentInventoryItem> Cameras,
    IReadOnlyList<EquipmentInventoryItem> Telescopes,
    IReadOnlyList<EquipmentComboMetrics> EquipmentPerformance,
    IReadOnlyList<FilterUsageEntry> FilterUsage,
    IReadOnlyList<TopTargetEntry> TopTargets,
    IReadOnlyList<TimelineEntry> TimelineMonthly,
    IReadOnlyList<TimelineEntry> TimelineWeekly,
    IReadOnlyList<TimelineEntry> TimelineDaily,
    IReadOnlyDictionary<DateOnly, int> RigsPerNight,
    DataQualityStats DataQuality,
    StorageStats Storage,
    IReadOnlyList<IngestEntry> IngestHistory,
    GuidingStats Guiding);

// ---- spec 12.5's Guiding section, answered by GuidingStatsQuery ---------------------------
//
// Port of schemas/stats_guiding.py plus the cross-rig baselines the web builds in its view
// (GuidingScorecard.tsx:62-86). Declared below StatsResponse on purpose: the join of
// GuidingStats onto StatsResponse is a separate change in a separate task, and appending here
// keeps the two edits off the same lines.

/// <summary>One canonical rig's whole guiding record (spec 12.5's Guiding section). Port of
/// <c>schemas/stats_guiding.py:13-24</c>, filled by <c>services/phd2_stats.py:49-65</c>.</summary>
/// <remarks>
/// Every RMS figure is frame-count weighted through <c>Phd2Metrics.WeightedRms</c>, the same
/// function a session card's night rollup uses, so the Statistics page and a session card never
/// disagree about what a night's RMS was (<c>phd2_stats.py:9-11</c>). The 100-frame gate of spec
/// 5.16 decides which rows feed those four figures and nothing else: a gated session still counts
/// in <see cref="SessionCount"/>, <see cref="GuidedHours"/>, <see cref="SettleMedianS"/> and
/// <see cref="ExposureMsValues"/>.
/// </remarks>
/// <param name="Telescope">The canonical telescope from the alias map, so two spellings of one
/// scope land in one row. The raw stored spelling never appears here.</param>
/// <param name="SessionCount">Every session of the rig, gated ones included.</param>
/// <param name="GatedSessionCount">Sessions under <c>Phd2Metrics.MinFrames</c>, the ones the four
/// RMS figures leave out. Exactly 100 frames is not gated.</param>
/// <param name="GuidedHours">Summed <c>duration_s</c> over hours, rounded to 2. Not nullable: a
/// rig with no duration sums to 0.0 and reports 0.0, as the web does.</param>
/// <param name="RmsTotalArcsec">Null when no session of the rig cleared the gate with a
/// value.</param>
/// <param name="RmsRaArcsec">As <see cref="RmsTotalArcsec"/>.</param>
/// <param name="RmsDecArcsec">As <see cref="RmsTotalArcsec"/>.</param>
/// <param name="RmsTotalFilteredArcsec">The same weighting over
/// <c>rms_total_filtered_arcsec</c>, spec 7.6's one-pass 5 sigma excursion filter.</param>
/// <param name="RaDecRatio"><see cref="RmsDecArcsec"/> over <see cref="RmsRaArcsec"/>, deliberately
/// unrounded (<c>phd2_stats.py:60</c>); the two decimals the scorecard prints are a formatter's.
/// Null when either figure is null <b>and</b> when <see cref="RmsRaArcsec"/> is exactly 0, because
/// the Python reads <c>if ra</c>, which is false for zero as well as for null.</param>
/// <param name="SettleMedianS">Median of the rig's non-null per-session medians, deliberately
/// <b>not</b> rounded here (<c>phd2_stats.py:61</c>) although the night rollup rounds its own to 3
/// (<c>phd2_metrics.py:545</c>). The two are different figures and the port keeps the difference.
/// Null when no session of the rig stored one.</param>
/// <param name="ExposureMsValues">The distinct guide exposures the rig ran, ascending. Empty when
/// no session carried one.</param>
public sealed record GuidingRig(
    string Telescope,
    int SessionCount,
    int GatedSessionCount,
    double GuidedHours,
    double? RmsTotalArcsec,
    double? RmsRaArcsec,
    double? RmsDecArcsec,
    double? RmsTotalFilteredArcsec,
    double? RaDecRatio,
    double? SettleMedianS,
    IReadOnlyList<int> ExposureMsValues);

/// <summary>The three altitude buckets the guiding arc draws, in the web's own declaration order
/// (<c>phd2_stats.py:28</c>), which is also the order the rows sort in.</summary>
/// <remarks>
/// The web carries these as the string literals <c>&lt;30</c>, <c>30-60</c> and <c>&gt;60</c> and
/// orders by <c>ALTITUDE_BANDS.index(...)</c>; an enum gives that ordering for free. Nothing
/// serialises these names: the labels the page prints are the App layer's text, not a stored key.
/// Both boundaries are exact, so 30.0 is <see cref="From30To60"/> and 60.0 is
/// <see cref="Above60"/>.
/// </remarks>
public enum GuidingAltitudeBand
{
    /// <summary>Altitude below 30 degrees.</summary>
    Below30,

    /// <summary>Altitude from 30 degrees up to but not including 60.</summary>
    From30To60,

    /// <summary>Altitude of 60 degrees and above.</summary>
    Above60,
}

/// <summary>One (canonical rig, altitude band) bucket. Port of
/// <c>schemas/stats_guiding.py:27-33</c>, filled by <c>phd2_stats.py:95-101</c>.</summary>
/// <remarks>
/// A pair with no session produces no row at all and the view fills the gap. The altitude itself
/// comes from the guiding section header's own pointing line and from nowhere else (ruling G3):
/// the arc needs no observer coordinates and computes no altitude. A session with a null
/// <c>alt_deg</c>, which is the ASIAIR shape, is in its rig row and in no band row.
/// </remarks>
/// <param name="SessionCount">Every session of the bucket, gated ones included, so a band whose
/// only session is under the gate has a non-zero count and three null RMS figures.</param>
/// <param name="RmsTotalArcsec">The same frame-count weighted figure as the rig row, over this
/// bucket's own sessions. Null when no session of the bucket cleared the gate with a value.</param>
/// <param name="RmsRaArcsec">As <see cref="RmsTotalArcsec"/>.</param>
/// <param name="RmsDecArcsec">As <see cref="RmsTotalArcsec"/>.</param>
public sealed record GuidingAltitudeBandRow(
    string Telescope,
    GuidingAltitudeBand Band,
    int SessionCount,
    double? RmsTotalArcsec,
    double? RmsRaArcsec,
    double? RmsDecArcsec);

/// <summary>The cross-rig grading input the scorecard colours its cells against, one
/// <see cref="MetricBaseline"/> per graded metric over the rig rows of the same response.</summary>
/// <remarks>
/// The web builds this in the view (<c>GuidingScorecard.tsx:62-86</c>); the port builds it in the
/// query so the App layer keeps the one baseline builder it already has, and so the grading input
/// is a Data figure a Data case can pin. Each baseline's <c>N</c> counts the non-null values of
/// that one metric and never the number of rigs, which is what
/// <c>MetricBaseline</c>'s own summary warns about: a library of nine rigs where two carry no RMS
/// grades against seven values, not nine. <c>FrameQuality.MadZ</c> gates on
/// <c>N &lt; FrameQuality.MinGroup</c>, so a library with one rig, or with seven, grades nothing
/// and every cell is neutral. That is ruling G4 and it needs no rig-count check of its own.
/// </remarks>
public sealed record GuidingBaselines(
    MetricBaseline RmsTotal,
    MetricBaseline RmsRa,
    MetricBaseline RmsDec);

/// <summary>The whole answer of <see cref="GuidingStatsQuery.Get"/>. Port of
/// <c>schemas/stats_guiding.py:36-39</c> plus <see cref="Baselines"/>.</summary>
/// <param name="UnmappedSessionCount">Every session row whose <c>telescope</c> is null, that is
/// whose equipment profile is mapped to no rig (<c>phd2_stats.py:83-85</c>). Counted across the
/// whole corpus and not per night, and it is what the page's empty notice prints.</param>
/// <param name="Rigs">Ordinal ascending by <see cref="GuidingRig.Telescope"/>. Empty on a library
/// whose every profile is unmapped, which is the state a fresh install and a bare CLI scan leave.
/// Never null.</param>
/// <param name="AltitudeBands">Ordered by telescope ordinal, then by band declaration order. Never
/// null.</param>
/// <param name="Baselines">Three baselines of <c>MetricBaseline(null, null, 0)</c> on a bare
/// database. Never null.</param>
public sealed record GuidingStats(
    int UnmappedSessionCount,
    IReadOnlyList<GuidingRig> Rigs,
    IReadOnlyList<GuidingAltitudeBandRow> AltitudeBands,
    GuidingBaselines Baselines);
