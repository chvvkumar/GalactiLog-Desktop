using System.Globalization;
using System.Text.Json;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Phd2;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>
/// The whole Analysis page's data side (spec 12.14), port of
/// <c>backend/app/api/analysis.py</c> at <c>591234b</c>: the Correlation scatter with its trend,
/// the Distributions histogram and box plot, the Time Series with its two moving averages, the
/// ten by ten correlation matrix, the Compare tab's two groups, and the two picker lists the
/// filter bar is built from. Where a line number is cited with no other file named, the file is
/// <c>analysis.py</c> at that revision.
/// </summary>
/// <remarks>
/// <para>
/// Read-only, synchronous and library-wide, as every other query in this namespace is (ruling
/// A8): the caller runs it off the UI thread and publishes through the post seam. Every figure is
/// computed by <see cref="Analysis"/>, the pure Core port; this class's job is to get the right
/// rows, in a defined order, into the right groups.
/// </para>
/// <para>
/// <b>Row order is part of the answer.</b> <c>analysis.py</c> issues no <c>ORDER BY</c> anywhere,
/// so its row order is PostgreSQL's, which is undefined. Three figures on this page depend on it:
/// every compensated sum is order dependent in the last bits, the 5,000 point cap picks
/// <c>points[floor(i * step)]</c> and so decides WHICH points are drawn, and the session grouping
/// emits groups in first-appearance order. The port therefore orders every statement that feeds a
/// figure by <c>i.session_date, i.id</c> and the exact-parity bar (ruling A4) is defined against
/// THAT sequence. <c>id</c> compares ordinally, which is SQLite's default BINARY collation over
/// the TEXT spelling the provider stores a <see cref="Guid"/> as, and is the same order Python's
/// <c>str</c> comparison gives over the same spelling; no C# re-sort of the rows exists here,
/// because a culture-sensitive or case-insensitive comparer would silently reorder every point
/// set.
/// </para>
/// <para>
/// Every operand a caller can influence is BOUND. The only interpolation into a statement is
/// <see cref="SqlFragments.LightFrameOnly"/> and a column name from
/// <c>AnalysisMetrics.Column</c>, both constants of this assembly chosen by an enum, which can
/// never carry user text; the alias map, the dates and every metric value go through
/// <see cref="SqlParameters"/>.
/// </para>
/// <para>
/// Departures from the Python, all of them, and every one is recorded in spec 12.14: the explicit
/// <c>ORDER BY</c> above; a row whose <c>session_date</c> is unreadable is skipped rather than
/// labelled <c>None</c> (ruling A11); <c>strftime('%Y-%m', ...)</c> stands in for PostgreSQL's
/// <c>to_char</c>; the PHD2 rig comes from the live profile map rather than the stored column
/// (ruling A10); the matrix is computed in C# because SQLite has no <c>corr()</c>;
/// <see cref="TimeSeriesPoint.TargetName"/> is null unless the night has exactly one target;
/// <c>Compare</c> answers a state rather than an HTTP 400 and <see cref="Distribution"/> answers
/// null; and <c>DistinctPlateScales</c> is published where the web counts nothing (user ruling
/// U3).
/// </para>
/// </remarks>
/// <param name="profileMap">The raw <c>general.phd2_profile_map</c>, normally
/// <c>() =&gt; settingsStore.GetGeneral().Phd2ProfileMap</c>, read once per call and never per
/// row, exactly as <see cref="GuidingStatsQuery"/> takes it and for the reason that constructor's
/// own parameter comment gives (ruling A10).</param>
/// <param name="stats">The existing statistics memo, for the equipment combination list of
/// <see cref="EquipmentCombinations"/> only. This query runs no SQL of its own for that list.</param>
public sealed class AnalysisQuery(
    DatabaseConnectionString connectionString,
    AliasMapCache aliases,
    StatsCache stats,
    Func<JsonElement?> profileMap)
{
    /// <summary>The deterministic row order every statement that feeds a figure carries, and the
    /// order the parity bar is defined against. <c>id</c> because <c>session_date</c> alone is not
    /// unique and an unordered tie is not an answer.</summary>
    private const string RowOrder = "ORDER BY i.session_date, i.id";

    private const string AndSeparator = "\n  AND ";

    // ---- the six figures ---------------------------------------------------------------

    /// <summary>The Correlation tab's scatter, its trend and its two stats cards, port of lines
    /// 351 to 484. <paramref name="x"/> may be any of the twenty five metrics, the PHD2 five
    /// included, which reach the night rollup of <c>Phd2Nights</c> instead of an
    /// <c>images</c> column; <paramref name="y"/> may be any of the twenty in
    /// <c>METRIC_MAP</c>, which is what line 366's guard allows, and never a PHD2
    /// member.</summary>
    /// <remarks>Both granularities are honoured (ruling A17): <c>Frame</c> plots one point per
    /// row in row order, <c>Session</c> one point per <c>(session_date, resolved_target_id)</c>
    /// group at the group's two medians, with NO rig in the key, groups emitted in
    /// first-appearance order.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">A metric outside the set the tab accepts.
    /// Task 4 never offers one in a picker, so this is a programming error guard and not a user
    /// path.</exception>
    public CorrelationResult Correlation(AnalysisMetric x, AnalysisMetric y, AnalysisFilter filter)
    {
        if (!Enum.IsDefined(x))
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, "Not an Analysis metric.");
        }

        RequireColumnMetric(y, nameof(y));

        var map = aliases.Current;
        var isPhd2 = AnalysisMetrics.Phd2X.Contains(x);

        return Read((context, connection) =>
        {
            var nights = isPhd2 ? Phd2Nights(context, map, x) : null;

            var parameters = new SqlParameters();
            // The X column carries no non-null predicate when X is a PHD2 metric: there is no
            // column to test, and the equivalent of the web's `x IS NOT NULL` (line 404) is the
            // drop of a frame whose (rig, night) pair has no rollup value, below.
            var where = isPhd2
                ? Where(filter, map, parameters, y)
                : Where(filter, map, parameters, x, y);

            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT {(isPhd2 ? "i.telescope" : $"i.{AnalysisMetrics.Column(x)}")},
                       i.{AnalysisMetrics.Column(y)}, i.session_date, i.resolved_target_id,
                       i.arcsec_per_pixel
                FROM images i
                WHERE {where}
                {RowOrder};
                """;
            parameters.ApplyTo(command);

            var scales = new HashSet<double>();
            var rows = new List<(double X, double Y, DateOnly Night, Guid? TargetId)>();

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    // Ruling A11: an unreadable session_date is skipped, never thrown on.
                    // TargetListingQuery shipped the defect this rule exists for, where one bad
                    // row emptied the whole dashboard listing.
                    if (SqlReaders.ReadDate(reader, 2) is not { } night)
                    {
                        continue;
                    }

                    double xValue;
                    if (isPhd2)
                    {
                        var rawTelescope = SqlReaders.ReadText(reader, 0);
                        var rig = map.CanonicalTelescope(rawTelescope) ?? rawTelescope;

                        // The port of the LEFT JOIN at lines 395 to 401 followed by line 404's
                        // `x IS NOT NULL`: a frame whose (rig, night) pair has no PHD2 night, or
                        // whose matched figure is null, is dropped from the plot. Strict
                        // session_date equality, no widening and no timezone reasoning.
                        if (rig is null
                            || !nights!.TryGetValue((night, rig), out var figure)
                            || figure is not { } value)
                        {
                            continue;
                        }

                        xValue = value;
                    }
                    else
                    {
                        xValue = SqlReaders.ReadDouble(reader, 0);
                    }

                    rows.Add((
                        xValue,
                        SqlReaders.ReadDouble(reader, 1),
                        night,
                        reader.IsDBNull(3) ? null : reader.GetGuid(3)));
                    AddScale(scales, reader, 4);
                }
            }

            var grouped = filter.Granularity == AnalysisGranularity.Session
                ? SessionPoints(rows)
                : rows;

            var xs = grouped.ConvertAll(point => point.X);
            var ys = grouped.ConvertAll(point => point.Y);

            // Line 446: the fences are computed once per axis over the FULL raw point set and
            // only when it holds at least four points; below that every flag is false.
            OutlierFences? xFences = null;
            OutlierFences? yFences = null;
            if (grouped.Count >= 4)
            {
                xFences = Analysis.Fences(xs);
                yFences = Analysis.Fences(ys);
            }

            var points = grouped.ConvertAll(point => new CorrelationPoint(
                point.X,
                point.Y,
                point.Night,
                point.TargetId,
                xFences is not null
                    && (Analysis.IsOutlier(point.X, xFences)
                        || Analysis.IsOutlier(point.Y, yFences!))));

            // Lines 451 to 455: the trend and both stats cards are computed over the FULL point
            // set, BEFORE any downsampling, so they describe every matching frame. Quality is the
            // port's addition for defect D6 and rides beside the web's own r, never instead of it.
            var trend = Analysis.Trend(xs, ys);
            var xStats = Analysis.Summary(xs);
            var yStats = Analysis.Summary(ys);
            var quality = Analysis.PearsonQuality(xs, ys);

            // Lines 412 and 426 resolve the names before the cap, so the FULL point set's targets
            // are resolved and not the downsampled one's.
            var names = TargetNames(connection, points);

            var sampled = Analysis.Downsample(points, Analysis.CorrelationPointCap);

            return new CorrelationResult(
                sampled, trend, xStats, yStats, names, quality,
                scales.Count, points.Count, sampled.Count);
        });
    }

    /// <summary>The Distributions tab's histogram mode, port of lines 487 to 564. Null when the
    /// selection leaves fewer than two values (line 528), which is the port of the web's HTTP 400
    /// and is a page state rather than an error. Granularity is honoured.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A PHD2 metric, which line 499 rejects.
    /// </exception>
    public DistributionResult? Distribution(AnalysisMetric metric, AnalysisFilter filter)
    {
        RequireColumnMetric(metric, nameof(metric));
        var map = aliases.Current;

        return Read<DistributionResult?>((_, connection) =>
        {
            var parameters = new SqlParameters();
            var where = Where(filter, map, parameters, metric);

            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT i.{AnalysisMetrics.Column(metric)}, i.session_date, i.resolved_target_id,
                       i.arcsec_per_pixel
                FROM images i
                WHERE {where}
                {RowOrder};
                """;
            parameters.ApplyTo(command);

            var scales = new HashSet<double>();
            var rows = new List<(double Value, DateOnly Night, Guid? TargetId)>();

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (SqlReaders.ReadDate(reader, 1) is not { } night)
                    {
                        continue;
                    }

                    rows.Add((
                        SqlReaders.ReadDouble(reader, 0),
                        night,
                        reader.IsDBNull(2) ? null : reader.GetGuid(2)));
                    AddScale(scales, reader, 3);
                }
            }

            // Lines 520 to 526: under session granularity one value per
            // (session_date, resolved_target_id) group, the group's median, groups in
            // first-appearance order.
            var values = filter.Granularity == AnalysisGranularity.Session
                ? SessionMedians(rows)
                : rows.ConvertAll(row => row.Value);

            if (values.Count < 2)
            {
                return null;
            }

            // Guarded by the count above, which is the same gate Summary itself applies.
            var summary = Analysis.Summary(values)!;

            // Lines 546 and 547 read the mean and the deviation off the SummaryStats object whose
            // fields line 190 already rounded to six, so the cubed sum runs on rounded inputs
            // (defect D3). Passing the full-precision figures changes the fourth decimal on real
            // data, which ruling A4 forbids. The round to 4 is line 558's, the caller's.
            var skewness = PythonNumerics.RoundLikePython(
                Analysis.Skewness(values, summary.Mean, summary.StdDev), 4);

            return new DistributionResult(
                Analysis.Histogram(values), summary, skewness, scales.Count);
        });
    }

    /// <summary>The Distributions tab's box plot mode, port of lines 567 to 657. Granularity is
    /// IGNORED and the rows are always frames (line 568 has no granularity parameter), which is
    /// parity with the web and not an omission (ruling A17). A group whose box answers null, that
    /// is fewer than four values, is dropped with no row and no notice (lines 650 to 652).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A PHD2 metric, which line 579 rejects.
    /// </exception>
    public BoxPlotResult BoxPlot(AnalysisMetric metric, BoxPlotGrouping groupBy, AnalysisFilter filter)
    {
        RequireColumnMetric(metric, nameof(metric));

        if (!Enum.IsDefined(groupBy))
        {
            throw new ArgumentOutOfRangeException(nameof(groupBy), groupBy, "Not a box plot grouping.");
        }

        var map = aliases.Current;

        return Read((_, connection) =>
        {
            var parameters = new SqlParameters();
            var where = Where(filter, map, parameters, metric);

            using var command = connection.CreateCommand();
            // One statement for all four groupings rather than four: the extra columns cost one
            // read each and the alternative is four nearly identical statements that can drift.
            // strftime stands in for PostgreSQL's to_char (line 597), which SQLite does not have;
            // it answers null for a value it cannot parse, and a null month is dropped below.
            // The month reads capture_date while the date range filter reads session_date, which
            // is the web's own asymmetry (line 597 against line 167) and spec 12.14 keeps it.
            command.CommandText =
                $"""
                SELECT i.{AnalysisMetrics.Column(metric)}, i.session_date, i.resolved_target_id,
                       i.telescope, i.camera, i.filter_used,
                       strftime('%Y-%m', i.capture_date), i.arcsec_per_pixel
                FROM images i
                WHERE {where}
                {RowOrder};
                """;
            parameters.ApplyTo(command);

            var scales = new HashSet<double>();
            var rows = 0;
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var groups = new List<(string Name, List<double> Values)>();
            var targetIds = new Dictionary<string, Guid>(StringComparer.Ordinal);

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    // Ruling A11 again, and it reaches this tab too although the web's box plot
                    // never reads the column: the port orders by it, so a row it cannot read is
                    // a row with no defined position, and the one rule is that such a row is
                    // skipped everywhere rather than in four of six methods.
                    if (SqlReaders.ReadDate(reader, 1) is null)
                    {
                        continue;
                    }

                    // BEFORE the grouping, deliberately: user ruling U3 counts the scales among
                    // the frames the QUERY READ, after every filter and every non-null metric
                    // predicate and before any grouping, so a frame the grouping drops (no
                    // filter name, no camera, an unparseable month, no resolved target) still
                    // carries its plate scale into the count. Counting after the group key would
                    // make the warning depend on which grouping the reader happened to pick.
                    AddScale(scales, reader, 7);

                    // Beside AddScale and for the same reason: BoxPlotResult.RowCount is over the
                    // rows the QUERY READ, before any grouping key, so the figure does not move
                    // with the grouping the reader picked. It is what lets the tab tell spec
                    // 12.14's "Box plot with no group left" from "No row matches the filters".
                    rows++;

                    var key = GroupKey(map, groupBy, reader, targetIds);
                    if (key is null)
                    {
                        continue;
                    }

                    Bucket(index, groups, key).Add(SqlReaders.ReadDouble(reader, 0));
                }
            }

            if (groupBy == BoxPlotGrouping.Target)
            {
                groups = MergeByTargetName(connection, groups, targetIds);
            }

            // sorted(grouped.items()) at line 649 is an ordinal comparison, never the current
            // culture: the default string comparer in .NET is culture-sensitive and would give a
            // different group order on the user's machine than on a reviewer's.
            groups.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));

            var boxes = new List<BoxPlot>(groups.Count);
            foreach (var (name, values) in groups)
            {
                if (Analysis.Box(values, name) is { } box)
                {
                    boxes.Add(box);
                }
            }

            return new BoxPlotResult(boxes, scales.Count, rows);
        });
    }

    /// <summary>The Time Series tab, port of lines 660 to 755: one point per imaged night, the
    /// night's median, with the two moving averages and the month boundaries. Granularity is
    /// IGNORED and the grouping is always nightly (line 661), which is parity with the web
    /// (ruling A17).</summary>
    /// <exception cref="ArgumentOutOfRangeException">A PHD2 metric, which line 671 rejects.
    /// </exception>
    public TimeSeriesResult TimeSeries(AnalysisMetric metric, AnalysisFilter filter)
    {
        RequireColumnMetric(metric, nameof(metric));
        var map = aliases.Current;

        return Read((_, connection) =>
        {
            var parameters = new SqlParameters();
            var where = Where(filter, map, parameters, metric);

            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT i.{AnalysisMetrics.Column(metric)}, i.session_date, i.resolved_target_id,
                       i.arcsec_per_pixel
                FROM images i
                WHERE {where}
                {RowOrder};
                """;
            parameters.ApplyTo(command);

            var scales = new HashSet<double>();
            var nightly = new Dictionary<DateOnly, (List<double> Values, HashSet<Guid> Targets)>();

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (SqlReaders.ReadDate(reader, 1) is not { } night)
                    {
                        continue;
                    }

                    if (!nightly.TryGetValue(night, out var entry))
                    {
                        entry = (new List<double>(), new HashSet<Guid>());
                        nightly[night] = entry;
                    }

                    entry.Values.Add(SqlReaders.ReadDouble(reader, 0));
                    if (!reader.IsDBNull(2))
                    {
                        entry.Targets.Add(reader.GetGuid(2));
                    }

                    AddScale(scales, reader, 3);
                }
            }

            var names = TargetNames(
                connection,
                [.. nightly.Values.SelectMany(entry => entry.Targets).Distinct()]);

            var points = new List<TimeSeriesPoint>(nightly.Count);
            foreach (var night in nightly.Keys.Order())
            {
                var (values, targets) = nightly[night];

                // Lines 712 and 713 take list(target_ids)[0], an arbitrary member of a Python
                // set, so the web's label on a two target night is whichever the set happened to
                // order first and carries no meaning (defect D4). Spec 12.14 replaces it: the
                // name only when the night has exactly one target, otherwise null, and the view
                // reads "Mixed". No string of that word exists in this assembly.
                var name = targets.Count == 1 && names.TryGetValue(targets.First(), out var only)
                    ? only
                    : null;

                points.Add(new TimeSeriesPoint(
                    night,
                    PythonNumerics.RoundLikePython(Statistics.Median(values)!.Value, 6),
                    name,
                    targets.Count,
                    values.Count));
            }

            // Line 721 builds raw_values from p.value, the nightly median line 716 ALREADY
            // rounded to six, and _moving_avg sums those. Passing the unrounded medians produces
            // a different sixth decimal on every moving average point (ruling S10).
            var rounded = points.ConvertAll(point => point.Value);

            var months = new HashSet<string>(StringComparer.Ordinal);
            var boundaries = new List<DateOnly>();
            foreach (var point in points)
            {
                if (months.Add(point.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture)))
                {
                    boundaries.Add(point.Date);
                }
            }

            return new TimeSeriesResult(
                points,
                MovingAverage(points, rounded, Analysis.MovingAverageShortWindow),
                MovingAverage(points, rounded, Analysis.MovingAverageLongWindow),
                boundaries,
                scales.Count);
        });
    }

    /// <summary>The correlation matrix, port of lines 758 to 818: exactly one hundred cells,
    /// <c>AnalysisMetrics.X</c> outer and <c>AnalysisMetrics.Y</c> inner (ruling A26). Granularity
    /// is IGNORED and the rows are always frames (line 759).</summary>
    /// <remarks>
    /// <para>
    /// SQLite has no <c>corr()</c>, so the cells are computed in C#. The cell is
    /// <c>Analysis.MatrixPearson</c>, which is PostgreSQL's <c>corr(Y, X)</c> and not
    /// <c>Analysis.PearsonR</c> (ruling A19): a row where either argument is null is excluded,
    /// which is what <c>NPoints</c> counts, and a column with zero variance over the pair's rows
    /// gives NO value where <c>_pearson_r</c> would give <c>0.0</c>. <b>Both gates live inside
    /// that member</b>, the ten point minimum and the min equals max test, so this query holds
    /// neither and cannot forget either. <c>NPoints</c> is still reported when the r is null (line
    /// 813), so the tooltip can say how many points there were.
    /// </para>
    /// <para>
    /// ponytail: one read fills twenty value lists and twenty presence lists, one per metric, and
    /// each pair then copies its paired rows into two buffers rented once at the row count and
    /// reused across all one hundred pairs. Ceiling: a 200,000 frame library is about 32 MB of
    /// doubles for the duration of one call, off the UI thread, behind
    /// <see cref="AnalysisCache"/>, which is the same ceiling <see cref="StatsQuery"/>'s own
    /// remark already accepts for the same reason. Upgrade path if it ever matters: a streaming
    /// pass per pair, never a hundred statements. Holding a value list per pair instead would be
    /// a hundred copies of the library, which is why the buffers are reused.
    /// </para>
    /// </remarks>
    public MatrixResult Matrix(AnalysisFilter filter)
    {
        var map = aliases.Current;

        return Read((_, connection) =>
        {
            var metrics = new List<AnalysisMetric>(AnalysisMetrics.X);
            metrics.AddRange(AnalysisMetrics.Y);

            var parameters = new SqlParameters();
            // No per-metric non-null predicate: the matrix reads twenty columns and gates per
            // pair in C# instead, which is what corr()'s own null handling does (line 794).
            var where = Where(filter, map, parameters);

            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT {string.Join(", ", metrics.Select(metric => $"i.{AnalysisMetrics.Column(metric)}"))},
                       i.arcsec_per_pixel, i.session_date
                FROM images i
                WHERE {where}
                {RowOrder};
                """;
            parameters.ApplyTo(command);

            var scales = new HashSet<double>();
            var values = new List<double>[metrics.Count];
            var present = new List<bool>[metrics.Count];
            for (var i = 0; i < metrics.Count; i++)
            {
                values[i] = [];
                present[i] = [];
            }

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (SqlReaders.ReadDate(reader, metrics.Count + 1) is null)
                    {
                        continue;
                    }

                    for (var i = 0; i < metrics.Count; i++)
                    {
                        var value = SqlReaders.ReadNullableDouble(reader, i);
                        values[i].Add(value ?? 0d);
                        present[i].Add(value is not null);
                    }

                    AddScale(scales, reader, metrics.Count);
                }
            }

            var rowCount = values[0].Count;
            var xBuffer = new double[rowCount];
            var yBuffer = new double[rowCount];

            var cells = new List<MatrixCell>(AnalysisMetrics.X.Count * AnalysisMetrics.Y.Count);
            for (var xi = 0; xi < AnalysisMetrics.X.Count; xi++)
            {
                for (var yi = 0; yi < AnalysisMetrics.Y.Count; yi++)
                {
                    var xValues = values[xi];
                    var xPresent = present[xi];
                    var yValues = values[AnalysisMetrics.X.Count + yi];
                    var yPresent = present[AnalysisMetrics.X.Count + yi];

                    var n = 0;
                    for (var row = 0; row < rowCount; row++)
                    {
                        if (xPresent[row] && yPresent[row])
                        {
                            xBuffer[n] = xValues[row];
                            yBuffer[n] = yValues[row];
                            n++;
                        }
                    }

                    var r = Analysis.MatrixPearson(
                        new ArraySegment<double>(xBuffer, 0, n),
                        new ArraySegment<double>(yBuffer, 0, n));

                    cells.Add(new MatrixCell(
                        AnalysisMetrics.X[xi],
                        AnalysisMetrics.Y[yi],
                        r is { } value ? PythonNumerics.RoundLikePython(value, 4) : null,
                        n));
                }
            }

            return new MatrixResult(cells, scales.Count);
        });
    }

    /// <summary>The Compare tab, port of lines 821 to 933. Takes only the dates and NOT an
    /// <see cref="AnalysisFilter"/>, because the web applies no equipment and no filter here
    /// (lines 853 to 868, ruling A17): the two groups ARE the equipment or the filter, and this
    /// signature makes it impossible to pass a filter and believe it was applied.</summary>
    /// <remarks>
    /// Never null (ruling S7). The web's HTTP 400 for a thin group (line 883) becomes
    /// <see cref="CompareResult.State"/> with both raw counts, because spec 12.14's States table
    /// requires the tab to name WHICH group is short and to tell that apart from "no row matches
    /// the filters", and a bare null carries neither. <see cref="CompareResult.GroupA"/> and
    /// <see cref="CompareResult.GroupB"/> are non-null exactly when the state is
    /// <see cref="CompareState.Ok"/>, while the two names are always carried, so the tab can name
    /// a group whose box it cannot draw.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A PHD2 metric, which line 832 rejects.
    /// </exception>
    public CompareResult Compare(
        AnalysisMetric metric, CompareMode mode, string groupA, string groupB,
        DateOnly? from, DateOnly? to)
    {
        RequireColumnMetric(metric, nameof(metric));

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a compare mode.");
        }

        ArgumentNullException.ThrowIfNull(groupA);
        ArgumentNullException.ThrowIfNull(groupB);

        var map = aliases.Current;

        return Read((_, connection) =>
        {
            var a = CompareValues(connection, map, metric, mode, groupA, from, to);
            var b = CompareValues(connection, map, metric, mode, groupB, from, to);

            // The RAW value counts, before the four value gate, so they are the figures the tab's
            // sentence prints.
            var countA = a.Values.Count;
            var countB = b.Values.Count;

            var state = (countA, countB) switch
            {
                (0, 0) => CompareState.NoRows,
                ( < 4, < 4) => CompareState.BothShort,
                ( < 4, _) => CompareState.GroupAShort,
                (_, < 4) => CompareState.GroupBShort,
                _ => CompareState.Ok,
            };

            if (state != CompareState.Ok)
            {
                return new CompareResult(
                    state, groupA, groupB, countA, countB, null, null, null, false, null, null);
            }

            // Lines 885 to 888: the boxes and the two stats cards are over the PIXEL values, and
            // a frame with no plate scale contributes to both of them (line 841's own comment
            // says so) and to nothing else. Both are guarded by the four value gate above.
            var boxA = new CompareGroup(groupA, Analysis.Box(a.Values, groupA)!, Analysis.Summary(a.Values)!);
            var boxB = new CompareGroup(groupB, Analysis.Box(b.Values, groupB)!, Analysis.Summary(b.Values)!);

            bool comparable;
            string? verdict;
            double? medianArcsecA = null;
            double? medianArcsecB = null;

            // Ruling A18: _PIXEL_METRICS holds hfr alone (line 80) and has exactly one reader,
            // line 843, inside /compare. Every other tab on this page reads images.median_hfr in
            // pixels and converts nothing.
            if (metric == AnalysisMetric.Hfr)
            {
                if (a.Arcsec.Count >= 4 && b.Arcsec.Count >= 4)
                {
                    // Three decimals here, not six: lines 911 and 912.
                    medianArcsecA = PythonNumerics.RoundLikePython(Statistics.Median(a.Arcsec)!.Value, 3);
                    medianArcsecB = PythonNumerics.RoundLikePython(Statistics.Median(b.Arcsec)!.Value, 3);
                    comparable = true;
                    verdict = Analysis.CompareVerdict(
                        groupA, groupB, medianArcsecA.Value, medianArcsecB.Value,
                        boxA.Stats.Count, boxB.Stats.Count, " (arcsec)");
                }
                else
                {
                    // The web builds a sentence here (lines 916 to 920) that its own frontend
                    // discards and composes again itself, so the port returns null and Task 6
                    // composes the one reachable sentence from Comparable and the two medians.
                    comparable = false;
                    verdict = null;
                }
            }
            else
            {
                comparable = true;
                verdict = Analysis.CompareVerdict(
                    groupA, groupB, boxA.Stats.Median, boxB.Stats.Median,
                    boxA.Stats.Count, boxB.Stats.Count, "");
            }

            return new CompareResult(
                CompareState.Ok, groupA, groupB, countA, countB, boxA, boxB,
                verdict, comparable, medianArcsecA, medianArcsecB);
        });
    }

    // ---- the two picker lists ----------------------------------------------------------

    /// <summary>The canonical telescope and camera pairings the filter bar and the Compare tab's
    /// equipment pickers offer, the port of <c>AnalysisPage.tsx</c> lines 93 to 102, which maps
    /// the stats response's own <c>equipment_performance</c> list. In that list's own order
    /// (frame count descending, then telescope, then camera, ordinally).</summary>
    /// <remarks>
    /// Runs NO SQL. ponytail: on a library whose Statistics page has not been opened, the first
    /// read here builds the whole stats aggregate. Ceiling: one full pass over the LIGHT rows, off
    /// the UI thread, behind <see cref="StatsCache"/>, once per invalidation, which is the same
    /// cost the web pays for the same list. Upgrade path if it ever matters: a dedicated
    /// <c>SELECT DISTINCT telescope, camera</c>, which would have to re-implement the alias
    /// folding and the <c>Grouped</c> rule, and that second copy is what design lesson 1 forbids.
    /// </remarks>
    public IReadOnlyList<EquipmentCombination> EquipmentCombinations()
        => [.. stats.Current.EquipmentPerformance.Select(combo =>
            new EquipmentCombination(combo.Telescope, combo.Camera, combo.Grouped))];

    /// <summary>The distinct <c>filter_used</c> values of LIGHT frames, port of lines 335 to 348.
    /// <b>Raw stored values, not folded through the alias map</b>, which is what the web returns.
    /// The box plot's own By Filter grouping DOES fold (line 623), so the two lists can differ on
    /// a library with a configured filter group: that is the web's behaviour and not a defect of
    /// the port.</summary>
    public IReadOnlyList<string> Filters()
        => Read((_, connection) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT DISTINCT i.filter_used
                FROM images i
                WHERE i.{SqlFragments.LightFrameOnly}
                  AND i.filter_used IS NOT NULL
                ORDER BY i.filter_used;
                """;

            var filters = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (SqlReaders.ReadText(reader, 0) is { } value)
                {
                    filters.Add(value);
                }
            }

            return (IReadOnlyList<string>)filters;
        });

    // ---- the shared read shape ---------------------------------------------------------

    // One short-lived context per call through GalactiLogContextOptions.Create, the shape
    // GuidingStatsQuery and SessionDetailQuery use, so PragmaConnectionInterceptor stays in the
    // path. The context is handed over too, because the PHD2 night rollup reads phd2_sessions
    // through EF exactly as GuidingStatsQuery does.
    private T Read<T>(Func<GalactiLogContext, SqliteConnection, T> read)
    {
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        return read(context, (SqliteConnection)context.Database.GetDbConnection());
    }

    /// <summary>The shared predicates of lines 403 to 407 and the shared filter of lines 146 to
    /// 170, as one WHERE body.</summary>
    /// <remarks>
    /// The only text interpolated here is <see cref="SqlFragments.LightFrameOnly"/> and a column
    /// name from <c>AnalysisMetrics.Column</c>: both are constants of this assembly chosen by an
    /// enum and can never carry user text. Every operand a caller can influence, the alias
    /// variants, the filter name and both dates, is bound.
    /// <para>
    /// ponytail: the alias <c>IN</c> list grows with the number of raw spellings the user
    /// configured for one canonical name. Ceiling:
    /// <c>SQLITE_MAX_VARIABLE_NUMBER</c>, 32,766 in every build this application ships, which an
    /// alias group of thirty thousand spellings cannot plausibly reach. Upgrade path if a real
    /// library ever gets near it: chunk the list, which is a rule nothing today needs.
    /// </para>
    /// </remarks>
    private static string Where(
        AnalysisFilter filter, AliasMap map, SqlParameters parameters, params AnalysisMetric[] nonNull)
    {
        var terms = new List<string>
        {
            $"i.{SqlFragments.LightFrameOnly}",
            "i.capture_date IS NOT NULL",
        };

        foreach (var metric in nonNull)
        {
            terms.Add($"i.{AnalysisMetrics.Column(metric)} IS NOT NULL");
        }

        // Python's `if telescope:` is false for an empty string as well as for a None, so an
        // empty component applies nothing (lines 156 to 164).
        if (!string.IsNullOrEmpty(filter.Telescope))
        {
            terms.Add(InList("i.telescope", map.ExpandTelescope(filter.Telescope), parameters));
        }

        if (!string.IsNullOrEmpty(filter.Camera))
        {
            terms.Add(InList("i.camera", map.ExpandCamera(filter.Camera), parameters));
        }

        // An EXACT match with no alias folding at all, because line 165 is
        // `Image.filter_used == filter_used`. That is asymmetric with the box plot's own By
        // Filter grouping, which DOES fold; the asymmetry is the web's and spec 12.14 states it.
        if (!string.IsNullOrEmpty(filter.FilterUsed))
        {
            terms.Add($"i.filter_used = {parameters.Add(filter.FilterUsed)}");
        }

        // Both bounds are inclusive (lines 167 and 169) and both are on session_date, not
        // capture_date. Bound as the invariant yyyy-MM-dd string the column is stored in, the way
        // StatsQuery.CalendarSql binds its own two bounds.
        if (filter.From is { } from)
        {
            terms.Add($"i.session_date >= {parameters.Add(Bound(from))}");
        }

        if (filter.To is { } to)
        {
            terms.Add($"i.session_date <= {parameters.Add(Bound(to))}");
        }

        return string.Join(AndSeparator, terms);
    }

    private static string Bound(DateOnly date)
        => date.ToString(SqlReaders.DateFormat, CultureInfo.InvariantCulture);

    private static string InList(string column, IReadOnlyList<string> variants, SqlParameters parameters)
        => $"{column} IN ({string.Join(", ", variants.Select(variant => parameters.Add(variant)))})";

    // ---- the PHD2 night rollup, lines 101 to 141, ruling A10 ---------------------------

    // The ten columns of lines 126 to 140 that the port needs, minus the one it must never read.
    private sealed record Phd2Row(
        string? EquipmentProfile,
        DateOnly? SessionDate,
        int FrameCount,
        double? RmsTotal,
        double? RmsRa,
        double? RmsDec,
        int DropCount,
        double? SnrMean);

    /// <summary>One figure per (canonical rig, night) for one PHD2 metric, the port of
    /// <c>phd2_night_subquery</c>.</summary>
    /// <remarks>
    /// The web groups on the STORED <c>phd2_sessions.telescope</c> column folded through the alias
    /// map. <b>The port does not</b>, and this is measured rather than argued: that column is NULL
    /// on all 937 rows of the user's own library, because the port resolves the rig live and
    /// nothing ever writes the column back, so a verbatim port of lines 137 and 144 would return
    /// ZERO rows and all five PHD2 X metrics would render an empty chart on a library that has 50
    /// night rows. The column is therefore not projected at all, so no later reader can reach for
    /// it, exactly as <see cref="GuidingStatsQuery"/> does and for the reason recorded there.
    /// </remarks>
    private Dictionary<(DateOnly Night, string Rig), double?> Phd2Nights(
        GalactiLogContext context, AliasMap map, AnalysisMetric metric)
    {
        // Read and normalised ONCE per call, never per row: Normalize allocates a fresh
        // dictionary on every call and the corpus is a few thousand sessions. Live rather than
        // cached, so the answer follows the map as it stands now.
        var profiles = Phd2Profiles.Normalize(profileMap());

        var sessions = context.Phd2Sessions
            .Select(session => new Phd2Row(
                session.EquipmentProfile,
                session.SessionDate,
                session.FrameCount,
                session.RmsTotalArcsec,
                session.RmsRaArcsec,
                session.RmsDecArcsec,
                session.DropCount,
                session.SnrMean))
            .ToList();

        // The tuple key's default comparer is the ordinal string comparer, which is what the rig
        // name must be compared with: a culture-sensitive one would join two rigs whose names
        // differ only in an accent.
        var groups = new Dictionary<(DateOnly Night, string Rig), List<Phd2Row>>();
        foreach (var session in sessions)
        {
            // NOT the stored column. A session whose profile resolves to nothing is skipped,
            // which is the port's equivalent of line 137's `telescope IS NOT NULL`.
            if (Phd2Profiles.EffectiveTelescope(session.EquipmentProfile, profiles) is not { } effective)
            {
                continue;
            }

            // Line 138.
            if (session.SessionDate is not { } night)
            {
                continue;
            }

            var key = (night, map.CanonicalTelescope(effective) ?? effective);
            if (!groups.TryGetValue(key, out var rows))
            {
                rows = [];
                groups[key] = rows;
            }

            rows.Add(session);
        }

        var nights = new Dictionary<(DateOnly Night, string Rig), double?>(groups.Count);
        foreach (var (key, rows) in groups)
        {
            nights[key] = Phd2Figure(metric, rows);
        }

        return nights;
    }

    private static double? Phd2Figure(AnalysisMetric metric, List<Phd2Row> rows) => metric switch
    {
        // Ruling S1: the UNROUNDED core, never Phd2Metrics.WeightedRms. Lines 113 to 118 are SQL
        // with no round() anywhere and the figure is consumed unrounded by _compute_trend,
        // _pearson_r and _compute_summary_stats, so rounding it here moves the trend's slope on
        // the user's own library. The MinFrames gate and the null on a zero denominator both live
        // inside that member.
        AnalysisMetric.Phd2RmsTotal =>
            Phd2Metrics.WeightedRmsUnrounded(rows, row => row.RmsTotal, row => row.FrameCount),
        AnalysisMetric.Phd2RmsRa =>
            Phd2Metrics.WeightedRmsUnrounded(rows, row => row.RmsRa, row => row.FrameCount),
        AnalysisMetric.Phd2RmsDec =>
            Phd2Metrics.WeightedRmsUnrounded(rows, row => row.RmsDec, row => row.FrameCount),
        AnalysisMetric.Phd2StarLostPct => StarLostPct(rows),
        AnalysisMetric.Phd2SnrMean => SnrMean(rows),
        _ => null,
    };

    // Lines 128 to 131: over EVERY session of the night, gated by nothing, null when the
    // denominator is zero, which is the port of func.nullif(..., 0).
    //
    // Ruling S2: these two accumulate PLAINLY, with a += loop and not CompensatedSum. Every other
    // sum on this page is compensated and the difference will look like an oversight, so it is
    // said here: lines 128 to 135 are SQLAlchemy func.sum aggregates executed by PostgreSQL and
    // not Python sum() sites, so a plain accumulation matches what the database did. The
    // star-lost sums are over INTEGER columns and are therefore exact.
    private static double? StarLostPct(List<Phd2Row> rows)
    {
        var drops = 0L;
        var frames = 0L;
        foreach (var row in rows)
        {
            drops += row.DropCount;
            frames += row.FrameCount;
        }

        return frames == 0 ? null : 100.0 * drops / frames;
    }

    // Lines 132 to 135: over the sessions with a non-null snr_mean, gated by nothing, null when
    // the denominator is zero. See StarLostPct for why the accumulation is plain.
    private static double? SnrMean(List<Phd2Row> rows)
    {
        var numerator = 0d;
        var denominator = 0L;
        foreach (var row in rows)
        {
            if (row.SnrMean is not { } snr)
            {
                continue;
            }

            numerator += snr * row.FrameCount;
            denominator += row.FrameCount;
        }

        return denominator == 0 ? null : numerator / denominator;
    }

    // ---- grouping helpers ---------------------------------------------------------------

    // Lines 419 to 436, ruling A17: the key is (session_date, resolved_target_id) and there is NO
    // RIG in it. A null resolved_target_id is a legal key and collects all the night's unresolved
    // frames into one group.
    //
    // The index into a list, rather than a bare Dictionary enumeration: Python dicts preserve
    // insertion order and the group sequence follows the row sequence, while a .NET Dictionary's
    // enumeration order is unspecified and has changed between runs after a removal. Relying on
    // it is the kind of thing that passes for a year and then reorders a chart.
    private static List<(double X, double Y, DateOnly Night, Guid? TargetId)> SessionPoints(
        List<(double X, double Y, DateOnly Night, Guid? TargetId)> rows)
    {
        var index = new Dictionary<(DateOnly, Guid?), int>();
        var groups = new List<(List<double> Xs, List<double> Ys, DateOnly Night, Guid? TargetId)>();

        foreach (var row in rows)
        {
            var key = (row.Night, row.TargetId);
            if (!index.TryGetValue(key, out var at))
            {
                at = groups.Count;
                index[key] = at;
                groups.Add(([], [], row.Night, row.TargetId));
            }

            groups[at].Xs.Add(row.X);
            groups[at].Ys.Add(row.Y);
        }

        // Statistics.Median is the one median in this solution (ruling A5) and averages the two
        // middle values on an even count, as statistics.median does. Unwrapped with !.Value and
        // never with ?? 0: a group that exists holds at least one value, so null is impossible,
        // and a zero on a chart would hide a caller bug.
        return groups.ConvertAll(group => (
            Statistics.Median(group.Xs)!.Value,
            Statistics.Median(group.Ys)!.Value,
            group.Night,
            group.TargetId));
    }

    // Lines 520 to 526, the same key and the same insertion order, one value per group.
    private static List<double> SessionMedians(List<(double Value, DateOnly Night, Guid? TargetId)> rows)
    {
        var index = new Dictionary<(DateOnly, Guid?), int>();
        var groups = new List<List<double>>();

        foreach (var row in rows)
        {
            var key = (row.Night, row.TargetId);
            if (!index.TryGetValue(key, out var at))
            {
                at = groups.Count;
                index[key] = at;
                groups.Add([]);
            }

            groups[at].Add(row.Value);
        }

        return groups.ConvertAll(values => Statistics.Median(values)!.Value);
    }

    // Lines 613 to 646. Null drops the row, which is what each branch's `continue` does.
    private static string? GroupKey(
        AliasMap map, BoxPlotGrouping groupBy, SqliteDataReader reader, Dictionary<string, Guid> targetIds)
    {
        switch (groupBy)
        {
            case BoxPlotGrouping.Equipment:
            {
                var telescope = map.CanonicalTelescope(SqlReaders.ReadText(reader, 3))
                    ?? SqlReaders.ReadText(reader, 3);
                var camera = map.CanonicalCamera(SqlReaders.ReadText(reader, 4))
                    ?? SqlReaders.ReadText(reader, 4);

                // The separator is " + ", space plus space, exactly (line 621).
                return string.IsNullOrEmpty(telescope) || string.IsNullOrEmpty(camera)
                    ? null
                    : $"{telescope} + {camera}";
            }

            case BoxPlotGrouping.Filter:
            {
                var name = map.CanonicalFilter(SqlReaders.ReadText(reader, 5))
                    ?? SqlReaders.ReadText(reader, 5);
                return string.IsNullOrEmpty(name) ? null : name;
            }

            case BoxPlotGrouping.Month:
            {
                var month = SqlReaders.ReadText(reader, 6);
                return string.IsNullOrEmpty(month) ? null : month;
            }

            default:
            {
                if (reader.IsDBNull(2))
                {
                    return null;
                }

                var targetId = reader.GetGuid(2);
                var key = targetId.ToString();
                targetIds[key] = targetId;
                return key;
            }
        }
    }

    // Lines 638 to 646: the target grouping merges by NAME, not by id, so two target ids that
    // resolve to one primary name become one group whose values are concatenated. The
    // concatenation order follows the group insertion order.
    private static List<(string Name, List<double> Values)> MergeByTargetName(
        SqliteConnection connection,
        List<(string Name, List<double> Values)> groups,
        Dictionary<string, Guid> targetIds)
    {
        var names = TargetNames(connection, [.. targetIds.Values.Distinct()]);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var merged = new List<(string Name, List<double> Values)>();

        foreach (var (key, values) in groups)
        {
            var name = targetIds.TryGetValue(key, out var targetId) && names.TryGetValue(targetId, out var resolved)
                ? resolved
                : key;
            Bucket(index, merged, name).AddRange(values);
        }

        return merged;
    }

    private static List<double> Bucket(
        Dictionary<string, int> index, List<(string Name, List<double> Values)> groups, string key)
    {
        if (!index.TryGetValue(key, out var at))
        {
            at = groups.Count;
            index[key] = at;
            groups.Add((key, []));
        }

        return groups[at].Values;
    }

    // Lines 723 to 736: one entry per position where the window is full, each carrying the index
    // into the values so the date comes off the point at that index.
    private static IReadOnlyList<MovingAveragePoint> MovingAverage(
        List<TimeSeriesPoint> points, List<double> values, int window)
        => [.. Analysis.MovingAverage(values, window)
            .Select(entry => new MovingAveragePoint(points[entry.Index].Date, entry.Value))];

    // ---- target names ------------------------------------------------------------------

    private static IReadOnlyDictionary<Guid, string> TargetNames(
        SqliteConnection connection, List<CorrelationPoint> points)
        => TargetNames(
            connection,
            [.. points.Where(point => point.TargetId is not null)
                .Select(point => point.TargetId!.Value)
                .Distinct()]);

    /// <summary>Port of <c>_resolve_target_names</c> (lines 173 to 180): ONE statement with bound
    /// ids, never one per id, and an empty id set issues no statement at all (line 177).</summary>
    /// <remarks>
    /// ponytail: the bound id list grows with the distinct target count of the point set.
    /// Ceiling: <c>SQLITE_MAX_VARIABLE_NUMBER</c>, 32,766, which a point set touching thirty
    /// thousand distinct targets cannot plausibly reach in the corpus this application describes.
    /// Upgrade path if a real library ever gets near it: chunk the read.
    /// </remarks>
    private static IReadOnlyDictionary<Guid, string> TargetNames(
        SqliteConnection connection, List<Guid> ids)
    {
        var names = new Dictionary<Guid, string>();
        if (ids.Count == 0)
        {
            return names;
        }

        var parameters = new SqlParameters();
        var bound = string.Join(", ", ids.Select(id => parameters.Add(id)));

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT t.id, t.primary_name
            FROM targets t
            WHERE t.id IN ({bound});
            """;
        parameters.ApplyTo(command);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names[reader.GetGuid(0)] = reader.GetString(1);
        }

        return names;
    }

    // ---- compare ------------------------------------------------------------------------

    private static (List<double> Values, List<double> Arcsec) CompareValues(
        SqliteConnection connection,
        AliasMap map,
        AnalysisMetric metric,
        CompareMode mode,
        string group,
        DateOnly? from,
        DateOnly? to)
    {
        // Fail closed: a malformed Equipment-mode group reads no row rather than the whole
        // library. No statement is issued for it at all, which is also the cheaper answer.
        if (CompareScope(mode, group, from, to) is not { } scope)
        {
            return ([], []);
        }

        var parameters = new SqlParameters();
        var where = Where(scope, map, parameters, metric);

        if (mode == CompareMode.Filter)
        {
            // Line 868 is unconditional, unlike the filter bar's own filter_used at line 164,
            // which Python truthiness skips for an empty string. A group named "" therefore still
            // compares for equality here, which is why this predicate is added rather than
            // carried on the AnalysisFilter above.
            where += $"{AndSeparator}i.filter_used = {parameters.Add(group)}";
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT i.{AnalysisMetrics.Column(metric)}, i.arcsec_per_pixel, i.session_date
            FROM images i
            WHERE {where}
            {RowOrder};
            """;
        parameters.ApplyTo(command);

        var values = new List<double>();
        var arcsec = new List<double>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (SqlReaders.ReadDate(reader, 2) is null)
            {
                continue;
            }

            var value = SqlReaders.ReadDouble(reader, 0);
            values.Add(value);

            // Lines 872 to 876: the arcsecond list holds value * scale for the rows whose scale is
            // non-null, and only for the one pixel metric.
            if (metric == AnalysisMetric.Hfr && SqlReaders.ReadNullableDouble(reader, 1) is { } scale)
            {
                arcsec.Add(value * scale);
            }
        }

        return (values, arcsec);
    }

    /// <summary>
    /// Lines 858 to 868. In equipment mode the group is the two canonical names joined by
    /// <see cref="CompareGroups.EquipmentSeparator"/>; an empty half applies nothing for its own
    /// column, which is the web's behaviour and is kept.
    /// </summary>
    /// <returns>The scope, or <b>null</b> for an Equipment-mode group string that does not split
    /// into exactly two parts, which is the one DEPARTURE from line 860 (task 6 review, escalation
    /// 4).</returns>
    /// <remarks>
    /// The web falls through to a filter with NO equipment predicate there, so both groups scope
    /// the whole library, read the same rows and the tab prints
    /// <c>Analysis.CompareVerdict</c>'s "Both groups have identical median values", with no empty
    /// state, no exception and no log line: a confident, plausible, wrong answer. Two inputs reach
    /// it. A separator that drifts between the App's picker and this method, which
    /// <see cref="CompareGroups.EquipmentSeparator"/> now makes impossible, and a canonical
    /// telescope or camera name that itself contains the separator, which is a header value the
    /// user's own files supplied and is therefore a trust boundary. This fails closed instead: the
    /// caller reads no row for that group, so the tab states which group is short, or that no row
    /// matched at all when both are malformed.
    /// </remarks>
    private static AnalysisFilter? CompareScope(CompareMode mode, string group, DateOnly? from, DateOnly? to)
    {
        if (mode != CompareMode.Equipment)
        {
            return new AnalysisFilter(null, null, null, AnalysisGranularity.Frame, from, to);
        }

        var parts = group.Split(CompareGroups.EquipmentSeparator);
        return parts.Length == 2
            ? new AnalysisFilter(parts[0], parts[1], null, AnalysisGranularity.Frame, from, to)
            : null;
    }

    // ---- small shared rules --------------------------------------------------------------

    // User ruling U3: the count of DISTINCT NON-NULL plate scales among the frames the query read.
    // A null contributes nothing and is not an extra value, so a library where half the frames
    // carry no scale and the rest share one counts 1 and shows no warning. Raw doubles, not
    // rounded: the two real rigs read 0.989... and 2.937..., which are far apart, and rounding
    // first would be a threshold nobody chose.
    private static void AddScale(HashSet<double> scales, SqliteDataReader reader, int ordinal)
    {
        if (SqlReaders.ReadNullableDouble(reader, ordinal) is { } scale)
        {
            scales.Add(scale);
        }
    }

    // The web raises HTTP 400 for an unknown metric (lines 499, 579, 671 and 832). The port has no
    // HTTP layer and the argument is an enum, so the refusal is an exception at the top of the
    // method, and the only metrics that can reach one are the PHD2 five, which have no images
    // column and belong to the Correlation X picker alone.
    private static void RequireColumnMetric(AnalysisMetric metric, string parameterName)
    {
        if (!Enum.IsDefined(metric) || AnalysisMetrics.Column(metric).Length == 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName, metric, "Not a metric this tab reads.");
        }
    }
}
