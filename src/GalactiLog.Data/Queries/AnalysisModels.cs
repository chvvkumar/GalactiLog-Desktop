using GalactiLog.Core.Metrics;

namespace GalactiLog.Data.Queries;

// Port of backend/app/api/analysis.py and backend/app/schemas/analysis.py, spec 12.14
// (core-shapes.md, phase17). Records and enums only, landed by the records-first pre-step with no
// logic: AnalysisQuery, AnalysisCache and AnalysisMetrics.Column/Key/Parse are Task 3's.

/// <summary>The twenty five metrics the Analysis page reads, in <c>analysis.py</c>'s own
/// <c>METRIC_MAP</c> and <c>PHD2_X_METRICS</c> order (lines 51 to 98) so a reviewer can diff this
/// declaration against the source. The first ten are environmental and equipment figures, X
/// candidates only (<c>X_METRICS</c>, line 82). The next ten are quality figures, Y candidates
/// only (<c>Y_METRICS</c>, line 86). The last five are PHD2 night metrics, X candidates for the
/// Correlation tab only and never a Y or a matrix member (<c>PHD2_X_METRICS</c>, line 95).
/// <c>AnalysisMetrics.Key</c> is the persisted and oracle string for each member;
/// <c>AnalysisMetrics.Parse</c> is its inverse.</summary>
public enum AnalysisMetric
{
    // Environmental and equipment, X candidates (METRIC_MAP lines 53 to 62, X_METRICS line 82).
    Humidity, WindSpeed, AmbientTemp, DewPoint, Pressure,
    CloudCover, SkyQuality, FocuserTemp, Airmass, SensorTemp,
    // Quality, Y candidates (METRIC_MAP lines 64 to 73, Y_METRICS line 86).
    Hfr, Fwhm, Eccentricity, GuidingRms, GuidingRmsRa,
    GuidingRmsDec, DetectedStars, AduMean, AduMedian, AduStdev,
    // PHD2 night metrics, X only and correlation only (PHD2_X_METRICS line 95).
    Phd2RmsTotal, Phd2RmsRa, Phd2RmsDec, Phd2StarLostPct, Phd2SnrMean,
}

/// <summary>Frame-level or session-level rows (<c>analysis.py</c> line 358). <see cref="Frame"/>
/// is the default and the zero member, matching the Python's <c>Query("frame", ...)</c>.</summary>
public enum AnalysisGranularity { Frame, Session }

/// <summary>How the Distributions tab's box plot groups its frames (<c>analysis.py</c> line
/// 570).</summary>
public enum BoxPlotGrouping { Filter, Equipment, Month, Target }

/// <summary>What the Compare tab's two groups are (<c>analysis.py</c> line 824).</summary>
public enum CompareMode { Equipment, Filter }

/// <summary>
/// The one encoding an Equipment-mode Compare group string uses, so the writer and the reader
/// cannot drift (task 6 review, escalation 4).
/// </summary>
/// <remarks>
/// The App's picker joins the two raw canonical names with <see cref="EquipmentSeparator"/> and
/// <c>AnalysisQuery.Compare</c> splits on it. Before this, the tab authored one spelling and the
/// query held a bare literal, and a drift between them was silent: the query's own guard let a
/// string that did not split into exactly two parts fall through to a filter with no equipment
/// predicate, so both groups scoped the whole library, read the same rows and printed
/// "Both groups have identical median values", with no empty state, no exception and no log line.
/// The guard now fails closed instead, which is a documented departure from
/// <c>analysis.py</c> line 860.
/// </remarks>
public static class CompareGroups
{
    /// <summary>The separator between the telescope and the camera in an Equipment-mode group
    /// string, which is what pinned <c>CompareTab.tsx</c> line 97 joins with and what
    /// <c>analysis.py</c> line 859 splits on.</summary>
    public const string EquipmentSeparator = "|||";
}

