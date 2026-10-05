using GalactiLog.Core.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>The library-wide baselines and the eccentricity source they pooled.</summary>
/// <param name="Groups">Per (telescope, camera, filter) group, per metric, keyed by
/// <see cref="FrameQuality.GroupKey"/> over canonical names.</param>
/// <param name="EccentricitySource">The library's modal <c>eccentricity_source</c>, the only one
/// the eccentricity baseline pooled (spec 7.2). A caller comparing a session median against these
/// baselines must check that its own pooled source matches, because two sources are not
/// comparable and a cross-source z-score is a wrong number, not an approximate one.</param>
public sealed record RigBaselines(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, MetricBaseline>> Groups,
    string? EccentricitySource);

/// <summary>
/// The library-wide (telescope, camera, filter) baselines the session insights grade against.
/// Deliberately shaped exactly like <see cref="AliasMapCache"/>: a lazily built value behind a
/// lock, a TTL backstop, and an explicit <see cref="Invalidate"/> the App layer calls when a scan
/// finishes. Copying that shape rather than extracting a generic TTL cache for two call sites is
/// the smaller change; see questions.md Q7.
/// </summary>
/// <remarks>
/// <para>
/// Two honest caveats, both carried from the web application's own notes and both left unfixed on
/// purpose. First, the rig baseline includes the frames of the session being viewed, so a session
/// drags the baseline slightly towards itself and shrinks its own z-score. That is conservative:
/// it can only hide a warning, never invent one, and it shrinks as the library grows. Second, the
/// whole-night check compares a session median against a per-frame MAD, and the sampling
/// distribution of a median is narrower than that of a single frame, which makes
/// <see cref="FrameQuality.ZReject"/> a conservative bar for that comparison too. Do not try to
/// fix either: both make the insight quieter, and a quiet insight beats a wrong one.
/// </para>
/// <para>
/// Raw equipment and filter strings are folded through <see cref="AliasMapCache"/> before the
/// group key is built, because the session baselines fold the same way and the two dictionaries
/// have to key identically or every whole-night check misses.
/// </para>
/// </remarks>
public sealed class RigBaselinesCache(
    DatabaseConnectionString connectionString,
    AliasMapCache aliases,
    Func<DateTime>? utcNow = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private readonly Lock _lock = new();

    private RigBaselines? _cached;
    private DateTime _builtAtUtc;

    /// <summary>Returns the current baselines, rebuilding them if they were invalidated or the
    /// TTL elapsed. Safe to call from any thread. One record rather than two properties so a
    /// caller can never read a dictionary and a source that came from different builds.</summary>
    public RigBaselines Current
    {
        get
        {
            lock (_lock)
            {
                if (_cached is null || _utcNow() - _builtAtUtc >= Ttl)
                {
                    _cached = Load();
                    _builtAtUtc = _utcNow();
                }

                return _cached;
            }
        }
    }

    /// <summary>Drops the cached baselines so the next read rebuilds. The App layer calls this
    /// from <c>ScanStatusService.ScanFinished</c>, never from <c>ScanCoordinator</c>.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _cached = null;
        }
    }

    // ponytail: a full scan of the images table over ten columns, once per five minutes or once
    // per scan, holding one small record per LIGHT frame in memory while the baselines are built.
    // Ceiling: a 200 000 frame library is a few tens of megabytes for the duration of one Load.
    // Upgrade path if it ever matters: a materialized per-group aggregate table maintained by the
    // scan writer, invalidated the same way. Do not build that now.
    private RigBaselines Load()
    {
        var map = aliases.Current;

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT i.telescope, i.camera, i.filter_used, i.median_hfr, i.fwhm, i.eccentricity,
                   i.eccentricity_source, i.detected_stars, i.adu_median, i.guiding_rms_arcsec
            FROM images i
            WHERE i.{SqlFragments.LightFrameOnly};
            """;

        var rows = new List<(string? Telescope, string? Camera, string? Filter, double? Hfr, double? Fwhm,
            double? Eccentricity, string? EccentricitySource, double? Stars, double? AduMedian, double? Guiding)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                map.CanonicalTelescope(SqlReaders.ReadText(reader, 0)),
                map.CanonicalCamera(SqlReaders.ReadText(reader, 1)),
                map.CanonicalFilter(SqlReaders.ReadText(reader, 2)),
                SqlReaders.ReadNullableDouble(reader, 3),
                SqlReaders.ReadNullableDouble(reader, 4),
                SqlReaders.ReadNullableDouble(reader, 5),
                SqlReaders.ReadText(reader, 6),
                SqlReaders.ReadNullableDouble(reader, 7),
                SqlReaders.ReadNullableDouble(reader, 8),
                SqlReaders.ReadNullableDouble(reader, 9)));
        }

        // Spec 7.2 applies to the library baseline exactly as it applies to a session aggregate:
        // an eccentricity baseline pooled over three incomparable sources describes none of them.
        // A frame outside the modal source contributes its other metrics and a null eccentricity,
        // so it still counts towards the HFR and guiding baselines it is comparable within.
        EccentricitySources.TryGetModalSource(
            rows.Select(row => (row.EccentricitySource, row.Eccentricity)),
            out var modalSource);

        var frames = rows.ConvertAll(row => new GradedFrame(
            row.Telescope,
            row.Camera,
            row.Filter,
            row.Hfr,
            row.Fwhm,
            EccentricitySources.IsPooled(row.EccentricitySource, row.Eccentricity, modalSource)
                ? row.Eccentricity
                : null,
            row.Stars,
            row.AduMedian,
            row.Guiding));

        return new RigBaselines(FrameQuality.GroupBaselines(frames), modalSource);
    }
}
