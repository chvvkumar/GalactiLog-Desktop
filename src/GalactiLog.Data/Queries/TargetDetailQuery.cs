using System.Text.Json;
using GalactiLog.Core.Aliases;
using GalactiLog.Core.Metrics;
using GalactiLog.Core.Phd2;
using GalactiLog.Core.Targets;
using GalactiLog.Data.Ingest;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GalactiLog.Data.Queries;

/// <summary>
/// The read behind the top of Target detail (spec 12.4), port of
/// <c>target_detail.get_target_detail</c>: the header block, the totals row and the session
/// overview list the ledger renders one night row per (P12 R10 retired the session accordion).
/// </summary>
/// <remarks>
/// <para>
/// Four statements in one round trip, read through <c>NextResult</c>, over a short-lived
/// <see cref="GalactiLogContext"/> so <see cref="PragmaConnectionInterceptor"/> stays in the path
/// and the busy timeout of spec 5.1 applies to a read taken while a scan writes. The group is
/// named by <see cref="SqlFragments.GroupScope"/>, the same fragment the dashboard listing pins
/// with, so a detail page can never describe a group the dashboard does not produce.
/// </para>
/// <para>
/// Spec 7.1.1: <c>median_fwhm</c> appears in none of these statements. <c>fwhm</c> is the only
/// FWHM any consumer reads, and the two are never averaged together.
/// </para>
/// </remarks>
/// <param name="profileMap">The raw <c>general.phd2_profile_map</c>, normally
/// <c>() =&gt; settingsStore.GetGeneral().Phd2ProfileMap</c>, read once per <see cref="Get"/> and
/// never per night. It is what resolves each guiding session's rig for
/// <see cref="SessionOverview.GuidingSessionCount"/>, through the one rule
/// <see cref="Phd2Profiles.EffectiveTelescope"/> that <c>Phd2NightQuery</c> uses, so the closed
/// band's count and the opened band's list cannot disagree.
/// <para>
/// Required, with no default, for the reason <c>Phd2NightQuery</c> gives about its own: a
/// defaulted argument would let a construction site added later resolve every session against an
/// empty map, which under <see cref="Phd2Profiles.EffectiveTelescope"/>'s live-map rule leaves
/// every session unmapped and makes the count fall through to spec 7.6's sole-unmapped-profile
/// branch instead of naming this card's rig. A wrong count with no diagnostic is the fail-open
/// shape design lesson 2 names, so the compiler is made to ask instead.
/// </para></param>
public sealed class TargetDetailQuery(
    DatabaseConnectionString connectionString,
    AliasMapCache aliases,
    Func<JsonElement?> profileMap)
{
    /// <summary>Null when the group holds no LIGHT frame, when the key names a targets row that
    /// does not exist, or when that row is merged away (<c>merged_into_id</c> is not null). The
    /// caller renders an error state; see questions.md Q4.</summary>
    /// <param name="groupKey">A <c>TargetRow.GroupKey</c> straight from the dashboard row.</param>
    public TargetDetail? Get(string groupKey)
    {
        var map = aliases.Current;

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        var read = Read(connection, groupKey);
        if (read.Frames.Count == 0)
        {
            return null;
        }

        // Ruling Q4: a stale key whose targets row was merged away or deleted is an error state,
        // not a page. Following merged_into_id to the winner needs Phase 7's merge machinery and
        // would hide from the user that a merge happened.
        if (read.TargetId is not null && (read.Target is null || read.Target.MergedIntoId is not null))
        {
            return null;
        }

        // Review ruling: the header reports the key in its storage form, taken from the rows the
        // group actually matched, so a caller that passed an upper-cased GUID gets the same
        // string the dashboard row carries.
        var nights = Nights(read.Frames);
        var totals = BuildTotals(read.Frames, map);
        var keys = totals.IntegrationSecondsByFilter.Keys.ToDictionary(key => key, StringComparer.OrdinalIgnoreCase);
        return new TargetDetail(
            BuildHeader(read.Frames[0].StorageGroupKey, read),
            totals,
            BuildSessions(nights, read.NoteDates, map, GuidingSessions(context, read.Frames)),
            [.. read.Frames.Select(frame => frame.FilePath)])
        {
            NightFilters = BuildNightFilters(nights, map, keys),
            NightFrames = BuildNightFrames(read.Frames, nights, map, keys),
        };
    }

    /// <summary>The surviving target a merged-away group key was merged into, with the
    /// merged-away target's own name, or null when the key named something else (a pruned group, a
    /// key that never existed, an <c>obj:</c> group). Called only after <see cref="Get"/> returned
    /// null, so the page can name the merge instead of guessing (FIXER LIST item 14, ruling Q12).
    /// <c>LoserName</c> is Phase 7 FIXER item 18: <see cref="Get"/> returns null for a merged-away
    /// key, so the page has no header and rendered an empty title until it had this.</summary>
    /// <remarks>
    /// Spec 12.10's rule is unchanged: <see cref="Get"/> still returns null for a merged-away
    /// target and the page is never silently redirected to the winner. This only lets the callout
    /// say what happened and offer the winner as a link the user chooses to follow.
    /// </remarks>
    /// <param name="groupKey">A <c>TargetRow.GroupKey</c> straight from the dashboard row.</param>
    public (Guid TargetId, string PrimaryName, string LoserName)? MergedInto(string groupKey)
    {
        if (!Guid.TryParse(groupKey, out var targetId))
        {
            return null;
        }

        using var context = new GalactiLogContext(GalactiLogContextOptions.Create(connectionString.Value));
        context.Database.OpenConnection();
        var connection = (SqliteConnection)context.Database.GetDbConnection();

        var parameters = new SqlParameters();
        var idParam = parameters.Add(targetId);

        using var command = connection.CreateCommand();

        // The inner join is the whole rule: no row comes back for an unknown id, nor for a target
        // whose merged_into_id is null, nor for one pointing at a winner that is gone.
        command.CommandText =
            $"""
            SELECT w.id, w.primary_name, t.primary_name
            FROM targets t
            JOIN targets w ON w.id = t.merged_into_id
            WHERE t.id = {idParam};
            """;
        parameters.ApplyTo(command);

        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetGuid(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    // ---- the round trip ----------------------------------------------------------------
    //
    // ponytail: one row per LIGHT frame of a single group is held in memory, because SQLite has
    // no median() and target_detail.py computes every figure over the frame list. Ceiling: a
    // target with 20 000 frames is 20 000 small records, a few megabytes. Upgrade path if it ever
    // matters: push the means and the counts into SQL and keep only the medians in memory, or
    // materialize per-session aggregates at ingest. Do not build that now.
    private static ReadResult Read(SqliteConnection connection, string groupKey)
    {
        var parameters = new SqlParameters();
        var scope = SqlFragments.GroupScope(groupKey, parameters.Add);
        var isTarget = Guid.TryParse(groupKey, out var targetId);

        using var command = connection.CreateCommand();

        // Statement 1 selects capture_date so the ordering it drives is visible beside the
        // columns it orders; the aggregates read the row order, never the value. The targets join
        // carries the t alias SqlFragments documents for the callers that need it. The trailing
        // group-key column is the storage form of the key (review ruling): it is what the header
        // block reports, so a caller that passes an upper-cased GUID gets back the same string
        // the dashboard row carries rather than its own.
        var text =
            $"""
            SELECT i.session_date, i.capture_date, i.exposure_time, i.filter_used, i.telescope, i.camera,
                   i.median_hfr, i.arcsec_per_pixel, i.eccentricity, i.eccentricity_source,
                   i.fwhm, i.guiding_rms_arcsec, i.detected_stars, i.guiding_rms_source, i.file_path,
                   {SqlFragments.GroupKeyExpression}
            FROM images i
            LEFT JOIN targets t ON t.id = i.resolved_target_id
            WHERE i.{SqlFragments.LightFrameOnly}
              AND {scope}
            ORDER BY i.capture_date;
            """;

        if (isTarget)
        {
            // Statements 2 to 4 exist only for a resolved key: an obj: group has no targets row,
            // no memberships and no session notes to key on.
            //
            // P12 ruling Q6: the reference frame's plate scale and width ride on statement 2
            // rather than a fifth round trip. One subquery for both figures, not two (P12 review
            // P2-3): the scale bar's premise is that the two describe the same frame, and two
            // independent LIMIT 1 subselects are free to pick two different rows whenever the
            // newest capture_date is tied. Reading both out of one row makes that structural, and
            // the id tiebreaker makes which row it is deterministic. The subquery reuses the one
            // scope fragment above and its bound parameters, so SqlFragments.GroupScope stays the
            // only place a group is named. json_extract has no index (spec 5.2, 12.2) and needs
            // none: the subquery reads one row.
            var idParam = parameters.Add(targetId);
            text +=
                $"""

                SELECT id, primary_name, aliases, ra, dec, object_type, constellation,
                       size_major, size_minor, position_angle, v_mag, surface_brightness,
                       sac_description, sac_notes, notes, reference_thumbnail_path, merged_into_id,
                       name_locked, user_defined,
                       r.reference_arcsec_per_pixel,
                       r.reference_frame_width
                FROM targets
                LEFT JOIN (SELECT i.arcsec_per_pixel AS reference_arcsec_per_pixel,
                                  CAST(json_extract(i.raw_headers, '$.NAXIS1') AS INTEGER) AS reference_frame_width
                           FROM images i
                           WHERE i.{SqlFragments.LightFrameOnly} AND {scope} AND i.capture_date IS NOT NULL
                           ORDER BY i.capture_date DESC, i.id DESC LIMIT 1) r ON 1 = 1
                WHERE id = {idParam};

                SELECT catalog_name, catalog_number, metadata
                FROM target_catalog_memberships WHERE target_id = {idParam}
                ORDER BY catalog_name;

                SELECT session_date FROM session_notes WHERE target_id = {idParam};
                """;
        }

        command.CommandText = text;
        parameters.ApplyTo(command);

        var frames = new List<OverviewFrame>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            frames.Add(new OverviewFrame(
                SqlReaders.ReadDate(reader, 0),
                SqlReaders.ReadDouble(reader, 2),
                SqlReaders.ReadText(reader, 3),
                SqlReaders.ReadText(reader, 4),
                SqlReaders.ReadText(reader, 5),
                SqlReaders.ReadNullableDouble(reader, 6),
                SqlReaders.ReadNullableDouble(reader, 7),
                SqlReaders.ReadNullableDouble(reader, 8),
                SqlReaders.ReadText(reader, 9),
                SqlReaders.ReadNullableDouble(reader, 10),
                SqlReaders.ReadNullableDouble(reader, 11),
                SqlReaders.ReadNullableDouble(reader, 12),
                SqlReaders.ReadText(reader, 13),
                reader.GetString(14),
                reader.GetValue(15).ToString() ?? groupKey,
                SqlReaders.ReadNullableDateTime(reader, 1)));
        }

        if (!isTarget)
        {
            return new ReadResult(null, frames, null, [], []);
        }

        TargetHeaderRow? target = null;
        if (reader.NextResult() && reader.Read())
        {
            target = new TargetHeaderRow(
                reader.GetGuid(0),
                reader.GetString(1),
                SqlReaders.ReadText(reader, 2),
                SqlReaders.ReadText(reader, 5),
                SqlReaders.ReadText(reader, 6),
                SqlReaders.ReadNullableDouble(reader, 3),
                SqlReaders.ReadNullableDouble(reader, 4),
                SqlReaders.ReadNullableDouble(reader, 7),
                SqlReaders.ReadNullableDouble(reader, 8),
                SqlReaders.ReadNullableDouble(reader, 9),
                SqlReaders.ReadNullableDouble(reader, 10),
                SqlReaders.ReadNullableDouble(reader, 11),
                SqlReaders.ReadText(reader, 12),
                SqlReaders.ReadText(reader, 13),
                SqlReaders.ReadText(reader, 14),
                SqlReaders.ReadText(reader, 15),
                reader.IsDBNull(16) ? null : reader.GetGuid(16),
                SqlReaders.ReadBool(reader, 17),
                SqlReaders.ReadBool(reader, 18),
                SqlReaders.ReadNullableDouble(reader, 19),
                SqlReaders.ReadNullableInt(reader, 20));
        }

        var memberships = new List<CatalogMembershipBadge>();
        reader.NextResult();
        while (reader.Read())
        {
            memberships.Add(new CatalogMembershipBadge(
                reader.GetString(0),
                reader.GetString(1),
                SqlReaders.ReadText(reader, 2)));
        }

        var noteDates = new HashSet<DateOnly>();
        reader.NextResult();
        while (reader.Read())
        {
            if (SqlReaders.ReadDate(reader, 0) is { } date)
            {
                noteDates.Add(date);
            }
        }

        return new ReadResult(targetId, frames, target, memberships, noteDates);
    }

    // ---- the header block --------------------------------------------------------------

    private static TargetHeaderBlock BuildHeader(string groupKey, ReadResult read)
    {
        if (read.Target is not { } target)
        {
            // An obj: group has no targets row at all, so every catalog field is empty and the
            // name is the raw OBJECT string. Rule 7: the category is never null.
            return new TargetHeaderBlock(
                groupKey,
                null,
                SqlFragments.DisplayNameOf(groupKey),
                [],
                null,
                TargetListingCriteria.UnresolvedCategory,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                // ReferenceArcsecPerPixel and ReferenceFrameWidthPixels: an obj: group has no
                // reference thumbnail, so there is nothing for a scale bar to sit over.
                null,
                null,
                false,
                false,
                []);
        }

        return new TargetHeaderBlock(
            groupKey,
            target.Id,
            target.PrimaryName,
            SqlReaders.ParseAliases(target.Aliases),
            target.ObjectType,
            ObjectTypeCategories.Categorize(target.ObjectType),
            target.Constellation,
            target.Ra,
            target.Dec,
            target.SizeMajor,
            target.SizeMinor,
            target.PositionAngle,
            target.VMag,
            target.SurfaceBrightness,
            target.SacDescription,
            target.SacNotes,
            target.Notes,
            target.ReferenceThumbnailPath,
            target.ReferenceArcsecPerPixel,
            target.ReferenceFrameWidthPixels,
            target.NameLocked,
            target.UserDefined,
            read.Memberships);
    }

    // ---- the totals row ----------------------------------------------------------------

    private static TargetTotals BuildTotals(List<OverviewFrame> frames, AliasMap map)
    {
        var integration = 0d;
        var integrationByFilter = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var framesByFilter = new Dictionary<string, List<OverviewFrame>>(StringComparer.OrdinalIgnoreCase);
        var sessionDates = new HashSet<DateOnly>();
        DateOnly? first = null;
        DateOnly? last = null;
        foreach (var frame in frames)
        {
            integration += frame.ExposureSeconds;
            if (map.CanonicalFilter(frame.FilterUsed) is { } filter && !string.IsNullOrWhiteSpace(filter))
            {
                integrationByFilter[filter] = integrationByFilter.GetValueOrDefault(filter) + frame.ExposureSeconds;
                if (!framesByFilter.TryGetValue(filter, out var bucket))
                {
                    framesByFilter[filter] = bucket = [];
                }

                bucket.Add(frame);
            }

            if (frame.SessionDate is not { } date)
            {
                continue;
            }

            sessionDates.Add(date);
            if (first is null || date < first)
            {
                first = date;
            }

            if (last is null || date > last)
            {
                last = date;
            }
        }

        var arcsec = HfrArcsec(frames);
        var eccentricity = ModalSourceEccentricity(frames);
        var pooled = PooledEccentricity(frames);
        var all = Means(frames, pooled);

        return new TargetTotals(
            integration,
            frames.Count,
            // Ruling Q5: a frame with a null session_date counts in every figure here but is not
            // a session, so it moves no session count and appears on no card.
            sessionDates.Count,
            first,
            last,
            all.Hfr,
            Statistics.Mean(arcsec.Values),
            arcsec.ExcludedCount,
            all.Eccentricity,
            eccentricity.ModalSource,
            eccentricity.ExcludedCount,
            all.Fwhm,
            all.GuidingRmsArcsec,
            all.DetectedStars,
            CanonicalFilters(frames, map),
            integrationByFilter,
            // Rule 6: the totals row's equipment is the flat set of canonical telescope and
            // camera names, each as its own entry, not the dashboard's rig strings.
            [.. frames
                .SelectMany(frame => new[]
                {
                    map.CanonicalTelescope(frame.Telescope),
                    map.CanonicalCamera(frame.Camera),
                })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)])
        {
            MeansByFilter = framesByFilter.ToDictionary(
                pair => pair.Key, pair => Means(pair.Value, pooled), StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>The five means over a group's frames; the All frames row and every filter row come
    /// from here, so they cannot drift. Eccentricity reads <paramref name="eccentricity"/>, the
    /// target's modal source pool.</summary>
    private static MetricMeans Means(IEnumerable<OverviewFrame> frames, Func<OverviewFrame, double?> eccentricity)
        => new(
            Statistics.Mean(frames.Select(frame => frame.MedianHfr)),
            Statistics.Mean(frames.Select(eccentricity)),
            // fwhm is already arcseconds (spec 7.1.1) and never passes through the plate scale.
            Statistics.Mean(frames.Select(frame => frame.Fwhm)),
            Statistics.Mean(frames.Select(frame => frame.GuidingRmsArcsec)),
            Statistics.Mean(frames.Select(frame => frame.DetectedStars)));

    /// <summary>Rule 1. A frame with an HFR and a strictly positive plate scale contributes the
    /// converted value; one with an HFR and no usable plate scale is counted as excluded; one
    /// with no HFR at all is in neither pool, because it was not excluded for lack of a plate
    /// scale, it simply has no HFR.</summary>
    private static (List<double?> Values, int ExcludedCount) HfrArcsec(List<OverviewFrame> frames)
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

    /// <summary>Rule 2, <c>target_detail._modal_source_ecc</c>: pool only the frames whose
    /// <c>eccentricity_source</c> is the most common one, so this average and the library average
    /// of Phase 9 agree. Spec 7.2: the three sources are never pooled.
    /// <para>
    /// The vote and the pooling test are <see cref="EccentricitySources"/>, which is the one
    /// implementation of spec 7.2's rule in the solution: this used to hold a
    /// second copy of the tie break, and two copies of a tie break eventually disagree and make
    /// the same library report two different averages on two screens.
    /// </para>
    /// </summary>
    private static (List<double?> Values, string? ModalSource, int ExcludedCount) ModalSourceEccentricity(
        List<OverviewFrame> frames)
    {
        if (!EccentricitySources.TryGetModalSource(
            frames.Select(frame => (frame.EccentricitySource, frame.Eccentricity)),
            out var modalSource))
        {
            return ([], null, 0);
        }

        var values = new List<double?>();
        var excluded = 0;
        foreach (var frame in frames)
        {
            if (frame.Eccentricity is null)
            {
                // Not excluded: a frame with no eccentricity was never in the pool to begin with.
                continue;
            }

            if (EccentricitySources.IsPooled(frame.EccentricitySource, frame.Eccentricity, modalSource))
            {
                values.Add(frame.Eccentricity);
            }
            else
            {
                excluded++;
            }
        }

        return (values, modalSource, excluded);
    }

    /// <summary>Rule 5. Canonical filter names, nulls dropped, sorted case-insensitively. Unlike
    /// the dashboard palette (Phase 5 ruling Q14) there is no "Unknown" bucket: spec 12.4 asks
    /// for the filters used, and a frame with no FILTER card used no filter.</summary>
    private static IReadOnlyList<string> CanonicalFilters(IEnumerable<OverviewFrame> frames, AliasMap map)
        => [.. frames
            .Select(frame => map.CanonicalFilter(frame.FilterUsed))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];

    // ---- the session overview list -----------------------------------------------------

    /// <summary>
    /// The guiding sessions of every night this group covers, with each session's rig already
    /// resolved through the live profile map, bucketed by night. One statement, an index range
    /// scan on <c>ix_phd2_sessions_session_date</c> over the group's own span, never one statement
    /// per night: the closed band's count exists precisely so a page does not issue a query per
    /// card (task3-review.md P2-2 and P2-3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The stored <c>phd2_sessions.telescope</c> column is not read.</b> The live map is the
    /// sole authority for a session's rig and
    /// <see cref="Phd2Profiles.EffectiveTelescope"/> is the one implementation of that rule, so a
    /// profile the reader has unmapped resolves to no rig here exactly as it does in the band.
    /// </para>
    /// <para>
    /// A null <c>equipment_profile</c> is projected as the empty string, as
    /// <c>Phd2NightQuery</c> projects it and for the same reason: the PHD2 embedded in an ASIAIR
    /// writes the profile line with nothing but a trailing space, so the rig rule's sole-profile
    /// branch must see one spelling of "no profile".
    /// </para>
    /// <para>
    /// The span is taken from the group's own frames rather than an <c>IN</c> list of its nights,
    /// which keeps the statement one index range scan whatever the night count and needs no
    /// parameter-limit batching. <c>phd2_sessions</c> is bounded by the guide-log corpus (937 rows
    /// over sixty real logs), and only two small columns are drawn down.
    /// </para>
    /// </remarks>
    private Dictionary<DateOnly, List<GuidingSessionRow>> GuidingSessions(
        GalactiLogContext context, List<OverviewFrame> frames)
    {
        var buckets = new Dictionary<DateOnly, List<GuidingSessionRow>>();
        var dates = frames.Where(frame => frame.SessionDate is not null).ToList();
        if (dates.Count == 0)
        {
            return buckets;
        }

        var first = dates.Min(frame => frame.SessionDate!.Value);
        var last = dates.Max(frame => frame.SessionDate!.Value);

        // Read and normalised ONCE per call, never per night and never per session.
        var profiles = Phd2Profiles.Normalize(profileMap());

        var rows = context.Phd2Sessions
            .Where(session => session.SessionDate != null
                && session.SessionDate >= first
                && session.SessionDate <= last)
            .Select(session => new { Night = session.SessionDate!.Value, session.EquipmentProfile })
            .ToList();

        foreach (var row in rows)
        {
            if (!buckets.TryGetValue(row.Night, out var bucket))
            {
                buckets[row.Night] = bucket = [];
            }

            var profile = row.EquipmentProfile ?? "";
            bucket.Add(new GuidingSessionRow(
                Phd2Profiles.EffectiveTelescope(profile, profiles), profile));
        }

        return buckets;
    }

    /// <summary>What the Guiding band will report for one card, which is what a closed band states
    /// (spec 12.4). The rig argument is <c>RigCount &gt; 1 ? null : Telescope</c>, the same one
    /// <c>SessionCardViewModel</c> hands the section, and the selection is
    /// <see cref="Phd2Metrics.SelectNightRows"/> over the alias-expanded match set, so this count
    /// and the opened band's list are one rule read twice rather than two rules.</summary>
    private static int GuidingCount(
        List<GuidingSessionRow> rows, string? telescope, AliasMap map)
        => string.IsNullOrEmpty(telescope)
            ? rows.Count
            : Phd2Metrics.SelectNightRows(
                rows, row => row.Telescope, row => row.Profile, map.TelescopeMatchSet(telescope)).Count;

    /// <summary>One guiding session of a night, in the two fields the count's rig rule reads. The
    /// telescope is the RESOLVED rig, never the stored column.</summary>
    private sealed record GuidingSessionRow(string? Telescope, string Profile);

    /// <summary>Rule 3: bucket by session_date, dropping the undated frames. Bucket order follows
    /// statement 1's capture_date ordering, which is what makes the session's first frame the
    /// first element of its bucket. The one night rule the sessions and the night rows share.</summary>
    private static Dictionary<DateOnly, List<OverviewFrame>> Nights(List<OverviewFrame> frames)
    {
        var buckets = new Dictionary<DateOnly, List<OverviewFrame>>();
        foreach (var frame in frames)
        {
            if (frame.SessionDate is not { } date)
            {
                continue;
            }

            if (!buckets.TryGetValue(date, out var bucket))
            {
                buckets[date] = bucket = [];
            }

            bucket.Add(frame);
        }

        return buckets;
    }

    /// <summary>A frame's eccentricity, null unless the frame is in the group's modal source pool, so
    /// a night row and a frame point pool what the night's median pools, and a filter row pools what
    /// the target's mean pools.</summary>
    private static Func<OverviewFrame, double?> PooledEccentricity(List<OverviewFrame> night)
    {
        if (!EccentricitySources.TryGetModalSource(
            night.Select(frame => (frame.EccentricitySource, frame.Eccentricity)),
            out var modalSource))
        {
            return _ => null;
        }

        return frame => EccentricitySources.IsPooled(frame.EccentricitySource, frame.Eccentricity, modalSource)
            ? frame.Eccentricity
            : null;
    }

    /// <summary>The frame's filter as the key <c>IntegrationSecondsByFilter</c> stores it, so an
    /// ordinal lookup from a row or a point finds the totals entry whatever this frame's casing.</summary>
    private static string? FilterKey(OverviewFrame frame, AliasMap map, Dictionary<string, string> keys)
        => map.CanonicalFilter(frame.FilterUsed) is { } filter && keys.TryGetValue(filter, out var key) ? key : null;

    /// <summary>One row per night and canonical filter, nights newest first, filters in
    /// name order. A frame with no filter is in no row, as in <c>IntegrationSecondsByFilter</c>.</summary>
    private static IReadOnlyList<NightFilterOverview> BuildNightFilters(
        Dictionary<DateOnly, List<OverviewFrame>> nights, AliasMap map, Dictionary<string, string> keys)
    {
        var rows = new List<NightFilterOverview>();
        foreach (var date in nights.Keys.OrderDescending())
        {
            var night = nights[date];
            var eccentricity = PooledEccentricity(night);
            var byFilter = night
                .Select(frame => (Key: FilterKey(frame, map, keys), Frame: frame))
                .Where(pair => pair.Key is not null)
                .GroupBy(pair => pair.Key!, pair => pair.Frame, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var group in byFilter)
            {
                var frames = group.ToList();
                rows.Add(new NightFilterOverview(
                    date,
                    group.Key,
                    frames.Sum(frame => frame.ExposureSeconds),
                    frames.Count,
                    [.. frames
                        .GroupBy(frame => frame.ExposureSeconds)
                        .OrderBy(length => length.Key)
                        .Select(length => new ExposureCount(length.Key, length.Count()))],
                    Statistics.Median(frames.Select(frame => frame.MedianHfr)),
                    Statistics.Median(frames.Select(eccentricity)),
                    Statistics.Median(frames.Select(frame => frame.Fwhm)),
                    Statistics.Median(frames.Select(frame => frame.GuidingRmsArcsec)),
                    Statistics.Median(frames.Select(frame => frame.DetectedStars))));
            }
        }

        return rows;
    }

    /// <summary>Every dated frame with a capture time, in statement 1's capture order.</summary>
    private static IReadOnlyList<NightFramePoint> BuildNightFrames(
        List<OverviewFrame> frames,
        Dictionary<DateOnly, List<OverviewFrame>> nights,
        AliasMap map,
        Dictionary<string, string> keys)
    {
        var eccentricity = nights.ToDictionary(night => night.Key, night => PooledEccentricity(night.Value));
        var points = new List<NightFramePoint>();
        foreach (var frame in frames)
        {
            if (frame.SessionDate is not { } date || frame.CaptureUtc is not { } capture)
            {
                continue;
            }

            points.Add(new NightFramePoint(
                date,
                capture,
                FilterKey(frame, map, keys) ?? "",
                frame.MedianHfr,
                eccentricity[date](frame),
                frame.Fwhm,
                frame.GuidingRmsArcsec,
                frame.DetectedStars));
        }

        return points;
    }

    private static IReadOnlyList<SessionOverview> BuildSessions(
        Dictionary<DateOnly, List<OverviewFrame>> buckets,
        HashSet<DateOnly> noteDates,
        AliasMap map,
        Dictionary<DateOnly, List<GuidingSessionRow>> guiding)
    {
        var sessions = new List<SessionOverview>(buckets.Count);
        foreach (var date in buckets.Keys.OrderDescending())
        {
            var bucket = buckets[date];
            var arcsec = HfrArcsec(bucket);
            // Review ruling on spec 7.2: "aggregations pool only same-source values" is not
            // scoped to the totals row, so the card's median pools the session's own modal
            // source with the same tie-break, and reports which source that was.
            var sessionEccentricity = ModalSourceEccentricity(bucket);
            var firstFrame = bucket[0];

            // Rule 4: a rig is the canonical (telescope, camera) pair, and a pair with a null
            // half counts as its own rig rather than being folded away.
            var rigs = new HashSet<(string? Telescope, string? Camera)>();
            foreach (var frame in bucket)
            {
                rigs.Add((map.CanonicalTelescope(frame.Telescope), map.CanonicalCamera(frame.Camera)));
            }

            var telescope = map.CanonicalTelescope(firstFrame.Telescope);

            sessions.Add(new SessionOverview(
                date,
                bucket.Sum(frame => frame.ExposureSeconds),
                bucket.Count,
                Statistics.Median(bucket.Select(frame => frame.MedianHfr)),
                Statistics.Median(arcsec.Values),
                arcsec.ExcludedCount,
                Statistics.Median(sessionEccentricity.Values),
                sessionEccentricity.ModalSource,
                Statistics.Median(bucket.Select(frame => frame.Fwhm)),
                Statistics.Median(bucket.Select(frame => frame.GuidingRmsArcsec)),
                Statistics.Median(bucket.Select(frame => frame.DetectedStars)),
                CanonicalFilters(bucket, map),
                map.CanonicalCamera(firstFrame.Camera),
                telescope,
                rigs.Count,
                noteDates.Contains(date),
                GuidingProvenanceOf(bucket),
                // The band narrows to this card's rig on a single-rig night and takes the whole
                // night on a multi-rig one, which is where the web narrows it too.
                guiding.TryGetValue(date, out var nightGuiding)
                    ? GuidingCount(nightGuiding, rigs.Count > 1 ? null : telescope, map)
                    : 0));
        }

        return sessions;
    }

    /// <summary>Spec 12.4's four-way guiding provenance truth table (Phase 15A review ruling: a
    /// frame with a real guiding RMS and a null source counts as not from a guide log). The one
    /// place this is computed: both the session pane's facts line and the ledger cell's mark read
    /// the member this builds rather than re-deriving it from a filtered or paged frame set.
    /// <para>
    /// Over the night's RMS-bearing frames only (<c>guiding_rms_arcsec</c> non-null); a frame with
    /// no RMS at all never counts either way, whatever its source column holds. Among those, a
    /// frame is "phd2" when its source is exactly <see cref="Phd2Correlation.Phd2Source"/> and
    /// "other" for every other case: <c>csv</c>, null, or an unrecognized string. The unqualified
    /// <see cref="GuidingRmsProvenance.Phd2"/> row therefore means every RMS-bearing frame of the
    /// night is from the guide log, not merely that one is; a single non-phd2 RMS-bearing frame
    /// beside it is enough to make the night <see cref="GuidingRmsProvenance.Mixed"/>.
    /// </para></summary>
    private static GuidingRmsProvenance GuidingProvenanceOf(IEnumerable<OverviewFrame> bucket)
    {
        var hasPhd2 = false;
        var hasOther = false;
        foreach (var frame in bucket)
        {
            if (frame.GuidingRmsArcsec is null)
            {
                continue;
            }

            if (string.Equals(frame.GuidingRmsSource, Phd2Correlation.Phd2Source, StringComparison.Ordinal))
            {
                hasPhd2 = true;
            }
            else
            {
                hasOther = true;
            }
        }

        return (hasOther, hasPhd2) switch
        {
            (true, true) => GuidingRmsProvenance.Mixed,
            (false, true) => GuidingRmsProvenance.Phd2,
            (true, false) => GuidingRmsProvenance.Csv,
            _ => GuidingRmsProvenance.None,
        };
    }

    /// <summary>One LIGHT frame of the target as statement 1 read it, which is every column the
    /// totals row and the session overviews need and nothing else. Named <c>OverviewFrame</c>, not
    /// <c>FrameRow</c>: <c>SessionDetailModels.FrameRow</c> is the public frame
    /// read model of the expanded card, a different shape, and a private nested type that shadows
    /// a public one in the same namespace reads as the same record in a stack trace.</summary>
    private sealed record OverviewFrame(
        DateOnly? SessionDate,
        double ExposureSeconds,
        string? FilterUsed,
        string? Telescope,
        string? Camera,
        double? MedianHfr,
        double? ArcsecPerPixel,
        double? Eccentricity,
        string? EccentricitySource,
        double? Fwhm,
        double? GuidingRmsArcsec,
        double? DetectedStars,
        string? GuidingRmsSource,
        string FilePath,
        string StorageGroupKey,
        DateTime? CaptureUtc);

    private sealed record TargetHeaderRow(
        Guid Id,
        string PrimaryName,
        string? Aliases,
        string? ObjectType,
        string? Constellation,
        double? Ra,
        double? Dec,
        double? SizeMajor,
        double? SizeMinor,
        double? PositionAngle,
        double? VMag,
        double? SurfaceBrightness,
        string? SacDescription,
        string? SacNotes,
        string? Notes,
        string? ReferenceThumbnailPath,
        Guid? MergedIntoId,
        bool NameLocked,
        bool UserDefined,
        double? ReferenceArcsecPerPixel,
        int? ReferenceFrameWidthPixels);

    private sealed record ReadResult(
        Guid? TargetId,
        List<OverviewFrame> Frames,
        TargetHeaderRow? Target,
        IReadOnlyList<CatalogMembershipBadge> Memberships,
        HashSet<DateOnly> NoteDates);
}