/// <summary>The three fixed lists <c>analysis.py</c> keys its matrix and its metric pickers from.
/// Task 3 adds <c>Key</c>, <c>Parse</c> and the internal column lookup; this pre-step
/// lands only the lists themselves, because Task 2 and Task 3 both need the shape before either
/// exists.</summary>
public static class AnalysisMetrics
{
    /// <summary>The matrix's rows, in <c>X_METRICS</c> order (line 82). Never a PHD2 member (lines
    /// 92 to 94), which is what keeps the matrix at exactly one hundred cells (ruling
    /// A26).</summary>
    public static readonly IReadOnlyList<AnalysisMetric> X =
    [
        AnalysisMetric.Humidity, AnalysisMetric.WindSpeed, AnalysisMetric.AmbientTemp,
        AnalysisMetric.DewPoint, AnalysisMetric.Pressure, AnalysisMetric.CloudCover,
        AnalysisMetric.SkyQuality, AnalysisMetric.FocuserTemp, AnalysisMetric.Airmass,
        AnalysisMetric.SensorTemp,
    ];

    /// <summary>The matrix's columns, in <c>Y_METRICS</c> order (line 86).</summary>
    public static readonly IReadOnlyList<AnalysisMetric> Y =
    [
        AnalysisMetric.Hfr, AnalysisMetric.Fwhm, AnalysisMetric.Eccentricity,
        AnalysisMetric.GuidingRms, AnalysisMetric.GuidingRmsRa, AnalysisMetric.GuidingRmsDec,
        AnalysisMetric.DetectedStars, AnalysisMetric.AduMean, AnalysisMetric.AduMedian,
        AnalysisMetric.AduStdev,
    ];

    /// <summary>Correlation X candidates only, in <c>PHD2_X_METRICS</c> order (line 95). Every
    /// other tab rejects a member of this list.</summary>
    public static readonly IReadOnlyList<AnalysisMetric> Phd2X =
    [
        AnalysisMetric.Phd2RmsTotal, AnalysisMetric.Phd2RmsRa, AnalysisMetric.Phd2RmsDec,
        AnalysisMetric.Phd2StarLostPct, AnalysisMetric.Phd2SnrMean,
    ];

    // One table, indexed by the enum's own ordinal, rather than three switch expressions that can
    // disagree. Column is empty for the five PHD2 members, which are derived from phd2_sessions
    // and have no images column at all (AnalysisQuery.Correlation is the only reader and it
    // resolves them through the night rollup instead).
    //
    // The key is METRIC_MAP's own string (lines 53 to 73) and PHD2_X_METRICS' (line 96 to 97); the
    // column is the images column that key maps to. Four of the twenty differ from their key:
    // hfr reads median_hfr and the three guiding figures read the _arcsec spellings. Fwhm reads
    // images.fwhm, the N.I.N.A. Session Metadata CSV column already in arcseconds (line 65), and
    // NOT median_fwhm, which is a different column that no analysis endpoint reads.
    private static readonly (string Key, string Column)[] Table =
    [
        ("humidity", "humidity"),
        ("wind_speed", "wind_speed"),
        ("ambient_temp", "ambient_temp"),
        ("dew_point", "dew_point"),
        ("pressure", "pressure"),
        ("cloud_cover", "cloud_cover"),
        ("sky_quality", "sky_quality"),
        ("focuser_temp", "focuser_temp"),
        ("airmass", "airmass"),
        ("sensor_temp", "sensor_temp"),
        ("hfr", "median_hfr"),
        ("fwhm", "fwhm"),
        ("eccentricity", "eccentricity"),
        ("guiding_rms", "guiding_rms_arcsec"),
        ("guiding_rms_ra", "guiding_rms_ra_arcsec"),
        ("guiding_rms_dec", "guiding_rms_dec_arcsec"),
        ("detected_stars", "detected_stars"),
        ("adu_mean", "adu_mean"),
        ("adu_median", "adu_median"),
        ("adu_stdev", "adu_stdev"),
        ("phd2_rms_total", ""),
        ("phd2_rms_ra", ""),
        ("phd2_rms_dec", ""),
        ("phd2_star_lost_pct", ""),
        ("phd2_snr_mean", ""),
    ];

