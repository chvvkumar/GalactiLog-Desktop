using System.Globalization;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>
/// Everything an expanded session card shows (spec 12.4), port of
/// <c>target_detail.get_session_detail</c>: the session's ranges, per-filter medians and detail
/// rows, the median airmass, ambient temperature and humidity, the session insights, and the
/// frame rows the frame table and the per-session chart both bind to.
/// </summary>
/// <remarks>
/// <para>
/// Issued when a card expands, never up front, which is why it is a separate query from
/// <see cref="TargetDetailQuery"/> rather than a fourth statement on it. The laziness itself is a
/// view-model property (Task 4 asserts it); this type only keeps the read cheap enough that one
/// per expansion is fine.
/// </para>
/// <para>
/// Two statements in one round trip over a short-lived <see cref="GalactiLogContext"/> so
/// <see cref="PragmaConnectionInterceptor"/> stays in the path, then all aggregation in C#,
/// because SQLite has no median. The group is named by <see cref="SqlFragments.GroupScope"/>, so
/// a card can never describe a group the dashboard does not produce.
/// </para>
/// <para>
/// Spec 7.1.1: <c>median_fwhm</c> appears nowhere in this file. <c>fwhm</c> is the only FWHM in
/// every median, range and chart series; Task 6's raw header panel is the only reader of the
/// other one.
/// </para>
/// </remarks>
public sealed class SessionDetailQuery(
    DatabaseConnectionString connectionString,
    AliasMapCache aliases,
    RigBaselinesCache rigBaselines)
{
    /// <summary>Null when the group has no LIGHT frame on that date.</summary>
    /// <param name="groupKey">A <c>TargetRow.GroupKey</c>, the same value
    /// <c>TargetDetailQuery.Get</c> took.</param>
    /// <param name="sessionDate">The card's session date. Ruling Q5: a frame with a null
    /// <c>session_date</c> is in no session, so it is unreachable through this query.</param>
    public SessionDetail? Get(string groupKey, DateOnly sessionDate)
    {
        var map = aliases.Current;

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        var (rows, notes) = Read(connection, groupKey, sessionDate, map);
        if (rows.Count == 0)
        {
            return null;
        }

        // P25 R3: the builders the merge shares read frame rows, materialised once here.
        IReadOnlyList<FrameRow> frames = rows.ConvertAll(row => row.Frame);

        // Spec 7.2, coordinator ruling: one modal source decides every eccentricity figure on
        // this card, so the ranges, the per-filter medians and the outlier baselines all describe
        // the same source and the card can name it.
        EccentricitySources.TryGetModalSource(
            rows.Select(row => (row.EccentricitySource, row.Frame.Eccentricity)),
            out var modalSource);
        var excludedEccentricity = rows.Count(row =>
            row.Frame.Eccentricity is not null
            && !EccentricitySources.IsPooled(row.EccentricitySource, row.Frame.Eccentricity, modalSource));

        var graded = rows.ConvertAll(row => new GradedFrame(
            row.Telescope,
            row.Camera,
            row.Frame.FilterUsed,
            row.Frame.MedianHfr,
            row.Frame.Fwhm,
            PooledEccentricity(row.Frame, modalSource),
            row.Frame.DetectedStars,
            row.Frame.AduMedian,
            row.Frame.GuidingRmsArcsec));
        var sessionBaselines = FrameQuality.GroupBaselines(graded);

        // P12 R3: the per-frame half of the two outlier insights, computed here because this is
        // the only place that holds both the session median and the session baselines. Read has
        // neither, and threading them into a 39 column reader loop would put one rule in two
        // places.
        var hfrFlags = FlagHfrOutliers([.. rows.Select(row => row.Frame.MedianHfr)]);
        var eccentricityFlags = FlagMadOutliers(graded, sessionBaselines, FrameQuality.EccentricityMetric);
        // P24 R22: the same MAD rule over the other graded metrics, for the frames strip's
        // outlier pills; no insight sentence is built for them.
        var fwhmFlags = FlagMadOutliers(graded, sessionBaselines, "fwhm");
        var starsFlags = FlagMadOutliers(graded, sessionBaselines, "detected_stars", higherIsBetter: true);
        var guidingRmsFlags = FlagMadOutliers(graded, sessionBaselines, "guiding_rms_arcsec");

        // Spec 12.4's per-frame grading, beside the two flag arrays and for the same reason: this
        // is the only place that holds both baselines. No statement is issued for it, so the page
        // still costs one session query.
        var grading = BuildGrading(graded, sessionBaselines, modalSource);

        var arcsec = HfrArcsec(frames);
        var first = rows[0];
        var last = rows[^1];

        // Spec 12.4's rig order, built once here and handed to everything that needs it: the
        // insights, the two per-rig table splits and the rig groups themselves. "Ordered by first
        // capture time within the night" is the first appearance of each label in row order,
        // because Read already ordered the rows by capture_date (questions.md Q10). One order, so
        // the third per-rig insight cannot name a different rig from the third thumbnail.
        var rigOrder = RigOrder(frames);

        // The night-level reference trio (SessionDetail.ReferenceImageId, ReferenceFramePath and
        // ReferenceCandidates). Spec 12.4's queries paragraph declares it, and nothing under src/
        // reads it: the thumbnail strip is one box per rig and takes each rig group's own
        // candidates. It is a declared-but-unread member of the read model, kept because the
        // contract names it and because a single-rig night's trio is what a caller with no rig
        // list would need; Task 8 records that in TRACKING rather than the member being dropped
        // silently (Task 5 review P3).
        var nightReference = RankReference(frames);

        return new SessionDetail(
            groupKey,
            sessionDate,
            rows.Count,
            rows.Sum(row => row.Frame.ExposureTime ?? 0d),
            Range(rows.Select(row => row.Frame.MedianHfr)),
            Statistics.Median(arcsec.Values),
            arcsec.ExcludedCount,
            Range(frames.Select(frame => PooledEccentricity(frame, modalSource))),
            modalSource,
            excludedEccentricity,
            Range(rows.Select(row => row.Frame.Fwhm)),
            Range(rows.Select(row => row.Frame.GuidingRmsArcsec)),
            Range(rows.Select(row => row.Frame.SensorTemp)),
            // Rule 5: the capture-ordered first frame's gain and capture time, and the last
            // frame's capture time. A frame with no capture_date sorts first under SQLite's null
            // ordering, and a null first or last time stays null rather than borrowing another
            // frame's: an unknown start is not the second frame's start.
            first.Frame.CameraGain,
            [.. rows.Select(row => row.Frame.ExposureTime).OfType<double>().Distinct().Order()],
            first.Frame.CaptureDate,
            last.Frame.CaptureDate,
            SplitPerRig(frames, rigOrder, group => BuildFilterMedians(group, modalSource)),
            SplitPerRig(frames, rigOrder, group => BuildFilterDetails(group, modalSource)),
            Statistics.Median(rows.Select(row => row.Frame.Airmass)),
            Statistics.Median(rows.Select(row => row.Frame.AmbientTemp)),
            Statistics.Median(rows.Select(row => row.Frame.Humidity)),
            BuildInsights(rows, graded, sessionBaselines, modalSource, hfrFlags, rigOrder),
            [.. rows.Select((row, index) => row.Frame with
            {
                IsHfrOutlier = hfrFlags[index],
                IsEccentricityOutlier = eccentricityFlags[index],
                IsFwhmOutlier = fwhmFlags[index],
                IsStarsOutlier = starsFlags[index],
                IsGuidingRmsOutlier = guidingRmsFlags[index],
                Grading = grading[index],
            })],
            notes,
            BuildRigGroups(rows, rigOrder, modalSource),
            nightReference.ImageId,
            nightReference.Paths.Count > 0 ? nightReference.Paths[0] : null,
            nightReference.Paths,
            SplitPerRig(frames, rigOrder, BuildFilterAcquisitions));
    }

    // ---- the night's rigs (PAR-004) and their reference frames (PAR-008) ----------------

    /// <summary>Spec 12.4's rig order: first capture time within the night. The rows arrive
    /// ordered by <c>capture_date</c>, so the first appearance of each label in row order is the
    /// answer and no second sort is needed. The one place this order is decided
    /// (questions.md Q10).</summary>
    internal static IReadOnlyList<string> RigOrder(IReadOnlyList<FrameRow> frames)
    {
        List<string> order = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var frame in frames)
        {
            if (seen.Add(frame.Rig))
            {
                order.Add(frame.Rig);
            }
        }

        return order;
    }

    /// <summary>Ruling C4: on a multi-rig night the per-filter blocks are built per rig and
    /// concatenated in rig order, each row carrying its rig's label; on a single-rig night the
    /// builder runs exactly as it did before this phase and every label stays null. One helper for
    /// both tables, so the two blocks cannot split on different rules.</summary>
    internal static IReadOnlyList<T> SplitPerRig<T>(
        IReadOnlyList<FrameRow> frames,
        IReadOnlyList<string> rigOrder,
        Func<IReadOnlyList<FrameRow>, IReadOnlyList<T>> build)
        where T : class
    {
        if (rigOrder.Count <= 1)
        {
            return build(frames);
        }

        List<T> all = [];
        foreach (var label in rigOrder)
        {
            all.AddRange(build([.. frames.Where(frame => IsRig(frame, label))])
                .Select(entry => WithRigLabel(entry, label)));
        }

        return all;
    }

    // The three record types the split produces each carry a trailing optional RigLabel, and this is
    // the one place it is stamped on. A `with` expression rather than a second constructor call,
    // so adding a member to either record cannot silently drop it here.
    private static T WithRigLabel<T>(T entry, string label)
        where T : class
        => entry switch
        {
            FilterMedians medians => (T)(object)(medians with { RigLabel = label }),
            FilterDetailRow detail => (T)(object)(detail with { RigLabel = label }),
            FilterAcquisition acquisition => (T)(object)(acquisition with { RigLabel = label }),
            _ => entry,
        };

    internal static bool IsRig(FrameRow frame, string label)
        => string.Equals(frame.Rig, label, StringComparison.Ordinal);

    /// <summary>Spec 12.4's rig groups, in <see cref="RigOrder"/>'s order, each figure over that
    /// rig's own rows. The eccentricity pooling rule is not re-derived per rig: the session's modal
    /// source decides every eccentricity figure on the card (spec 7.2), so a rig's range describes
    /// the same source the night's does.</summary>
    private static IReadOnlyList<RigGroup> BuildRigGroups(
        List<Row> rows,
        IReadOnlyList<string> rigOrder,
        string? modalSource)
    {
        var multiRig = rigOrder.Count > 1;
        List<RigGroup> groups = [];

        for (var index = 0; index < rigOrder.Count; index++)
        {
            var label = rigOrder[index];
            List<Row> own = [.. rows.Where(row => IsRig(row.Frame, label))];
            var reference = RankReference(own.Select(row => row.Frame));

            groups.Add(new RigGroup(
                index,
                label,
                own[0].Telescope,
                own[0].Camera,
                own.Count,
                own.Sum(row => row.Frame.ExposureTime ?? 0d),
                reference.ImageId,
                reference.Paths.Count > 0 ? reference.Paths[0] : null,
                reference.Paths,

                // A single-rig night's ranges table is the session's own and is unchanged, so the
                // one rig carries none: null here is what keeps that table exactly as it was.
                multiRig
                    ?
                    [
                        Range(own.Select(row => row.Frame.MedianHfr)),
                        Range(own.Select(row => PooledEccentricity(row.Frame, modalSource))),
                        Range(own.Select(row => row.Frame.Fwhm)),
                        Range(own.Select(row => row.Frame.GuidingRmsArcsec)),
                        Range(own.Select(row => row.Frame.SensorTemp)),
                    ]
                    : null));
        }

        return groups;
    }

    /// <summary>
    /// Spec 12.4's reference pick (PAR-008), written once and called twice: once over the whole
    /// night and once per rig. The sharpest frame the set has, not the newest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Candidates are the rows whose <c>median_hfr</c> is non-null and non-zero, ascending by that
    /// value, a tie going to the newer capture time. A stored zero is not a measurement and is
    /// excluded, which is the same rule <c>mosaic_composite.select_best_frame</c> applies in the
    /// web application.
    /// </para>
    /// <para>
    /// When no row of the set carries an HFR at all, the set falls back to section 11.4's rule and
    /// takes the newest rows with a capture date, which is the case a night scanned before any
    /// metric was derived lands in. A set with neither yields nothing and the page draws its
    /// placeholder.
    /// </para>
    /// <para>
    /// At most three paths come back, in ranked order, because spec 12.4's decode walk tries at
    /// most three. The walk itself is the view-model's: this ranks, and nothing on the page ranks
    /// frames a second time.
    /// </para>
    /// </remarks>
    internal static (Guid? ImageId, IReadOnlyList<string> Paths) RankReference(IEnumerable<FrameRow> frames)
    {
        var all = frames.ToList();

        List<FrameRow> ranked =
        [
            .. all
                .Where(frame => frame.MedianHfr is { } hfr && hfr != 0d)
                .OrderBy(frame => frame.MedianHfr!.Value)
                .ThenByDescending(frame => frame.CaptureDate ?? DateTime.MinValue)
                .Take(ReferenceCandidateLimit),
        ];

        if (ranked.Count == 0)
        {
            ranked =
            [
                .. all
                    .Where(frame => frame.CaptureDate is not null)
                    .OrderByDescending(frame => frame.CaptureDate!.Value)
                    .Take(ReferenceCandidateLimit),
            ];
        }

        return ranked.Count == 0
            ? (null, [])
            : (ranked[0].ImageId, [.. ranked.Select(frame => frame.FilePath)]);
    }

    /// <summary>Spec 12.4's decode walk tries at most three frames, so ranking more than three
    /// would be work nothing reads.</summary>
    private const int ReferenceCandidateLimit = 3;

    // ---- the round trip ----------------------------------------------------------------
    //
    // ponytail: one row per LIGHT frame of one session is held in memory, which is what the frame
    // table binds anyway. Ceiling: a 2 000 frame all-night session is 2 000 small records. There
    // is nothing to push into SQL here, because the frame rows are the deliverable, not an
    // intermediate. raw_headers is deliberately not selected: it is the largest column in the
    // table and only one frame's worth is ever displayed (Task 6 reads it per frame).
    private static (List<Row> Rows, string? Notes) Read(
        SqliteConnection connection,
        string groupKey,
        DateOnly sessionDate,
        AliasMap map)
    {
        var parameters = new SqlParameters();
        var scope = SqlFragments.GroupScope(groupKey, parameters.Add);
        // Spec 5.1 stores session_date as an invariant yyyy-MM-dd string, and TargetListingQuery
        // binds it the same way. Binding a DateOnly would let the provider choose a form.
        var dateParam = parameters.Add(sessionDate.ToString(SqlReaders.DateFormat, CultureInfo.InvariantCulture));
        var isTarget = Guid.TryParse(groupKey, out var targetId);

        using var command = connection.CreateCommand();

        var text =
            $"""
            SELECT i.id, i.file_path, i.file_name, i.capture_date, i.filter_used, i.exposure_time,
                   i.median_hfr, i.arcsec_per_pixel, i.eccentricity, i.eccentricity_source, i.fwhm,
                   i.detected_stars, i.guiding_rms_arcsec, i.guiding_rms_ra_arcsec, i.guiding_rms_dec_arcsec,
                   i.guiding_rms_source, i.adu_mean, i.adu_median, i.adu_stdev, i.adu_min, i.adu_max,
                   i.focuser_position, i.focuser_temp, i.ambient_temp, i.dew_point, i.humidity, i.pressure,
                   i.wind_speed, i.wind_direction, i.wind_gust, i.cloud_cover, i.sky_quality, i.airmass,
                   i.pier_side, i.rotator_position, i.sensor_temp, i.camera_gain, i.telescope, i.camera
            FROM images i
            WHERE i.{SqlFragments.LightFrameOnly}
              AND i.session_date = {dateParam}
              AND {scope}
            ORDER BY i.capture_date;
            """;

        if (isTarget)
        {
            // Statement 2 exists only for a resolved key: an obj: group has no target id to key a
            // session note on, so its Notes is always null.
            text +=
                $"""

                SELECT notes FROM session_notes
                WHERE target_id = {parameters.Add(targetId)} AND session_date = {dateParam};
                """;
        }

        command.CommandText = text;
        parameters.ApplyTo(command);

        var rows = new List<Row>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // A 39 column reader is where an ordinal slips, so every ordinal is written out in
            // order and Get_FrameRows_CarryEveryFrameTableColumn asserts all of them field by
            // field against one fully populated frame.
            var telescope = map.CanonicalTelescope(SqlReaders.ReadText(reader, 37));
            var camera = map.CanonicalCamera(SqlReaders.ReadText(reader, 38));
            var arcsecPerPixel = SqlReaders.ReadNullableDouble(reader, 7);
            var eccentricitySource = SqlReaders.ReadText(reader, 9);
            rows.Add(new Row(
                new FrameRow(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    SqlReaders.ReadNullableDateTime(reader, 3),
                    map.CanonicalFilter(SqlReaders.ReadText(reader, 4)),
                    SqlReaders.ReadNullableDouble(reader, 5),
                    SqlReaders.ReadNullableDouble(reader, 6),
                    SqlReaders.ReadNullableDouble(reader, 8),
                    SqlReaders.ReadNullableDouble(reader, 10),
                    SqlReaders.ReadNullableInt(reader, 11),
                    SqlReaders.ReadNullableDouble(reader, 12),
                    SqlReaders.ReadNullableDouble(reader, 13),
                    SqlReaders.ReadNullableDouble(reader, 14),
                    SqlReaders.ReadText(reader, 15),
                    SqlReaders.ReadNullableDouble(reader, 16),
                    SqlReaders.ReadNullableDouble(reader, 17),
                    SqlReaders.ReadNullableDouble(reader, 18),
                    SqlReaders.ReadNullableInt(reader, 19),
                    SqlReaders.ReadNullableInt(reader, 20),
                    SqlReaders.ReadNullableInt(reader, 21),
                    SqlReaders.ReadNullableDouble(reader, 22),
                    SqlReaders.ReadNullableDouble(reader, 23),
                    SqlReaders.ReadNullableDouble(reader, 24),
                    SqlReaders.ReadNullableDouble(reader, 25),
                    SqlReaders.ReadNullableDouble(reader, 26),
                    SqlReaders.ReadNullableDouble(reader, 27),
                    SqlReaders.ReadNullableDouble(reader, 28),
                    SqlReaders.ReadNullableDouble(reader, 29),
                    SqlReaders.ReadNullableDouble(reader, 30),
                    SqlReaders.ReadNullableDouble(reader, 31),
                    SqlReaders.ReadNullableDouble(reader, 32),
                    SqlReaders.ReadText(reader, 33),
                    SqlReaders.ReadNullableDouble(reader, 34),
                    SqlReaders.ReadNullableDouble(reader, 35),
                    SqlReaders.ReadNullableInt(reader, 36),
                    // Rule 8: one spelling of the rig label, so the insight prefixes and the
                    // chart's rig split cannot disagree. Matches build_rig_details.
                    $"{telescope ?? "Unknown"} / {camera ?? "Unknown"}",
                    // P12 R3: both flags are false here and set by Get, which is the only place
                    // that holds the session median and the session baselines they need.
                    false,
                    false,
                    // P25 R3: the merge pools eccentricity and recomputes arcsecond HFR over the
                    // union, so the two inputs ride on the frame row as well as on Row.
                    EccentricitySource: eccentricitySource,
                    ArcsecPerPixel: arcsecPerPixel),
                arcsecPerPixel,
                eccentricitySource,
                telescope,
                camera));
        }

        string? notes = null;
        if (isTarget && reader.NextResult() && reader.Read())
        {
            notes = SqlReaders.ReadText(reader, 0);
        }

        return (rows, notes);
    }

    // ---- ranges and medians ------------------------------------------------------------

    /// <summary>Rule 1. One pass for the minimum, the maximum and the median, all three null when
    /// no frame carries the metric. Null is "not measured", never zero.</summary>
    internal static MetricRangeSummary Range(IEnumerable<double?> values)
    {
        var present = new List<double?>();
        double? min = null;
        double? max = null;
        foreach (var value in values)
        {
            present.Add(value);
            if (value is not { } number)
            {
                continue;
            }

            if (min is null || number < min)
            {
                min = number;
            }

            if (max is null || number > max)
            {
                max = number;
            }
        }

        return new MetricRangeSummary(min, max, Statistics.Median(present));
    }

    /// <summary>Rule 2, identical to <c>TargetDetailQuery</c>'s rule and deliberately so: a frame
    /// with an HFR and a strictly positive plate scale contributes the converted value, one with
    /// an HFR and no usable plate scale is counted as excluded, and one with no HFR is in neither
    /// pool. The session figure is a median of the converted values, not a mean.</summary>
    internal static (List<double?> Values, int ExcludedCount) HfrArcsec(IReadOnlyList<FrameRow> frames)
    {
        var values = new List<double?>();
        var excluded = 0;
        foreach (var frame in frames)
        {
            if (frame.MedianHfr is not { } hfr)
            {
                continue;
            }

            if (frame.ArcsecPerPixel is { } scale && scale > 0)
            {
                values.Add(hfr * scale);
            }
            else
            {
                excluded++;
            }
        }

        return (values, excluded);
    }

    internal static double? PooledEccentricity(FrameRow frame, string? modalSource)
        => EccentricitySources.IsPooled(frame.EccentricitySource, frame.Eccentricity, modalSource)
            ? frame.Eccentricity
            : null;

    // ---- per-filter blocks -------------------------------------------------------------

    /// <summary>Rule 3, first half: one entry per canonical filter name, nulls dropped, ordered by
    /// name. A frame with no FILTER card used no filter and gets no row, matching the totals row's
    /// "no Unknown bucket" rule.</summary>
    internal static IReadOnlyList<FilterMedians> BuildFilterMedians(IReadOnlyList<FrameRow> frames, string? modalSource)
        => [.. frames
            .Where(frame => frame.FilterUsed is not null)
            .GroupBy(frame => frame.FilterUsed!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new FilterMedians(
                group.Key,
                Statistics.Median(group.Select(frame => frame.MedianHfr)),
                Statistics.Median(group.Select(frame => PooledEccentricity(frame, modalSource))),
                Statistics.Median(group.Select(frame => frame.Fwhm)),
                Statistics.Median(group.Select(frame => frame.GuidingRmsArcsec)),
                Statistics.Median(group.Select(frame => (double?)frame.DetectedStars))))];

    /// <summary>Rule 3's grouping, declared once: one group per (canonical filter, exposure time),
    /// ordered by filter name then exposure ascending, so one filter shot at two exposures yields
    /// two groups and the Exposure column means something. A null exposure sorts as 0, matching
    /// <c>build_rig_details</c>. Both per-filter row builders below project from this one sequence,
    /// which is what makes row <c>n</c> of <see cref="SessionDetail.FilterAcquisitions"/> row
    /// <c>n</c> of <see cref="SessionDetail.FilterDetails"/> by construction rather than by two
    /// rules that happen to agree.</summary>
    private static IEnumerable<IGrouping<(string Filter, double? ExposureTime), FrameRow>> FilterGroups(
        IReadOnlyList<FrameRow> frames)
        => frames
            .Where(frame => frame.FilterUsed is not null)
            .GroupBy(frame => (Filter: frame.FilterUsed!, frame.ExposureTime))
            .OrderBy(group => group.Key.Filter, StringComparer.Ordinal)
            .ThenBy(group => group.Key.ExposureTime ?? 0d);

    /// <summary>Rule 3, second half: one row per <see cref="FilterGroups"/> group.</summary>
    internal static IReadOnlyList<FilterDetailRow> BuildFilterDetails(IReadOnlyList<FrameRow> frames, string? modalSource)
        => [.. FilterGroups(frames)
            .Select(group => new FilterDetailRow(
                group.Key.Filter,
                group.Count(),
                group.Sum(frame => frame.ExposureTime ?? 0d),
                Statistics.Median(group.Select(frame => frame.MedianHfr)),
                Statistics.Median(group.Select(frame => PooledEccentricity(frame, modalSource))),
                group.Key.ExposureTime))];

    /// <summary>Spec 12.16's per-filter acquisition figures, over the same groups in the same
    /// order as <see cref="BuildFilterDetails"/>.</summary>
    internal static IReadOnlyList<FilterAcquisition> BuildFilterAcquisitions(IReadOnlyList<FrameRow> frames)
        => [.. FilterGroups(frames)
            .Select(group => new FilterAcquisition(
                group.Key.Filter,
                group.Count(),
                group.Key.ExposureTime,
                ModalGain(group.Select(frame => frame.CameraGain)),
                Statistics.Median(group.Select(frame => frame.SensorTemp)),
                Statistics.Median(group.Select(frame => frame.SkyQuality)),
                Statistics.Median(group.Select(frame => frame.Fwhm)),
                Statistics.Median(group.Select(frame => frame.AmbientTemp))))];

    /// <summary>Spec 12.16's modal gain: the value occurring most often, a tie broken by the
    /// smaller value, null when no frame carries one. The tie break is what makes the figure
    /// well defined, and it is why this is not a group-and-take-the-first.</summary>
    internal static int? ModalGain(IEnumerable<int?> gains)
    {
        int? modal = null;
        var best = 0;
        foreach (var group in gains.OfType<int>().GroupBy(gain => gain))
        {
            var count = group.Count();
            if (count > best || (count == best && group.Key < modal))
            {
                modal = group.Key;
                best = count;
            }
        }

        return modal;
    }

    // ---- session insights --------------------------------------------------------------
    //
    // Rule 6: exactly three kinds. Spec 12.4 scopes the insights to "HFR outliers and
    // eccentricity outliers relative to the session baseline and the rig baseline"; the web
    // application's session-duration, sensor-temperature-stability and best/poor-HFR verdicts are
    // out of scope (questions.md Q6). Order is fixed so the tests can assert it: the three
    // session-wide kinds in the order above, then the first two repeated per rig, in the night's
    // rig order (first capture time, P14A Q10), when the night holds more than one rig.

    private IReadOnlyList<SessionInsight> BuildInsights(
        List<Row> rows,
        List<GradedFrame> graded,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, MetricBaseline>> sessionBaselines,
        string? modalSource,
        bool[] hfrFlags,
        IReadOnlyList<string> rigOrder)
    {
        var insights = new List<SessionInsight>();

        // P12 review P2-1: the session-wide sentence counts the very array the frame rows carry,
        // so its number and the flagged rows are the same frames and cannot drift.
        var hfr = rows.Select(row => row.Frame.MedianHfr).OfType<double>().ToList();
        AddIfPresent(insights, HfrOutliers(HfrOutlierThreshold(hfr), hfrFlags.Count(flag => flag), ""));
        AddIfPresent(insights, EccentricityOutliers(graded, sessionBaselines, ""));
        insights.AddRange(EccentricityVsRig(sessionBaselines, modalSource));

        if (rigOrder.Count <= 1)
        {
            return insights;
        }

        // P14A questions.md Q10: the rig order is the one built in Get, by first capture time, and
        // no longer this method's own alphabetical grouping. Two orders in one query would let the
        // third per-rig sentence name a different rig from the third thumbnail box.
        var entries = rows
            .Select((row, index) => (row.Frame.Rig, Frame: row.Frame, Graded: graded[index]))
            .ToList();

        foreach (var label in rigOrder)
        {
            var rig = entries.Where(entry => string.Equals(entry.Rig, label, StringComparison.Ordinal)).ToList();
            var prefix = $"[{label}] ";
            // The rig's own scope and therefore its own threshold and its own flags, through the
            // same two helpers. Ruling Q8 keeps this verdict off the frame rows.
            var rigHfr = rig.ConvertAll(entry => entry.Frame.MedianHfr);
            AddIfPresent(insights, HfrOutliers(
                HfrOutlierThreshold(rigHfr.OfType<double>().ToList()),
                FlagHfrOutliers(rigHfr).Count(flag => flag),
                prefix));
            // The rig's own frames against the same session baselines, not a rebuilt per-rig
            // baseline: the baselines are already per (telescope, camera, filter), so a rig's
            // frames only ever meet their own groups in them.
            AddIfPresent(insights, EccentricityOutliers(
                rig.Select(entry => entry.Graded),
                sessionBaselines,
                prefix));
        }

        return insights;
    }

    private static void AddIfPresent(List<SessionInsight> insights, SessionInsight? insight)
    {
        if (insight is not null)
        {
            insights.Add(insight);
        }
    }

    /// <summary>Port of <c>target_helpers.hfr_outlier_insight</c>, given the threshold from
    /// <see cref="HfrOutlierThreshold"/> and the count of frames
    /// <see cref="FlagHfrOutliers(IReadOnlyList{double?})"/> flagged against it. Silent when there
    /// is no threshold (no median, or fewer than three frames carry an HFR) and when nothing
    /// exceeded it. HFR is unbounded, so <c>median * 1.5</c> is always exceedable and the rule has
    /// no reachability flaw; its scale invariance is a calibration opinion the web application made
    /// deliberately and is left alone.</summary>
    private static SessionInsight? HfrOutliers(double? thresholdOrNull, int count, string prefix)
    {
        if (thresholdOrNull is not { } threshold || count == 0)
        {
            return null;
        }

        return new SessionInsight(
            InsightLevel.Warning,
            "hfr_outliers",
            string.Format(
                CultureInfo.InvariantCulture,
                "{0}{1} frame{2} with HFR outlier{2} (> {3:0.0})",
                prefix,
                count,
                Plural(count),
                threshold));
    }

    /// <summary>Port of <c>target_helpers.ecc_outlier_insight</c>. Silent when nothing could be
    /// judged (no usable baseline: sparse group, uniform group, or an eccentricity outside the
    /// pooled source) as well as when nothing was bad, because the two are different answers and
    /// only one of them is worth a sentence.</summary>
    private static SessionInsight? EccentricityOutliers(
        IEnumerable<GradedFrame> frames,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, MetricBaseline>> baselines,
        string prefix)
    {
        var (count, graded) = FrameQuality.CountOutliers(frames, baselines, FrameQuality.EccentricityMetric);
        if (graded == 0 || count == 0)
        {
            return null;
        }

        return new SessionInsight(
            InsightLevel.Warning,
            "eccentricity_outliers",
            string.Format(
                CultureInfo.InvariantCulture,
                // ASCII >=, not the Unicode form the web application emits: the house style
                // forbids decorative characters and a console-mode log line must stay readable.
                "{0}{1} frame{2} with eccentricity outlier{2} (>= {3:0} MAD above the group median)",
                prefix,
                count,
                Plural(count),
                FrameQuality.ZReject));
    }

    /// <summary>Port of <c>target_helpers.session_ecc_vs_rig_insights</c>. The per-frame z-score
    /// cannot see a night where every frame is elongated, because the session median moves with
    /// the frames it measures; comparing that median against the library baseline is the only way
    /// to notice systematic failure (collimation, tilt, a sagging focuser, poor polar
    /// alignment).</summary>
    private IEnumerable<SessionInsight> EccentricityVsRig(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, MetricBaseline>> sessionBaselines,
        string? modalSource)
    {
        var rig = rigBaselines.Current;

        // Spec 7.2: the session pooled one source and the library baseline pooled its own modal
        // source. When those differ the z-score would compare two incomparable scales, so the
        // whole-night check stays silent rather than emitting a wrong number.
        if (!string.Equals(modalSource, rig.EccentricitySource, StringComparison.Ordinal))
        {
            yield break;
        }

        foreach (var key in sessionBaselines.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            var session = sessionBaselines[key].GetValueOrDefault(FrameQuality.EccentricityMetric);
            if (session?.Median is not { } sessionMedian
                || session.N < FrameQuality.MinSessionMedianFrames)
            {
                continue;
            }

            var baseline = rig.Groups.TryGetValue(key, out var group)
                ? group.GetValueOrDefault(FrameQuality.EccentricityMetric)
                : null;
            if (FrameQuality.MadZ(sessionMedian, baseline) is not { } z || z < FrameQuality.ZReject)
            {
                continue;
            }

            yield return new SessionInsight(
                InsightLevel.Warning,
                "eccentricity_vs_rig",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[{0}] Whole session is elongated: median eccentricity {1:0.00} vs {2:0.00} "
                        + "typical for this rig ({3:0.0} MAD above normal)",
                    FrameQuality.GroupLabel(key),
                    sessionMedian,
                    baseline!.Median,
                    z));
        }
    }

    // ---- the per-frame half of the same two rules (P12 R3) -----------------------------
    //
    // Placed next to HfrOutliers and EccentricityOutliers deliberately: the pair that must agree
    // sits together, so an edit to a threshold or a guard has the other half in the same screen.

    /// <summary>The one HFR outlier threshold rule: the median of the HFR-carrying frames times
    /// 1.5, or null when there is no median or when two or fewer frames carry an HFR, which is
    /// the case the <c>hfr_outliers</c> insight stays silent on. The single place the multiplier
    /// and both guards live, so the insight and the per-frame flags cannot drift apart
    /// (P12 review P2-1).</summary>
    private static double? HfrOutlierThreshold(List<double> values)
        => Statistics.Median(values.Select(value => (double?)value)) is { } median && values.Count > 2
            ? median * 1.5
            : null;

    /// <summary>The per-frame half of the <c>hfr_outliers</c> insight (P12 R3): one flag per
    /// frame, in the order given, for the frames whose HFR exceeds
    /// <see cref="HfrOutlierThreshold"/> over that same list. The only strict comparison against
    /// the threshold in this file, and the array the session-wide insight counts, so the flagged
    /// rows and the insight's number are the same frames by construction rather than by two
    /// methods agreeing. Called once per scope: once over the session and once per rig on a
    /// multi-rig night, each with its own values and therefore its own threshold.</summary>
    private static bool[] FlagHfrOutliers(IReadOnlyList<double?> values)
    {
        var flags = new bool[values.Count];
        if (HfrOutlierThreshold(values.OfType<double>().ToList()) is not { } threshold)
        {
            return flags;
        }

        for (var index = 0; index < values.Count; index++)
        {
            flags[index] = values[index] is { } value && value > threshold;
        }

        return flags;
    }

    /// <summary>The per-frame half of the <c>eccentricity_outliers</c> insight (P12 R3), and
    /// since P24 R22 the same rule over any graded metric. Reuses
    /// <see cref="FrameQuality.MadZ"/> and <see cref="FrameQuality.GroupKey"/> directly, which is
    /// the same code path <see cref="FrameQuality.CountOutliers"/> walks, so a frame this marks is
    /// a frame that method counted. A frame with no usable baseline is not an outlier and is not
    /// graded: the two answers are different and only one of them gets ink.</summary>
    private static bool[] FlagMadOutliers(
        List<GradedFrame> graded,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, MetricBaseline>> baselines,
        string metric,
        bool higherIsBetter = false)
    {
        var flags = new bool[graded.Count];
        for (var index = 0; index < graded.Count; index++)
        {
            var frame = graded[index];
            var baseline = baselines.TryGetValue(FrameQuality.GroupKey(frame), out var group)
                ? group.GetValueOrDefault(metric)
                : null;
            flags[index] = FrameQuality.MadZ(FrameQuality.Value(frame, metric), baseline, higherIsBetter) is { } z
                && z >= FrameQuality.ZReject;
        }

        return flags;
    }

    /// <summary>Spec 12.4's per-frame deviations, both baselines at once, so the "Compare to"
    /// toggle re-reads a value this query already returned rather than issuing a second one. Reuses
    /// <see cref="FrameQuality.MadZ"/> and <see cref="FrameQuality.GroupKey"/>, the same code path
    /// the flags and the insights walk, so a sentence and a band cannot contradict each other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Detected stars is the one metric graded <c>higherIsBetter</c>: a bigger number is a better
    /// frame there, and <see cref="FrameQuality.MadZ"/> flips the sign so a positive deviation
    /// always means worse.
    /// </para>
    /// <para>
    /// A missing rig group or a missing metric inside one yields an empty grade, never a throw:
    /// the library can hold a group whose rig baseline is sparse.
    /// </para>
    /// <para>
    /// Spec 7.2: the rig eccentricity baseline pooled the library's modal source, so a session
    /// pooled on a different source has no comparable rig eccentricity grade at all. That is the
    /// same refusal <see cref="EccentricityVsRig"/> already makes for the whole-night sentence; a
    /// cross-source deviation is a wrong number rather than an approximate one.
    /// </para>
    /// </remarks>
    private FrameGrading[] BuildGrading(
        List<GradedFrame> graded,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, MetricBaseline>> sessionBaselines,
        string? modalSource)
    {
        var rig = rigBaselines.Current;
        var rigComparable = string.Equals(modalSource, rig.EccentricitySource, StringComparison.Ordinal);
        var grading = new FrameGrading[graded.Count];

        for (var index = 0; index < graded.Count; index++)
        {
            var frame = graded[index];
            var key = FrameQuality.GroupKey(frame);
            var session = sessionBaselines.GetValueOrDefault(key);
            var rigGroup = rig.Groups.GetValueOrDefault(key);

            grading[index] = new FrameGrading(
                Grade(frame.MedianHfr, session, "median_hfr"),
                Grade(frame.MedianHfr, rigGroup, "median_hfr"),
                Grade(frame.Eccentricity, session, FrameQuality.EccentricityMetric),
                rigComparable
                    ? Grade(frame.Eccentricity, rigGroup, FrameQuality.EccentricityMetric)
                    : new MetricGrade(null, null),
                Grade(frame.Fwhm, session, "fwhm"),
                Grade(frame.Fwhm, rigGroup, "fwhm"),
                Grade(frame.DetectedStars, session, "detected_stars", higherIsBetter: true),
                Grade(frame.AduMedian, session, "adu_median"),
                Grade(frame.GuidingRmsArcsec, session, "guiding_rms_arcsec"));
        }

        return grading;

        static MetricGrade Grade(
            double? value,
            IReadOnlyDictionary<string, MetricBaseline>? group,
            string metric,
            bool higherIsBetter = false)
        {
            var baseline = group?.GetValueOrDefault(metric);
            return new MetricGrade(
                FrameQuality.MadZ(value, baseline, higherIsBetter),
                baseline?.Median);
        }
    }

    private static string Plural(int count) => count > 1 ? "s" : "";

    /// <summary>One read row: the public frame row plus the canonical rig halves, which only the
    /// rig groups and the grading passes need. The plate scale and the eccentricity source ride on
    /// both since P25, because the merge needs them without a Row.</summary>
    private sealed record Row(
        FrameRow Frame,
        double? ArcsecPerPixel,
        string? EccentricitySource,
        string? Telescope,
        string? Camera);
}
