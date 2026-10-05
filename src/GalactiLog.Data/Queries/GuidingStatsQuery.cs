using System.Text.Json;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Phd2;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>
/// Spec 12.5's Guiding section in one read, port of
/// <c>backend/app/services/phd2_stats.py</c> behind <c>backend/app/api/stats_guiding.py</c>: per
/// canonical rig the session counts, guided hours, the four frame-count weighted RMS figures, the
/// Dec-to-RA ratio, the median settle and the guide exposures used; per rig and altitude band the
/// same weighted RMS with its session count; the corpus-wide count of sessions whose profile is
/// mapped to nothing; and the cross-rig baselines the scorecard grades its cells against.
/// </summary>
/// <remarks>
/// <para>
/// One statement, eleven columns of <c>phd2_sessions</c> and no others, then a C# pass to group
/// (<c>phd2_stats.py:72-90</c>). The web source's own reason carries over unchanged: the sessions
/// table is a few hundred to a few thousand rows, and every figure here is a frame-count weighted
/// RMS or a plain sum, which is cheaper to write once in C# than three ways in SQL. SQLite has no
/// <c>percentile_cont</c> and cannot fold an alias map without interpolating user text into a
/// statement, which the query rules forbid outright.
/// </para>
/// <para>
/// <b>This query never touches the frames table.</b> Every figure here is already an aggregate on
/// the session row, and the frames table is 1.2 million rows on the user's own corpus, so a read
/// that reached for a frame would be correct and would cost a full scan on every Statistics load.
/// </para>
/// <para>
/// Read-only and library-wide: it takes no rig filter because the web has none
/// (<c>stats_guiding.py:19</c>), which is also why <see cref="StatsQuery"/> takes no group key.
/// </para>
/// <para>
/// Two web modules that look like omissions next to this one, recorded so a reviewer does not
/// rediscover them. <c>phd2_stats.py</c> itself needs no Core port: it is a database aggregation
/// with one pure three-branch helper, <c>altitude_band</c>, whose rules live here the way
/// <see cref="StatsQuery"/> holds its own bucket boundaries, and its one genuinely shared piece,
/// the frame-count weighted RMS, is already in Core as <c>Phd2Metrics.WeightedRms</c>. And
/// <c>phd2_sidereal.py</c> is not reached at all: it is a sidereal cross-check that emits a
/// warning verdict when a log's pointing disagrees with the configured longitude, it may never
/// change a stored value by its own docstring, and nothing on this page reads its output, because
/// <c>alt_deg</c> is parsed from the log's own pointing line and stored (ruling G3). Spec 19.1
/// leaves it deferred.
/// </para>
/// </remarks>
/// <param name="profileMap">The raw <c>general.phd2_profile_map</c>, normally
/// <c>() =&gt; settingsStore.GetGeneral().Phd2ProfileMap</c>, read once per <see cref="Get"/> and
/// never per row. It is not optional and has no default: the stored <c>telescope</c> column is the
/// map's answer as it stood at ingest and nothing ever writes a newer one back, so a reader that
/// skipped this argument would show a user who has just mapped a profile the state from before the
/// mapping, forever (task5b-review.md P2-1).</param>
public sealed class GuidingStatsQuery(
    DatabaseConnectionString connectionString,
    AliasMapCache aliases,
    Func<JsonElement?> profileMap)
{
    /// <summary>
    /// The Guiding section's whole answer. Never null, and never throws on a bare database: an
    /// empty library returns <c>UnmappedSessionCount</c> 0, empty <c>Rigs</c> and
    /// <c>AltitudeBands</c>, and three empty baselines.
    /// </summary>
    public GuidingStats Get()
    {
        var map = aliases.Current;

        // Read and normalised ONCE per call, never per row: Normalize allocates a fresh dictionary
        // on every call and the corpus is a few thousand sessions. Live rather than cached, which
        // is the whole point of the fix: the answer must follow the map as it stands now.
        var profiles = Phd2Profiles.Normalize(profileMap());

        // One short-lived context per call through GalactiLogContextOptions.Create, the shape
        // SessionDetailQuery uses, so PragmaConnectionInterceptor stays in the path.
        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));

        var sessions = context.Phd2Sessions
            // phd2_sessions.telescope is not projected at all. Nothing here may read it: the rig
            // is the live map's answer and only the live map's answer (phase-review.md F3), and a
            // column absent from the row cannot be reached for by a later reader.
            .Select(session => new Row(
                session.EquipmentProfile,
                session.DurationS,
                session.FrameCount,
                session.SettleMedianS,
                session.RmsRaArcsec,
                session.RmsDecArcsec,
                session.RmsTotalArcsec,
                session.RmsTotalFilteredArcsec,
                session.ExposureMs,
                session.AltDeg))
            .ToList();

        var byRig = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
        var byBand = new Dictionary<(string Telescope, GuidingAltitudeBand Band), List<Row>>();
        var unmapped = 0;

        foreach (var row in sessions)
        {
            // NOT the stored column. The one implementation of "which rig is this session really",
            // shared with Phd2NightQuery and the correlation pass, resolved against the map as it
            // stands now: a profile mapped after the scan never reaches phd2_sessions.telescope,
            // because the only writer is the ingest and the guide-log pass skips an unchanged log
            // (task5b-review.md P2-1). Both the rig grouping and the unmapped count follow it, so
            // the "Map profiles" link on the empty notice leads to an action that fills the page.
            // The map is the SOLE authority (phase-review.md F3): a profile it no longer carries
            // joins the unmapped tally rather than keeping the rig the reader just removed.
            var telescope = Phd2Profiles.EffectiveTelescope(row.EquipmentProfile, profiles);

            // Null only, deliberately, and not IsNullOrEmpty: Phd2Session.Telescope's own summary
            // says null means the profile is unmapped, and the ingest writes nothing else for one
            // (phd2_stats.py:83-85). A map entry carrying a null telescope is the same answer.
            if (telescope is null)
            {
                unmapped++;
                continue;
            }

            // The port of normalize_equipment(r.telescope, tel_map): two spellings of one scope
            // land in one row and the raw spelling never reaches the output.
            var rig = map.CanonicalTelescope(telescope) ?? telescope;
            Bucket(byRig, rig).Add(row);

            if (BandOf(row.AltDeg) is { } band)
            {
                Bucket(byBand, (rig, band)).Add(row);
            }
        }

        var rigs = byRig
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => Rig(entry.Key, entry.Value))
            .ToList();

        var bands = byBand
            .OrderBy(entry => entry.Key.Telescope, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.Band)
            .Select(entry => new GuidingAltitudeBandRow(
                entry.Key.Telescope,
                entry.Key.Band,
                entry.Value.Count,
                WeightedRms(entry.Value, row => row.RmsTotalArcsec),
                WeightedRms(entry.Value, row => row.RmsRaArcsec),
                WeightedRms(entry.Value, row => row.RmsDecArcsec)))
            .ToList();

        return new GuidingStats(unmapped, rigs, bands, Baselines(rigs));
    }

    // The ten columns of phd2_stats.py:72-76 plus equipment_profile, which the web does not need
    // because it resolves the rig in SQL against the live map and this port resolves it here. A
    // private record rather than an anonymous type only so the helpers below can name it.
    private sealed record Row(
        string? EquipmentProfile,
        double DurationS,
        int FrameCount,
        double? SettleMedianS,
        double? RmsRaArcsec,
        double? RmsDecArcsec,
        double? RmsTotalArcsec,
        double? RmsTotalFilteredArcsec,
        double? ExposureMs,
        double? AltDeg);

    // Port of phd2_stats.altitude_band (phd2_stats.py:31-38). Both boundaries are exact: 30.0 is
    // From30To60 and 60.0 is Above60. A null altitude is in no band at all, which is the ASIAIR
    // shape: its header carries no Alt field, and bucketing it as 0 would claim the rig guided
    // below the horizon.
    private static GuidingAltitudeBand? BandOf(double? altDeg) => altDeg switch
    {
        null => null,
        < 30 => GuidingAltitudeBand.Below30,
        < 60 => GuidingAltitudeBand.From30To60,
        _ => GuidingAltitudeBand.Above60,
    };

    // Port of phd2_stats._rig (phd2_stats.py:49-65), field by field.
    private static GuidingRig Rig(string telescope, List<Row> rows)
    {
        var ra = WeightedRms(rows, row => row.RmsRaArcsec);
        var dec = WeightedRms(rows, row => row.RmsDecArcsec);

        return new GuidingRig(
            telescope,
            rows.Count,
            rows.Count(row => row.FrameCount < Phd2Metrics.MinFrames),
            // CompensatedSum, not Sum: phd2_stats.py:57 is a bare sum() over the float duration
            // column, and CPython 3.12, which the backend requires, gives that builtin Neumaier
            // compensation. Enumerable.Sum accumulates left to right, which is a different double
            // on a rig with hundreds of sessions.
            PythonNumerics.RoundLikePython(
                PythonNumerics.CompensatedSum(rows.Select(row => row.DurationS)) / 3600, 2),
            WeightedRms(rows, row => row.RmsTotalArcsec),
            ra,
            dec,
            WeightedRms(rows, row => row.RmsTotalFilteredArcsec),
            // phd2_stats.py:60 is "dec / ra if ra and dec is not None else None", and Python's
            // "if ra" is false for a zero as well as for a null. A null check alone would divide
            // by zero here and put an infinity on the scorecard instead of the missing glyph.
            ra is { } raValue && raValue != 0 && dec is { } decValue ? decValue / raValue : null,
            // Deliberately unrounded: phd2_stats.py:61 calls statistics.median bare, while the
            // night rollup rounds its own median to 3 (phd2_metrics.py:545). Two different
            // figures, and the port keeps the difference. Statistics.Median is the house member
            // and averages the two middle values on an even count, as statistics.median does.
            Statistics.Median(rows.Select(row => row.SettleMedianS)),
            // sorted({int(round(r.exposure_ms)) ...}) (phd2_stats.py:62-64). One-argument Python
            // round is half-to-even on the exact binary value, which is RoundLikePython(x, 0); a
            // bare cast would truncate and read 499 for 499.6.
            [.. rows
                .Where(row => row.ExposureMs is not null)
                .Select(row => (int)PythonNumerics.RoundLikePython(row.ExposureMs!.Value, 0))
                .Distinct()
                .Order()]);
    }

    // Every RMS figure on this page goes through the one Core implementation, the same one the
    // session card's night rollup uses, for the reason phd2_stats.py's own docstring gives: the
    // Statistics page and a session card must never disagree about what a night's RMS was. The
    // gate of spec 5.16 and the six-decimal rounding both live inside it.
    private static double? WeightedRms(List<Row> rows, Func<Row, double?> value)
        => Phd2Metrics.WeightedRms(rows, value, row => row.FrameCount);

    // Port of buildBaselines (GuidingScorecard.tsx:62-86), moved into the query by ruling Q1. The
    // builder itself is MetricBaseline.Of, the one in the solution: N counts the non-null values of
    // this one metric and never the number of rigs, so a library of nine rigs where two carry no
    // RMS grades against seven values or not at all.
    private static GuidingBaselines Baselines(List<GuidingRig> rigs)
        => new(
            MetricBaseline.Of(rigs.Select(rig => rig.RmsTotalArcsec)),
            MetricBaseline.Of(rigs.Select(rig => rig.RmsRaArcsec)),
            MetricBaseline.Of(rigs.Select(rig => rig.RmsDecArcsec)));

    private static List<Row> Bucket<TKey>(Dictionary<TKey, List<Row>> buckets, TKey key)
        where TKey : notnull
    {
        if (!buckets.TryGetValue(key, out var rows))
        {
            rows = [];
            buckets[key] = rows;
        }

        return rows;
    }
}