    /// <summary>The web's own metric string, which is what <c>display.analysis</c> persists and
    /// what the oracle is fed. Not a display label: spec 12.14's label table is the App layer's
    /// and no string of it lives here.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value outside the enum's own set, which is
    /// the refusal every member of this page documents.</exception>
    public static string Key(AnalysisMetric metric) => Entry(metric).Key;

    /// <summary>The inverse of <see cref="Key"/>, answering null for anything outside the set, so
    /// ruling A9's "a stored value outside a key's set reads as the default" is served without a
    /// throw on a hand-edited settings document.</summary>
    public static AnalysisMetric? Parse(string? key)
    {
        if (key is null)
        {
            return null;
        }

        for (var i = 0; i < Table.Length; i++)
        {
            if (string.Equals(Table[i].Key, key, StringComparison.Ordinal))
            {
                return (AnalysisMetric)i;
            }
        }

        return null;
    }

    /// <summary>The <c>images</c> column this metric reads. Internal because nothing outside the
    /// query has any business with a column name, and empty for the five PHD2 members, which have
    /// no column.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value outside the enum's own set.
    /// </exception>
    internal static string Column(AnalysisMetric metric) => Entry(metric).Column;

    // The one bounds check for both readers. The table is indexed by the enum's own ordinal, so a
    // value outside it indexed the array directly and raised IndexOutOfRangeException where the
    // published refusal on this page is ArgumentOutOfRangeException. AnalysisQuery's own guards
    // test Enum.IsDefined before they read a column and never reach this; AnalysisCache builds its
    // discriminator from Key BEFORE it delegates, so it does.
    private static (string Key, string Column) Entry(AnalysisMetric metric)
        => (uint)metric < (uint)Table.Length
            ? Table[(int)metric]
            : throw new ArgumentOutOfRangeException(
                nameof(metric), metric, "Not an Analysis metric.");
}

/// <summary>The filter every Analysis tab is handed, applied per tab as spec 12.14's "Where the
/// filters apply" table states (ruling A17): a tab that ignores part of this record is parity with
/// the web, not an omission, and <c>AnalysisQuery</c>'s own member docs say which part each
/// tab uses.</summary>
/// <param name="Telescope">A CANONICAL telescope name, expanded to every stored variant inside the
/// query (lines 157 to 163). Null applies no telescope filter.</param>
/// <param name="Camera">A CANONICAL camera name, expanded the same way.</param>
/// <param name="FilterUsed">A RAW stored value compared with equality and never folded through the
/// alias map, because line 165 is <c>Image.filter_used == filter_used</c> with no alias expansion
/// at all (a suspected web defect, section 8).</param>
/// <param name="Granularity">Frame or session rows, where the tab honours it.</param>
/// <param name="From">The inclusive start of the date range, or null for no lower bound.</param>
/// <param name="To">The inclusive end of the date range, or null for no upper bound.</param>
public sealed record AnalysisFilter(
    string? Telescope,
    string? Camera,
    string? FilterUsed,
    AnalysisGranularity Granularity,
    DateOnly? From,
    DateOnly? To);

/// <summary>One frame plotted on the Correlation tab's scatter.</summary>
/// <param name="X">The X metric's value for this frame.</param>
/// <param name="Y">The Y metric's value for this frame.</param>
/// <param name="Night">The frame's session date.</param>
/// <param name="TargetId">The frame's target, or null when it could not be resolved.</param>
/// <param name="Outlier">Whether the point falls outside the box plot fences of its own axis.
/// </param>
public sealed record CorrelationPoint(
    double X, double Y, DateOnly Night, Guid? TargetId, bool Outlier);

