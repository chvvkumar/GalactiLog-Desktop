using System.Globalization;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Io;
using GalactiLog.Core.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>
/// The whole Statistics page in one read (spec 12.5's Query paragraph), port of the web
/// application's single <c>/api/stats</c> endpoint in
/// <c>backend/app/api/stats.py</c>. <see cref="Calendar"/> is separate because its range is
/// user-controlled and the rest of the response is not.
/// </summary>
/// <remarks>
/// <para>
/// Read-only, and every operand the caller can influence is bound. Unlike the dashboard and
/// detail queries it takes no group key: the response is library-wide, so there is no group to
/// scope it to. That is the second documented exception in this namespace after
/// <see cref="ReferenceThumbnailSourcesQuery"/>, and it is stated here so a reviewer does not read
/// it as an omission.
/// </para>
/// <para>
/// SQLite has no <c>percentile_cont</c>, no <c>array_agg</c> and no dictionary-driven <c>CASE</c>,
/// so the three server-side computations the web source leans on move into C#: every median and
/// MAD goes through <see cref="Statistics"/>, alias folding happens after the read through
/// <see cref="AliasMapCache"/> (building a <c>CASE</c> from an alias map would interpolate user
/// text into a statement, which the query rules forbid outright), and <c>array_agg(distinct col)</c>
/// becomes a set of raw names per canonical name. The dividing line is therefore simple: every
/// aggregate that needs alias folding or a median comes from the one streaming pass in
/// <c>RunPass</c> (equipment inventory, equipment performance, filter usage, rigs per night and
/// data quality), and every aggregate SQLite can compute on its own stays in SQL (overview, top
/// targets, the timelines, ingest history and the storage figures).
/// </para>
/// <para>
/// ponytail: the streaming pass reads twelve columns of every LIGHT row and holds each group's
/// value lists in memory so they can be medianed. Ceiling: a 200 000 frame library is a few tens
/// of megabytes of doubles for the duration of one <see cref="Get"/>, which runs once per
/// invalidation, off the UI thread, behind <see cref="StatsCache"/>. That is the price of having
/// no <c>percentile_cont</c>. Upgrade path if it ever matters: a per-group streaming median
/// (t-digest, or a two-pass count-and-select), never a second query. questions.md Q10.
/// </para>
/// </remarks>
public sealed class StatsQuery(
    DatabaseConnectionString connectionString,
    AliasMapCache aliases,
    GuidingStatsQuery guiding)
{
    /// <summary>The web's own limit. The Statistics page shows the first ten
    /// (<c>TopTargets.tsx</c> slices the list), so the extra ten are what a future "show more"
    /// binds to with no query change.</summary>
    public const int TopTargetLimit = 20;

    /// <summary>The web's own limit on the ingest history chart.</summary>
    public const int IngestHistoryDays = 30;

    /// <summary>The terminal <c>scan_runs.state</c> the ingest history counts (spec 5.13). A
    /// cancelled or failed run added files it cannot account for, so it is not history.</summary>
    private const string CompletedScanState = "complete";

    /// <summary>
    /// The calendar statement, exposed so the operand-binding test can read it rather than assert
    /// by review. Both bounds are parameters; nothing is interpolated but
    /// <see cref="SqlFragments.LightFrameOnly"/>, which is a constant of this assembly.
    /// </summary>
    public const string CalendarSql =
        $"""
        SELECT i.session_date, coalesce(sum(i.exposure_time), 0),
               count(DISTINCT i.resolved_target_id), count(i.id)
        FROM images i
        WHERE i.{SqlFragments.LightFrameOnly}
          AND i.session_date IS NOT NULL
          AND i.session_date >= @p0
          AND i.session_date <= @p1
        GROUP BY i.session_date
        ORDER BY i.session_date;
        """;

    // The pixel-domain histogram's boundaries, transcribed from _query_hfr_buckets. The last
    // bucket's real bound is 100 and its label is "5.0+", which is the web source's own
    // inconsistency and is kept: a frame with an HFR of 150 pixels is in no bucket at all.
    private const double PixelOverflowHigh = 100d;

    private static readonly (double Low, double? High)[] PixelBuckets =
    [
        (0d, 1.0), (1.0, 1.5), (1.5, 2.0), (2.0, 2.5),
        (2.5, 3.0), (3.0, 4.0), (4.0, 5.0), (5.0, PixelOverflowHigh),
    ];

    // _HFR_ARCSEC_BUCKET_RANGES: sixteen half-arcsecond buckets from 0.0 to 8.0, then an
    // unbounded overflow whose high bound is None.
    private static readonly (double Low, double? High)[] ArcsecBuckets = BuildArcsecBuckets();

    /// <param name="thumbnailCacheBytes">The thumbnail cache's size on disk (questions.md Q12).
    /// <c>GalactiLog.Data</c> cannot reference <c>GalactiLog.App</c>, where <c>ThumbnailCache</c>
    /// and the <c>AppWriter</c> instance live, so the figure arrives as a delegate that
    /// <c>AppHost</c> binds to a sum over <c>AppWriter.EnumerateThumbnailFiles</c>. Null reports
    /// 0, which is what every caller that does not care about storage gets.</param>
    public StatsResponse Get(Func<long>? thumbnailCacheBytes = null)
    {
        var map = aliases.Current;

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        // One deferred read transaction across both commands, so the equipment sections and the
        // overview describe one state of the library. Without it a scan committing between the two
        // yields a response whose frame counts disagree with each other, which is the kind of
        // inconsistency a reader blames on the arithmetic. Deferred, so this takes a read lock at
        // the first statement and never a write lock. SqliteConnection.CreateCommand attaches the
        // active transaction itself, so the two readers below need no change.
        using var snapshot = connection.BeginTransaction(deferred: true);

        var pass = RunPass(connection, map);
        var aggregates = ReadAggregates(connection);

        snapshot.Commit();

        return new StatsResponse(
            aggregates.Overview,
            Inventory(pass.Cameras),
            Inventory(pass.Telescopes),
            Performance(pass.Combos),
            [.. pass.FilterUsage
                .Select(entry => new FilterUsageEntry(entry.Key, entry.Value))
                .OrderByDescending(entry => entry.IntegrationSeconds)
                .ThenBy(entry => entry.FilterName, StringComparer.Ordinal)],
            aggregates.TopTargets,
            aggregates.TimelineMonthly,
            Weekly(aggregates.PerNight),
            [.. aggregates.PerNight.Select(night => new TimelineEntry(
                night.Date.ToString(SqlReaders.DateFormat, CultureInfo.InvariantCulture),
                night.IntegrationSeconds))],
            pass.RigsPerNight.ToDictionary(entry => entry.Key, entry => entry.Value.Count),
            Quality(pass.Quality),
            new StorageStats(
                aggregates.FitsBytesCatalogued,
                thumbnailCacheBytes?.Invoke() ?? 0,
                aggregates.DatabasePageBytes + WalBytes(connection)),
            aggregates.IngestHistory,
            // Spec 12.5's Guiding section, inside the one response so it shares this response's
            // cache and its invalidation. Its own short-lived context, outside the snapshot
            // transaction above: it reads phd2_sessions and nothing the two sections share, so
            // there is no figure the two could disagree about.
            guiding.Get());
    }

    /// <summary>One row per imaging night in the inclusive range, grouped on <c>session_date</c>
    /// alone: a night imaged with two rigs is one calendar cell, deliberately unlike
    /// <see cref="StatsOverview.RigSessionCount"/>. The web's <c>year</c> parameter and its
    /// no-argument rolling window (which filters on <c>capture_date</c> while grouping on
    /// <c>session_date</c>) collapse into this one bounded range, as spec 12.5 names it.</summary>
    public IReadOnlyList<CalendarEntry> Calendar(DateOnly from, DateOnly to)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        var parameters = new SqlParameters();
        // Spec 5.1 stores session_date as an invariant yyyy-MM-dd string and every other query
        // binds it the same way, so the comparison is a text comparison on a sortable format.
        parameters.Add(from.ToString(SqlReaders.DateFormat, CultureInfo.InvariantCulture));
        parameters.Add(to.ToString(SqlReaders.DateFormat, CultureInfo.InvariantCulture));

        using var command = connection.CreateCommand();
        command.CommandText = CalendarSql;
        parameters.ApplyTo(command);

        var entries = new List<CalendarEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (SqlReaders.ReadDate(reader, 0) is not { } date)
            {
                continue;
            }

            entries.Add(new CalendarEntry(
                date,
                SqlReaders.ReadDouble(reader, 1),
                SqlReaders.ReadInt(reader, 2),
                SqlReaders.ReadInt(reader, 3)));
        }

        return entries;
    }

    // ---- the streaming pass ------------------------------------------------------------

    private static PassResult RunPass(SqliteConnection connection, AliasMap map)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT i.telescope, i.camera, i.filter_used, i.session_date, i.exposure_time,
                   i.resolved_target_id, i.median_hfr, i.eccentricity, i.eccentricity_source,
                   i.fwhm, i.guiding_rms_arcsec, i.arcsec_per_pixel
            FROM images i
            WHERE i.{SqlFragments.LightFrameOnly};
            """;

        var result = new PassResult();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var rawTelescope = SqlReaders.ReadText(reader, 0);
            var rawCamera = SqlReaders.ReadText(reader, 1);
            var rawFilter = SqlReaders.ReadText(reader, 2);
            var sessionDate = SqlReaders.ReadDate(reader, 3);
            var exposure = SqlReaders.ReadNullableDouble(reader, 4) ?? 0d;
            var targetId = reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5);
            var hfr = SqlReaders.ReadNullableDouble(reader, 6);
            var eccentricity = SqlReaders.ReadNullableDouble(reader, 7);
            var eccentricitySource = SqlReaders.ReadText(reader, 8);
            var fwhm = SqlReaders.ReadNullableDouble(reader, 9);
            var guiding = SqlReaders.ReadNullableDouble(reader, 10);
            var plateScale = SqlReaders.ReadNullableDouble(reader, 11);

            var telescope = map.CanonicalTelescope(rawTelescope);
            var camera = map.CanonicalCamera(rawCamera);
            var filter = map.CanonicalFilter(rawFilter);

            // Equipment inventory. The web's WHERE is `col IS NOT NULL`; a blank string folds to
            // a null canonical name in AliasMap, so a frame recorded with an empty CAMERA card is
            // treated as having none rather than grouping under an empty name.
            if (camera is not null)
            {
                Inventory(result.Cameras, camera, rawCamera!)
                    .Add(sessionDate, targetId, exposure, fwhm, guiding);
            }

            if (telescope is not null)
            {
                Inventory(result.Telescopes, telescope, rawTelescope!)
                    .Add(sessionDate, targetId, exposure, fwhm, guiding);
            }

            // Filter usage: `filter_used IS NOT NULL`, re-keyed by canonical name afterwards.
            if (filter is not null)
            {
                result.FilterUsage[filter] = result.FilterUsage.GetValueOrDefault(filter) + exposure;
            }

            // Rigs per night. The web concatenates the two normalized names with Postgres'
            // concat(), which renders a NULL as an empty string, so a frame with no telescope
            // still counts as a rig ("|CAM") rather than being skipped. Reproduced literally.
            if (sessionDate is { } night)
            {
                if (!result.RigsPerNight.TryGetValue(night, out var rigs))
                {
                    rigs = new HashSet<string>(StringComparer.Ordinal);
                    result.RigsPerNight[night] = rigs;
                }

                rigs.Add($"{telescope ?? ""}|{camera ?? ""}");
            }

            // Equipment performance, over frames carrying both names (`telescope IS NOT NULL AND
            // camera IS NOT NULL`).
            if (telescope is not null && camera is not null)
            {
                var combo = Combo(result.Combos, telescope, camera);
                combo.RawTelescopes.Add(rawTelescope!);
                combo.RawCameras.Add(rawCamera!);
                combo.Add(sessionDate, exposure, hfr, eccentricity, fwhm, guiding);

                // A frame whose canonical filter is null contributes to the combo totals and to no
                // filter entry, matching the web's `continue`.
                if (filter is not null)
                {
                    combo.Filter(filter).Add(exposure, hfr, eccentricity, fwhm);
                }
            }

            result.Quality.Add(hfr, eccentricity, eccentricitySource, plateScale);
        }

        return result;
    }

    private static InventoryAccumulator Inventory(
        Dictionary<string, InventoryAccumulator> accumulators, string canonical, string rawName)
    {
        if (!accumulators.TryGetValue(canonical, out var accumulator))
        {
            accumulator = new InventoryAccumulator();
            accumulators[canonical] = accumulator;
        }

        accumulator.RawNames.Add(rawName);
        return accumulator;
    }

    private static ComboAccumulator Combo(
        Dictionary<(string Telescope, string Camera), ComboAccumulator> accumulators,
        string telescope,
        string camera)
    {
        var key = (telescope, camera);
        if (!accumulators.TryGetValue(key, out var accumulator))
        {
            accumulator = new ComboAccumulator();
            accumulators[key] = accumulator;
        }

        return accumulator;
    }

    // ---- the aggregates SQLite can compute on its own ----------------------------------

    private static Aggregates ReadAggregates(SqliteConnection connection)
    {
        var parameters = new SqlParameters();
        var topLimit = parameters.Add(TopTargetLimit);
        var scanState = parameters.Add(CompletedScanState);
        var ingestLimit = parameters.Add(IngestHistoryDays);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT coalesce(sum(CASE WHEN i.capture_date IS NOT NULL THEN i.exposure_time END), 0),
                   count(DISTINCT CASE WHEN i.capture_date IS NOT NULL THEN i.resolved_target_id END),
                   count(i.id),
                   count(DISTINCT CASE WHEN i.capture_date IS NOT NULL
                         THEN i.session_date || '|' || coalesce(i.telescope, '') || '|' || coalesce(i.camera, '') END),
                   min(CASE WHEN i.capture_date IS NOT NULL THEN i.session_date END),
                   max(CASE WHEN i.capture_date IS NOT NULL THEN i.session_date END)
            FROM images i
            LEFT JOIN targets t ON t.id = i.resolved_target_id
            WHERE i.{SqlFragments.LightFrameOnly}
              AND (i.resolved_target_id IS NULL OR t.merged_into_id IS NULL);

            SELECT t.primary_name, coalesce(sum(i.exposure_time), 0) AS integration
            FROM images i
            JOIN targets t ON t.id = i.resolved_target_id
            WHERE i.{SqlFragments.LightFrameOnly}
            GROUP BY t.primary_name
            ORDER BY integration DESC, t.primary_name
            LIMIT {topLimit};

            SELECT strftime('%Y-%m', i.session_date) AS period, coalesce(sum(i.exposure_time), 0)
            FROM images i
            WHERE i.{SqlFragments.LightFrameOnly} AND i.session_date IS NOT NULL
            GROUP BY period
            ORDER BY period;

            SELECT i.session_date, coalesce(sum(i.exposure_time), 0)
            FROM images i
            WHERE i.{SqlFragments.LightFrameOnly} AND i.session_date IS NOT NULL
            GROUP BY i.session_date
            ORDER BY i.session_date;

            SELECT date(r.started_at) AS day, coalesce(sum(r.new_files), 0)
            FROM scan_runs r
            WHERE r.state = {scanState}
            GROUP BY day
            ORDER BY day DESC
            LIMIT {ingestLimit};

            SELECT (SELECT coalesce(sum(i.file_size), 0) FROM images i),
                   (SELECT page_count FROM pragma_page_count()) * (SELECT page_size FROM pragma_page_size());
            """;
        parameters.ApplyTo(command);

        using var reader = command.ExecuteReader();

        reader.Read();
        var overview = new StatsOverview(
            SqlReaders.ReadDouble(reader, 0),
            SqlReaders.ReadInt(reader, 1),
            SqlReaders.ReadInt(reader, 2),
            SqlReaders.ReadInt(reader, 3),
            SqlReaders.ReadDate(reader, 4),
            SqlReaders.ReadDate(reader, 5));

        var topTargets = new List<TopTargetEntry>();
        reader.NextResult();
        while (reader.Read())
        {
            topTargets.Add(new TopTargetEntry(reader.GetString(0), SqlReaders.ReadDouble(reader, 1)));
        }

        var monthly = new List<TimelineEntry>();
        reader.NextResult();
        while (reader.Read())
        {
            monthly.Add(new TimelineEntry(reader.GetString(0), SqlReaders.ReadDouble(reader, 1)));
        }

        // One statement for the daily timeline and the weekly one: session_date is stored in the
        // yyyy-MM-dd form to_char(session_date, 'YYYY-MM-DD') produces, so it is already the daily
        // label, and the weekly label is folded from the same dates in C# (see Weekly).
        var perNight = new List<(DateOnly Date, double IntegrationSeconds)>();
        reader.NextResult();
        while (reader.Read())
        {
            if (SqlReaders.ReadDate(reader, 0) is { } date)
            {
                perNight.Add((date, SqlReaders.ReadDouble(reader, 1)));
            }
        }

        var ingest = new List<IngestEntry>();
        reader.NextResult();
        while (reader.Read())
        {
            if (SqlReaders.ReadDate(reader, 0) is { } day)
            {
                ingest.Add(new IngestEntry(day, SqlReaders.ReadInt(reader, 1)));
            }
        }

        // The newest thirty runs are selected descending and then reversed, so the chart's X axis
        // runs forward in time.
        ingest.Reverse();

        reader.NextResult();
        reader.Read();
        var fitsBytes = reader.IsDBNull(0) ? 0L : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        var pageBytes = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);

        return new Aggregates(overview, topTargets, monthly, perNight, ingest, fitsBytes, pageBytes);
    }

    /// <summary>The write-ahead log's length, which <c>page_count * page_size</c> does not
    /// include: a database with a large uncheckpointed WAL occupies both. A read through
    /// <see cref="UserFiles"/>, never a directory walk, and 0 when there is no WAL file or the
    /// connection has no file behind it.</summary>
    private static long WalBytes(SqliteConnection connection)
    {
        var path = connection.DataSource;
        if (string.IsNullOrEmpty(path))
        {
            return 0;
        }

        var wal = path + "-wal";
        return UserFiles.Exists(wal) ? UserFiles.GetFileInfo(wal).Length : 0;
    }

    // ---- shaping the pass into read models ---------------------------------------------

    private static IReadOnlyList<EquipmentInventoryItem> Inventory(
        Dictionary<string, InventoryAccumulator> accumulators)
        => [.. accumulators
            .Select(entry => new EquipmentInventoryItem(
                entry.Key,
                entry.Value.FrameCount,
                entry.Value.IntegrationSeconds,
                entry.Value.Nights.Count,
                entry.Value.Targets.Count,
                entry.Value.Nights.Count == 0
                    ? null
                    : entry.Value.IntegrationSeconds / entry.Value.Nights.Count,
                Round2(Statistics.Median(entry.Value.Fwhm)),
                entry.Value.Fwhm.Count,
                Round2(Statistics.Median(entry.Value.GuidingRms)),
                entry.Value.RawNames.Count > 1))
            // The web orders by frame count descending and stops there, which leaves a tie to
            // Postgres. The name breaks it here so two rigs with the same frame count do not swap
            // places between reads.
            .OrderByDescending(item => item.FrameCount)
            .ThenBy(item => item.Name, StringComparer.Ordinal)];

    private static IReadOnlyList<EquipmentComboMetrics> Performance(
        Dictionary<(string Telescope, string Camera), ComboAccumulator> accumulators)
        => [.. accumulators
            .Select(entry => new EquipmentComboMetrics(
                entry.Key.Telescope,
                entry.Key.Camera,
                entry.Value.FrameCount,
                entry.Value.IntegrationSeconds,
                entry.Value.Sessions.Count == 0
                    ? null
                    : entry.Value.IntegrationSeconds / entry.Value.Sessions.Count,
                // The canonical-grouped medians, matching _query_equipment_mad, never a blend of
                // the per-filter medians. This is what makes Performance and Inventory agree.
                Round2(Statistics.Median(entry.Value.Hfr)),
                Round2(Minimum(entry.Value.Hfr)),
                Round2(Statistics.Median(entry.Value.Eccentricity)),
                Round2(Statistics.Median(entry.Value.Fwhm)),
                entry.Value.Fwhm.Count,
                // Not a web figure: spec 13's equipment comparison chart needs a per-combination
                // guiding median, and the inventory's is per camera and per telescope separately,
                // which is a different category axis. Same rule as the inventory's, so the two
                // cannot disagree about what a guiding median means.
                Round2(Statistics.Median(entry.Value.GuidingRms)),
                Round3(Statistics.Mad(entry.Value.Hfr)),
                Round3(Statistics.Mad(entry.Value.Eccentricity)),
                Round3(Statistics.Mad(entry.Value.Fwhm)),
                entry.Value.RawTelescopes.Count > 1 || entry.Value.RawCameras.Count > 1,
                Breakdown(entry.Value.Filters)))
            .OrderByDescending(combo => combo.FrameCount)
            .ThenBy(combo => combo.Telescope, StringComparer.Ordinal)
            .ThenBy(combo => combo.Camera, StringComparer.Ordinal)];

    /// <summary>One entry per canonical filter inside a combination. The medians are real medians
    /// over the filter's own frames, not the web's <c>weighted_median_approx</c>: that
    /// approximation exists only because the web could not re-aggregate a result set that SQL had
    /// already grouped, and this pass holds the raw values.</summary>
    private static IReadOnlyList<EquipmentFilterMetrics> Breakdown(
        IReadOnlyDictionary<string, FilterAccumulator> filters)
        => [.. filters
            .Select(entry => new EquipmentFilterMetrics(
                entry.Key,
                entry.Value.FrameCount,
                entry.Value.IntegrationSeconds,
                Round2(Statistics.Median(entry.Value.Hfr)),
                Round2(Minimum(entry.Value.Hfr)),
                Round2(Statistics.Median(entry.Value.Eccentricity)),
                Round2(Statistics.Median(entry.Value.Fwhm)),
                entry.Value.Fwhm.Count))
            .OrderByDescending(metrics => metrics.FrameCount)
            .ThenBy(metrics => metrics.FilterName, StringComparer.Ordinal)];

    private static DataQualityStats Quality(QualityAccumulator quality)
    {
        // Spec 7.2's pooling rule, through the one implementation the session card already uses
        // (questions.md Q5). Rebuilding the tie-break here would eventually give the Statistics
        // page and the session card two different library averages.
        var pooled = EccentricitySources.TryGetModalSource(quality.Eccentricity, out var modalSource);
        var modal = new List<double?>();
        if (pooled)
        {
            foreach (var (source, value) in quality.Eccentricity)
            {
                if (EccentricitySources.IsPooled(source, value, modalSource))
                {
                    modal.Add(value);
                }
            }
        }

        return new DataQualityStats(
            // Rounding to two decimals, but NOT through the web's falsy test: an average of
            // exactly 0.0 stays 0.0 and only an absent value is null (questions.md Q9).
            Round2(Statistics.Mean(quality.Hfr)),
            Round2(Statistics.Mean(quality.HfrArcsec)),
            Round2(Minimum(quality.Hfr)),
            Round2(Minimum(quality.HfrArcsec)),
            Round2(pooled ? Statistics.Mean(modal) : Statistics.Mean(quality.Eccentricity.Select(frame => frame.Value))),
            quality.UnscaledFrameCount,
            pooled ? quality.Eccentricity.Count - modal.Count : null,
            pooled ? modalSource : null,
            [.. PixelBuckets.Select((bucket, index) => new HfrBucket(
                bucket.High is { } high && high < PixelOverflowHigh
                    ? $"{Label(bucket.Low)}-{Label(high)}"
                    : Label(bucket.Low) + "+",
                bucket.Low,
                bucket.High,
                quality.PixelCounts[index]))],
            [.. ArcsecBuckets.Select((bucket, index) => new HfrBucket(
                bucket.High is { } high ? $"{Label(bucket.Low)}-{Label(high)}" : Label(bucket.Low) + "+",
                bucket.Low,
                bucket.High,
                quality.ArcsecCounts[index]))]);
    }

    /// <summary>
    /// The weekly timeline. Postgres' <c>to_char(session_date, 'IYYY-"W"IW')</c> has no SQLite
    /// equivalent: <c>strftime('%W')</c> is a Sunday-start week number with no ISO year, so it
    /// would put a different set of dates in a different set of weeks. This is the one grouping
    /// that cannot be done in SQL, so it is folded here from the per-night rows with
    /// <see cref="ISOWeek"/>, zero-padded to two digits exactly as <c>IW</c> is. Task 1's
    /// <c>AstroNight.IsoWeekMonday</c> applies the same ISO rule from the other direction.
    /// </summary>
    private static IReadOnlyList<TimelineEntry> Weekly(
        IReadOnlyList<(DateOnly Date, double IntegrationSeconds)> perNight)
    {
        var weeks = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (date, seconds) in perNight)
        {
            var midnight = date.ToDateTime(TimeOnly.MinValue);
            var isoYear = ISOWeek.GetYear(midnight).ToString("D4", CultureInfo.InvariantCulture);
            var isoWeek = ISOWeek.GetWeekOfYear(midnight).ToString("D2", CultureInfo.InvariantCulture);
            var label = $"{isoYear}-W{isoWeek}";
            weeks[label] = weeks.GetValueOrDefault(label) + seconds;
        }

        return [.. weeks
            .Select(entry => new TimelineEntry(entry.Key, entry.Value))
            .OrderBy(entry => entry.Period, StringComparer.Ordinal)];
    }

    // ---- small shared rules -------------------------------------------------------------

    /// <summary>The web's <c>nullif(x, 0)</c>, applied at exactly the sites the web applies it
    /// (<c>fwhm</c> and <c>guiding_rms_arcsec</c> in the inventory, <c>median_hfr</c>,
    /// <c>eccentricity</c> and <c>fwhm</c> in the performance queries) and nowhere else:
    /// <c>exposure_time</c>, the counts and the data-quality figures read their columns raw.
    /// </summary>
    private static double? NonZero(double? value) => value is null or 0 ? null : value;

    private static double? Minimum(List<double?> values)
        => values.Count == 0 ? null : values.Min();

    // Math.Round is half-to-even, which is what Python's round() does, so the port and the web
    // application round a .005 the same way.
    private static double? Round2(double? value)
        => value is { } number ? Math.Round(number, 2, MidpointRounding.ToEven) : null;

    private static double? Round3(double? value)
        => value is { } number ? Math.Round(number, 3, MidpointRounding.ToEven) : null;

    private static string Label(double bound)
        => bound.ToString("0.0", CultureInfo.InvariantCulture);

    private static (double Low, double? High)[] BuildArcsecBuckets()
    {
        var buckets = new (double Low, double? High)[17];
        for (var i = 0; i < 16; i++)
        {
            buckets[i] = (i * 0.5, (i + 1) * 0.5);
        }

        buckets[16] = (8.0, null);
        return buckets;
    }

    // ---- accumulators -------------------------------------------------------------------
    //
    // Named types rather than a dozen ToList() materializations of the same rows: the pass reads
    // each LIGHT frame once and every group's figures fall out of these.

    private sealed class PassResult
    {
        public Dictionary<string, InventoryAccumulator> Cameras { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, InventoryAccumulator> Telescopes { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string Telescope, string Camera), ComboAccumulator> Combos { get; } = [];
        public Dictionary<string, double> FilterUsage { get; } = new(StringComparer.Ordinal);
        public Dictionary<DateOnly, HashSet<string>> RigsPerNight { get; } = [];
        public QualityAccumulator Quality { get; } = new();
    }

    private sealed class InventoryAccumulator
    {
        public int FrameCount { get; private set; }
        public double IntegrationSeconds { get; private set; }
        public HashSet<DateOnly> Nights { get; } = [];
        public HashSet<Guid> Targets { get; } = [];
        public HashSet<string> RawNames { get; } = new(StringComparer.Ordinal);
        public List<double?> Fwhm { get; } = [];
        public List<double?> GuidingRms { get; } = [];

        public void Add(DateOnly? sessionDate, Guid? targetId, double exposure, double? fwhm, double? guiding)
        {
            FrameCount++;
            IntegrationSeconds += exposure;
            if (sessionDate is { } night)
            {
                Nights.Add(night);
            }

            if (targetId is { } target)
            {
                Targets.Add(target);
            }

            if (NonZero(fwhm) is { } usableFwhm)
            {
                Fwhm.Add(usableFwhm);
            }

            if (NonZero(guiding) is { } usableGuiding)
            {
                GuidingRms.Add(usableGuiding);
            }
        }
    }

    private sealed class ComboAccumulator
    {
        private readonly Dictionary<string, FilterAccumulator> _filters = new(StringComparer.Ordinal);

        public int FrameCount { get; private set; }
        public double IntegrationSeconds { get; private set; }
        public HashSet<DateOnly> Sessions { get; } = [];
        public HashSet<string> RawTelescopes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> RawCameras { get; } = new(StringComparer.Ordinal);
        public List<double?> Hfr { get; } = [];
        public List<double?> Eccentricity { get; } = [];
        public List<double?> Fwhm { get; } = [];
        public List<double?> GuidingRms { get; } = [];
        public IReadOnlyDictionary<string, FilterAccumulator> Filters => _filters;

        public void Add(
            DateOnly? sessionDate,
            double exposure,
            double? hfr,
            double? eccentricity,
            double? fwhm,
            double? guiding)
        {
            FrameCount++;
            IntegrationSeconds += exposure;
            if (sessionDate is { } night)
            {
                Sessions.Add(night);
            }

            AddNonZero(Hfr, hfr);
            AddNonZero(Eccentricity, eccentricity);
            AddNonZero(Fwhm, fwhm);
            AddNonZero(GuidingRms, guiding);
        }

        public FilterAccumulator Filter(string canonical)
        {
            if (!_filters.TryGetValue(canonical, out var accumulator))
            {
                accumulator = new FilterAccumulator();
                _filters[canonical] = accumulator;
            }

            return accumulator;
        }
    }

    private sealed class FilterAccumulator
    {
        public int FrameCount { get; private set; }
        public double IntegrationSeconds { get; private set; }
        public List<double?> Hfr { get; } = [];
        public List<double?> Eccentricity { get; } = [];
        public List<double?> Fwhm { get; } = [];

        public void Add(double exposure, double? hfr, double? eccentricity, double? fwhm)
        {
            FrameCount++;
            IntegrationSeconds += exposure;
            AddNonZero(Hfr, hfr);
            AddNonZero(Eccentricity, eccentricity);
            AddNonZero(Fwhm, fwhm);
        }
    }

    private sealed class QualityAccumulator
    {
        public List<double?> Hfr { get; } = [];
        public List<double?> HfrArcsec { get; } = [];
        public List<(string? Source, double? Value)> Eccentricity { get; } = [];
        public int UnscaledFrameCount { get; private set; }
        public int[] PixelCounts { get; } = new int[PixelBuckets.Length];
        public int[] ArcsecCounts { get; } = new int[ArcsecBuckets.Length];

        /// <summary>_query_data_quality applies no <c>nullif</c>, so a stored HFR of 0 is a real
        /// measurement here even though the equipment queries treat it as absent. The arcsecond
        /// figures pool only frames carrying both an HFR and a plate scale, because in the web
        /// source the product is NULL otherwise and every aggregate skips it.</summary>
        public void Add(double? hfr, double? eccentricity, string? eccentricitySource, double? plateScale)
        {
            if (eccentricity is not null)
            {
                Eccentricity.Add((eccentricitySource, eccentricity));
            }

            if (hfr is not { } value)
            {
                return;
            }

            Hfr.Add(value);
            Count(PixelCounts, PixelBuckets, value);

            if (plateScale is not { } scale)
            {
                UnscaledFrameCount++;
                return;
            }

            var arcsec = value * scale;
            HfrArcsec.Add(arcsec);
            Count(ArcsecCounts, ArcsecBuckets, arcsec);
        }

        // A value lands in the one bucket whose [low, high) it falls in, and in none at all when
        // it is below the first low or at or above the last bounded high: the web's per-bucket
        // count(id).filter(x >= low, x < high) has exactly that hole and the histogram is a
        // distribution, not a total. An unbounded high (the arcsecond overflow) has no upper test.
        private static void Count(int[] counts, (double Low, double? High)[] buckets, double value)
        {
            for (var index = 0; index < buckets.Length; index++)
            {
                var (low, high) = buckets[index];
                if (value >= low && (high is not { } bound || value < bound))
                {
                    counts[index]++;
                    return;
                }
            }
        }
    }

    private static void AddNonZero(List<double?> values, double? value)
    {
        if (NonZero(value) is { } usable)
        {
            values.Add(usable);
        }
    }

    private sealed record Aggregates(
        StatsOverview Overview,
        IReadOnlyList<TopTargetEntry> TopTargets,
        IReadOnlyList<TimelineEntry> TimelineMonthly,
        IReadOnlyList<(DateOnly Date, double IntegrationSeconds)> PerNight,
        IReadOnlyList<IngestEntry> IngestHistory,
        long FitsBytesCatalogued,
        long DatabasePageBytes);
}