/// <summary>The Correlation tab's answer for one metric pair and filter.</summary>
/// <param name="Points">The plotted points, downsampled to <see cref="SampledCount"/> when
/// <see cref="TotalCount"/> exceeds the cap.</param>
/// <param name="Trend">The least squares trend line, or null when there were too few points or the
/// fit was degenerate.</param>
/// <param name="XStats">The summary of every X value read, before downsampling.</param>
/// <param name="YStats">The summary of every Y value read, before downsampling.</param>
/// <param name="TargetNames">Every target id named in <see cref="Points"/>, resolved to its
/// display name.</param>
/// <param name="Quality">Why the trend's Pearson r is exactly zero, when it is. Has no web
/// counterpart (section 3.1 of the seam).</param>
/// <param name="DistinctPlateScales">The count of distinct non-null plate scales among the frames
/// read, user ruling U3. Zero when no frame carried one, one for a single optical train, more than
/// one when the selection spans several.</param>
/// <param name="TotalCount">Every point the query read, before downsampling.</param>
/// <param name="SampledCount">How many of <see cref="TotalCount"/> are actually in
/// <see cref="Points"/>, at most <c>Analysis.CorrelationPointCap</c>.</param>
public sealed record CorrelationResult(
    IReadOnlyList<CorrelationPoint> Points,
    TrendLine? Trend,
    SummaryStats? XStats,
    SummaryStats? YStats,
    IReadOnlyDictionary<Guid, string> TargetNames,
    CorrelationQuality Quality,
    int DistinctPlateScales,
    int TotalCount,
    int SampledCount);

/// <summary>The Distributions tab's histogram mode answer for one metric and filter. Null from
/// <c>AnalysisQuery.Distribution</c> when there were too few values, which replaces the
/// web's HTTP 400 (line 529): an exception is not an answer a page can render.</summary>
/// <param name="Bins">The histogram's bins, left to right.</param>
/// <param name="Stats">The summary of every value read.</param>
/// <param name="Skewness">The distribution's skewness, computed from <see cref="Stats"/>'s already
/// rounded mean and standard deviation.</param>
/// <param name="DistinctPlateScales">As <see cref="CorrelationResult.DistinctPlateScales"/>.
/// </param>
public sealed record DistributionResult(
    IReadOnlyList<HistogramBin> Bins, SummaryStats Stats, double Skewness,
    int DistinctPlateScales);

/// <summary>The Distributions tab's box plot mode answer for one metric, grouping and filter.
/// </summary>
/// <param name="Groups">One box per group that had enough values, in the query's own order, which
/// is ascending ordinal by <see cref="BoxPlot.GroupName"/> (<c>analysis.py</c> line 649's
/// <c>sorted(grouped.items())</c>). The tab draws them as they arrive and orders nothing of its
/// own.</param>
/// <param name="DistinctPlateScales">As <see cref="CorrelationResult.DistinctPlateScales"/>.
/// </param>
/// <param name="RowCount">Every row the query read after its filters and its non-null metric
/// predicate and BEFORE any grouping key, which is precisely the population
/// <paramref name="DistinctPlateScales"/> is counted over. It exists so the tab can tell spec
/// 12.14's "Box plot with no group left" from "No row matches the filters": an empty
/// <paramref name="Groups"/> is the same value whether no row matched at all or every group was
/// dropped at the four-value gate, and the two rows call for opposite actions, so a reader given
/// the wrong one is told to widen the filters when grouping more coarsely is what would help.
/// Counted before the grouping key, not after, so the figure does not depend on which grouping the
/// reader picked.</param>
public sealed record BoxPlotResult(
    IReadOnlyList<BoxPlot> Groups, int DistinctPlateScales, int RowCount);

/// <summary>One night's value on the Time Series tab.</summary>
/// <param name="Date">The session date.</param>
/// <param name="Value">The nightly median of the metric.</param>
/// <param name="TargetName">The night's target name, non-null only when <see cref="TargetCount"/>
/// is exactly 1. Where the web picks an arbitrary member of a Python set on a two-target night
/// (defect D4, line 713), the port names a target only when there is exactly one and otherwise
/// leaves this null, and the view reads "Mixed".</param>
/// <param name="TargetCount">How many distinct targets the night resolved to. Has no web
/// counterpart: a single nullable name could not tell a "Mixed" night from a night with no target
/// resolved at all.</param>
/// <param name="FrameCount">How many frames contributed to the night's median.</param>
public sealed record TimeSeriesPoint(
    DateOnly Date, double Value, string? TargetName, int TargetCount, int FrameCount);

/// <summary>One point of a moving average over the Time Series tab's nightly values.</summary>
public sealed record MovingAveragePoint(DateOnly Date, double Value);

/// <summary>The Time Series tab's answer for one metric and filter. Granularity is always nightly
/// medians here (line 661): <see cref="AnalysisFilter.Granularity"/> is ignored, which is parity
/// with the web and not an omission (ruling A17).</summary>
/// <param name="Points">The nightly values, in date order.</param>
/// <param name="Ma7">The short moving average, window
/// <c>Analysis.MovingAverageShortWindow</c>.</param>
/// <param name="Ma30">The long moving average, window <c>Analysis.MovingAverageLongWindow</c>.
/// </param>
/// <param name="MonthBoundaries">The dates where a calendar month begins. Computed for parity with
/// the pinned web, which computes it and never draws it; no Analysis view draws a month gridline.
/// </param>
/// <param name="DistinctPlateScales">As <see cref="CorrelationResult.DistinctPlateScales"/>.
/// </param>
public sealed record TimeSeriesResult(
    IReadOnlyList<TimeSeriesPoint> Points,
    IReadOnlyList<MovingAveragePoint> Ma7,
    IReadOnlyList<MovingAveragePoint> Ma30,
    IReadOnlyList<DateOnly> MonthBoundaries,
    int DistinctPlateScales);

/// <summary>One cell of the correlation matrix.</summary>
/// <param name="X">The row metric, always a member of <see cref="AnalysisMetrics.X"/>.</param>
/// <param name="Y">The column metric, always a member of <see cref="AnalysisMetrics.Y"/>.</param>
/// <param name="PearsonR">PostgreSQL's <c>corr(Y, X)</c> over the pair's non-null rows, from
/// <c>Analysis.MatrixPearson</c>, not <c>Analysis.PearsonR</c> (ruling A19). Null when
/// <see cref="NPoints"/> is below <c>Analysis.MatrixMinimumPoints</c> or the pair's variance is
/// zero on either side.</param>
/// <param name="NPoints">How many rows had both metrics non-null.</param>
public sealed record MatrixCell(
    AnalysisMetric X, AnalysisMetric Y, double? PearsonR, int NPoints);

/// <summary>The Matrix tab's answer: exactly one hundred cells, <see cref="AnalysisMetrics.X"/> by
/// <see cref="AnalysisMetrics.Y"/> (ruling A26).</summary>
/// <param name="Cells">The cells, in <see cref="AnalysisMetrics.X"/> then
/// <see cref="AnalysisMetrics.Y"/> order.</param>
/// <param name="DistinctPlateScales">As <see cref="CorrelationResult.DistinctPlateScales"/>.
/// </param>
public sealed record MatrixResult(
    IReadOnlyList<MatrixCell> Cells, int DistinctPlateScales);

/// <summary>One side of the Compare tab, in pixels for the box and the summary and, separately, in
/// arcseconds for the verdict (ruling A18).</summary>
public sealed record CompareGroup(string Name, BoxPlot Box, SummaryStats Stats);

/// <summary>Which of the Compare tab's four short-of-data shapes <see cref="CompareResult"/>
/// answers, ruling S7 (seam review P1-7). The web's HTTP 400 (line 883) becomes this state rather
/// than a null result, because 12.14's States table requires naming WHICH group is short and a
/// bare null carries no count to name it with.</summary>
public enum CompareState
{
    /// <summary>Both groups hold at least 4 values. <see cref="CompareResult.GroupA"/> and
    /// <see cref="CompareResult.GroupB"/> are non-null only in this state.</summary>
    Ok,
    /// <summary>Group A holds fewer than 4 values, group B holds at least 4.</summary>
    GroupAShort,
    /// <summary>Group B holds fewer than 4 values, group A holds at least 4.</summary>
    GroupBShort,
    /// <summary>Both groups hold fewer than 4 values, and at least one holds a row.</summary>
    BothShort,
    /// <summary>Neither group matched a single row.</summary>
    NoRows,
}

/// <summary>The Compare tab's answer for one metric, mode and pair of group names. Never null
/// (ruling S7): the web's HTTP 400 (line 883) becomes <see cref="State"/> rather than an absent
/// result, so the tab can say which group is short instead of only that the tab is empty.</summary>
/// <param name="State">Which of the four short-of-data shapes applied, or <see cref="CompareState.Ok"/>.
/// </param>
/// <param name="NameA">The first group's name, always populated, name and count kept apart from
/// <see cref="GroupA"/> so a short group can still be named in the tab's sentence.</param>
/// <param name="NameB">The second group's name, always populated, under the same rule.</param>
/// <param name="CountA">The first group's RAW value count, before the four value gate, so it is
/// the figure the "group A holds only n values" sentence prints.</param>
/// <param name="CountB">The second group's raw value count, under the same rule.</param>
/// <param name="GroupA">The first group's box and summary. Non-null exactly when
/// <see cref="State"/> is <see cref="CompareState.Ok"/>.</param>
/// <param name="GroupB">The second group's box and summary, under the same rule.</param>
/// <param name="Verdict">The comparison sentence, from <c>Analysis.CompareVerdict</c>. Null exactly
/// when <see cref="Comparable"/> is false: the web's own "cannot be compared" sentence that reads
/// the two arcsecond medians is unreachable (section 5.4 item 8), so only the reachable sentence is
/// built, and Task 6 composes it from <see cref="Comparable"/> and the two medians.</param>
/// <param name="Comparable">Whether both groups had at least four plate-scaled values.</param>
/// <param name="MedianArcsecA">The first group's median in arcseconds, or null when it has fewer
/// than four plate-scaled values.</param>
/// <param name="MedianArcsecB">The second group's median in arcseconds, under the same rule.
/// </param>
public sealed record CompareResult(
    CompareState State,
    string NameA,
    string NameB,
    int CountA,
    int CountB,
    CompareGroup? GroupA,
    CompareGroup? GroupB,
    string? Verdict,
    bool Comparable,
    double? MedianArcsecA,
    double? MedianArcsecB);

/// <summary>One canonical telescope and camera pairing the Analysis page's pickers can offer,
/// projected from <see cref="StatsCache"/>'s equipment performance list rather than from a query of
/// its own (section 6.7 of the seam).</summary>
/// <param name="Grouped">True when more than one raw name folded onto this canonical
/// pairing.</param>
public sealed record EquipmentCombination(string Telescope, string Camera, bool Grouped);

/// <summary>The key <c>AnalysisCache</c> builds at its one choke point and never a caller
/// (design lesson 2). A record, so the cache keys on structural equality and nothing concatenates
/// strings the way the web's <c>:</c>-joined key does, which two different filters can collide on
/// when a telescope name contains a colon.</summary>
/// <param name="Tab">One of <c>correlation</c>, <c>distribution</c>, <c>boxplot</c>,
/// <c>timeseries</c>, <c>matrix</c>, <c>compare</c>, <c>filters</c>.</param>
/// <param name="Discriminator">Whatever else the tab's answer depends on beyond
/// <paramref name="Filter"/>, for example the metric pair or the grouping.</param>
/// <param name="Filter">The filter the answer was computed under.</param>
/// <param name="GroupA">The Compare tab's first group string, carried as its own member because it
/// is a catalogue value and not a closed vocabulary: joined into
/// <paramref name="Discriminator"/> with any separator, two different pairs of group names can
/// build one string and the second call is served the first's answer. Null on every other
/// tab.</param>
/// <param name="GroupB">The Compare tab's second group string, under the same rule.</param>
public sealed record AnalysisCacheKey(
    string Tab,
    string Discriminator,
    AnalysisFilter Filter,
    string? GroupA = null,
    string? GroupB = null);
